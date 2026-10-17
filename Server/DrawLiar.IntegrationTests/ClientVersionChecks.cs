using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static void VerifyClientVersionRules()
    {
        Check(ClientVersionRules.PLATFORMS.SequenceEqual(new[] { "Android", "Windows" }),
            "버전 정책 플랫폼과 표시 순서는 Android, Windows여야 합니다.");
        foreach (string valid in new[] { "0.0.0", "0.1.0", "1.2.3", "10.20.30" })
            Check(ClientVersionRules.TryParse(valid, out _), "세 자리 버전을 해석해야 합니다: " + valid);
        foreach (string invalid in new[] { "", "1", "1.2", "1.2.3.4", "-1.2.3", "1.2.-3", "1.2.x", "2147483648.0.0" })
            Check(!ClientVersionRules.TryParse(invalid, out _), "잘못된 버전 형식을 거부해야 합니다: " + invalid);
        Check(ClientVersionRules.IsOlder("1.9.9", "2.0.0")
            && ClientVersionRules.IsOlder("1.2.99", "1.3.0")
            && ClientVersionRules.IsOlder("1.2.3", "1.2.4")
            && !ClientVersionRules.IsOlder("1.2.3", "1.2.3")
            && !ClientVersionRules.IsOlder("1.2.10", "1.2.9"),
            "버전은 문자열 순서가 아닌 major·minor·patch 숫자로 비교해야 합니다.");
        Check(ClientVersionRules.IsOlder("", "1.2.3") && ClientVersionRules.IsOlder("invalid", "1.2.3")
            && !ClientVersionRules.IsOlder("1.2.3", "") && !ClientVersionRules.IsOlder("1.2.3", "invalid")
            && !ClientVersionRules.IsOlder("invalid", "invalid"),
            "해석할 수 없는 현재 버전은 구버전으로, 비어 있거나 잘못된 기준 버전은 미적용으로 처리해야 합니다.");
        Report("클라이언트 버전 세 자리 형식·숫자 비교·잘못된 현재/기준 버전 규칙 검증");
    }

    private static async Task VerifyClientVersionDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439 && scoped.Database == "postgres",
            "클라이언트 버전 검증은 전용 로컬 PostgreSQL 25439 포트만 사용합니다.");
        string schema = "drawliar_client_version_" + Guid.NewGuid().ToString("N");
        scoped.SearchPath = schema;
        scoped.Pooling = false;
        scoped.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        async Task<int> Sql(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, owner);
            foreach (object value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return await command.ExecuteNonQueryAsync();
        }
        await Sql("CREATE SCHEMA \"" + schema + "\"");
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            var policies = await database.AdminClientVersionsAsync();
            Check(policies.Select(policy => policy.Platform).SequenceEqual(ClientVersionRules.PLATFORMS)
                && policies.All(policy => policy.MinimumVersion == "" && policy.LatestVersion == "" && policy.StoreUrl == ""),
                "저장하지 않은 플랫폼도 지정 순서와 빈 정책으로 반환해야 합니다.");
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "invalid"), false, false);

            const string STORE_URL = "https://play.google.com/store/apps/details?id=com.rascallab.liargame";
            var android = new ClientVersionPolicy { Platform = "Android", MinimumVersion = "1.2.3", LatestVersion = "2.0.0", StoreUrl = STORE_URL };
            await database.AdminSetClientVersionAsync("Android", android);
            policies = await database.AdminClientVersionsAsync();
            Check(policies[0].MinimumVersion == "1.2.3" && policies[0].LatestVersion == "2.0.0"
                && policies[0].StoreUrl == STORE_URL && policies[1].MinimumVersion == "" && policies[1].LatestVersion == "",
                "정책 저장은 해당 플랫폼에만 적용하고 미설정 플랫폼을 유지해야 합니다.");
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "1.2.2"), true, true, "2.0.0", STORE_URL);
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "1.2.3"), false, true, "2.0.0", STORE_URL);
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "2.0.0"), false, false, "2.0.0", STORE_URL);
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "3.0.0"), false, false, "2.0.0", STORE_URL);
            foreach (string version in new[] { "", "invalid", "1.2" })
                CheckClientVersionResponse(await database.ClientVersionAsync("Android", version), true, true, "2.0.0", STORE_URL);
            foreach (string platform in new[] { "", "WebGL", "Editor", "android" })
                CheckClientVersionResponse(await database.ClientVersionAsync(platform, "invalid"), false, false);

            foreach (string platform in new[] { "", "WebGL", "android" })
                await ClientVersionError(() => database.AdminSetClientVersionAsync(platform, android));
            foreach (var invalid in new[]
            {
                new ClientVersionPolicy { MinimumVersion = "1.2", LatestVersion = "2.0.0" },
                new ClientVersionPolicy { MinimumVersion = "1.2.3", LatestVersion = "2.0.0.0" },
                new ClientVersionPolicy { MinimumVersion = "2.0.1", LatestVersion = "2.0.0" },
                new ClientVersionPolicy { StoreUrl = "http://example.com/store" },
                new ClientVersionPolicy { StoreUrl = "/store" },
                new ClientVersionPolicy { StoreUrl = "javascript:alert(1)" },
                new ClientVersionPolicy { StoreUrl = "https://example.com/" + new string('a', 513) }
            })
                await ClientVersionError(() => database.AdminSetClientVersionAsync("Android", invalid));
            policies = await database.AdminClientVersionsAsync();
            Check(policies[0].MinimumVersion == "1.2.3" && policies[0].LatestVersion == "2.0.0" && policies[0].StoreUrl == STORE_URL,
                "검증에 실패한 저장은 기존 정책을 변경하면 안 됩니다.");

            await Sql("UPDATE \"ClientVersionPolicy\" SET \"UpdatedAt\"=$2 WHERE \"Platform\"=$1", "Android", DateTimeOffset.UnixEpoch);
            await database.AdminSetClientVersionAsync("Android", new ClientVersionPolicy { LatestVersion = "2.0.0" });
            await using (var updated = new NpgsqlCommand("""
                SELECT count(*)=1 AND bool_and("UpdatedAt">$1) FROM "ClientVersionPolicy" WHERE "Platform"=$2
                """, owner))
            {
                updated.Parameters.AddWithValue(DateTimeOffset.UnixEpoch);
                updated.Parameters.AddWithValue("Android");
                Check(await updated.ExecuteScalarAsync() is true, "재저장은 행을 중복 생성하지 않고 UpdatedAt을 갱신해야 합니다.");
            }
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "1.0.0"), false, true, "2.0.0");
            await database.AdminSetClientVersionAsync("Android", new ClientVersionPolicy { MinimumVersion = "1.2.3" });
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "1.2.2"), true, true);
            await database.AdminSetClientVersionAsync("Android", new ClientVersionPolicy());
            CheckClientVersionResponse(await database.ClientVersionAsync("Android", "invalid"), false, false);
            await database.AdminSetClientVersionAsync("Windows", new ClientVersionPolicy
            { Platform = "Windows", MinimumVersion = "1.0.0", LatestVersion = "1.0.0", StoreUrl = "https://example.com/windows" });
            CheckClientVersionResponse(await database.ClientVersionAsync("Windows", "0.9.9"), true, true, "1.0.0", "https://example.com/windows");
            policies = await database.AdminClientVersionsAsync();
            Check(policies.Select(policy => policy.Platform).SequenceEqual(ClientVersionRules.PLATFORMS)
                && policies[0].MinimumVersion == "" && policies[1].MinimumVersion == "1.0.0",
                "재저장 후에도 플랫폼 순서와 플랫폼별 독립 정책을 유지해야 합니다.");
            Report("격리 PostgreSQL 클라이언트 버전 정책 기본값·UPSERT·입력 거부·강제/일반 업데이트·비대상 허용 검증");
        }
        finally { await Sql("DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    private static void CheckClientVersionResponse(ClientVersionResponse response, bool required, bool available,
        string latest = "", string storeUrl = "") =>
        Check(response.UpdateRequired == required && response.UpdateAvailable == available
            && response.LatestVersion == latest && response.StoreUrl == storeUrl,
            "클라이언트 버전 조회의 강제/일반 업데이트 여부와 버전·스토어 주소가 정확해야 합니다.");

    private static async Task ClientVersionError(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("잘못된 클라이언트 버전 정책을 거부해야 합니다."); }
        catch (ApiException error) { Check(error.Code == "InvalidClientVersion", "클라이언트 버전 정책 검증 오류 코드가 정확해야 합니다."); }
    }
}
