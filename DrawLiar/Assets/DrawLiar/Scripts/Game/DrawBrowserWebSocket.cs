#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    internal sealed class DrawBrowserWebSocket : WebSocket
    {
        [DllImport("__Internal")] private static extern int DrawBrowserSocketConnect(string url);
        [DllImport("__Internal")] private static extern int DrawBrowserSocketState(int id);
        [DllImport("__Internal")] private static extern int DrawBrowserSocketRead(int id, byte[] buffer, int offset, int count);
        [DllImport("__Internal")] private static extern int DrawBrowserSocketSend(int id, byte[] buffer, int offset, int count);
        [DllImport("__Internal")] private static extern int DrawBrowserSocketClose(int id, int code);
        [DllImport("__Internal")] private static extern int DrawBrowserSocketCloseCode(int id);
        [DllImport("__Internal")] private static extern void DrawBrowserSocketRelease(int id);

        private readonly int _id;
        private readonly TaskCompletionSource<bool> _opened = new TaskCompletionSource<bool>();
        private TaskCompletionSource<WebSocketReceiveResult> _receiving;
        private TaskCompletionSource<bool> _closed;
        private ArraySegment<byte> _receiveBuffer;
        private bool _hasOpened, _closeSent, _closeReceived, _aborted, _disposed;
        private WebSocketCloseStatus? _closeStatus;

        private DrawBrowserWebSocket(int id) => _id = id;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string CloseStatusDescription => "";
        public override string SubProtocol => null;
        public override WebSocketState State
        {
            get
            {
                if (_aborted) return WebSocketState.Aborted;
                if (_disposed || _closeReceived) return WebSocketState.Closed;
                if (_closeSent) return WebSocketState.CloseSent;
                int state = DrawBrowserSocketState(_id);
                if (state == 0) return WebSocketState.Connecting;
                if (state == 1 || _hasOpened) return WebSocketState.Open;
                return WebSocketState.Closed;
            }
        }

        public static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int id = DrawBrowserSocketConnect(uri.AbsoluteUri);
            if (id <= 0) throw new WebSocketException("웹소켓 연결을 시작하지 못했습니다.");
            var socket = new DrawBrowserWebSocket(id);
            DrawBrowserRuntime.Track(socket);
            try
            {
                using (cancellation.Register(() => socket._opened.TrySetCanceled())) await socket._opened.Task;
                cancellation.ThrowIfCancellationRequested();
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            RequireActive();
            if (messageType != WebSocketMessageType.Text || !endOfMessage || buffer.Array == null)
                throw new ArgumentException("웹소켓 메시지가 올바르지 않습니다.");
            if (DrawBrowserSocketSend(_id, buffer.Array, buffer.Offset, buffer.Count) != 1)
                throw new WebSocketException("웹소켓 메시지를 보내지 못했습니다.");
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            RequireActive();
            if (_receiving != null) throw new InvalidOperationException("웹소켓 수신이 진행 중입니다.");
            if (buffer.Array == null || buffer.Count <= 0) throw new ArgumentException("수신 버퍼가 올바르지 않습니다.");
            if (TryReceive(buffer, out var result)) return result;
            var completion = _receiving = new TaskCompletionSource<WebSocketReceiveResult>();
            _receiveBuffer = buffer;
            try
            {
                using (cancellation.Register(() => completion.TrySetCanceled())) return await completion.Task;
            }
            finally { if (_receiving == completion) _receiving = null; }
        }

        private bool TryReceive(ArraySegment<byte> buffer, out WebSocketReceiveResult result)
        {
            int read = DrawBrowserSocketRead(_id, buffer.Array, buffer.Offset, buffer.Count);
            if (read == -1) { result = null; return false; }
            if (read == -2)
            {
                _closeReceived = true;
                _closeStatus = (WebSocketCloseStatus)DrawBrowserSocketCloseCode(_id);
                result = new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, _closeStatus, "");
                return true;
            }
            if (read < 0) throw new WebSocketException("웹소켓 메시지를 읽지 못했습니다.");
            result = new WebSocketReceiveResult(read / 2, WebSocketMessageType.Text, (read & 1) != 0);
            return true;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            RequireActive();
            if (DrawBrowserSocketClose(_id, (int)closeStatus) != 1) throw new WebSocketException("웹소켓 연결을 종료하지 못했습니다.");
            _closeSent = true;
            return Task.CompletedTask;
        }

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellation)
        {
            await CloseOutputAsync(closeStatus, statusDescription, cancellation);
            var completion = _closed = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => completion.TrySetCanceled())) await completion.Task;
        }

        public override void Abort()
        {
            if (_disposed) return;
            _aborted = true;
            Dispose();
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DrawBrowserSocketRelease(_id);
            _opened.TrySetCanceled();
            _receiving?.TrySetCanceled();
            _closed?.TrySetCanceled();
        }

        public bool Pump()
        {
            if (_disposed) return false;
            int state = DrawBrowserSocketState(_id);
            if (!_hasOpened)
            {
                if (state == 1) { _hasOpened = true; _opened.TrySetResult(true); }
                else if (state > 1 || state < 0) _opened.TrySetException(new WebSocketException("웹소켓 연결을 거부했습니다."));
            }
            if (_receiving != null && !_receiving.Task.IsCompleted)
            {
                try { if (TryReceive(_receiveBuffer, out var result)) _receiving.TrySetResult(result); }
                catch (WebSocketException exception) { _receiving.TrySetException(exception); }
            }
            if (state == 3) _closed?.TrySetResult(true);
            return !_disposed;
        }

        private void RequireActive()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DrawBrowserWebSocket));
            if (!_hasOpened || _aborted || _closeSent || _closeReceived) throw new WebSocketException("웹소켓이 연결되어 있지 않습니다.");
        }
    }
}
#endif
