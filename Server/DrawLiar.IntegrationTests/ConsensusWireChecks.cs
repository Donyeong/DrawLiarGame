using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;

internal static partial class Integration
{
    private static async Task VerifyConsensusWireAsync()
    {
        var sockets = Enumerable.Range(0, 5).Select(_ => new PolicySocket()).ToArray();
        var connections = sockets.Select((socket, index) => new GameConnection(socket, "consensus" + index, "test", CancellationToken.None)).ToArray();
        var roomData = new ServerRoomData
        {
            RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "consensus0",
            Settings = new ServerRoomSettings { Topics = new[] { "과일" }, RoundCount = 1, RoleSeconds = 3,
                DrawSeconds = 5, DiscussionSeconds = 30, RebuttalSeconds = 6, VoteSeconds = 8 }
        };
        var room = new DedicatedRoom(roomData,
            new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } },
            Array.Empty<ServerTopicData>(), 0, () => { });
        double now = 0;
        void Send(int index, string kind, int target = 0, int epoch = 0, bool approve = false)
        {
            var envelope = new GameplayEnvelope { Type = "request", Kind = kind, Target = target, BallotVersion = epoch, Approve = approve };
            var received = JsonSerializer.Deserialize<GameplayEnvelope>(JsonSerializer.Serialize(envelope, GameplayWire.Json), GameplayWire.Json)!;
            Check(received.Target == target && received.BallotVersion == epoch && received.Approve == approve,
                "JSON 게임 프레임은 후보·투표 버전·찬반 값을 보존해야 합니다.");
            room.Receive(connections[index], received, now += .2);
        }
        async Task<RoomSnapshot> State(Func<RoomSnapshot, bool> predicate, int index = 0)
        {
            await WaitAsync(() => sockets[index].LastState is { } state && predicate(state), 3);
            return sockets[index].LastState!;
        }
        try
        {
            for (int index = 0; index < connections.Length; index++)
                Check(room.Join(connections[index], new RedeemTicketResponse
                {
                    AccountId = "consensus" + index, Room = roomData, IsSpectator = index == 4, SpectatorOnly = index == 4,
                    Profile = new ProfileData { AccountId = "consensus" + index, DisplayName = "합의검증" + index }
                }, now), "합의 게임의 참가자와 관전자를 준비해야 합니다.");
            await State(state => state.Players.Length == 5);
            await WaitAsync(() => sockets.All(socket => socket.LastState?.HostPlayerId == 1), 3);
            Check(sockets.Count(socket => socket.LastState?.IsHost == true) == 1, "현재 방장 ID는 모든 클라이언트에 같고 방장 상태는 한 명이어야 합니다.");
            Send(0, "start");
            await State(state => state.Phase == GamePhase.RoleReveal);
            room.Tick(now = 100);
            var drawing = await State(state => state.Phase == GamePhase.Drawing);
            while (drawing.Phase == GamePhase.Drawing)
            {
                int artist = drawing.ArtistId;
                Send(artist - 1, "endTurn");
                drawing = await State(state => state.ArtistId != artist || state.Phase != GamePhase.Drawing);
            }
            int initialEpoch = drawing.BallotVersion;
            Check(drawing.Phase == GamePhase.Discussion, "데디케이티드는 그림 뒤 지목 가능한 토론을 보내야 합니다.");
            Send(0, "vote", 2, initialEpoch - 1);
            Check(!sockets[0].LastState!.Players.Single(player => player.Id == 1).HasVoted, "지난 버전의 지목 패킷은 거부해야 합니다.");
            Send(4, "vote", 1, initialEpoch);
            Check(!sockets[0].LastState!.Players.Single(player => player.Id == 5).HasVoted, "관전자의 현재 버전 지목도 거부해야 합니다.");
            Send(0, "vote", 2, initialEpoch);
            await State(state => state.Players.Single(player => player.Id == 1).HasVoted && state.LocalVoteTargetId == 2);
            Send(0, "vote", 3, initialEpoch);
            await WaitAsync(() => sockets.All(socket => socket.LastState is { Phase: GamePhase.Discussion } state
                && state.Players.Single(player => player.Id == 2).VoteCount == 0
                && state.Players.Single(player => player.Id == 3).VoteCount == 1), 3);
            Check(sockets[0].LastState!.LocalVoteTargetId == 3
                && sockets.Skip(1).All(socket => socket.LastState!.LocalVoteTargetId == -1),
                "재지목은 이전 표를 대체하고 본인에게만 확정된 대상을 전달해야 합니다.");
            for (int index = 1; index < 3; index++) Send(index, "vote", 1, initialEpoch);
            var voting = await State(state => state.Phase == GamePhase.Discussion
                && state.Players.Count(player => player.IsConnected && !player.IsSpectator && player.HasVoted) == 3);
            await WaitAsync(() => sockets.Select((socket, index) => socket.LastState is { } state
                && state.LocalVoteTargetId == (index == 0 ? 3 : index == 3 || index == 4 ? -1 : 1)).All(value => value), 3);
            Check(sockets.All(socket => socket.LastState is { Phase: GamePhase.Discussion } state
                && state.BallotVersion == initialEpoch && state.RemainingSeconds > 0
                && state.Players.Single(player => player.Id == 1).VoteCount == 2
                && state.Players.Single(player => player.Id == 2).VoteCount == 0
                && state.Players.Single(player => player.Id == 3).VoteCount == 1),
                "모든 연결은 마지막 표만 집계하고 유효 지목을 하지 않은 참가자가 있으면 토론을 유지해야 합니다.");
            double deadline = now + voting.RemainingSeconds;
            Send(0, "vote", 3, initialEpoch);
            Send(3, "vote", 1, initialEpoch);
            var rebuttal = await State(state => state.Phase == GamePhase.Rebuttal);
            int judgmentEpoch = rebuttal.BallotVersion;
            Check(rebuttal.HasAccused && rebuttal.AccusedPlayerId == 1 && judgmentEpoch > initialEpoch
                && rebuttal.JudgmentVoterCount == 3 && rebuttal.JudgmentVotesCast == 0
                && rebuttal.LocalVoteTargetId == 3
                && now < deadline && rebuttal.RemainingSeconds == 6
                && rebuttal.Players.All(player => !player.IsLiar && !player.IsCaught && player.Score == 0 && player.RoundPoints == 0),
                "마지막 유효표를 받으면 원래 마감 전이라도 비밀 유지 반론과 새 버전을 즉시 열어야 합니다.");
            await WaitAsync(() => sockets.All(socket => socket.LastState is { Phase: GamePhase.Rebuttal } state
                && state.AccusedPlayerId == 1 && state.BallotVersion == judgmentEpoch), 3);
            Send(0, "vote", 2, initialEpoch);
            Check(sockets[0].LastState!.LocalVoteTargetId == 3 && sockets[0].LastState!.AccusedPlayerId == 1,
                "자동 확정 이후의 지목 변경은 기존 후보와 제출한 표를 바꾸면 안 됩니다.");
            Send(1, "judge", 1, initialEpoch, false);
            Send(1, "judge", 2, judgmentEpoch, false);
            Send(0, "judge", 1, judgmentEpoch, true);
            Send(4, "judge", 1, judgmentEpoch, true);
            Check(sockets[0].LastState!.JudgmentVotesCast == 0,
                "지난 버전·다른 후보·후보 본인·관전자의 찬반은 집계하면 안 됩니다.");
            Send(1, "judge", 1, judgmentEpoch, false);
            await State(state => state.JudgmentVotesCast == 1 && state.RejectionCount == 1);
            Send(1, "judge", 1, judgmentEpoch, true);
            var voted = await State(state => state.Players.Single(player => player.Id == 2).HasJudged, 1);
            Check(!voted.LocalJudgmentApprove && voted.ApprovalCount == 0 && voted.RejectionCount == 1,
                "중복 찬반은 제출한 선택이나 집계를 바꾸면 안 됩니다.");
            Send(2, "judge", 1, judgmentEpoch, false);
            Send(3, "judge", 1, judgmentEpoch, false);
            var reset = await State(state => state.Phase == GamePhase.Discussion && state.BallotVersion > judgmentEpoch);
            Check(reset.Round == 1 && reset.CanvasVersion == drawing.CanvasVersion && !reset.HasAccused
                && reset.JudgmentVotesCast == 0 && reset.Players.All(player => !player.HasVoted && !player.HasJudged && player.RoundPoints == 0 && player.Score == 0),
                "부결은 같은 라운드 토론에 새 버전을 발급하고 이전 표·중간 보상을 숨겨야 합니다.");
            Send(1, "vote", 1, initialEpoch);
            Send(1, "judge", 1, judgmentEpoch, true);
            var unchanged = sockets[0].LastState!;
            Check(unchanged.Phase == GamePhase.Discussion && unchanged.Players.All(player => !player.HasVoted && !player.HasJudged),
                "늦게 도착한 앞 지목·찬반 패킷은 새 토론을 바꾸면 안 됩니다.");
            Send(0, "vote", 2, reset.BallotVersion);
            for (int index = 1; index < 4; index++) Send(index, "vote", 1, reset.BallotVersion);
            var second = await State(state => state.Phase == GamePhase.Rebuttal && state.BallotVersion > reset.BallotVersion);
            Send(1, "judge", 1, judgmentEpoch, true);
            Check(sockets[0].LastState!.JudgmentVotesCast == 0, "같은 후보라도 지난 찬반 버전의 표를 새 판정에 재사용하면 안 됩니다.");
            room.Disconnect(connections[0], now += .2);
            var transferred = await State(state => state.Phase == GamePhase.Discussion && state.HostPlayerId == 2 && !state.HasAccused, 1);
            Check(transferred.IsHost && transferred.BallotVersion > second.BallotVersion
                && transferred.Players.All(player => !player.HasVoted && !player.HasJudged),
                "후보인 방장이 나가면 토론을 다시 열고 실제 새 방장 ID를 게시해야 합니다.");
            await WaitAsync(() => sockets.Skip(1).All(socket => socket.LastState?.HostPlayerId == 2), 3);
            Check(sockets.Skip(1).Count(socket => socket.LastState?.IsHost == true) == 1,
                "방장 이전 후 모든 참가자·관전자는 같은 방장 ID를 받아야 합니다.");
            Report("JSON 재지목·개인 확정 대상·전원 제출 즉시 진행·늦은 패킷·찬반 동결·부결 리셋·비밀 점수·후보 이탈·방장 ID 동기화 검증");
        }
        finally { foreach (var connection in connections) await connection.DisposeAsync(); }
    }
}
