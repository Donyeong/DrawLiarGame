namespace DrawLiar.Server;

public sealed class AdvertisingService(ServerDatabase database, AdvertisingSettings settings, AdMobSsvVerifier verifier)
{
    public Task<AdsStateResponse> StatusAsync(Guid accountId, string platform, CancellationToken cancellationToken = default) =>
        database.AdvertisingStatusAsync(accountId, platform, settings, cancellationToken);

    public Task<AdsRewardResponse> BeginAsync(Guid accountId, string sessionHash, AdsRewardBeginRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || !AdsRules.SupportsPlatform(request.Platform)
            || !Guid.TryParse(request.OperationId, out Guid operationId) || operationId == Guid.Empty)
            throw new ApiException("InvalidAdReward");
        if (!settings.PlatformEnabled(request.Platform) || !settings.DailyRewardEnabled)
            throw new ApiException("AdsUnavailable", 503);
        return database.BeginAdRewardAsync(accountId, sessionHash, operationId, request.Platform, settings, cancellationToken);
    }

    public Task<AdsRewardResponse> RewardAsync(Guid accountId, string platform, string ticket,
        CancellationToken cancellationToken = default)
    {
        ValidateTicket(platform, ticket);
        return database.AdRewardAsync(accountId, platform, ticket, settings, cancellationToken);
    }

    public Task<AdsRewardResponse> CompleteWebAsync(Guid accountId, string sessionHash, AdsRewardCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ApiException("InvalidAdReward");
        ValidateTicket(request.Platform, request.Ticket);
        if (request.Platform != AdsRules.WEB_GL || !settings.RewardProviderEnabled(AdsRules.WEB_GL))
            throw new ApiException("AdsUnavailable", 503);
        return database.CompleteWebAdRewardAsync(accountId, sessionHash, request.Ticket, settings, cancellationToken);
    }

    public async Task VerifyAdMobAsync(string rawQuery, CancellationToken cancellationToken = default)
    {
        if (!settings.RewardProviderEnabled(AdsRules.ANDROID)) throw new ApiException("AdsUnavailable", 503);
        var verified = await verifier.VerifyAsync(rawQuery, settings.AndroidRewardedAdUnitId, cancellationToken);
        if (verified == null) return;
        // 서명이 유효한 콜백은 지급하지 않더라도 200으로 응답해 AdMob 재시도를 막고, 일시 장애(5xx)만 재시도시킨다.
        try { await database.CompleteVerifiedAdRewardAsync(verified, settings, cancellationToken); }
        catch (ApiException exception) when (exception.Status is >= 400 and < 500) { }
    }

    private static void ValidateTicket(string platform, string ticket)
    {
        if (!AdsRules.SupportsPlatform(platform) || ticket == null || ticket.Length != 64 || !ticket.All(Uri.IsHexDigit))
            throw new ApiException("InvalidAdReward");
    }
}
