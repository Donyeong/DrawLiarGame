using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace DrawLiar
{
    internal static class GoogleAccountProvider
    {
        public static string Platform => Application.isMobilePlatform ? "mobile" : "desktop";
        public static bool IsSupported
        {
            get
            {
#if UNITY_ANDROID || UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
                return true;
#else
                return false;
#endif
            }
        }

        public static async Task<GoogleAuthRequest> AuthenticateAsync(GoogleChallengeResponse challenge, CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            var authorization = await GoogleDesktopAuthentication.AuthenticateAsync(challenge.ClientId, challenge.Nonce, Application.OpenURL, cancellationToken);
            return new GoogleAuthRequest { ChallengeId = challenge.ChallengeId, Code = authorization.Code,
                CodeVerifier = authorization.CodeVerifier, RedirectUri = authorization.RedirectUri };
#elif UNITY_ANDROID && !UNITY_EDITOR
            cancellationToken.ThrowIfCancellationRequested();
            var context = SynchronizationContext.Current ?? throw new InvalidOperationException("Google 로그인은 게임 화면에서 실행하세요.");
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string requestId = Guid.NewGuid().ToString("N");
            var callback = new Callback(context, completion);
            using var registration = cancellationToken.Register(() => context.Post(_ =>
            {
                Cancel(requestId); completion.TrySetCanceled();
            }, null));
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var bridge = new AndroidJavaClass(BRIDGE_CLASS);
                bridge.CallStatic("SignIn", activity, requestId, challenge.ClientId, challenge.Nonce, callback);
                return new GoogleAuthRequest { ChallengeId = challenge.ChallengeId, IdToken = await completion.Task };
            }
            catch (AndroidJavaException) { throw new InvalidOperationException("Google 로그인 서비스를 사용할 수 없습니다."); }
            finally { Cancel(requestId); GC.KeepAlive(callback); }
#else
            await Task.CompletedTask;
            throw new InvalidOperationException("이 플랫폼에서는 Google 로그인을 지원하지 않습니다.");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private const string BRIDGE_CLASS = "com.drawliar.accounts.DrawGoogleAccount";
        private static void Cancel(string requestId)
        {
            try { using var bridge = new AndroidJavaClass(BRIDGE_CLASS); bridge.CallStatic("Cancel", requestId); }
            catch (AndroidJavaException) { }
        }
        [Preserve]
        private sealed class Callback : AndroidJavaProxy
        {
            private readonly SynchronizationContext _context;
            private readonly TaskCompletionSource<string> _completion;
            public Callback(SynchronizationContext context, TaskCompletionSource<string> completion) : base(BRIDGE_CLASS + "$Callback")
            {
                _context = context; _completion = completion;
            }
            [Preserve]
            public void OnCompleted(string idToken, string error)
            {
                _context.Post(_ =>
                {
                    if (error == "cancelled") _completion.TrySetCanceled();
                    else if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(idToken))
                        _completion.TrySetException(new InvalidOperationException("Google 로그인 서비스를 사용할 수 없습니다."));
                    else _completion.TrySetResult(idToken);
                }, null);
            }
        }
#endif
    }
}
