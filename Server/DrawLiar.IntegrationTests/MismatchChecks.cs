using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyMismatchAsync()
    {
        VerifyMismatchSettings();
        VerifyMismatchTopics();
        foreach (DrawingMode mode in Enum.GetValues<DrawingMode>())
        {
            VerifyMismatchFlow(mode, true);
            VerifyMismatchFlow(mode, false);
        }
        VerifyMismatchFlow(DrawingMode.Relay, null);
        VerifyMismatchNextRound();
        await VerifyMismatchDedicatedAsync();
        Report("미스매치 단어 분배·JSON 비밀 보호·재접속/난입·찬반 재토론·다수 정답 추측/결과·설정 직렬화 검증");
    }

    private static RoomSettings MismatchSettings(DrawingMode mode = DrawingMode.Relay, int rounds = 1) => new()
    {
        LiarMode = LiarMode.Mismatch, LiarCount = 7, Mode = mode, RoundCount = rounds, Topics = new[] { "검증 주제" },
        RoleSeconds = 3, DrawSeconds = 5, DiscussionSeconds = 20, RebuttalSeconds = 5,
        RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5
    };

    private static GameData MismatchData(params string[] words) => new()
    {
        Topics = new[] { new TopicData { Name = "검증 주제", Words = words.Length == 0 ? new[] { "미스매치사과", "미스매치배" } : words } },
        Scoring = new ScoreRules { CitizenCorrectVote = 7, LiarCorrectGuess = 5, LiarUncaught = 9 }
    };

    private static GameSession MismatchGame(RoomSettings? settings = null, GameData? data = null, int seed = 19, int count = 3)
    {
        var game = new GameSession(settings ?? MismatchSettings(), data ?? MismatchData(), seed);
        for (int id = 1; id <= count; id++) Check(game.Join(id, "검증 화가" + id, 0, 0), "미스매치 참가자를 준비해야 합니다.");
        Check(game.Join(99, "관전자", 0, 0, true, true), "명시 관전자를 준비해야 합니다.");
        return game;
    }

    private static void VerifyMismatchSettings()
    {
        var local = MismatchSettings(); local.Validate();
        Check(local.LiarCount == 1 && local.Copy().LiarMode == LiarMode.Mismatch, "미스매치는 라이어 한 명으로 정규화하고 복사에 모드를 보존해야 합니다.");
        var unknown = new RoomSettings { LiarMode = (LiarMode)99 }; unknown.Validate();
        Check(unknown.LiarMode == LiarMode.Classic, "잘못된 로컬 모드는 기본 모드로 정규화해야 합니다.");
        var server = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(local, Json), Json)!;
        ServerDatabase.ValidateSettings(server);
        Check(server.LiarMode == 1 && server.LiarCount == 1, "서버 JSON 설정은 미스매치 모드와 한 명 제한을 보존해야 합니다.");
        string stored = JsonSerializer.Serialize(server, ServerRuntime.Json);
        var restored = JsonSerializer.Deserialize<ServerRoomSettings>(stored, ServerRuntime.Json)!;
        var dedicated = JsonSerializer.Deserialize<RoomSettings>(stored, Json)!;
        Check(restored.LiarMode == 1 && dedicated.LiarMode == LiarMode.Mismatch
            && JsonSerializer.Deserialize<ServerRoomSettings>("{}", ServerRuntime.Json)!.LiarMode == 0,
            "저장·서버 재로드·DS 변환은 모드를 유지하고 기존 JSON은 기본 모드로 읽어야 합니다.");
        foreach (int value in new[] { -1, 2, 99 })
        {
            restored.LiarMode = value;
            bool rejected = false;
            try { ServerDatabase.ValidateSettings(restored); }
            catch (ApiException error) when (error.Code == "InvalidSettings" && error.Status == 400) { rejected = true; }
            Check(rejected, "서버가 알 수 없는 라이어 모드를 거부해야 합니다.");
        }
    }

    private static void VerifyMismatchTopics()
    {
        foreach (string[] words in new[] { new[] { "한단어" }, new[] { "사 과", " 사과 " }, new[] { "ＡＢＣ", "abc", "a b c" } })
        {
            var data = MismatchData(words);
            data.Topics = data.Topics.Concat(new[] { new TopicData { Name = "선택하지 않은 주제", Words = new[] { "다른사과", "다른배" } } }).ToArray();
            var game = MismatchGame(data: data);
            Check(!game.CanStart && !game.Start(0) && game.Phase == GamePhase.Lobby && game.Round == 0,
                "선택한 주제에 서로 다른 정규화 단어가 없으면 다른 주제로 우회하지 않고 시작을 거부해야 합니다.");
        }
        var classicSettings = MismatchSettings(); classicSettings.LiarMode = LiarMode.Classic; classicSettings.LiarCount = 1;
        var classic = MismatchGame(classicSettings, MismatchData("한단어"));
        Check(classic.Start(0), "기본 모드의 한 단어 주제는 계속 사용할 수 있어야 합니다.");
        int classicLiar = Enumerable.Range(1, 3).Single(id => classic.Snapshot(id, 1, 0).LocalIsLiar);
        Check(classic.Snapshot(classicLiar, 1, 0).Word == "" && classic.Snapshot(99, 1, 0).Word == ""
            && classic.Snapshot(1, 1, 0).MismatchWord == "", "기본 모드의 라이어·관전자 비밀과 미스매치 필드 기본값은 유지해야 합니다.");
        for (int seed = 0; seed < 8; seed++)
        {
            var settings = MismatchSettings(); settings.Topics = new[] { "검증 주제", "한단어 주제" };
            var data = MismatchData(); data.Topics = data.Topics.Concat(new[] { new TopicData { Name = "한단어 주제", Words = new[] { "한단어" } } }).ToArray();
            var game = MismatchGame(settings, data, seed, 8);
            Check(game.Start(0) && game.Snapshot(1, 1, 0).Topic == "검증 주제", "여러 주제 중 미스매치에 유효한 주제만 선택해야 합니다.");
            var groups = Enumerable.Range(1, 8).GroupBy(id => GameRules.NormalizeGuess(game.Snapshot(id, 1, 0).Word)).OrderBy(group => group.Count()).ToArray();
            Check(groups.Length == 2 && groups[0].Count() == 1 && groups[1].Count() == 7, "여덟 명도 정확히 한 명에게만 다른 단어를 배정해야 합니다.");
        }
        var reloaded = MismatchGame(data: MismatchData("한단어"));
        Check(!reloaded.CanStart && reloaded.Start(0, MismatchData()), "시작 시 갱신된 단어 데이터가 유효하면 이전 단어 부족 상태에서 시작할 수 있어야 합니다.");
        var configured = MismatchGame(classicSettings, MismatchData("한단어"));
        Check(configured.CanStart && configured.Configure(MismatchSettings()) && !configured.CanStart
            && !configured.Start(0), "기본 모드에서 미스매치로 변경하면 한 단어 주제로 시작할 수 없어야 합니다.");
        Check(configured.Configure(classicSettings) && configured.CanStart && configured.Start(0),
            "미스매치에서 기본 모드로 복원하면 동일 한 단어 주제로 다시 시작할 수 있어야 합니다.");
    }

    private static void VerifyMismatchFlow(DrawingMode mode, bool? correctGuess)
    {
        var game = MismatchGame(MismatchSettings(mode));
        CompletedMatchData? completed = null; game.MatchCompleted += result => completed = result;
        Check(game.Start(0), "미스매치 경기를 시작해야 합니다.");
        var words = Enumerable.Range(1, 3).ToDictionary(id => id, id => game.Snapshot(id, 1, 0).Word);
        var groups = words.GroupBy(pair => GameRules.NormalizeGuess(pair.Value)).OrderBy(group => group.Count()).ToArray();
        Check(groups.Length == 2 && groups[0].Count() == 1 && groups[1].Count() == 2, "미스매치 단어는 한 명과 나머지 두 명으로 나뉘어야 합니다.");
        int minority = groups[0].Single().Key; string alternate = words[minority], common = groups[1].First().Value;
        VerifyMismatchSecret(game, words, common, alternate, 0);
        var before = game.Snapshot(minority, 1, 0);
        game.Disconnect(minority, .1);
        Check(game.Join(minority, "재접속", 0, 0), "소수 단어 참가자가 재접속해야 합니다.");
        Check(game.Snapshot(minority, 1, .1).Word == alternate && !game.Snapshot(minority, 1, .1).LocalIsLiar,
            "재접속은 소수 단어를 유지하되 아직 역할을 공개하면 안 됩니다.");
        Check(game.Join(4, "난입 참가자", 0, 0), "미스매치 진행 중 일반 난입이 가능해야 합니다."); words.Add(4, common);
        var joined = game.Snapshot(4, 1, .1);
        Check(joined.Word == common && !joined.LocalIsLiar && !joined.LocalIsSpectator
            && joined.DrawingOrder.SequenceEqual(before.DrawingOrder.Concat(new[] { 4 })) && joined.DrawingEpoch == before.DrawingEpoch,
            "난입은 다수 단어를 받고 기존 차례·비밀·그림 epoch를 초기화하지 않아야 합니다.");
        double now = 3.1; game.Tick(now);
        VerifyMismatchSecret(game, words, common, alternate, now);
        while (game.Phase == GamePhase.Drawing) Check(game.EndTurn(game.ArtistId, now += .1), "각자 그림 차례를 마칠 수 있어야 합니다.");
        Check(game.Phase == GamePhase.Discussion, "미스매치 그림 이후 토론해야 합니다.");
        VerifyMismatchSecret(game, words, common, alternate, now);
        void Nominate()
        {
            foreach (int id in words.Keys) Check(game.Vote(id, id == minority ? words.Keys.First(other => other != minority) : minority, now), "자신을 제외한 참가자를 지목할 수 있어야 합니다.");
            Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(1, 1, now).AccusedPlayerId == minority, "모든 지목 뒤 소수 단어 참가자가 반론해야 합니다.");
            VerifyMismatchSecret(game, words, common, alternate, now);
        }
        Nominate(); int previousBallot = game.BallotVersion;
        foreach (int id in words.Keys.Where(id => id != minority)) Check(game.Judge(id, minority, false, now), "반대 투표를 처리해야 합니다.");
        Check(game.Phase == GamePhase.Discussion && game.BallotVersion > previousBallot, "부결되면 새로운 지목 버전으로 다시 토론해야 합니다.");
        VerifyMismatchSecret(game, words, common, alternate, now);
        Nominate();
        foreach (int id in words.Keys.Where(id => id != minority)) Check(game.Judge(id, minority, true, now), "찬성 투표를 처리해야 합니다.");
        Check(game.Phase == GamePhase.LiarReveal, "가결된 뒤 소수 단어 참가자의 역할을 공개해야 합니다.");
        foreach (int id in words.Keys.Append(99))
        {
            var snapshot = game.Snapshot(id, 1, now);
            Check(snapshot.Players.Single(player => player.Id == minority).IsLiar && snapshot.LocalIsLiar == (id == minority)
                && snapshot.MismatchWord == "" && snapshot.Word == (id == 99 ? "" : words[id]),
                "라이어 공개 후에도 본인 배정 단어만 보내고 별도 소수 단어 필드·관전자 단어는 숨겨야 합니다.");
        }
        now += game.Settings.RevealSeconds + .01; game.Tick(now);
        Check(game.Phase == GamePhase.Guessing && !game.Guess(99, common, now)
            && !game.Guess(words.Keys.First(id => id != minority), common, now), "정답 추측 권한은 소수 단어 참가자에게만 있어야 합니다.");
        Check(game.Snapshot(minority, 1, now).Word == alternate && game.Snapshot(minority, 1, now).MismatchWord == ""
            && !MismatchStrings(game.Snapshot(minority, 1, now)).Contains(common), "추측 중에는 다수 정답을 소수 참가자에게 노출하면 안 됩니다.");
        if (correctGuess.HasValue) Check(game.Guess(minority, correctGuess.Value ? common : alternate, now), "소수 참가자의 정답 제출을 처리해야 합니다.");
        else { now += game.Settings.GuessSeconds; game.Tick(now); }
        foreach (int id in words.Keys.Append(99))
        {
            var result = game.Snapshot(id, 1, now);
            Check(result.Phase == GamePhase.RoundResults && result.Word == common && result.MismatchWord == alternate,
                "라운드 결과에서는 모든 참가자와 관전자에게 다수 정답·소수 단어를 동일하게 공개해야 합니다.");
            var player = result.Players.Single(value => value.Id == minority);
            Check(player.RoundPoints == (correctGuess == true ? 5 : 0)
                && player.GuessOutcome == (correctGuess == true ? GuessOutcome.Correct : correctGuess == false ? GuessOutcome.Incorrect : GuessOutcome.Unanswered)
                && result.Players.Where(value => !value.IsSpectator && value.Id != minority).All(value => value.RoundPoints == 7),
                "다수 정답을 맞힌 경우만 추측 점수를 주고 기존 찬성 점수와 오답·미응답 결과를 유지해야 합니다.");
        }
        Check(completed != null && completed.Players.Count(player => player.LiarRounds == 1) == 1
            && completed.Players.Single(player => player.PlayerId == minority).CorrectGuesses == (correctGuess == true ? 1 : 0),
            "역할을 숨겨도 서버 완료 전적에는 소수 역할과 실제 정답 여부를 기록해야 합니다.");
        game.Tick(now += game.Settings.ResultSeconds);
        Check(words.Keys.Append(99).All(id => game.Snapshot(id, 1, now) is { Phase: GamePhase.MatchResults } result
            && result.Word == common && result.MismatchWord == alternate), "최종 결과에서도 동일한 다수 정답·소수 단어를 유지해야 합니다.");
    }

    private static void VerifyMismatchSecret(GameSession game, Dictionary<int, string> words, string common, string alternate, double now)
    {
        foreach (int id in words.Keys.Append(99))
        {
            var snapshot = game.Snapshot(id, 1, now);
            Check(!snapshot.LocalIsLiar && snapshot.Players.All(player => !player.IsLiar && !player.IsCaught)
                && snapshot.Word == (id == 99 ? "" : words[id]) && snapshot.MismatchWord == "",
                "공개 전에는 역할을 숨기고 본인 단어만 전달해야 합니다.");
            var payload = MismatchStrings(snapshot);
            Check(id == 99 ? !payload.Contains(common) && !payload.Contains(alternate)
                : !payload.Contains(words[id] == common ? alternate : common), "직렬화된 참가자·관전자 JSON에 다른 배정 단어가 남으면 안 됩니다.");
        }
    }

    private static string[] MismatchStrings(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, GameplayWire.Json));
        var values = new List<string>();
        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) values.Add(element.GetString()!);
            else if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) Collect(property.Value);
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Collect(item);
        }
        Collect(document.RootElement); return values.ToArray();
    }

    private static void VerifyMismatchNextRound()
    {
        var game = MismatchGame(MismatchSettings(rounds: 2)); Check(game.Start(0), "두 라운드 미스매치를 시작해야 합니다.");
        double now = 0;
        for (int index = 0; index < 20 && game.Phase != GamePhase.RoundResults; index++) game.Tick(now += 1000);
        Check(game.Phase == GamePhase.RoundResults && game.Snapshot(99, 1, now).MismatchWord.Length > 0, "첫 결과의 소수 단어를 확인해야 합니다.");
        game.Tick(now += game.Settings.ResultSeconds);
        Check(game.Phase == GamePhase.RoleReveal && game.Round == 2, "다음 라운드로 정상 진행해야 합니다.");
        var words = Enumerable.Range(1, 3).ToDictionary(id => id, id => game.Snapshot(id, 1, now).Word);
        var groups = words.GroupBy(pair => pair.Value).OrderBy(group => group.Count()).ToArray();
        Check(groups.Length == 2 && groups[0].Count() == 1, "새 라운드는 다시 소수 한 명을 배정해야 합니다.");
        VerifyMismatchSecret(game, words, groups[1].First().Value, groups[0].First().Value, now);
    }

    private static async Task VerifyMismatchDedicatedAsync()
    {
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "mismatch1",
            Settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(MismatchSettings(), Json), Json)! };
        ServerDatabase.ValidateSettings(roomData.Settings);
        var room = new DedicatedRoom(roomData, MismatchData(), Array.Empty<ServerTopicData>(), 0, () => { });
        var sockets = Enumerable.Range(0, 5).Select(_ => new PolicySocket()).ToArray();
        var peers = sockets.Select((socket, index) => new GameConnection(socket, "mismatch" + (index + 1), "test", CancellationToken.None)).ToArray();
        RedeemTicketResponse Ticket(int index, bool observer = false) => new() { AccountId = peers[index].AccountId, Room = roomData,
            IsSpectator = observer, SpectatorOnly = observer, Profile = new ProfileData { AccountId = peers[index].AccountId, DisplayName = "연결 검증" + index } };
        try
        {
            for (int index = 0; index < 4; index++) Check(room.Join(peers[index], Ticket(index, index == 3), 0), "DS 참가자·관전자를 입장시켜야 합니다.");
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, .1);
            await WaitAsync(() => sockets.Take(4).All(socket => socket.LastState?.Phase == GamePhase.RoleReveal), 3);
            var groups = sockets.Take(3).GroupBy(socket => socket.LastState!.Word).OrderBy(group => group.Count()).ToArray();
            string common = groups[1].Key, alternate = groups[0].Key;
            Check(room.Status().Settings.LiarMode == 1 && room.Status().Settings.LiarCount == 1
                && sockets.Take(4).All(socket => !socket.LastState!.LocalIsLiar && socket.LastState.Players.All(player => !player.IsLiar)),
                "DS 설정과 실제 연결 snapshot에도 미스매치 비밀 규칙을 적용해야 합니다.");
            for (int index = 0; index < 4; index++)
            {
                var snapshot = sockets[index].LastState!; var payload = MismatchStrings(new GameplayEnvelope { Type = "state", State = snapshot });
                Check(index == 3 ? snapshot.Word == "" && !payload.Contains(common) && !payload.Contains(alternate)
                    : !payload.Contains(snapshot.Word == common ? alternate : common), "실제 DS 연결별 JSON은 다른 단어와 관전자 정답을 숨겨야 합니다.");
            }
            Check(room.Join(peers[4], Ticket(4), .2), "DS 난입 참가자를 입장시켜야 합니다.");
            await WaitAsync(() => sockets[4].LastState?.Phase == GamePhase.RoleReveal, 3);
            var joined = sockets[4].LastState!;
            Check(joined.Word == common && !joined.LocalIsLiar && joined.MismatchWord == "",
                "DS 난입은 다수 단어를 받고 소수 역할을 추가하지 않아야 합니다.");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }
}
