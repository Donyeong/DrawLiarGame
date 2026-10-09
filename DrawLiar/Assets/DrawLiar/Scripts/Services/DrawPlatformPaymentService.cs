using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    public interface IDrawCommerceBackend
    {
        bool IsCurrentAccount(string accountId);
        Task<CommercePaymentResponse> PrepareAsync(CommercePrepareRequest request, CancellationToken cancellationToken);
        Task<CommercePaymentResponse> ConfirmAsync(CommerceConfirmRequest request, CancellationToken cancellationToken);
        Task<CommerceOrdersResponse> OrdersAsync(CancellationToken cancellationToken);
    }

    public sealed class DrawPlatformPaymentService : IDisposable
    {
        private readonly IDrawCommerceBackend _backend;
        private readonly Func<IDrawPlatformPaymentProvider> _findProvider;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _prices = new Dictionary<string, string>(StringComparer.Ordinal);
        private CancellationTokenSource _session;
        private string _accountId = "";
        private CommerceCatalogue _catalogue = new CommerceCatalogue();
        public event Action PricesChanged;
        public event Action<ProfileData> ProfileConfirmed;

        public DrawPlatformPaymentService(IDrawCommerceBackend backend, Func<IDrawPlatformPaymentProvider> findProvider = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _findProvider = findProvider ?? DrawPlatformPaymentProviders.Find;
        }

        public bool IsAvailable => FindProvider() != null;
        public bool CanPurchase(CommerceProduct product) => product != null && product.Kind == CommerceRules.PAID_GEMS
            && IsAvailable && _prices.ContainsKey(product.Id);
        public string LocalizedPrice(CommerceProduct product) => product != null && CanPurchase(product)
            ? _prices[product.Id] : DrawLocalization.Text("현재 결제를 이용할 수 없습니다.");

        public void StartSession(string accountId, CancellationToken cancellationToken)
        {
            Reset();
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException(nameof(accountId));
            _accountId = accountId;
            _session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public void ApplyCatalogue(CommerceCatalogue catalogue)
        {
            _catalogue = catalogue ?? new CommerceCatalogue();
            _prices.Clear();
            PricesChanged?.Invoke();
        }

        public async Task RefreshPricesAsync(CancellationToken cancellationToken = default)
        {
            var provider = FindProvider();
            if (provider == null || _session == null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
            string account = _accountId;
            await _gate.WaitAsync(linked.Token);
            try
            {
                EnsureSession(account, linked.Token);
                var products = (_catalogue.Products ?? Array.Empty<CommerceProduct>()).Where(product => product?.Kind == CommerceRules.PAID_GEMS).ToArray();
                var prices = await provider.GetLocalizedPricesAsync(products, linked.Token);
                EnsureSession(account, linked.Token);
                if (!ReferenceEquals(provider, FindProvider())) throw Unavailable();
                _prices.Clear();
                if (prices != null)
                    foreach (var product in products)
                        if (prices.TryGetValue(product.Id, out string price) && !string.IsNullOrWhiteSpace(price) && price.Length <= 64)
                            _prices[product.Id] = price;
                PricesChanged?.Invoke();
            }
            finally { _gate.Release(); }
        }

        public async Task<CommercePaymentResponse> PurchaseAsync(CommerceProduct expected, CancellationToken cancellationToken = default)
        {
            if (_session == null || !CanPurchase(expected)) throw Unavailable();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
            string account = _accountId;
            await _gate.WaitAsync(linked.Token);
            try
            {
                EnsureSession(account, linked.Token);
                var provider = FindProvider();
                if (provider == null || !CanPurchase(expected)) throw Unavailable();
                var request = new CommercePrepareRequest { ProductId = expected.Id, Provider = provider.Id, OperationId = Guid.NewGuid().ToString("N") };
                CommercePaymentResponse prepared;
                try
                {
                    request.StoreProof = await provider.GetAuthenticationProofAsync(linked.Token);
                    EnsureSession(account, linked.Token);
                    prepared = await _backend.PrepareAsync(request, linked.Token);
                }
                finally { request.StoreProof = ""; }
                EnsureSession(account, linked.Token);
                ValidateResponse(prepared, account, null);
                if (!SameProduct(expected, prepared.Product))
                    throw new InvalidOperationException(DrawLocalization.Text("상품 정보가 변경되었습니다. 상점을 새로고침해 주세요."));
                var order = prepared.Order;
                if (order.ProductId != expected.Id || order.Provider != provider.Id || order.StoreProductId != expected.StoreProductId
                    || order.State != "Prepared" || string.IsNullOrWhiteSpace(order.AccountBinding)) throw InvalidPurchase();
                if (!ReferenceEquals(provider, FindProvider())) throw Unavailable();
                var purchase = await provider.PurchaseAsync(order, linked.Token);
                try
                {
                    EnsureSession(account, linked.Token);
                    ValidatePurchase(purchase, order);
                    if (purchase.Cancelled) throw new OperationCanceledException(linked.Token);
                    if (purchase.Pending) return new CommercePaymentResponse { Order = order, Product = prepared.Product, Profile = prepared.Profile };
                    return await ConfirmAsync(purchase, order, provider, account, linked.Token);
                }
                finally { if (purchase != null) purchase.Proof = ""; }
            }
            finally { _gate.Release(); }
        }

        public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
        {
            if (_session == null || FindProvider() == null) throw Unavailable();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _session.Token);
            string account = _accountId;
            await _gate.WaitAsync(linked.Token);
            DrawPlatformPurchase[] purchases = null;
            try
            {
                EnsureSession(account, linked.Token);
                var provider = FindProvider();
                if (provider == null) throw Unavailable();
                var response = await _backend.OrdersAsync(linked.Token);
                EnsureSession(account, linked.Token);
                var orders = (response?.Orders ?? Array.Empty<CommerceOrderData>()).Where(order => order != null && order.Provider == provider.Id).ToArray();
                purchases = await provider.FetchPurchasesAsync(orders, linked.Token) ?? Array.Empty<DrawPlatformPurchase>();
                EnsureSession(account, linked.Token);
                int recovered = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var purchase in purchases)
                {
                    if (purchase == null || purchase.Cancelled || purchase.Pending || !seen.Add(purchase.OrderId)) continue;
                    var order = orders.FirstOrDefault(candidate => candidate.OrderId == purchase.OrderId);
                    if (order == null) continue;
                    ValidatePurchase(purchase, order);
                    var confirmed = await ConfirmAsync(purchase, order, provider, account, linked.Token);
                    if (confirmed.Order.State == "Completed") recovered++;
                }
                return recovered;
            }
            finally
            {
                if (purchases != null) foreach (var purchase in purchases) if (purchase != null) purchase.Proof = "";
                _gate.Release();
            }
        }

        private async Task<CommercePaymentResponse> ConfirmAsync(DrawPlatformPurchase purchase, CommerceOrderData order,
            IDrawPlatformPaymentProvider provider, string account, CancellationToken cancellationToken)
        {
            EnsureSession(account, cancellationToken);
            if (string.IsNullOrWhiteSpace(purchase.Proof)) throw InvalidPurchase();
            var request = new CommerceConfirmRequest { OrderId = order.OrderId, Proof = purchase.Proof };
            CommercePaymentResponse confirmed;
            try { confirmed = await _backend.ConfirmAsync(request, cancellationToken); }
            finally { request.Proof = ""; }
            EnsureSession(account, cancellationToken);
            ValidateResponse(confirmed, account, order);
            if (confirmed.Order.State == "Completed")
            {
                ProfileConfirmed?.Invoke(confirmed.Profile);
                // 지급 이후 승인 실패는 영수증을 보존해 복구 시 다시 승인한다.
                try { await provider.CompleteAsync(purchase, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
                EnsureSession(account, cancellationToken);
            }
            return confirmed;
        }

        private IDrawPlatformPaymentProvider FindProvider()
        {
            var provider = _findProvider();
            return provider != null && provider.IsAvailable && (_catalogue.Providers ?? Array.Empty<CommerceProviderData>())
                .Any(candidate => candidate != null && candidate.Id == provider.Id && candidate.Available) ? provider : null;
        }

        private void EnsureSession(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session == null || _session.IsCancellationRequested || _accountId != accountId || !_backend.IsCurrentAccount(accountId))
                throw new OperationCanceledException(cancellationToken);
        }

        private static void ValidatePurchase(DrawPlatformPurchase purchase, CommerceOrderData order)
        {
            if (purchase == null || purchase.OrderId != order.OrderId || purchase.StoreProductId != order.StoreProductId) throw InvalidPurchase();
        }

        private static void ValidateResponse(CommercePaymentResponse response, string account, CommerceOrderData expected)
        {
            var order = response?.Order;
            if (order == null || string.IsNullOrWhiteSpace(order.OrderId) || response.Profile?.AccountId != account
                || response.Profile.PaidGems < 0 || response.Profile.Coins < 0 || response.Profile.Experience < 0
                || (order.State != "Prepared" && order.State != "Completed" && order.State != "ReviewRequired")
                || (expected != null && (order.OrderId != expected.OrderId || order.ProductId != expected.ProductId
                    || order.Provider != expected.Provider || order.StoreProductId != expected.StoreProductId
                    || order.AccountBinding != expected.AccountBinding))) throw InvalidPurchase();
        }

        private static bool SameProduct(CommerceProduct expected, CommerceProduct actual) => actual != null && expected.Id == actual.Id
            && expected.Kind == actual.Kind && expected.PaidGems == actual.PaidGems && expected.PricePaidGems == actual.PricePaidGems
            && expected.SubscriptionDays == actual.SubscriptionDays && expected.StoreProductId == actual.StoreProductId;
        private static InvalidOperationException Unavailable() => new InvalidOperationException(DrawLocalization.Text("현재 결제를 이용할 수 없습니다."));
        private static InvalidOperationException InvalidPurchase() => new InvalidOperationException(DrawLocalization.Text("결제 정보를 확인할 수 없습니다."));

        public void Reset()
        {
            _session?.Cancel(); _session?.Dispose(); _session = null;
            _accountId = "";
            _catalogue = new CommerceCatalogue();
            _prices.Clear();
            _findProvider()?.Reset();
        }

        public void Dispose() => Reset();
    }
}
