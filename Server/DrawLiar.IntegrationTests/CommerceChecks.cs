using DrawLiar;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyCommerceAsync()
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var products = CommerceRules.CreateProducts();
        Check(products.Length == 4 && products.Select(product => product.Id).Distinct().Count() == 4
            && products.Where(product => product.Kind == CommerceRules.PAID_GEMS).Select(product => product.PaidGems).SequenceEqual(new[] { 100, 550, 1200 }),
            "보석 충전 세 가지와 구독은 고유 상품 ID를 사용해야 합니다.");
        Check(AvatarParts.CreateShopProducts().Single(product => product.Id == "painter").Name == "화가 세트"
            && products.Single(product => product.Id == CommerceRules.PAINTER_PRODUCT_ID) is { Name: "화가 세트", PricePaidGems: 300, SubscriptionDays: 30 },
            "코인 코스튬 화가 세트와 30일 구독 상품은 영구적으로 분리해야 합니다.");
        Check(CommerceRules.ExtendSubscription(null, now, 30) == now.AddDays(30)
            && CommerceRules.ExtendSubscription(now.AddDays(-1), now, 30) == now.AddDays(30)
            && CommerceRules.ExtendSubscription(now.AddDays(5), now, 30) == now.AddDays(35),
            "신규·만료·잔여 구독은 UTC 기준으로 정확히 30일 연장해야 합니다.");
        var profile = new ProfileData { AccountId = Guid.NewGuid().ToString(), SubscriptionExpiresAt = now.ToString("O"), HasPainterSubscription = true };
        Check(!CommerceRules.IsPainterSubscriber(profile, now) && CommerceRules.IsPainterSubscriber(profile, now.AddSeconds(-1)),
            "만료 시각과 정확히 일치하면 구독 권한이 끝나고 클라이언트 bool로 우회할 수 없어야 합니다.");

        var repository = new CommerceTestRepository(now);
        var account = Guid.NewGuid(); var otherAccount = Guid.NewGuid();
        repository.Add(account); repository.Add(otherAccount);
        var unavailable = new CommerceService(repository);
        Check(unavailable.Catalogue().Providers.All(provider => !provider.Available), "기본 Steam·GooglePlay 공급자는 비활성 상태여야 합니다.");
        await CommerceError(() => unavailable.PrepareAsync(account, Prepare("gems-100")), "PaymentProviderUnavailable");
        Check(repository.OrderCount == 0 && repository.Profile(account).PaidGems == 0, "공급자 미설정은 주문 생성·유료재화 지급 전에 거부해야 합니다.");
        var provider = new CommerceTestProvider();
        var service = new CommerceService(repository, new[] { provider });
        var operation = Guid.NewGuid().ToString();
        var prepared = await service.PrepareAsync(account, Prepare("gems-550", operation));
        var repeated = await service.PrepareAsync(account, Prepare("gems-550", operation));
        Check(prepared.Order.OrderId == repeated.Order.OrderId && repository.OrderCount == 1 && prepared.Product.PaidGems == 550,
            "동일 준비 요청은 원래 주문·보상 스냅샷을 재사용해야 합니다.");
        await CommerceError(() => service.PrepareAsync(account, Prepare("gems-100", operation)), "OperationConflict");
        int verifications = provider.Verifications;
        await CommerceError(() => service.ConfirmAsync(otherAccount, Confirm(prepared, "valid")), "PaymentOrderNotFound");
        Check(provider.Verifications == verifications, "다른 계정은 영수증 검증 전에 주문 소유권으로 차단해야 합니다.");
        foreach (string invalid in new[] { "unpaid", "quantity", "order", "provider", "scope", "sku", "binding", "empty-transaction", "control-transaction" })
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, invalid)), "InvalidPayment");
        Check(repository.Profile(account).PaidGems == 0 && repository.TransactionCount == 0 && repository.LedgerCount == 0,
            "결제 완료·수량·주문·앱·SKU·계정 연결이 잘못된 영수증은 아무 보상도 지급하지 않아야 합니다.");
        var completed = await service.ConfirmAsync(account, Confirm(prepared, "valid"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ConfirmAsync(account, Confirm(prepared, "valid"))));
        Check(completed.Order.State == "Completed" && repository.Profile(account).PaidGems == 550
            && repository.TransactionCount == 1 && repository.LedgerCount == 1 && repository.Profile(account).Coins == 500,
            "동시 확정·응답 유실 재시도는 보석과 원장을 한 번만 지급하고 코인은 유지해야 합니다.");
        var stolen = await service.PrepareAsync(otherAccount, Prepare("gems-550"));
        await CommerceError(() => service.ConfirmAsync(otherAccount, Confirm(stolen, "valid")), "PaymentTransactionAlreadyClaimed");
        var duplicate = await service.PrepareAsync(account, Prepare("gems-550"));
        await CommerceError(() => service.ConfirmAsync(account, Confirm(duplicate, "valid")), "PaymentTransactionAlreadyClaimed");
        Check(repository.Profile(otherAccount).PaidGems == 0 && repository.Profile(account).PaidGems == 550,
            "동일 스토어 거래는 다른 계정·다른 주문에서 재사용할 수 없어야 합니다.");
        await CommerceError(() => repository.SetSubscriberBadgeAsync(account, true, default), "SubscriptionRequired");
        var membership = new CommercePurchaseRequest { ProductId = CommerceRules.PAINTER_PRODUCT_ID,
            OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 299 };
        await CommerceError(() => repository.PurchaseCommerceAsync(account, membership, default), "ProductChanged");
        membership.ExpectedPricePaidGems = 300;
        var subscribed = await repository.PurchaseCommerceAsync(account, membership, default);
        await repository.PurchaseCommerceAsync(account, membership, default);
        Check(subscribed.PaidGems == 250 && subscribed.SubscriptionExpiresAt == now.AddDays(30).ToString("O")
            && repository.Profile(account).PaidGems == 250 && repository.LedgerCount == 2,
            "구독 구매·재전송은 300보석 차감과 30일 권한을 정확히 한 번만 적용해야 합니다.");
        Check((await repository.SetSubscriberBadgeAsync(account, true, default)).ShowSubscriberBadge
            && !(await repository.SetSubscriberBadgeAsync(account, false, default)).ShowSubscriberBadge,
            "활성 구독자는 뱃지를 직접 표시하거나 숨길 수 있어야 합니다.");
        await CommerceError(() => repository.PurchaseCommerceAsync(account, new CommercePurchaseRequest
        { ProductId = "gems-100", OperationId = Guid.NewGuid().ToString() }, default), "ProductUnavailable");
        await CommerceError(() => repository.PurchaseCommerceAsync(account, new CommercePurchaseRequest
        { ProductId = CommerceRules.PAINTER_PRODUCT_ID, OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 300 }, default), "InsufficientPaidGems");
        await repository.SetSubscriberBadgeAsync(account, true, default);
        repository.Now = now.AddDays(30);
        Check(!repository.Profile(account).ShowSubscriberBadge && !repository.Profile(account).HasPainterSubscription,
            "만료 후 저장된 뱃지 선택도 공개 프로필에서 표시하지 않아야 합니다.");
        await CommerceError(() => repository.SetSubscriberBadgeAsync(account, true, default), "SubscriptionRequired");
        await service.ReviewReversalAsync("Steam", "refund");
        await service.ReviewReversalAsync("Steam", "refund");
        Check(repository.ReviewCount == 1 && repository.Profile(account).PaidGems == 250 && repository.LedgerCount == 2,
            "확인된 환불은 감사 검토로 한 번만 기록하고 소비된 재화·원장을 임의 회수하지 않아야 합니다.");
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "valid")), "PaymentReviewRequired");
        Report("결제 공급자 차단·영수증 소유권/앱/SKU/수량·전역 거래 멱등성·코인 분리·30일 구독/가격/뱃지 만료·환불 검토 검증");
    }

    private static CommercePrepareRequest Prepare(string product, string? operation = null) => new()
    { ProductId = product, Provider = "Steam", OperationId = operation ?? Guid.NewGuid().ToString() };
    private static CommerceConfirmRequest Confirm(CommercePaymentResponse prepared, string proof) => new() { OrderId = prepared.Order.OrderId, Proof = proof };
    private static async Task CommerceError(Func<Task> action, string code)
    {
        try { await action(); throw new InvalidOperationException("거부해야 하는 결제 요청입니다: " + code); }
        catch (ApiException error) { Check(error.Code == code, "결제 오류가 정확해야 합니다: " + code + "/" + error.Code); }
    }

    private sealed class CommerceTestProvider : ICommercePaymentProvider, ICommerceReversalProvider
    {
        public string Id => "Steam";
        public string AppScope => "test-app";
        public bool IsAvailable => true;
        public int Verifications;
        public string TransactionId = "transaction-1";
        public Task PrepareAsync(CommerceStoredOrder order, string storeProof, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VerifiedCommercePayment> VerifyAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken)
        {
            Verifications++;
            return Task.FromResult(new VerifiedCommercePayment(proof == "order" ? "other" : order.Order.OrderId,
                proof == "provider" ? "GooglePlay" : Id, proof == "scope" ? "other-app" : AppScope,
                proof == "sku" ? "other-sku" : order.Order.StoreProductId,
                proof == "binding" ? "other-account" : order.Order.AccountBinding,
                proof == "empty-transaction" ? "" : proof == "control-transaction" ? "bad\ntransaction" : TransactionId,
                proof == "quantity" ? 2 : 1, proof != "unpaid"));
        }
        public Task<VerifiedCommerceReversal> VerifyReversalAsync(string proof, CancellationToken cancellationToken) =>
            Task.FromResult(new VerifiedCommerceReversal(Id, AppScope, TransactionId, proof == "refund" ? "Refunded" : "Unknown"));
    }

    private sealed class CommerceTestRepository(DateTimeOffset now) : ICommerceRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, ProfileData> _profiles = new();
        private readonly Dictionary<Guid, CommerceStoredOrder> _orders = new();
        private readonly Dictionary<(string, string, string), Guid> _transactions = new();
        private readonly Dictionary<(Guid, string, Guid), string> _ledger = new();
        private readonly HashSet<Guid> _reviews = new();
        public DateTimeOffset Now = now;
        public int OrderCount => _orders.Count;
        public int TransactionCount => _transactions.Count;
        public int LedgerCount => _ledger.Count;
        public int ReviewCount => _reviews.Count;
        public void Add(Guid account) => _profiles.Add(account, new ProfileData { AccountId = account.ToString(), Coins = 500 });
        public ProfileData Profile(Guid account)
        {
            var profile = _profiles[account];
            return new ProfileData { AccountId = profile.AccountId, Coins = profile.Coins, PaidGems = profile.PaidGems,
                SubscriptionExpiresAt = profile.SubscriptionExpiresAt, HasPainterSubscription = CommerceRules.IsPainterSubscriber(profile, Now),
                ShowSubscriberBadge = profile.ShowSubscriberBadge && CommerceRules.IsPainterSubscriber(profile, Now), ServerTimeUnixSeconds = Now.ToUnixTimeSeconds() };
        }
        public Task<ProfileData> CommerceProfileAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(Profile(accountId));
        public Task<CommerceStoredOrder> PrepareCommerceOrderAsync(CommerceStoredOrder order, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var existing = _orders.Values.FirstOrDefault(item => item.AccountId == order.AccountId && item.OperationId == order.OperationId);
                if (existing != null && (existing.Order.ProductId != order.Order.ProductId || existing.Order.Provider != order.Order.Provider || existing.AppScope != order.AppScope))
                    throw new ApiException("OperationConflict", 409);
                if (existing != null) return Task.FromResult(existing);
                _orders.Add(Guid.Parse(order.Order.OrderId), order); return Task.FromResult(order);
            }
        }
        public Task<CommerceStoredOrder> CommerceOrderAsync(Guid accountId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(_orders.TryGetValue(orderId, out var order) && order.AccountId == accountId ? order : throw new ApiException("PaymentOrderNotFound", 404));
        public Task<CommerceOrdersResponse> CommerceOrdersAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(new CommerceOrdersResponse
        { Orders = _orders.Values.Where(order => order.AccountId == accountId).Select(order => order.Order).ToArray(), ServerTimeUnixSeconds = Now.ToUnixTimeSeconds() });
        public Task<CommercePaymentResponse> CompleteCommerceOrderAsync(CommerceStoredOrder order, string appScope, string transactionId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var key = (order.Order.Provider, appScope, ServerRuntime.Hash(transactionId)); var id = Guid.Parse(order.Order.OrderId);
                if (_transactions.TryGetValue(key, out var previous) && previous != id) throw new ApiException("PaymentTransactionAlreadyClaimed", 409);
                if (order.Order.State == "ReviewRequired") throw new ApiException("PaymentReviewRequired", 409);
                if (order.Order.State != "Completed")
                {
                    _profiles[order.AccountId].PaidGems = checked(_profiles[order.AccountId].PaidGems + order.Product.PaidGems);
                    _transactions.Add(key, id); _ledger.Add((order.AccountId, CommerceRules.PAID_GEMS, id), order.Product.Id);
                    order.Order.State = "Completed"; order.Order.CompletedAt = Now.ToString("O");
                }
                return Task.FromResult(new CommercePaymentResponse { Order = order.Order, Product = order.Product, Profile = Profile(order.AccountId) });
            }
        }
        public Task<ProfileData> PurchaseCommerceAsync(Guid accountId, CommercePurchaseRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var product = CommerceRules.CreateProducts().FirstOrDefault(product => product.Id == request.ProductId && product.Kind == CommerceRules.PAINTER_SUBSCRIPTION)
                    ?? throw new ApiException("ProductUnavailable");
                if (request.ExpectedPricePaidGems != product.PricePaidGems) throw new ApiException("ProductChanged", 409);
                var key = (accountId, CommerceRules.PAINTER_SUBSCRIPTION, Guid.Parse(request.OperationId)); var profile = _profiles[accountId];
                if (_ledger.TryGetValue(key, out var previous) && previous != product.Id) throw new ApiException("OperationConflict", 409);
                if (!_ledger.ContainsKey(key))
                {
                    if (profile.PaidGems < product.PricePaidGems) throw new ApiException("InsufficientPaidGems", 409);
                    DateTimeOffset? expiry = DateTimeOffset.TryParse(profile.SubscriptionExpiresAt, out var value) ? value : null;
                    profile.SubscriptionExpiresAt = CommerceRules.ExtendSubscription(expiry, Now, product.SubscriptionDays).ToString("O");
                    profile.PaidGems -= product.PricePaidGems; _ledger.Add(key, product.Id);
                }
                return Task.FromResult(Profile(accountId));
            }
        }
        public Task<ProfileData> SetSubscriberBadgeAsync(Guid accountId, bool showBadge, CancellationToken cancellationToken)
        {
            if (showBadge && !CommerceRules.IsPainterSubscriber(_profiles[accountId], Now)) throw new ApiException("SubscriptionRequired", 403);
            _profiles[accountId].ShowSubscriberBadge = showBadge; return Task.FromResult(Profile(accountId));
        }
        public Task FlagCommerceTransactionForReviewAsync(VerifiedCommerceReversal reversal, CancellationToken cancellationToken)
        {
            var order = _transactions[(reversal.Provider, reversal.AppScope, ServerRuntime.Hash(reversal.TransactionId))];
            _reviews.Add(order); _orders[order].Order.State = "ReviewRequired"; return Task.CompletedTask;
        }
    }
}
