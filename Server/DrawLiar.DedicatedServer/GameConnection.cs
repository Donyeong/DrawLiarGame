using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace DrawLiar.DedicatedServer;

internal sealed class GameConnection : IAsyncDisposable
{
    private const double CHAT_INTERVAL_SECONDS = .5;
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task _writer;
    private double _windowStarted;
    private int _frameCount, _strokeCount, _requestCount;
    private double _lastChat = double.NegativeInfinity;
    private int _stopped;

    public string AccountId { get; }
    public string SessionToken { get; }
    public int PlayerId { get; set; }
    public CancellationToken CancellationToken => _lifetime.Token;
    public bool IsAlive => Volatile.Read(ref _stopped) == 0;
    public bool FrameRateExceeded => _frameCount > 120;
    public bool LeftVoluntarily { get; private set; }

    public GameConnection(WebSocket socket, string accountId, string sessionToken, CancellationToken cancellationToken)
    {
        _socket = socket;
        AccountId = accountId;
        SessionToken = sessionToken;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _writer = WriteAsync();
    }

    public bool Queue(GameplayEnvelope envelope)
    {
        if (!IsAlive) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, GameplayWire.Json);
        if (bytes.Length > GameplayWire.MAX_FRAME_BYTES || !_outgoing.Writer.TryWrite(bytes))
        {
            Abort();
            return false;
        }
        return true;
    }

    public bool AcceptRate(string type, double now)
    {
        if (now - _windowStarted >= 1)
        {
            _windowStarted = now;
            _frameCount = _strokeCount = _requestCount = 0;
        }
        if (++_frameCount > 120) return false;
        if (type == "stroke") return ++_strokeCount <= 60;
        return ++_requestCount <= 10;
    }

    public bool AcceptChat(double now)
    {
        if (now - _lastChat < CHAT_INTERVAL_SECONDS) return false;
        _lastChat = now;
        return true;
    }

    public async Task<GameplayEnvelope?> ReceiveAsync()
    {
        var message = await GameplayWire.ReceiveAsync(_socket, _lifetime.Token);
        if (message == null && _socket.CloseStatus == WebSocketCloseStatus.NormalClosure) LeftVoluntarily = true;
        return message;
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        _lifetime.Cancel();
        _socket.Abort();
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var bytes in _outgoing.Reader.ReadAllAsync(_lifetime.Token))
                await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _lifetime.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        await _writer;
        _lifetime.Dispose();
    }
}
