namespace DrawLiar.Server;

public sealed record CommerceStoredOrder(Guid AccountId, Guid OperationId, CommerceOrderData Order, CommerceProduct Product, string AppScope);

public sealed record VerifiedCommercePayment(string OrderId, string Provider, string AppScope, string StoreProductId,
    string AccountBinding, string TransactionId, int Quantity, bool Purchased);

public sealed record VerifiedCommerceReversal(string Provider, string AppScope, string TransactionId, string Reason);

public sealed class VerifiedCommerceReversalException(VerifiedCommerceReversal reversal) : Exception("PaymentReversed")
{
    public VerifiedCommerceReversal Reversal { get; } = reversal;
}

public interface ICommerceReversalProvider
{
    Task<VerifiedCommerceReversal> VerifyReversalAsync(string proof, CancellationToken cancellationToken);
}

public interface ICommercePaymentProvider
{
    string Id { get; }
    string AppScope { get; }
    bool IsAvailable { get; }
    Task PrepareAsync(CommerceStoredOrder order, string storeProof, CancellationToken cancellationToken);
    Task<VerifiedCommercePayment> VerifyAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken);
}

public interface ICommercePaymentFinalizer
{
    string TransactionId(string proof);
    Task FinalizeAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken);
}

public interface ICommerceRepository
{
    Task<CommerceStoredOrder> PrepareCommerceOrderAsync(CommerceStoredOrder order, CancellationToken cancellationToken);
    Task<CommerceStoredOrder> CommerceOrderAsync(Guid accountId, Guid orderId, CancellationToken cancellationToken);
    Task<CommerceOrdersResponse> CommerceOrdersAsync(Guid accountId, CancellationToken cancellationToken);
    Task<CommercePaymentResponse> CompleteCommerceOrderAsync(CommerceStoredOrder order, string appScope,
        string transactionId, CancellationToken cancellationToken);
    Task<ProfileData> PurchaseCommerceAsync(Guid accountId, CommercePurchaseRequest request, CancellationToken cancellationToken);
    Task<ProfileData> SetSubscriberBadgeAsync(Guid accountId, bool showBadge, CancellationToken cancellationToken);
    Task<ProfileData> CommerceProfileAsync(Guid accountId, CancellationToken cancellationToken);
    Task FlagCommerceTransactionForReviewAsync(VerifiedCommerceReversal reversal, CancellationToken cancellationToken);
}

public sealed class UnconfiguredCommercePaymentProvider(string id) : ICommercePaymentProvider
{
    public string Id { get; } = id;
    public string AppScope => "";
    public bool IsAvailable => false;
    public Task PrepareAsync(CommerceStoredOrder order, string storeProof, CancellationToken cancellationToken) =>
        Task.FromException(new ApiException("PaymentProviderUnavailable", 503));
    public Task<VerifiedCommercePayment> VerifyAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken) =>
        Task.FromException<VerifiedCommercePayment>(new ApiException("PaymentProviderUnavailable", 503));
}

public sealed class CommerceService
{
    private readonly ICommerceRepository _repository;
    private readonly IReadOnlyDictionary<string, ICommercePaymentProvider> _providers;
    private readonly CommerceTokenProtector? _tokenProtector;
    private readonly ICommerceFinalizationRepository? _finalizations;
    public bool HasDurableFinalization => _tokenProtector != null && _finalizations != null;

    public CommerceService(ICommerceRepository repository, IEnumerable<ICommercePaymentProvider>? providers = null,
        CommerceTokenProtector? tokenProtector = null)
    {
        _repository = repository;
        var registered = (providers ?? new ICommercePaymentProvider[]
        {
            new UnconfiguredCommercePaymentProvider("Steam"), new UnconfiguredCommercePaymentProvider("GooglePlay")
        }).ToArray();
        if (registered.Any(provider => provider.Id is not ("Steam" or "GooglePlay")))
            throw new ArgumentException("등록되지 않은 결제 플랫폼입니다.", nameof(providers));
        _providers = registered.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        _tokenProtector = tokenProtector;
        _finalizations = repository as ICommerceFinalizationRepository;
        if (registered.Any(provider => provider.IsAvailable && provider is ICommercePaymentFinalizer) && !HasDurableFinalization)
            throw new InvalidOperationException("결제 소비 공급자에는 영구 대기열과 별도 토큰 암호화 키가 필요합니다.");
    }

    public CommerceCatalogue Catalogue() => new()
    {
        Products = CommerceRules.CreateProducts(), ServerTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Providers = _providers.Values.Select(provider => new CommerceProviderData
        { Id = provider.Id, Available = provider.IsAvailable && !string.IsNullOrWhiteSpace(provider.AppScope) }).ToArray()
    };

    public async Task<CommercePaymentResponse> PrepareAsync(Guid accountId, CommercePrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || !Guid.TryParse(request.OperationId, out Guid operationId) || operationId == Guid.Empty
            || request.StoreProof == null || request.StoreProof.Length > 8192) throw new ApiException("InvalidPayment");
        var provider = Provider(request.Provider);
        var product = CommerceRules.CreateProducts().FirstOrDefault(product => product.Id == request.ProductId
            && product.Kind == CommerceRules.PAID_GEMS) ?? throw new ApiException("ProductUnavailable");
        var candidate = new CommerceStoredOrder(accountId, operationId, new CommerceOrderData
        {
            OrderId = Guid.NewGuid().ToString(), ProductId = product.Id, Provider = provider.Id,
            StoreProductId = product.StoreProductId, AccountBinding = ServerRuntime.Hash("commerce:" + accountId),
            State = "Prepared", CreatedAt = ServerRuntime.Timestamp(DateTimeOffset.UtcNow)
        }, product, provider.AppScope);
        var order = await _repository.PrepareCommerceOrderAsync(candidate, cancellationToken);
        if (order.Order.State == "ReviewRequired") throw new ApiException("PaymentReviewRequired", 409);
        if (order.Order.State == "Prepared") await provider.PrepareAsync(order, request.StoreProof, cancellationToken);
        return new CommercePaymentResponse { Order = order.Order, Product = order.Product,
            Profile = await _repository.CommerceProfileAsync(accountId, cancellationToken) };
    }

    public async Task<CommercePaymentResponse> ConfirmAsync(Guid accountId, CommerceConfirmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || !Guid.TryParse(request.OrderId, out Guid orderId) || orderId == Guid.Empty
            || request.Proof == null || request.Proof.Length > 8192) throw new ApiException("InvalidPayment");
        var order = await _repository.CommerceOrderAsync(accountId, orderId, cancellationToken);
        if (order.Order.State == "Completed")
        {
            if (_providers.TryGetValue(order.Order.Provider, out var completedProvider)
                && completedProvider is ICommercePaymentFinalizer completedFinalizer)
            {
                Provider(order.Order.Provider);
                await FinalizeAsync(order, request.Proof, completedFinalizer, cancellationToken);
            }
            return new CommercePaymentResponse { Order = order.Order, Product = order.Product,
                Profile = await _repository.CommerceProfileAsync(accountId, cancellationToken) };
        }
        if (order.Order.State != "Prepared") throw new ApiException("PaymentReviewRequired", 409);
        var provider = Provider(order.Order.Provider);
        var payment = await provider.VerifyAsync(order, request.Proof, cancellationToken);
        if (!payment.Purchased || payment.Quantity != 1 || payment.OrderId != order.Order.OrderId
            || payment.Provider != provider.Id || payment.AppScope != provider.AppScope || payment.AppScope != order.AppScope
            || payment.StoreProductId != order.Order.StoreProductId || payment.AccountBinding != order.Order.AccountBinding
            || string.IsNullOrWhiteSpace(payment.TransactionId) || payment.TransactionId.Length > 256
            || payment.TransactionId.Any(char.IsControl)) throw new ApiException("InvalidPayment", 403);
        var response = provider is ICommercePaymentFinalizer
            ? await _finalizations!.CompleteWithFinalizationAsync(order, provider.AppScope, payment.TransactionId,
                _tokenProtector!.Protect(order, request.Proof), cancellationToken)
            : await _repository.CompleteCommerceOrderAsync(order, provider.AppScope, payment.TransactionId, cancellationToken);
        if (provider is ICommercePaymentFinalizer finalizer)
            await FinalizeAsync(order, request.Proof, finalizer, cancellationToken);
        return response;
    }

    public async Task RetryFinalizationAsync(PendingCommerceFinalization pending, CancellationToken cancellationToken = default)
    {
        if (!HasDurableFinalization || pending.Order.Order.State != "Completed") throw new ApiException("PaymentReviewRequired", 409);
        if (Provider(pending.Order.Order.Provider) is not ICommercePaymentFinalizer finalizer)
            throw new ApiException("PaymentProviderUnavailable", 503);
        string proof = _tokenProtector!.Unprotect(pending.Order, pending.ProtectedProof);
        await FinalizeAsync(pending.Order, proof, finalizer, cancellationToken);
    }

    private async Task FinalizeAsync(CommerceStoredOrder order, string proof, ICommercePaymentFinalizer finalizer,
        CancellationToken cancellationToken)
    {
        await _finalizations!.RequireCommerceTransactionAsync(order, finalizer.TransactionId(proof), cancellationToken);
        try { await finalizer.FinalizeAsync(order, proof, cancellationToken); }
        catch (VerifiedCommerceReversalException error)
        {
            var reversal = error.Reversal;
            if (reversal.Provider != order.Order.Provider || reversal.AppScope != order.AppScope
                || reversal.TransactionId != finalizer.TransactionId(proof) || reversal.Reason is not ("Refunded" or "Revoked"))
                throw new ApiException("InvalidPayment", 403);
            await _repository.FlagCommerceTransactionForReviewAsync(reversal, cancellationToken);
            throw new ApiException("PaymentReviewRequired", 409);
        }
        await _finalizations.CompleteCommerceFinalizationAsync(Guid.Parse(order.Order.OrderId), cancellationToken);
    }

    public async Task ReviewReversalAsync(string providerId, string proof, CancellationToken cancellationToken = default)
    {
        var provider = Provider(providerId);
        if (provider is not ICommerceReversalProvider reversalProvider || string.IsNullOrWhiteSpace(proof) || proof.Length > 8192)
            throw new ApiException("InvalidPayment");
        var reversal = await reversalProvider.VerifyReversalAsync(proof, cancellationToken);
        if (reversal.Provider != provider.Id || reversal.AppScope != provider.AppScope
            || string.IsNullOrWhiteSpace(reversal.TransactionId) || reversal.TransactionId.Length > 256
            || reversal.TransactionId.Any(char.IsControl) || reversal.Reason is not ("Refunded" or "Revoked"))
            throw new ApiException("InvalidPayment", 403);
        await _repository.FlagCommerceTransactionForReviewAsync(reversal, cancellationToken);
    }

    private ICommercePaymentProvider Provider(string id)
    {
        if (id == null || !_providers.TryGetValue(id, out var provider) || !provider.IsAvailable
            || string.IsNullOrWhiteSpace(provider.AppScope)) throw new ApiException("PaymentProviderUnavailable", 503);
        return provider;
    }
}
