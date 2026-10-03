using DrawLiar;
using DrawLiar.Server;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null) builder.WebHost.UseUrls("http://127.0.0.1:19080");
ServerRuntime.Configure(builder);
string adminKey = builder.Configuration["Admin:Key"] ?? "";
if (adminKey.Length < 32) throw new InvalidOperationException("Admin:Key는 32자 이상이어야 합니다.");
var app = builder.Build();
await ServerRuntime.InitializeAsync(app);
var database = app.Services.GetRequiredService<ServerDatabase>();
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
app.MapGet("/api/rooms", (string? search) => database.RoomsAsync(search, true));
await app.RunAsync();
