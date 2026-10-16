using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private const string SPY_WORD = "스파이검증사과";
    private const string SPY_ALIAS = "비공개정답별칭";

    private static RoomSettings SpySettings(DrawingMode mode = DrawingMode.Relay, int rounds = 1) => new()
    {
        LiarMode = LiarMode.Spy, LiarCount = 7, Mode = mode, RoundCount = rounds,
        RoleSeconds = 3, DrawSeconds = 5, DiscussionSeconds = 20, RebuttalSeconds = 5,
        GuessSeconds = 5, ResultSeconds = 5, Topics = new[] { "스파이 검증" }
    };

    private static GameData SpyData() => new()
    {
        Topics = new[] { new TopicData { Name = "스파이 검증", Words = new[] { SPY_WORD },
            Aliases = new[] { new WordAlias { Word = SPY_WORD, Accept = new[] { SPY_ALIAS } } } } },
        Scoring = new ScoreRules { CitizenCorrectVote = 7, LiarCorrectGuess = 5, LiarUncaught = 9 }
    };

    private static GameSession SpyGame(DrawingMode mode = DrawingMode.Relay, int count = 4, int seed = 21, int rounds = 1)
    {
        var game = new GameSession(SpySettings(mode, rounds), SpyData(), seed);
        for (int id = 1; id <= count; id++) Check(game.Join(id, "검증 화가" + id, 0, 0), "스파이 참가자를 준비해야 합니다.");
        Check(game.Join(99, "관전자", 0, 0, true, true) && game.Start(0), "스파이 경기와 관전을 시작해야 합니다.");
        return game;
    }

    private static int SpyRole(GameSession game, bool spy, int count, double now = 0) => Enumerable.Range(1, count)
        .Single(id => spy ? game.Snapshot(id, 1, now).LocalIsSpy : game.Snapshot(id, 1, now).LocalIsLiar);

    private static async Task VerifySpyAsync()
    {
        VerifySpySettings();
        foreach (DrawingMode mode in Enum.GetValues<DrawingMode>())
        {
            VerifySpyAssignments(mode);
            foreach (bool caught in new[] { false, true })
                foreach (bool? correct in new bool?[] { false, true, null }) VerifySpyScoring(mode, caught, correct);
            VerifySpyCaught(mode);
            VerifySpyReentry(mode);
            VerifySpyDisconnects(mode);
            VerifySpyNextRound(mode);
        }
        await VerifySpyDedicatedAsync();
        Report("스파이 3~8명 배정·개인 역할/JSON 비밀·추측 권한·팀 점수/승패·스파이 적발·난입·재접속·이탈·라운드 초기화 검증");
    }

    private static void VerifySpySettings()
    {
        Check((int)LiarMode.Spy == 3 && GameRules.MinimumPlayers(LiarMode.Spy) == 3, "스파이는 모드 3과 기존 최소 세 명을 유지해야 합니다.");
        var settings = SpySettings(); settings.Validate();
        Check(settings.LiarCount == 1 && settings.MaxPlayers == 8 && settings.Copy().LiarMode == LiarMode.Spy,
            "스파이 설정은 라이어 한 명·정원 여덟 명으로 정규화해야 합니다.");
        var server = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        server.LiarCount = 7; ServerDatabase.ValidateSettings(server);
        var restored = JsonSerializer.Deserialize<RoomSettings>(JsonSerializer.Serialize(server, ServerRuntime.Json), Json)!;
        Check(server.LiarMode == 3 && server.LiarCount == 1 && restored.LiarMode == LiarMode.Spy,
            "게임서버 검증·저장·DS 설정 변환은 스파이 모드를 보존해야 합니다.");
        var tooSmall = new GameSession(SpySettings(), SpyData(), 1);
        tooSmall.Join(1, "첫 화가", 0, 0); tooSmall.Join(2, "둘째 화가", 0, 0); tooSmall.Join(99, "관전", 0, 0, true, true);
        Check(!tooSmall.CanStart && !tooSmall.Start(0) && tooSmall.Join(3, "셋째 화가", 0, 0) && tooSmall.Start(0),
            "관전자를 뺀 실제 참가자 세 명부터 시작해야 합니다.");
    }

    private static void VerifySpyAssignments(DrawingMode mode)
    {
        for (int count = 3; count <= 8; count++)
            for (int seed = 0; seed < 12; seed++)
            {
                var game = SpyGame(mode, count, seed);
                int spy = SpyRole(game, true, count), liar = SpyRole(game, false, count);
                Check(spy != liar && Enumerable.Range(1, count).Count(id => game.Snapshot(id, 1, 0).LocalIsSpy) == 1
                    && Enumerable.Range(1, count).Count(id => game.Snapshot(id, 1, 0).LocalIsLiar) == 1,
                    "모든 인원·시드에서 서로 다른 스파이와 라이어를 정확히 한 명씩 배정해야 합니다.");
                VerifySpySecret(game, spy, liar, 0);
                if (count == 8) Check(!game.Join(9, "정원 초과", 0, 0), "스파이 모드도 참가자 정원을 늘리면 안 됩니다.");
            }
    }

    private static void VerifySpySecret(GameSession game, int spy, int liar, double now)
    {
        foreach (int id in game.Snapshot(1, 1, now).Players.Select(player => player.Id).Append(199))
        {
            var state = game.Snapshot(id, 1, now);
            bool observer = state.LocalIsSpectator;
            Check(state.LocalIsSpy == (!observer && id == spy) && state.LocalIsLiar == (!observer && id == liar)
                && state.RevealedLiarCount == -1 && state.Players.All(player => !player.IsLiar && !player.IsSpy && !player.IsCaught)
                && state.Word == (observer || id == liar ? "" : SPY_WORD) && state.MismatchWord == "",
                "공개 전 본인 역할만 전달하고 스파이는 정답을 알되 다른 역할·관전자 정답은 숨겨야 합니다.");
            var strings = MismatchStrings(new GameplayEnvelope { Type = "state", State = state });
            Check(!strings.Contains(SPY_ALIAS) && (!(observer || id == liar) || !strings.Contains(SPY_WORD)),
                "직렬화된 개별 상태에도 관전자·라이어 정답과 서버 정답 별칭을 노출하면 안 됩니다.");
            var decoded = JsonSerializer.Deserialize<RoomSnapshot>(JsonSerializer.Serialize(state, GameplayWire.Json), GameplayWire.Json)!;
            Check(decoded.LocalIsSpy == state.LocalIsSpy && decoded.Players.All(player => !player.IsSpy), "스파이 개인·공용 플래그의 JSON 계약을 유지해야 합니다.");
        }
    }

    private static double SpyDiscussion(GameSession game)
    {
        double now = game.Settings.RoleSeconds + .01;
        game.Tick(now);
        while (game.Phase == GamePhase.Drawing) Check(game.EndTurn(game.ArtistId, now += .01), "스파이·시민·라이어가 자신의 그림 차례를 마쳐야 합니다.");
        Check(game.Phase == GamePhase.Discussion, "그림 다음에는 토론으로 진행해야 합니다.");
        return now;
    }

    private static void SpyAccuse(GameSession game, int accused, double now)
    {
        var ids = game.Snapshot(1, 1, now).Players.Where(player => player.IsConnected && !player.IsSpectator).Select(player => player.Id).ToArray();
        foreach (int id in ids) Check(game.Vote(id, id == accused ? ids.First(other => other != accused) : accused, now), "스파이 모드의 지목을 제출해야 합니다.");
        Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(1, 1, now).AccusedPlayerId == accused, "최다표 후보가 반론해야 합니다.");
    }

    private static void SpyApprove(GameSession game, double now)
    {
        var state = game.Snapshot(1, 1, now);
        foreach (int id in state.Players.Where(player => player.IsConnected && !player.IsSpectator && player.Id != state.AccusedPlayerId).Select(player => player.Id))
            Check(game.Judge(id, state.AccusedPlayerId, true, now), "후보를 제외한 참가자의 찬성을 처리해야 합니다.");
        Check(game.Phase == GamePhase.LiarReveal, "가결 후 역할 공개를 진행해야 합니다.");
    }

    private static void VerifySpyScoring(DrawingMode mode, bool caught, bool? correct)
    {
        var game = SpyGame(mode);
        int spy = SpyRole(game, true, 4), liar = SpyRole(game, false, 4);
        int[] citizens = Enumerable.Range(1, 4).Where(id => id != spy && id != liar).ToArray();
        CompletedMatchData? completed = null; int reports = 0; game.MatchCompleted += result => { completed = result; reports++; };
        double now = SpyDiscussion(game);
        VerifySpySecret(game, spy, liar, now);
        SpyAccuse(game, caught ? liar : citizens[0], now);
        VerifySpySecret(game, spy, liar, now);
        SpyApprove(game, now);
        foreach (int id in Enumerable.Range(1, 4).Append(99))
        {
            var state = game.Snapshot(id, 1, now);
            Check(state.RevealedLiarCount == 1 && state.Players.Count(player => player.IsSpy) == 1
                && state.Players.Single(player => player.IsSpy).Id == spy && state.Players.Single(player => player.IsLiar).Id == liar
                && state.Word == (id == 99 || id == liar ? "" : SPY_WORD), "공개 시 스파이와 라이어를 별도로 표시하되 추측 정답은 계속 숨겨야 합니다.");
        }
        now += game.Snapshot(1, 1, now).RemainingSeconds + .01; game.Tick(now);
        Check(game.Phase == GamePhase.Guessing && !game.Guess(spy, SPY_WORD, now) && !game.Guess(citizens[0], SPY_WORD, now)
            && !game.Guess(99, SPY_WORD, now), "정답을 아는 스파이·시민·관전자는 라이어 추측을 대신 제출하면 안 됩니다.");
        if (correct.HasValue) Check(game.Guess(liar, correct.Value ? SPY_ALIAS : "틀린답", now), "실제 라이어만 정답 또는 오답을 제출해야 합니다.");
        else { now += game.Settings.GuessSeconds; game.Tick(now); }
        bool teamWins = !caught || correct == true;
        int teamScore = teamWins ? (caught ? 0 : 9) + (correct == true ? 5 : 0) : 0;
        var result = game.Snapshot(1, 1, now);
        Check(result.Phase == GamePhase.RoundResults && result.Word == SPY_WORD && result.Players.Single(player => player.Id == spy).RoundPoints == teamScore
            && result.Players.Single(player => player.Id == liar).RoundPoints == teamScore
            && result.Players.Where(player => citizens.Contains(player.Id)).All(player => player.RoundPoints == (teamWins ? 0 : 7)),
            "라이어의 적발·추측 결과를 스파이와 공유하고 승리한 팀에만 동일한 점수를 지급해야 합니다.");
        Check(result.Players.Single(player => player.Id == spy) is { HasGuessed: false, Guess: "", GuessOutcome: GuessOutcome.Hidden }
            && completed != null && completed.Players.Single(player => player.PlayerId == spy) is { LiarRounds: 1, CitizenRounds: 0, CorrectGuesses: 0 }
            && completed.Players.Single(player => player.PlayerId == liar).CorrectGuesses == (correct == true ? 1 : 0)
            && completed.Players.Single(player => player.PlayerId == spy).Won == teamWins
            && result.Winners.Order().SequenceEqual((teamWins ? new[] { spy, liar } : citizens).Order()),
            "스파이는 같은 승패를 받되 직접 추측한 전적·추측 UI 상태를 만들어서는 안 됩니다.");
        game.Tick(now += game.Settings.ResultSeconds); game.Tick(now + 100);
        Check(game.Phase == GamePhase.MatchResults && reports == 1 && game.Snapshot(99, 1, now).Players.Single(player => player.Id == spy).IsSpy,
            "관전자 최종 결과에도 역할을 보존하고 완료 전적을 한 번만 발행해야 합니다.");
    }

    private static void VerifySpyCaught(DrawingMode mode)
    {
        var game = SpyGame(mode); int spy = SpyRole(game, true, 4), liar = SpyRole(game, false, 4);
        double now = SpyDiscussion(game); SpyAccuse(game, spy, now); SpyApprove(game, now);
        Check(game.Snapshot(99, 1, now).Players.Single(player => player.Id == spy) is { IsSpy: true, IsLiar: false, IsCaught: true },
            "스파이를 지목한 가결은 스파이 적발로 공개해야 합니다.");
        now += game.Snapshot(1, 1, now).RemainingSeconds + .01; game.Tick(now);
        var state = game.Snapshot(1, 1, now);
        Check(state.Phase == GamePhase.RoundResults && !game.Guess(liar, SPY_WORD, now) && !game.Guess(spy, SPY_WORD, now)
            && state.Players.Single(player => player.Id == liar).GuessOutcome == GuessOutcome.Hidden
            && state.Players.Where(player => player.Id == spy || player.Id == liar).All(player => player.RoundPoints == 0)
            && state.Players.Where(player => !player.IsSpectator && player.Id != spy && player.Id != liar).All(player => player.RoundPoints == 7),
            "스파이가 적발되면 라이어 추측 없이 시민 전체가 승리하고 라이어 팀은 생존 점수를 받지 않아야 합니다.");
    }

    private static void VerifySpyReentry(DrawingMode mode)
    {
        var game = SpyGame(mode); int spy = SpyRole(game, true, 4), liar = SpyRole(game, false, 4);
        var before = game.Snapshot(spy, 1, 0);
        game.Disconnect(spy, .1);
        Check(game.HoldsPlayerSeat(spy) && game.Join(spy, "복귀 스파이", 0, 0), "단절된 스파이 좌석으로 재접속해야 합니다.");
        var restored = game.Snapshot(spy, 1, .1);
        Check(restored.LocalIsSpy && !restored.LocalIsLiar && restored.Word == SPY_WORD && restored.DrawingOrder.SequenceEqual(before.DrawingOrder),
            "재접속은 스파이 역할·정답·차례를 다시 추첨하지 않고 복원해야 합니다.");
        Check(game.Join(5, "역할 공개 중 난입", 0, 0), "역할 공개 중 새 시민을 입장시켜야 합니다.");
        VerifySpySecret(game, spy, liar, .1);
        double now = game.Settings.RoleSeconds + .01; game.Tick(now);
        Check(game.Phase == GamePhase.Drawing && game.Join(6, "그리기 중 난입", 0, 0), "그리기 중 새 시민을 입장시켜야 합니다.");
        VerifySpySecret(game, spy, liar, now);
        while (game.Phase == GamePhase.Drawing) Check(game.EndTurn(game.ArtistId, now += .01), "난입한 시민도 그림 차례를 가져야 합니다.");
        Check(game.Join(7, "토론 중 난입", 0, 0), "토론 중 새 시민을 입장시켜야 합니다.");
        SpyAccuse(game, liar, now);
        Check(game.Join(8, "반론 중 난입", 0, 0), "반론 중 새 시민을 입장시켜야 합니다.");
        foreach (int id in new[] { 5, 6, 7, 8 })
            Check(game.Snapshot(id, 1, now) is { LocalIsSpy: false, LocalIsLiar: false, LocalIsSpectator: false, Word: SPY_WORD },
                "모든 중도 참가자는 스파이·라이어를 추가하지 않고 정답을 아는 시민이어야 합니다.");
        VerifySpySecret(game, spy, liar, now);
        var settings = SpySettings(mode); settings.AllowMidRoundJoin = false;
        var disabled = new GameSession(settings, SpyData(), 2);
        for (int id = 1; id <= 3; id++) disabled.Join(id, "화가", 0, 0);
        Check(disabled.Start(0) && disabled.Join(4, "입장 대기", 0, 0)
            && disabled.Snapshot(4, 1, 0) is { LocalIsSpectator: true, LocalIsSpy: false, LocalIsLiar: false, Word: "" },
            "난입 금지 방의 중도 입장은 정답을 모르는 관전이어야 합니다.");
    }

    private static void VerifySpyDisconnects(DrawingMode mode)
    {
        var game = SpyGame(mode); int spy = SpyRole(game, true, 4), liar = SpyRole(game, false, 4);
        double now = SpyDiscussion(game); SpyAccuse(game, spy, now); game.Disconnect(spy, now + .1);
        Check(game.Phase == GamePhase.Discussion && game.Join(spy, "복귀", 0, 0) && game.Snapshot(spy, 1, now).LocalIsSpy,
            "지목된 스파이의 연결이 끊기면 지목을 취소하고 재접속 역할은 유지해야 합니다.");
        SpyAccuse(game, liar, now + .2); SpyApprove(game, now + .2);
        now += .2 + game.Snapshot(1, 1, now + .2).RemainingSeconds + .01; game.Tick(now);
        game.Disconnect(spy, now);
        Check(game.Phase == GamePhase.Guessing && game.Join(spy, "추측 중 복귀", 0, 0) && !game.Guess(spy, SPY_WORD, now),
            "스파이 이탈·복귀가 라이어 추측을 종료하거나 스파이에게 추측권을 주면 안 됩니다.");
        game.Disconnect(liar, now + .1);
        Check(game.Phase == GamePhase.RoundResults, "실제 라이어가 추측 중 끊기면 남은 스파이를 기다리지 않고 채점해야 합니다.");
        var shortage = SpyGame(mode, 3);
        shortage.Disconnect(1, .1); shortage.Disconnect(2, .2);
        Check(shortage.Phase == GamePhase.MatchResults, "한 명만 연결되면 스파이 모드도 안전하게 경기를 종료해야 합니다.");
    }

    private static void VerifySpyNextRound(DrawingMode mode)
    {
        var game = SpyGame(mode, rounds: 2);
        double now = SpyDiscussion(game); SpyAccuse(game, SpyRole(game, true, 4, now), now); SpyApprove(game, now);
        now += game.Snapshot(1, 1, now).RemainingSeconds + .01; game.Tick(now);
        game.Tick(now += game.Settings.ResultSeconds);
        Check(game.Phase == GamePhase.RoleReveal && game.Round == 2, "다음 스파이 라운드를 시작해야 합니다.");
        int spy = SpyRole(game, true, 4, now), liar = SpyRole(game, false, 4, now);
        VerifySpySecret(game, spy, liar, now);
        Check(game.Snapshot(1, 1, now).Players.All(player => !player.IsCaught && !player.HasGuessed && player.RoundPoints == 0),
            "새 라운드에 이전 스파이 적발·추측·공개 상태가 남으면 안 됩니다.");
        var small = SpyGame(mode, 3, rounds: 2); now = SpyDiscussion(small);
        SpyAccuse(small, SpyRole(small, true, 3, now), now); SpyApprove(small, now);
        now += small.Snapshot(1, 1, now).RemainingSeconds + .01; small.Tick(now); small.Disconnect(1, now);
        small.Tick(now += small.Settings.ResultSeconds);
        Check(small.Phase == GamePhase.MatchResults && small.Round == 1, "두 명만 남은 다음 라운드에 시민 없는 스파이 배정을 하면 안 됩니다.");
        game.ReturnToLobby();
        Check(game.Snapshot(spy, 1, now) is { LocalIsSpy: false, LocalIsLiar: false, Word: "" }
            && game.Snapshot(99, 1, now).Players.All(player => !player.IsSpy && !player.IsLiar), "대기실로 돌아오면 스파이 역할을 제거해야 합니다.");
    }

    private static async Task VerifySpyDedicatedAsync()
    {
        var settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(SpySettings(), Json), Json)!;
        var data = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "spy1", Settings = settings };
        ServerDatabase.ValidateSettings(settings);
        var room = new DedicatedRoom(data, SpyData(), Array.Empty<ServerTopicData>(), 0, () => { });
        var sockets = Enumerable.Range(0, 6).Select(_ => new PolicySocket()).ToArray();
        var peers = sockets.Take(5).Select((socket, index) => new GameConnection(socket, "spy" + (index + 1), "test", CancellationToken.None)).ToList();
        RedeemTicketResponse Ticket(int index, bool observer = false) => new() { AccountId = peers[index].AccountId, Room = data,
            IsSpectator = observer, SpectatorOnly = observer, Profile = new ProfileData { AccountId = peers[index].AccountId, DisplayName = "연결 화가" + index } };
        try
        {
            for (int index = 0; index < 4; index++) Check(room.Join(peers[index], Ticket(index, index == 3), 0), "스파이 DS 참가자와 관전자를 입장시켜야 합니다.");
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
            await WaitAsync(() => sockets.Take(4).All(socket => socket.LastState?.Phase == GamePhase.RoleReveal), 3);
            Check(sockets.Take(3).Count(socket => socket.LastState!.LocalIsSpy) == 1 && sockets.Take(3).Count(socket => socket.LastState!.LocalIsLiar) == 1
                && sockets.Take(4).All(socket => socket.LastState!.Players.All(player => !player.IsSpy && !player.IsLiar)),
                "실제 연결별 DS 전송도 스파이·라이어 한 명을 배정하고 상대 정체를 숨겨야 합니다.");
            foreach (var socket in sockets.Take(4))
            {
                var state = socket.LastState!;
                Check(state.Word == (state.LocalIsLiar || state.LocalIsSpectator ? "" : SPY_WORD)
                    && !MismatchStrings(state).Contains(SPY_ALIAS), "DS 직렬화 상태가 각자의 정답 접근과 별칭 비밀을 지켜야 합니다.");
            }
            Check(room.Join(peers[4], Ticket(4), .2), "DS 스파이 경기에 시민이 난입해야 합니다.");
            await WaitAsync(() => sockets[4].LastState?.Phase == GamePhase.RoleReveal, 3);
            Check(sockets[4].LastState is { LocalIsSpy: false, LocalIsLiar: false, Word: SPY_WORD }, "DS 난입도 새 스파이를 만들면 안 됩니다.");
            int spyIndex = Array.FindIndex(sockets, socket => socket.LastState?.LocalIsSpy == true);
            var before = sockets[spyIndex].LastState!;
            peers.Add(new GameConnection(sockets[5], peers[spyIndex].AccountId, "test", CancellationToken.None));
            peers[spyIndex].Abort(); room.Disconnect(peers[spyIndex], .3);
            Check(room.Join(peers[5], Ticket(5), .4), "같은 계정으로 DS에 다시 접속해야 합니다.");
            await WaitAsync(() => sockets[5].LastState?.Phase == GamePhase.RoleReveal, 3);
            Check(sockets[5].LastState!.LocalPlayerId == before.LocalPlayerId && sockets[5].LastState!.LocalIsSpy == before.LocalIsSpy
                && sockets[5].LastState!.LocalIsLiar == before.LocalIsLiar && sockets[5].LastState!.Word == before.Word,
                "DS 계정 재접속은 연결 ID와 스파이·라이어 역할 및 단어를 복원해야 합니다.");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }
}
