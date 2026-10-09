namespace DrawLiar.Server;

public sealed record CommerceStoredOrder(Guid AccountId, Guid OperationId, CommerceOrderData Order, CommerceProduct Product, string AppScope);

public sealed record VerifiedCommercePayment(string OrderId, string Provider, string AppScope, string StoreProductId,
    string AccountBinding, string TransactionId, int Quantity, bool Purchased);

public sealed record VerifiedCommerceReversal(string Provider, string AppScope, string TransactionId, string Reason);

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

    public CommerceService(ICommerceRepository repository, IEnumerable<ICommercePaymentProvider>? providers = null)
    {
        _repository = repository;
        var registered = (providers ?? new ICommercePaymentProvider[]
        {
            new UnconfiguredCommercePaymentProvider("Steam"), new UnconfiguredCommercePaymentProvider("GooglePlay")
        }).ToArray();
        if (registered.Any(provider => provider.Id is not ("Steam" or "GooglePlay")))
            throw new ArgumentException("등록되지 않은 결제 플랫폼입니다.", nameof(providers));
        _providers = registered.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
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
        if (order.Order.State == "Completed") return new CommercePaymentResponse { Order = order.Order, Product = order.Product,
            Profile = await _repository.CommerceProfileAsync(accountId, cancellationToken) };
        if (order.Order.State != "Prepared") throw new ApiException("PaymentReviewRequired", 409);
        var provider = Provider(order.Order.Provider);
        var payment = await provider.VerifyAsync(order, request.Proof, cancellationToken);
        if (!payment.Purchased || payment.Quantity != 1 || payment.OrderId != order.Order.OrderId
            || payment.Provider != provider.Id || payment.AppScope != provider.AppScope || payment.AppScope != order.AppScope
            || payment.StoreProductId != order.Order.StoreProductId || payment.AccountBinding != order.Order.AccountBinding
            || string.IsNullOrWhiteSpace(payment.TransactionId) || payment.TransactionId.Length > 256
            || payment.TransactionId.Any(char.IsControl)) throw new ApiException("InvalidPayment", 403);
        return await _repository.CompleteCommerceOrderAsync(order, provider.AppScope, payment.TransactionId, cancellationToken);
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
