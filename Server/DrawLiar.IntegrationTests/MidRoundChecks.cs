using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyMidRoundAsync()
    {
        foreach (DrawingMode mode in Enum.GetValues<DrawingMode>()) VerifyMidRoundFlow(mode);
        VerifyMidRoundSeats();
        await VerifyMidRoundDedicatedAsync();
        Report("난입 시민·차례 추가·투표/찬반·결과 소급 제외·꺼짐 관전·정상 퇴장/재접속 좌석·120초 만료 검증");
    }

    private static GameSession MidRoundGame(DrawingMode mode, bool allow = true, int count = 3, int rounds = 1)
    {
        var settings = ProfileRoomSettings(rounds); settings.AllowMidRoundJoin = allow; settings.Mode = mode;
        var game = new GameSession(settings, ProfileGameData(), 872);
        for (int id = 1; id <= count; id++) Check(game.Join(id, "화가" + id, 0, 0), "난입 검증 참가자가 입장해야 합니다.");
        Check(game.Start(0), "난입 검증 경기를 시작해야 합니다.");
        return game;
    }

    private static void VerifyMidRoundFlow(DrawingMode mode)
    {
        var game = MidRoundGame(mode);
        CompletedMatchData? completed = null; game.MatchCompleted += result => completed = result;
        double now = 0;
        var initial = game.Snapshot(1, 1, now);
        int liar = Enumerable.Range(1, 3).Single(id => game.Snapshot(id, 1, now).LocalIsLiar);
        Check(game.Join(4, "역할 중 난입", 0, 0), "역할 공개 중에도 일반 참가자가 입장해야 합니다.");
        var joined = game.Snapshot(4, 1, now);
        Check(!joined.LocalIsSpectator && !joined.LocalIsLiar && joined.Word == "검증사과"
            && joined.DrawingOrder.SequenceEqual(initial.DrawingOrder.Concat(new[] { 4 }))
            && joined.RemainingSeconds == initial.RemainingSeconds && joined.DrawingEpoch == initial.DrawingEpoch,
            "새 시민에게만 제시어를 주고 기존 순서·라이어·타이머·그림 epoch를 유지해야 합니다.");
        Check(game.Snapshot(liar, 1, now).LocalIsLiar && game.Snapshot(liar, 1, now).Word == "",
            "난입은 기존 라이어의 역할과 비밀 상태를 바꾸면 안 됩니다.");
        game.Tick(now = 3);
        initial = game.Snapshot(1, 1, now);
        Check(game.Join(5, "그리기 중 난입", 0, 0), "그리기 중 시민으로 입장해야 합니다.");
        joined = game.Snapshot(5, 1, now);
        Check(joined.ArtistId == initial.ArtistId && joined.RemainingSeconds == initial.RemainingSeconds
            && joined.CanvasVersion == initial.CanvasVersion && joined.DrawingOrder.Last() == 5,
            "현재 작가의 남은 시간을 바꾸지 않고 새 차례만 마지막에 추가해야 합니다.");
        var artists = new List<int>();
        while (game.Phase == GamePhase.Drawing) { artists.Add(game.ArtistId); Check(game.EndTurn(game.ArtistId, now += .1), "기존 차례를 정상 마쳐야 합니다."); }
        Check(artists.SequenceEqual(initial.DrawingOrder.Concat(new[] { 5 })), "난입 작가를 포함한 순서대로 실제 그리기를 진행해야 합니다.");
        int[] oldVoters = Enumerable.Range(1, 5).ToArray();
        foreach (int id in oldVoters.Take(4)) Check(game.Vote(id, id == liar ? oldVoters.First(other => other != liar) : liar, now), "토론 지목을 저장해야 합니다.");
        initial = game.Snapshot(1, 1, now);
        Check(game.Join(6, "토론 중 난입", 0, 0) && game.Phase == GamePhase.Discussion, "토론 중 난입도 새 투표자로 참여해야 합니다.");
        Check(game.Snapshot(6, 1, now).DrawingOrder.SequenceEqual(initial.DrawingOrder)
            && game.Snapshot(6, 1, now).RemainingSeconds == initial.RemainingSeconds
            && oldVoters.Take(4).All(id => game.Snapshot(id, 1, now).Players.Single(player => player.Id == id).HasVoted),
            "토론 중 난입은 이미 그린 순서·투표·시간을 초기화하면 안 됩니다.");
        int finalOld = oldVoters[4];
        Check(game.Vote(finalOld, finalOld == liar ? oldVoters.First(other => other != liar) : liar, now)
            && game.Phase == GamePhase.Discussion && game.Vote(6, liar, now) && game.Phase == GamePhase.Rebuttal,
            "새 참가자를 포함해 모두 지목하면 즉시 반론으로 진행해야 합니다.");
        initial = game.Snapshot(1, 1, now);
        int firstJudge = oldVoters.First(id => id != liar);
        Check(game.Judge(firstJudge, liar, true, now) && game.Join(7, "반론 중 난입", 0, 0), "반론 중에도 새 시민이 찬반에 참여해야 합니다.");
        joined = game.Snapshot(7, 1, now);
        Check(joined.AccusedPlayerId == liar && joined.BallotVersion == initial.BallotVersion
            && joined.RemainingSeconds == initial.RemainingSeconds && joined.JudgmentVoterCount == initial.JudgmentVoterCount + 1,
            "지목 대상·투표 버전·시간을 보존하고 새 찬반 투표자를 포함해야 합니다.");
        foreach (int id in Enumerable.Range(1, 7).Where(id => id != liar && id != firstJudge)) Check(game.Judge(id, liar, true, now), "난입 시민을 포함해 찬반을 저장해야 합니다.");
        Check(game.Phase == GamePhase.LiarReveal && game.Join(8, "공개 중 난입", 0, 0), "라이어 공개 중 입장도 시민이어야 합니다.");
        Check(!game.Snapshot(8, 1, now).LocalIsLiar && !game.Snapshot(8, 1, now).LocalIsSpectator, "공개 이후 새 라이어를 배정하면 안 됩니다.");
        AdvanceProfilePhase(game, ref now);
        Check(!game.Guess(8, "검증사과", now) && game.Guess(liar, "검증사과", now), "정답 제출 권한은 기존 라이어에게만 있어야 합니다.");
        Check(completed?.Players.Length == 8 && completed.Players.Single(player => player.PlayerId == 7) is { CitizenRounds: 1, CorrectVotes: 1, Score: 7 }
            && completed.Players.Single(player => player.PlayerId == 8) is { CitizenRounds: 1, Score: 0 },
            "참여한 라운드와 실제 찬반 점수만 새 시민 전적에 저장해야 합니다.");
        int[] winners = game.Snapshot(1, 1, now).Winners;
        game.Disconnect(5, now, false);
        Check(game.Join(9, "결과 중 난입", 0, 0), "결과 중 새 시민이 빈자리에 입장해야 합니다.");
        Check(game.Snapshot(9, 1, now).Players.Single(player => player.Id == 9).Score == 0
            && game.Snapshot(9, 1, now).Winners.SequenceEqual(winners) && completed!.Players.All(player => player.PlayerId != 9),
            "결과 입장은 이미 확정된 점수·우승·완료 전적을 소급하면 안 됩니다.");

        var disabled = MidRoundGame(mode, false);
        Check(disabled.Join(4, "다음 경기 대기", 0, 0) && disabled.Snapshot(4, 1, 0).LocalIsSpectator
            && disabled.Snapshot(4, 1, 0).Word == "" && disabled.Snapshot(4, 1, 0).DrawingOrder.Length == 3,
            "난입을 끈 방의 일반 입장은 이번 경기를 관전해야 합니다.");
        disabled.ReturnToLobby();
        Check(!disabled.Snapshot(4, 1, 0).LocalIsSpectator, "자동 관전자는 다음 대기실의 빈자리에 배정해야 합니다.");
        Check(disabled.Join(10, "명시 관전", 0, 0, true, true) && disabled.Start(0)
            && disabled.Snapshot(10, 1, 0).LocalIsSpectator && disabled.Snapshot(10, 1, 0).Word == "",
            "명시 관전은 방 옵션과 관계없이 관전을 유지해야 합니다.");
    }

    private static void VerifyMidRoundSeats()
    {
        var game = MidRoundGame(DrawingMode.Relay, count: 8);
        var before = game.Snapshot(1, 1, 0);
        game.Disconnect(1, 0);
        Check(game.HoldsPlayerSeat(1) && !game.Join(9, "정원 초과", 0, 0) && game.Join(1, "복귀", 0, 0), "장애 재접속 좌석은 보존하고 아홉 번째 동시 참가자를 막아야 합니다.");
        Check(game.Snapshot(1, 1, 0).LocalIsLiar == before.LocalIsLiar && game.Snapshot(1, 1, 0).DrawingOrder.SequenceEqual(before.DrawingOrder), "재접속은 역할과 순서를 복원해야 합니다.");
        game.Disconnect(1, 0, false);
        Check(!game.HoldsPlayerSeat(1) && game.Join(9, "빈자리 난입", 0, 0) && !game.Join(1, "가득 찬 방 복귀", 0, 0), "정상 퇴장 좌석은 새 시민이 쓰고 가득 차면 이전 참가자 복귀도 거부해야 합니다.");
        Check(game.ActiveCount == 8 && game.Snapshot(9, 2, 0).Players.Count(player => !player.IsSpectator) == 8
            && game.Snapshot(9, 2, 0).DrawingOrder.Length == 9, "활성 카드는 정원 안에 두고 지난 작가의 순서 기록은 보존해야 합니다.");
        CompletedMatchData? result = null; game.MatchCompleted += value => result = value;
        double now = 0;
        for (int index = 0; index < 30 && game.Phase != GamePhase.MatchResults; index++) game.Tick(now += 1000);
        Check(result?.Players.Length == 9 && result.Players.All(player => player.RoundsPlayed == 1), "한 경기의 아홉 번째 교체 참가자도 완료 전적에 포함해야 합니다.");

        var partial = MidRoundGame(DrawingMode.Relay, rounds: 2);
        CompletedMatchData? partialResult = null; partial.MatchCompleted += value => partialResult = value;
        now = 0;
        while (partial.Phase != GamePhase.RoundResults) partial.Tick(now += 1000);
        Check(partial.Join(4, "채점 후 참가", 0, 0), "채점 후 입장을 준비해야 합니다.");
        partial.Disconnect(1, now, false); partial.Disconnect(2, now, false); partial.Disconnect(3, now, false);
        partial.Tick(now += 1000);
        Check(!partial.Snapshot(4, 1, now).Winners.Contains(4) && partialResult!.Players.All(player => player.PlayerId != 4), "완료 라운드가 없는 결과 난입자에게 우승이나 전적을 주면 안 됩니다.");
    }

    private static async Task VerifyMidRoundDedicatedAsync()
    {
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "P1", Settings = new ServerRoomSettings { AllowMidRoundJoin = true, DrawSeconds = 180, RoleSeconds = 3, Topics = new[] { "과일" } } };
        var room = new DedicatedRoom(roomData, new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } }, Array.Empty<ServerTopicData>(), 0, () => { });
        var peers = new List<GameConnection>();
        RedeemTicketResponse Ticket(string account) => new() { AccountId = account, Room = roomData, IsSpectator = true, Profile = new ProfileData { AccountId = account, DisplayName = account } };
        try
        {
            for (int index = 1; index <= 8; index++)
            { var connection = new GameConnection(new PolicySocket(), "P" + index, "test", CancellationToken.None); peers.Add(connection); Check(room.Join(connection, Ticket(connection.AccountId), 0), "현재 옵션이 오래된 자동관전 티켓보다 우선해야 합니다."); }
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
            peers[7].Abort(); room.Disconnect(peers[7], 1);
            var newcomer = new GameConnection(new PolicySocket(), "late", "test", CancellationToken.None); peers.Add(newcomer);
            Check(!room.Join(newcomer, Ticket("late"), 120.99), "120초 장애 유예 동안 새 참가자는 예약 좌석을 차지하면 안 됩니다.");
            room.Tick(121);
            Check(room.Join(newcomer, Ticket("late"), 121.1) && room.Status().PlayerAccountIds.Length == 8
                && room.Status().SpectatorAccountIds.Length == 0, "유예가 끝나면 기존 전적을 남기면서 신규 시민에게 좌석을 배정해야 합니다.");
            await Task.CompletedTask;
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }
}
