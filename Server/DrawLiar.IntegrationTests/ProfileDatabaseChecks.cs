using DrawLiar;
using DrawLiar.Server;
using Npgsql;
using System.Text.Json;

internal static partial class Integration
{
    private static async Task VerifyProfileDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString);
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439,
            "프로필 DB 검증은 전용 로컬 임시 PostgreSQL 25439 포트만 사용합니다.");
        string schema = "drawliar_profile_test_" + Guid.NewGuid().ToString("N");
        scoped.SearchPath = schema; scoped.Pooling = false; scoped.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            Guid[] accounts = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            Guid legacyRoom = Guid.NewGuid();
            await SeedProfilePreviousSchemaAsync(owner, accounts, legacyRoom);
            await SeedProfileSixAsync(owner);
            var previousChecks = await ProfileChecksAsync(owner);
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            await VerifyJudgmentMigrationAsync(owner, previousChecks);
            var legacy = await database.PublicProfileAsync(accounts[0], accounts[0]);
            Check(legacy.Friendship == "Self" && legacy.AccountId == accounts[0].ToString()
                && legacy.DisplayName == "기존프로필0" && legacy.AvatarColor == 2 && legacy.Accessory == 1
                && DateTimeOffset.Parse(legacy.JoinedAt) == new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)
                && legacy.RecentMatches.Length == 0 && StatsZero(legacy.Stats),
                "기존 계정의 가입일·외형을 보존하고 존재하지 않는 과거 전적을 만들면 안 됩니다.");
            Check((await database.ProfileAsync(accounts[0])).Coins == 321,
                "프로필 마이그레이션은 기존 재화를 수정하면 안 됩니다.");
            Guid newAccount = await database.DevelopmentAccountAsync("신규프로필");
            var fresh = await database.PublicProfileAsync(accounts[0], newAccount);
            Check(fresh.Friendship == "None" && StatsZero(fresh.Stats) && fresh.RecentMatches.Length == 0,
                "신규 계정은 모든 전적이 0이고 관계 없음으로 표시해야 합니다.");
            CheckPublicProfileFields(legacy);

            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "None", "None");
            await database.RequestFriendAsync(accounts[0], accounts[1]);
            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "Outgoing", "Incoming");
            await database.RespondFriendAsync(accounts[1], accounts[0], false);
            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "None", "None");
            await database.RequestFriendAsync(accounts[1], accounts[0]);
            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "Incoming", "Outgoing");
            await database.RespondFriendAsync(accounts[0], accounts[1], true);
            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "Friends", "Friends");
            await database.RemoveFriendAsync(accounts[0], accounts[1]);
            await CheckProfileFriendshipAsync(database, accounts[0], accounts[1], "None", "None");
            await ExpectProfileErrorAsync(() => database.PublicProfileAsync(accounts[0], Guid.NewGuid()), "AccountNotFound", 404);
            try { ServerRuntime.AccountId("invalid-guid"); throw new InvalidOperationException("잘못된 계정 ID를 수락했습니다."); }
            catch (ApiException exception) when (exception.Code == "InvalidAccount" && exception.Status == 400) { }
            Report("격리 PostgreSQL 프로필 마이그레이션·기본 전적 0·개인정보 제외·친구 요청/수락/삭제 양방향 검증");

            DateTimeOffset startTime = DateTimeOffset.UtcNow.AddHours(-1);
            var first = ProfileMatchFixture(accounts.Take(3).ToArray(), legacyRoom, startTime, 0);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.RecordMatchAsync(CloneProfileMatch(first))));
            var reordered = CloneProfileMatch(first);
            reordered.Players = reordered.Players.Reverse().ToArray();
            reordered.RoomId = reordered.RoomId.ToUpperInvariant();
            reordered.PlayedAt = DateTimeOffset.Parse(reordered.PlayedAt).ToOffset(TimeSpan.FromHours(9)).ToString("O");
            await database.RecordMatchAsync(reordered);
            Check((await database.PublicProfileAsync(accounts[1], accounts[0])).Stats.MatchesPlayed == 1
                && await ProfileRowCountAsync(owner, "MatchRecord") == 1 && await ProfileRowCountAsync(owner, "AccountMatch") == 3,
                "동일 결과의 병렬·재시도·정규화 요청은 경기와 참가자 전적을 한 번만 저장해야 합니다.");
            var changed = CloneProfileMatch(first);
            changed.Players[0].Score = 30; changed.Players[1].Rank = 2;
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(changed), "MatchResultConflict", 409);
            await using (var delete = new NpgsqlCommand("DELETE FROM \"Room\" WHERE \"RoomId\"=$1", owner))
            { delete.Parameters.AddWithValue(legacyRoom); await delete.ExecuteNonQueryAsync(); }
            await database.RecordMatchAsync(CloneProfileMatch(first));
            for (int index = 1; index < 13; index++)
                await database.RecordMatchAsync(ProfileMatchFixture(accounts.Take(3).ToArray(), legacyRoom, startTime.AddMinutes(index), index));
            var recorded = await database.PublicProfileAsync(accounts[1], accounts[0]);
            Check(recorded.Stats.MatchesPlayed == 13 && recorded.Stats.MatchesWon == 13 && recorded.Stats.TotalScore == 208
                && recorded.Stats.BestScore == 22 && recorded.Stats.RoundsPlayed == 26 && recorded.Stats.CitizenRounds == 13
                && recorded.Stats.LiarRounds == 13 && recorded.Stats.CorrectVotes == 13 && recorded.Stats.CorrectGuesses == 13,
                "저장된 13경기만 점수·최고점·우승·역할·정답 전적에 누적해야 합니다.");
            Check(recorded.RecentMatches.Length == 10 && recorded.RecentMatches.Select(match => match.Score).SequenceEqual(Enumerable.Range(13, 10).Reverse())
                && recorded.RecentMatches.All(match => match.PlayerCount == 3 && match.RoundCount == 2 && match.Rank == 1 && match.Won)
                && recorded.RecentMatches.Select(match => match.PlayedAt).SequenceEqual(recorded.RecentMatches.Select(match => match.PlayedAt).OrderDescending())
                && recorded.RecentMatches.Select(match => match.Mode).Distinct().Order().SequenceEqual(new[] { 0, 1 }),
                "최근 경기는 최신 10건만 내림차순으로 표시하고 동점·모드·인원·라운드를 보존해야 합니다.");
            Check((await database.PublicProfileAsync(accounts[0], accounts[1])).RecentMatches.All(match => match.Rank == 1 && match.Won)
                && (await database.PublicProfileAsync(accounts[0], accounts[2])).RecentMatches.All(match => match.Rank == 3 && !match.Won)
                && (await database.PublicProfileAsync(accounts[0], accounts[2])).Stats.CorrectVotes == 26
                && StatsZero((await database.PublicProfileAsync(accounts[0], accounts[3])).Stats),
                "동점 우승과 라이어 역할의 올바른 찬반을 기록하며 보고서에 없는 관전자는 제외해야 합니다.");
            CheckPublicProfileFields(recorded);
            Report("전적 병렬 멱등 저장·동점 순위·실제 누적 통계·최근 10경기·방 삭제 후 보고/재시도 검증");

            var invalid = new List<MatchResultRequest>();
            void AddInvalid(Action<MatchResultRequest> alter)
            { var request = ProfileMatchFixture(accounts.Take(3).ToArray(), legacyRoom, startTime, 0); alter(request); invalid.Add(request); }
            AddInvalid(request => request.Players[0].Score = -1);
            AddInvalid(request => request.Players[0].RoundsPlayed = 0);
            AddInvalid(request => request.Players[0].CitizenRounds = 2);
            AddInvalid(request => request.Players[0].CorrectVotes = 3);
            AddInvalid(request => request.Players[0].CorrectGuesses = 2);
            AddInvalid(request => request.Players[0].Rank = 2);
            AddInvalid(request => request.Players[1].AccountId = request.Players[0].AccountId);
            AddInvalid(request => request.Players = request.Players.Take(2).ToArray());
            AddInvalid(request => request.RoundCount = 0);
            AddInvalid(request => request.Mode = 2);
            AddInvalid(request => request.MatchId = Guid.Empty.ToString());
            AddInvalid(request => request.PlayedAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
            foreach (var request in invalid) await ExpectProfileErrorAsync(() => database.RecordMatchAsync(request), "InvalidMatchResult", 400);
            var wrongNode = ProfileMatchFixture(accounts.Take(3).ToArray(), legacyRoom, startTime, 0);
            wrongNode.NodeId = "other-test-node";
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(wrongNode), "InvalidMatchAuthority", 403);
            var wrongRoom = ProfileMatchFixture(accounts.Take(3).ToArray(), Guid.NewGuid(), startTime, 0);
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(wrongRoom), "InvalidMatchAuthority", 403);
            var unadmitted = ProfileMatchFixture(accounts.Take(3).ToArray(), legacyRoom, startTime, 0);
            unadmitted.Players[0].AccountId = newAccount.ToString();
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(unadmitted), "InvalidMatchParticipant", 403);
            Check(await ProfileRowCountAsync(owner, "MatchRecord") == 13 && await ProfileRowCountAsync(owner, "AccountMatch") == 39,
                "잘못된 보고서·서버·미참가 계정은 통계를 부분 저장하면 안 됩니다.");

            await VerifyProfileRoomAdmissionsAsync(database, owner);
            await using (var ban = new NpgsqlCommand("UPDATE \"Account\" SET \"IsBanned\"=true WHERE \"Id\"=$1", owner))
            { ban.Parameters.AddWithValue(accounts[2]); await ban.ExecuteNonQueryAsync(); }
            await ExpectProfileErrorAsync(() => database.PublicProfileAsync(accounts[0], accounts[2]), "AccountNotFound", 404);
            Report("잘못된 ID/보고서·노드 권한·미참가 전적·정지 계정 조회 거부 및 통계 보존 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
            Report("프로필 검증 임시 스키마 삭제");
        }
        await VerifyFreshJudgmentSchemaAsync(connectionString);
    }

    private static string ProfileMigration(string suffix)
    {
        var assembly = typeof(ServerDatabase).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task SeedProfileSixAsync(NpgsqlConnection owner)
    {
        await using (var migrate = new NpgsqlCommand(ProfileMigration("006_ProfileMatches.sql"), owner)) await migrate.ExecuteNonQueryAsync();
        await using (var version = new NpgsqlCommand("INSERT INTO \"SchemaVersion\" (\"Version\") VALUES (6)", owner)) await version.ExecuteNonQueryAsync();
        var checks = await ProfileChecksAsync(owner);
        string legacy = checks.Single(pair => pair.Value == "\"CorrectVotes\"<=\"CitizenRounds\"AND\"CorrectGuesses\"<=\"LiarRounds\"").Key;
        Check(legacy.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'), "초기 CHECK 이름은 안전한 PostgreSQL 식별자여야 합니다.");
        await using (var rename = new NpgsqlCommand("ALTER TABLE \"AccountMatch\" RENAME CONSTRAINT \"" + legacy
            + "\" TO \"ProfileTest_LegacyRenamed\"", owner)) await rename.ExecuteNonQueryAsync();
        await using (var unrelated = new NpgsqlCommand("ALTER TABLE \"AccountMatch\" ADD CONSTRAINT \"ProfileTest_Unrelated\" CHECK (\"Score\" < 100000)", owner))
            await unrelated.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, string>> ProfileChecksAsync(NpgsqlConnection owner)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT "conname",regexp_replace(pg_get_expr("conbin","conrelid"),'[[:space:]()]','','g')
            FROM "pg_constraint" WHERE "conrelid"='"AccountMatch"'::regclass AND "contype"='c'
            """, owner);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) checks.Add(reader.GetString(0), reader.GetString(1));
        return checks;
    }

    private static async Task VerifyJudgmentMigrationAsync(NpgsqlConnection owner, Dictionary<string, string> previous)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var command = new NpgsqlCommand(ProfileMigration("007_JudgmentBounds.sql"), owner);
            await command.ExecuteNonQueryAsync();
        }
        var current = await ProfileChecksAsync(owner);
        Check(!current.ContainsKey("ProfileTest_LegacyRenamed")
            && current["AccountMatch_JudgmentBounds"] == "\"CorrectVotes\"<=\"RoundsPlayed\"AND\"CorrectGuesses\"<=\"LiarRounds\""
            && previous.Where(pair => pair.Key != "ProfileTest_LegacyRenamed" && pair.Value != "\"Rank\">=1AND\"Rank\"<=8").All(pair => current.TryGetValue(pair.Key, out string? expression) && expression == pair.Value),
            "이름이 바뀐 기존 시민 한도만 교체하고 다른 점수·역할 CHECK는 반복 실행 후에도 그대로 보존해야 합니다.");
        await using var version = new NpgsqlCommand("SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=7", owner);
        Check(Convert.ToInt32(await version.ExecuteScalarAsync()) == 1, "정답 찬반 집계 마이그레이션7을 한 번 적용해야 합니다.");
        Report("006 업그레이드·007 직접 두 번 재실행·CHECK 이름 독립성과 비관계 제약 보존 검증");
    }

    private static async Task VerifyFreshJudgmentSchemaAsync(string connectionString)
    {
        string schema = "drawliar_profile_fresh_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false, IncludeErrorDetail = false };
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            var checks = await ProfileChecksAsync(owner);
            Check(checks["AccountMatch_JudgmentBounds"] == "\"CorrectVotes\"<=\"RoundsPlayed\"AND\"CorrectGuesses\"<=\"LiarRounds\""
                && checks.Values.All(expression => !expression.Contains("\"CorrectVotes\"<=\"CitizenRounds\"", StringComparison.Ordinal)),
                "신규 DB도 시민 라운드에 제한하지 않는 찬반 한도를 적용해야 합니다.");
            Report("신규 스키마 반복 초기화·모든 역할의 올바른 찬반 집계 한도 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static bool StatsZero(ProfileStatsData stats) => stats.MatchesPlayed == 0 && stats.MatchesWon == 0
        && stats.TotalScore == 0 && stats.BestScore == 0 && stats.RoundsPlayed == 0 && stats.CitizenRounds == 0
        && stats.LiarRounds == 0 && stats.CorrectVotes == 0 && stats.CorrectGuesses == 0;

    private static async Task CheckProfileFriendshipAsync(ServerDatabase database, Guid first, Guid second, string outgoing, string incoming)
    {
        Check((await database.PublicProfileAsync(first, second)).Friendship == outgoing
            && (await database.PublicProfileAsync(second, first)).Friendship == incoming,
            "공개 프로필의 친구 상태는 조회자 기준으로 양방향을 구분해야 합니다.");
    }

    private static void CheckPublicProfileFields(PublicProfileData profile)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(profile, Json));
        void CheckFields(JsonElement element, params string[] allowed) => Check(element.EnumerateObject().Select(field => field.Name).Order()
            .SequenceEqual(allowed.Order()), "공개 프로필 JSON은 명시된 공개 필드만 반환해야 합니다.");
        CheckFields(json.RootElement, "AccountId", "DisplayName", "AvatarColor", "Accessory", "JoinedAt", "Friendship", "Stats", "RecentMatches");
        CheckFields(json.RootElement.GetProperty("Stats"), "MatchesPlayed", "MatchesWon", "TotalScore", "BestScore", "RoundsPlayed",
            "CitizenRounds", "LiarRounds", "CorrectVotes", "CorrectGuesses");
        foreach (var match in json.RootElement.GetProperty("RecentMatches").EnumerateArray())
            CheckFields(match, "MatchId", "PlayedAt", "Score", "Rank", "PlayerCount", "Mode", "Won", "RoundCount");
    }

    private static MatchResultRequest ProfileMatchFixture(Guid[] accounts, Guid roomId, DateTimeOffset playedAt, int index) => new()
    {
        NodeId = "profile-test-node", RoomId = roomId.ToString(), MatchId = Guid.NewGuid().ToString(), PlayedAt = playedAt.ToString("O"),
        Mode = index % 2, RoundCount = 2,
        Players = new[]
        {
            new MatchPlayerResult { AccountId = accounts[0].ToString(), Score = 10 + index, Rank = 1, Won = true,
                RoundsPlayed = 2, CitizenRounds = 1, LiarRounds = 1, CorrectVotes = 1, CorrectGuesses = 1 },
            new MatchPlayerResult { AccountId = accounts[1].ToString(), Score = 10 + index, Rank = 1, Won = true,
                RoundsPlayed = 2, CitizenRounds = 2, LiarRounds = 0, CorrectVotes = 1, CorrectGuesses = 0 },
            new MatchPlayerResult { AccountId = accounts[2].ToString(), Score = 5 + index, Rank = 3, Won = false,
                RoundsPlayed = 2, CitizenRounds = 0, LiarRounds = 2, CorrectVotes = 2, CorrectGuesses = 1 }
        }
    };

    private static MatchResultRequest CloneProfileMatch(MatchResultRequest request) =>
        JsonSerializer.Deserialize<MatchResultRequest>(JsonSerializer.Serialize(request, Json), Json)!;

    private static async Task ExpectProfileErrorAsync(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (ApiException exception) when (exception.Code == code && exception.Status == status) { return; }
        throw new InvalidOperationException("잘못된 프로필 요청은 " + code + " (" + status + ")로 거부해야 합니다.");
    }

    private static async Task<long> ProfileRowCountAsync(NpgsqlConnection connection, string table)
    {
        Check(table is "MatchRecord" or "AccountMatch", "검증 테이블은 고정 목록만 사용합니다.");
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SeedProfilePreviousSchemaAsync(NpgsqlConnection connection, Guid[] accounts, Guid roomId)
    {
        await using (var version = new NpgsqlCommand("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY,\"AppliedAt\" timestamptz NOT NULL DEFAULT now())", connection))
            await version.ExecuteNonQueryAsync();
        var assembly = typeof(ServerDatabase).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
        {
            int migration = int.Parse(resource.Split('.')[^2].Split('_')[0]);
            if (migration >= 6) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await using (var apply = new NpgsqlCommand(await reader.ReadToEndAsync(), connection)) await apply.ExecuteNonQueryAsync();
            await using var applied = new NpgsqlCommand("INSERT INTO \"SchemaVersion\" (\"Version\") VALUES ($1)", connection);
            applied.Parameters.AddWithValue(migration); await applied.ExecuteNonQueryAsync();
        }
        for (int index = 0; index < accounts.Length; index++)
        {
            await using var account = new NpgsqlCommand("""
                INSERT INTO "Account" ("Id","DisplayName","AvatarColor","Accessory","Coins","CreatedAt","Email","PasswordHash")
                VALUES ($1,$2,2,1,321,'2025-01-02T03:04:05Z',$3,'private-password-hash')
                """, connection);
            account.Parameters.AddWithValue(accounts[index]); account.Parameters.AddWithValue("기존프로필" + index);
            account.Parameters.AddWithValue("profile" + index + "@example.invalid"); await account.ExecuteNonQueryAsync();
        }
        await using (var node = new NpgsqlCommand("""
            INSERT INTO "DedicatedNode" ("NodeId","PublicUrl","Capacity","HeartbeatAt") VALUES ('profile-test-node','ws://127.0.0.1:19070/play',32,now())
            """, connection)) await node.ExecuteNonQueryAsync();
        await using var room = new NpgsqlCommand("""
            INSERT INTO "Room" ("RoomId","RoomCode","OwnerAccountId","NodeId","Settings","PlayerCount","SpectatorCount","PlayerAccountIds","SpectatorAccountIds","Established")
            VALUES ($1,'ABCDEF',$2,'profile-test-node',$3::jsonb,3,1,$4::jsonb,$5::jsonb,true)
            """, connection);
        room.Parameters.AddWithValue(roomId); room.Parameters.AddWithValue(accounts[0]);
        room.Parameters.AddWithValue(JsonSerializer.Serialize(new ServerRoomSettings(), Json));
        room.Parameters.AddWithValue(JsonSerializer.Serialize(accounts.Take(3).Select(account => account.ToString())));
        room.Parameters.AddWithValue(JsonSerializer.Serialize(accounts.Skip(3).Select(account => account.ToString())));
        await room.ExecuteNonQueryAsync();
    }

    private static async Task VerifyProfileRoomAdmissionsAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        var accounts = new List<Guid>();
        var sessions = new List<ServerSession>();
        for (int index = 0; index < 3; index++)
        {
            Guid account = await database.DevelopmentAccountAsync("새방화가" + index);
            accounts.Add(account);
            var issued = await database.IssueSessionAsync(account, "game:profile-test");
            sessions.Add(await database.AuthenticateAsync(issued.Token, "game:profile-test"));
        }
        await database.RegisterDedicatedAsync(new RegisterDedicatedRequest
        { NodeId = "profile-test-node", PublicUrl = "ws://127.0.0.1:19070/play", Capacity = 32 }, true);
        var assignment = await database.CreateRoomAsync(sessions[0], new CreateRoomRequest());
        for (int index = 0; index < sessions.Count; index++)
        {
            var ticket = index == 0 ? assignment : await database.JoinRoomAsync(sessions[index], assignment.RoomId, false);
            var redeemed = await database.RedeemTicketAsync(new RedeemTicketRequest
            { NodeId = "profile-test-node", RoomId = ticket.RoomId, JoinTicket = ticket.JoinTicket });
            Check(redeemed.AccountId == accounts[index].ToString(), "실제 입장권 교환 계정이 일치해야 합니다.");
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM \"Room\" WHERE \"RoomId\"=$1", owner))
        { delete.Parameters.AddWithValue(Guid.Parse(assignment.RoomId)); await delete.ExecuteNonQueryAsync(); }
        await database.RecordMatchAsync(ProfileMatchFixture(accounts.ToArray(), Guid.Parse(assignment.RoomId), DateTimeOffset.UtcNow, 0));
        Check((await database.PublicProfileAsync(accounts[0], accounts[0])).Stats.MatchesPlayed == 1,
            "신규 방의 생성 권한·교환 입장 이력은 방 삭제 후 전적 저장에도 남아야 합니다.");
        Report("새 방 생성·실제 입장권 교환·방 삭제 후 영구 참가 이력으로 전적 저장 검증");
    }
}
