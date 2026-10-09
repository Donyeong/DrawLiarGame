using System;
using System.Globalization;

namespace DrawLiar
{
    public static class CommerceRules
    {
        public const string PAID_GEMS = "PaidGems";
        public const string PAINTER_SUBSCRIPTION = "PainterSubscription";
        public const string PAINTER_PRODUCT_ID = "painter-membership-30d";
        public const int SUBSCRIPTION_DAYS = 30;

        public static CommerceProduct[] CreateProducts() => new[]
        {
            GemProduct("gems-100", "보석 100개", 100),
            GemProduct("gems-550", "보석 550개", 550),
            GemProduct("gems-1200", "보석 1,200개", 1200),
            new CommerceProduct { Id = PAINTER_PRODUCT_ID, Name = "화가 세트", Kind = PAINTER_SUBSCRIPTION,
                PricePaidGems = 300, SubscriptionDays = SUBSCRIPTION_DAYS }
        };

        private static CommerceProduct GemProduct(string id, string name, int amount) => new CommerceProduct
        {
            Id = id, Name = name, Kind = PAID_GEMS, PaidGems = amount, StoreProductId = "drawliar." + id.Replace('-', '_')
        };

        public static bool IsPainterSubscriber(ProfileData profile, DateTimeOffset utcNow) => profile != null
            && !string.IsNullOrEmpty(profile.AccountId) && IsActive(profile.SubscriptionExpiresAt, utcNow);

        public static bool IsActive(string expiresAt, DateTimeOffset utcNow) => DateTimeOffset.TryParse(expiresAt,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry) && expiry > utcNow;

        public static DateTimeOffset ExtendSubscription(DateTimeOffset? expiresAt, DateTimeOffset utcNow, int days)
        {
            if (days != SUBSCRIPTION_DAYS) throw new ArgumentOutOfRangeException(nameof(days));
            return (expiresAt.HasValue && expiresAt.Value > utcNow ? expiresAt.Value : utcNow).ToUniversalTime().AddDays(days);
        }
    }
}
