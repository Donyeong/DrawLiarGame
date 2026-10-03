using System.Net.Http.Json;
using System.Security.Cryptography;
using DrawLiar.Server;

namespace DrawLiar.GameServer;

public sealed class MainRegistration(IConfiguration configuration, IHostEnvironment environment, ILogger<MainRegistration> logger) : BackgroundService
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<MainRegistration> _logger = logger;
    private long _lastSuccessTicks;
    public bool IsReady => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSuccessTicks) < TimeSpan.FromSeconds(30).Ticks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string mainUrl = _configuration["GameServer:MainServerUrl"] ?? throw new InvalidOperationException("GameServer:MainServerUrl가 필요합니다.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        string pin = _configuration["Cluster:CertificateSha256"] ?? "";
        if (pin.Length > 0)
        {
            if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new InvalidOperationException("Cluster:CertificateSha256 형식 오류");
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null
                && ServerRuntime.FixedEquals(Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant(), pin.ToLowerInvariant());
        }
        if (!Uri.TryCreate(mainUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(_environment.IsDevelopment() && uri.IsLoopback && uri.Scheme == "http"))
            throw new InvalidOperationException("GameServer:MainServerUrl는 HTTPS 주소여야 합니다.");
        using var client = new HttpClient(handler) { BaseAddress = new Uri(mainUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-Cluster-Key", _configuration["Cluster:Key"]);
        var request = new RegisterGameRequest { NodeId = _configuration["GameServer:NodeId"]!, PublicUrl = _configuration["GameServer:PublicUrl"]! };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("internal/game/register", request, ServerRuntime.Json, stoppingToken);
                if (response.IsSuccessStatusCode) Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
                else _logger.LogWarning("메인서버 등록 거부: HTTP {Status}", (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { _logger.LogWarning("메인서버 등록 실패: {Type}", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
