using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;

namespace DrawLiar.Server;

public sealed class GoogleAuthentication(IConfiguration configuration, ServerDatabase database)
{
    private static readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IConfiguration _configuration = configuration;
    private readonly ServerDatabase _database = database;

    public Task<GoogleChallengeResponse> ChallengeAsync(GoogleChallengeRequest request, Guid? accountId)
    {
        string key = request.Platform switch { "desktop" => "Google:DesktopClientId", "mobile" => "Google:MobileClientId", _ => throw new ApiException("InvalidPlatform") };
        return _database.CreateChallengeAsync(_configuration[key] ?? "", request.Platform, accountId);
    }

    public async Task<Guid> AuthenticateAsync(GoogleAuthRequest request, Guid? accountId, CancellationToken cancellationToken)
    {
        var challenge = await _database.ConsumeChallengeAsync(request.ChallengeId, accountId);
        string token = request.IdToken;
        if (challenge.Platform == "desktop") token = await ExchangeCodeAsync(request, challenge.ClientId, cancellationToken);
        if (token == null || token.Length is < 32 or > 16_384) throw new ApiException("InvalidGoogleCredential", 401);
        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(token, new GoogleJsonWebSignature.ValidationSettings
            { Audience = [challenge.ClientId], ExpirationTimeClockTolerance = TimeSpan.Zero }).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ApiException("InvalidGoogleCredential", 401); }
        if (payload.Audience is not string audience || audience != challenge.ClientId || !ServerRuntime.FixedEquals(challenge.Nonce, payload.Nonce ?? "")
            || string.IsNullOrWhiteSpace(payload.Subject) || payload.Subject.Length > 255)
            throw new ApiException("InvalidGoogleCredential", 401);
        // 외부 인증의 sub만 계정 식별자로 사용하며 이메일이 같은 계정을 자동 연결하지 않는다.
        string displayName = "화가_" + ServerRuntime.NewToken()[..8];
        return await _database.GoogleAccountAsync(payload.Subject, displayName, accountId);
    }

    private async Task<string> ExchangeCodeAsync(GoogleAuthRequest request, string clientId, CancellationToken cancellationToken)
    {
        string secret = _configuration["Google:DesktopClientSecret"] ?? "";
        if (string.IsNullOrWhiteSpace(secret)) throw new ApiException("GoogleUnavailable", 503);
        string verifier = request.CodeVerifier ?? "";
        if (request.Code == null || request.Code.Length is < 1 or > 1024 || request.Code.Any(c => c < 33 || c > 126)
            || verifier.Length is < 43 or > 128 || verifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '_' and not '~')
            || !Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme != "http" || redirect.Host != "127.0.0.1"
            || redirect.Port <= 0 || request.RedirectUri != $"http://127.0.0.1:{redirect.Port}/oauth2/callback")
            throw new ApiException("InvalidGoogleCredential", 401);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret, ["code"] = request.Code,
            ["code_verifier"] = verifier, ["redirect_uri"] = request.RedirectUri, ["grant_type"] = "authorization_code"
        });
        try
        {
            using var response = await _client.PostAsync("https://oauth2.googleapis.com/token", form, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ApiException("InvalidGoogleCredential", 401);
            if (response.Content.Headers.ContentLength > 32 * 1024) throw new ApiException("GoogleUnavailable", 503);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            byte[] bytes = new byte[32 * 1024 + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count > 32 * 1024) throw new ApiException("GoogleUnavailable", 503);
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
            if (!document.RootElement.TryGetProperty("id_token", out var idToken) || idToken.ValueKind != JsonValueKind.String)
                throw new ApiException("GoogleUnavailable", 503);
            return idToken.GetString()!;
        }
        catch (ApiException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new ApiException("GoogleUnavailable", 503); }
    }
}
