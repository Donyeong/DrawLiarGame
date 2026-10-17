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

async Task<LoginResponse> Login(HttpContext context, Guid accountId, string method)
{
    var issued = await database.IssueSessionAsync(accountId, "main");
    return await CompleteLogin(context, issued, method);
}

async Task<LoginResponse> CompleteLogin(HttpContext context, IssuedSession issued, string method)
{
    var session = await database.AuthenticateAsync(issued.Token, "main");
    LoginResponse response;
    try { response = await database.AssignGameAsync(session, issued.Token); }
    catch { await database.LogoutAsync(session); throw; }
    await database.LogActivityAsync("Login", session.AccountId, new
    {
        Method = method, Platform = ClientHeader(context, ClientVersionRules.PLATFORM_HEADER), Version = ClientHeader(context, ClientVersionRules.VERSION_HEADER)
    });
    return response;
}

static string ClientHeader(HttpContext context, string name)
{
    string value = context.Request.Headers[name].ToString();
    return value.Length > 32 ? value[..32] : value;
}

Task<ServerSession> Authenticate(HttpContext context) => database.AuthenticateAsync(ServerRuntime.Bearer(context), "main");

app.MapGet("/api/client/version", (Func<HttpContext, Task<ClientVersionResponse>>)ClientVersion);
app.MapPost("/api/auth/guest", async (HttpContext context, GuestLoginRequest request) =>
    await CompleteLogin(context, await database.GuestLoginAsync(request, context.RequestAborted), "Guest")).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request) =>
    await Login(context, await database.LoginAsync(request), "Email")).RequireRateLimiting("auth");
app.MapPost("/api/auth/development", async (HttpContext context, DevelopmentLoginRequest request) =>
{
    if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("MainServer:AllowDevelopmentAuth")) throw new ApiException("DevelopmentAuthDisabled", 403);
    return await Login(context, await database.DevelopmentAccountAsync(request.DisplayName), "Development");
}).RequireRateLimiting("auth");
app.MapPost("/api/session/assign", (Func<HttpContext, Task<LoginResponse>>)(async context => await database.AssignGameAsync(await Authenticate(context), ServerRuntime.Bearer(context))));
app.MapPost("/api/session/logout", (Func<HttpContext, Task<IResult>>)(async context =>
{
    var session = await Authenticate(context);
    await database.LogoutAsync(session);
    await database.LogActivityAsync("Logout", session.AccountId);
    return Results.NoContent();
}));
app.MapPost("/api/auth/google/challenge", async (HttpContext context, GoogleChallengeRequest request) =>
{
    Guid? accountId = context.Request.Headers.ContainsKey("Authorization") ? (await Authenticate(context)).AccountId : null;
    return await google.ChallengeAsync(request, accountId);
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/google", async (HttpContext context, GoogleAuthRequest request) =>
    await Login(context, await google.AuthenticateAsync(request, null, context.RequestAborted), "Google")).RequireRateLimiting("auth");
app.MapPost("/api/auth/google/link", async (HttpContext context, GoogleAuthRequest request) =>
{
    var session = await Authenticate(context);
    await google.AuthenticateAsync(request, session.AccountId, context.RequestAborted);
    await database.LogActivityAsync("GoogleLink", session.AccountId);
    return await database.ProfileAsync(session.AccountId);
}).RequireRateLimiting("auth");
app.MapPost("/internal/game/register", async (HttpContext context, RegisterGameRequest request) =>
{
    ServerRuntime.RequireCluster(context, app.Configuration);
    await database.RegisterGameAsync(request, app.Environment.IsDevelopment());
    return Results.NoContent();
});
await app.RunAsync();
