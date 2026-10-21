mergeInto(LibraryManager.library, {
  $DrawAds: {
    next: 0,
    sessions: {},
    scriptPromise: null,
    publisher: "",
    h5Ready: false,
    load: function (publisher) {
      if (DrawAds.scriptPromise) return DrawAds.publisher === publisher ? DrawAds.scriptPromise : Promise.reject(new Error("publisher"));
      DrawAds.publisher = publisher;
      var pending = new Promise(function (resolve, reject) {
        var script = document.createElement("script");
        var finished = false;
        var timer;
        function failed() {
          if (finished) return;
          finished = true;
          clearTimeout(timer);
          if (script.parentNode) script.parentNode.removeChild(script);
          reject(new Error("unavailable"));
        }
        script.async = true;
        script.crossOrigin = "anonymous";
        script.src = "https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=" + encodeURIComponent(publisher);
        script.setAttribute("data-ad-frequency-hint", "120s");
        script.onload = function () {
          if (finished) return;
          finished = true;
          clearTimeout(timer);
          resolve();
        };
        script.onerror = failed;
        window.adsbygoogle = window.adsbygoogle || [];
        timer = setTimeout(failed, 30000);
        try { document.head.appendChild(script); } catch (error) { failed(); }
      });
      DrawAds.scriptPromise = pending;
      pending.catch(function () {
        if (DrawAds.scriptPromise !== pending) return;
        DrawAds.scriptPromise = null;
        DrawAds.publisher = "";
      });
      return pending;
    },
    // 사이트 동의 훅이 없으면 AdSense 태그가 띄우는 Google CMP의 TCF 동의 결정을 기다린다.
    // Google CMP는 규제 지역 방문자에게만 로드되므로, 태그 로드 후에도 없으면 동의가 필요 없는 지역으로 본다.
    google: {
      request: function (publisher) {
        return DrawAds.load(publisher).then(function () {
          return new Promise(function (resolve) {
            var deadline = Date.now() + 5000;
            (function wait() {
              if (typeof window.__tcfapi !== "function") {
                if (Date.now() > deadline) resolve(true); else setTimeout(wait, 100);
                return;
              }
              window.__tcfapi("addEventListener", 2, function (data, success) {
                if (!success || !data) { resolve(false); return; }
                if (data.gdprApplies !== false && data.eventStatus !== "tcloaded" && data.eventStatus !== "useractioncomplete") return;
                if (data.gdprApplies && window.googlefc && typeof window.googlefc.showRevocationMessage === "function")
                  DrawAds.google.showPrivacyOptions = function () { window.googlefc.showRevocationMessage(); };
                window.__tcfapi("removeEventListener", 2, function () {}, data.listenerId);
                resolve(true);
              });
            })();
          });
        });
      }
    },
    live: function (session) { return session && !session.closed && DrawAds.sessions[session.id] === session; },
    start: function (session) {
      if (!DrawAds.live(session)) return Promise.resolve();
      session.allowed = true;
      return DrawAds.load(session.publisher).then(function () {
        if (!DrawAds.live(session) || !session.allowed) return;
        session.state.Initialized = true;
        session.state.InitializationFinished = true;
        if (!session.banner) DrawAds.banner(session);
        if (!session.rewarded) return;
        if (DrawAds.h5Ready) { session.state.RewardedReady = true; return; }
        window.adsbygoogle.push({
          preloadAdBreaks: "on", sound: "on",
          onReady: function () {
            DrawAds.h5Ready = true;
            Object.keys(DrawAds.sessions).forEach(function (key) {
              var current = DrawAds.sessions[key];
              if (DrawAds.live(current) && current.allowed && current.rewarded) current.state.RewardedReady = true;
            });
          }
        });
      });
    },
    banner: function (session) {
      if (!DrawAds.live(session) || !session.allowed || !session.slot || window.innerWidth < 320) return;
      var container = document.createElement("div");
      container.style.cssText = "position:fixed;bottom:0;left:0;right:0;display:flex;justify-content:center;align-items:center;visibility:hidden;transform:translateY(calc(100% + 64px));background:#fff;padding-bottom:env(safe-area-inset-bottom,0px);z-index:10000;pointer-events:none;";
      var unit = document.createElement("ins");
      unit.className = "adsbygoogle";
      unit.style.cssText = "display:inline-block;width:320px;height:50px;pointer-events:auto;";
      unit.setAttribute("data-ad-client", session.publisher);
      unit.setAttribute("data-ad-slot", session.slot);
      container.appendChild(unit);
      document.body.appendChild(container);
      session.banner = container;
      session.observer = new MutationObserver(function () {
        if (!DrawAds.live(session) || !session.allowed) return;
        var status = unit.getAttribute("data-ad-status");
        if (status !== "filled" && status !== "unfilled") return;
        session.state.BannerReady = status === "filled";
        DrawAds.visibility(session);
      });
      session.observer.observe(unit, { attributes: true, attributeFilter: ["data-ad-status"] });
      (window.adsbygoogle = window.adsbygoogle || []).push({});
    },
    visibility: function (session) {
      if (!session.banner) return;
      var canvas = Module["canvas"] || document.querySelector("canvas");
      var full = document.fullscreenElement;
      var wrapper = full && full.tagName !== "CANVAS" && typeof full.contains === "function" && full.contains(canvas);
      var parent = wrapper ? full : document.body;
      if (session.banner.parentNode !== parent) parent.appendChild(session.banner);
      var visible = session.state.BannerReady && session.visible && !document.hidden && window.innerWidth >= 320 && (!full || wrapper);
      session.banner.style.visibility = visible ? "visible" : "hidden";
      // AdSense가 자식 iframe을 visibility:visible로 덮어쓰므로, 숨길 때는 화면 밖으로 내려 보이거나 클릭되지 않게 한다.
      session.banner.style.transform = visible ? "" : "translateY(calc(100% + 64px))";
      var scale = canvas && canvas.getBoundingClientRect().height > 0 ? canvas.height / canvas.getBoundingClientRect().height : window.devicePixelRatio || 1;
      session.state.BannerHeight = visible ? Math.ceil(session.banner.getBoundingClientRect().height * scale) : 0;
    },
    closePrompt: function (session) {
      if (session.prompt && session.prompt.parentNode) session.prompt.parentNode.removeChild(session.prompt);
      session.prompt = null;
    },
    prompt: function (session, request, show) {
      if (!DrawAds.live(session) || session.state.RewardRequest !== request) return;
      var overlay = document.createElement("div");
      overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;background:rgba(35,25,20,.65);display:flex;align-items:center;justify-content:center;padding:20px;box-sizing:border-box;";
      overlay.setAttribute("role", "dialog");
      overlay.setAttribute("aria-modal", "true");
      var panel = document.createElement("div");
      panel.style.cssText = "display:flex;gap:12px;max-width:100%;padding:24px;border-radius:16px;background:#fff6e8;border:2px solid #765036;";
      var watch = document.createElement("button");
      watch.textContent = session.watchLabel;
      var cancel = document.createElement("button");
      cancel.textContent = session.cancelLabel;
      [watch, cancel].forEach(function (button) {
        button.style.cssText = "font:600 16px system-ui,sans-serif;min-height:48px;padding:10px 18px;border-radius:10px;border:1px solid #8c6546;background:#ffe2a8;color:#432d20;cursor:pointer;";
        panel.appendChild(button);
      });
      function dismiss() {
        if (!DrawAds.live(session) || session.state.RewardRequest !== request) return;
        session.state.RewardOutcome = 2;
        DrawAds.closePrompt(session);
      }
      watch.onclick = function () {
        if (!DrawAds.live(session) || session.state.RewardRequest !== request) return;
        DrawAds.closePrompt(session);
        try { show(); } catch (error) { session.state.RewardOutcome = 3; }
      };
      cancel.onclick = dismiss;
      overlay.onclick = function (event) { if (event.target === overlay) dismiss(); };
      overlay.onkeydown = function (event) {
        if (event.key === "Escape") { event.preventDefault(); dismiss(); }
        else if (event.key === "Tab") {
          event.preventDefault();
          if (document.activeElement === watch) cancel.focus(); else watch.focus();
        }
      };
      overlay.appendChild(panel);
      session.prompt = overlay;
      var full = document.fullscreenElement;
      if (full && document.exitFullscreen) {
        try { Promise.resolve(document.exitFullscreen()).catch(function () {}); } catch (error) {}
      }
      document.body.appendChild(overlay);
      watch.focus();
    }
  },

  DrawAdsInitialize__deps: ["$DrawAds"],
  DrawAdsInitialize: function (publisherPointer, slotPointer, rewarded, consentRequired, watchPointer, cancelPointer) {
    var publisher = UTF8ToString(publisherPointer);
    var slot = UTF8ToString(slotPointer);
    if (!/^ca-pub-\d{16}$/.test(publisher) || (slot && !/^\d{10}$/.test(slot))) return 0;
    var id = ++DrawAds.next;
    var session = {
      id: id, publisher: publisher, slot: slot, rewarded: !!rewarded, closed: false, visible: false, allowed: false,
      watchLabel: UTF8ToString(watchPointer), cancelLabel: UTF8ToString(cancelPointer),
      state: { Initialized: false, InitializationFinished: false, BannerReady: false, RewardedReady: false,
        PrivacyRequired: false, BannerHeight: 0, RewardPlaying: false, RewardRequest: "", RewardOutcome: 0, PrivacyOpen: false }
    };
    DrawAds.sessions[id] = session;
    session.visibilityListener = function () {
      if (session.state.Initialized && session.allowed && !session.banner) DrawAds.banner(session);
      DrawAds.visibility(session);
    };
    document.addEventListener("visibilitychange", session.visibilityListener);
    document.addEventListener("fullscreenchange", session.visibilityListener);
    window.addEventListener("resize", session.visibilityListener);
    var gate = window.DrawLiarAdvertisingConsent || (consentRequired ? DrawAds.google : null);
    session.gate = gate;
    var consent;
    try {
      consent = gate && typeof gate.request === "function" ? Promise.resolve(gate.request(publisher)) : Promise.resolve(!consentRequired);
    } catch (error) { consent = Promise.resolve(false); }
    consent.then(function (allowed) {
      if (!DrawAds.live(session)) return;
      session.state.PrivacyRequired = !!(gate && typeof gate.showPrivacyOptions === "function");
      if (allowed !== true) { session.state.InitializationFinished = true; return; }
      return DrawAds.start(session);
    }).catch(function () { if (DrawAds.live(session)) session.state.InitializationFinished = true; });
    return id;
  },

  DrawAdsState__deps: ["$DrawAds", "$stringToNewUTF8"],
  DrawAdsState: function (id) {
    var session = DrawAds.sessions[id];
    if (!session) return stringToNewUTF8("{}");
    DrawAds.visibility(session);
    return stringToNewUTF8(JSON.stringify(session.state));
  },

  DrawAdsBannerVisible__deps: ["$DrawAds"],
  DrawAdsBannerVisible: function (id, visible) {
    var session = DrawAds.sessions[id];
    if (!DrawAds.live(session)) return;
    session.visible = !!visible;
    DrawAds.visibility(session);
  },

  DrawAdsReward__deps: ["$DrawAds"],
  DrawAdsReward: function (id, requestPointer, watchPointer, cancelPointer) {
    var session = DrawAds.sessions[id];
    var request = UTF8ToString(requestPointer);
    if (!DrawAds.live(session) || !session.allowed || !session.state.RewardedReady || session.state.RewardPlaying || !/^[a-f0-9]{32}$/.test(request)) return 0;
    session.state.RewardRequest = request;
    session.state.RewardOutcome = 0;
    session.watchLabel = UTF8ToString(watchPointer);
    session.cancelLabel = UTF8ToString(cancelPointer);
    var viewed = false;
    function current() { return DrawAds.live(session) && session.allowed && session.state.RewardRequest === request; }
    try {
      window.adsbygoogle.push({
        type: "reward", name: "daily_gems",
        beforeReward: function (show) { if (current()) DrawAds.prompt(session, request, show); },
        beforeAd: function () { if (current()) { DrawAds.closePrompt(session); session.state.RewardPlaying = true; } },
        afterAd: function () { if (current()) session.state.RewardPlaying = false; },
        adDismissed: function () { if (current()) { viewed = false; session.state.RewardOutcome = 2; } },
        adViewed: function () { if (current()) viewed = true; },
        adBreakDone: function () {
          if (!current()) return;
          DrawAds.closePrompt(session);
          session.state.RewardPlaying = false;
          session.state.RewardOutcome = viewed ? 1 : 2;
        }
      });
      return 1;
    } catch (error) { session.state.RewardOutcome = 3; return 0; }
  },

  DrawAdsCancelReward__deps: ["$DrawAds"],
  DrawAdsCancelReward: function (id, requestPointer) {
    var session = DrawAds.sessions[id];
    if (!DrawAds.live(session) || session.state.RewardRequest !== UTF8ToString(requestPointer)) return;
    DrawAds.closePrompt(session);
    session.state.RewardRequest = "";
    session.state.RewardOutcome = 0;
    session.state.RewardPlaying = false;
  },

  DrawAdsPrivacy__deps: ["$DrawAds"],
  DrawAdsPrivacy: function (id) {
    var session = DrawAds.sessions[id];
    var gate = session && session.gate;
    if (!DrawAds.live(session) || !gate || typeof gate.showPrivacyOptions !== "function" || session.state.PrivacyOpen) return;
    session.state.PrivacyOpen = true;
    Promise.resolve().then(function () { return gate.showPrivacyOptions(); }).then(function (allowed) {
      if (!DrawAds.live(session)) return;
      if (allowed === false) {
        session.allowed = false;
        session.state.BannerReady = false;
        session.state.RewardedReady = false;
        session.state.RewardPlaying = false;
        if (session.state.RewardRequest) session.state.RewardOutcome = 3;
        DrawAds.closePrompt(session);
        if (session.observer) session.observer.disconnect();
        if (session.banner && session.banner.parentNode) session.banner.parentNode.removeChild(session.banner);
        session.banner = null;
        session.state.BannerHeight = 0;
      } else if (allowed === true) return DrawAds.start(session);
    }).catch(function () {}).then(function () { if (DrawAds.live(session)) session.state.PrivacyOpen = false; });
  },

  DrawAdsDispose__deps: ["$DrawAds"],
  DrawAdsDispose: function (id) {
    var session = DrawAds.sessions[id];
    if (!session) return;
    session.closed = true;
    DrawAds.closePrompt(session);
    if (session.observer) session.observer.disconnect();
    if (session.banner && session.banner.parentNode) session.banner.parentNode.removeChild(session.banner);
    document.removeEventListener("visibilitychange", session.visibilityListener);
    document.removeEventListener("fullscreenchange", session.visibilityListener);
    window.removeEventListener("resize", session.visibilityListener);
    delete DrawAds.sessions[id];
  }
});
