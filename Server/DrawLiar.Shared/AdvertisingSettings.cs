using Microsoft.Extensions.Configuration;

namespace DrawLiar.Server;

public sealed class AdvertisingSettings
{
    public bool Enabled { get; }
    public bool DailyRewardEnabled { get; }
    public int RewardPaidGems { get; }
    public int WebMinimumSeconds { get; }
    public bool AndroidEnabled { get; }
    public string AndroidBannerAdUnitId { get; }
    public string AndroidRewardedAdUnitId { get; }
    public bool WebEnabled { get; }
    public string WebPublisherId { get; }
    public string WebBannerSlotId { get; }
    public bool AllowWebClientVerification { get; }

    public AdvertisingSettings(IConfiguration configuration)
    {
        Enabled = configuration.GetValue<bool>("Advertising:Enabled");
        DailyRewardEnabled = configuration.GetValue<bool?>("Advertising:DailyRewardEnabled") ?? true;
        RewardPaidGems = configuration.GetValue<int?>("Advertising:RewardPaidGems") ?? AdsRules.DEFAULT_REWARD_PAID_GEMS;
        WebMinimumSeconds = configuration.GetValue<int?>("Advertising:Web:MinimumRewardSeconds") ?? AdsRules.DEFAULT_WEB_MINIMUM_SECONDS;
        AndroidEnabled = configuration.GetValue<bool>("Advertising:Android:Enabled");
        AndroidBannerAdUnitId = configuration["Advertising:Android:BannerAdUnitId"] ?? "";
        AndroidRewardedAdUnitId = configuration["Advertising:Android:RewardedAdUnitId"] ?? "";
        WebEnabled = configuration.GetValue<bool>("Advertising:Web:Enabled");
        WebPublisherId = configuration["Advertising:Web:PublisherId"] ?? "";
        WebBannerSlotId = configuration["Advertising:Web:BannerSlotId"] ?? "";
        AllowWebClientVerification = configuration.GetValue<bool>("Advertising:Web:AllowClientVerification");
        if (RewardPaidGems is < 1 or > AdsRules.MAX_REWARD_PAID_GEMS)
            throw new InvalidOperationException("Advertising:RewardPaidGems는 1~100이어야 합니다.");
        if (WebMinimumSeconds is < 1 or >= AdsRules.TICKET_LIFETIME_SECONDS)
            throw new InvalidOperationException("Advertising:Web:MinimumRewardSeconds는 1~599초여야 합니다.");
        if (Enabled && AndroidEnabled && (!ValidAdMobUnit(AndroidBannerAdUnitId) || !ValidAdMobUnit(AndroidRewardedAdUnitId)))
            throw new InvalidOperationException("Android 광고 단위 ID를 설정해야 합니다.");
        if (Enabled && WebEnabled && (!WebPublisherId.StartsWith("ca-pub-", StringComparison.Ordinal)
            || WebPublisherId.Length != 23 || !WebPublisherId[7..].All(char.IsAsciiDigit)
            || WebBannerSlotId.Length is < 1 or > 32 || !WebBannerSlotId.All(char.IsAsciiDigit)))
            throw new InvalidOperationException("Web 광고 게시자 ID와 배너 슬롯 ID를 설정해야 합니다.");
    }

    private static bool ValidAdMobUnit(string id) => id.StartsWith("ca-app-pub-", StringComparison.Ordinal)
        && id.Length == 38 && id[27] == '/' && id[11..27].All(char.IsAsciiDigit) && id[28..].All(char.IsAsciiDigit);

    public bool PlatformEnabled(string platform) => Enabled && (platform == AdsRules.ANDROID && AndroidEnabled
        || platform == AdsRules.WEB_GL && WebEnabled);
    public bool RewardProviderEnabled(string platform) => PlatformEnabled(platform) && DailyRewardEnabled
        && (platform == AdsRules.ANDROID || platform == AdsRules.WEB_GL && AllowWebClientVerification);
}
