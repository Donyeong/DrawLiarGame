using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private DrawPlatformPaymentService _platformPayments;
        private readonly Dictionary<string, string> _commerceOperations = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _commerceSession = "", _commerceAccount = "";
        private long _commerceServerTime;
        private double _commerceTimeAnchor;
        private bool _commerceRecoveryRequested, _commerceRecovering;
        private int _commerceRecoveryRevision;
        private double _nextCommerceRecovery;
        public CommerceCatalogue Commerce { get; private set; } = EmptyCommerceCatalogue();
        public bool CommerceLoaded { get; private set; }
        public bool IsPlatformPaymentAvailable => IsAuthenticated && CommerceLoaded && _platformPayments?.IsAvailable == true;
        public DateTimeOffset CommerceUtcNow => _commerceServerTime <= 0 ? DateTimeOffset.MinValue
            : DateTimeOffset.FromUnixTimeSeconds(_commerceServerTime).AddSeconds(Math.Min(253402300799 - _commerceServerTime,
                Math.Max(0, Time.realtimeSinceStartupAsDouble - _commerceTimeAnchor)));
        public bool IsPainterSubscriber => Profile?.HasPainterSubscription == true && _commerceServerTime > 0
            && CommerceRules.IsPainterSubscriber(Profile, CommerceUtcNow);
        public event Action CommerceChanged;

        public bool OwnsCosmetic(string productId) => !string.IsNullOrEmpty(productId)
            && (Profile?.OwnedCosmetics ?? Array.Empty<string>()).Contains(productId, StringComparer.Ordinal);

        public string CommercePrice(CommerceProduct product) => product?.Kind == CommerceRules.PAINTER_SUBSCRIPTION || product?.Kind == CommerceRules.COSMETIC
            ? DrawLocalization.Format("{0} 보석", product.PricePaidGems)
            : _platformPayments?.LocalizedPrice(product) ?? DrawLocalization.Text("현재 결제를 이용할 수 없습니다.");

        public bool CanPurchaseCommerce(CommerceProduct product)
        {
            if (!IsAuthenticated || !CommerceLoaded || product == null || IsBusy || _loggingOut) return false;
            if (!(Commerce.Products ?? Array.Empty<CommerceProduct>()).Any(candidate => candidate?.Id == product.Id)) return false;
            if (product.Kind != CommerceRules.COSMETIC && !DrawPlatformPaymentProviders.IsPurchasePlatformSupported) return false;
            return product.Kind == CommerceRules.PAINTER_SUBSCRIPTION || product.Kind == CommerceRules.COSMETIC
                ? product.PricePaidGems > 0 && Profile.PaidGems >= product.PricePaidGems
                    && (product.Kind != CommerceRules.COSMETIC || !OwnsCosmetic(product.Id))
                : _platformPayments?.CanPurchase(product) == true;
        }

        public Task RefreshCommerceAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            await RefreshCommerceCoreAsync(cancellationToken);
            SetStatus("상점을 갱신했습니다.");
        });

        private async Task RefreshCommerceCoreAsync(CancellationToken cancellationToken = default)
        {
            RequireLogin();
            EnsureCommerceSession();
            string session = _gameSession, account = Profile.AccountId;
            var catalogue = await SendAsync<CommerceCatalogue>(_gameServerUrl, "/api/commerce", "GET", bearer: session, cancellationToken: cancellationToken);
            EnsureCommerceIdentity(session, account, cancellationToken);
            ValidateCommerceCatalogue(catalogue);
            Commerce = catalogue;
            CommerceLoaded = true;
            CaptureCommerceClock(catalogue.ServerTimeUnixSeconds);
            _platformPayments.ApplyCatalogue(catalogue);
            CommerceChanged?.Invoke();
            if (_platformPayments.IsAvailable)
            {
                try { await _platformPayments.RefreshPricesAsync(cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
            EnsureCommerceIdentity(session, account, cancellationToken);
            var profile = await SendAsync<ProfileData>(_gameServerUrl, "/api/profile", "GET", bearer: session, cancellationToken: cancellationToken);
            EnsureCommerceIdentity(session, account, cancellationToken);
            ApplyCommerceProfile(profile, account);
            RequestCommerceRecovery();
        }

        public Task PurchaseCommerceAsync(string productId, int expectedPricePaidGems, CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            RequireLogin();
            EnsureCommerceSession();
            if (!CommerceLoaded) throw new InvalidOperationException(DrawLocalization.Text("현재 구매할 수 없는 아이템이 있어요."));
            var product = (Commerce.Products ?? Array.Empty<CommerceProduct>()).FirstOrDefault(candidate => candidate?.Id == productId);
            if (product == null) throw new InvalidOperationException(DrawLocalization.Text("현재 구매할 수 없는 아이템이 있어요."));
            if (product.Kind != CommerceRules.COSMETIC && !DrawPlatformPaymentProviders.IsPurchasePlatformSupported)
                throw new InvalidOperationException(Application.platform == RuntimePlatform.WebGLPlayer
                    ? DrawLocalization.Text("웹에서는 구매할 수 없습니다. PC(Steam) 또는 모바일(Play Store)에서 구매해 주세요.")
                    : DrawLocalization.Text("현재 결제를 이용할 수 없습니다."));
            string session = _gameSession, account = Profile.AccountId;
            if (product.Kind == CommerceRules.PAINTER_SUBSCRIPTION || product.Kind == CommerceRules.COSMETIC)
            {
                if (product.PricePaidGems != expectedPricePaidGems)
                    throw new InvalidOperationException(DrawLocalization.Text("상품 정보가 변경되었습니다. 상점을 새로고침해 주세요."));
                if (!_commerceOperations.TryGetValue(product.Id, out string operationId))
                    _commerceOperations[product.Id] = operationId = Guid.NewGuid().ToString("N");
                var profile = await SendAsync<ProfileData>(_gameServerUrl, "/api/commerce/purchase", "POST",
                    new CommercePurchaseRequest { ProductId = product.Id, OperationId = operationId, ExpectedPricePaidGems = expectedPricePaidGems }, session, cancellationToken);
                EnsureCommerceIdentity(session, account, cancellationToken);
                ApplyCommerceProfile(profile, account);
                _commerceOperations.Remove(product.Id);
                SetStatus(product.Kind == CommerceRules.COSMETIC ? "아이템을 구매했습니다." : "구독을 구매했습니다.");
            }
            else
            {
                CommercePaymentResponse response;
                try { response = await _platformPayments.PurchaseAsync(product, cancellationToken); }
                finally { RequestCommerceRecovery(); }
                EnsureCommerceIdentity(session, account, cancellationToken);
                SetStatus(response.Order.State == "Completed" ? "결제가 확인되었습니다." : "결제 확인을 기다리고 있습니다.");
            }
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            EnsureCommerceIdentity(session, account, cancellationToken);
            CommerceChanged?.Invoke();
        });

        public Task RestoreCommerceAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            RequireLogin();
            EnsureCommerceSession();
            await RefreshCommerceCoreAsync(cancellationToken);
            await _platformPayments.RestoreAsync(cancellationToken);
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            SetStatus("구매 내역을 확인했습니다.");
            CommerceChanged?.Invoke();
        });

        public Task SaveCosmeticAsync(string category, string productId, CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            RequireLogin();
            if (!CosmeticRules.IsCategory(category) || (!string.IsNullOrEmpty(productId)
                && (CosmeticRules.Find(productId)?.Category != category || !OwnsCosmetic(productId))))
                throw new InvalidOperationException(DrawLocalization.Text("보유한 아이템만 장착할 수 있습니다."));
            string session = _gameSession, account = Profile.AccountId;
            var profile = await SendAsync<ProfileData>(_gameServerUrl, "/api/profile/cosmetics", "POST",
                new CosmeticEquipRequest { Category = category, ProductId = productId ?? "" }, session, cancellationToken);
            EnsureCommerceIdentity(session, account, cancellationToken);
            ApplyCommerceProfile(profile, account);
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            EnsureCommerceIdentity(session, account, cancellationToken);
            SetStatus("장식을 적용했습니다.");
            CommerceChanged?.Invoke();
        });

        public Task SaveSubscriberBadgeAsync(bool showBadge, CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            RequireLogin();
            string session = _gameSession, account = Profile.AccountId;
            var profile = await SendAsync<ProfileData>(_gameServerUrl, "/api/profile/subscriber-badge", "POST",
                new SubscriberBadgeRequest { ShowBadge = showBadge }, session, cancellationToken);
            EnsureCommerceIdentity(session, account, cancellationToken);
            ApplyCommerceProfile(profile, account);
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            EnsureCommerceIdentity(session, account, cancellationToken);
            SetStatus("구독자 뱃지를 저장했습니다.");
            CommerceChanged?.Invoke();
        });

        private void EnsureCommerceSession()
        {
            RequireLogin();
            if (_platformPayments == null)
            {
                _platformPayments = new DrawPlatformPaymentService(new CommerceBackend(this));
                _platformPayments.PricesChanged += OnCommercePricesChanged;
                _platformPayments.ProfileConfirmed += OnCommerceProfileConfirmed;
                _platformPayments.PurchasesChanged += RequestCommerceRecovery;
            }
            if (_commerceSession == _gameSession && _commerceAccount == Profile.AccountId) return;
            _commerceSession = _gameSession;
            _commerceAccount = Profile.AccountId;
            _platformPayments.StartSession(_commerceAccount, _lifetime);
        }

        private void OnCommercePricesChanged() => CommerceChanged?.Invoke();
        private void OnCommerceProfileConfirmed(ProfileData profile) => ApplyCommerceProfile(profile, _commerceAccount);

        private void RequestCommerceRecovery()
        {
            _commerceRecoveryRequested = true;
            _nextCommerceRecovery = 0;
            ++_commerceRecoveryRevision;
        }

        private void PollCommerceRecovery()
        {
            if (!_commerceRecoveryRequested || _commerceRecovering || !IsAuthenticated || IsBusy
                || _loggingOut || _lobbyChatPaused || DrawPlatformPaymentProviders.Find() == null
                || (CommerceLoaded && _platformPayments?.IsAvailable != true)
                || Time.realtimeSinceStartupAsDouble < _nextCommerceRecovery) return;
            _ = RecoverCommerceAsync();
        }

        private async Task RecoverCommerceAsync()
        {
            _commerceRecovering = true;
            _commerceRecoveryRequested = false;
            int revision = _commerceRecoveryRevision;
            string session = _gameSession, account = Profile.AccountId;
            try
            {
                if (!CommerceLoaded) await RefreshCommerceCoreAsync(_lifetime);
                EnsureCommerceIdentity(session, account, _lifetime);
                if (_platformPayments?.IsAvailable != true) return;
                await _platformPayments.RestoreAsync(_lifetime);
                EnsureCommerceIdentity(session, account, _lifetime);
                if (_platformPayments.HasPendingPurchases) _commerceRecoveryRequested = true;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || session != _gameSession
                || account != Profile?.AccountId || _loggingOut) { }
            catch (Exception)
            {
                if (session == _gameSession && account == Profile?.AccountId) _commerceRecoveryRequested = true;
            }
            finally
            {
                _commerceRecovering = false;
                if (revision == _commerceRecoveryRevision)
                    _nextCommerceRecovery = Time.realtimeSinceStartupAsDouble + 30;
            }
        }

        private void ApplyCommerceProfile(ProfileData profile, string account)
        {
            if (profile?.AccountId != account || profile.PaidGems < 0 || profile.Coins < 0 || profile.Experience < 0
                || profile.ServerTimeUnixSeconds <= 0 || profile.ServerTimeUnixSeconds > 253402300799)
                throw new InvalidOperationException(DrawLocalization.Text("결제 정보를 확인할 수 없습니다."));
            SetProfile(profile);
            CommerceChanged?.Invoke();
        }

        private void EnsureCommerceIdentity(string session, string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.ThrowIfCancellationRequested();
            if (_loggingOut || session != _gameSession || account != Profile?.AccountId) throw new OperationCanceledException(cancellationToken);
        }

        private static void ValidateCommerceCatalogue(CommerceCatalogue catalogue)
        {
            if (catalogue == null || catalogue.ServerTimeUnixSeconds <= 0 || catalogue.ServerTimeUnixSeconds > 253402300799)
                throw new InvalidOperationException(DrawLocalization.Text("결제 정보를 확인할 수 없습니다."));
            catalogue.Products ??= Array.Empty<CommerceProduct>();
            catalogue.Providers ??= Array.Empty<CommerceProviderData>();
            if (catalogue.Products.Any(product => product == null || string.IsNullOrWhiteSpace(product.Id)
                    || product.PaidGems < 0 || product.PricePaidGems < 0
                    || (product.Kind == CommerceRules.PAID_GEMS && (product.PaidGems <= 0 || string.IsNullOrWhiteSpace(product.StoreProductId)))
                    || (product.Kind == CommerceRules.PAINTER_SUBSCRIPTION && (product.SubscriptionDays != CommerceRules.SUBSCRIPTION_DAYS || product.PricePaidGems <= 0))
                    || (product.Kind == CommerceRules.COSMETIC && (product.PricePaidGems <= 0 || !CosmeticRules.IsCategory(product.Category)
                        || CosmeticRules.Find(product.Id)?.Category != product.Category))
                    || (product.Kind != CommerceRules.PAID_GEMS && product.Kind != CommerceRules.PAINTER_SUBSCRIPTION && product.Kind != CommerceRules.COSMETIC))
                || catalogue.Products.Select(product => product.Id).Distinct(StringComparer.Ordinal).Count() != catalogue.Products.Length
                || catalogue.Providers.Any(provider => provider == null || (provider.Id != DrawPlatformPaymentProviders.STEAM && provider.Id != DrawPlatformPaymentProviders.GOOGLE_PLAY)))
                throw new InvalidOperationException(DrawLocalization.Text("결제 정보를 확인할 수 없습니다."));
        }

        private void CaptureCommerceClock(long serverTime)
        {
            if (serverTime <= 0 || serverTime > 253402300799) return;
            _commerceServerTime = serverTime;
            _commerceTimeAnchor = Time.realtimeSinceStartupAsDouble;
        }

        private static CommerceCatalogue EmptyCommerceCatalogue() => new CommerceCatalogue { Products = CommerceRules.CreateProducts() };

        private void ResetCommerce(bool resetIdentity = true)
        {
            _platformPayments?.Reset();
            _commerceRecoveryRequested = false;
            ++_commerceRecoveryRevision;
            _commerceSession = _commerceAccount = "";
            Commerce = EmptyCommerceCatalogue();
            CommerceLoaded = false;
            if (resetIdentity)
            {
                _commerceOperations.Clear();
                _commerceServerTime = 0;
                _commerceTimeAnchor = 0;
            }
            CommerceChanged?.Invoke();
        }

        private void DisposeCommerce()
        {
            if (_platformPayments == null) return;
            _platformPayments.PricesChanged -= OnCommercePricesChanged;
            _platformPayments.ProfileConfirmed -= OnCommerceProfileConfirmed;
            _platformPayments.PurchasesChanged -= RequestCommerceRecovery;
            _platformPayments.Dispose();
            _platformPayments = null;
        }

        private sealed class CommerceBackend : IDrawCommerceBackend, IDrawCommerceOrderLookup
        {
            private readonly LobbyServiceBridge _owner;
            public CommerceBackend(LobbyServiceBridge owner) => _owner = owner;
            public bool IsCurrentAccount(string accountId) => _owner != null && !_owner._loggingOut
                && _owner._commerceSession == _owner._gameSession && _owner.Profile?.AccountId == accountId;
            public Task<CommercePaymentResponse> PrepareAsync(CommercePrepareRequest request, CancellationToken cancellationToken)
                => _owner.SendAsync<CommercePaymentResponse>(_owner._gameServerUrl, "/api/payments/prepare", "POST", request, _owner._commerceSession, cancellationToken);
            public Task<CommercePaymentResponse> ConfirmAsync(CommerceConfirmRequest request, CancellationToken cancellationToken)
                => _owner.SendAsync<CommercePaymentResponse>(_owner._gameServerUrl, "/api/payments/confirm", "POST", request, _owner._commerceSession, cancellationToken);
            public Task<CommerceOrdersResponse> OrdersAsync(CancellationToken cancellationToken)
                => _owner.SendAsync<CommerceOrdersResponse>(_owner._gameServerUrl, "/api/payments/orders", "GET", bearer: _owner._commerceSession, cancellationToken: cancellationToken);
            public async Task<CommerceOrderData> OrderAsync(string orderId, CancellationToken cancellationToken)
            {
                try
                {
                    return await _owner.SendAsync<CommerceOrderData>(_owner._gameServerUrl, "/api/payments/orders/" + Uri.EscapeDataString(orderId),
                        "GET", bearer: _owner._commerceSession, cancellationToken: cancellationToken);
                }
                catch (ApiRequestException exception) when (exception.Code == "PaymentOrderNotFound") { return null; }
            }
        }
    }
}
