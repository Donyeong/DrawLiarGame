using System.Text.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyOptionalLiarDatabaseAsync(string mainUrl)
    {
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "선택 라이어 DB 검증은 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_OPTIONAL_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_OPTIONAL_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25539 && scoped.Database == "postgres"
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_optional_test_[0-9a-f]{32}$"),
            "선택 라이어 DB 검증은 무작위 스키마의 전용 PostgreSQL만 사용합니다.");
        using var main = Client(mainUrl); await RequireHealthAsync(main);
        var users = new[] { await GuestAndEnterAsync(main), await GuestAndEnterAsync(main), await GuestAndEnterAsync(main) };
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "선택 라이어 검증은 전용 게임서버만 사용합니다.");
        using var game = Client(users[0].Login.GameServerUrl); await RequireHealthAsync(game);
        await RewardClusterPostAsync(game, "/internal/dedicated/register", new RegisterDedicatedRequest
        { NodeId = "dedicated-reward-qa", PublicUrl = "ws://127.0.0.1:25570/play", Capacity = 32 });
        var settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(OptionalSettings(), Json), Json)!;
        var assignment = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest { Settings = settings }, users[0].Session.SessionToken);
        var tickets = new RedeemTicketResponse[3];
        for (int index = 0; index < 3; index++)
        {
            var admission = index == 0 ? assignment : await JoinAsync(game, assignment.RoomId, users[index], false);
            tickets[index] = await RewardClusterPostAsync<RedeemTicketResponse>(game, "/internal/tickets/redeem",
                new RedeemTicketRequest { NodeId = "dedicated-reward-qa", RoomId = admission.RoomId, JoinTicket = admission.JoinTicket });
            Check(tickets[index].AccountId == users[index].Login.AccountId && tickets[index].Room.Settings.LiarMode == 2
                && tickets[index].Room.Settings.LiarCount == 1, "실제 방 생성·입장권 교환은 새 모드와 한 명 상한을 보존해야 합니다.");
        }
        MatchResultRequest? completed = null;
        var room = new DedicatedRoom(tickets[0].Room, OptionalData(), Array.Empty<ServerTopicData>(), 0, () => { }, matchCompleted: value => completed = value);
        var peers = users.Select(user => new GameConnection(new PolicySocket(), user.Login.AccountId, "optional-db-test", CancellationToken.None)).ToArray();
        try
        {
            for (int index = 0; index < 2; index++) Check(room.Join(peers[index], tickets[index], 0), "실제 계정 둘을 데디케이티드에 입장시켜야 합니다.");
            double now = 0; room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, now);
            Check(!room.Status().IsInProgress && completed == null, "실제 계정과 입장권을 사용해도 참가자 둘로는 시작할 수 없어야 합니다.");
            Check(room.Join(peers[2], tickets[2], now), "세 번째 실제 계정이 데디케이티드에 입장해야 합니다.");
            room.Receive(peers[0], new GameplayEnvelope { Type = "request", Kind = "start" }, now);
            Check(room.Status().IsInProgress, "세 번째 실제 계정이 참가하면 방장이 시작할 수 있어야 합니다.");
            for (int index = 0; index < 30 && completed == null; index++) room.Tick(now += 1000);
            Check(completed != null && completed.Players.Length == 3 && completed.Players.All(player => player.RoundsPlayed == 1
                && player.WeightedRoundParticipants == 3 && player.WeightedRoundScore == 3L * player.Score),
                "실제 데디케이티드 3인 경기의 완료 전적과 보상 가중치를 발행해야 합니다.");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RewardClusterPostAsync(game, "/internal/dedicated/matches", CloneProfileMatch(completed!))));
            foreach (var user in users)
            {
                var player = completed!.Players.Single(value => value.AccountId == user.Login.AccountId);
                int expected = MatchRewardRules.Calculate(ServerDatabase.RewardPolicy, player.WeightedRoundParticipants, player.WeightedRoundScore, 500);
                var receipt = await RewardGetAsync(game, completed.MatchId, user.Session.SessionToken);
                Check(receipt.Recorded && receipt.CoinReward == expected && receipt.Profile.Coins == 500 + expected && expected >= 4,
                    "3인 완료 경기 보상은 실제 점수로 계산하고 중복 보고에도 한 번만 지급해야 합니다.");
                var publicProfile = await GetAsync<PublicProfileData>(game, "/api/profiles/" + user.Login.AccountId, user.Session.SessionToken);
                Check(publicProfile.Stats.MatchesPlayed == 1 && publicProfile.RecentMatches.Single().PlayerCount == 3,
                    "3인 완료 경기는 프로필 전적에도 인원 셋으로 표시되어야 합니다.");
            }
            await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
            Check(await ProfileRowCountAsync(owner, "MatchRecord") == 1 && await ProfileRowCountAsync(owner, "AccountMatch") == 3,
                "동일 3인 경기의 병렬 보고는 한 경기·세 계정 ledger만 저장해야 합니다.");
            await using (var migrated = new NpgsqlCommand("SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=13", owner))
                Check(Convert.ToInt64(await migrated.ExecuteScalarAsync()) == 1, "기존 2인 전적을 위한 마이그레이션을 보존해야 합니다.");
            foreach (int invalid in new[] { 1, 65 })
            {
                bool rejected = false;
                await using var update = new NpgsqlCommand("UPDATE \"MatchRecord\" SET \"PlayerCount\"=$1 WHERE \"MatchId\"=$2", owner);
                update.Parameters.AddWithValue(invalid); update.Parameters.AddWithValue(Guid.Parse(completed!.MatchId));
                try { await update.ExecuteNonQueryAsync(); }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation) { rejected = true; }
                Check(rejected, "기존 전적 경계는 1명 또는 65명 전적을 여전히 거부해야 합니다.");
            }
            var historical = CloneProfileMatch(completed!); historical.MatchId = Guid.NewGuid().ToString();
            historical.Players = historical.Players.Take(2).ToArray();
            foreach (var player in historical.Players)
            {
                player.Rank = 1 + historical.Players.Count(other => other.Score > player.Score);
                player.WeightedRoundParticipants = 2;
                player.WeightedRoundScore = 2L * player.Score;
            }
            await RewardClusterPostAsync(game, "/internal/dedicated/matches", historical);
            foreach (var player in historical.Players)
            {
                var user = users.Single(value => value.Login.AccountId == player.AccountId);
                var publicProfile = await GetAsync<PublicProfileData>(game, "/api/profiles/" + player.AccountId, user.Session.SessionToken);
                Check(publicProfile.Stats.MatchesPlayed == 2
                    && publicProfile.RecentMatches.Any(match => match.MatchId == historical.MatchId && match.PlayerCount == 2),
                    "시작 최소 인원을 바꿔도 기존 2인 경기 보고와 프로필 전적 호환은 유지해야 합니다.");
            }
            Check(await ProfileRowCountAsync(owner, "MatchRecord") == 2 && await ProfileRowCountAsync(owner, "AccountMatch") == 5,
                "3인 새 경기와 2인 기존 전적은 각자의 인원대로 저장되어야 합니다.");
            Report("실제 PostgreSQL·HTTP 방/입장권·DS 2인 거부/3인 시작·완료 전적·4중복 정산·보상·기존 2인 전적 호환·스키마13 경계 검증");
        }
        finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    }
}
