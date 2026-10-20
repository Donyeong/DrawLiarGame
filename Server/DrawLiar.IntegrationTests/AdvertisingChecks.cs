using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyAdvertisingAsync()
    {
        Check(AdsRules.SupportsPlatform("Android") && AdsRules.SupportsPlatform("WebGL")
            && !AdsRules.SupportsPlatform("Windows") && !AdsRules.SupportsPlatform("Steam")
            && !AdsRules.SupportsPlatform("android"), "광고 허용 플랫폼은 Android와 WebGL만이어야 합니다.");
        var now = DateTimeOffset.Parse("2026-10-16T23:59:59Z");
        Check(AdsRules.NextReset(now) == DateTimeOffset.Parse("2026-10-17T00:00:00Z")
            && AdsRules.NextReset(now.ToOffset(TimeSpan.FromHours(9))) == AdsRules.NextReset(now), "광고 보상은 기기 시간대와 무관하게 UTC 자정에 갱신해야 합니다.");
        var disabled = new AdvertisingSettings(new ConfigurationBuilder().Build());
        Check(!disabled.PlatformEnabled(AdsRules.ANDROID) && !disabled.PlatformEnabled(AdsRules.WEB_GL)
            && disabled.RewardPaidGems == 5 && !disabled.AllowWebClientVerification, "미설정 광고와 웹 클라이언트 시청 신뢰는 기본 비활성이어야 합니다.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var http = new HttpClient(new AdKeyHandler(JsonSerializer.Serialize(new { keys = new[]
        { new { keyId = 12345L, base64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) } } })));
        var verifier = new AdMobSsvVerifier(http);
        string ticket = new('a', 64), user = new('b', 64);
        string content = "ad_network=5450213213286189855&ad_unit=5224354917&custom_data=" + ticket
            + "&reward_amount=5&reward_item=gems&timestamp=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            + "&transaction_id=verification-test&user_id=" + user;
        string Signed(string value)
        {
            byte[] signature = key.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return "?" + value + "&signature=" + Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "&key_id=12345";
        }
        string query = Signed(content);
        var result = await verifier.VerifyAsync(query, "ca-app-pub-3940256099942544/5224354917");
        Check(result != null && result.Ticket == ticket && result.UserId == user && result.TransactionId == "verification-test", "공식 형식의 원문 ECDSA DER 서명을 검증해야 합니다.");
        await AdsError(() => verifier.VerifyAsync(query.Replace(ticket, new string('c', 64)), "ca-app-pub-3940256099942544/5224354917"), "InvalidAdVerification");
        await AdsError(() => verifier.VerifyAsync(query + "&extra=1", "ca-app-pub-3940256099942544/5224354917"), "InvalidAdVerification");
        await AdsError(() => verifier.VerifyAsync(Signed(content).Replace("key_id=12345", "key_id=12346"), "ca-app-pub-3940256099942544/5224354917"), "InvalidAdVerification");
        string consoleTest = "ad_network=5450213213286189855&ad_unit=1234567890&reward_amount=1&reward_item=Reward&timestamp="
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "&transaction_id=123456789";
        Check(await verifier.VerifyAsync(query, "ca-app-pub-3940256099942544/0000000000") == null
            && await verifier.VerifyAsync(Signed(content + "&custom_data=" + ticket), "ca-app-pub-3940256099942544/5224354917") == null
            && await verifier.VerifyAsync(Signed(consoleTest), "ca-app-pub-3940256099942544/5224354917") == null,
            "서명이 유효한 비대상 콜백(다른 광고단위·중복 매개변수·콘솔 URL 확인)은 지급 없이 수락해야 합니다.");
        Console.WriteLine("광고 플랫폼·UTC 갱신·기본 비활성·AdMob 원문 서명·변조·비대상 콜백 수락 검증 통과");
    }

    private sealed class AdKeyHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri?.ToString() == "https://www.gstatic.com/admob/reward/verifier-keys.json", "SSV 공개 키는 고정 Google HTTPS 주소에서만 읽어야 합니다.");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static async Task AdsError(Func<Task> action, string code)
    {
        try { await action(); throw new InvalidOperationException("광고 오류가 누락되었습니다: " + code); }
        catch (ApiException exception) when (exception.Code == code) { }
    }
}
