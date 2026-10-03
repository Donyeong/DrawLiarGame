using System.Net.WebSockets;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyRoomPolicyAsync()
    {
        for (int index = 0; index < 500; index++)
        {
            string code = RoomCodes.Create();
            Check(code.Length == 6 && code.All(RoomCodes.ALPHABET.Contains), "방 코드는 구분하기 쉬운 여섯 글자여야 합니다.");
            Check(RoomCodes.Normalize(" " + code[..3].ToLowerInvariant() + "- " + code[3..].ToLowerInvariant() + " ") == code,
                "방 코드의 대소문자·공백·하이픈을 정규화해야 합니다.");
        }
        foreach (string invalid in new[] { "ABC01D", "ABCI23", "ABCL23", "ABCO23", "ABC1234", "ABC!23" })
        {
            bool rejected = false;
            try { RoomCodes.Normalize(invalid); }
            catch (ApiException) { rejected = true; }
            Check(rejected, "잘못되거나 모호한 코드를 거부해야 합니다.");
        }

        var roomData = new ServerRoomData
        {
            RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "host",
            Settings = new ServerRoomSettings { RoomName = "정책검증", MaxPlayers = 8, Topics = new[] { "과일" } }
        };
        var data = new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } };
        int notifications = 0;
        var room = new DedicatedRoom(roomData, data, Array.Empty<ServerTopicData>(), 0, () => notifications++);
        RedeemTicketResponse Ticket(string account, bool spectator = false) => new()
        {
            AccountId = account, AdmissionId = ServerRuntime.Hash(Guid.NewGuid().ToString()), Room = roomData, IsSpectator = spectator, SpectatorOnly = spectator,
            Profile = new ProfileData { AccountId = account, DisplayName = account }
        };
        await using var host = new GameConnection(new PolicySocket(), "host", "test", CancellationToken.None);
        await using var observer = new GameConnection(new PolicySocket(), "observer", "test", CancellationToken.None);
        Check(room.Join(host, Ticket("host"), 0), "정책 검증 참가가 가능해야 합니다.");
        string[] acknowledgedIds = room.Status().AdmissionIds;
        Check(room.Join(observer, Ticket("observer", true), 0), "정책 검증 관전이 가능해야 합니다.");
        room.AcknowledgeAdmissions(acknowledgedIds);
        Check(room.Status().AdmissionIds.Length == 1 && !room.Status().AdmissionIds.Intersect(acknowledgedIds).Any(),
            "heartbeat 성공 확인은 보낸 입장 ID만 제거하고 병렬로 추가된 ID는 보존해야 합니다.");
        await host.ReceiveAsync();
        room.Disconnect(host, 1);
        Check(!room.Status().Closed && room.Status().SpectatorCount == 1, "마지막 참가자 퇴장 후에도 관전자가 있으면 방을 유지해야 합니다.");
        await observer.ReceiveAsync();
        room.Disconnect(observer, 2);
        Check(room.Status().Closed, "마지막 관전자의 정상 퇴장은 즉시 방 종료를 요청해야 합니다.");
        await using var reconnect = new GameConnection(new PolicySocket(), "host", "test", CancellationToken.None);
        Check(room.Join(reconnect, Ticket("host"), 3) && !room.Status().Closed, "종료 승인 전 유효한 입장권으로 방을 복원할 수 있어야 합니다.");
        reconnect.Abort();
        room.Disconnect(reconnect, 4);
        Check(!room.Tick(123.9) && room.Tick(124), "비정상 단절은 120초 재접속 유예 후에 종료해야 합니다.");
        Check(notifications >= 6, "명단·종료 변화는 게임서버에 즉시 전달해야 합니다.");
        var match = new DedicatedRoom(roomData, data, Array.Empty<ServerTopicData>(), 0, () => { });
        await using var interrupted = new GameConnection(new PolicySocket(), "host", "test", CancellationToken.None);
        await using var leftFirst = new GameConnection(new PolicySocket(), "first", "test", CancellationToken.None);
        await using var leftLast = new GameConnection(new PolicySocket(), "last", "test", CancellationToken.None);
        Check(match.Join(interrupted, Ticket("host"), 0) && match.Join(leftFirst, Ticket("first"), 0)
            && match.Join(leftLast, Ticket("last"), 0), "경기 중 재접속 정책을 준비해야 합니다.");
        match.Receive(interrupted, new GameplayEnvelope { Type = "request", Kind = "start" }, 0.1);
        interrupted.Abort();
        match.Disconnect(interrupted, 1);
        await leftFirst.ReceiveAsync();
        match.Disconnect(leftFirst, 2);
        await leftLast.ReceiveAsync();
        match.Disconnect(leftLast, 3);
        Check(!match.Status().Closed && !match.Tick(120.9), "다른 사람의 정상 퇴장이 장애 참가자의 재접속 유예를 취소하면 안 됩니다.");
        await using var recovered = new GameConnection(new PolicySocket(), "host", "test", CancellationToken.None);
        Check(match.Join(recovered, Ticket("host"), 4), "장애 참가자의 기존 역할로 복원해야 합니다.");
        await recovered.ReceiveAsync();
        match.Disconnect(recovered, 5);
        Check(match.Status().Closed, "복원이 완료된 참가자의 정상 퇴장에는 장애 유예가 남으면 안 됩니다.");
        await VerifyStartAuthorityAsync(data);
        Report("방 코드 입력 정규화·마지막 관전자 종료·종료 요청 중 재입장·120초 재접속 유예 검증");
    }

    private static async Task VerifyStartAuthorityAsync(GameData data)
    {
        var legacySettings = new ServerRoomSettings { MaxPlayers = 3, LiarCount = 7 };
        ServerDatabase.ValidateSettings(legacySettings);
        Check(legacySettings.MaxPlayers == 8 && legacySettings.LiarCount == 7, "게임서버도 이전 정원 입력을 8명으로 정규화해야 합니다.");
        var roomData = new ServerRoomData
        {
            RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "host",
            Settings = new ServerRoomSettings { MaxPlayers = 3, LiarCount = 7, Topics = new[] { "과일" } }
        };
        var room = new DedicatedRoom(roomData, data, Array.Empty<ServerTopicData>(), 0, () => { });
        RedeemTicketResponse Ticket(string account, bool spectator = false) => new()
        {
            AccountId = account, Room = roomData, IsSpectator = spectator, SpectatorOnly = spectator,
            Profile = new ProfileData { AccountId = account, DisplayName = account }
        };
        var hostSocket = new PolicySocket();
        var secondSocket = new PolicySocket();
        var thirdSocket = new PolicySocket();
        await using var host = new GameConnection(hostSocket, "host", "test", CancellationToken.None);
        await using var second = new GameConnection(secondSocket, "second", "test", CancellationToken.None);
        await using var third = new GameConnection(thirdSocket, "third", "test", CancellationToken.None);
        await using var observer = new GameConnection(new PolicySocket(), "observer", "test", CancellationToken.None);
        Check(room.Join(host, Ticket("host"), 0) && room.Join(second, Ticket("second"), 0)
            && room.Join(observer, Ticket("observer", true), 0), "시작 권한 검증의 참가자와 관전자를 준비해야 합니다.");
        room.Receive(host, new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
        Check(!room.Status().IsInProgress && room.Status().PlayerCount == 2 && room.Status().Settings.MaxPlayers == 8,
            "관전자는 시작 인원에 포함하지 않고 정원은 항상 8명이어야 합니다.");
        Check(room.Join(third, Ticket("third"), .2), "세 번째 참가자가 입장해야 합니다.");
        room.Receive(second, new GameplayEnvelope { Type = "request", Kind = "start" }, .3);
        Check(!room.Status().IsInProgress, "세 명이어도 방장 외의 시작 요청은 거부해야 합니다.");
        room.Receive(host, new GameplayEnvelope { Type = "request", Kind = "start" }, .4);
        Check(room.Status().IsInProgress && room.Status().Settings.LiarCount == 2,
            "세 명이면 방장이 시작할 수 있고 라이어 수는 시민 한 명을 남기도록 보정해야 합니다.");
        await WaitAsync(() => hostSocket.LastState?.Phase == GamePhase.RoleReveal
            && secondSocket.LastState?.Phase == GamePhase.RoleReveal && thirdSocket.LastState?.Phase == GamePhase.RoleReveal, 3);
        Check(new[] { hostSocket.LastState!, secondSocket.LastState!, thirdSocket.LastState! }.Count(state => state.LocalIsLiar) == 2,
            "공개된 라이어 수와 실제 개인 역할이 일치해야 합니다.");
        float remaining = hostSocket.LastState!.RemainingSeconds;
        room.Receive(host, new GameplayEnvelope { Type = "request", Kind = "start" }, .5);
        room.Tick(1.6);
        await WaitAsync(() => hostSocket.LastState!.RemainingSeconds < remaining, 3);
        Check(hostSocket.LastState!.Round == 1 && hostSocket.LastState.Phase == GamePhase.RoleReveal
            && Math.Abs(remaining - hostSocket.LastState.RemainingSeconds - 1.2f) < .001f,
            "이미 진행 중인 경기의 시작 요청은 역할이나 라운드를 다시 만들면 안 됩니다.");

        var interrupted = new DedicatedRoom(roomData, data, Array.Empty<ServerTopicData>(), 0, () => { });
        await using var liveHost = new GameConnection(new PolicySocket(), "host", "test", CancellationToken.None);
        await using var liveSecond = new GameConnection(new PolicySocket(), "second", "test", CancellationToken.None);
        await using var deadThird = new GameConnection(new PolicySocket(), "third", "test", CancellationToken.None);
        interrupted.Join(liveHost, Ticket("host"), 0);
        interrupted.Join(liveSecond, Ticket("second"), 0);
        interrupted.Join(deadThird, Ticket("third"), 0);
        deadThird.Abort();
        interrupted.Receive(liveHost, new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
        Check(!interrupted.Status().IsInProgress && interrupted.Status().PlayerCount == 2,
            "단절 처리가 대기 중인 연결을 세 번째 참가자로 세어 시작하면 안 됩니다.");
        Report("8명 고정·최소 3명·방장 시작 권한·진행 중 중복 시작·라이어 수 보정·죽은 연결 제외 검증");
    }

    private sealed class PolicySocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private RoomSnapshot? _lastState;
        public RoomSnapshot? LastState => Volatile.Read(ref _lastState);
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "leave";
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        { _state = WebSocketState.CloseReceived; return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken cancellationToken)
        {
            var envelope = JsonSerializer.Deserialize<GameplayEnvelope>(buffer.AsSpan(), GameplayWire.Json);
            if (envelope?.Type == "state") Volatile.Write(ref _lastState, envelope.State);
            return Task.CompletedTask;
        }
    }
}
