using System.Net;
using System.Text;
using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;
using Google.Apis.Auth.OAuth2;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class Integration
{
    private static async Task VerifyGooglePlayCommerceAsync()
    {
        var handler = new GooglePlayTestHandler();
        using var http = new HttpClient(handler);
        var disabled = await GooglePlayPaymentProvider.CreateAsync(new ConfigurationBuilder().Build(), http);
        Check(!disabled.IsAvailable && handler.Requests == 0, "명시적으로 활성화하지 않은 Google Play 결제는 초기화하지 않아야 합니다.");
        try
        {
            await GooglePlayPaymentProvider.CreateAsync(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Commerce:GooglePlay:Enabled"] = "true", ["Commerce:GooglePlay:CredentialsFile"] = "" }).Build(), http);
            throw new Exception("서비스 계정이 없는 Google Play 공급자는 시작할 수 없어야 합니다.");
        }
        catch (InvalidOperationException) { }

        var repository = new CommerceTestRepository(DateTimeOffset.UtcNow);
        var account = Guid.NewGuid();
        repository.Add(account);
        var provider = new GooglePlayPaymentProvider("com.rascallab.liargame", http, GoogleCredential.FromAccessToken("test-access-token"));
        byte[] encryptionKey = RandomNumberGenerator.GetBytes(32);
        var protector = new CommerceTokenProtector(encryptionKey);
        var service = new CommerceService(repository, new[] { provider }, protector);
        var prepared = await service.PrepareAsync(account, new CommercePrepareRequest
        { ProductId = "gems-550", Provider = "GooglePlay", OperationId = Guid.NewGuid().ToString(), StoreProof = "" });
        Check(service.Catalogue().Providers.Single().Available && handler.Requests == 0,
            "인증된 게임 세션은 별도 StoreProof 없이 Google Play 주문을 준비할 수 있어야 합니다.");
        handler.Order = prepared.Order;
        foreach (string invalid in new[] { "canceled", "quantity", "refunded", "sku", "account", "order", "consumed", "multi-item", "rent", "preorder", "missing", "malformed" })
        {
            handler.Mode = invalid;
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "InvalidPayment");
        }
        handler.Mode = "pending";
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentPending");
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            handler.Status = status;
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentProviderUnavailable");
        }
        handler.Status = HttpStatusCode.NotFound;
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "InvalidPayment");
        handler.Status = HttpStatusCode.OK;
        handler.Mode = "transport-error";
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentProviderUnavailable");
        Check(repository.Profile(account).PaidGems == 0 && repository.TransactionCount == 0 && repository.LedgerCount == 0 && handler.Consumes == 0,
            "대기·취소·다른 계정/상품/주문·수량·소비·환불·API 장애는 지급과 소비 전에 차단해야 합니다.");
        int requests = handler.Requests;
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "bad\ntoken")), "InvalidPayment");
        Check(handler.Requests == requests, "제어 문자를 포함한 구매 토큰은 외부 API로 전달하지 않아야 합니다.");

        handler.Mode = "valid";
        handler.FailConsume = true;
        handler.BeforeConsume = () => Check(repository.Profile(account).PaidGems == 550 && repository.LedgerCount == 1,
            "Google Play 소비는 보석과 원장 저장이 확정된 뒤에만 수행해야 합니다.");
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentProviderUnavailable");
        Check(repository.Profile(account).PaidGems == 550 && repository.TransactionCount == 1 && handler.Consumes == 1 && repository.FinalizationCount == 1,
            "지급 이후 소비 실패는 완료 주문을 보존하여 안전하게 재시도해야 합니다.");
        handler.Mode = "order";
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "other-purchase-token")), "InvalidPayment");
        Check(handler.Consumes == 1, "완료 주문에 다른 토큰을 넣어도 미지급 구매를 소비하지 않아야 합니다.");
        handler.Mode = "valid";
        await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "other-purchase-token")), "InvalidPayment");
        Check(handler.Consumes == 1, "같은 계정·상품·주문 연결값으로 별도 구매한 토큰도 기존 거래 해시와 다르면 소비하지 않아야 합니다.");
        handler.FailConsume = false;
        var restarted = new CommerceService(repository, new[] { provider }, new CommerceTokenProtector(encryptionKey));
        var worker = new CommerceFinalizationWorker(repository, restarted, NullLogger<CommerceFinalizationWorker>.Instance);
        await worker.RunOnceAsync();
        Check(repository.FinalizationCount == 0 && handler.Consumed, "앱과 서버가 재시작되어도 영구 대기열의 암호화된 토큰으로 소비를 완료해야 합니다.");
        var completed = await service.ConfirmAsync(account, Confirm(prepared, "purchase-token"));
        await service.ConfirmAsync(account, Confirm(prepared, "purchase-token"));
        Check(completed.Order.State == "Completed" && completed.Profile.PaidGems == 550 && repository.LedgerCount == 1
            && repository.TransactionCount == 1 && handler.Consumes == 2 && handler.Consumed,
            "응답 유실·소비 재시도와 이미 소비된 완료 주문은 중복 지급·소비 없이 완료되어야 합니다.");
        var stored = await repository.CommerceOrderAsync(account, Guid.Parse(prepared.Order.OrderId), default);
        string protectedProof = protector.Protect(stored, "purchase-token");
        Check(protector.Unprotect(stored, protectedProof) == "purchase-token" && !protectedProof.Contains("purchase-token", StringComparison.Ordinal),
            "대기열 토큰은 별도 키로 암호화해야 합니다.");
        foreach (var wrong in new[] { stored with { AppScope = "other-app" }, stored with { AccountId = Guid.NewGuid() } })
        {
            try { protector.Unprotect(wrong, protectedProof); throw new Exception("다른 주문에 암호문을 재사용할 수 없어야 합니다."); }
            catch (CryptographicException) { }
        }
        try { new CommerceTokenProtector(RandomNumberGenerator.GetBytes(32)).Unprotect(stored, protectedProof); throw new Exception("잘못된 키를 거부해야 합니다."); }
        catch (CryptographicException) { }
        await VerifyGooglePlayFinalizationReversalsAsync();
        Report("Google Play 서버 API 인증 경계·상품/계정/주문/상태/수량/환불 검증·지급 후 소비·완료 재시도 검증");
    }

    private static async Task VerifyGooglePlayFinalizationReversalsAsync()
    {
        foreach (bool cancelled in new[] { true, false })
        {
            var repository = new CommerceTestRepository(DateTimeOffset.UtcNow);
            var account = Guid.NewGuid(); repository.Add(account);
            var handler = new GooglePlayTestHandler { FailConsume = true };
            using var http = new HttpClient(handler);
            var provider = new GooglePlayPaymentProvider("com.rascallab.liargame", http, GoogleCredential.FromAccessToken("test-access-token"));
            var service = new CommerceService(repository, new[] { provider }, new CommerceTokenProtector(RandomNumberGenerator.GetBytes(32)));
            var prepared = await service.PrepareAsync(account, new CommercePrepareRequest
            { Provider = "GooglePlay", ProductId = "gems-550", OperationId = Guid.NewGuid().ToString() });
            handler.Order = prepared.Order;
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentProviderUnavailable");
            handler.Cancelled = cancelled;
            handler.Refunded = !cancelled;
            foreach (string mode in new[] { "account", "order", "sku", "quantity", "multi-item", "rent", "preorder", "malformed", "missing", "bad-consumption" })
            {
                handler.Mode = mode;
                await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "InvalidPayment");
            }
            handler.Mode = "valid";
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "other-purchase-token")), "InvalidPayment");
            handler.Status = HttpStatusCode.ServiceUnavailable;
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentProviderUnavailable");
            Check(repository.ReviewCount == 0 && repository.FinalizationCount == 1 && prepared.Order.State == "Completed",
                "다른 토큰·계정/주문/상품/수량 불일치·손상 응답·API 장애는 완료 결제를 환불 검토로 전환하지 않아야 합니다.");
            handler.Status = HttpStatusCode.OK;
            var worker = new CommerceFinalizationWorker(repository, service, NullLogger<CommerceFinalizationWorker>.Instance);
            await worker.RunOnceAsync();
            int requests = handler.Requests;
            await worker.RunOnceAsync();
            Check(repository.ReviewCount == 1 && repository.FinalizationCount == 0 && prepared.Order.State == "ReviewRequired"
                && repository.Profile(account).PaidGems == 550 && repository.LedgerCount == 1 && handler.Consumes == 1 && handler.Requests == requests,
                "지급 후 권위 확인된 취소·전액 환불은 기존 재화·원장을 유지한 채 검토 상태로 전환하고 소비 재시도를 종료해야 합니다.");
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "purchase-token")), "PaymentReviewRequired");
        }
    }

    private sealed class GooglePlayTestHandler : HttpMessageHandler
    {
        public CommerceOrderData Order = new();
        public string Mode = "valid";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Requests;
        public int Consumes;
        public bool Consumed;
        public bool FailConsume;
        public bool Cancelled;
        public bool Refunded;
        public string PurchaseToken = "purchase-token";
        public Action? BeforeConsume;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Check(request.RequestUri?.Host == "androidpublisher.googleapis.com"
                && request.RequestUri.AbsolutePath.StartsWith("/androidpublisher/v3/applications/com.rascallab.liargame/purchases/", StringComparison.Ordinal)
                && request.Headers.Authorization?.ToString() == "Bearer test-access-token", "검증 요청은 설정된 앱의 공식 Google API와 서버 인증을 사용해야 합니다.");
            if (Mode == "transport-error") throw new HttpRequestException("테스트 네트워크 장애");
            if (request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri!.AbsolutePath == "/androidpublisher/v3/applications/com.rascallab.liargame/purchases/products/"
                    + Order.StoreProductId + "/tokens/" + Uri.EscapeDataString(PurchaseToken) + ":consume" && request.Content == null,
                    "소비는 검증된 상품·토큰의 공식 consume API로 빈 본문을 전송해야 합니다.");
                BeforeConsume?.Invoke();
                Consumes++;
                if (FailConsume) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                Consumed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/purchases/productsv2/tokens/", StringComparison.Ordinal),
                "구매 검증은 상품을 토큰에서 조회하는 v2 API를 사용해야 합니다.");
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            if (Mode is "missing" or "malformed") return Task.FromResult(JsonResponse(Mode == "missing" ? "{}" : "{"));
            var details = new Dictionary<string, object>
            {
                ["quantity"] = Mode == "quantity" ? 2 : 1, ["refundableQuantity"] = Mode == "refunded" || Refunded ? 0 : 1,
                ["consumptionState"] = Mode == "bad-consumption" ? "UNKNOWN" : Consumed || Mode == "consumed" ? "CONSUMPTION_STATE_CONSUMED" : "CONSUMPTION_STATE_YET_TO_BE_CONSUMED"
            };
            if (Mode is "rent" or "preorder") details[Mode == "rent" ? "rentOfferDetails" : "preorderOfferDetails"] = new { };
            var item = new { productId = Mode == "sku" ? "other-product" : Order.StoreProductId, productOfferDetails = details };
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
            {
                purchaseStateContext = new { purchaseState = Mode == "pending" ? "PENDING" : Mode == "canceled" || Cancelled ? "CANCELLED" : "PURCHASED" },
                obfuscatedExternalAccountId = Mode == "account" ? "other-account" : Order.AccountBinding,
                obfuscatedExternalProfileId = Mode == "order" ? "other-order" : Order.OrderId,
                productLineItem = Mode == "multi-item" ? new[] { item, item } : new[] { item }
            })));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
