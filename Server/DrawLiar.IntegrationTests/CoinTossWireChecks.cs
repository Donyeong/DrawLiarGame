using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;

internal static partial class Integration
{
    private static async Task VerifyCoinTossWireAsync()
    {
        var sockets = Enumerable.Range(0, 5).Select(_ => new PolicySocket()).ToArray();
        var peers = sockets.Select((socket, index) => new GameConnection(socket, "coin" + index, "test", CancellationToken.None)).ToArray();
        var roomData = new ServerRoomData
        {
            RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "coin0",
            Settings = new ServerRoomSettings { Topics = new[] { "과일" }, RoundCount = 1, RoleSeconds = 3,
                DrawSeconds = 5, DiscussionSeconds = 30, RebuttalSeconds = 6 }
        };
        var room = new DedicatedRoom(roomData,
            new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } },
            Array.Empty<ServerTopicData>(), 0, () => { });
        double now = 0;
        void Join(int index) => Check(room.Join(peers[index], new RedeemTicketResponse
        {
            AccountId = peers[index].AccountId, Room = roomData, IsSpectator = index >= 3, SpectatorOnly = index >= 3,
            Profile = new ProfileData { AccountId = peers[index].AccountId, DisplayName = "동전 검증" + index }
        }, now), "동전 검증의 참가자·관전자가 입장해야 합니다.");
        void Send(int index, string kind, int target = 0, int version = 0, bool approve = false)
        {
            var frame = new GameplayEnvelope { Type = "request", Kind = kind, Target = target, BallotVersion = version, Approve = approve };
            var received = JsonSerializer.Deserialize<GameplayEnvelope>(JsonSerializer.Serialize(frame, GameplayWire.Json), GameplayWire.Json)!;
            room.Receive(peers[index], received, now += .05);
        }
        async Task<RoomSnapshot> State(Func<RoomSnapshot, bool> predicate)
        {
            await WaitAsync(() => sockets.Take(4).All(socket => socket.LastState is { } state && predicate(state)), 3);
            return sockets[0].LastState!;
        }
        async Task<RoomSnapshot> Nominate()
        {
            int version = sockets[0].LastState!.BallotVersion;
            Send(0, "vote", 2, version);
            Send(1, "vote", 1, version);
            Send(2, "vote", 1, version);
            return await State(state => state.Phase == GamePhase.Rebuttal && state.BallotVersion > version);
        }
        try
        {
            for (int index = 0; index < 4; index++) Join(index);
            await State(state => state.Players.Length == 4);
            Send(0, "start");
            await State(state => state.Phase == GamePhase.RoleReveal);
            room.Tick(now = 100);
            var drawing = await State(state => state.Phase == GamePhase.Drawing);
            while (drawing.Phase == GamePhase.Drawing)
            {
                int artist = drawing.ArtistId;
                Send(artist - 1, "endTurn");
                drawing = await State(state => state.Phase != GamePhase.Drawing || state.ArtistId != artist);
            }
            var first = await Nominate();
            Send(1, "judge", 1, first.BallotVersion, true);
            Send(2, "judge", 1, first.BallotVersion, false);
            var rejected = await State(state => state.Phase == GamePhase.Discussion && state.BallotVersion > first.BallotVersion);
            Check(rejected.Round == 1 && !rejected.IsJudgmentCoinToss && !rejected.JudgmentCoinApproved
                && rejected.Players.All(player => !player.IsLiar && !player.IsCaught && player.Score == 0),
                "첫 찬반 동률 JSON은 같은 라운드 부결과 비공개 역할을 전달해야 합니다.");

            var second = await Nominate();
            Send(1, "judge", 1, first.BallotVersion, false);
            Send(3, "judge", 1, second.BallotVersion, true);
            Send(1, "judge", 1, second.BallotVersion, true);
            Send(2, "judge", 1, second.BallotVersion, false);
            var toss = await State(state => state.IsJudgmentCoinToss);
            double started = now;
            bool approved = toss.JudgmentCoinApproved;
            Check(sockets.Take(4).All(socket => socket.LastState is { } state
                && state.Phase == GamePhase.Rebuttal && state.JudgmentCoinApproved == approved
                && state.ApprovalCount == 1 && state.RejectionCount == 1 && state.JudgmentVotesCast == 2
                && state.RevealedLiarCount == -1 && state.RemainingSeconds == GameRules.JUDGMENT_COIN_TOSS_SECONDS
                && state.Players.All(player => !player.IsLiar && !player.IsCaught && player.Score == 0 && player.RoundPoints == 0)),
                "두 번째 동률 JSON은 모든 참가자·관전자에게 같은 동전 결과와 대기 시간만 보내야 합니다.");
            Send(1, "judge", 1, second.BallotVersion, false);
            now = started + 1.2;
            room.Tick(now);
            await State(state => state.IsJudgmentCoinToss && state.RemainingSeconds < 2);
            Join(4);
            await WaitAsync(() => sockets[4].LastState is { IsJudgmentCoinToss: true }, 3);
            Check(sockets.All(socket => socket.LastState is { } state && state.IsJudgmentCoinToss
                && state.JudgmentCoinApproved == approved && Math.Abs(state.RemainingSeconds - 1.8f) < .01f
                && state.ApprovalCount == 1 && state.RejectionCount == 1 && state.BallotVersion == second.BallotVersion),
                "추가 투표·중도 관전은 서버 동전 결과를 바꾸거나 연출 시간을 다시 시작하면 안 됩니다.");
            Check(sockets[4].LastState!.LocalIsSpectator && sockets[4].LastState!.Word == "",
                "동전 중 입장한 관전자는 비밀 제시어를 받으면 안 됩니다.");
            room.Tick(now = started + GameRules.JUDGMENT_COIN_TOSS_SECONDS);
            var expected = approved ? GamePhase.LiarReveal : GamePhase.Discussion;
            await WaitAsync(() => sockets.All(socket => socket.LastState is { } state && state.Phase == expected && !state.IsJudgmentCoinToss), 3);
            Check(sockets.All(socket => !socket.LastState!.JudgmentCoinApproved),
                "동전 결과를 적용한 뒤 JSON의 동전 하위 상태는 해제해야 합니다.");
            Report("첫 동률 부결·두 번째 동전 JSON 동기화·3초 대기·늦은 표·중도 관전·역할 비공개 검증");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }
}
