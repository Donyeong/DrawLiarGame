using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
#if UNITY_ANDROID && !UNITY_EDITOR
    internal sealed class DrawGooglePlayPaymentProvider : MonoBehaviour, IDrawPlatformPaymentProvider, IDrawPlatformPurchaseNotifications
    {
        [Serializable] private sealed class ProductQuery { public string[] ProductIds = Array.Empty<string>(); }
        [Serializable] private sealed class NativePrice { public string StoreProductId = ""; public string Price = ""; }
        [Serializable] private sealed class NativePurchase
        {
            public string OrderId = "";
            public string AccountBinding = "";
            public string StoreProductId = "";
            public string Proof = "";
            public bool Pending = false;
        }
        [Serializable] private sealed class NativeResponse
        {
            public int Code = -1;
            public NativePrice[] Prices = Array.Empty<NativePrice>();
            public NativePurchase[] Purchases = Array.Empty<NativePurchase>();
        }

        private AndroidJavaObject _native;
        private int _mainThreadId;
        private int _revision;
        private double _nextPoll;
        private bool _disposed;
        private CancellationTokenSource _session = new CancellationTokenSource();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            var runtime = new GameObject(nameof(DrawGooglePlayPaymentProvider));
            DontDestroyOnLoad(runtime);
            runtime.AddComponent<DrawGooglePlayPaymentProvider>();
        }

        private void Awake()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _native = new AndroidJavaObject("com.rascallab.liargame.billing.DrawBilling", activity);
                DrawPlatformPaymentProviders.Register(this);
            }
            catch (Exception) { _native?.Dispose(); _native = null; }
        }

        private void Update()
        {
            if (!IsAvailable || Time.realtimeSinceStartupAsDouble < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartupAsDouble + 1;
            try
            {
                int revision = _native.Call<int>("Revision");
                if (_revision == revision) return;
                _revision = revision;
                PurchasesChanged?.Invoke();
            }
            catch (Exception) { }
        }

        private void OnDestroy()
        {
            _disposed = true;
            _session.Cancel();
            _session.Dispose();
            DrawPlatformPaymentProviders.Unregister(this);
            if (_native == null) return;
            try { _native.Call("Close"); }
            catch (Exception) { }
            _native.Dispose();
            _native = null;
        }

        public event Action PurchasesChanged;
        public string Id => DrawPlatformPaymentProviders.GOOGLE_PLAY;
        public bool IsAvailable => !_disposed && _native != null;

        public Task<string> GetAuthenticationProofAsync(CancellationToken cancellationToken)
        {
            EnsureAvailable(cancellationToken);
            return Task.FromResult("");
        }

        public async Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(CommerceProduct[] products, CancellationToken cancellationToken)
        {
            EnsureAvailable(cancellationToken);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var product in products ?? Array.Empty<CommerceProduct>())
                if (product?.Kind == CommerceRules.PAID_GEMS && !string.IsNullOrWhiteSpace(product.StoreProductId)) ids.Add(product.StoreProductId);
            var prices = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ids.Count == 0) return prices;
            var query = new ProductQuery { ProductIds = new List<string>(ids).ToArray() };
            var response = await RequestAsync("QueryPrices", cancellationToken, 45, JsonUtility.ToJson(query));
            foreach (var entry in response.Prices ?? Array.Empty<NativePrice>())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Price)) continue;
                foreach (var product in products)
                    if (product != null && product.StoreProductId == entry.StoreProductId) prices[product.Id] = entry.Price;
            }
            return prices;
        }

        public async Task<DrawPlatformPurchase> PurchaseAsync(CommerceOrderData order, CancellationToken cancellationToken)
        {
            EnsureAvailable(cancellationToken);
            if (order == null || order.Provider != Id || string.IsNullOrWhiteSpace(order.StoreProductId)
                || string.IsNullOrWhiteSpace(order.OrderId) || order.OrderId.Length > 64
                || string.IsNullOrWhiteSpace(order.AccountBinding) || order.AccountBinding.Length > 64) throw InvalidPurchase();
            var response = await RequestAsync("Purchase", cancellationToken, 1200, order.OrderId, order.AccountBinding, order.StoreProductId);
            if (response.Code == 1)
                return new DrawPlatformPurchase { OrderId = order.OrderId, StoreProductId = order.StoreProductId, Cancelled = true };
            foreach (var purchase in response.Purchases ?? Array.Empty<NativePurchase>())
                if (Matches(purchase, order)) return Convert(purchase);
            throw InvalidPurchase();
        }

        public async Task<DrawPlatformPurchase[]> FetchPurchasesAsync(CommerceOrderData[] orders, CancellationToken cancellationToken)
        {
            var response = await RequestAsync("FetchPurchases", cancellationToken, 45);
            var purchases = new List<DrawPlatformPurchase>();
            foreach (var purchase in response.Purchases ?? Array.Empty<NativePurchase>())
            {
                if (purchase == null || string.IsNullOrWhiteSpace(purchase.OrderId) || string.IsNullOrWhiteSpace(purchase.Proof)) continue;
                CommerceOrderData known = null;
                foreach (var order in orders ?? Array.Empty<CommerceOrderData>())
                    if (order?.OrderId == purchase.OrderId) { known = order; break; }
                if (known == null || (known.Provider == Id && Matches(purchase, known))) purchases.Add(Convert(purchase));
            }
            return purchases.ToArray();
        }

        public Task CompleteAsync(DrawPlatformPurchase purchase, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 서버가 지급 확정 후 구매 토큰을 소비한다.
            return Task.CompletedTask;
        }

        public void Reset()
        {
            EnsureMainThread();
            _session.Cancel();
            _session.Dispose();
            _session = new CancellationTokenSource();
            if (!IsAvailable) return;
            try { _native.Call("Reset"); }
            catch (Exception) { }
        }

        private async Task<NativeResponse> RequestAsync(string method, CancellationToken cancellationToken, int timeoutSeconds, params string[] arguments)
        {
            EnsureAvailable(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
            string request = Guid.NewGuid().ToString("N");
            var parameters = new object[arguments.Length + 1];
            parameters[0] = request;
            for (int i = 0; i < arguments.Length; i++) parameters[i + 1] = arguments[i];
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            try
            {
                _native.Call(method, parameters);
                while (true)
                {
                    EnsureAvailable(linked.Token);
                    string json = _native.Call<string>("TakeResult", request);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var response = JsonUtility.FromJson<NativeResponse>(json);
                        if (response == null || (response.Code != 0 && !(method == "Purchase" && response.Code == 1))) throw Unavailable();
                        return response;
                    }
                    if (Time.realtimeSinceStartupAsDouble >= deadline) throw Unavailable();
                    await DrawAsync.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
                }
            }
            catch (AndroidJavaException) { throw Unavailable(); }
            finally
            {
                if (_native != null)
                    try { _native.Call("Cancel", request); }
                    catch (Exception) { }
            }
        }

        private void EnsureAvailable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureMainThread();
            if (!IsAvailable) throw Unavailable();
        }

        private void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) throw Unavailable();
        }

        private static bool Matches(NativePurchase purchase, CommerceOrderData order) => purchase != null
            && !string.IsNullOrWhiteSpace(purchase.Proof) && purchase.OrderId == order.OrderId
            && purchase.AccountBinding == order.AccountBinding && purchase.StoreProductId == order.StoreProductId;

        private static DrawPlatformPurchase Convert(NativePurchase purchase) => new DrawPlatformPurchase
        {
            OrderId = purchase.OrderId, StoreProductId = purchase.StoreProductId, Proof = purchase.Proof, Pending = purchase.Pending
        };
        private static InvalidOperationException Unavailable() => new InvalidOperationException(DrawLocalization.Text("현재 결제를 이용할 수 없습니다."));
        private static InvalidOperationException InvalidPurchase() => new InvalidOperationException(DrawLocalization.Text("결제 정보를 확인할 수 없습니다."));
    }
#endif
}
