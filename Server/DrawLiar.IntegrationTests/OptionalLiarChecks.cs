using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static RoomSettings OptionalSettings(DrawingMode mode = DrawingMode.Relay) => new()
    {
        LiarMode = LiarMode.Optional, LiarCount = 7, Mode = mode, RoundCount = 1, Topics = new[] { "과일" },
        RoleSeconds = 3, DrawSeconds = 5, DiscussionSeconds = 20, RebuttalSeconds = 5,
        RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5
    };

    private static GameData OptionalData() => new()
    {
        Topics = new[] { new TopicData { Name = "과일", Words = new[] { "비밀사과" } } },
        Scoring = new ScoreRules { CitizenCorrectVote = 7, LiarCorrectGuess = 5, LiarUncaught = 9 }
    };

    private static GameSession OptionalGame(int seed, int count = 3, DrawingMode mode = DrawingMode.Relay)
    {
        var game = new GameSession(OptionalSettings(mode), OptionalData(), seed);
        for (int id = 1; id <= count; id++) Check(game.Join(id, "검증 화가" + id, 0, 0), "선택 라이어 참가자가 입장해야 합니다.");
        Check(game.Join(99, "관전자", 0, 0, true, true), "선택 라이어 관전자가 입장해야 합니다.");
        return game;
    }

    private static GameSession OptionalGameWithLiar(bool liar, int count = 3, DrawingMode mode = DrawingMode.Relay)
    {
        for (int seed = 0; seed < 64; seed++)
        {
            var game = OptionalGame(seed, count, mode);
            Check(game.Start(0), "선택 라이어 경기를 시작해야 합니다.");
            if (Enumerable.Range(1, count).Any(id => game.Snapshot(id, 1, 0).LocalIsLiar) == liar) return game;
        }
        throw new InvalidOperationException("0명·1명 라이어 검증 경기를 모두 생성해야 합니다.");
    }

    private static async Task VerifyOptionalLiarAsync()
    {
        VerifyOptionalSettingsAndPrivacy();
        foreach (DrawingMode mode in Enum.GetValues<DrawingMode>())
        {
            VerifyOptionalAbsenceTie(mode);
            VerifyOptionalRejectAndCatch(mode);
            VerifyOptionalWrongAbsence(mode);
        }
        VerifyOptionalDisconnectedTruth();
        VerifyOptionalMidRoundJoin();
        await VerifyOptionalDedicatedAsync();
        Report("선택 라이어 2인 시작 거부·3인 시작·0/1명 비밀 상태·없음 지목·첫 동률 부결·동전 판정·재토론·점수·빈 추측 생략 검증");
    }

    private static void VerifyOptionalSettingsAndPrivacy()
    {
        Check(Enum.GetValues<LiarMode>().All(mode => GameRules.MinimumPlayers(mode) == 3),
            "모든 라이어 방식은 참가자 세 명 이상이어야 시작할 수 있습니다.");
        var settings = OptionalSettings(); settings.Validate();
        Check(settings.LiarCount == 1 && settings.MaxPlayers == 8 && settings.Copy().LiarMode == LiarMode.Optional,
            "선택 라이어 설정은 최대 한 명·8명 정원과 복사 모드를 보존해야 합니다.");
        var server = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        server.LiarCount = 7; ServerDatabase.ValidateSettings(server);
        var restored = JsonSerializer.Deserialize<RoomSettings>(JsonSerializer.Serialize(server, ServerRuntime.Json), Json)!;
        Check(server.LiarMode == 2 && server.LiarCount == 1 && restored.LiarMode == LiarMode.Optional,
            "서버 저장·전송 설정은 새 모드와 최대 한 명 규칙을 보존해야 합니다.");
        foreach (LiarMode mode in Enum.GetValues<LiarMode>())
        {
            var configuration = MismatchSettings(); configuration.LiarMode = mode;
            var tooSmall = new GameSession(configuration, MismatchData(), 1);
            for (int id = 1; id <= 2; id++) Check(tooSmall.Join(id, "화가", 0, 0), "최소 인원 검증 참가자를 입장시켜야 합니다.");
            Check(tooSmall.Join(99, "관전자", 0, 0, true, true), "최소 인원 검증 관전자를 입장시켜야 합니다.");
            Check(!tooSmall.CanStart && !tooSmall.Start(0), "관전자를 포함해 접속자가 세 명이어도 참가자 둘로는 시작할 수 없어야 합니다.");
            Check(tooSmall.Join(3, "세 번째 화가", 0, 0) && tooSmall.CanStart && tooSmall.Start(0),
                "모든 라이어 방식은 세 번째 참가자가 입장하면 시작할 수 있어야 합니다.");
        }
        foreach (int count in new[] { 3, 4, 8 })
        {
            var roleCounts = new HashSet<int>();
            for (int seed = 0; seed < 32; seed++)
            {
                var game = OptionalGame(seed, count); Check(game.Start(0), "선택 라이어 경기를 시작해야 합니다.");
                int liars = Enumerable.Range(1, count).Count(id => game.Snapshot(id, 1, 0).LocalIsLiar); roleCounts.Add(liars);
                Check(liars is 0 or 1, "선택 라이어는 정확히 0명 또는 1명이어야 합니다.");
                foreach (int id in Enumerable.Range(1, count).Append(99))
                {
                    var snapshot = JsonSerializer.Deserialize<RoomSnapshot>(JsonSerializer.Serialize(game.Snapshot(id, 1, 0), GameplayWire.Json), GameplayWire.Json)!;
                    Check(snapshot.RevealedLiarCount == -1 && snapshot.Players.All(player => !player.IsLiar)
                        && snapshot.Word == (id == 99 || snapshot.LocalIsLiar ? "" : "비밀사과"),
                        "공개 전 공용 JSON에 실제 라이어 수나 다른 역할·관전자 제시어가 새면 안 됩니다.");
                }
            }
            Check(roleCounts.SetEquals(new[] { 0, 1 }), "여러 시드에서 라이어 있음·없음 모두 배정되어야 합니다.");
        }
        foreach (LiarMode mode in new[] { LiarMode.Classic, LiarMode.Mismatch })
        {
            var classic = MismatchGame(); var configuration = MismatchSettings(); configuration.LiarMode = mode;
            Check(classic.Configure(configuration) && classic.Start(0), "기존 모드를 시작해야 합니다.");
            double now = OptionalDiscussion(classic);
            Check(!classic.Vote(1, GameRules.NO_LIAR_TARGET, now), "기존 모드에서는 라이어 없음 지목을 거부해야 합니다.");
        }
    }

    private static double OptionalDiscussion(GameSession game)
    {
        double now = 3.1; game.Tick(now);
        while (game.Phase == GamePhase.Drawing) Check(game.EndTurn(game.ArtistId, now += .1), "모든 참가자가 그림 차례를 완료해야 합니다.");
        Check(game.Phase == GamePhase.Discussion, "그림 이후 토론과 지목이 열려야 합니다.");
        return now;
    }

    private static double OptionalNominateNone(GameSession game, double now)
    {
        foreach (int id in game.Snapshot(1, 1, now).Players.Where(player => player.IsConnected && !player.IsSpectator).Select(player => player.Id))
            Check(game.Vote(id, GameRules.NO_LIAR_TARGET, now), "참가자는 라이어 없음에 지목 투표할 수 있어야 합니다.");
        Check(game.Phase == GamePhase.Discussion, "없음 지목도 전원 제출 후 마감까지 변경할 수 있어야 합니다.");
        AdvanceProfilePhase(game, ref now);
        var snapshot = game.Snapshot(1, 1, now);
        Check(snapshot.Phase == GamePhase.Rebuttal && snapshot.HasAccused && snapshot.AccusedPlayerId == GameRules.NO_LIAR_TARGET
            && snapshot.JudgmentVoterCount == snapshot.Players.Count(player => player.IsConnected && !player.IsSpectator)
            && snapshot.RevealedLiarCount == -1, "없음 지목은 실제 역할을 숨긴 채 전원 찬반으로 넘어가야 합니다.");
        return now;
    }

    private static void VerifyOptionalAbsenceTie(DrawingMode mode)
    {
        var game = OptionalGameWithLiar(false, 4, mode); double now = OptionalDiscussion(game);
        CompletedMatchData? completed = null; int completionCount = 0; game.MatchCompleted += value => { completed = value; completionCount++; };
        Check(!game.Vote(99, -1, now) && !game.Vote(1, -2, now) && !game.Vote(1, 1, now), "관전자·잘못된 음수 대상·자기 지목은 거부해야 합니다.");
        Check(game.Vote(1, -1, now) && game.Vote(1, -1, now)
            && game.Snapshot(1, 1, now).LocalVoteTargetId == -1
            && game.Snapshot(1, 1, now).Players.Single(player => player.Id == 1).HasVoted,
            "같은 없음 지목의 재전송은 표를 중복 집계하지 않고 본인 제출 상태를 유지해야 합니다.");
        Check(game.Vote(2, -1, now), "두 번째 없음 지목을 제출해야 합니다.");
        Check(game.Vote(3, -1, now) && game.Phase == GamePhase.Discussion && game.Vote(4, -1, now),
            "네 참가자의 없음 지목을 모두 제출할 수 있어야 합니다.");
        Check(game.Phase == GamePhase.Discussion, "전원 없음 지목 후에도 토론 마감까지 기다려야 합니다.");
        AdvanceProfilePhase(game, ref now);
        Check(!game.Judge(99, -1, true, now) && !game.Judge(1, 2, true, now), "관전자·다른 대상 찬반은 거부해야 합니다.");
        Check(game.Judge(1, -1, true, now) && !game.Judge(1, -1, false, now), "찬반도 한 번만 제출해야 합니다.");
        Check(game.Judge(2, -1, false, now) && game.Judge(3, -1, true, now) && game.Phase == GamePhase.Rebuttal,
            "네 번째 참가자의 찬반을 기다려야 합니다.");
        Check(game.Judge(4, -1, false, now) && game.Phase == GamePhase.Discussion && !game.Snapshot(99, 1, now).IsJudgmentCoinToss,
            "첫 2대2 찬반 동률이면 없음 지목도 부결해야 합니다.");
        now = OptionalNominateNone(game, now);
        for (int id = 1; id <= 4; id++) Check(game.Judge(id, -1, id is 1 or 3, now), "두 번째 없음 지목의 동률을 제출해야 합니다.");
        var toss = game.Snapshot(99, 1, now);
        Check(toss.IsJudgmentCoinToss && toss.AccusedPlayerId == -1 && toss.RemainingSeconds == 3 && toss.RevealedLiarCount == -1
            && !game.Judge(1, -1, false, now + 1), "없음 지목도 두 번째 동률부터 역할을 숨기고 3초 동전을 기다려야 합니다.");
        game.Tick(now += GameRules.JUDGMENT_COIN_TOSS_SECONDS);
        if (!toss.JudgmentCoinApproved)
        {
            Check(game.Phase == GamePhase.Discussion, "반대 동전은 없음 지목도 재토론으로 돌려야 합니다.");
            now = OptionalNominateNone(game, now);
            Check(game.Judge(1, -1, true, now) && game.Judge(3, -1, true, now), "기존 올바른 참가자만 재찬성해야 합니다.");
            game.Tick(now += game.Settings.RebuttalSeconds + .01);
        }
        Check(game.Phase == GamePhase.LiarReveal && !game.Snapshot(99, 1, now).IsJudgmentCoinToss,
            "동전 가결 또는 이후 찬성 우세는 없음 지목의 역할 공개로 진행해야 합니다.");
        Check(game.Snapshot(99, 1, now).RevealedLiarCount == 0, "공개 단계에는 관전자에게도 실제 0명을 알려야 합니다.");
        game.Tick(now += game.Settings.RevealSeconds + .01);
        Check(game.Phase == GamePhase.RoundResults && completed?.Players.Length == 4
            && completed.Players.Where(player => player.PlayerId is 1 or 3).All(player => player.Score == 7 && player.WeightedRoundScore == 28)
            && completed.Players.Where(player => player.PlayerId is 2 or 4).All(player => player.Score == 0)
            && completed.Players.All(player => player.CitizenRounds == 1 && player.LiarRounds == 0 && player.WeightedRoundParticipants == 4),
            "라이어가 없으면 빈 추측 시간을 생략하고 올바른 찬성만 점수·4인 보상 가중치에 반영해야 합니다.");
        for (int index = 0; index < 4; index++) game.Tick(now += 1000);
        Check(game.Phase == GamePhase.MatchResults && completionCount == 1, "경기 완료 보고서는 반복 Tick에도 한 번만 발행해야 합니다.");
        game.ReturnToLobby();
        Check(game.Snapshot(1, 1, now).RevealedLiarCount == -1 && !game.Snapshot(1, 1, now).HasAccused,
            "대기방 복귀는 이전 공개 역할 수와 없음 지목 상태를 제거해야 합니다.");
    }

    private static void VerifyOptionalRejectAndCatch(DrawingMode mode)
    {
        var game = OptionalGameWithLiar(true, mode: mode); double now = OptionalDiscussion(game);
        int liar = Enumerable.Range(1, 3).Single(id => game.Snapshot(id, 1, now).LocalIsLiar);
        var citizens = Enumerable.Range(1, 3).Where(id => id != liar).ToArray(); int citizen = citizens[0];
        now = OptionalNominateNone(game, now); int ballot = game.BallotVersion;
        foreach (int id in Enumerable.Range(1, 3)) Check(game.Judge(id, -1, false, now), "실제 라이어가 있으면 없음 지목에 반대할 수 있어야 합니다.");
        Check(game.Phase == GamePhase.Discussion && game.BallotVersion > ballot && !game.Snapshot(1, 1, now).HasAccused
            && game.Snapshot(1, 1, now).RevealedLiarCount == -1, "부결은 비밀을 유지하고 새 토론·투표로 복귀해야 합니다.");
        Check(!game.Judge(citizen, -1, true, now) && game.Vote(citizen, liar, now), "오래된 찬반은 거부하고 새 지목은 허용해야 합니다.");
        game.Tick(now += game.Settings.DiscussionSeconds + .01);
        Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(citizen, 1, now).AccusedPlayerId == liar
            && game.Snapshot(citizen, 1, now).JudgmentVoterCount == 2, "사람 지목은 지목된 사람을 제외한 찬반 규칙을 유지해야 합니다.");
        Check(!game.Judge(liar, liar, true, now) && game.Judge(citizens[0], liar, true, now) && game.Phase == GamePhase.Rebuttal,
            "지목된 사람은 찬반할 수 없고 나머지 시민의 찬반이 끝나기를 기다려야 합니다.");
        Check(game.Judge(citizens[1], liar, false, now) && game.Phase == GamePhase.Discussion,
            "사람 지목도 첫 1대1 찬반 동률이면 부결해야 합니다.");
        foreach (int id in Enumerable.Range(1, 3)) Check(game.Vote(id, id == liar ? citizen : liar, now), "같은 라운드에서 라이어를 다시 지목해야 합니다.");
        AdvanceProfilePhase(game, ref now);
        foreach (int id in citizens) Check(game.Judge(id, liar, true, now), "동률 이후 찬성 우세는 지연 없이 가결해야 합니다.");
        Check(game.Phase == GamePhase.LiarReveal && !game.Snapshot(citizen, 1, now).IsJudgmentCoinToss,
            "찬성 우세는 앞 동률 횟수와 무관하게 가결해야 합니다.");
        Check(game.Snapshot(99, 1, now).RevealedLiarCount == 1, "역할 공개 시 한 명을 알려야 합니다.");
        game.Tick(now += game.Settings.RevealSeconds + .01);
        Check(game.Phase == GamePhase.Guessing && !game.Guess(citizen, "비밀사과", now) && game.Guess(liar, "오답", now),
            "실제 라이어가 있으면 본인만 정답 추측을 진행해야 합니다.");
        var result = game.Snapshot(citizen, 1, now);
        Check(result.Phase == GamePhase.RoundResults && result.Players.Where(player => !player.IsSpectator).All(player => player.RoundPoints == 7)
            && result.Players.Single(player => player.Id == liar).IsCaught,
            "올바른 없음 반대와 사람 찬성은 중복 가산 없이 한 라운드 한 번씩 점수를 지급해야 합니다.");
    }

    private static void VerifyOptionalWrongAbsence(DrawingMode mode)
    {
        var game = OptionalGameWithLiar(true, 3, mode); double now = OptionalDiscussion(game);
        int liar = Enumerable.Range(1, 3).Single(id => game.Snapshot(id, 1, now).LocalIsLiar);
        int dissenter = Enumerable.Range(1, 3).First(id => id != liar);
        now = OptionalNominateNone(game, now);
        foreach (int id in Enumerable.Range(1, 3)) Check(game.Judge(id, -1, id != dissenter, now), "없음 지목에 찬반을 제출해야 합니다.");
        Check(game.Phase == GamePhase.LiarReveal && game.Snapshot(99, 1, now).RevealedLiarCount == 1
            && game.Snapshot(99, 1, now).Players.All(player => !player.IsCaught), "잘못 가결된 없음 지목은 실제 라이어를 잡은 것으로 처리하면 안 됩니다.");
        game.Tick(now += game.Settings.RevealSeconds + .01); Check(game.Guess(liar, "비밀사과", now), "살아남은 라이어는 정답을 추측할 수 있어야 합니다.");
        var result = game.Snapshot(dissenter, 1, now);
        Check(result.Players.Single(player => player.Id == liar).RoundPoints == 14
            && result.Players.Single(player => player.Id == dissenter).RoundPoints == 7
            && result.Players.Single(player => !player.IsSpectator && player.Id != liar && player.Id != dissenter).RoundPoints == 0,
            "잘못된 없음 찬성은 점수가 없고 실제 라이어 생존·정답과 올바른 반대 점수를 유지해야 합니다.");
    }

    private static void VerifyOptionalDisconnectedTruth()
    {
        var game = OptionalGameWithLiar(true, 4); double now = OptionalDiscussion(game);
        int liar = Enumerable.Range(1, 4).Single(id => game.Snapshot(id, 1, now).LocalIsLiar); game.Disconnect(liar, now);
        now = OptionalNominateNone(game, now);
        var citizens = Enumerable.Range(1, 4).Where(id => id != liar).ToArray();
        Check(game.Judge(citizens[0], -1, false, now), "단절된 라이어가 있는 라운드에서도 없음 반대가 가능해야 합니다.");
        foreach (int id in citizens.Skip(1)) Check(game.Judge(id, -1, true, now), "나머지 시민은 찬성할 수 있어야 합니다.");
        Check(game.Snapshot(99, 1, now).RevealedLiarCount == 1, "단절된 라이어도 라운드의 실제 역할 수에 포함해야 합니다.");
        game.Tick(now += game.Settings.RevealSeconds + .01);
        Check(game.Phase == GamePhase.RoundResults && game.Snapshot(citizens[0], 1, now).Players.Single(player => player.Id == citizens[0]).RoundPoints == 7,
            "라이어 단절은 없음의 진실을 바꾸지 않으며 빈 추측을 생략하고 올바른 반대 점수를 지급해야 합니다.");
    }

    private static void VerifyOptionalMidRoundJoin()
    {
        var game = OptionalGameWithLiar(false); double now = OptionalDiscussion(game);
        now = OptionalNominateNone(game, now);
        Check(game.Join(4, "도중 참가자", 0, 0), "없음 찬반 중에도 일반 난입이 가능해야 합니다.");
        var joined = game.Snapshot(4, 1, now);
        Check(!joined.LocalIsSpectator && joined.Word == "비밀사과" && joined.RevealedLiarCount == -1
            && joined.JudgmentVoterCount == 4, "찬반 도중 난입한 시민은 역할 수를 모른 채 현재 없음 찬반에 참가해야 합니다.");
        Check(game.Judge(1, -1, true, now) && game.Judge(2, -1, true, now) && game.Judge(3, -1, true, now) && game.Phase == GamePhase.Rebuttal,
            "난입 참가자 찬반을 기다리지 않고 이전 세 명만으로 조기 확정하면 안 됩니다.");
        Check(game.Judge(4, -1, true, now) && game.Phase == GamePhase.LiarReveal, "추가된 시민까지 찬반을 마치면 즉시 공개해야 합니다.");
        game.Tick(now += game.Settings.RevealSeconds + .01);
        Check(game.Phase == GamePhase.RoundResults && game.Snapshot(4, 1, now).Players.Single(player => player.Id == 4).RoundPoints == 7,
            "난입 시민의 올바른 없음 찬성도 현재 라운드 점수에 포함해야 합니다.");
    }

    private static async Task VerifyOptionalDedicatedAsync()
    {
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "optional1",
            Settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(OptionalSettings(), Json), Json)! };
        ServerDatabase.ValidateSettings(roomData.Settings);
        var room = new DedicatedRoom(roomData, OptionalData(), Array.Empty<ServerTopicData>(), 0, () => { });
        var sockets = Enumerable.Range(0, 4).Select(_ => new PolicySocket()).ToArray();
        var peers = sockets.Select((socket, index) => new GameConnection(socket, "optional" + (index + 1), "test", CancellationToken.None)).ToArray();
        try
        {
            foreach (int index in new[] { 0, 1, 3 }) Check(room.Join(peers[index], new RedeemTicketResponse
            { AccountId = peers[index].AccountId, Room = roomData, IsSpectator = index == 3, SpectatorOnly = index == 3,
                Profile = new ProfileData { AccountId = peers[index].AccountId, DisplayName = "연결 검증" + index } }, 0), "DS 참가자 둘·관전자를 입장시켜야 합니다.");
            room.Receive(peers[1], new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
            Check(!room.Status().IsInProgress, "두 명이어도 비방장은 경기를 시작할 수 없어야 합니다.");
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, .2);
            Check(!room.Status().IsInProgress && room.Status().PlayerCount == 2,
                "방장도 참가자가 둘뿐이면 시작할 수 없으며 관전자는 최소 인원에 포함하지 않아야 합니다.");
            Check(room.Join(peers[2], new RedeemTicketResponse
            { AccountId = peers[2].AccountId, Room = roomData,
                Profile = new ProfileData { AccountId = peers[2].AccountId, DisplayName = "세 번째 연결 검증" } }, .3), "DS 세 번째 참가자가 입장해야 합니다.");
            room.Receive(peers[1], new GameplayEnvelope { Type = "request", Kind = "start" }, .4);
            Check(!room.Status().IsInProgress, "참가자가 세 명이어도 비방장은 시작할 수 없어야 합니다.");
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, .5);
            await WaitAsync(() => sockets.All(socket => socket.LastState?.Phase == GamePhase.RoleReveal), 3);
            Check(room.Status().IsInProgress && room.Status().PlayerCount == 3 && sockets.All(socket => socket.LastState!.RevealedLiarCount == -1),
                "DS는 선택 라이어도 세 명부터 시작하고 연결별 역할 수를 숨겨야 합니다.");
            double now = 4; room.Tick(now);
            for (int index = 0; index < 3; index++)
            {
                await WaitAsync(() => sockets[0].LastState?.Phase == GamePhase.Drawing, 3);
                int artist = sockets[0].LastState!.ArtistId;
                room.Receive(peers.Single(peer => peer.PlayerId == artist), new GameplayEnvelope { Type = "request", Kind = "endTurn" }, now += .1);
                await WaitAsync(() => sockets[0].LastState?.ArtistId != artist || sockets[0].LastState?.Phase != GamePhase.Drawing, 3);
            }
            await WaitAsync(() => sockets.All(socket => socket.LastState?.Phase == GamePhase.Discussion), 3);
            int nominationVersion = sockets[0].LastState!.BallotVersion;
            room.Receive(peers[3], new GameplayEnvelope { Type = "request", Kind = "vote", Target = -1, BallotVersion = nominationVersion }, now += .1);
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "vote", Target = -1, BallotVersion = nominationVersion - 1 }, now += .1);
            for (int index = 0; index < 3; index++) room.Receive(peers[index], new GameplayEnvelope { Type = "request", Kind = "vote", Target = -1, BallotVersion = nominationVersion }, now += .1);
            await WaitAsync(() => sockets.All(socket => socket.LastState is { Phase: GamePhase.Discussion } state
                && state.Players.Count(player => player.HasVoted && !player.IsSpectator) == 3), 3);
            Check(sockets.Take(3).All(socket => socket.LastState!.LocalVoteTargetId == -1
                && socket.LastState!.Players.Single(player => player.Id == socket.LastState!.LocalPlayerId).HasVoted)
                && sockets[3].LastState!.LocalVoteTargetId == -1 && sockets[3].LastState!.LocalIsSpectator,
                "없음 target -1은 본인 HasVoted와 함께 전달하고 관전자에게 개인 표를 공개하면 안 됩니다.");
            room.Tick(now += sockets[0].LastState!.RemainingSeconds + .01);
            await WaitAsync(() => sockets.All(socket => socket.LastState?.Phase == GamePhase.Rebuttal), 3);
            Check(sockets.All(socket => socket.LastState is { HasAccused: true, AccusedPlayerId: -1, JudgmentVoterCount: 3, RevealedLiarCount: -1 }),
                "실제 DS JSON은 -1 없음 표적과 전원 찬반 수를 전달해야 합니다.");
            int judgmentVersion = sockets[0].LastState!.BallotVersion;
            for (int index = 0; index < 3; index++) room.Receive(peers[index], new GameplayEnvelope
            { Type = "request", Kind = "judge", Target = -1, BallotVersion = judgmentVersion, Approve = true }, now += .1);
            await WaitAsync(() => sockets.All(socket => socket.LastState?.Phase == GamePhase.LiarReveal), 3);
            Check(sockets.All(socket => socket.LastState!.RevealedLiarCount is 0 or 1), "역할 공개 이후 JSON은 실제 역할 수를 전달해야 합니다.");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
        Report("DS 2인 시작 거부·3인 방장 권한·관전자 제외·없음 표적 wire·전원 찬반·공개 전후 역할 수 검증");
    }
}
