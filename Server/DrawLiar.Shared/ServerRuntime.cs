using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DrawLiar.Server;

public sealed class ApiException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class ServerRuntime
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.IncludeFields = true;
            options.SerializerOptions.PropertyNamingPolicy = null;
        });
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
        builder.Services.AddSingleton<ServerDatabase>();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            foreach (string proxy in builder.Configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(proxy, out var address)) throw new InvalidOperationException("Network:TrustedProxies에는 IP 주소만 설정합니다.");
                options.KnownProxies.Add(address);
            }
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/internal") && FixedEquals(context.Request.Headers["X-Cluster-Key"].ToString(), builder.Configuration["Cluster:Key"] ?? ""))
                    return RateLimitPartition.GetNoLimiter("internal");
                return RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("room-join", context => RateLimitPartition.GetFixedWindowLimiter(
                Hash(context.Request.Headers.Authorization.ToString()),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        string clusterKey = builder.Configuration["Cluster:Key"] ?? "";
        if (clusterKey.Length < 32) throw new InvalidOperationException("Cluster:Key는 32자 이상이어야 합니다.");
    }

    public static async Task InitializeAsync(WebApplication app, bool mapHealth = true)
    {
        await app.Services.GetRequiredService<ServerDatabase>().InitializeAsync();
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/ws"))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps) throw new ApiException("HttpsRequired", 403);
                }
                await next(context);
            }
            catch (ApiException exception)
            {
                context.Response.StatusCode = exception.Status;
                await context.Response.WriteAsJsonAsync(new ApiError { Code = exception.Code }, context.RequestAborted);
            }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505")
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new ApiError { Code = "AlreadyExists" }, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // 인증 정보와 요청 본문을 로그에 포함하지 않는다.
                app.Logger.LogError("요청 실패 {Method} {Path}: {Type}", context.Request.Method, context.Request.Path, exception.GetType().Name);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new ApiError { Code = "ServerUnavailable" }, context.RequestAborted);
            }
        });
        app.UseRateLimiter();
        if (mapHealth) app.MapGet("/health", () => Results.Ok(new { Status = "Ready" }));
    }

    public static void RequireCluster(HttpContext context, IConfiguration configuration)
    {
        if (!FixedEquals(context.Request.Headers["X-Cluster-Key"].ToString(), configuration["Cluster:Key"] ?? ""))
            throw new ApiException("Unauthorized", 401);
    }

    public static string Bearer(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new ApiException("Unauthorized", 401);
        string token = header[7..];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ApiException("Unauthorized", 401);
        return token;
    }

    public static void RequirePublicHttps(WebApplicationBuilder builder, string key)
    {
        string url = builder.Configuration[key] ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(builder.Environment.IsDevelopment() && uri.Scheme == "http"))
            throw new InvalidOperationException($"{key}는 HTTPS 주소여야 합니다. HTTP는 Development에서만 사용할 수 있습니다.");
    }

    public static bool FixedEquals(string left, string right) => left.Length == right.Length && left.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public static string Timestamp(DateTimeOffset value) => value.ToString("O");
    public static Guid AccountId(string value) => Guid.TryParse(value, out Guid id) ? id : throw new ApiException("InvalidAccount");

    public static string DisplayName(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length < 2 || value.Length > 16 || value.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != ' ')) throw new ApiException("InvalidDisplayName");
        return value;
    }

    public static string Email(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (value.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(value, out var address) || address.Address != value) throw new ApiException("InvalidEmail");
        return value;
    }

    public static string PasswordHash(string password)
    {
        if (password == null || password.Length < 10 || password.Length > 128) throw new ApiException("InvalidPassword");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"210000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        if (password == null || password.Length > 128) return false;
        string[] parts = stored.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations) || iterations != 210_000) return false;
        try
        {
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
