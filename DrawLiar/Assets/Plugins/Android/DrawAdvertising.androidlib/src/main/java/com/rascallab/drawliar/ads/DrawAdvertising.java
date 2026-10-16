package com.rascallab.drawliar.ads;

import android.app.Activity;
import android.app.Application;
import android.graphics.Insets;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.FrameLayout;
import androidx.annotation.Keep;
import com.google.android.gms.ads.AdError;
import com.google.android.gms.ads.AdListener;
import com.google.android.gms.ads.AdRequest;
import com.google.android.gms.ads.AdSize;
import com.google.android.gms.ads.AdView;
import com.google.android.gms.ads.FullScreenContentCallback;
import com.google.android.gms.ads.LoadAdError;
import com.google.android.gms.ads.MobileAds;
import com.google.android.gms.ads.rewarded.RewardedAd;
import com.google.android.gms.ads.rewarded.RewardedAdLoadCallback;
import com.google.android.gms.ads.rewarded.ServerSideVerificationOptions;
import com.google.android.ump.ConsentInformation;
import com.google.android.ump.ConsentRequestParameters;
import com.google.android.ump.UserMessagingPlatform;
import java.lang.ref.WeakReference;
import org.json.JSONObject;

@Keep
public final class DrawAdvertising implements Application.ActivityLifecycleCallbacks {
    private final WeakReference<Activity> activity;
    private final String bannerId;
    private final String rewardedId;
    private final Handler main = new Handler(Looper.getMainLooper());
    private volatile boolean closed;
    private volatile boolean initialized;
    private volatile boolean initializationFinished;
    private volatile boolean bannerReady;
    private volatile boolean rewardedReady;
    private volatile boolean privacyRequired;
    private volatile boolean privacyOpen;
    private volatile boolean rewardPlaying;
    private volatile int bannerHeight;
    private volatile String rewardRequest = "";
    private volatile String cancelledRewardRequest = "";
    private volatile int rewardOutcome;
    private boolean bannerVisible;
    private boolean paused;
    private boolean initializing;
    private boolean rewardLoading;
    private boolean rewardEarned;
    private long rewardedLoadedAt;
    private int bannerWidth;
    private FrameLayout container;
    private AdView banner;
    private RewardedAd rewarded;
    private ConsentInformation consent;
    private final Runnable reloadReward = () -> LoadRewarded();
    private final Runnable reloadBanner = () -> LoadBanner();
    private final Runnable checkLayout = new Runnable() {
        @Override public void run() {
            if (closed) return;
            Activity current = Current();
            if (current == null) { Close(); return; }
            int width = current.getWindow().getDecorView().getWidth();
            if (initialized && width > 0 && width != bannerWidth) LoadBanner();
            if (rewarded != null && System.currentTimeMillis() - rewardedLoadedAt >= 3500000) {
                rewarded = null;
                rewardedReady = false;
                LoadRewarded();
            }
            main.postDelayed(this, 1000);
        }
    };

    @Keep
    public DrawAdvertising(Activity owner, String bannerAdUnitId, String rewardedAdUnitId) {
        activity = new WeakReference<>(owner);
        bannerId = ValidId(bannerAdUnitId) ? bannerAdUnitId : "";
        rewardedId = ValidId(rewardedAdUnitId) ? rewardedAdUnitId : "";
    }

    private static boolean ValidId(String value) { return value != null && value.matches("ca-app-pub-[0-9]{16}/[0-9]{10}"); }
    private Activity Current() {
        Activity current = activity.get();
        return current == null || current.isFinishing() || current.isDestroyed() ? null : current;
    }

    @Keep
    public void Initialize() {
        main.post(() -> {
            Activity current = Current();
            if (closed || initializing || current == null) return;
            initializing = true;
            current.getApplication().registerActivityLifecycleCallbacks(this);
            consent = UserMessagingPlatform.getConsentInformation(current);
            consent.requestConsentInfoUpdate(current, new ConsentRequestParameters.Builder().build(), () -> {
                if (closed || Current() == null) return;
                UserMessagingPlatform.loadAndShowConsentFormIfRequired(current, error -> ConsentFinished());
            }, error -> ConsentFinished());
        });
    }

    private void ConsentFinished() {
        if (closed || Current() == null) return;
        privacyRequired = consent.getPrivacyOptionsRequirementStatus() == ConsentInformation.PrivacyOptionsRequirementStatus.REQUIRED;
        if (!consent.canRequestAds()) { initializationFinished = true; return; }
        Activity current = Current();
        new Thread(() -> {
            if (closed || current == null) return;
            MobileAds.initialize(current.getApplicationContext(), status -> main.post(() -> {
                if (closed || Current() == null) return;
                initialized = true;
                initializationFinished = true;
                LoadBanner();
                LoadRewarded();
                main.post(checkLayout);
            }));
        }, "DrawLiarAdsInit").start();
    }

    private void DestroyBanner() {
        bannerReady = false;
        bannerHeight = 0;
        if (banner != null) {
            if (banner.getParent() instanceof ViewGroup) ((ViewGroup) banner.getParent()).removeView(banner);
            banner.destroy();
            banner = null;
        }
        if (container != null) {
            if (container.getParent() instanceof ViewGroup) ((ViewGroup) container.getParent()).removeView(container);
            container = null;
        }
    }

    private void LoadBanner() {
        Activity current = Current();
        if (closed || !initialized || current == null || bannerId.isEmpty() || !consent.canRequestAds()) return;
        main.removeCallbacks(reloadBanner);
        DestroyBanner();
        View decor = current.getWindow().getDecorView();
        bannerWidth = decor.getWidth();
        if (bannerWidth <= 0) { main.postDelayed(reloadBanner, 1000); return; }
        float density = current.getResources().getDisplayMetrics().density;
        int insetLeft = 0, insetRight = 0, insetBottom = 0;
        WindowInsets insets = decor.getRootWindowInsets();
        if (insets != null && Build.VERSION.SDK_INT >= 30) {
            Insets safeInsets = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
            insetLeft = safeInsets.left;
            insetRight = safeInsets.right;
            insetBottom = safeInsets.bottom;
        } else if (insets != null) {
            int[] legacyInsets = LegacyInsets(insets);
            insetLeft = legacyInsets[0];
            insetRight = legacyInsets[1];
            insetBottom = legacyInsets[2];
        }
        int widthDp = Math.max(1, (int) ((bannerWidth - insetLeft - insetRight) / density));
        if (widthDp < 320) return;
        AdSize size = AdSize.BANNER;
        container = new FrameLayout(current);
        container.setBackgroundColor(android.graphics.Color.WHITE);
        container.setPadding(insetLeft, 0, insetRight, insetBottom);
        container.setVisibility(View.GONE);
        FrameLayout.LayoutParams layout = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
            size.getHeightInPixels(current) + insetBottom, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        current.addContentView(container, layout);
        AdView created = new AdView(current);
        banner = created;
        created.setAdUnitId(bannerId);
        created.setAdSize(size);
        container.addView(created, new FrameLayout.LayoutParams(size.getWidthInPixels(current), size.getHeightInPixels(current), Gravity.TOP | Gravity.CENTER_HORIZONTAL));
        created.setAdListener(new AdListener() {
            @Override public void onAdLoaded() {
                if (closed || banner != created || Current() == null) return;
                bannerReady = true;
                bannerHeight = layout.height;
                ApplyBannerVisibility();
            }
            @Override public void onAdFailedToLoad(LoadAdError error) {
                if (closed || banner != created) return;
                DestroyBanner();
                main.postDelayed(reloadBanner, 60000);
            }
        });
        created.loadAd(new AdRequest.Builder().build());
    }

    @SuppressWarnings("deprecation")
    private static int[] LegacyInsets(WindowInsets insets) {
        return new int[] { insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom() };
    }

    private void ApplyBannerVisibility() {
        if (container != null) container.setVisibility(bannerReady && bannerVisible && !paused ? View.VISIBLE : View.GONE);
        if (banner != null) { if (bannerVisible && !paused) banner.resume(); else banner.pause(); }
    }

    private void LoadRewarded() {
        Activity current = Current();
        if (closed || !initialized || current == null || rewardedId.isEmpty() || rewardLoading || rewarded != null
            || rewardPlaying || !consent.canRequestAds()) return;
        main.removeCallbacks(reloadReward);
        rewardLoading = true;
        RewardedAd.load(current, rewardedId, new AdRequest.Builder().build(), new RewardedAdLoadCallback() {
            @Override public void onAdLoaded(RewardedAd ad) {
                rewardLoading = false;
                if (closed || Current() == null || consent == null || !consent.canRequestAds()) return;
                rewarded = ad;
                rewardedLoadedAt = System.currentTimeMillis();
                rewardedReady = true;
            }
            @Override public void onAdFailedToLoad(LoadAdError error) {
                rewardLoading = false;
                if (closed) return;
                rewarded = null;
                rewardedReady = false;
                main.postDelayed(reloadReward, 60000);
            }
        });
    }

    @Keep
    public void SetBannerVisible(boolean visible) { main.post(() -> { if (!closed) { bannerVisible = visible; ApplyBannerVisibility(); } }); }
    @Keep
    public void SetPaused(boolean value) { main.post(() -> { if (!closed) { paused = value; ApplyBannerVisibility(); } }); }

    @Keep
    public boolean ShowRewarded(String request, String userId, String ticket) {
        if (closed || !rewardedReady || request == null || request.length() > 64 || userId == null || userId.length() > 256
            || ticket == null || !ticket.matches("[a-f0-9]{64}")) return false;
        main.post(() -> {
            Activity current = Current();
            if (closed || request.equals(cancelledRewardRequest)) return;
            if (current == null || rewarded == null || rewardPlaying || consent == null || !consent.canRequestAds()) {
                rewardOutcome = 3;
                rewardRequest = request;
                return;
            }
            rewardOutcome = 0;
            rewardEarned = false;
            rewardRequest = request;
            RewardedAd ad = rewarded;
            rewarded = null;
            rewardedReady = false;
            ad.setServerSideVerificationOptions(new ServerSideVerificationOptions.Builder().setUserId(userId).setCustomData(ticket).build());
            ad.setFullScreenContentCallback(new FullScreenContentCallback() {
                @Override public void onAdShowedFullScreenContent() { if (!closed && rewardRequest.equals(request)) rewardPlaying = true; }
                @Override public void onAdDismissedFullScreenContent() {
                    if (closed) return;
                    rewardPlaying = false;
                    if (rewardRequest.equals(request)) rewardOutcome = rewardEarned ? 1 : 2;
                    LoadRewarded();
                }
                @Override public void onAdFailedToShowFullScreenContent(AdError error) {
                    if (closed) return;
                    rewardPlaying = false;
                    if (rewardRequest.equals(request)) rewardOutcome = 3;
                    LoadRewarded();
                }
            });
            if (!closed && !request.equals(cancelledRewardRequest))
                ad.show(current, reward -> { if (!closed && rewardRequest.equals(request)) rewardEarned = true; });
            else LoadRewarded();
        });
        return true;
    }

    @Keep
    public void CancelReward(String request) {
        cancelledRewardRequest = request;
        main.post(() -> {
            if (!rewardRequest.equals(request)) return;
            rewardRequest = "";
            rewardOutcome = 0;
            rewardEarned = false;
        });
    }

    @Keep
    public void ShowPrivacyOptions() {
        main.post(() -> {
            Activity current = Current();
            if (closed || current == null || !privacyRequired || privacyOpen) return;
            privacyOpen = true;
            UserMessagingPlatform.showPrivacyOptionsForm(current, error -> {
                if (closed) return;
                privacyOpen = false;
                privacyRequired = consent.getPrivacyOptionsRequirementStatus() == ConsentInformation.PrivacyOptionsRequirementStatus.REQUIRED;
                if (!consent.canRequestAds()) {
                    DestroyBanner();
                    rewarded = null;
                    rewardedReady = false;
                } else if (initialized) { LoadBanner(); LoadRewarded(); }
                else ConsentFinished();
            });
        });
    }

    @Keep
    public String GetState() {
        try {
            return new JSONObject().put("Initialized", initialized).put("InitializationFinished", initializationFinished)
                .put("BannerReady", bannerReady && !paused).put("RewardedReady", rewardedReady)
                .put("PrivacyRequired", privacyRequired).put("BannerHeight", bannerHeight)
                .put("RewardPlaying", rewardPlaying).put("RewardRequest", rewardRequest)
                .put("RewardOutcome", rewardOutcome).put("PrivacyOpen", privacyOpen).toString();
        } catch (Exception ignored) { return "{}"; }
    }

    @Keep
    public void Close() {
        closed = true;
        main.post(() -> {
            main.removeCallbacksAndMessages(null);
            DestroyBanner();
            rewarded = null;
            rewardedReady = false;
            rewardPlaying = false;
            rewardRequest = "";
            Activity current = activity.get();
            if (current != null) current.getApplication().unregisterActivityLifecycleCallbacks(this);
        });
    }

    @Override public void onActivityDestroyed(Activity value) { if (value == activity.get()) Close(); }
    @Override public void onActivityPaused(Activity value) { if (value == activity.get()) SetPaused(true); }
    @Override public void onActivityResumed(Activity value) { if (value == activity.get()) SetPaused(false); }
    @Override public void onActivityCreated(Activity value, Bundle saved) { }
    @Override public void onActivityStarted(Activity value) { }
    @Override public void onActivityStopped(Activity value) { }
    @Override public void onActivitySaveInstanceState(Activity value, Bundle saved) { }
}
