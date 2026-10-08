using DrawLiar;
using DrawLiar.Server;
using DrawLiar.GameServer;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null) builder.WebHost.UseUrls("http://127.0.0.1:19060");
ServerRuntime.Configure(builder);
ServerRuntime.RequirePublicHttps(builder, "GameServer:PublicUrl");
builder.Services.AddSingleton<MainRegistration>();
builder.Services.AddHostedService(services => services.GetRequiredService<MainRegistration>());
var app = builder.Build();
await ServerRuntime.InitializeAsync(app, false);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
app.MapGet("/health", (MainRegistration registration) => registration.IsReady ? Results.Ok(new { Status = "Ready" }) : Results.StatusCode(503));
var database = app.Services.GetRequiredService<ServerDatabase>();
string nodeId = app.Configuration["GameServer:NodeId"] ?? throw new InvalidOperationException("GameServer:NodeId가 필요합니다.");
Task<ServerSession> Authenticate(HttpContext context) => database.AuthenticateAsync(ServerRuntime.Bearer(context), "game:" + nodeId);
var lobbyChat = new LobbyChatHub(async (token, cancellation) =>
{
    var identity = await database.AuthenticateLobbyAsync(token, "game:" + nodeId, cancellation);
    return new LobbyChatIdentity(identity.Session.AccountId, identity.DisplayName, identity.Session.ExpiresAt, identity.Level);
});
app.Map("/ws/lobby", lobbyChat.HandleAsync);

app.MapPost("/api/session/enter", (EnterGameRequest request) => database.EnterGameAsync(request.AssignmentToken, nodeId));
app.MapPost("/api/session/logout", (Func<HttpContext, Task<IResult>>)(async context => { await database.LogoutAsync(await Authenticate(context)); return Results.NoContent(); }));
app.MapGet("/api/profile", (Func<HttpContext, Task<ProfileData>>)(async context => await database.ProfileAsync((await Authenticate(context)).AccountId)));
app.MapGet("/api/matches/{matchId}/reward", async (HttpContext context, string matchId) =>
    await database.MatchRewardAsync((await Authenticate(context)).AccountId, matchId, context.RequestAborted));
app.MapGet("/api/profiles/{accountId}", async (HttpContext context, string accountId) =>
    await database.PublicProfileAsync((await Authenticate(context)).AccountId, ServerRuntime.AccountId(accountId), context.RequestAborted));
app.MapPatch("/api/profile", async (HttpContext context, UpdateProfileRequest request) => await database.UpdateProfileAsync((await Authenticate(context)).AccountId, request));
app.MapGet("/api/friends", (Func<HttpContext, Task<FriendListResponse>>)(async context => await database.FriendsAsync((await Authenticate(context)).AccountId)));
app.MapGet("/api/social/inbox", (Func<HttpContext, Task<SocialInboxResponse>>)(async context =>
    await database.SocialInboxAsync((await Authenticate(context)).AccountId, context.RequestAborted)));
app.MapPost("/api/friends/request", async (HttpContext context, FriendRequest request) => { await database.RequestFriendAsync((await Authenticate(context)).AccountId, ServerRuntime.AccountId(request.AccountId)); return Results.NoContent(); });
app.MapPost("/api/friends/cancel", async (HttpContext context, FriendRequest request) => { await database.CancelFriendRequestAsync((await Authenticate(context)).AccountId, ServerRuntime.AccountId(request.AccountId), context.RequestAborted); return Results.NoContent(); });
app.MapPost("/api/friends/respond", async (HttpContext context, FriendRespondRequest request) => { await database.RespondFriendAsync((await Authenticate(context)).AccountId, ServerRuntime.AccountId(request.AccountId), request.Accept); return Results.NoContent(); });
app.MapDelete("/api/friends/{accountId}", async (HttpContext context, string accountId) => { await database.RemoveFriendAsync((await Authenticate(context)).AccountId, ServerRuntime.AccountId(accountId)); return Results.NoContent(); });
app.MapGet("/api/shop", (Func<HttpContext, Task<ShopResponse>>)(async context => { await Authenticate(context); return new ShopResponse { Products = ServerDatabase.ShopProducts }; }));
app.MapPost("/api/shop/purchase", async (HttpContext context, PurchaseRequest request) => await database.PurchaseAsync((await Authenticate(context)).AccountId, request));
app.MapPost("/api/shop/purchase-batch", async (HttpContext context, PurchaseBatchRequest request) =>
    await database.PurchaseBatchAsync((await Authenticate(context)).AccountId, request, context.RequestAborted));
app.MapGet("/api/topic-workshop", async (HttpContext context, string? language, bool? mine, int? offset, int? limit, string? search, string? sort) =>
    await database.ListWorkshopTopicsAsync((await Authenticate(context)).AccountId, language, mine ?? false, offset ?? 0, limit ?? 20, context.RequestAborted, search, sort));
app.MapPost("/api/topic-workshop", async (HttpContext context, TopicWorkshopPublishRequest request) =>
    await database.PublishWorkshopTopicAsync((await Authenticate(context)).AccountId, request, context.RequestAborted));
app.MapGet("/api/topic-workshop/{topicId}", async (HttpContext context, string topicId) =>
    await database.DownloadWorkshopTopicAsync((await Authenticate(context)).AccountId, topicId, context.RequestAborted));
app.MapGet("/api/topic-workshop/{topicId}/preview", async (HttpContext context, string topicId) =>
    await database.PreviewWorkshopTopicAsync((await Authenticate(context)).AccountId, topicId, context.RequestAborted));
app.MapPost("/api/topic-workshop/{topicId}/recommend", async (HttpContext context, string topicId, TopicWorkshopRecommendationRequest request) =>
    await database.RecommendWorkshopTopicAsync((await Authenticate(context)).AccountId, topicId, request.IsRecommended, context.RequestAborted));
app.MapDelete("/api/topic-workshop/{topicId}", async (HttpContext context, string topicId) =>
{
    await database.DeleteWorkshopTopicAsync((await Authenticate(context)).AccountId, topicId, context.RequestAborted);
    return Results.NoContent();
});
app.MapGet("/api/rooms", async (HttpContext context, string? search) => { await Authenticate(context); return await database.RoomsAsync(search); });
app.MapPost("/api/rooms", async (HttpContext context, CreateRoomRequest request) => await database.CreateRoomAsync(await Authenticate(context), request));
app.MapPost("/api/rooms/{roomId}/join", async (HttpContext context, string roomId, JoinRoomRequest request) => await database.JoinRoomAsync(await Authenticate(context), roomId, request.AsSpectator, request.Password)).RequireRateLimiting("room-join");
app.MapPost("/api/rooms/{roomId}/invite", async (HttpContext context, string roomId, FriendRequest request) =>
    await database.InviteToRoomAsync(await Authenticate(context), roomId, ServerRuntime.AccountId(request.AccountId), context.RequestAborted)).RequireRateLimiting("room-join");
app.MapPost("/api/room-invitations/{invitationId}/respond", async (HttpContext context, string invitationId, RoomInvitationRespondRequest request) =>
    await database.RespondRoomInvitationAsync(await Authenticate(context), invitationId, request.Accept, request.Password, context.RequestAborted)).RequireRateLimiting("room-join");
app.MapPost("/internal/rooms/configure", async (HttpContext context, ConfigureRoomRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    return await database.ConfigureRoomAsync(request, context.RequestAborted);
});
app.MapPost("/internal/rooms/kick", async (HttpContext context, KickRoomRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    return await database.KickRoomAsync(request, context.RequestAborted);
});
app.MapPost("/internal/dedicated/register", async (HttpContext context, RegisterDedicatedRequest request) => { ServerRuntime.RequireCluster(context, app.Configuration); await database.RegisterDedicatedAsync(request, app.Environment.IsDevelopment()); return Results.NoContent(); });
app.MapPost("/internal/dedicated/heartbeat", async (HttpContext context, DedicatedHeartbeatRequest request) => { ServerRuntime.RequireCluster(context, app.Configuration); return await database.DedicatedHeartbeatAsync(request); });
app.MapPost("/internal/dedicated/matches", async (HttpContext context, MatchResultRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    await database.RecordMatchAsync(request, context.RequestAborted);
    return Results.NoContent();
});
app.MapPost("/internal/tickets/redeem", async (HttpContext context, RedeemTicketRequest request) => { ServerRuntime.RequireCluster(context, app.Configuration); return await database.RedeemTicketAsync(request); });
app.MapPost("/internal/sessions/check", async (HttpContext context, SessionCheckRequest request) => { ServerRuntime.RequireCluster(context, app.Configuration); return new SessionCheckResponse { Valid = await database.CheckDedicatedSessionAsync(request) }; });
app.MapPost("/internal/profiles/refresh", async (HttpContext context, SessionCheckRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    if (!await database.CheckDedicatedSessionAsync(request)) throw new ApiException("Unauthorized", 401);
    return await database.ProfileAsync(ServerRuntime.AccountId(request.AccountId));
});
await app.RunAsync();
