using System;

namespace DrawLiar
{
    [Serializable] public sealed class CommerceCatalogue
    {
        public CommerceProduct[] Products = Array.Empty<CommerceProduct>();
        public CommerceProviderData[] Providers = Array.Empty<CommerceProviderData>();
        public long ServerTimeUnixSeconds;
    }
    [Serializable] public sealed class CommerceProduct
    {
        public string Id = "";
        public string Name = "";
        public string Kind = "";
        public int PaidGems;
        public int PricePaidGems;
        public int SubscriptionDays;
        public string StoreProductId = "";
    }
    [Serializable] public sealed class CommerceProviderData { public string Id = ""; public bool Available; }
    [Serializable] public sealed class CommercePurchaseRequest { public string ProductId = ""; public string OperationId = ""; public int ExpectedPricePaidGems; }
    [Serializable] public sealed class CommercePrepareRequest
    {
        public string ProductId = "";
        public string Provider = "";
        public string OperationId = "";
        public string StoreProof = "";
    }
    [Serializable] public sealed class CommerceConfirmRequest { public string OrderId = ""; public string Proof = ""; }
    [Serializable] public sealed class CommerceOrderData
    {
        public string OrderId = "";
        public string ProductId = "";
        public string Provider = "";
        public string StoreProductId = "";
        public string AccountBinding = "";
        public string State = "";
        public string CreatedAt = "";
        public string CompletedAt = "";
    }
    [Serializable] public sealed class CommercePaymentResponse
    {
        public CommerceOrderData Order = new CommerceOrderData();
        public CommerceProduct Product = new CommerceProduct();
        public ProfileData Profile = new ProfileData();
    }
    [Serializable] public sealed class CommerceOrdersResponse
    {
        public CommerceOrderData[] Orders = Array.Empty<CommerceOrderData>();
        public long ServerTimeUnixSeconds;
    }
    [Serializable] public sealed class SubscriberBadgeRequest { public bool ShowBadge; }
}
