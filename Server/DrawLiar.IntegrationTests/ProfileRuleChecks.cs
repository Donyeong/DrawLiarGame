using DrawLiar;
using DrawLiar.DedicatedServer;
using System.Text.Json;

internal static partial class Integration
{
    private static GameData ProfileGameData() => new()
    {
        Scoring = new ScoreRules { CitizenCorrectVote = 7, LiarCorrectGuess = 5, LiarUncaught = 3 },
        Topics = new[] { new TopicData { Name = "전적검증", Words = new[] { "검증사과" } } }
    };

    private static RoomSettings ProfileRoomSettings(int rounds = 3) => new()
    {
        RoundCount = rounds, AllowMidRoundJoin = false, Topics = new[] { "전적검증" }, RoleSeconds = 3, DrawSeconds = 5,
        DiscussionSeconds = 5, RebuttalSeconds = 0, VoteSeconds = 5, RevealSeconds = 3,
        GuessSeconds = 5, ResultSeconds = 5
    };

    private static async Task VerifyProfileRulesAsync()
    {
        var session = new GameSession(ProfileRoomSettings(), ProfileGameData(), 731);
        for (int id = 1; id <= 4; id++) Check(session.Join(id, "검증화가" + id, id, 0), "참가자가 입장해야 합니다.");
        Check(session.Join(99, "고정관전자", 0, 0, true, true), "관전자가 입장해야 합니다.");
        var completed = new List<CompletedMatchData>();
        session.MatchCompleted += completed.Add;
        double now = 0;
        Check(session.Start(now), "최소 인원을 채운 경기가 시작해야 합니다.");
        Check(session.Join(100, "도중관전자", 0, 0), "진행 중 입장이 가능해야 합니다.");
        var expected = Enumerable.Range(1, 4).ToDictionary(id => id, id => new CompletedMatchPlayerData { PlayerId = id });
        for (int round = 1; round <= 3; round++)
        {
            CompleteProfileRound(session, ref now, expected);
            Check(session.Phase == GamePhase.RoundResults, "라운드별 점수를 먼저 확정해야 합니다.");
            if (round == 1) session.Disconnect(4, now);
            AdvanceProfilePhase(session, ref now);
        }
        Check(session.Phase == GamePhase.MatchResults && completed.Count == 1, "완료 결과는 경기당 한 번만 발행해야 합니다.");
        var result = completed.Single();
        Check(Guid.TryParse(result.MatchId, out _) && result.RoundCount == 3 && result.Mode == DrawingMode.Relay
            && result.Players.Select(player => player.PlayerId).SequenceEqual(new[] { 1, 2, 3, 4 }),
            "도중 이탈자의 완료 라운드는 남기고 고정·도중 관전자는 전적에서 제외해야 합니다.");
        foreach (var player in result.Players)
        {
            var wanted = expected[player.PlayerId];
            Check(player.Score == wanted.Score && player.RoundsPlayed == wanted.RoundsPlayed
                && player.CitizenRounds == wanted.CitizenRounds && player.LiarRounds == wanted.LiarRounds
                && player.CorrectVotes == wanted.CorrectVotes && player.CorrectGuesses == wanted.CorrectGuesses
                && player.Rank == 1 + expected.Values.Count(other => other.Score > player.Score)
                && player.Won == session.Snapshot(1, 1, now).Winners.Contains(player.PlayerId),
                "전적은 실제 점수·역할·정답 투표/추측·동점 순위·기존 우승 판정을 보존해야 합니다.");
        }
        string frozen = JsonSerializer.Serialize(result, Json);
        for (int index = 0; index < 4; index++) session.Tick(now + 1000 + index);
        Check(completed.Count == 1, "종료 상태의 반복 Tick은 전적을 다시 발행하면 안 됩니다.");
        session.ReturnToLobby();
        Check(session.Join(4, "복귀화가", 4, 0) && session.Start(now += 100), "다음 경기를 새로 시작할 수 있어야 합니다.");
        var secondExpected = session.Snapshot(1, 1, now).Players.Where(player => !player.IsSpectator)
            .ToDictionary(player => player.Id, player => new CompletedMatchPlayerData { PlayerId = player.Id });
        for (int round = 0; round < 3; round++)
        {
            CompleteProfileRound(session, ref now, secondExpected);
            AdvanceProfilePhase(session, ref now);
        }
        Check(completed.Count == 2 && completed[1].MatchId != result.MatchId
            && JsonSerializer.Serialize(result, Json) == frozen,
            "다음 경기 초기화는 앞 경기 보고서를 바꾸지 않고 새 MatchId를 사용해야 합니다.");

        var tied = new GameSession(ProfileRoomSettings(1), ProfileGameData(), 11);
        for (int id = 1; id <= 3; id++) tied.Join(id, "동점화가" + id, 0, 0);
        CompletedMatchData? tieResult = null;
        tied.MatchCompleted += value => tieResult = value;
        now = 0; tied.Start(now);
        var tieExpected = Enumerable.Range(1, 3).ToDictionary(id => id, id => new CompletedMatchPlayerData());
        CompleteProfileRound(tied, ref now, tieExpected);
        AdvanceProfilePhase(tied, ref now);
        Check(tieResult != null && tieResult.Players.Count(player => player.Rank == 1 && player.Won) == 2
            && tieResult.Players.Single(player => !player.Won).Rank == 3,
            "동점 우승 두 명은 1위이고 다음 점수 참가자는 3위여야 합니다.");

        var timeout = new GameSession(ProfileRoomSettings(1), ProfileGameData(), 14);
        for (int id = 1; id <= 3; id++) timeout.Join(id, "시간초과화가" + id, 0, 0);
        CompletedMatchData? timeoutResult = null;
        timeout.MatchCompleted += value => timeoutResult = value;
        now = 0; timeout.Start(now);
        for (int index = 0; index < 25 && timeout.Phase != GamePhase.MatchResults; index++) timeout.Tick(now += 1000);
        Check(timeoutResult != null && timeoutResult.Players.All(player => player.RoundsPlayed == 1
            && player.CorrectVotes == 0 && player.CorrectGuesses == 0)
            && timeoutResult.Players.Single(player => player.LiarRounds == 1).Score == 3
            && timeoutResult.Players.Where(player => player.CitizenRounds == 1).All(player => player.Score == 0),
            "투표·정답 없는 시간초과 경기는 정답 통계를 올리지 않고 실제 생존 점수만 기록해야 합니다.");

        var departing = new GameSession(ProfileRoomSettings(1), ProfileGameData(), 15);
        for (int id = 1; id <= 4; id++) departing.Join(id, "도중이탈화가" + id, 0, 0);
        CompletedMatchData? departingResult = null;
        departing.MatchCompleted += value => departingResult = value;
        now = 0; departing.Start(now); departing.Disconnect(4, now);
        for (int index = 0; index < 25 && departing.Phase != GamePhase.MatchResults; index++) departing.Tick(now += 1000);
        var departed = departingResult?.Players.Single(player => player.PlayerId == 4);
        Check(departingResult?.Players.Length == 4 && departed?.RoundsPlayed == 1 && departed.Score == 0
            && departed.CitizenRounds + departed.LiarRounds == 1 && !departed.Won,
            "채점 전에 이탈한 라운드 참가자는 역할 이력을 남기되 받지 않은 점수·우승을 만들면 안 됩니다.");

        var aborted = new GameSession(ProfileRoomSettings(), ProfileGameData(), 12);
        for (int id = 1; id <= 3; id++) aborted.Join(id, "중단화가" + id, 0, 0);
        int abortReports = 0;
        aborted.MatchCompleted += _ => abortReports++;
        aborted.Start(0); aborted.Disconnect(2, 1); aborted.Disconnect(3, 2);
        Check(aborted.Phase == GamePhase.MatchResults && abortReports == 0,
            "채점한 라운드가 없는 중단 경기는 전적으로 만들면 안 됩니다.");

        var partial = new GameSession(ProfileRoomSettings(), ProfileGameData(), 13);
        for (int id = 1; id <= 4; id++) partial.Join(id, "일부화가" + id, 0, 0);
        CompletedMatchData? partialResult = null;
        partial.MatchCompleted += value => partialResult = value;
        now = 0; partial.Start(now);
        var partialExpected = Enumerable.Range(1, 4).ToDictionary(id => id, id => new CompletedMatchPlayerData());
        CompleteProfileRound(partial, ref now, partialExpected);
        AdvanceProfilePhase(partial, ref now);
        partial.Join(101, "미참가관전자", 0, 0);
        partial.Disconnect(2, now); partial.Disconnect(3, now); partial.Disconnect(4, now);
        Check(partialResult != null && partialResult.RoundCount == 1 && partialResult.Players.Length == 4
            && partialResult.Players.All(player => player.RoundsPlayed == 1),
            "중단 경기의 전적은 채점된 라운드만 계산하고 새 관전자를 포함하면 안 됩니다.");
        Report("경기 전적 1회 발행·역할/투표/정답·동점·중도 이탈·관전자 제외·이전 보고서 보존 검증");
        await VerifyProfileOutboxAsync();
    }

    private static void AdvanceProfilePhase(GameSession session, ref double now)
    {
        now += session.Snapshot(1, 1, now).RemainingSeconds + 0.01;
        session.Tick(now);
    }

    private static void CompleteProfileRound(GameSession session, ref double now, Dictionary<int, CompletedMatchPlayerData> expected)
    {
        Check(session.Phase == GamePhase.RoleReveal, "역할 공개부터 라운드를 검증해야 합니다.");
        int[] active = session.Snapshot(1, 1, now).Players.Where(player => player.IsConnected && !player.IsSpectator)
            .Select(player => player.Id).ToArray();
        double roleTime = now;
        int liar = active.Single(id => session.Snapshot(id, 1, roleTime).LocalIsLiar);
        AdvanceProfilePhase(session, ref now);
        while (session.Phase == GamePhase.Drawing)
            Check(session.EndTurn(session.ArtistId, now += 0.1), "참가자가 그림 차례를 마칠 수 있어야 합니다.");
        Check(session.Phase == GamePhase.Discussion, "모든 그림 차례 뒤 토론해야 합니다.");
        foreach (int id in active)
            Check(session.Vote(id, id == liar ? active.First(other => other != liar) : liar, now += 0.1), "정상 투표를 수락해야 합니다.");
        Check(session.Phase == GamePhase.Rebuttal && session.Snapshot(1, 1, now).AccusedPlayerId == liar,
            "전원 지목 뒤 한 명의 반론으로 진행해야 합니다.");
        Check(session.Snapshot(1, 1, now).Players.All(player => !player.IsLiar && player.RoundPoints == 0),
            "반론 중 역할이나 올바른 찬반 보상을 노출하면 안 됩니다.");
        foreach (int id in active.Where(id => id != liar))
            Check(session.Judge(id, liar, true, now += 0.1), "후보 외의 참가자가 찬반에 참여해야 합니다.");
        Check(session.Phase == GamePhase.LiarReveal, "찬반 가결 뒤 라이어를 공개해야 합니다.");
        AdvanceProfilePhase(session, ref now);
        Check(session.Guess(liar, "검증사과", now += 0.1), "라이어 정답을 채점해야 합니다.");
        foreach (int id in active)
        {
            var value = expected[id];
            value.RoundsPlayed++;
            if (id == liar) { value.LiarRounds++; value.CorrectGuesses++; value.Score += 5; }
            else { value.CitizenRounds++; value.CorrectVotes++; value.Score += 7; }
        }
    }

    private static async Task VerifyProfileOutboxAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "drawliar-profile-data-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ProfileGameData(), Json));
        try
        {
            using var registry = new RoomRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Dedicated:GameDataPath"] = path, ["Dedicated:NodeId"] = "profile-test-node", ["Dedicated:Capacity"] = "1" }).Build());
            var roomData = new ServerRoomData
            {
                RoomId = Guid.NewGuid().ToString(), OwnerAccountId = Guid.NewGuid().ToString(),
                Settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(ProfileRoomSettings(1), Json), Json)!
            };
            var sockets = Enumerable.Range(0, 4).Select(_ => new PolicySocket()).ToArray();
            var accounts = new[] { roomData.OwnerAccountId, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
            var connections = sockets.Select((socket, index) => new GameConnection(socket, accounts[index], "profile-test", CancellationToken.None)).ToArray();
            try
            {
                DedicatedRoom? room = null;
                for (int index = 0; index < connections.Length; index++)
                {
                    room = registry.Join(connections[index], new RedeemTicketResponse
                    {
                        AccountId = accounts[index], Room = roomData, IsSpectator = index == 3, SpectatorOnly = index == 3,
                        Profile = new ProfileData { AccountId = accounts[index], DisplayName = "입장화가" + index }
                    });
                    Check(room != null, "데디케이티드 방에 입장해야 합니다.");
                }
                await WaitAsync(() => sockets.All(socket => socket.LastState?.Players.Length == 4), 3);
                Check(sockets[0].LastState!.Players.All(player => player.AccountId == accounts[player.Id - 1]),
                    "프로필 조회용 AccountId는 입장권의 실제 계정과 일치해야 합니다.");
                double now = RoomRegistry.Now + 1;
                room!.Receive(connections[0], new GameplayEnvelope { Type = "request", Kind = "start" }, now);
                Check(room.Status().IsInProgress, "보고서 대기열이 비어 있으면 시작할 수 있어야 합니다.");
                for (int index = 0; index < 30 && registry.PendingResults().Length == 0; index++) room.Tick(now += 1000);
                var pending = registry.PendingResults().Single();
                Check(pending.NodeId == "profile-test-node" && pending.RoomId == roomData.RoomId && pending.RoundCount == 1
                    && pending.Players.Select(player => player.AccountId).Order().SequenceEqual(accounts.Take(3).Order())
                    && DateTimeOffset.TryParse(pending.PlayedAt, out _),
                    "완료 보고서에는 서버 노드·실제 계정·완료 시각만 매핑하고 관전자를 제외해야 합니다.");
                string frozen = JsonSerializer.Serialize(pending, Json);
                Check(room.Status().IsInProgress, "최종 라운드 결과 대기 중에도 완료 보고서가 이미 보존되어야 합니다.");
                foreach (var connection in connections)
                {
                    await connection.ReceiveAsync();
                    room.Disconnect(connection, now += 1);
                }
                var closed = room.Status();
                Check(closed.Closed, "정상 퇴장한 빈 방은 종료되어야 합니다.");
                registry.AcknowledgeHeartbeat(new[] { closed }, new[] { roomData.RoomId });
                Check(registry.Statuses().Length == 0 && JsonSerializer.Serialize(registry.PendingResults().Single(), Json) == frozen,
                    "방 삭제와 다음 로비 초기화는 미전송 완료 보고서를 삭제하거나 바꾸면 안 됩니다.");
                registry.AcknowledgeResult(pending.MatchId);
                registry.AcknowledgeResult(pending.MatchId);
                Check(registry.PendingResults().Length == 0, "저장 승인한 보고서는 멱등하게 대기열에서 제거해야 합니다.");
                Report("데디케이티드 계정 매핑·관전자 제외·방 삭제 후 미전송 전적 보존·승인 제거 검증");
            }
            finally { foreach (var connection in connections) await connection.DisposeAsync(); }
        }
        finally { File.Delete(path); }
    }
}
