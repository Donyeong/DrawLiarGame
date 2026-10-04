#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    internal sealed class DrawBrowserRuntime : MonoBehaviour
    {
        private static DrawBrowserRuntime _instance;
        private readonly List<DrawBrowserWebSocket> _sockets = new List<DrawBrowserWebSocket>();
        private readonly Dictionary<CancellationTokenSource, double> _timeouts = new Dictionary<CancellationTokenSource, double>();
        private readonly List<CancellationTokenSource> _dueTimeouts = new List<CancellationTokenSource>();

        private static DrawBrowserRuntime Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var owner = new GameObject("DrawLiarBrowser") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(owner);
                return _instance = owner.AddComponent<DrawBrowserRuntime>();
            }
        }

        public static void Track(DrawBrowserWebSocket socket) => Instance._sockets.Add(socket);

        public static void CancelAfter(CancellationTokenSource source, TimeSpan delay)
        {
            if (delay == Timeout.InfiniteTimeSpan) Instance._timeouts.Remove(source);
            else Instance._timeouts[source] = Time.realtimeSinceStartupAsDouble + Math.Max(0, delay.TotalSeconds);
        }

        public static Task Delay(TimeSpan delay, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<bool>();
            Instance.StartCoroutine(WaitForDelay(delay, completion, cancellation));
            return completion.Task;
        }

        private static IEnumerator WaitForDelay(TimeSpan delay, TaskCompletionSource<bool> completion, CancellationToken cancellation)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + Math.Max(0, delay.TotalSeconds);
            using (cancellation.Register(() => completion.TrySetCanceled()))
            {
                while (!completion.Task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                completion.TrySetResult(true);
            }
        }

        public static async Task<string> ClipboardAsync(string value, bool read)
        {
            int id = DrawBrowserInterop.DrawBrowserClipboardStart(value, read ? 1 : 0);
            if (id <= 0) throw new InvalidOperationException("요청을 처리하지 못했습니다. 다시 시도하세요.");
            var completion = new TaskCompletionSource<string>();
            Instance.StartCoroutine(ReceiveClipboard(id, completion));
            try { return await completion.Task; }
            finally { DrawBrowserInterop.DrawBrowserClipboardRelease(id); }
        }

        private static IEnumerator ReceiveClipboard(int id, TaskCompletionSource<string> completion)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                int state = DrawBrowserInterop.DrawBrowserClipboardState(id);
                if (state == 1) { completion.TrySetResult(DrawBrowserInterop.DrawBrowserClipboardValue(id) ?? ""); yield break; }
                if (state != 0) break;
                yield return null;
            }
            completion.TrySetException(new InvalidOperationException("요청을 처리하지 못했습니다. 다시 시도하세요."));
        }

        public static async Task<string> AuthenticateGoogleAsync(string clientId, string nonce, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(clientId) || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
                || clientId.Length > 256 || string.IsNullOrEmpty(nonce) || nonce.Length > 1024)
                throw GoogleError();
            int id = DrawBrowserInterop.DrawBrowserGoogleStart(clientId, nonce, DrawLocalization.Text("닫기"));
            if (id <= 0) throw GoogleError();
            var completion = new TaskCompletionSource<string>();
            Instance.StartCoroutine(ReceiveGoogle(id, completion, cancellation));
            try { return await completion.Task; }
            finally { DrawBrowserInterop.DrawBrowserGoogleCancel(id); }
        }

        private static IEnumerator ReceiveGoogle(int id, TaskCompletionSource<string> completion, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                int state = DrawBrowserInterop.DrawBrowserGoogleState(id);
                if (state == 1)
                {
                    string credential = DrawBrowserInterop.DrawBrowserGoogleCredential(id);
                    if (string.IsNullOrEmpty(credential) || credential.Length > 65536) completion.TrySetException(GoogleError());
                    else completion.TrySetResult(credential);
                    yield break;
                }
                if (state == 2) { completion.TrySetCanceled(); yield break; }
                if (state != 0) { completion.TrySetException(GoogleError()); yield break; }
                yield return null;
            }
            completion.TrySetCanceled();
        }

        private static InvalidOperationException GoogleError() => new InvalidOperationException("Google 로그인 서비스를 사용할 수 없습니다.");

        private void Update()
        {
            _dueTimeouts.Clear();
            foreach (var timeout in _timeouts)
                if (timeout.Key.IsCancellationRequested || Time.realtimeSinceStartupAsDouble >= timeout.Value) _dueTimeouts.Add(timeout.Key);
            foreach (var source in _dueTimeouts)
            {
                _timeouts.Remove(source);
                try { source.Cancel(); } catch (ObjectDisposedException) { }
            }
            for (int index = _sockets.Count - 1; index >= 0; index--)
                if (!_sockets[index].Pump()) _sockets.RemoveAt(index);
        }

        private void OnDestroy()
        {
            foreach (var socket in _sockets) socket.Dispose();
            _sockets.Clear();
            if (_instance == this) _instance = null;
        }
    }
}
#endif
