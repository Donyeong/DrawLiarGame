using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class DrawNetworkManager : MonoBehaviour
    {
        private const int MAXIMUM_STROKES = GameRules.MAX_CANVAS_STROKES;
        private const int MAXIMUM_FRAME_BYTES = 1024 * 1024;
        private const int MAXIMUM_PENDING_FRAMES = 512;
        private sealed class IncomingQueue
        {
            public readonly int Generation;
            public readonly ConcurrentQueue<GameplayEnvelope> Frames = new ConcurrentQueue<GameplayEnvelope>();
            public int Pending;
            public string Failure;

            public IncomingQueue(int generation) => Generation = generation;
        }

        private IncomingQueue _incoming = new IncomingQueue(0);
        private readonly List<DrawStroke> _canvas = new List<DrawStroke>();
        private readonly DrawAsyncGate _sendLock = new DrawAsyncGate();
        private WebSocket _socket;
        private CancellationTokenSource _lifetime;
        private TaskCompletionSource<bool> _initialState;
        private int _generation, _pendingSends, _canvasVersion;
        private long _sequence;
        private bool _configuringRoom;
        private bool _refreshingProfile;
        private string _kickRequestId;
        private event Action<GameplayEnvelope> RoomConfigurationReceived;
        private event Action<GameplayEnvelope> ProfileRefreshReceived;
        private event Action<GameplayEnvelope> KickResultReceived;

        public RoomSnapshot State { get; private set; }
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open && State != null;
        public int LocalPlayerId => State?.LocalPlayerId ?? -1;
        public int CanvasVersion => _canvasVersion;
        public bool CanDraw => IsConnected && !State.LocalIsSpectator
            && State.Players.Any(player => player.Id == LocalPlayerId && player.IsConnected && !player.IsSpectator)
            && (State.Phase == GamePhase.Lobby || State.Phase == GamePhase.Drawing && State.ArtistId == LocalPlayerId);
        public event Action<RoomSnapshot> StateChanged;
        public event Action<DrawStroke> StrokeReceived;
        public event Action CanvasCleared;
        public event Action<ChatLine> ChatReceived;
        public event Action<string> Notice;
        public event Action<string> Kicked;

        public async Task ConnectAsync(DedicatedAssignment assignment)
        {
            if (assignment == null || string.IsNullOrEmpty(assignment.JoinTicket)) throw new ArgumentException("방 입장권이 없습니다.");
            Leave();
            var config = OnlineServicesConfig.Load();
            string dedicatedUrl = config.ResolveDedicatedServerUrl(assignment.DedicatedUrl);
            var uri = new Uri(dedicatedUrl);
            if (uri.Scheme != "ws" && uri.Scheme != "wss") throw new ArgumentException("데디케이티드 주소가 올바르지 않습니다.");
            config.ValidateServerUrl(dedicatedUrl);
            int generation = _generation;
            var lifetime = _lifetime = new CancellationTokenSource();
            var ready = _initialState = DrawAsync.Completion<bool>();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    DrawAsync.CancelAfter(timeout, TimeSpan.FromSeconds(20));
                    var socket = await DrawWebSocketClient.ConnectAsync(uri, config.CertificateSha256, timeout.Token);
                    if (generation != _generation) { socket.Dispose(); throw new OperationCanceledException(); }
                    _socket = socket;
                    await SendAsync(new GameplayEnvelope { Type = "hello", Ticket = assignment.JoinTicket, RoomId = assignment.RoomId }, generation, timeout.Token);
                    _ = ReceiveAsync(socket, generation, lifetime.Token);
                    var completed = await Task.WhenAny(ready.Task, DrawAsync.Delay(TimeSpan.FromSeconds(20), lifetime.Token));
                    if (completed != ready.Task) throw new TimeoutException("방 상태를 받지 못했습니다.");
                    await ready.Task;
                }
            }
            catch
            {
                if (generation == _generation) Leave();
                throw;
            }
        }

        public async Task ConfigureRoomAsync(RoomSettings settings, string password = "", ServerTopicData[] customTopics = null)
        {
            if (!CanConfigureRoom(State)) throw new InvalidOperationException(DrawLocalization.Text("방 옵션 변경 권한이 없습니다."));
            if (settings == null || _configuringRoom) throw new InvalidOperationException(DrawLocalization.Text("방 옵션을 저장하지 못했습니다. 다시 시도하세요."));
            var valid = settings.Copy();
            valid.Validate();
            if (valid.Topics == null || valid.Topics.Length == 0) throw new InvalidOperationException(DrawLocalization.Text("주제를 하나 이상 선택하세요."));
            var topics = (customTopics ?? Array.Empty<ServerTopicData>()).Select(topic => topic == null ? null
                : new ServerTopicData { Name = topic.Name, Words = topic.Words?.ToArray() }).ToArray();
            password ??= "";
            if (!valid.IsPrivate) password = "";
            string requestId = Guid.NewGuid().ToString("N");
            var completion = DrawAsync.Completion<bool>();
            void OnState(RoomSnapshot state)
            {
                if (!CanConfigureRoom(state)) completion.TrySetException(new InvalidOperationException(DrawLocalization.Text("방 옵션 변경 권한이 없습니다.")));
            }
            void OnConfigured(GameplayEnvelope message)
            {
                if (message.RequestId != requestId) return;
                if (message.Accepted) completion.TrySetResult(true);
                else completion.TrySetException(new InvalidOperationException(DrawLocalization.Text(string.IsNullOrWhiteSpace(message.Text)
                    ? "방 옵션을 저장하지 못했습니다. 다시 시도하세요." : message.Text)));
            }
            _configuringRoom = true;
            StateChanged += OnState;
            RoomConfigurationReceived += OnConfigured;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    DrawAsync.CancelAfter(timeout, TimeSpan.FromSeconds(10));
                    await SendAsync(new GameplayEnvelope { Type = "request", Kind = "configure", Settings = valid, CustomTopics = topics, Password = password, RequestId = requestId }, _generation, timeout.Token);
                    using (timeout.Token.Register(() => completion.TrySetCanceled())) await completion.Task;
                }
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(DrawLocalization.Text("방 옵션을 저장하지 못했습니다. 다시 시도하세요."));
            }
            finally
            {
                StateChanged -= OnState;
                RoomConfigurationReceived -= OnConfigured;
                _configuringRoom = false;
                password = "";
            }
        }

        private bool CanConfigureRoom(RoomSnapshot state) => IsConnected && state != null && state.IsHost
            && (state.Phase == GamePhase.Lobby || state.Phase == GamePhase.MatchResults);

        public async Task RefreshProfileAsync()
        {
            if (!IsConnected) return;
            if (_refreshingProfile) throw new InvalidOperationException(DrawLocalization.Text("프로필을 불러오지 못했습니다."));
            string requestId = Guid.NewGuid().ToString("N");
            int generation = _generation;
            var completion = DrawAsync.Completion<bool>();
            void OnRefreshed(GameplayEnvelope message)
            {
                if (message.RequestId != requestId || generation != _generation) return;
                if (message.Accepted) completion.TrySetResult(true);
                else completion.TrySetException(new InvalidOperationException(DrawLocalization.Text(string.IsNullOrWhiteSpace(message.Text)
                    ? "프로필을 불러오지 못했습니다." : message.Text)));
            }
            _refreshingProfile = true;
            ProfileRefreshReceived += OnRefreshed;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                DrawAsync.CancelAfter(timeout, TimeSpan.FromSeconds(10));
                await SendAsync(new GameplayEnvelope { Type = "request", Kind = "refreshProfile", RequestId = requestId }, generation, timeout.Token);
                using (timeout.Token.Register(() => completion.TrySetCanceled())) await completion.Task;
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(DrawLocalization.Text("프로필을 불러오지 못했습니다."));
            }
            finally
            {
                ProfileRefreshReceived -= OnRefreshed;
                _refreshingProfile = false;
            }
        }

        public void StartMatch() => Request("start");
        public void EndTurn() => Request("endTurn");
        public void Vote(int id) => Request("vote", target: id, ballotVersion: State?.BallotVersion ?? 0);
        public void Judge(bool approve)
        {
            var state = State;
            if (state == null || !state.HasAccused) return;
            Request("judge", target: state.AccusedPlayerId, approve: approve, ballotVersion: state.BallotVersion);
        }
        public void Guess(string answer) => Request("guess", GameRules.CleanText(answer, 40));
        public void Chat(string text) => Request("chat", GameRules.CleanText(text, 160));
        public void ReturnToLobby() => Request("lobby");

        public bool CanKickPlayer(int playerId) => IsConnected && State.IsHost && playerId > 0 && playerId != State.LocalPlayerId
            && State.Players.Any(player => player.Id == playerId && player.IsConnected);

        public async Task KickPlayerAsync(int playerId)
        {
            if (!IsConnected || !State.IsHost)
                throw new InvalidOperationException(DrawLocalization.Text("강퇴 권한이 없습니다."));
            if (!CanKickPlayer(playerId))
                throw new InvalidOperationException(DrawLocalization.Text("강퇴할 참가자가 방에 없습니다."));
            if (_kickRequestId != null)
                throw new InvalidOperationException(DrawLocalization.Text("강퇴 요청을 처리하고 있습니다. 잠시 기다려 주세요."));
            string requestId = Guid.NewGuid().ToString("N");
            int generation = _generation;
            var completion = DrawAsync.Completion<bool>();
            void OnResult(GameplayEnvelope message)
            {
                if (message.RequestId != requestId || generation != _generation) return;
                if (message.Accepted) completion.TrySetResult(true);
                else completion.TrySetException(new InvalidOperationException(KickFailureMessage(message.Code)));
            }
            _kickRequestId = requestId;
            KickResultReceived += OnResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                DrawAsync.CancelAfter(timeout, TimeSpan.FromSeconds(10));
                await SendAsync(new GameplayEnvelope { Type = "request", Kind = "kick", Target = playerId, RequestId = requestId }, generation, timeout.Token);
                using (timeout.Token.Register(() => completion.TrySetCanceled())) await completion.Task;
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(DrawLocalization.Text("강퇴하지 못했습니다. 다시 시도하세요."));
            }
            catch (Exception exception) when (exception is WebSocketException || exception is IOException || exception is ObjectDisposedException)
            {
                throw new InvalidOperationException(DrawLocalization.Text("강퇴하지 못했습니다. 다시 시도하세요."));
            }
            finally
            {
                KickResultReceived -= OnResult;
                if (_kickRequestId == requestId) _kickRequestId = null;
            }
        }

        private static string KickFailureMessage(string code) => DrawLocalization.Text(code switch
        {
            "RoomKickDenied" => "강퇴 권한이 없습니다.",
            "RoomKickTargetUnavailable" => "강퇴할 참가자가 방에 없습니다.",
            "RoomKickPending" => "강퇴 요청을 처리하고 있습니다. 잠시 기다려 주세요.",
            _ => "강퇴하지 못했습니다. 다시 시도하세요."
        });

        public void SendStroke(DrawStroke stroke)
        {
            if (!CanDraw) return;
            stroke.CanvasVersion = _canvasVersion;
            stroke.AuthorPlayerId = LocalPlayerId;
            if (stroke.Eraser) { stroke.R = stroke.G = stroke.B = 255; stroke.Eraser = false; }
            if (GameRules.ValidStroke(stroke, _canvasVersion)) Send(new GameplayEnvelope { Type = "stroke", Stroke = stroke });
        }

        public void ReplayCanvas()
        {
            CanvasCleared?.Invoke();
            foreach (var stroke in _canvas) StrokeReceived?.Invoke(stroke);
        }

        public void Leave()
        {
            int generation = DrawAsync.Increment(ref _generation);
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
            _socket?.Abort();
            _socket?.Dispose();
            _socket = null;
            _initialState?.TrySetCanceled();
            _initialState = null;
            DrawAsync.Exchange(ref _incoming, new IncomingQueue(generation));
            _sequence = 0;
            _kickRequestId = null;
            _canvas.Clear();
            _canvasVersion = 0;
            ResetDrawingHistory(0, 0, false);
            State = null;
            CanvasCleared?.Invoke();
            StateChanged?.Invoke(null);
        }

        public async Task LeaveAsync()
        {
            int generation = _generation;
            var socket = _socket;
            if (socket != null && socket.State == WebSocketState.Open)
            {
                using (var timeout = new CancellationTokenSource())
                {
                    DrawAsync.CancelAfter(timeout, TimeSpan.FromSeconds(2));
                    try
                    {
                        await _sendLock.WaitAsync(timeout.Token);
                        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token); }
                        finally { _sendLock.Release(); }
                    }
                    catch (Exception exception) when (exception is WebSocketException || exception is OperationCanceledException || exception is ObjectDisposedException) { }
                }
            }
            if (generation == _generation) Leave();
        }

        private void Request(string kind, string text = "", int target = -1, bool approve = false, int ballotVersion = 0)
        {
            if (IsConnected) Send(new GameplayEnvelope { Type = "request", Kind = kind, Text = text, Target = target,
                Approve = approve, BallotVersion = ballotVersion });
        }

        private void Send(GameplayEnvelope envelope)
        {
            if (_lifetime != null) _ = SendObservedAsync(envelope, _generation, _lifetime.Token);
        }

        private async Task SendObservedAsync(GameplayEnvelope envelope, int generation, CancellationToken cancellation)
        {
            try { await SendAsync(envelope, generation, cancellation); }
            catch (Exception exception) when (exception is WebSocketException || exception is IOException || exception is OperationCanceledException || exception is ObjectDisposedException)
            {
                if (generation == _generation && !cancellation.IsCancellationRequested)
                    Enqueue(new GameplayEnvelope { Type = "disconnected", Text = "서버 연결이 끊겼습니다." }, generation);
            }
        }

        private async Task SendAsync(GameplayEnvelope envelope, int generation, CancellationToken cancellation)
        {
            if (DrawAsync.Increment(ref _pendingSends) > MAXIMUM_PENDING_FRAMES)
            {
                DrawAsync.Decrement(ref _pendingSends);
                throw new IOException("송신 대기열이 가득 찼습니다.");
            }
            try
            {
                await _sendLock.WaitAsync(cancellation);
                try
                {
                    var socket = _socket;
                    if (generation != DrawAsync.Read(ref _generation) || socket == null) return;
                    var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(envelope));
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation);
                }
                finally { _sendLock.Release(); }
            }
            finally { DrawAsync.Decrement(ref _pendingSends); }
        }

        private async Task ReceiveAsync(WebSocket socket, int generation, CancellationToken cancellation)
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
                            if (result.MessageType == WebSocketMessageType.Close && result.CloseStatusDescription == "RoomKicked")
                            {
                                Enqueue(new GameplayEnvelope { Type = "kicked", Code = "RoomKicked" }, generation);
                                return;
                            }
                            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("서버가 연결을 종료했습니다.");
                            if (result.MessageType != WebSocketMessageType.Text || frame.Length + result.Count > MAXIMUM_FRAME_BYTES) throw new IOException("서버 메시지가 올바르지 않습니다.");
                            frame.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        var envelope = JsonUtility.FromJson<GameplayEnvelope>(Encoding.UTF8.GetString(frame.ToArray()));
                        if (envelope == null || !Enqueue(envelope, generation)) throw new IOException("수신 대기열이 가득 찼습니다.");
                    }
                }
            }
            catch (Exception exception) when (exception is WebSocketException || exception is IOException || exception is OperationCanceledException || exception is ObjectDisposedException || exception is ArgumentException)
            {
                if (!cancellation.IsCancellationRequested && generation == _generation)
                    Enqueue(new GameplayEnvelope { Type = "disconnected", Text = "서버 연결이 종료되었습니다. 로비에서 다시 참가할 수 있습니다." }, generation);
            }
        }

        private bool Enqueue(GameplayEnvelope envelope, int generation)
        {
            var incoming = DrawAsync.Read(ref _incoming);
            var socket = _socket;
            if (generation != incoming.Generation || generation != DrawAsync.Read(ref _generation)) return false;
            if (DrawAsync.Increment(ref incoming.Pending) > MAXIMUM_PENDING_FRAMES)
            {
                DrawAsync.Decrement(ref incoming.Pending);
                DrawAsync.CompareExchange(ref incoming.Failure, "수신 대기열이 가득 차 연결을 종료했습니다.", null);
                socket?.Abort();
                return false;
            }
            incoming.Frames.Enqueue(envelope);
            return true;
        }

        private void Update()
        {
            var incoming = DrawAsync.Read(ref _incoming);
            var failure = DrawAsync.Exchange(ref incoming.Failure, null);
            if (failure != null && incoming.Generation == DrawAsync.Read(ref _generation))
            {
                _initialState?.TrySetException(new IOException(failure));
                Leave();
                Notice?.Invoke(failure);
                return;
            }
            for (int count = 0; count < 128 && incoming.Frames.TryDequeue(out var message); count++)
            {
                DrawAsync.Decrement(ref incoming.Pending);
                if (incoming.Generation != DrawAsync.Read(ref _generation)) break;
                if (message.Sequence > 0)
                {
                    if (message.Sequence <= _sequence) continue;
                    _sequence = message.Sequence;
                }
                switch (message.Type)
                {
                    case "state":
                        var previousState = State;
                        State = message.State;
                        SynchronizeDrawingState(previousState);
                        var ready = _initialState;
                        StateChanged?.Invoke(State);
                        if (State != null && incoming.Generation == DrawAsync.Read(ref _generation)) ready?.TrySetResult(true);
                        break;
                    case "canvas": ReceiveCanvas(message); break;
                    case "stroke": if (CurrentDrawingFrame(message)) ReceiveStroke(message.Stroke); break;
                    case "clearOwn": ReceiveClearOwn(message); break;
                    case "authorDrawing": ReceiveAuthorDrawing(message); break;
                    case "chat": ChatReceived?.Invoke(message.Line); break;
                    case "notice": Notice?.Invoke(message.Text); break;
                    case "configured": RoomConfigurationReceived?.Invoke(message); break;
                    case "profile-refreshed": ProfileRefreshReceived?.Invoke(message); break;
                    case "kickResult": KickResultReceived?.Invoke(message); break;
                    case "kicked":
                        string kickedNotice = DrawLocalization.Text("방장에 의해 강퇴되었습니다.");
                        _initialState?.TrySetException(new InvalidOperationException(kickedNotice));
                        Kicked?.Invoke(kickedNotice);
                        Leave();
                        Notice?.Invoke(kickedNotice);
                        return;
                    case "disconnected":
                        _initialState?.TrySetException(new IOException(message.Text));
                        Leave();
                        Notice?.Invoke(message.Text);
                        break;
                }
            }
            AdvanceDrawingRequests();
        }

        private void ReceiveStroke(DrawStroke stroke)
        {
            if (!GameRules.ValidStroke(stroke, _canvasVersion) || stroke.AuthorPlayerId <= 0 || _canvas.Count >= MAXIMUM_STROKES
                || !AddAuthorStroke(stroke)) return;
            _canvas.Add(stroke);
            StrokeReceived?.Invoke(stroke);
            AuthorDrawingChanged?.Invoke(stroke.AuthorPlayerId);
        }

        private void OnDestroy() => Leave();
    }
}
