using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyRoomKickWireAsync()
    {
        var sockets = Enumerable.Range(0, 6).Select(_ => new KickSocket()).ToArray();
        var peers = sockets.Select((socket, index) => new GameConnection(socket, "kick" + index, "test", CancellationToken.None)).ToList();
        var data = new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } };
        var model = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "kick0", Settings = new ServerRoomSettings { Topics = new[] { "과일" } } };
        var room = new DedicatedRoom(model, data, [], RoomRegistry.Now, () => { });
        RedeemTicketResponse Ticket(int index) => new() { AccountId = "kick" + index, Room = model, SpectatorOnly = index >= 4,
            Profile = new ProfileData { AccountId = "kick" + index, DisplayName = "강퇴검증" + index } };
        int persisted = 0;
        Task<KickRoomResponse> Persist(KickRoomRequest request, CancellationToken _) { persisted++; return Task.FromResult(new KickRoomResponse
            { RoomId = request.RoomId, AccountId = request.TargetAccountId, OperationId = request.OperationId }); }
        async Task<GameplayEnvelope> Kick(GameConnection actor, KickSocket socket, int target, string? requestId = null,
            Func<KickRoomRequest, CancellationToken, Task<KickRoomResponse>>? persist = null)
        {
            string id = requestId ?? Guid.NewGuid().ToString();
            int previousReplies = socket.Frames.Count(frame => frame.Type == "kickResult" && frame.RequestId == id);
            await room.KickAsync(actor, new GameplayEnvelope { Type = "request", Kind = "kick", Target = target, RequestId = id }, RoomRegistry.Now, persist ?? Persist);
            await WaitAsync(() => socket.Frames.Count(frame => frame.Type == "kickResult" && frame.RequestId == id) > previousReplies, 3);
            return socket.Frames.Last(frame => frame.Type == "kickResult" && frame.RequestId == id);
        }
        try
        {
            for (int index = 0; index < 6; index++) Check(room.Join(peers[index], Ticket(index), RoomRegistry.Now), "강퇴 검증 참가자/관전자가 입장해야 합니다.");
            Check(!(await Kick(peers[1], sockets[1], 3)).Accepted && !(await Kick(peers[0], sockets[0], 1)).Accepted
                && (await Kick(peers[0], sockets[0], 3, "invalid")).Code == "InvalidOperation" && persisted == 0,
                "비방장·자기 자신·잘못된 operation은 DB 호출 없이 거부해야 합니다.");
            room.Disconnect(peers[5], RoomRegistry.Now);
            Check((await Kick(peers[0], sockets[0], 6)).Code == "RoomKickTargetUnavailable" && persisted == 0, "미연결 대상은 거부해야 합니다.");
            string receipt = Guid.NewGuid().ToString();
            Check((await Kick(peers[0], sockets[0], 5, receipt)).Accepted, "방장은 관전자를 강퇴할 수 있어야 합니다.");
            await WaitAsync(() => sockets[4].State == WebSocketState.CloseSent, 3);
            Check(sockets[4].Frames.Last().Type == "kicked" && sockets[4].Frames.Last().Code == "RoomKicked"
                && sockets[4].CloseStatus == WebSocketCloseStatus.PolicyViolation && sockets[4].CloseStatusDescription == "RoomKicked"
                && !room.Status().SpectatorAccountIds.Contains("kick4"), "관전자는 kicked JSON을 마지막으로 받은 뒤 표준 종료되고 좌석을 해제해야 합니다.");
            Check((await Kick(peers[0], sockets[0], 5, receipt)).Accepted && persisted == 1
                && (await Kick(peers[0], sockets[0], 3, receipt)).Code == "InvalidOperation", "동일 operation 재요청은 원래 결과를 반환하고 다른 대상에 재사용할 수 없어야 합니다.");
            await using var blocked = new GameConnection(new KickSocket(), "kick4", "test", CancellationToken.None);
            Check(!room.Join(blocked, Ticket(4), RoomRegistry.Now), "강퇴된 관전자는 오래된 입장 응답으로도 같은 방에 재접속할 수 없어야 합니다.");
            var replacementSocket = new KickSocket();
            var replacement = new GameConnection(replacementSocket, "kick0", "test", CancellationToken.None); peers.Add(replacement);
            Check(room.Join(replacement, Ticket(0), RoomRegistry.Now), "현재 방장 계정의 재연결을 준비해야 합니다.");
            int calls = persisted;
            await room.KickAsync(peers[0], new GameplayEnvelope { Type = "request", Kind = "kick", Target = 3, RequestId = Guid.NewGuid().ToString() }, RoomRegistry.Now, Persist);
            Check(persisted == calls, "교체된 이전 연결의 강퇴 요청은 처리하면 안 됩니다.");
            GameConnection moderatedTarget = peers[2];
            foreach (var failure in new (Exception Error, string Code)[] { (new ApiException("RoomKickDenied", 403), "RoomKickDenied"), (new OperationCanceledException(), "ServerUnavailable") })
            {
                var denied = await Kick(replacement, replacementSocket, 3, persist: (_, _) =>
                {
                    moderatedTarget.AbortInvalidSession();
                    Check(moderatedTarget.IsAlive, "pending 중 invalid 결과는 terminal/실패 확정까지 유예해야 합니다.");
                    return Task.FromException<KickRoomResponse>(failure.Error);
                });
                Check(!denied.Accepted && denied.Code == failure.Code && room.Status().PlayerAccountIds.Contains("kick2"), "DB 거부/timeout은 대상과 좌석을 보존해야 합니다.");
                Check(!moderatedTarget.IsAlive, "DB 거부/timeout 후에는 기록한 invalid 결과로 즉시 Abort를 재개해야 합니다.");
                moderatedTarget = new GameConnection(new KickSocket(), "kick2", "test", CancellationToken.None); peers.Add(moderatedTarget);
                Check(room.Join(moderatedTarget, Ticket(2), RoomRegistry.Now), "실패한 강퇴는 재연결을 차단하면 안 됩니다.");
            }
            var ready = new TaskCompletionSource<KickRoomRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource<KickRoomResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            string pendingId = Guid.NewGuid().ToString();
            var pending = room.KickAsync(replacement, new GameplayEnvelope { Type = "request", Kind = "kick", Target = 3, RequestId = pendingId }, RoomRegistry.Now,
                (request, _) => { ready.TrySetResult(request); return complete.Task; });
            var captured = await ready.Task;
            moderatedTarget.AbortInvalidSession();
            Check(moderatedTarget.IsAlive, "DB 강퇴 확정과 session-validation이 겹쳐도 terminal 전송 전에 소켓을 Abort하면 안 됩니다.");
            await using var pendingTarget = new GameConnection(new KickSocket(), "kick2", "test", CancellationToken.None);
            Check(!room.Join(pendingTarget, Ticket(2), RoomRegistry.Now), "강퇴 커밋 중 대상 연결 교체를 막아야 합니다.");
            Check((await Kick(replacement, replacementSocket, 4)).Code == "RoomKickPending", "동시 강퇴는 명시적인 pending 응답으로 거부해야 합니다.");
            room.Disconnect(replacement, RoomRegistry.Now);
            Check(room.Status().OwnerAccountId == "kick0", "비동기 권위 작업 중 방장 이전을 직렬화해야 합니다.");
            complete.SetResult(new KickRoomResponse { RoomId = captured.RoomId, AccountId = captured.TargetAccountId, OperationId = captured.OperationId });
            await pending;
            await WaitAsync(() => replacementSocket.Frames.Any(frame => frame.Type == "kickResult" && frame.RequestId == pendingId), 3);
            Check(replacementSocket.Frames.Last(frame => frame.Type == "kickResult" && frame.RequestId == pendingId).Code == "RoomKickDenied"
                && room.Status().OwnerAccountId == "kick1" && !room.Status().PlayerAccountIds.Contains("kick2"),
                "await 후 떠난 actor에게 성공 권한을 주지 않고 DB에서 확정한 차단과 새 방장을 정합적으로 적용해야 합니다.");
            Check((await Kick(peers[1], sockets[1], 4)).Accepted, "이전된 새 방장은 남은 참가자를 강퇴할 수 있어야 합니다.");
            var formerSocket = new KickSocket(); var former = new GameConnection(formerSocket, "kick0", "test", CancellationToken.None); peers.Add(former);
            Check(room.Join(former, Ticket(0), RoomRegistry.Now) && (await Kick(former, formerSocket, peers[1].PlayerId)).Code == "RoomKickDenied",
                "재연결한 이전 방장은 권한을 되찾으면 안 됩니다.");
            await VerifyKickMatchFlowAsync(data);
            Report("강퇴 wire 권한·GUID·관전자·terminal/close순서·중복·교체연결·pending/await·방장 이전·경기/전적 보존 검증");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }

    private static async Task VerifyKickMatchFlowAsync(GameData data)
    {
        var sockets = Enumerable.Range(0, 5).Select(_ => new KickSocket()).ToArray();
        var peers = sockets.Select((socket, index) => new GameConnection(socket, "matchkick" + index, "test", CancellationToken.None)).ToArray();
        var model = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "matchkick0", NodeId = "kick-qa",
            Settings = new ServerRoomSettings { Topics = new[] { "과일" }, RoundCount = 1, RoleSeconds = 3, DrawSeconds = 5, DiscussionSeconds = 5,
                RebuttalSeconds = 0, VoteSeconds = 5, RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5 } };
        MatchResultRequest? completed = null;
        double now = RoomRegistry.Now;
        var room = new DedicatedRoom(model, data, [], now, () => { }, matchCompleted: result => completed = result);
        async Task<RoomSnapshot> State(Func<RoomSnapshot, bool> predicate)
        {
            await WaitAsync(() => sockets[0].Latest is { } snapshot && predicate(snapshot), 3);
            return sockets[0].Latest!;
        }
        void Send(int actor, string kind, int target = 0, int ballot = 0) => room.Receive(peers[actor - 1],
            new GameplayEnvelope { Type = "request", Kind = kind, Target = target, BallotVersion = ballot }, now += .05);
        async Task Kick(int target) => await room.KickAsync(peers[0],
            new GameplayEnvelope { Type = "request", Kind = "kick", Target = target, RequestId = Guid.NewGuid().ToString() }, now,
            (request, _) => Task.FromResult(new KickRoomResponse { RoomId = request.RoomId, AccountId = request.TargetAccountId, OperationId = request.OperationId }));
        try
        {
            for (int index = 0; index < 5; index++) Check(room.Join(peers[index], new RedeemTicketResponse { Room = model, AccountId = peers[index].AccountId,
                Profile = new ProfileData { AccountId = peers[index].AccountId, DisplayName = "경기강퇴" + index } }, now), "경기 강퇴 참가자를 준비해야 합니다.");
            Send(1, "start"); var state = await State(value => value.Phase == GamePhase.RoleReveal);
            room.Tick(now += state.RemainingSeconds + .01); state = await State(value => value.Phase == GamePhase.Drawing);
            if (state.ArtistId == 1) { Send(1, "endTurn"); state = await State(value => value.Phase == GamePhase.Drawing && value.ArtistId != 1); }
            int artist = state.ArtistId; await Kick(artist);
            state = await State(value => value.Phase == GamePhase.Drawing && value.ArtistId != artist);
            Check(state.Players.All(player => player.Id != artist) && !room.Status().PlayerAccountIds.Contains(peers[artist - 1].AccountId),
                "그리는 참가자를 강퇴하면 좌석을 해제하고 다음 차례로 진행해야 합니다.");
            while (state.Phase == GamePhase.Drawing)
            {
                int previous = state.ArtistId; Send(previous, "endTurn");
                state = await State(value => value.Phase != GamePhase.Drawing || value.ArtistId != previous);
            }
            Check(state.Phase == GamePhase.Discussion, "그리기 완료 후 토론으로 진행해야 합니다.");
            int nominee = state.Players.First(player => player.Id != 1 && player.IsConnected).Id;
            var voters = state.Players.Where(player => player.IsConnected && player.Id != nominee).ToArray();
            foreach (var voter in voters) Send(voter.Id, "vote", nominee, state.BallotVersion);
            await State(value => value.Players.Single(player => player.Id == nominee).VoteCount == voters.Length);
            await Kick(nominee);
            state = await State(value => value.Phase == GamePhase.Discussion && value.Players.Where(player => player.IsConnected).All(player => !player.HasVoted));
            Check(state.Players.All(player => !player.IsLiar) && state.RevealedLiarCount == -1,
                "지목 대상 강퇴는 해당 표를 회수하면서 역할을 조기에 공개하면 안 됩니다.");
            for (int step = 0; step < 30 && state.Phase != GamePhase.MatchResults; step++)
            {
                room.Tick(now += Math.Max(.1, state.RemainingSeconds + .01));
                await WaitAsync(() => sockets[0].Latest != state, 3); state = sockets[0].Latest!;
            }
            Check(state.Phase == GamePhase.MatchResults && completed?.Players.Length == 5
                && completed.Players.Any(player => player.AccountId == peers[artist - 1].AccountId)
                && completed.Players.Any(player => player.AccountId == peers[nominee - 1].AccountId),
                "강퇴한 참가자의 라운드 참가·전적/보상 입력은 완료 경기 기록에 보존해야 합니다.");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }

    private sealed class KickSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private RoomSnapshot? _latest;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeDescription;
        public ConcurrentQueue<GameplayEnvelope> Frames { get; } = new();
        public RoomSnapshot? Latest => Volatile.Read(ref _latest);
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) => CloseOutputAsync(status, description, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        { _closeStatus = status; _closeDescription = description; _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromCanceled<WebSocketReceiveResult>(new CancellationToken(true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken cancellationToken)
        {
            var envelope = JsonSerializer.Deserialize<GameplayEnvelope>(buffer.AsSpan(), GameplayWire.Json)!;
            Frames.Enqueue(envelope);
            if (envelope.Type == "state") Volatile.Write(ref _latest, envelope.State);
            return Task.CompletedTask;
        }
    }
}
