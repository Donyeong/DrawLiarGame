using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using DrawLiar.Server;

namespace DrawLiar.DedicatedServer;

internal sealed class GameClusterClient : BackgroundService
{
    private readonly HttpClient _http;
    private readonly RoomRegistry _rooms;
    private readonly ILogger<GameClusterClient> _logger;
    private readonly string _nodeId, _publicUrl;
    private volatile bool _registered;
    private double _lastSuccess = RoomRegistry.Now;
    private readonly ConditionalWeakTable<GameConnection, SessionValidationLease> _sessionLeases = new();
    private readonly SemaphoreSlim _controlGate = new(1, 1);

    public bool IsReady => _registered && RoomRegistry.Now - _lastSuccess < 30;
    public string NodeId => _nodeId;

    public GameClusterClient(IConfiguration configuration, IHostEnvironment environment, RoomRegistry rooms, ILogger<GameClusterClient> logger)
    {
        _rooms = rooms;
        _logger = logger;
        _nodeId = configuration["Dedicated:NodeId"] ?? "dedicated-1";
        _publicUrl = configuration["Dedicated:PublicUrl"] ?? "ws://127.0.0.1:19070/play";
        string gameServer = configuration["Dedicated:GameServerUrl"] ?? "http://127.0.0.1:19060";
        string key = configuration["Cluster:Key"] ?? "";
        if (key.Length < 32) throw new InvalidOperationException("Cluster:Key는 32자 이상이어야 합니다.");
        if (!Uri.TryCreate(_publicUrl, UriKind.Absolute, out var publicUri)
            || publicUri.Scheme != "wss" && !(environment.IsDevelopment() && publicUri.IsLoopback && publicUri.Scheme == "ws")
            || publicUri.AbsolutePath != "/play" || publicUri.Query.Length != 0 || publicUri.UserInfo.Length != 0)
            throw new InvalidOperationException("Dedicated:PublicUrl은 /play 웹소켓 주소여야 합니다.");
        if (!Uri.TryCreate(gameServer, UriKind.Absolute, out var gameUri)
            || gameUri.Scheme != "https" && !(environment.IsDevelopment() && gameUri.IsLoopback && gameUri.Scheme == "http"))
            throw new InvalidOperationException("Dedicated:GameServerUrl은 HTTPS 주소여야 합니다.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        string pin = configuration["Cluster:CertificateSha256"] ?? "";
        if (pin.Length > 0)
        {
            if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new InvalidOperationException("Cluster:CertificateSha256 형식 오류");
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null
                && ServerRuntime.FixedEquals(Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant(), pin.ToLowerInvariant());
        }
        _http = new HttpClient(handler) { BaseAddress = gameUri, Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("X-Cluster-Key", key);
    }

    public async Task<RedeemTicketResponse?> RedeemAsync(string ticket, string roomId, CancellationToken cancellationToken)
    {
        if (!IsReady || ticket.Length > 512 || roomId.Length > 80) return null;
        using var response = await _http.PostAsJsonAsync("/internal/tickets/redeem",
            new RedeemTicketRequest { JoinTicket = ticket, NodeId = _nodeId, RoomId = roomId }, GameplayWire.Json, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadFromJsonAsync<RedeemTicketResponse>(GameplayWire.Json, cancellationToken);
        if (result == null || string.IsNullOrEmpty(result.AccountId) || string.IsNullOrEmpty(result.SessionToken)
            || result.AdmissionId == null || result.AdmissionId.Length != 64 || !result.AdmissionId.All(Uri.IsHexDigit)
            || result.Room == null || result.Profile == null || result.Room.RoomId != roomId || result.Room.NodeId != _nodeId
            || result.Profile.AccountId != result.AccountId) return null;
        return result;
    }

    public async Task<RoomConfigurationData> ConfigureAsync(ConfigureRoomRequest request, CancellationToken cancellationToken)
    {
        _rooms.ValidateConfigurationStateSize(request);
        await _controlGate.WaitAsync(cancellationToken);
        try
        {
            await HeartbeatAsync(cancellationToken);
            request.NodeId = _nodeId;
            using var response = await _http.PostAsJsonAsync("/internal/rooms/configure", request, GameplayWire.Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ApiError>(GameplayWire.Json, cancellationToken);
                throw new ApiException(error?.Code ?? "ServerUnavailable", (int)response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<RoomConfigurationData>(GameplayWire.Json, cancellationToken)
                ?? throw new JsonException("방 설정 응답이 비어 있습니다.");
        }
        finally { _controlGate.Release(); }
    }

    public async Task<ProfileData> RefreshProfileAsync(SessionCheckRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/internal/profiles/refresh", request, GameplayWire.Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(GameplayWire.Json, cancellationToken);
            throw new ApiException(error?.Code ?? "ServerUnavailable", (int)response.StatusCode);
        }
        var profile = await response.Content.ReadFromJsonAsync<ProfileData>(GameplayWire.Json, cancellationToken);
        if (profile == null || profile.AccountId != request.AccountId || string.IsNullOrEmpty(profile.DisplayName))
            throw new JsonException("프로필 응답이 올바르지 않습니다.");
        return profile;
    }

    public async Task<KickRoomResponse> KickAsync(KickRoomRequest request, CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken);
        try
        {
            await HeartbeatAsync(cancellationToken);
            request.NodeId = _nodeId;
            using var response = await _http.PostAsJsonAsync("/internal/rooms/kick", request, GameplayWire.Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ApiError>(GameplayWire.Json, cancellationToken);
                throw new ApiException(error?.Code ?? "ServerUnavailable", (int)response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<KickRoomResponse>(GameplayWire.Json, cancellationToken)
                ?? throw new JsonException("강퇴 응답이 비어 있습니다.");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException
            || exception is ApiException { Status: >= 500 })
        {
            using var recover = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await HeartbeatAsync(recover.Token); }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException or IOException) { }
            throw;
        }
        finally { _controlGate.Release(); }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        var statuses = _rooms.Statuses();
        using var heartbeat = await _http.PostAsJsonAsync("/internal/dedicated/heartbeat",
            new DedicatedHeartbeatRequest { NodeId = _nodeId, Rooms = statuses }, GameplayWire.Json, cancellationToken);
        heartbeat.EnsureSuccessStatusCode();
        var acknowledged = await heartbeat.Content.ReadFromJsonAsync<DedicatedHeartbeatResponse>(GameplayWire.Json, cancellationToken)
            ?? throw new JsonException("방 종료 승인 응답이 비어 있습니다.");
        _rooms.AcknowledgeHeartbeat(statuses, acknowledged.ClosedRoomIds, acknowledged.Configurations, acknowledged.Kicks);
        _lastSuccess = RoomRegistry.Now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var validation = Task.WhenAll(ValidateSessionsAsync(stoppingToken), EnforceSessionLeasesAsync(stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_registered)
                {
                    using var register = await _http.PostAsJsonAsync("/internal/dedicated/register",
                        new RegisterDedicatedRequest { NodeId = _nodeId, PublicUrl = _publicUrl, Capacity = _rooms.Capacity },
                        GameplayWire.Json, stoppingToken);
                    register.EnsureSuccessStatusCode();
                    _registered = true;
                }
                await FlushResultsAsync(stoppingToken);
                await _controlGate.WaitAsync(stoppingToken);
                try { await HeartbeatAsync(stoppingToken); }
                finally { _controlGate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning("게임서버 제어 연결을 재시도합니다: {FailureType}", exception.GetType().Name);
                _registered = false;
                if (RoomRegistry.Now - _lastSuccess >= 30) _rooms.AbortAll();
            }
            try { await _rooms.WaitForStatusChangeAsync(TimeSpan.FromSeconds(_registered ? 10 : 5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        await validation;
    }

    private async Task ValidateSessionsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await Parallel.ForEachAsync(_rooms.Connections(), new ParallelOptions
                {
                    MaxDegreeOfParallelism = 16, CancellationToken = stoppingToken
                }, (connection, token) => new ValueTask(ValidateSessionAsync(connection, token)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public void TrackValidatedSession(GameConnection connection) =>
        _sessionLeases.GetValue(connection, _ => new SessionValidationLease(RoomRegistry.Now)).Succeed(RoomRegistry.Now);

    private async Task EnforceSessionLeasesAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                EnforceSessionLeases(RoomRegistry.Now);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal void EnforceSessionLeases(double now)
    {
        foreach (var connection in _rooms.Connections())
            if (!_sessionLeases.TryGetValue(connection, out var lease) || !lease.AllowsTransient(now))
                connection.AbortInvalidSession();
    }

    internal async Task ValidateSessionAsync(GameConnection connection, CancellationToken cancellationToken)
    {
        // 입장권 교환에 성공한 연결만 유예를 받을 수 있다.
        if (!_sessionLeases.TryGetValue(connection, out var lease)) { connection.AbortInvalidSession(); return; }
        if (!lease.AllowsTransient(RoomRegistry.Now)) { connection.AbortInvalidSession(); return; }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(lease.RemainingSeconds(RoomRegistry.Now)));
        try
        {
            using var check = await _http.PostAsJsonAsync("/internal/sessions/check", new SessionCheckRequest
            {
                AccountId = connection.AccountId, SessionToken = connection.SessionToken
            }, GameplayWire.Json, budget.Token);
            if (!check.IsSuccessStatusCode)
            {
                if (!SessionValidationLease.IsTransient(check.StatusCode) || !lease.AllowsTransient(RoomRegistry.Now))
                    connection.AbortInvalidSession();
                return;
            }
            var result = await check.Content.ReadFromJsonAsync<SessionCheckResponse>(GameplayWire.Json, budget.Token)
                ?? throw new JsonException("세션 확인 응답이 비어 있습니다.");
            if (!result.Valid) connection.AbortInvalidSession();
            else lease.Succeed(RoomRegistry.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            if (!lease.AllowsTransient(RoomRegistry.Now)) connection.AbortInvalidSession();
        }
    }

    internal async Task FlushResultsAsync(CancellationToken cancellationToken)
    {
        double started = RoomRegistry.Now;
        foreach (var result in _rooms.PendingResults())
        {
            if (RoomRegistry.Now - started >= 2) break;
            try
            {
                using var response = await _http.PostAsJsonAsync("/internal/dedicated/matches", result, GameplayWire.Json, cancellationToken);
                if (IsPermanentResultFailure(response.StatusCode))
                {
                    _rooms.QuarantineResult(result.MatchId, (int)response.StatusCode);
                    _logger.LogError("완료 경기 기록을 격리했습니다: {MatchId}, HTTP {Status}", result.MatchId, (int)response.StatusCode);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                _rooms.AcknowledgeResult(result.MatchId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                _rooms.RetryResult(result.MatchId);
                _logger.LogWarning("완료 경기 기록을 재시도합니다: {FailureType}", exception.GetType().Name);
            }
        }
    }

    internal static bool IsPermanentResultFailure(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;

    public override void Dispose()
    {
        _http.Dispose();
        _controlGate.Dispose();
        base.Dispose();
    }
}

internal sealed class SessionValidationLease
{
    internal const double GRACE_SECONDS = 30;
    private double _lastSuccess;

    public SessionValidationLease(double now) => _lastSuccess = now;
    public void Succeed(double now) => Volatile.Write(ref _lastSuccess, now);
    public bool AllowsTransient(double now) => now - Volatile.Read(ref _lastSuccess) < GRACE_SECONDS;
    public double RemainingSeconds(double now) => Math.Max(.001, GRACE_SECONDS - (now - Volatile.Read(ref _lastSuccess)));
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}
