using DrawLiar;
using DrawLiar.Admin.Security;
using DrawLiar.Server;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

if (PasswordHashGenerator.IsRequested(args)) return PasswordHashGenerator.Run();
var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null) builder.WebHost.UseUrls("http://127.0.0.1:19080");
ServerRuntime.Configure(builder);
var adminOptions = new AdminOptions();
builder.Configuration.GetSection("Admin").Bind(adminOptions);
adminOptions.ResolvePaths(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(adminOptions);
builder.Services.AddSingleton<AdminAuthenticationService>();
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(adminOptions.DataProtectionKeysPath))
    .SetApplicationName("DrawLiar.AdminServer");
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/login";
    options.Cookie.Name = "DrawLiar.Admin.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.Equals("/console.js"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else if (context.Request.Path.Equals("/") || context.Request.Path.Equals("/Index", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            context.Response.Redirect("login");
        else
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "DrawLiar.Admin.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options => options.AddPolicy("admin-login", context =>
    HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter("login-page")));
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});
var app = builder.Build();
_ = app.Services.GetRequiredService<AdminAuthenticationService>();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps && !context.Request.Path.Equals("/health"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
app.UseRouting();
await ServerRuntime.InitializeAsync(app, mapHealth: false, initializeDatabase: false);
app.UseAuthentication();
app.UseWhen(context => context.Request.Path.Equals("/console.css") || context.Request.Path.StartsWithSegments("/vendor"),
    assets => assets.UseStaticFiles());
app.UseAuthorization();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { Status = "Ready" })).AllowAnonymous();
app.MapRazorPages();
var database = app.Services.GetRequiredService<ServerDatabase>();
var api = app.MapGroup("/api").RequireAuthorization();
api.AddEndpointFilter(async (invocation, next) =>
{
    var context = invocation.HttpContext;
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new ApiError { Code = "InvalidCsrfToken" }); }
    }
    return await next(invocation);
});
api.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) => new
{
    Username = context.User.Identity!.Name,
    CsrfToken = antiforgery.GetAndStoreTokens(context).RequestToken
});
api.MapGet("/accounts", (string? search) => database.AdminAccountsAsync(search));
api.MapPatch("/accounts/{accountId}/ban", async (HttpContext context, string accountId, AdminAccountAction request) =>
{
    await database.AdminBanAsync(ServerRuntime.AccountId(accountId), request.IsBanned, context.User.Identity!.Name!);
    return Results.NoContent();
});
api.MapGet("/accounts/{accountId}/logs", (HttpContext context, string accountId, long? before) =>
    database.AdminAccountLogsAsync(ServerRuntime.AccountId(accountId), before ?? 0, context.RequestAborted));
api.MapGet("/mail", (Func<HttpContext, Task<AdminMailData[]>>)(context => database.AdminMailsAsync(context.RequestAborted)));
api.MapPost("/mail", (HttpContext context, AdminMailRequest request) => database.AdminSendMailAsync(request, context.RequestAborted));
api.MapPost("/mail/{mailId}/cancel", async (HttpContext context, string mailId) =>
{
    if (!Guid.TryParse(mailId, out var id) || id == Guid.Empty) throw new ApiException("InvalidMail");
    await database.AdminCancelMailAsync(id, context.RequestAborted);
    return Results.NoContent();
});
api.MapGet("/rooms", (string? search) => database.RoomsAsync(search, true));
api.MapGet("/rooms/{roomId}/logs", (HttpContext context, string roomId, long? before) =>
    database.AdminRoomLogsAsync(Guid.TryParse(roomId, out var id) ? id : throw new ApiException("InvalidRoom"), before ?? 0, context.RequestAborted));
api.MapGet("/matches/{matchId}/drawings/{round:int}", (HttpContext context, string matchId, int round) =>
    database.AdminMatchDrawingAsync(matchId, round, context.RequestAborted));
api.MapGet("/reports", (string? status) => database.AdminReportsAsync(string.IsNullOrEmpty(status) ? null : status));
api.MapPatch("/reports/{reportId}", async (string reportId, AdminReportAction request) => { await database.AdminSetReportStatusAsync(ServerRuntime.AccountId(reportId), request.Status); return Results.NoContent(); });
api.MapGet("/client-versions", () => database.AdminClientVersionsAsync());
api.MapPut("/client-versions/{platform}", async (string platform, ClientVersionPolicy request) => { await database.AdminSetClientVersionAsync(platform, request); return Results.NoContent(); });
await app.RunAsync();
return 0;
