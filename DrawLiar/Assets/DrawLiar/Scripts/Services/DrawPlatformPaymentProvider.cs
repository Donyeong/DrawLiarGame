using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    public interface IDrawPlatformPaymentProvider
    {
        string Id { get; }
        bool IsAvailable { get; }
        Task<string> GetAuthenticationProofAsync(CancellationToken cancellationToken);
        Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(CommerceProduct[] products, CancellationToken cancellationToken);
        Task<DrawPlatformPurchase> PurchaseAsync(CommerceOrderData order, CancellationToken cancellationToken);
        Task<DrawPlatformPurchase[]> FetchPurchasesAsync(CommerceOrderData[] orders, CancellationToken cancellationToken);
        // 스토어 승인·소비는 서버 지급이 확정된 거래에만 호출하며 중복 호출도 허용해야 한다.
        Task CompleteAsync(DrawPlatformPurchase purchase, CancellationToken cancellationToken);
        void Reset();
    }

    public sealed class DrawPlatformPurchase
    {
        public string OrderId = "";
        public string StoreProductId = "";
        public string Proof = "";
        public bool Pending;
        public bool Cancelled;
    }

    public static class DrawPlatformPaymentProviders
    {
        public const string STEAM = "Steam";
        public const string GOOGLE_PLAY = "GooglePlay";
        private static readonly Dictionary<string, IDrawPlatformPaymentProvider> PROVIDERS = new Dictionary<string, IDrawPlatformPaymentProvider>(StringComparer.Ordinal);

        public static void Register(IDrawPlatformPaymentProvider provider)
        {
            if (provider == null || (provider.Id != STEAM && provider.Id != GOOGLE_PLAY))
                throw new ArgumentException(nameof(provider));
            PROVIDERS[provider.Id] = provider;
        }

        public static void Unregister(IDrawPlatformPaymentProvider provider)
        {
            if (provider != null && PROVIDERS.TryGetValue(provider.Id, out var current) && ReferenceEquals(provider, current))
                PROVIDERS.Remove(provider.Id);
        }

        public static IDrawPlatformPaymentProvider Find()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string PLATFORM = GOOGLE_PLAY;
#elif UNITY_STANDALONE && !UNITY_EDITOR
            const string PLATFORM = STEAM;
#else
            return null;
#endif
#if (UNITY_ANDROID || UNITY_STANDALONE) && !UNITY_EDITOR
            return PROVIDERS.TryGetValue(PLATFORM, out var provider) && provider.IsAvailable ? provider : null;
#endif
        }
    }
}
