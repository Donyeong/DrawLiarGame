using System.Net.WebSockets;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddHostedService(services => services.GetRequiredService<RoomRegistry>());
builder.Services.AddSingleton<GameClusterClient>();
builder.Services.AddHostedService(services => services.GetRequiredService<GameClusterClient>());
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
app.MapGet("/health", (GameClusterClient cluster) => cluster.IsReady
    ? Results.Ok(new { Status = "ready", NodeId = cluster.NodeId }) : Results.StatusCode(503));
var connectionSlots = new SemaphoreSlim(1024, 1024);
app.Map("/play", async (HttpContext context, GameClusterClient cluster, RoomRegistry rooms, ILoggerFactory loggers) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (!cluster.IsReady || !await connectionSlots.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    DedicatedRoom? room = null;
    GameConnection? connection = null;
    WebSocket? socket = null;
    try
    {
        socket = await context.WebSockets.AcceptWebSocketAsync();
        using var authenticate = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        authenticate.CancelAfter(TimeSpan.FromSeconds(10));
        var hello = await GameplayWire.ReceiveAsync(socket, authenticate.Token);
        if (hello?.Type != "hello" || string.IsNullOrEmpty(hello.Ticket) || string.IsNullOrEmpty(hello.RoomId))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "입장권이 필요합니다.", authenticate.Token);
            return;
        }
        var ticket = await cluster.RedeemAsync(hello.Ticket, hello.RoomId, authenticate.Token);
        if (ticket == null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "입장권이 만료되었거나 유효하지 않습니다.", authenticate.Token);
            return;
        }
        connection = new GameConnection(socket, ticket.AccountId, ticket.SessionToken, context.RequestAborted);
        room = rooms.Join(connection, ticket);
        if (room == null)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "방에 입장할 수 없습니다.", authenticate.Token);
            return;
        }
        while (connection.IsAlive)
        {
            var message = await connection.ReceiveAsync();
            if (message == null) break;
            if (message.Type == "request" && message.Kind == "configure")
                await room.ConfigureAsync(connection, message, RoomRegistry.Now, cluster.ConfigureAsync);
            else if (message.Type == "request" && message.Kind == "kick")
                await room.KickAsync(connection, message, RoomRegistry.Now, cluster.KickAsync);
            else if (message.Type == "request" && message.Kind == "refreshProfile")
                await room.RefreshProfileAsync(connection, message, RoomRegistry.Now, cluster.RefreshProfileAsync);
            else room.Receive(connection, message, RoomRegistry.Now);
        }
    }
    catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or HttpRequestException)
    {
        if (exception is JsonException or InvalidDataException)
            loggers.CreateLogger("Gameplay").LogDebug("올바르지 않은 게임 메시지로 연결을 종료했습니다.");
    }
    finally
    {
        try
        {
            if (connection != null)
            {
                room?.Disconnect(connection, RoomRegistry.Now);
                await connection.DisposeAsync();
            }
        }
        finally
        {
            socket?.Dispose();
            connectionSlots.Release();
        }
    }
});
app.Run();

public partial class Program;
