using DrawLiar;
using DrawLiar.Server;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null) builder.WebHost.UseUrls("http://127.0.0.1:19050");
ServerRuntime.Configure(builder);
ServerRuntime.RequirePublicHttps(builder, "MainServer:PublicUrl");
builder.Services.AddSingleton<GoogleAuthentication>();
var app = builder.Build();
await ServerRuntime.InitializeAsync(app);
var database = app.Services.GetRequiredService<ServerDatabase>();
var google = app.Services.GetRequiredService<GoogleAuthentication>();

Task<ClientVersionResponse> ClientVersion(HttpContext context)
{
    if (!context.Request.Headers.TryGetValue(ClientVersionRules.PLATFORM_HEADER, out var platform)
        || !context.Request.Headers.TryGetValue(ClientVersionRules.VERSION_HEADER, out var version)
        || platform.ToString().Length > 32 || version.ToString().Length > 32)
        return Task.FromResult(new ClientVersionResponse());
    return database.ClientVersionAsync(platform.ToString(), version.ToString());
}

app.Use(async (context, next) =>
{
    if ((context.Request.Path.StartsWithSegments("/api/auth")
        || string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/session/assign", StringComparison.OrdinalIgnoreCase))
        && (await ClientVersion(context)).UpdateRequired)
        throw new ApiException("UpdateRequired", 426);
    await next(context);
});

async Task<LoginResponse> Login(Guid accountId)
{
    var issued = await database.IssueSessionAsync(accountId, "main");
    return await CompleteLogin(issued);
}

async Task<LoginResponse> CompleteLogin(IssuedSession issued)
{
    var session = await database.AuthenticateAsync(issued.Token, "main");
    try { return await database.AssignGameAsync(session, issued.Token); }
    catch { await database.LogoutAsync(session); throw; }
}

Task<ServerSession> Authenticate(HttpContext context) => database.AuthenticateAsync(ServerRuntime.Bearer(context), "main");

app.MapGet("/api/client/version", (Func<HttpContext, Task<ClientVersionResponse>>)ClientVersion);
app.MapPost("/api/auth/guest", async (HttpContext context, GuestLoginRequest request) =>
    await CompleteLogin(await database.GuestLoginAsync(request, context.RequestAborted))).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (LoginRequest request) => await Login(await database.LoginAsync(request))).RequireRateLimiting("auth");
app.MapPost("/api/auth/development", async (DevelopmentLoginRequest request) =>
{
    if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("MainServer:AllowDevelopmentAuth")) throw new ApiException("DevelopmentAuthDisabled", 403);
    return await Login(await database.DevelopmentAccountAsync(request.DisplayName));
}).RequireRateLimiting("auth");
app.MapPost("/api/session/assign", (Func<HttpContext, Task<LoginResponse>>)(async context => await database.AssignGameAsync(await Authenticate(context), ServerRuntime.Bearer(context))));
app.MapPost("/api/session/logout", (Func<HttpContext, Task<IResult>>)(async context => { await database.LogoutAsync(await Authenticate(context)); return Results.NoContent(); }));
app.MapPost("/api/auth/google/challenge", async (HttpContext context, GoogleChallengeRequest request) =>
{
    Guid? accountId = context.Request.Headers.ContainsKey("Authorization") ? (await Authenticate(context)).AccountId : null;
    return await google.ChallengeAsync(request, accountId);
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/google", async (HttpContext context, GoogleAuthRequest request) => await Login(await google.AuthenticateAsync(request, null, context.RequestAborted))).RequireRateLimiting("auth");
app.MapPost("/api/auth/google/link", async (HttpContext context, GoogleAuthRequest request) =>
{
    var session = await Authenticate(context);
    await google.AuthenticateAsync(request, session.AccountId, context.RequestAborted);
    return await database.ProfileAsync(session.AccountId);
}).RequireRateLimiting("auth");
app.MapPost("/internal/game/register", async (HttpContext context, RegisterGameRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    await database.RegisterGameAsync(request, app.Environment.IsDevelopment());
    return Results.NoContent();
});
await app.RunAsync();
