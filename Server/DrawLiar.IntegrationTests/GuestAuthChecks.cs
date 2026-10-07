using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DrawLiar;
using DrawLiar.Server;
using Microsoft.Extensions.Configuration;
using Npgsql;

internal static partial class Integration
{
    private static GuestLoginRequest NewGuestRequest() => new()
    {
        GuestId = Guid.NewGuid().ToString("D"),
        GuestSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GuestCredential.BYTE_LENGTH)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    };

    private static void VerifyGuestCredentialRules()
    {
        for (int index = 0; index < 100; index++)
        {
            var request = NewGuestRequest();
            string hash = GuestCredential.Hash(request.GuestSecret);
            Check(GuestCredential.IsValid(request.GuestSecret) && hash.Length == 64 && hash != request.GuestSecret
                && GuestCredential.Verify(hash, request.GuestSecret) && GuestCredential.Verify(hash.ToLowerInvariant(), request.GuestSecret)
                && !GuestCredential.Verify(hash, NewGuestRequest().GuestSecret), "게스트 비밀값은 정규 인코딩과 해시 비교로 검증해야 합니다.");
        }
        foreach (string invalid in new[] { "", new string('A', 42), new string('A', 44), new string('A', 42) + "B",
            new string('A', 42) + "=", "+" + new string('A', 42), "/" + new string('A', 42), "가" + new string('A', 42) })
            Check(!GuestCredential.IsValid(invalid), "길이·문자·사용하지 않는 마지막 비트가 잘못된 비밀값을 거부해야 합니다.");
        Check(!GuestCredential.Verify(new string('Z', 64), new string('A', 43))
            && !GuestCredential.Verify("", new string('A', 43)), "손상된 저장 해시는 인증을 허용하면 안 됩니다.");
        Report("게스트 256비트 비밀값의 정규 Base64URL·SHA256·위조 거부 검증");
    }

    private static async Task VerifyGuestApiAsync(HttpClient main)
    {
        var request = NewGuestRequest();
        var first = await PostAsync<LoginResponse>(main, "/api/auth/guest", request, null);
        Check(first.Profile.IsGuest && !first.Profile.HasGoogleAccount, "운영 게스트 인증은 개발 인증 설정 없이 동작해야 합니다.");
        var retry = await PostAsync<LoginResponse>(main, "/api/auth/guest", request, null);
        Check(retry.AccountId == first.AccountId && retry.Profile.Coins == first.Profile.Coins
            && retry.SessionToken != first.SessionToken, "응답 유실 재시도는 같은 계정으로 새 세션만 발급해야 합니다.");
        using var game = Client(retry.GameServerUrl);
        var entered = await PostAsync<GameSessionResponse>(game, "/api/session/enter",
            new EnterGameRequest { AssignmentToken = retry.AssignmentToken }, null);
        Check(entered.Profile.IsGuest && entered.Profile.AccountId == first.AccountId, "게임서버에도 게스트 상태가 전달되어야 합니다.");
        var purchased = await PostAsync<ProfileData>(game, "/api/shop/purchase", new PurchaseRequest
            { ProductId = "crown", OperationId = Guid.NewGuid().ToString("D") }, entered.SessionToken);
        string displayName = "게스트_" + Guid.NewGuid().ToString("N")[..8];
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/profile")
        {
            Content = JsonContent.Create(new UpdateProfileRequest
                { DisplayName = displayName, AvatarColor = 2, Accessory = 4 }, options: Json)
        })
        {
            patch.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", entered.SessionToken);
            using var response = await game.SendAsync(patch);
            response.EnsureSuccessStatusCode();
        }
        foreach (var invalid in new[]
        {
            new GuestLoginRequest { GuestId = request.GuestId, GuestSecret = NewGuestRequest().GuestSecret },
            new GuestLoginRequest { GuestId = Guid.Empty.ToString("D"), GuestSecret = request.GuestSecret },
            new GuestLoginRequest { GuestId = request.GuestId.Replace("-", ""), GuestSecret = request.GuestSecret },
            new GuestLoginRequest { GuestId = request.GuestId, GuestSecret = "invalid" }
        })
        {
            using var response = await SendAsync(main, "/api/auth/guest", invalid, null);
            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
            Check(response.StatusCode == HttpStatusCode.Unauthorized && error?.Code == "InvalidGuestCredential",
                "잘못된 게스트 자격으로 새 계정이나 세션을 발급하면 안 됩니다.");
        }
        using (var signup = await SendAsync(main, "/api/auth/register", new { }, null))
            Check(signup.StatusCode == HttpStatusCode.NotFound, "공개 회원가입 API는 제공하면 안 됩니다.");
        await PostNoContentAsync(main, "/api/session/logout", new { }, first.SessionToken);
        await PostNoContentAsync(main, "/api/session/logout", new { }, retry.SessionToken);
        await AssertRejectedAsync(game, "/api/rooms", new CreateRoomRequest(), entered.SessionToken);
        var restored = await PostAsync<LoginResponse>(main, "/api/auth/guest", request, null);
        Check(restored.AccountId == first.AccountId && restored.Profile.DisplayName == displayName
            && restored.Profile.Coins == purchased.Coins && restored.Profile.Coins == first.Profile.Coins - 150
            && restored.Profile.AvatarColor == 2 && restored.Profile.Accessory == 4 && restored.Profile.OwnedAccessories.Contains(4),
            "게스트 재접속은 이름·캐릭터·보유품·재화를 PostgreSQL에서 복원해야 합니다.");
        await PostNoContentAsync(main, "/api/session/logout", new { }, restored.SessionToken);
        Report("운영 게스트 생성·응답 유실 재시도·재접속 프로필/보유품/재화 복원·잘못된 자격 거부·회원가입 제거·하위 세션 폐기 검증");
    }

    private static async Task VerifyGuestDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        string schema = "drawliar_guest_test_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false, IncludeErrorDetail = false };
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            Guid legacyId = Guid.NewGuid();
            string legacyEmail = "legacy@example.invalid";
            string legacyPassword = "legacy-fixture-" + Guid.NewGuid().ToString("N");
            await SeedPreviousSchemaAsync(owner, legacyId, legacyEmail, legacyPassword);
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            Check(await database.LoginAsync(new LoginRequest { Email = legacyEmail, Password = legacyPassword }) == legacyId,
                "기존 이메일 계정은 마이그레이션 후에도 로그인할 수 있어야 합니다.");
            var legacy = await database.ProfileAsync(legacyId);
            Check(legacy.Coins == 321 && legacy.DisplayName == "기존계정" && !legacy.IsGuest && !legacy.HasGoogleAccount,
                "마이그레이션은 기존 계정의 프로필·재화를 바꾸면 안 됩니다.");

            var request = NewGuestRequest();
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.GuestLoginAsync(request)));
            var sessions = await Task.WhenAll(attempts.Select(issued => database.AuthenticateAsync(issued.Token, "main")));
            Guid accountId = sessions[0].AccountId;
            Check(sessions.All(session => session.AccountId == accountId), "병렬 재시도는 하나의 게스트 계정만 생성해야 합니다.");
            var initial = await database.ProfileAsync(accountId);
            Check(initial.Coins == 500 && initial.IsGuest && !initial.HasGoogleAccount, "신규 게스트의 기본 계정 상태가 정확해야 합니다.");
            await using (var stored = new NpgsqlCommand("SELECT \"GuestSecretHash\",\"Email\",\"PasswordHash\" FROM \"Account\" WHERE \"GuestId\"=$1", owner))
            {
                stored.Parameters.AddWithValue(Guid.Parse(request.GuestId));
                await using var reader = await stored.ExecuteReaderAsync();
                Check(await reader.ReadAsync() && GuestCredential.Verify(reader.GetString(0), request.GuestSecret)
                    && reader.GetString(0) != request.GuestSecret && reader.IsDBNull(1) && reader.IsDBNull(2),
                    "게스트 비밀값은 해시로만 저장하고 이메일·비밀번호를 만들지 않아야 합니다.");
            }
            await ExpectAuthErrorAsync(() => database.GuestLoginAsync(new GuestLoginRequest
                { GuestId = request.GuestId, GuestSecret = NewGuestRequest().GuestSecret }), "InvalidGuestCredential");
            await ExpectAuthErrorAsync(() => database.GuestLoginAsync(new GuestLoginRequest
                { GuestId = Guid.Empty.ToString("D"), GuestSecret = request.GuestSecret }), "InvalidGuestCredential");
            var purchased = await database.PurchaseAsync(accountId, new PurchaseRequest { ProductId = "crown", OperationId = Guid.NewGuid().ToString("D") });
            var customized = await database.UpdateProfileAsync(accountId, new UpdateProfileRequest { DisplayName = "연동화가", AvatarColor = 4, Accessory = 4 });
            await database.RequestFriendAsync(accountId, legacyId);
            await database.RespondFriendAsync(legacyId, accountId, true);
            var challenge = await database.CreateChallengeAsync("fixture-client", "mobile", accountId);
            await ExpectAuthErrorAsync(() => database.ConsumeChallengeAsync(challenge.ChallengeId, legacyId), "InvalidGoogleCredential");
            await ExpectAuthErrorAsync(() => database.ConsumeChallengeAsync(challenge.ChallengeId, accountId), "InvalidGoogleCredential");

            string subject = "fixture-subject-" + Guid.NewGuid().ToString("N");
            Guid linkedId = await database.GoogleAccountAsync(subject, "새이름", accountId);
            Guid loggedId = await database.GoogleAccountAsync(subject, "다른이름", null);
            var linked = await database.ProfileAsync(linkedId);
            Check(linkedId == accountId && loggedId == accountId && !linked.IsGuest && linked.HasGoogleAccount
                && linked.DisplayName == customized.DisplayName && linked.AvatarColor == customized.AvatarColor
                && linked.Accessory == customized.Accessory && linked.Coins == purchased.Coins
                && linked.OwnedAccessories.SequenceEqual(customized.OwnedAccessories), "Google 연동과 재로그인은 같은 계정의 재화·꾸미기를 유지해야 합니다.");
            Check((await database.FriendsAsync(accountId)).Friends.Any(friend => friend.AccountId == legacyId.ToString()),
                "Google 연동 후에도 기존 친구 관계가 유지되어야 합니다.");
            await ExpectAuthErrorAsync(() => database.GuestLoginAsync(request), "ExternalLoginRequired");
            await ExpectAuthErrorAsync(() => database.GuestLoginAsync(new GuestLoginRequest
                { GuestId = request.GuestId, GuestSecret = NewGuestRequest().GuestSecret }), "InvalidGuestCredential");
            await ExpectAuthErrorAsync(() => database.GoogleAccountAsync(subject, "화가", legacyId), "GoogleAlreadyLinked");
            await ExpectAuthErrorAsync(() => database.GoogleAccountAsync(subject + "-other", "화가", accountId), "GoogleAlreadyLinked");
            Check((await database.ProfileAsync(legacyId)).Coins == 321, "연동 충돌이 기존 계정을 수정하면 안 됩니다.");
            await using (var count = new NpgsqlCommand("SELECT count(*) FROM \"Account\" WHERE \"GuestId\"=$1", owner))
            {
                count.Parameters.AddWithValue(Guid.Parse(request.GuestId));
                Check(Convert.ToInt64(await count.ExecuteScalarAsync()) == 1, "연동된 게스트 ID를 다른 계정에 재사용하면 안 됩니다.");
            }
            Report("격리 PostgreSQL 게스트 병렬 생성·해시 저장·마이그레이션·Google 연동 보존·챌린지 귀속·연동 충돌 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
            Report("게스트 검증 임시 스키마 삭제");
        }
    }

    private static async Task SeedPreviousSchemaAsync(NpgsqlConnection connection, Guid legacyId, string email, string password)
    {
        await using (var version = new NpgsqlCommand("CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY,\"AppliedAt\" timestamptz NOT NULL DEFAULT now())", connection))
            await version.ExecuteNonQueryAsync();
        var assembly = typeof(ServerDatabase).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
        {
            int migration = int.Parse(resource.Split('.')[^2].Split('_')[0]);
            if (migration >= 5) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await using (var command = new NpgsqlCommand(await reader.ReadToEndAsync(), connection)) await command.ExecuteNonQueryAsync();
            await using var applied = new NpgsqlCommand("INSERT INTO \"SchemaVersion\" (\"Version\") VALUES ($1)", connection);
            applied.Parameters.AddWithValue(migration);
            await applied.ExecuteNonQueryAsync();
        }
        await using var seed = new NpgsqlCommand("INSERT INTO \"Account\" (\"Id\",\"Email\",\"PasswordHash\",\"DisplayName\",\"Coins\") VALUES ($1,$2,$3,'기존계정',321)", connection);
        seed.Parameters.AddWithValue(legacyId);
        seed.Parameters.AddWithValue(email);
        seed.Parameters.AddWithValue(ServerRuntime.PasswordHash(password));
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task ExpectAuthErrorAsync<T>(Func<Task<T>> action, string code)
    {
        try { await action(); }
        catch (ApiException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("잘못된 인증 상태는 " + code + "로 거부해야 합니다.");
    }
}
