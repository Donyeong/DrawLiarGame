package com.rascallab.liargame.billing;

import android.app.Activity;
import android.os.Handler;
import android.os.Looper;
import com.android.billingclient.api.AccountIdentifiers;
import com.android.billingclient.api.BillingClient;
import com.android.billingclient.api.BillingClientStateListener;
import com.android.billingclient.api.BillingFlowParams;
import com.android.billingclient.api.BillingResult;
import com.android.billingclient.api.PendingPurchasesParams;
import com.android.billingclient.api.ProductDetails;
import com.android.billingclient.api.Purchase;
import com.android.billingclient.api.QueryProductDetailsParams;
import com.android.billingclient.api.QueryPurchasesParams;
import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

public final class DrawBilling {
    private final WeakReference<Activity> _activity;
    private final Handler _main = new Handler(Looper.getMainLooper());
    private final Set<String> _requests = ConcurrentHashMap.newKeySet();
    private final ConcurrentHashMap<String, String> _results = new ConcurrentHashMap<>();
    private final AtomicInteger _revision = new AtomicInteger();
    private final List<ConnectionAction> _connectionActions = new ArrayList<>();
    private BillingClient _client;
    private boolean _connecting;
    private volatile boolean _closed;
    private PurchaseRequest _purchase;

    private static final class ConnectionAction {
        final String Request;
        final Runnable Action;
        ConnectionAction(String request, Runnable action) { Request = request; Action = action; }
    }

    private static final class PurchaseRequest {
        final String Request;
        final String OrderId;
        final String AccountBinding;
        final String ProductId;
        boolean Launched;
        PurchaseRequest(String request, String orderId, String accountBinding, String productId) {
            Request = request; OrderId = orderId; AccountBinding = accountBinding; ProductId = productId;
        }
    }

    public DrawBilling(Activity activity) { _activity = new WeakReference<>(activity); }
    public int Revision() { return _revision.get(); }

    public synchronized String TakeResult(String request) {
        String result = _results.remove(request);
        return result == null ? "" : result;
    }

    public synchronized void Cancel(String request) {
        _requests.remove(request);
        _results.remove(request);
        _main.post(() -> {
            _connectionActions.removeIf(action -> action.Request.equals(request));
            if (_purchase != null && _purchase.Request.equals(request) && !_purchase.Launched) _purchase = null;
        });
    }

    public synchronized void Reset() {
        _requests.clear();
        _results.clear();
        _main.post(() -> {
            _connectionActions.removeIf(action -> !_requests.contains(action.Request));
            if (_purchase != null && !_purchase.Launched && !_requests.contains(_purchase.Request)) _purchase = null;
        });
    }

    public void Close() {
        _closed = true;
        Reset();
        _main.post(() -> {
            _connectionActions.clear();
            _purchase = null;
            if (_client != null) { _client.endConnection(); _client = null; }
        });
    }

    public void QueryPrices(String request, String queryJson) {
        Enqueue(request, () -> {
            try {
                JSONArray ids = new JSONObject(queryJson).getJSONArray("ProductIds");
                if (ids.length() == 0 || ids.length() > 20) { Result(request, 5, null, null); return; }
                List<QueryProductDetailsParams.Product> products = new ArrayList<>();
                for (int i = 0; i < ids.length(); i++)
                    products.add(Product(ids.getString(i)));
                WithClient(request, () -> QueryDetails(request, products, details -> {
                    JSONArray prices = new JSONArray();
                    for (ProductDetails product : details) {
                        ProductDetails.OneTimePurchaseOfferDetails offer = SelectOffer(product);
                        if (offer == null) continue;
                        JSONObject price = new JSONObject();
                        Put(price, "StoreProductId", product.getProductId());
                        Put(price, "Price", offer.getFormattedPrice());
                        prices.put(price);
                    }
                    Result(request, 0, prices, null);
                }));
            } catch (Exception ignored) { Result(request, 5, null, null); }
        });
    }

    public void Purchase(String request, String orderId, String accountBinding, String productId) {
        Enqueue(request, () -> WithClient(request, () -> {
            if (_purchase != null || orderId.isEmpty() || orderId.length() > 64 || accountBinding.isEmpty()
                    || accountBinding.length() > 64 || productId.isEmpty()) { Result(request, 5, null, null); return; }
            PurchaseRequest purchase = new PurchaseRequest(request, orderId, accountBinding, productId);
            _purchase = purchase;
            QueryDetails(request, Collections.singletonList(Product(productId)), details -> {
                if (_purchase != purchase) return;
                if (!_requests.contains(request)) { _purchase = null; return; }
                ProductDetails selected = null;
                for (ProductDetails detail : details)
                    if (productId.equals(detail.getProductId())) { selected = detail; break; }
                ProductDetails.OneTimePurchaseOfferDetails offer = selected == null ? null : SelectOffer(selected);
                Activity activity = _activity.get();
                if (offer == null || activity == null || activity.isFinishing() || activity.isDestroyed()) {
                    FinishPurchase(4, null); return;
                }
                try {
                    BillingFlowParams.ProductDetailsParams.Builder product = BillingFlowParams.ProductDetailsParams.newBuilder()
                            .setProductDetails(selected);
                    if (offer.getOfferToken() != null && !offer.getOfferToken().isEmpty()) product.setOfferToken(offer.getOfferToken());
                    BillingFlowParams flow = BillingFlowParams.newBuilder()
                            .setProductDetailsParamsList(Collections.singletonList(product.build()))
                            .setObfuscatedAccountId(accountBinding).setObfuscatedProfileId(orderId).build();
                    purchase.Launched = true;
                    BillingResult result = _client.launchBillingFlow(activity, flow);
                    if (result.getResponseCode() != BillingClient.BillingResponseCode.OK) {
                        _revision.incrementAndGet();
                        FinishPurchase(result.getResponseCode(), null);
                    }
                } catch (Exception ignored) { FinishPurchase(6, null); }
            });
        }));
    }

    public void FetchPurchases(String request) {
        Enqueue(request, () -> WithClient(request, () -> {
            try {
                _client.queryPurchasesAsync(QueryPurchasesParams.newBuilder().setProductType(BillingClient.ProductType.INAPP).build(),
                        (result, purchases) -> _main.post(() -> Result(request, result.getResponseCode(), null, SerializePurchases(purchases))));
            } catch (Exception ignored) { Result(request, 6, null, null); }
        }));
    }

    private interface DetailsAction { void Run(List<ProductDetails> products); }

    private void QueryDetails(String request, List<QueryProductDetailsParams.Product> products, DetailsAction action) {
        try {
            _client.queryProductDetailsAsync(QueryProductDetailsParams.newBuilder().setProductList(products).build(),
                    (result, details) -> _main.post(() -> {
                        if (!_requests.contains(request)) {
                            if (_purchase != null && _purchase.Request.equals(request)) _purchase = null;
                            return;
                        }
                        if (result.getResponseCode() != BillingClient.BillingResponseCode.OK) {
                            if (_purchase != null && _purchase.Request.equals(request)) FinishPurchase(result.getResponseCode(), null);
                            else Result(request, result.getResponseCode(), null, null);
                            return;
                        }
                        try { action.Run(details.getProductDetailsList()); }
                        catch (Exception ignored) {
                            if (_purchase != null && _purchase.Request.equals(request)) FinishPurchase(6, null);
                            else Result(request, 6, null, null);
                        }
                    }));
        } catch (Exception ignored) {
            if (_purchase != null && _purchase.Request.equals(request)) FinishPurchase(6, null);
            else Result(request, 6, null, null);
        }
    }

    private static QueryProductDetailsParams.Product Product(String id) {
        return QueryProductDetailsParams.Product.newBuilder().setProductId(id).setProductType(BillingClient.ProductType.INAPP).build();
    }

    private static ProductDetails.OneTimePurchaseOfferDetails SelectOffer(ProductDetails product) {
        List<ProductDetails.OneTimePurchaseOfferDetails> offers = product.getOneTimePurchaseOfferDetailsList();
        if (offers == null || offers.isEmpty()) {
            ProductDetails.OneTimePurchaseOfferDetails legacy = product.getOneTimePurchaseOfferDetails();
            return IsStandardOffer(legacy) ? legacy : null;
        }
        ProductDetails.OneTimePurchaseOfferDetails selected = null;
        for (ProductDetails.OneTimePurchaseOfferDetails offer : offers) {
            if (!IsStandardOffer(offer)) continue;
            if (selected != null) return null;
            selected = offer;
        }
        return selected;
    }

    private static boolean IsStandardOffer(ProductDetails.OneTimePurchaseOfferDetails offer) {
        return offer != null && offer.getRentalDetails() == null && offer.getPreorderDetails() == null
                && (offer.getOfferId() == null || offer.getOfferId().isEmpty());
    }

    private void Enqueue(String request, Runnable action) {
        if (_closed || request == null || request.isEmpty()) return;
        _requests.add(request);
        _main.post(() -> {
            if (!_closed && _requests.contains(request)) {
                try { action.run(); }
                catch (Exception ignored) { Result(request, 6, null, null); }
            }
        });
    }

    private void WithClient(String request, Runnable action) {
        Activity activity = _activity.get();
        if (_closed || activity == null) { Result(request, 3, null, null); return; }
        if (_client == null) {
            _client = BillingClient.newBuilder(activity.getApplicationContext())
                    .enablePendingPurchases(PendingPurchasesParams.newBuilder().enableOneTimeProducts().build())
                    .enableAutoServiceReconnection()
                    .setListener((result, purchases) -> _main.post(() -> PurchasesUpdated(result, purchases)))
                    .build();
        }
        if (_client.isReady()) { action.run(); return; }
        _connectionActions.add(new ConnectionAction(request, action));
        if (_connecting) return;
        _connecting = true;
        try { _client.startConnection(new BillingClientStateListener() {
            @Override public void onBillingSetupFinished(BillingResult result) {
                _main.post(() -> {
                    _connecting = false;
                    List<ConnectionAction> waiting = new ArrayList<>(_connectionActions);
                    _connectionActions.clear();
                    if (_closed) return;
                    if (result.getResponseCode() == BillingClient.BillingResponseCode.OK) _revision.incrementAndGet();
                    for (ConnectionAction pending : waiting) {
                        if (!_requests.contains(pending.Request)) continue;
                        if (result.getResponseCode() != BillingClient.BillingResponseCode.OK) Result(pending.Request, result.getResponseCode(), null, null);
                        else {
                            try { pending.Action.run(); }
                            catch (Exception ignored) { Result(pending.Request, 6, null, null); }
                        }
                    }
                });
            }
            @Override public void onBillingServiceDisconnected() {
                _main.post(() -> {
                    _connecting = false;
                    for (ConnectionAction pending : _connectionActions) Result(pending.Request, -1, null, null);
                    _connectionActions.clear();
                });
            }
        }); }
        catch (Exception ignored) {
            _connecting = false;
            for (ConnectionAction pending : _connectionActions) Result(pending.Request, 6, null, null);
            _connectionActions.clear();
        }
    }

    private void PurchasesUpdated(BillingResult result, List<Purchase> purchases) {
        if (_closed) return;
        _revision.incrementAndGet();
        if (_purchase == null) return;
        if (result.getResponseCode() != BillingClient.BillingResponseCode.OK) { FinishPurchase(result.getResponseCode(), null); return; }
        JSONArray matched = new JSONArray();
        if (purchases != null) for (Purchase purchase : purchases) {
            JSONObject serialized = SerializePurchase(purchase);
            if (serialized != null && _purchase.OrderId.equals(serialized.optString("OrderId"))
                    && _purchase.AccountBinding.equals(serialized.optString("AccountBinding"))
                    && _purchase.ProductId.equals(serialized.optString("StoreProductId"))) matched.put(serialized);
        }
        if (matched.length() > 0) FinishPurchase(0, matched);
    }

    private void FinishPurchase(int code, JSONArray purchases) {
        if (_purchase == null) return;
        String request = _purchase.Request;
        _purchase = null;
        Result(request, code, null, purchases);
    }

    private static JSONArray SerializePurchases(List<Purchase> purchases) {
        JSONArray serialized = new JSONArray();
        if (purchases != null) for (Purchase purchase : purchases) {
            JSONObject entry = SerializePurchase(purchase);
            if (entry != null) serialized.put(entry);
        }
        return serialized;
    }

    private static JSONObject SerializePurchase(Purchase purchase) {
        if (purchase == null || purchase.getQuantity() != 1 || purchase.getProducts().size() != 1
                || (purchase.getPurchaseState() != Purchase.PurchaseState.PURCHASED && purchase.getPurchaseState() != Purchase.PurchaseState.PENDING)) return null;
        AccountIdentifiers identifiers = purchase.getAccountIdentifiers();
        if (identifiers == null || identifiers.getObfuscatedAccountId() == null || identifiers.getObfuscatedProfileId() == null) return null;
        JSONObject serialized = new JSONObject();
        Put(serialized, "OrderId", identifiers.getObfuscatedProfileId());
        Put(serialized, "AccountBinding", identifiers.getObfuscatedAccountId());
        Put(serialized, "StoreProductId", purchase.getProducts().get(0));
        Put(serialized, "Proof", purchase.getPurchaseToken());
        Put(serialized, "Pending", purchase.getPurchaseState() == Purchase.PurchaseState.PENDING);
        return serialized;
    }

    private synchronized void Result(String request, int code, JSONArray prices, JSONArray purchases) {
        if (_closed || !_requests.remove(request)) return;
        JSONObject result = new JSONObject();
        Put(result, "Code", code);
        Put(result, "Prices", prices == null ? new JSONArray() : prices);
        Put(result, "Purchases", purchases == null ? new JSONArray() : purchases);
        _results.put(request, result.toString());
    }

    private static void Put(JSONObject target, String key, Object value) {
        try { target.put(key, value); }
        catch (JSONException ignored) { throw new IllegalArgumentException(); }
    }
}
