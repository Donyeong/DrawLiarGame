using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyMatchRewardsAsync(string mainUrl)
    {
        VerifyMatchRewardSessions();
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "코인 보상 검증은 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_REWARD_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_REWARD_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25539 && scoped.Database == "postgres"
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_reward_test_[0-9a-f]{32}$"),
            "코인 보상 검증은 무작위 스키마의 전용 PostgreSQL만 사용합니다.");
        await VerifyMatchRewardMigrationAsync(scoped);
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
        using var main = Client(mainUrl); await RequireHealthAsync(main);
        var users = new List<TestUser>();
        for (int index = 0; index < 9; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "보상 검증은 전용 게임서버만 사용합니다.");
        using var game = Client(users[0].Login.GameServerUrl); await RequireHealthAsync(game);
        await RewardClusterPostAsync(game, "/internal/dedicated/register", new RegisterDedicatedRequest
        { NodeId = "dedicated-reward-qa", PublicUrl = "ws://127.0.0.1:25570/play", Capacity = 32 });
        var assignment = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest(), users[0].Session.SessionToken);
        for (int index = 0; index < 8; index++)
        {
            var ticket = index == 0 ? assignment : await JoinAsync(game, assignment.RoomId, users[index], false);
            var redeemed = await RewardClusterPostAsync<RedeemTicketResponse>(game, "/internal/tickets/redeem",
                new RedeemTicketRequest { NodeId = "dedicated-reward-qa", RoomId = ticket.RoomId, JoinTicket = ticket.JoinTicket });
            Check(redeemed.AccountId == users[index].Login.AccountId, "보상 대상은 실제 입장권을 교환한 계정이어야 합니다.");
        }
        string[] accounts = users.Take(8).Select(user => user.Login.AccountId).ToArray();
        var first = RewardFixture(accounts.Take(3).ToArray(), assignment.RoomId, 1, 3, new[] { 4, 4, 0 });
        var pending = await RewardGetAsync(game, first.MatchId, users[0].Session.SessionToken);
        Check(!pending.Recorded && pending.CoinReward == 0 && pending.Profile.Coins == 500,
            "미정산 결과 조회는 현재 잔액만 반환하고 코인을 지급하면 안 됩니다.");
        await RewardHttpErrorAsync(game, HttpMethod.Get, "/api/matches/" + first.MatchId + "/reward", null, null, HttpStatusCode.Unauthorized);
        await RewardHttpErrorAsync(game, HttpMethod.Get, "/api/matches/" + first.MatchId + "/reward", null, users[0].Login.SessionToken, HttpStatusCode.Unauthorized);
        await RewardHttpErrorAsync(game, HttpMethod.Post, "/internal/dedicated/matches", first, users[0].Session.SessionToken, HttpStatusCode.Unauthorized);
        await RewardHttpErrorAsync(game, HttpMethod.Get, "/api/matches/invalid/reward", null, users[0].Session.SessionToken, HttpStatusCode.BadRequest);
        Report("보상 미정산 조회·게임 세션 범위·클러스터 보고 권한 검증");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RewardClusterPostAsync(game, "/internal/dedicated/matches", CloneProfileMatch(first))));
        var reordered = CloneProfileMatch(first); reordered.Players = reordered.Players.Reverse().ToArray();
        reordered.RoomId = reordered.RoomId.ToUpperInvariant();
        reordered.PlayedAt = DateTimeOffset.Parse(reordered.PlayedAt).ToOffset(TimeSpan.FromHours(9)).ToString("O");
        await RewardClusterPostAsync(game, "/internal/dedicated/matches", reordered);
        int[] firstCoins = { 18, 18, 6 };
        for (int index = 0; index < 3; index++)
        {
            var receipt = await RewardGetAsync(game, first.MatchId, users[index].Session.SessionToken);
            Check(receipt.Recorded && receipt.MatchId == first.MatchId && receipt.CoinReward == firstCoins[index]
                && receipt.Profile.AccountId == accounts[index] && receipt.Profile.Coins == 500 + firstCoins[index],
                "3명 한 라운드는 실제 점수에 비례해18/18/6코인을 한 번만 지급해야 합니다.");
        }
        var outsider = await RewardGetAsync(game, first.MatchId, users[8].Session.SessionToken);
        Check(!outsider.Recorded && outsider.CoinReward == 0 && outsider.Profile.AccountId == users[8].Login.AccountId && outsider.Profile.Coins == 500,
            "다른 계정의 경기 보상은 공개하지 않고 조회자의 재화만 반환해야 합니다.");
        Check(await ProfileRowCountAsync(owner, "MatchRecord") == 1 && await ProfileRowCountAsync(owner, "AccountMatch") == 3,
            "동일 경기8개 병렬 보고와 정규화 재시도는 전적·보상 ledger를 하나만 저장해야 합니다.");
        await RewardGetAsync(game, first.MatchId, users[0].Session.SessionToken);
        Check((await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins == 518, "보상 조회의 반복 요청은 재지급하면 안 됩니다.");
        var conflicting = CloneProfileMatch(first); conflicting.Players[0].WeightedRoundScore--;
        await RewardClusterErrorAsync(game, conflicting, HttpStatusCode.Conflict, "MatchResultConflict");
        Report("실제 PostgreSQL 정산·0점 기본 보상·8중복 보고/정규화·조회 반복 멱등 검증");

        var eight = RewardFixture(accounts, assignment.RoomId, 2, 8, new[] { 6, 4, 0, 0, 0, 0, 0, 0 });
        await RewardClusterPostAsync(game, "/internal/dedicated/matches", eight);
        var large = await RewardGetAsync(game, eight.MatchId, users[0].Session.SessionToken);
        Check(large.CoinReward == 80 && large.Profile.Coins == 598,
            "8명 두 라운드6점은 참여32코인+점수48코인으로3명보다 인원·라운드에 비례해 증가해야 합니다.");
        Check((await RewardGetAsync(game, eight.MatchId, users[2].Session.SessionToken)).CoinReward == 32,
            "8명 두 라운드0점 참가자도 완료 참여분32코인을 받아야 합니다.");
        var simultaneous = Enumerable.Range(1, 8).Select(score => RewardFixture(accounts.Take(3).ToArray(), assignment.RoomId, 1, 3,
            new[] { score, score, 0 })).ToArray();
        for (int index = 0; index < simultaneous.Length; index += 2) simultaneous[index].Players = simultaneous[index].Players.Reverse().ToArray();
        await Task.WhenAll(simultaneous.Select(request => RewardClusterPostAsync(game, "/internal/dedicated/matches", request)));
        Check((await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins == 754
            && (await database.ProfileAsync(Guid.Parse(accounts[1]))).Coins == 738
            && (await database.ProfileAsync(Guid.Parse(accounts[2]))).Coins == 586,
            "서로 다른8경기가 같은 계정을 동시에 정산해도 계정 lock으로 잔액과 합계를 보존해야 합니다.");
        int beforePurchase = (await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins;
        var product = ServerDatabase.ShopProducts.First(item => item.Price > 0 && item.Price < beforePurchase && item.Accessory != 0);
        var purchaseMatch = RewardFixture(accounts.Take(3).ToArray(), assignment.RoomId, 1, 3, new[] { 1, 1, 0 });
        await Task.WhenAll(RewardClusterPostAsync(game, "/internal/dedicated/matches", purchaseMatch),
            PostAsync<ProfileData>(game, "/api/shop/purchase", new PurchaseRequest { ProductId = product.Id, OperationId = Guid.NewGuid().ToString() }, users[0].Session.SessionToken));
        Check((await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins == beforePurchase + 9 - product.Price,
            "상점 구매와 보상이 동시에 발생해도 추가·차감 둘 다 보존해야 합니다.");
        Report("인원/완료 라운드/점수 비례·다른 경기 동시 정산·구매와 보상 동시 잔액 검증");

        long savedMatches = await ProfileRowCountAsync(owner, "MatchRecord");
        int savedCoins = (await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins;
        foreach (Action<MatchResultRequest> change in new Action<MatchResultRequest>[]
        {
            value => value.Players[0].WeightedRoundParticipants = -1,
            value => value.Players[0].WeightedRoundParticipants = 65,
            value => value.Players[0].WeightedRoundScore = -1,
            value => value.Players[0].WeightedRoundScore = 257,
            value => { value.Players[0].WeightedRoundParticipants = 0; value.Players[0].WeightedRoundScore = 1; },
            value => value.Players[0].AccountId = users[8].Login.AccountId,
            value => value.NodeId = "wrong-reward-node"
        })
        {
            var invalid = RewardFixture(accounts.Take(3).ToArray(), assignment.RoomId, 1, 3, new[] { 4, 4, 0 }); change(invalid);
            await RewardClusterErrorAsync(game, invalid, null, null);
        }
        Check(await ProfileRowCountAsync(owner, "MatchRecord") == savedMatches
            && (await database.ProfileAsync(Guid.Parse(accounts[0]))).Coins == savedCoins,
            "범위를 벗어난 가중치·미참가 계정·다른 노드 보고는 전적과 코인을 부분 저장하면 안 됩니다.");
        await using (var cap = new NpgsqlCommand("UPDATE \"Account\" SET \"Coins\"=$2 WHERE \"Id\"=$1", owner))
        { cap.Parameters.AddWithValue(Guid.Parse(accounts[0])); cap.Parameters.AddWithValue(int.MaxValue - 2); await cap.ExecuteNonQueryAsync(); }
        var capped = RewardFixture(accounts.Take(3).ToArray(), assignment.RoomId, 1, 3, new[] { 4, 4, 0 });
        await RewardClusterPostAsync(game, "/internal/dedicated/matches", capped);
        var cappedReceipt = await RewardGetAsync(game, capped.MatchId, users[0].Session.SessionToken);
        Check(cappedReceipt.CoinReward == 2 && cappedReceipt.Profile.Coins == int.MaxValue,
            "잔액 상한에서는 실제 지급 가능한2코인만 ledger에 기록하고 정수 overflow를 막아야 합니다.");
        await RewardClusterPostAsync(game, "/internal/dedicated/matches", capped);
        Check((await RewardGetAsync(game, capped.MatchId, users[0].Session.SessionToken)).CoinReward == 2,
            "상한에 도달한 보고서를 재시도해도 원래 실제 지급액을 보존해야 합니다.");
        Report("가중치/참가/노드 검증·실제 지급액 ledger·잔액 상한/overflow·재시도 검증");
        await VerifyDedicatedRewardMappingAsync(game, database, users, assignment.RoomId);
    }

    private static async Task VerifyDedicatedRewardMappingAsync(HttpClient game, ServerDatabase database, List<TestUser> users, string roomId)
    {
        var active = users.Skip(3).Take(3).ToArray(); var observer = users[8];
        var tickets = active.Append(observer).ToArray();
        var before = new Dictionary<string, int>();
        foreach (var user in tickets) before[user.Login.AccountId] = (await database.ProfileAsync(Guid.Parse(user.Login.AccountId))).Coins;
        var roomData = new ServerRoomData
        {
            RoomId = roomId, NodeId = "dedicated-reward-qa", OwnerAccountId = active[0].Login.AccountId,
            Settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(ProfileRoomSettings(1), Json), Json)!
        };
        MatchResultRequest? completed = null;
        var room = new DedicatedRoom(roomData, ProfileGameData(), Array.Empty<ServerTopicData>(), 0, () => { }, matchCompleted: value => completed = value);
        var peers = tickets.Select(user => new GameConnection(new PolicySocket(), user.Login.AccountId, "reward-test", CancellationToken.None)).ToArray();
        try
        {
            for (int index = 0; index < peers.Length; index++)
                Check(room.Join(peers[index], new RedeemTicketResponse
                {
                    AccountId = tickets[index].Login.AccountId, Room = roomData, Profile = tickets[index].Session.Profile,
                    IsSpectator = index == 3, SpectatorOnly = index == 3
                }, 0), "데디케이티드 보상 검증 참가자가 입장해야 합니다.");
            double now = 0; room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, now);
            for (int index = 0; index < 30 && completed == null; index++) room.Tick(now += 1000);
            Check(completed != null && completed.Players.Length == 3 && completed.Players.All(player => player.WeightedRoundParticipants == 3
                && player.WeightedRoundScore == 3L * player.Score) && completed.Players.All(player => player.AccountId != observer.Login.AccountId),
                "데디케이티드는 실제 연결 계정에 게임 가중치를 매핑하고 관전자 보상 보고서를 만들면 안 됩니다.");
            await RewardClusterPostAsync(game, "/internal/dedicated/matches", completed!);
            foreach (var player in completed!.Players)
            {
                var user = active.Single(value => value.Login.AccountId == player.AccountId);
                var reward = await RewardGetAsync(game, completed.MatchId, user.Session.SessionToken);
                int earned = 6 + 3 * player.Score;
                Check(reward.Recorded && reward.CoinReward == earned && reward.Profile.Coins == before[player.AccountId] + earned,
                    "실제 게임→데디케이티드 보고→HTTP→DB→개인 조회에서 동일한 보상을 보존해야 합니다.");
            }
            var spectator = await RewardGetAsync(game, completed.MatchId, observer.Session.SessionToken);
            Check(!spectator.Recorded && spectator.Profile.Coins == before[observer.Login.AccountId], "관전자는 실제 데디케이티드 경기 종료 후에도 코인을 받으면 안 됩니다.");
            Report("실제 GameSession→DS 계정/가중치 매핑→인증HTTP→PostgreSQL 정산→자기 보상 조회·관전자 제외 검증");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }

    private static void VerifyMatchRewardSessions()
    {
        foreach (int count in new[] { 3, 8 })
        {
            var game = RewardGame(count, 2);
            var expected = Enumerable.Range(1, count).ToDictionary(id => id, id => new CompletedMatchPlayerData());
            CompletedMatchData? result = null; game.MatchCompleted += value => result = value;
            double now = 0;
            for (int round = 0; round < 2; round++) { CompleteProfileRound(game, ref now, expected); AdvanceProfilePhase(game, ref now); }
            Check(result != null && result.Players.All(player => player.WeightedRoundParticipants == count * 2
                && player.WeightedRoundScore == (long)count * expected[player.PlayerId].Score),
                "완료 라운드 인원과 각 라운드 실제 득점만 참가자의 보상 가중치에 누적해야 합니다.");
            Check(game.Snapshot(1, 1, now).IsMatchComplete && game.Snapshot(1, 1, now).MatchId == result!.MatchId,
                "완료 스냅샷은 정산 조회에 쓰는 실제 MatchId를 전달해야 합니다.");
        }
        var partial = RewardGame(4, 3);
        CompletedMatchData? partialResult = null; partial.MatchCompleted += value => partialResult = value;
        var firstExpected = Enumerable.Range(1, 4).ToDictionary(id => id, id => new CompletedMatchPlayerData());
        double time = 0; CompleteProfileRound(partial, ref time, firstExpected); AdvanceProfilePhase(partial, ref time);
        int departedId = Enumerable.Range(1, 4).Reverse().First(id => !partial.Snapshot(id, 1, time).LocalIsLiar);
        partial.Disconnect(departedId, time, false);
        Check(partial.Join(5, "두 번째 난입", 0, 0) && partial.Join(99, "관전자", 0, 0, true, true), "난입과 관전을 준비해야 합니다.");
        var secondExpected = Enumerable.Range(1, 4).Where(id => id != departedId).Append(5).ToDictionary(id => id, id => new CompletedMatchPlayerData());
        CompleteProfileRound(partial, ref time, secondExpected);
        Check(partial.Join(6, "결과 난입", 0, 0), "채점 이후 난입을 준비해야 합니다.");
        foreach (int id in secondExpected.Keys.Where(id => id != 1).Append(6)) partial.Disconnect(id, time, false);
        AdvanceProfilePhase(partial, ref time);
        Check(partialResult != null && partialResult.RoundCount == 2 && partialResult.Players.All(player => player.PlayerId is not 6 and not 99)
            && partialResult.Players.Single(player => player.PlayerId == departedId).WeightedRoundParticipants == 4
            && partialResult.Players.Single(player => player.PlayerId == departedId).WeightedRoundScore == 4L * firstExpected[departedId].Score
            && partialResult.Players.Single(player => player.PlayerId == 5).WeightedRoundParticipants == 5
            && partialResult.Players.Single(player => player.PlayerId == 5).WeightedRoundScore == 5L * secondExpected[5].Score
            && partialResult.Players.Single(player => player.PlayerId == 1).WeightedRoundParticipants == 9,
            "퇴장 현재 라운드는 보상0·이전 완료분 보존, 난입은 참가한 완료분만, 관전/결과난입/미완료 다음 라운드는 제외해야 합니다.");
        var aborted = RewardGame(3, 3); int reports = 0; aborted.MatchCompleted += _ => reports++;
        aborted.Disconnect(2, 1, false); aborted.Disconnect(3, 2, false);
        Check(reports == 0 && !aborted.Snapshot(1, 1, 2).IsMatchComplete, "한 라운드도 채점하지 않은 중단 경기는 정산 대상이 아니어야 합니다.");
        var timeout = RewardGame(3, 1); CompletedMatchData? timed = null; timeout.MatchCompleted += value => timed = value;
        time = 0; for (int index = 0; index < 30 && timed == null; index++) timeout.Tick(time += 1000);
        Check(timed != null && timed.Players.Any(player => player.Score == 0 && player.WeightedRoundParticipants == 3 && player.WeightedRoundScore == 0),
            "0점인 시민도 끝낸 라운드의 참여 가중치를 보존해야 합니다.");
        var departing = RewardGame(3, 1); CompletedMatchData? gone = null; departing.MatchCompleted += value => gone = value;
        departing.Disconnect(3, 0, false); time = 0; for (int index = 0; index < 30 && gone == null; index++) departing.Tick(time += 1000);
        Check(gone != null && gone.Players.Single(player => player.PlayerId == 3) is { RoundsPlayed: 1, WeightedRoundParticipants: 0, WeightedRoundScore: 0 },
            "시작 즉시 이탈자는 기존 전적을 남기되 끝내지 않은 라운드 참여 보상을 받으면 안 됩니다.");
        Report("게임 판정 실제 가중치·3/8명·0점·난입/퇴장/관전·부분완료·미완료 중단 검증");
    }

    private static GameSession RewardGame(int count, int rounds)
    {
        var settings = ProfileRoomSettings(rounds); settings.AllowMidRoundJoin = true;
        var game = new GameSession(settings, ProfileGameData(), 713);
        for (int id = 1; id <= count; id++) Check(game.Join(id, "보상화가" + id, 0, 0), "검증 참가자가 입장해야 합니다.");
        Check(game.Start(0), "검증 경기를 시작해야 합니다."); return game;
    }

    private static MatchResultRequest RewardFixture(string[] accounts, string roomId, int rounds, int count, int[] scores)
    {
        return new MatchResultRequest
        {
            NodeId = "dedicated-reward-qa", RoomId = roomId, MatchId = Guid.NewGuid().ToString(), PlayedAt = DateTimeOffset.UtcNow.ToString("O"), Mode = 0, RoundCount = rounds,
            Players = accounts.Select((account, index) => new MatchPlayerResult
            {
                AccountId = account, Score = scores[index], Rank = 1 + scores.Count(score => score > scores[index]), Won = scores[index] == scores.Max(),
                RoundsPlayed = rounds, CitizenRounds = rounds, WeightedRoundParticipants = (long)rounds * count, WeightedRoundScore = (long)count * scores[index]
            }).ToArray()
        };
    }

    private static Task<MatchRewardResponse> RewardGetAsync(HttpClient game, string matchId, string token) => GetAsync<MatchRewardResponse>(game, "/api/matches/" + matchId + "/reward", token);

    private static async Task<HttpResponseMessage> RewardClusterSendAsync(HttpClient game, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: Json) };
        request.Headers.Add("X-Cluster-Key", Environment.GetEnvironmentVariable("Cluster__Key") ?? throw new InvalidOperationException("검증 클러스터 키가 필요합니다."));
        return await game.SendAsync(request);
    }

    private static async Task RewardClusterPostAsync(HttpClient game, string path, object payload)
    {
        using var response = await RewardClusterSendAsync(game, path, payload);
        Check(response.StatusCode == HttpStatusCode.NoContent, "검증 클러스터 요청이 성공해야 합니다: " + path + " / " + await response.Content.ReadAsStringAsync());
    }

    private static async Task<T> RewardClusterPostAsync<T>(HttpClient game, string path, object payload)
    {
        using var response = await RewardClusterSendAsync(game, path, payload); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidDataException("클러스터 검증 응답이 비었습니다.");
    }

    private static async Task RewardClusterErrorAsync(HttpClient game, MatchResultRequest payload, HttpStatusCode? expected, string? code)
    {
        using var response = await RewardClusterSendAsync(game, "/internal/dedicated/matches", payload);
        Check(expected.HasValue ? response.StatusCode == expected.Value : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            "잘못된 보상 보고서는 거부해야 합니다.");
        if (code != null) Check((await response.Content.ReadFromJsonAsync<ApiError>(Json))?.Code == code, "보상 거부 오류 코드가 일치해야 합니다.");
    }

    private static async Task RewardHttpErrorAsync(HttpClient game, HttpMethod method, string path, object? payload, string? token, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload != null) request.Content = JsonContent.Create(payload, options: Json);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await game.SendAsync(request); Check(response.StatusCode == status, "보상 API 오류 상태가 일치해야 합니다: " + path);
    }

    private static async Task VerifyMatchRewardMigrationAsync(NpgsqlConnectionStringBuilder parent)
    {
        string schema = "drawliar_reward_legacy_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(parent.ConnectionString) { SearchPath = schema, Pooling = false };
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var accounts = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray(); Guid room = Guid.NewGuid();
            await SeedProfilePreviousSchemaAsync(owner, accounts, room);
            var assembly = typeof(ServerDatabase).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
            {
                int version = int.Parse(resource.Split('.')[^2].Split('_')[0]); if (version is < 6 or > 11) continue;
                using var stream = assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream);
                await using (var apply = new NpgsqlCommand(await reader.ReadToEndAsync(), owner)) await apply.ExecuteNonQueryAsync();
                await using var applied = new NpgsqlCommand("INSERT INTO \"SchemaVersion\" (\"Version\") VALUES ($1)", owner); applied.Parameters.AddWithValue(version); await applied.ExecuteNonQueryAsync();
            }
            var legacy = ProfileMatchFixture(accounts.Take(3).ToArray(), room, DateTimeOffset.UtcNow.AddDays(-1), 0);
            legacy.Players = legacy.Players.OrderBy(player => player.AccountId, StringComparer.Ordinal).ToArray();
            string hash = ServerRuntime.Hash(JsonSerializer.Serialize(new
            {
                legacy.NodeId, legacy.RoomId, legacy.MatchId, legacy.PlayedAt, legacy.Mode, legacy.RoundCount,
                Players = legacy.Players.Select(player => new { player.AccountId, player.Score, player.Rank, player.Won, player.RoundsPlayed,
                    player.CitizenRounds, player.LiarRounds, player.CorrectVotes, player.CorrectGuesses }).ToArray()
            }, Json));
            await using (var insert = new NpgsqlCommand("INSERT INTO \"MatchRecord\" (\"MatchId\",\"RoomId\",\"PlayedAt\",\"PlayerCount\",\"Mode\",\"RoundCount\",\"PayloadHash\") VALUES ($1,$2,$3,3,0,2,$4)", owner))
            { insert.Parameters.AddWithValue(Guid.Parse(legacy.MatchId)); insert.Parameters.AddWithValue(room); insert.Parameters.AddWithValue(DateTimeOffset.Parse(legacy.PlayedAt)); insert.Parameters.AddWithValue(hash); await insert.ExecuteNonQueryAsync(); }
            foreach (var player in legacy.Players)
            {
                await using var insert = new NpgsqlCommand("INSERT INTO \"AccountMatch\" (\"MatchId\",\"AccountId\",\"Score\",\"Rank\",\"Won\",\"RoundsPlayed\",\"CitizenRounds\",\"LiarRounds\",\"CorrectVotes\",\"CorrectGuesses\") VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)", owner);
                object[] values = { Guid.Parse(legacy.MatchId), Guid.Parse(player.AccountId), player.Score, player.Rank, player.Won, player.RoundsPlayed,
                    player.CitizenRounds, player.LiarRounds, player.CorrectVotes, player.CorrectGuesses };
                foreach (object value in values) insert.Parameters.Add(new NpgsqlParameter { Value = value }); await insert.ExecuteNonQueryAsync();
            }
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync(); await database.InitializeAsync(); await database.RecordMatchAsync(legacy);
            Check((await database.ProfileAsync(accounts[0])).Coins == 321 && (await database.PublicProfileAsync(accounts[0], accounts[0])).Stats.MatchesPlayed == 1,
                "이전 전적 마이그레이션과 legacy 보고서 재시도는 기존 전적·잔액을 보존하고 소급 보상을 지급하면 안 됩니다.");
            await using var reward = new NpgsqlCommand("SELECT sum(\"CoinReward\") FROM \"AccountMatch\"", owner);
            Check(Convert.ToInt64(await reward.ExecuteScalarAsync()) == 0, "기존 경기의 보상 ledger는0으로 초기화해야 합니다.");
            Report("기존 스키마11→12·반복 마이그레이션·기존 payload hash 재시도·소급 지급 제외 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner); await drop.ExecuteNonQueryAsync();
        }
    }
}
