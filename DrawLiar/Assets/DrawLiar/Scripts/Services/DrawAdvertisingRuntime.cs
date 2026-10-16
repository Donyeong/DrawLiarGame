using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace DrawLiar
{
    internal sealed class DrawAdvertisingRuntime : MonoBehaviour, IDrawAdvertisingProvider
    {
        [Serializable]
        private sealed class NativeState
        {
            public bool Initialized = false;
            public bool InitializationFinished = false;
            public bool BannerReady = false;
            public bool RewardedReady = false;
            public bool PrivacyRequired = false;
            public int BannerHeight = 0;
            public bool RewardPlaying = false;
            public string RewardRequest = "";
            public int RewardOutcome = 0;
            public bool PrivacyOpen = false;
        }

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private NativeState _state = new NativeState();
        private bool _disposed;
        private bool _bannerVisible;
        private bool _audioMuted;
        private bool _previousAudioPause;
        private bool _rewardPending;
        private double _nextPoll;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _native;
#elif UNITY_WEBGL && !UNITY_EDITOR
        private int _native;
        [DllImport("__Internal")] private static extern int DrawAdsInitialize(string publisherId, string bannerSlot, int rewarded,
            int consentRequired, string watchLabel, string cancelLabel);
        [DllImport("__Internal")] private static extern string DrawAdsState(int handle);
        [DllImport("__Internal")] private static extern void DrawAdsBannerVisible(int handle, int visible);
        [DllImport("__Internal")] private static extern int DrawAdsReward(int handle, string request, string watchLabel, string cancelLabel);
        [DllImport("__Internal")] private static extern void DrawAdsCancelReward(int handle, string request);
        [DllImport("__Internal")] private static extern void DrawAdsPrivacy(int handle);
        [DllImport("__Internal")] private static extern void DrawAdsDispose(int handle);
#endif
        public event Action Changed;
        public bool IsDisposed => _disposed;
        public bool IsBannerReady => !_disposed && _state.Initialized && _state.BannerReady;
        public bool IsRewardedReady => !_disposed && _state.Initialized && _state.RewardedReady && !_rewardPending;
        public bool IsPrivacyOptionsRequired => !_disposed && _state.PrivacyRequired;
        public int BannerHeightPixels => _bannerVisible && IsBannerReady ? Math.Max(0, _state.BannerHeight) : 0;

        public async Task InitializeAsync(AdsStateResponse state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(DrawAdvertisingRuntime));
            if (state == null || state.Platform != DrawAdvertisingProviders.Platform || state.AdsRemoved
                || (!state.BannerEnabled && !state.RewardedEnabled)) return;
            var config = DrawAdvertisingConfig.Load();
            if (config == null || !config.Enabled) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!config.IsAndroidConfigured || _native != null) return;
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    _native = new AndroidJavaObject("com.rascallab.drawliar.ads.DrawAdvertising", activity,
                        state.BannerEnabled ? state.BannerAdUnitId : "", state.RewardedEnabled ? state.RewardedAdUnitId : "");
                _native.Call("Initialize");
                _native.Call("SetBannerVisible", _bannerVisible);
#elif UNITY_WEBGL && !UNITY_EDITOR
                if (_native != 0) return;
                _native = DrawAdsInitialize(state.WebPublisherId, state.BannerEnabled ? state.WebBannerSlotId : "",
                    state.RewardedEnabled ? 1 : 0, config.WebConsentRequired ? 1 : 0,
                    DrawLocalization.Text("광고 보기"), DrawLocalization.Text("취소"));
                if (_native == 0) return;
                DrawAdsBannerVisible(_native, _bannerVisible ? 1 : 0);
#else
                await Task.CompletedTask;
                return;
#endif
#if (UNITY_ANDROID || UNITY_WEBGL) && !UNITY_EDITOR
                double deadline = Time.realtimeSinceStartupAsDouble + 120;
                while (!_state.InitializationFinished && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    Poll();
                    if (!_state.InitializationFinished) await DrawAsync.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
                }
                linked.Token.ThrowIfCancellationRequested();
                if (!_state.InitializationFinished || (!_state.Initialized && !_state.PrivacyRequired)) Dispose();
#endif
            }
            catch (OperationCanceledException) { Dispose(); throw; }
            catch (Exception) { Dispose(); }
        }

        public void SetBannerVisible(bool visible)
        {
            if (_disposed || _bannerVisible == visible) return;
            _bannerVisible = visible;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _native?.Call("SetBannerVisible", visible);
#elif UNITY_WEBGL && !UNITY_EDITOR
                if (_native != 0) DrawAdsBannerVisible(_native, visible ? 1 : 0);
#endif
            }
            catch (Exception) { _state.BannerReady = false; }
            Changed?.Invoke();
        }

        public async Task<DrawAdPlaybackResult> ShowRewardedAsync(AdsRewardTicket ticket, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRewardedReady || ticket == null || string.IsNullOrEmpty(ticket.Ticket) || string.IsNullOrEmpty(ticket.UserId))
                return new DrawAdPlaybackResult(false, "unavailable");
            string request = Guid.NewGuid().ToString("N");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _rewardPending = true;
            Changed?.Invoke();
            try
            {
                bool started = false;
#if UNITY_ANDROID && !UNITY_EDITOR
                started = _native != null && _native.Call<bool>("ShowRewarded", request, ticket.UserId, ticket.Ticket);
#elif UNITY_WEBGL && !UNITY_EDITOR
                started = _native != 0 && DrawAdsReward(_native, request,
                    DrawLocalization.Text("광고 보기"), DrawLocalization.Text("취소")) == 1;
#endif
                if (!started) return new DrawAdPlaybackResult(false, "unavailable");
                double deadline = Time.realtimeSinceStartupAsDouble + 180;
                while (Time.realtimeSinceStartupAsDouble < deadline)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    Poll();
                    if (_state.RewardRequest == request && _state.RewardOutcome != 0)
                        return new DrawAdPlaybackResult(_state.RewardOutcome == 1, _state.RewardOutcome == 1 ? "" : "dismissed");
                    await DrawAsync.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
                }
                return new DrawAdPlaybackResult(false, "timeout");
            }
            finally
            {
                try
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    _native?.Call("CancelReward", request);
#elif UNITY_WEBGL && !UNITY_EDITOR
                    if (_native != 0) DrawAdsCancelReward(_native, request);
#endif
                }
                catch (Exception) { }
                RestoreAudio();
                _rewardPending = false;
                if (!_disposed) Changed?.Invoke();
            }
        }

        public async Task ShowPrivacyOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPrivacyOptionsRequired) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
#if UNITY_ANDROID && !UNITY_EDITOR
            _native?.Call("ShowPrivacyOptions");
#elif UNITY_WEBGL && !UNITY_EDITOR
            if (_native != 0) DrawAdsPrivacy(_native);
#endif
            // 네이티브 UI 요청이 실행될 시간을 준 뒤 닫힘을 확인한다.
            await DrawAsync.Delay(TimeSpan.FromMilliseconds(250), linked.Token);
            Poll();
            while (_state.PrivacyOpen)
            {
                await DrawAsync.Delay(TimeSpan.FromMilliseconds(100), linked.Token);
                Poll();
            }
        }

        private void Update()
        {
            if (_disposed || Time.realtimeSinceStartupAsDouble < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartupAsDouble + .25;
            Poll();
        }

        private void Poll()
        {
            if (_disposed) return;
            try
            {
                string json = null;
#if UNITY_ANDROID && !UNITY_EDITOR
                json = _native?.Call<string>("GetState");
#elif UNITY_WEBGL && !UNITY_EDITOR
                if (_native != 0) json = DrawAdsState(_native);
#endif
                if (string.IsNullOrEmpty(json)) return;
                var next = JsonUtility.FromJson<NativeState>(json);
                if (next == null) return;
                bool changed = next.BannerReady != _state.BannerReady || next.RewardedReady != _state.RewardedReady
                    || next.BannerHeight != _state.BannerHeight || next.PrivacyRequired != _state.PrivacyRequired;
                _state = next;
                if (next.RewardPlaying && !_audioMuted)
                {
                    _previousAudioPause = AudioListener.pause;
                    AudioListener.pause = true;
                    _audioMuted = true;
                }
                else if (!next.RewardPlaying) RestoreAudio();
                if (changed) Changed?.Invoke();
            }
            catch (Exception) { Dispose(); }
        }

        private void OnApplicationPause(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _native?.Call("SetPaused", paused); } catch (Exception) { }
#endif
        }

        private void RestoreAudio()
        {
            if (!_audioMuted) return;
            AudioListener.pause = _previousAudioPause;
            _audioMuted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _native?.Call("Close");
                _native?.Dispose();
                _native = null;
#elif UNITY_WEBGL && !UNITY_EDITOR
                if (_native != 0) DrawAdsDispose(_native);
                _native = 0;
#endif
            }
            catch (Exception) { }
            RestoreAudio();
            Changed?.Invoke();
            Changed = null;
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            Dispose();
            _lifetime.Dispose();
        }
    }
}
