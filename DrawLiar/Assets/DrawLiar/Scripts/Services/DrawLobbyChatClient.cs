using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed class DrawLobbyChatClient : IDisposable
    {
        public const int MAXIMUM_TEXT_LENGTH = 200;
        private const int MAXIMUM_HISTORY = 50;
        private const int MAXIMUM_FRAME_BYTES = 128 * 1024;
        private const int MAXIMUM_PENDING_FRAMES = 128;

        private sealed class Connection
        {
            public readonly ConcurrentQueue<LobbyChatEnvelope> Incoming = new ConcurrentQueue<LobbyChatEnvelope>();
            public readonly CancellationTokenSource Cancellation;
            public WebSocket Socket;
            public int Pending;
            public int Overflow;
            public bool InvalidSession;

            public Connection(CancellationToken lifetime) => Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        private sealed class PendingMessage
        {
            public readonly string RequestId = Guid.NewGuid().ToString("N");
            public readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly List<LobbyChatMessage> _messages = new List<LobbyChatMessage>();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private Connection _connection;
        private PendingMessage _pendingMessage;
        private string _sessionToken = "";
        private string _statusSource = "";
        private long _latestMessageId;
        private bool _invalidSession;
        private bool _isSending;

        public IReadOnlyList<LobbyChatMessage> Messages => _messages;
        public bool IsConnected { get; private set; }
        public bool IsSending => _isSending;
        public int MemberCount { get; private set; }
        public string Status => string.IsNullOrEmpty(_statusSource) ? "" : DrawLocalization.Text(_statusSource);
        public event Action Changed;
        public event Action<string> Notice;

        public void Start(string gameServerUrl, string sessionToken, OnlineServicesConfig config, CancellationToken lifetime)
        {
            if (_connection != null || _invalidSession && _sessionToken == sessionToken) return;
            var uri = new Uri(gameServerUrl);
            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme == "https" ? "wss" : "ws",
                Path = "/ws/lobby",
                Query = "",
                Fragment = ""
            };
            config.ValidateServerUrl(builder.Uri.AbsoluteUri);
            _sessionToken = sessionToken;
            _invalidSession = false;
            var connection = _connection = new Connection(lifetime);
            _statusSource = "채팅에 연결하고 있습니다…";
            Changed?.Invoke();
            byte[] authentication = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new LobbyChatEnvelope { Type = "auth", SessionToken = sessionToken }));
            _ = ConnectLoopAsync(connection, builder.Uri, config.CertificateSha256, authentication);
        }

        public void Stop(bool clearHistory = false)
        {
            var connection = _connection;
            _connection = null;
            if (connection != null)
            {
                try { connection.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                connection.Socket?.Abort();
            }
            FailPending("채팅을 보내지 못했습니다. 다시 시도하세요.");
            IsConnected = false;
            MemberCount = 0;
            _statusSource = _invalidSession ? "로그인이 만료되었습니다. 다시 로그인해 주세요." : "";
            if (clearHistory)
            {
                _messages.Clear();
                _latestMessageId = 0;
                _sessionToken = "";
                _invalidSession = false;
                _statusSource = "";
            }
            if (connection != null || clearHistory) Changed?.Invoke();
        }

        public async Task SendAsync(string text)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > MAXIMUM_TEXT_LENGTH)
                throw Error("채팅은 1~200자로 입력하세요.");
            foreach (char character in text)
                if (char.IsControl(character)) throw Error("채팅은 1~200자로 입력하세요.");
            var connection = _connection;
            if (!IsConnected || connection?.Socket?.State != WebSocketState.Open)
                throw Error("채팅에 연결된 뒤 다시 시도하세요.");
            if (_isSending) throw Error("메시지를 너무 빠르게 보내고 있습니다.");
            var pending = _pendingMessage = new PendingMessage();
            _isSending = true;
            Changed?.Invoke();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Cancellation.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new LobbyChatEnvelope
                    {
                        Type = "chat", RequestId = pending.RequestId, Text = text
                    }));
                    try { await SendFrameAsync(connection.Socket, bytes, timeout.Token); }
                    catch (InvalidOperationException) { throw Error("채팅을 보내지 못했습니다. 다시 시도하세요."); }
                    using (timeout.Token.Register(() => pending.Completion.TrySetCanceled())) await pending.Completion.Task;
                }
            }
            catch (Exception exception) when (exception is WebSocketException || exception is IOException
                || exception is OperationCanceledException || exception is ObjectDisposedException)
            {
                throw Error("채팅을 보내지 못했습니다. 다시 시도하세요.");
            }
            finally
            {
                if (_pendingMessage == pending)
                {
                    _pendingMessage = null;
                    _isSending = false;
                    Changed?.Invoke();
                }
            }
        }

        public void Drain()
        {
            var connection = _connection;
            if (connection == null) return;
            if (Interlocked.Exchange(ref connection.Overflow, 0) != 0) Disconnect();
            for (int count = 0; count < 32 && connection.Incoming.TryDequeue(out var envelope); count++)
            {
                Interlocked.Decrement(ref connection.Pending);
                if (_connection != connection) return;
                switch (envelope.Type)
                {
                    case "history":
                        _messages.Clear();
                        _latestMessageId = 0;
                        var history = envelope.Messages ?? Array.Empty<LobbyChatMessage>();
                        for (int index = Math.Max(0, history.Length - MAXIMUM_HISTORY); index < history.Length; index++) AddMessage(history[index]);
                        MemberCount = Math.Max(0, envelope.MemberCount);
                        IsConnected = true;
                        _statusSource = "";
                        Changed?.Invoke();
                        break;
                    case "chat":
                        AddMessage(envelope.Message);
                        MemberCount = Math.Max(0, envelope.MemberCount);
                        if (_pendingMessage != null && envelope.RequestId == _pendingMessage.RequestId)
                            _pendingMessage.Completion.TrySetResult(true);
                        Changed?.Invoke();
                        break;
                    case "presence":
                        MemberCount = Math.Max(0, envelope.MemberCount);
                        Changed?.Invoke();
                        break;
                    case "notice": HandleNotice(envelope); break;
                    case "disconnected": Disconnect(); break;
                }
            }
        }

        private void AddMessage(LobbyChatMessage message)
        {
            if (message == null || message.Id <= _latestMessageId || string.IsNullOrEmpty(message.AccountId)
                || message.AccountId.Length > 128 || string.IsNullOrEmpty(message.DisplayName) || message.DisplayName.Length > 64
                || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > MAXIMUM_TEXT_LENGTH) return;
            _latestMessageId = message.Id;
            _messages.Add(message);
            if (_messages.Count > MAXIMUM_HISTORY) _messages.RemoveAt(0);
        }

        private void HandleNotice(LobbyChatEnvelope envelope)
        {
            string source = envelope.Code == "InvalidChat" ? "채팅은 1~200자로 입력하세요."
                : envelope.Code == "ChatRateLimited" ? "메시지를 너무 빠르게 보내고 있습니다."
                : envelope.Code == "InvalidSession" ? "로그인이 만료되었습니다. 다시 로그인해 주세요."
                : "채팅을 보내지 못했습니다. 다시 시도하세요.";
            if (envelope.Code == "InvalidSession")
            {
                _invalidSession = true;
                IsConnected = false;
                MemberCount = 0;
                _statusSource = source;
                FailPending(source);
                Changed?.Invoke();
            }
            if (_pendingMessage != null && envelope.RequestId == _pendingMessage.RequestId)
                _pendingMessage.Completion.TrySetException(Error(source));
            else Notice?.Invoke(DrawLocalization.Text(source));
        }

        private void Disconnect()
        {
            IsConnected = false;
            MemberCount = 0;
            _statusSource = "채팅 연결이 끊겼습니다. 다시 연결 중…";
            FailPending("채팅을 보내지 못했습니다. 다시 시도하세요.");
            Changed?.Invoke();
        }

        private void FailPending(string source) => _pendingMessage?.Completion.TrySetException(Error(source));
        private static InvalidOperationException Error(string source) => new InvalidOperationException(DrawLocalization.Text(source));

        private async Task ConnectLoopAsync(Connection connection, Uri uri, string certificatePin, byte[] authentication)
        {
            int retrySeconds = 2;
            try
            {
                while (!connection.Cancellation.IsCancellationRequested && !connection.InvalidSession)
                {
                    WebSocket socket = null;
                    try
                    {
                        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(connection.Cancellation.Token))
                        {
                            attempt.CancelAfter(TimeSpan.FromSeconds(20));
                            socket = await DrawWebSocketClient.ConnectAsync(uri, certificatePin, attempt.Token);
                            connection.Cancellation.Token.ThrowIfCancellationRequested();
                            connection.Socket = socket;
                            await SendFrameAsync(socket, authentication, attempt.Token);
                            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            var receiving = ReceiveAsync(socket, connection, ready, attempt.Token);
                            try
                            {
                                using (attempt.Token.Register(() => ready.TrySetCanceled()))
                                    if (await ready.Task)
                                    {
                                        attempt.CancelAfter(Timeout.InfiniteTimeSpan);
                                        retrySeconds = 2;
                                    }
                                await receiving;
                            }
                            finally
                            {
                                attempt.Cancel();
                                socket.Abort();
                                await receiving;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        if (!connection.Cancellation.IsCancellationRequested && !connection.InvalidSession)
                            Enqueue(connection, new LobbyChatEnvelope { Type = "disconnected" });
                    }
                    finally
                    {
                        if (connection.Socket == socket) connection.Socket = null;
                        socket?.Dispose();
                    }
                    if (connection.Cancellation.IsCancellationRequested || connection.InvalidSession) break;
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), connection.Cancellation.Token);
                    retrySeconds = Math.Min(retrySeconds * 2, 30);
                }
            }
            catch (OperationCanceledException) { }
            finally { connection.Cancellation.Dispose(); }
        }

        private async Task SendFrameAsync(WebSocket socket, byte[] bytes, CancellationToken cancellation)
        {
            await _sendLock.WaitAsync(cancellation);
            try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation); }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveAsync(WebSocket socket, Connection connection, TaskCompletionSource<bool> ready, CancellationToken cancellation)
        {
            var buffer = new byte[8192];
            try
            {
                while (!cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using (var frame = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                            if (result.MessageType == WebSocketMessageType.Close) throw new IOException();
                            if (result.MessageType != WebSocketMessageType.Text || frame.Length + result.Count > MAXIMUM_FRAME_BYTES) throw new IOException();
                            frame.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        var envelope = JsonUtility.FromJson<LobbyChatEnvelope>(Encoding.UTF8.GetString(frame.ToArray()));
                        if (envelope == null || !Enqueue(connection, envelope)) throw new IOException();
                        if (envelope.Type == "history") ready.TrySetResult(true);
                        else if (envelope.Type == "notice" && envelope.Code == "InvalidSession")
                        {
                            connection.InvalidSession = true;
                            ready.TrySetResult(false);
                            return;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is WebSocketException || exception is IOException
                || exception is OperationCanceledException || exception is ObjectDisposedException || exception is ArgumentException)
            {
                ready.TrySetCanceled();
                if (!cancellation.IsCancellationRequested && !connection.InvalidSession)
                    Enqueue(connection, new LobbyChatEnvelope { Type = "disconnected" });
            }
        }

        private static bool Enqueue(Connection connection, LobbyChatEnvelope envelope)
        {
            if (connection.Cancellation.IsCancellationRequested) return false;
            if (Interlocked.Increment(ref connection.Pending) > MAXIMUM_PENDING_FRAMES)
            {
                Interlocked.Decrement(ref connection.Pending);
                Interlocked.Exchange(ref connection.Overflow, 1);
                connection.Socket?.Abort();
                return false;
            }
            connection.Incoming.Enqueue(envelope);
            return true;
        }

        public void Dispose() => Stop(true);
    }
}
