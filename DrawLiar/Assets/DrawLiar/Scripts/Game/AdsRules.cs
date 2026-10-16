using System;

namespace DrawLiar
{
    public static class AdsRules
    {
        public const string ANDROID = "Android";
        public const string WEB_GL = "WebGL";
        public const string PRODUCT_ID = "daily-ad-gems";
        public const int DEFAULT_REWARD_PAID_GEMS = 5;
        public const int MAX_REWARD_PAID_GEMS = 100;
        public const int TICKET_LIFETIME_SECONDS = 600;
        public const int DEFAULT_WEB_MINIMUM_SECONDS = 30;
        public const string PENDING = "Pending";
        public const string COMPLETED = "Completed";
        public const string EXPIRED = "Expired";

        public static bool SupportsPlatform(string platform) => platform == ANDROID || platform == WEB_GL;
        public static DateTimeOffset NextReset(DateTimeOffset utcNow) => new DateTimeOffset(utcNow.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
    }
}
