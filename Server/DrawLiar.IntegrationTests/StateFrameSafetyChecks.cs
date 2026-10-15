using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyStateFrameSafetyAsync()
    {
        var korean = GameConnection.Serialize(new GameplayEnvelope { Type = "notice", Text = "한글과 <태그>를 안전하게 전달해요." });
        string json = Encoding.UTF8.GetString(korean);
        Check(json.Contains("한글", StringComparison.Ordinal) && json.Contains("\\u003C", StringComparison.Ordinal)
            && JsonSerializer.Deserialize<GameplayEnvelope>(korean, GameplayWire.Json)!.Text == "한글과 <태그>를 안전하게 전달해요.",
            "전송 인코더는 한글을 UTF-8로 유지하면서 HTML 민감 문자를 이스케이프해야 합니다.");
        var socket = new FrameSafetySocket();
        await using var connection = new GameConnection(socket, "frame-account", "frame-token", CancellationToken.None);
        Check(!connection.Queue(new GameplayEnvelope { Type = "notice", Text = new string('가', 40000) }) && connection.IsAlive,
            "크기를 넘는 서버 메시지는 연결을 종료하지 않고 거부해야 합니다.");
        Check(connection.Queue(new GameplayEnvelope { Type = "notice", Text = "다음 메시지" }),
            "크기를 넘는 메시지 뒤에도 정상 메시지를 전송할 수 있어야 합니다.");
        await WaitAsync(() => socket.SentCount > 0, 3);

        DedicatedRoom.ValidateStateSize(new RoomSettings { Topics = ["과일"] }, ["과일", "동물"]);
        string[] catalog = Enumerable.Range(0, 800).Select(index => index.ToString("D3") + new string('\'', 37)).ToArray();
        bool rejected = false;
        try { DedicatedRoom.ValidateStateSize(new RoomSettings { Topics = catalog.Take(128).ToArray() }, catalog); }
        catch (ApiException exception) { rejected = exception.Code == "RoomStateTooLarge" && exception.Status == 400; }
        Check(rejected, "최대 참가자 데이터와 주제 목록을 합친 크기가 넘는 설정은 승인 전에 거부해야 합니다.");

        var authoritative = new RoomSettings { Topics = catalog.Take(128).ToArray() };
        var legacy = new RoomSnapshot
        {
            Settings = authoritative.Copy(), AvailableTopics = catalog,
            Phase = GamePhase.RoundResults, Round = 3, Word = "정답", Summary = "경기 진행 유지",
            Players = Enumerable.Range(1, 64).Select(index => new PlayerView
            {
                Id = index, AccountId = Guid.NewGuid().ToString(), Name = new string('\'', 16),
                Guess = index < GameRules.MAX_PLAYERS ? new string('\'', 40) : "", Score = index, IsConnected = index <= 40,
                Cosmetics = new CosmeticLoadout { NicknameEffect = "nickname-sunset", AvatarBackground = "background-starlight",
                    AvatarFrame = "frame-crystal", ChatBubble = "bubble-cloud" }
            }).ToArray()
        };
        byte[]? frame = DedicatedRoom.SerializeState(legacy, 123, out bool reduced);
        Check(reduced && frame != null && frame.Length <= GameplayWire.MAX_FRAME_BYTES,
            "큰 기존 설정도 참가자 이력을 포함한 유효 상태를 프레임 상한 안에 전송해야 합니다.");
        var restored = JsonSerializer.Deserialize<GameplayEnvelope>(frame!, GameplayWire.Json)!;
        Check(restored.Type == "state" && restored.Sequence == 123 && restored.State.Phase == GamePhase.RoundResults
            && restored.State.Round == 3 && restored.State.Word == "정답" && restored.State.Players.Length == 64
            && restored.State.Players.Select(player => player.Score).SequenceEqual(Enumerable.Range(1, 64))
            && authoritative.Topics.Length == 128 && authoritative.Topics.SequenceEqual(catalog.Take(128)),
            "주제 표시 범위 축소는 서버의 선택 설정·경기 진행·참가자 점수를 보존해야 합니다.");
        Check(connection.QueueSerialized(frame!) && connection.IsAlive,
            "축소한 기존 방 상태를 같은 연결에 전달할 수 있어야 합니다.");

        var builtIn = new GameData { Topics = catalog.Select(name => new TopicData { Name = name, Words = ["사과", "배"] }).ToArray() };
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "host",
            Settings = new ServerRoomSettings { Topics = [catalog[0]] } };
        var room = new DedicatedRoom(roomData, builtIn, [], 0, () => { });
        var hostSocket = new FrameSafetySocket();
        await using var host = new GameConnection(hostSocket, "host", "frame-token", CancellationToken.None);
        Check(room.Join(host, new RedeemTicketResponse { AccountId = "host", Room = roomData,
            Profile = new ProfileData { AccountId = "host", DisplayName = "방장" } }, 0), "크기 초과 기존 방에 입장할 수 있어야 합니다.");
        await WaitAsync(() => hostSocket.StateCount > 0, 3);
        room.Tick(2);
        await WaitAsync(() => hostSocket.StateCount > 1, 3);
        Check(host.IsAlive && hostSocket.LargestFrame <= GameplayWire.MAX_FRAME_BYTES && hostSocket.NoticeCount == 1,
            "큰 기존 주제 목록은 연결과 주기 상태 전송을 유지하고 안내를 한 번만 전달해야 합니다.");
        Report("게임 상태 최대 인원 크기 검사·안전한 한글 인코딩·기존 초과 상태 축소·연결 유지 검증");
    }

    private sealed class FrameSafetySocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private int _sentCount, _stateCount, _noticeCount, _largestFrame;
        public int SentCount => Volatile.Read(ref _sentCount);
        public int StateCount => Volatile.Read(ref _stateCount);
        public int NoticeCount => Volatile.Read(ref _noticeCount);
        public int LargestFrame => Volatile.Read(ref _largestFrame);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken cancellationToken)
        {
            var message = JsonSerializer.Deserialize<GameplayEnvelope>(buffer.AsSpan(), GameplayWire.Json)!;
            Interlocked.Increment(ref _sentCount);
            if (message.Type == "state") Interlocked.Increment(ref _stateCount);
            if (message.Type == "notice") Interlocked.Increment(ref _noticeCount);
            Interlocked.Exchange(ref _largestFrame, Math.Max(LargestFrame, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
