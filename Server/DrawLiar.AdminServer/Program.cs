using DrawLiar;
using DrawLiar.Server;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null) builder.WebHost.UseUrls("http://127.0.0.1:19080");
ServerRuntime.Configure(builder);
string adminKey = builder.Configuration["Admin:Key"] ?? "";
if (adminKey.Length < 32) throw new InvalidOperationException("Admin:Key는 32자 이상이어야 합니다.");
var app = builder.Build();
await ServerRuntime.InitializeAsync(app, initializeDatabase: false);
var database = app.Services.GetRequiredService<ServerDatabase>();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !ServerRuntime.FixedEquals(header[7..], adminKey)) throw new ApiException("Unauthorized", 401);
    }
    await next(context);
});
app.MapGet("/api/accounts", (string? search) => database.AdminAccountsAsync(search));
app.MapPatch("/api/accounts/{accountId}/ban", async (string accountId, AdminAccountAction request) => { await database.AdminBanAsync(ServerRuntime.AccountId(accountId), request.IsBanned); return Results.NoContent(); });
app.MapGet("/api/mail", (Func<HttpContext, Task<AdminMailData[]>>)(context => database.AdminMailsAsync(context.RequestAborted)));
app.MapPost("/api/mail", (HttpContext context, AdminMailRequest request) => database.AdminSendMailAsync(request, context.RequestAborted));
app.MapPost("/api/mail/{mailId}/cancel", async (HttpContext context, string mailId) =>
{
    if (!Guid.TryParse(mailId, out var id) || id == Guid.Empty) throw new ApiException("InvalidMail");
    await database.AdminCancelMailAsync(id, context.RequestAborted);
    return Results.NoContent();
});
app.MapGet("/api/rooms", (string? search) => database.RoomsAsync(search, true));
app.MapGet("/api/reports", (string? status) => database.AdminReportsAsync(string.IsNullOrEmpty(status) ? null : status));
app.MapPatch("/api/reports/{reportId}", async (string reportId, AdminReportAction request) => { await database.AdminSetReportStatusAsync(ServerRuntime.AccountId(reportId), request.Status); return Results.NoContent(); });
app.MapGet("/api/client-versions", () => database.AdminClientVersionsAsync());
app.MapPut("/api/client-versions/{platform}", async (string platform, ClientVersionPolicy request) => { await database.AdminSetClientVersionAsync(platform, request); return Results.NoContent(); });
await app.RunAsync();
