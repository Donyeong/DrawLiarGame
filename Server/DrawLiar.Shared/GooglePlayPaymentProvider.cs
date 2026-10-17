using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;

namespace DrawLiar.Server;

public sealed class GooglePlayPaymentProvider : ICommercePaymentProvider, ICommercePaymentFinalizer
{
    private const string API_ROOT = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";
    private const string ANDROID_PUBLISHER_SCOPE = "https://www.googleapis.com/auth/androidpublisher";
    private readonly HttpClient _http;
    private readonly ITokenAccess _credential;
    public string Id => "GooglePlay";
    public string AppScope { get; }
    public bool IsAvailable => true;

    public GooglePlayPaymentProvider(string packageName, HttpClient http, ITokenAccess credential)
    {
        if (string.IsNullOrWhiteSpace(packageName) || packageName.Length > 256
            || !packageName.Contains('.') || packageName.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('.' or '_')))
            throw new InvalidOperationException("Commerce:GooglePlay:PackageName을 설정해야 합니다.");
        AppScope = packageName;
        _http = http;
        _credential = credential;
    }

    public static async Task<ICommercePaymentProvider> CreateAsync(IConfiguration configuration, HttpClient http,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("Commerce:GooglePlay:Enabled"))
            return new UnconfiguredCommercePaymentProvider("GooglePlay");
        string path = configuration["Commerce:GooglePlay:CredentialsFile"] ?? "";
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("Commerce:GooglePlay:CredentialsFile에 서버의 서비스 계정 파일 절대 경로가 필요합니다.");
        ServiceAccountCredential serviceAccount;
        try { serviceAccount = await CredentialFactory.FromFileAsync<ServiceAccountCredential>(path, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Google Play 결제용 서비스 계정 파일을 읽을 수 없습니다."); }
        var credential = GoogleCredential.FromServiceAccountCredential(serviceAccount).CreateScoped(ANDROID_PUBLISHER_SCOPE);
        return new GooglePlayPaymentProvider(configuration["Commerce:GooglePlay:PackageName"] ?? "", http, credential);
    }

    public Task PrepareAsync(CommerceStoredOrder order, string storeProof, CancellationToken cancellationToken)
    {
        ValidateOrder(order);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<VerifiedCommercePayment> VerifyAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken)
    {
        await VerifyPurchaseAsync(order, proof, false, cancellationToken);
        return new VerifiedCommercePayment(order.Order.OrderId, Id, AppScope, order.Order.StoreProductId,
            order.Order.AccountBinding, TransactionId(proof), 1, true);
    }

    public string TransactionId(string proof) => "purchase-token:" + ServerRuntime.Hash(proof);

    public async Task FinalizeAsync(CommerceStoredOrder order, string proof, CancellationToken cancellationToken)
    {
        bool consumed = await VerifyPurchaseAsync(order, proof, true, cancellationToken);
        if (consumed) return;
        string path = "purchases/products/" + Uri.EscapeDataString(order.Order.StoreProductId)
            + "/tokens/" + Uri.EscapeDataString(proof) + ":consume";
        using var response = await SendAsync(HttpMethod.Post, path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 응답 유실·동시 소비는 Google의 최종 상태로 확인한다.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict
                && await VerifyPurchaseAsync(order, proof, true, cancellationToken)) return;
            throw new ApiException("PaymentProviderUnavailable", 503);
        }
    }

    private void ValidateOrder(CommerceStoredOrder order)
    {
        if (order.Order.Provider != Id || order.AppScope != AppScope || order.Product.Kind != CommerceRules.PAID_GEMS
            || string.IsNullOrWhiteSpace(order.Order.StoreProductId) || order.Product.StoreProductId != order.Order.StoreProductId
            || string.IsNullOrWhiteSpace(order.Order.AccountBinding)) throw new ApiException("InvalidPayment", 403);
    }

    private async Task<bool> VerifyPurchaseAsync(CommerceStoredOrder order, string proof, bool allowConsumed,
        CancellationToken cancellationToken)
    {
        ValidateOrder(order);
        if (string.IsNullOrWhiteSpace(proof) || proof.Length > 8192 || proof.Any(char.IsControl))
            throw new ApiException("InvalidPayment", 403);
        using var response = await SendAsync(HttpMethod.Get, "purchases/productsv2/tokens/" + Uri.EscapeDataString(proof), cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new ApiException("InvalidPayment", 403);
        if (!response.IsSuccessStatusCode) throw new ApiException("PaymentProviderUnavailable", 503);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var purchase = document.RootElement;
            string state = purchase.GetProperty("purchaseStateContext").GetProperty("purchaseState").GetString() ?? "";
            var items = purchase.GetProperty("productLineItem");
            if (items.GetArrayLength() != 1
                || purchase.GetProperty("obfuscatedExternalAccountId").GetString() != order.Order.AccountBinding
                || purchase.GetProperty("obfuscatedExternalProfileId").GetString() != order.Order.OrderId)
                throw new ApiException("InvalidPayment", 403);
            var item = items[0];
            var details = item.GetProperty("productOfferDetails");
            if (item.GetProperty("productId").GetString() != order.Order.StoreProductId
                || details.GetProperty("quantity").GetInt32() != 1
                || details.TryGetProperty("rentOfferDetails", out _) || details.TryGetProperty("preorderOfferDetails", out _))
                throw new ApiException("InvalidPayment", 403);
            string consumption = details.GetProperty("consumptionState").GetString() ?? "";
            bool consumed = consumption == "CONSUMPTION_STATE_CONSUMED";
            int refundableQuantity = details.GetProperty("refundableQuantity").GetInt32();
            if (refundableQuantity is < 0 or > 1
                || consumption != "CONSUMPTION_STATE_YET_TO_BE_CONSUMED" && !consumed)
                throw new ApiException("InvalidPayment", 403);
            if (allowConsumed && (state == "CANCELLED" || state == "PURCHASED" && refundableQuantity == 0))
                throw new VerifiedCommerceReversalException(new VerifiedCommerceReversal(Id, AppScope, TransactionId(proof),
                    state == "CANCELLED" ? "Revoked" : "Refunded"));
            if (state == "PENDING") throw new ApiException("PaymentPending", 409);
            if (state != "PURCHASED" || refundableQuantity != 1) throw new ApiException("InvalidPayment", 403);
            if (consumption != "CONSUMPTION_STATE_YET_TO_BE_CONSUMED" && !(allowConsumed && consumed))
                throw new ApiException("InvalidPayment", 403);
            return consumed;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ApiException("InvalidPayment", 403);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            string token = await _credential.GetAccessTokenForRequestAsync(null, cancellationToken);
            if (string.IsNullOrWhiteSpace(token)) throw new ApiException("PaymentProviderUnavailable", 503);
            using var request = new HttpRequestMessage(method, API_ROOT + Uri.EscapeDataString(AppScope) + "/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ApiException) { throw; }
        catch { throw new ApiException("PaymentProviderUnavailable", 503); }
    }
}
