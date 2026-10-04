using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using DrawLiar.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DrawLiar.GameServer;

public sealed record LobbyChatIdentity(Guid AccountId, string DisplayName, DateTimeOffset ExpiresAt);

public sealed class LobbyChatHub
{
    public const int MAX_MESSAGE_LENGTH = 200;
    public const int HISTORY_LIMIT = 50;
    private const int MAX_FRAME_BYTES = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<Connection>> _members = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _sent = new();
    private readonly Queue<LobbyChatMessage> _history = new();
    private readonly SemaphoreSlim _slots = new(512, 512);
    private readonly Func<string, CancellationToken, Task<LobbyChatIdentity>> _authenticate;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _validationInterval;
    private long _sequence;

    public LobbyChatHub(Func<string, CancellationToken, Task<LobbyChatIdentity>> authenticate,
        TimeProvider? clock = null, TimeSpan? validationInterval = null)
    {
        _authenticate = authenticate;
        _clock = clock ?? TimeProvider.System;
        _validationInterval = validationInterval ?? TimeSpan.FromSeconds(15);
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!await _slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        Connection? connection = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            LobbyChatIdentity identity;
            string token;
            try
            {
                var hello = await ReceiveAsync(socket, handshake.Token);
                token = hello?.SessionToken ?? "";
                if (hello?.Type != "auth" || token.Length != 64 || !token.All(Uri.IsHexDigit))
                    throw new ApiException("Unauthorized", 401);
                identity = await _authenticate(token, handshake.Token);
                if (identity.ExpiresAt <= _clock.GetUtcNow()) throw new ApiException("Unauthorized", 401);
            }
            catch (ApiException exception) when (exception.Status is 401 or 403)
            {
                await RejectAsync(socket, "InvalidSession", lifetime.Token);
                return;
            }
            connection = new Connection(socket, token, identity.AccountId, lifetime.Token);
            if (!Join(connection))
            {
                connection.Stop("ChatUnavailable");
                await connection.DisposeAsync();
                return;
            }
            var validation = ValidateSessionAsync(connection);
            try
            {
                while (!connection.ReadCancellation.IsCancellationRequested)
                {
                    var receiving = ReceiveAsync(socket, lifetime.Token);
                    if (await Task.WhenAny(receiving, connection.Stopped) != receiving)
                    {
                        _ = ObserveReceiveAsync(receiving);
                        break;
                    }
                    var request = await receiving;
                    if (request == null) break;
                    if (request.Type != "chat" || !ValidRequestId(request.RequestId))
                    {
                        connection.Notice("InvalidChat", ValidRequestId(request.RequestId) ? request.RequestId : "");
                        continue;
                    }
                    string text = (request.Text ?? "").Trim();
                    if (text.Length == 0 || text.Length > MAX_MESSAGE_LENGTH || text.Any(char.IsControl))
                    {
                        connection.Notice("InvalidChat", request.RequestId);
                        continue;
                    }
                    identity = await _authenticate(connection.Token, connection.ReadCancellation);
                    if (identity.AccountId != connection.AccountId || identity.ExpiresAt <= _clock.GetUtcNow())
                        throw new ApiException("Unauthorized", 401);
                    Publish(connection, identity.DisplayName, text, request.RequestId);
                }
            }
            catch (ApiException exception) when (exception.Status is 401 or 403) { connection.Stop("InvalidSession"); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException) { connection.Stop("InvalidChat"); }
            finally
            {
                Leave(connection);
                connection.CancelRead();
                await validation;
                await connection.DisposeAsync();
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException) { }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILogger<LobbyChatHub>>().LogWarning("로비 채팅 연결 실패: {Type}", exception.GetType().Name);
        }
        finally
        {
            if (connection != null) Leave(connection);
            _slots.Release();
        }
    }

    private bool Join(Connection connection)
    {
        lock (_gate)
        {
            TrimRates(_clock.GetUtcNow());
            if (!_members.TryGetValue(connection.AccountId, out var connections))
                _members.Add(connection.AccountId, connections = new HashSet<Connection>());
            if (connections.Count >= 2) return false;
            connections.Add(connection);
            connection.Queue(new LobbyChatEnvelope { Type = "history", Messages = _history.ToArray(), MemberCount = _members.Count });
            Broadcast(new LobbyChatEnvelope { Type = "presence", MemberCount = _members.Count });
            return true;
        }
    }

    private void Leave(Connection connection)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(connection.AccountId, out var connections) || !connections.Remove(connection)) return;
            if (connections.Count == 0) _members.Remove(connection.AccountId);
            Broadcast(new LobbyChatEnvelope { Type = "presence", MemberCount = _members.Count });
        }
    }

    private void Publish(Connection connection, string displayName, string text, string requestId)
    {
        lock (_gate)
        {
            if (connection.ReadCancellation.IsCancellationRequested) return;
            DateTimeOffset now = _clock.GetUtcNow();
            TrimRates(now);
            if (!_sent.TryGetValue(connection.AccountId, out var recent))
                _sent.Add(connection.AccountId, recent = new Queue<DateTimeOffset>());
            if (recent.Count >= 5 || recent.Count > 0 && now - recent.Last() < TimeSpan.FromSeconds(1))
            {
                connection.Notice("ChatRateLimited", requestId);
                return;
            }
            recent.Enqueue(now);
            var message = new LobbyChatMessage
            {
                Id = ++_sequence, AccountId = connection.AccountId.ToString(), DisplayName = displayName,
                Text = text, SentAt = ServerRuntime.Timestamp(now)
            };
            _history.Enqueue(message);
            while (_history.Count > HISTORY_LIMIT) _history.Dequeue();
            var envelope = new LobbyChatEnvelope { Type = "chat", Message = message, MemberCount = _members.Count };
            byte[] broadcast = JsonSerializer.SerializeToUtf8Bytes(envelope, ServerRuntime.Json);
            envelope.RequestId = requestId;
            byte[] acknowledged = JsonSerializer.SerializeToUtf8Bytes(envelope, ServerRuntime.Json);
            foreach (var member in _members.Values.SelectMany(member => member))
                member.Queue(member == connection ? acknowledged : broadcast);
        }
    }

    private void TrimRates(DateTimeOffset now)
    {
        foreach (var pair in _sent.ToArray())
        {
            while (pair.Value.TryPeek(out var timestamp) && now - timestamp >= TimeSpan.FromSeconds(10)) pair.Value.Dequeue();
            if (pair.Value.Count == 0) _sent.Remove(pair.Key);
        }
    }

    private void Broadcast(LobbyChatEnvelope envelope)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ServerRuntime.Json);
        foreach (var connection in _members.Values.SelectMany(member => member)) connection.Queue(bytes);
    }

    private async Task ValidateSessionAsync(Connection connection)
    {
        try
        {
            while (!connection.ReadCancellation.IsCancellationRequested)
            {
                await Task.Delay(_validationInterval, connection.ReadCancellation);
                var identity = await _authenticate(connection.Token, connection.ReadCancellation);
                if (identity.AccountId != connection.AccountId || identity.ExpiresAt <= _clock.GetUtcNow())
                    throw new ApiException("Unauthorized", 401);
            }
        }
        catch (ApiException exception) when (exception.Status is 401 or 403) { connection.Stop("InvalidSession"); }
        catch (OperationCanceledException) when (connection.ReadCancellation.IsCancellationRequested) { }
        catch (Exception) { connection.Stop("ChatUnavailable"); }
    }

    private static bool ValidRequestId(string? value) => value != null && value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit);

    private static async Task ObserveReceiveAsync(Task<LobbyChatEnvelope?> receiving)
    {
        try { await receiving; }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException) { }
    }

    private static async Task<LobbyChatEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        byte[] buffer = new byte[MAX_FRAME_BYTES];
        int count = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || count + result.Count >= MAX_FRAME_BYTES)
                throw new InvalidDataException("로비 채팅 메시지 형식이 올바르지 않습니다.");
            count += result.Count;
            if (result.EndOfMessage) return JsonSerializer.Deserialize<LobbyChatEnvelope>(buffer.AsSpan(0, count), ServerRuntime.Json);
        }
    }

    private static async Task RejectAsync(WebSocket socket, string code, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new LobbyChatEnvelope { Type = "notice", Code = code }, ServerRuntime.Json);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, code, timeout.Token);
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _read;
        private readonly CancellationTokenSource _write;
        private readonly Task _sender;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopped;
        public string Token { get; }
        public Guid AccountId { get; }
        public CancellationToken ReadCancellation => _read.Token;
        public Task Stopped => _ended.Task;

        public Connection(WebSocket socket, string token, Guid accountId, CancellationToken cancellation)
        {
            _socket = socket;
            Token = token;
            AccountId = accountId;
            _read = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _write = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _sender = SendAsync();
        }

        public void Queue(LobbyChatEnvelope envelope) => Queue(JsonSerializer.SerializeToUtf8Bytes(envelope, ServerRuntime.Json));
        public void Queue(byte[] bytes)
        {
            if (!_outgoing.Writer.TryWrite(bytes))
            {
                CancelRead();
                _write.Cancel();
                _socket.Abort();
            }
        }
        public void Notice(string code, string requestId = "") => Queue(new LobbyChatEnvelope { Type = "notice", Code = code, RequestId = requestId });
        public void Stop(string code)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            Notice(code);
            CancelRead();
        }
        public void CancelRead()
        {
            _ended.TrySetResult();
            _read.Cancel();
        }

        private async Task SendAsync()
        {
            try
            {
                await foreach (byte[] bytes in _outgoing.Reader.ReadAllAsync(_write.Token))
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _write.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                CancelRead();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _outgoing.Writer.TryComplete();
            try
            {
                try { await _sender.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { _write.Cancel(); _socket.Abort(); await _sender; }
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                    }
                    catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidOperationException) { _socket.Abort(); }
                }
            }
            finally
            {
                _read.Dispose();
                _write.Dispose();
            }
        }
    }
}
