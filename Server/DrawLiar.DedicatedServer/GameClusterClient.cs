using System.Net.Http.Json;
using System.Security.Cryptography;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var validation = ValidateSessionsAsync(stoppingToken);
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
                var statuses = _rooms.Statuses();
                using var heartbeat = await _http.PostAsJsonAsync("/internal/dedicated/heartbeat",
                    new DedicatedHeartbeatRequest { NodeId = _nodeId, Rooms = statuses }, GameplayWire.Json, stoppingToken);
                heartbeat.EnsureSuccessStatusCode();
                var acknowledged = await heartbeat.Content.ReadFromJsonAsync<DedicatedHeartbeatResponse>(GameplayWire.Json, stoppingToken)
                    ?? throw new System.Text.Json.JsonException("방 종료 승인 응답이 비어 있습니다.");
                _rooms.AcknowledgeHeartbeat(statuses, acknowledged.ClosedRoomIds);
                _lastSuccess = RoomRegistry.Now;
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
                }, async (connection, token) =>
                {
                    try
                    {
                        using var check = await _http.PostAsJsonAsync("/internal/sessions/check", new SessionCheckRequest
                        {
                            AccountId = connection.AccountId, SessionToken = connection.SessionToken
                        }, GameplayWire.Json, token);
                        if (!check.IsSuccessStatusCode) { connection.Abort(); return; }
                        var result = await check.Content.ReadFromJsonAsync<SessionCheckResponse>(GameplayWire.Json, token);
                        if (result?.Valid != true) connection.Abort();
                    }
                    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
                    {
                        if (!stoppingToken.IsCancellationRequested) connection.Abort();
                    }
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
