using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace DrawLiar
{
    public interface IDrawAdvertisingProvider : IDisposable
    {
        event Action Changed;
        bool IsDisposed { get; }
        bool IsBannerReady { get; }
        bool IsRewardedReady { get; }
        bool IsPrivacyOptionsRequired { get; }
        int BannerHeightPixels { get; }
        Task InitializeAsync(AdsStateResponse state, CancellationToken cancellationToken);
        void SetBannerVisible(bool visible);
        Task<DrawAdPlaybackResult> ShowRewardedAsync(AdsRewardTicket ticket, CancellationToken cancellationToken);
        Task ShowPrivacyOptionsAsync(CancellationToken cancellationToken);
    }

    public sealed class DrawAdPlaybackResult
    {
        public bool Completed { get; }
        public string Failure { get; }
        internal DrawAdPlaybackResult(bool completed, string failure = "") { Completed = completed; Failure = failure; }
    }

    public static class DrawAdvertisingProviders
    {
        public static string Platform
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return "Android";
#elif UNITY_WEBGL && !UNITY_EDITOR
                return "WebGL";
#else
                return "";
#endif
            }
        }
        public static bool IsSupportedPlatform => Platform.Length > 0;

        public static IDrawAdvertisingProvider Create()
        {
            if (!IsSupportedPlatform) return null;
            var config = DrawAdvertisingConfig.Load();
            if (config == null || !config.Enabled) return null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!config.IsAndroidConfigured) return null;
#endif
            var owner = new GameObject("DrawLiarAdvertising") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(owner);
            return owner.AddComponent<DrawAdvertisingRuntime>();
        }
    }

}
