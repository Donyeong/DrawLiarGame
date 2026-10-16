using System;

namespace DrawLiar
{
    [Serializable] public sealed class AdsStateResponse
    {
        public string Platform = "";
        public bool BannerEnabled;
        public bool RewardedEnabled;
        public bool AdsRemoved;
        public bool CanClaimDailyReward;
        public bool ClaimedToday;
        public int RewardPaidGems;
        public string NextResetAt = "";
        public long ServerTimeUnixSeconds;
        public string BannerAdUnitId = "";
        public string RewardedAdUnitId = "";
        public string WebPublisherId = "";
        public string WebBannerSlotId = "";
    }
    [Serializable] public sealed class AdsRewardBeginRequest { public string Platform = ""; public string OperationId = ""; }
    [Serializable] public sealed class AdsRewardCompleteRequest { public string Platform = ""; public string Ticket = ""; }
    [Serializable] public sealed class AdsRewardTicket
    {
        public string Ticket = "";
        public string UserId = "";
        public string ExpiresAt = "";
        public string State = "";
    }
    [Serializable] public sealed class AdsRewardResponse
    {
        public AdsRewardTicket Ticket = new AdsRewardTicket();
        public string State = "";
        public ProfileData Profile = new ProfileData();
        public AdsStateResponse Status = new AdsStateResponse();
    }
}
