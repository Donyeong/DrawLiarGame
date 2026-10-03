using System.Net.WebSockets;
using System.Text.Json;

namespace DrawLiar.DedicatedServer;

internal static class GameplayWire
{
    public const int MAX_FRAME_BYTES = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<GameplayEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MAX_FRAME_BYTES)
                throw new InvalidDataException("게임 메시지 크기 또는 형식이 올바르지 않습니다.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<GameplayEnvelope>(message.GetBuffer().AsSpan(0, (int)message.Length), Json)
            ?? throw new InvalidDataException("게임 메시지가 비어 있습니다.");
    }
}
