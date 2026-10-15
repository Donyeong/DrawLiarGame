using System.Reflection;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyServerReliabilityDatabaseAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(settings.Host == "127.0.0.1" && settings.Port == 25439 && settings.Database == "postgres",
            "서버 안정성 검증은 전용 로컬 PostgreSQL 25439 포트만 사용합니다.");
        string schema = "drawliar_reliability_test_" + Guid.NewGuid().ToString("N");
        settings.SearchPath = schema;
        settings.Pooling = false;
        settings.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(settings.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", owner))
            await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = settings.ConnectionString }).Build());
            await database.InitializeAsync();
            const BindingFlags FLAGS = BindingFlags.NonPublic | BindingFlags.Instance;
            ((CancellationTokenSource)typeof(ServerDatabase).GetField("_maintenanceLifetime", FLAGS)!.GetValue(database)!).Cancel();
            await (Task)typeof(ServerDatabase).GetField("_maintenanceTask", FLAGS)!.GetValue(database)!;
            await VerifyReliabilitySessionExchangeAsync(database, owner);
            await VerifyReliabilityFriendRequestsAsync(database, owner);
            await VerifyReliabilityExpiryCleanupAsync(database, owner);
            await VerifyPlayerSafetyAsync(database, owner);
            await using var indexes = new NpgsqlCommand("""
                SELECT count(*) FROM "pg_indexes" WHERE "schemaname"=$1 AND "indexname"=ANY($2)
                """, owner);
            indexes.Parameters.AddWithValue(schema);
            indexes.Parameters.AddWithValue(new[] { "Session_ParentHash", "AuthChallenge_ExpiresAt", "Friendship_IncomingPending",
                "Friendship_PendingExpiry", "Friendship_ToAccountId" });
            Check(Convert.ToInt64(await indexes.ExecuteScalarAsync()) == 5, "서버 안정성 인덱스 5개를 마이그레이션해야 합니다.");
            database.Dispose();
            database.Dispose();
            Report("격리 PostgreSQL 동시 입장 교환·친구 수신 한도/양방향 잠금/만료·제한 배치 정리·차단/신고 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyReliabilitySessionExchangeAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        Guid account = await database.DevelopmentAccountAsync("동시입장");
        var main = await database.IssueSessionAsync(account, "main");
        var assignments = new List<IssuedSession>();
        for (int index = 0; index < 8; index++)
            assignments.Add(await database.IssueSessionAsync(account, "assignment:reliability-game", ServerRuntime.Hash(main.Token)));
        var entered = await Task.WhenAll(assignments.Select(assignment => database.EnterGameAsync(assignment.Token, "reliability-game")));
        int valid = 0;
        foreach (var response in entered)
        {
            try { await database.AuthenticateAsync(response.SessionToken, "game:reliability-game"); valid++; }
            catch (ApiException exception) when (exception.Status == 401) { }
        }
        Check(valid == 1, "동일 계정의 동시 assignment 교환 후 게임 세션은 하나만 유효해야 합니다.");
        await using var remaining = new NpgsqlCommand("""
            SELECT count(*) FROM "Session" WHERE "AccountId"=$1 AND "Scope"=$2
            """, owner);
        remaining.Parameters.AddWithValue(account);
        remaining.Parameters.AddWithValue("game:reliability-game");
        Check(Convert.ToInt64(await remaining.ExecuteScalarAsync()) == 1, "동시 교환이 게임 세션 교체를 우회하면 안 됩니다.");
        var single = await database.IssueSessionAsync(account, "assignment:reliability-game", ServerRuntime.Hash(main.Token));
        var repeated = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await database.EnterGameAsync(single.Token, "reliability-game"); return true; }
            catch (ApiException exception) when (exception.Status == 401) { return false; }
        }));
        Check(repeated.Count(success => success) == 1, "동일 assignment의 동시 교환은 한 번만 성공해야 합니다.");
    }

    private static async Task VerifyReliabilityFriendRequestsAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        Guid[] accounts = Enumerable.Range(0, 115).Select(_ => Guid.NewGuid()).ToArray();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO "Account" ("Id","DisplayName") SELECT "Id",$2 FROM unnest($1::uuid[]) AS "a"("Id")
            """, owner))
        {
            seed.Parameters.AddWithValue(accounts);
            seed.Parameters.AddWithValue("친구부하");
            await seed.ExecuteNonQueryAsync();
        }
        Guid recipient = accounts[^1];
        using var concurrency = new SemaphoreSlim(16);
        var requests = await Task.WhenAll(accounts.Take(112).Select(async account =>
        {
            await concurrency.WaitAsync();
            try
            {
                try { await database.RequestFriendAsync(account, recipient); return true; }
                catch (ApiException exception) when (exception.Code == "FriendLimit" && exception.Status == 409) { return false; }
            }
            finally { concurrency.Release(); }
        }));
        Check(requests.Count(success => success) == 100 && (await database.FriendsAsync(recipient)).Incoming.Length == 100,
            "여러 계정의 동시 친구요청에도 수신 대기는 100개까지만 허용해야 합니다.");
        int firstSender = Array.FindIndex(requests, success => success);
        await database.RequestFriendAsync(accounts[firstSender], recipient);
        Check((await database.FriendsAsync(recipient)).Incoming.Length == 100, "수신 한도에서 기존 신청 재시도는 멱등이어야 합니다.");
        Guid first = accounts[^3], second = accounts[^2];
        await Task.WhenAll(database.RequestFriendAsync(first, second), database.RequestFriendAsync(second, first));
        var pair = await database.FriendsAsync(first);
        Check(pair.Incoming.Length + pair.Outgoing.Length == 1, "반대 방향 동시 요청은 교착 없이 한 관계만 만들어야 합니다.");
        await database.RemoveFriendAsync(first, second);
        await database.RequestFriendAsync(first, second);
        await using (var expire = new NpgsqlCommand("""
            UPDATE "Friendship" SET "CreatedAt"=$3 WHERE "FromAccountId"=$1 AND "ToAccountId"=$2
            """, owner))
        {
            expire.Parameters.AddWithValue(first);
            expire.Parameters.AddWithValue(second);
            expire.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddDays(-31));
            await expire.ExecuteNonQueryAsync();
        }
        Check((await database.FriendsAsync(second)).Incoming.Length == 0, "30일 지난 미수락 신청은 수신 목록에서 제외해야 합니다.");
        bool rejected = false;
        try { await database.RespondFriendAsync(second, first, true); }
        catch (ApiException exception) when (exception.Code == "FriendRequestNotFound" && exception.Status == 404) { rejected = true; }
        Check(rejected, "만료 신청은 수락할 수 없어야 합니다.");
        await database.RequestFriendAsync(second, first);
        Check((await database.FriendsAsync(first)).Incoming.Single().AccountId == second.ToString(), "만료 신청과 반대 방향으로 다시 요청할 수 있어야 합니다.");
        await database.RespondFriendAsync(first, second, true);
        await using (var age = new NpgsqlCommand("""
            UPDATE "Friendship" SET "CreatedAt"=$3 WHERE "FromAccountId"=$1 AND "ToAccountId"=$2
            """, owner))
        {
            age.Parameters.AddWithValue(second);
            age.Parameters.AddWithValue(first);
            age.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddDays(-31));
            await age.ExecuteNonQueryAsync();
        }
        Check((await database.FriendsAsync(first)).Friends.Length == 1, "수락한 친구는 요청 만료의 영향을 받지 않아야 합니다.");
    }

    private static async Task VerifyReliabilityExpiryCleanupAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        Guid account = await database.DevelopmentAccountAsync("만료정리");
        string prefix = Guid.NewGuid().ToString("N");
        await using (var sessions = new NpgsqlCommand("""
            INSERT INTO "Session" ("Hash","AccountId","Scope","ExpiresAt")
                SELECT $1::text || "s"."Number"::text,$2,$3,$4 FROM generate_series($5,$6) AS "s"("Number")
            """, owner))
        {
            sessions.Parameters.AddWithValue(prefix);
            sessions.Parameters.AddWithValue(account);
            sessions.Parameters.AddWithValue("expired-test");
            sessions.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddDays(-1));
            sessions.Parameters.AddWithValue(1);
            sessions.Parameters.AddWithValue(300);
            await sessions.ExecuteNonQueryAsync();
        }
        await using (var challenges = new NpgsqlCommand("""
            INSERT INTO "AuthChallenge" ("Id","Nonce","ClientId","Platform","ExpiresAt")
                SELECT $1::text || "s"."Number"::text,$2,$3,$4,$5 FROM generate_series($6,$7) AS "s"("Number")
            """, owner))
        {
            challenges.Parameters.AddWithValue(prefix);
            challenges.Parameters.AddWithValue("nonce");
            challenges.Parameters.AddWithValue("client");
            challenges.Parameters.AddWithValue("test");
            challenges.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddDays(-1));
            challenges.Parameters.AddWithValue(1);
            challenges.Parameters.AddWithValue(300);
            await challenges.ExecuteNonQueryAsync();
        }
        await database.IssueSessionAsync(account, "main");
        await database.CreateChallengeAsync("test-client", "test", null);
        Check(await ReliabilityExpiredCountAsync(owner, true) == 300 && await ReliabilityExpiredCountAsync(owner, false) == 300,
            "세션 발급과 Google 챌린지 생성마다 만료 행 전체를 삭제하면 안 됩니다.");
        var cleanup = typeof(ServerDatabase).GetMethod("CleanupExpiredDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
        Check(await ReliabilityExpiredCountAsync(owner, true) == 44 && await ReliabilityExpiredCountAsync(owner, false) == 44,
            "한 번의 만료 정리는 테이블별 256개 배치로 제한해야 합니다.");
        await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
        Check(await ReliabilityExpiredCountAsync(owner, true) == 0 && await ReliabilityExpiredCountAsync(owner, false) == 0,
            "다음 배치에서 남은 만료 행을 정리해야 합니다.");
        var parent = await database.IssueSessionAsync(account, "main");
        await using (var children = new NpgsqlCommand("""
            INSERT INTO "Session" ("Hash","AccountId","Scope","ExpiresAt","ParentHash")
                SELECT $1::text || "s"."Number"::text,$2,$3,$4,$5 FROM generate_series($6,$7) AS "s"("Number")
            """, owner))
        {
            children.Parameters.AddWithValue(Guid.NewGuid().ToString("N"));
            children.Parameters.AddWithValue(account);
            children.Parameters.AddWithValue("assignment:expired-parent");
            children.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddHours(1));
            children.Parameters.AddWithValue(ServerRuntime.Hash(parent.Token));
            children.Parameters.AddWithValue(1);
            children.Parameters.AddWithValue(300);
            await children.ExecuteNonQueryAsync();
        }
        await using (var expire = new NpgsqlCommand("UPDATE \"Session\" SET \"ExpiresAt\"=$2 WHERE \"Hash\"=$1", owner))
        {
            expire.Parameters.AddWithValue(ServerRuntime.Hash(parent.Token));
            expire.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddMinutes(-1));
            await expire.ExecuteNonQueryAsync();
        }
        await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
        const string FAMILY_COUNT_SQL = "SELECT count(*) FROM \"Session\" WHERE \"Hash\"=$1 OR \"ParentHash\"=$1";
        await using (var leaf = new NpgsqlCommand(FAMILY_COUNT_SQL, owner))
        {
            leaf.Parameters.AddWithValue(ServerRuntime.Hash(parent.Token));
            Check(Convert.ToInt64(await leaf.ExecuteScalarAsync()) == 45, "부모의 무효 자식 300개도 한 배치에서 256개까지만 삭제해야 합니다.");
        }
        await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
        await using (var root = new NpgsqlCommand(FAMILY_COUNT_SQL, owner))
        {
            root.Parameters.AddWithValue(ServerRuntime.Hash(parent.Token));
            Check(Convert.ToInt64(await root.ExecuteScalarAsync()) == 1, "남은 자식을 지운 배치에서도 부모는 다음 순서에 정리해야 합니다.");
        }
        await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
        await using (var cascaded = new NpgsqlCommand(FAMILY_COUNT_SQL, owner))
        {
            cascaded.Parameters.AddWithValue(ServerRuntime.Hash(parent.Token));
            Check(Convert.ToInt64(await cascaded.ExecuteScalarAsync()) == 0, "만료된 부모와 자식 세션을 함께 폐기해야 합니다.");
        }
        await using var active = new NpgsqlCommand("SELECT count(*) FROM \"Session\" WHERE \"AccountId\"=$1 AND \"ExpiresAt\">now()", owner);
        active.Parameters.AddWithValue(account);
        Check(Convert.ToInt64(await active.ExecuteScalarAsync()) == 1, "정리 이후 유효한 세션은 보존해야 합니다.");
    }

    private static async Task<long> ReliabilityExpiredCountAsync(NpgsqlConnection connection, bool sessions)
    {
        string sql = sessions
            ? "SELECT count(*) FROM \"Session\" WHERE \"ExpiresAt\"<=now()"
            : "SELECT count(*) FROM \"AuthChallenge\" WHERE \"ExpiresAt\"<=now()";
        await using var count = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await count.ExecuteScalarAsync());
    }
}
