using System.Reflection;
using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyRoomActivityWireAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Relay);
        await fixture.FlushAsync();
        Check(fixture.Activities.Count(activity => activity.Kind == "RoomEnter") == 4
            && fixture.Activities.Single(activity => activity.AccountId == "observer").Spectator
            && fixture.Activities.All(activity => activity.RoomId == fixture.Room.RoomId), "방 입장은 계정·관전 여부와 함께 기록해야 합니다.");
        fixture.Send(fixture.Host, new GameplayEnvelope { Type = "request", Kind = "chat", Text = "그림 시발" });
        await fixture.StartAsync();
        var chat = fixture.Activities.Single(activity => activity.Kind == "RoomChat");
        var start = fixture.Activities.Single(activity => activity.Kind == "MatchStart");
        Check(chat.AccountId == "host" && chat.Text == "그림 **" && start.AccountId == "host" && start.MatchId == fixture.State.MatchId,
            "방 채팅은 가린 문장으로, 경기 시작은 방장과 경기 ID로 기록해야 합니다.");
        fixture.Room.Disconnect(fixture.Observer, fixture.Now);
        Check(fixture.Activities[^1] is { Kind: "RoomLeave", AccountId: "observer", Voluntary: false }, "연결 종료를 방 퇴장으로 기록해야 합니다.");
        Report("방 입장·채팅·경기 시작·퇴장 활동 기록 검증");
    }

    private static async Task VerifyActivityLogDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString);
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439, "활동 로그 검증은 로컬 임시 PostgreSQL 25439 포트만 사용합니다.");
        string schema = "drawliar_activity_log_" + Guid.NewGuid().ToString("N");
        scoped.SearchPath = schema; scoped.Pooling = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var command = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await command.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            Guid[] accounts = new Guid[3];
            for (int index = 0; index < accounts.Length; index++) accounts[index] = await database.DevelopmentAccountAsync("로그검증" + index);
            Guid reporter = accounts[0], target = accounts[1], room = Guid.NewGuid();
            static string Detail(AdminActivityLogData entry, string key) => JsonDocument.Parse(entry.Detail).RootElement.GetProperty(key).ToString();

            await database.RequestFriendAsync(reporter, target);
            await database.SetBlockAsync(reporter, target, true);
            bool blocked = false;
            try { await database.RequestFriendAsync(reporter, target); }
            catch (ApiException exception) { blocked = exception.Code == "BlockedPlayer"; }
            await database.ReportAsync(reporter, new PlayerReportRequest
            { TargetAccountId = target.ToString(), Reason = PlayerReportRequest.REASON_ABUSE, RoomId = room.ToString() });
            await database.AdminBanAsync(target, true, "운영자");
            await database.AdminBanAsync(target, false, "운영자");
            await database.LogActivityAsync("LobbyChat", target, new { Text = "안녕" });
            var targetLogs = (await database.AdminAccountLogsAsync(target, 0)).Entries;
            Check(blocked && targetLogs.Select(entry => entry.Kind).SequenceEqual(new[] { "LobbyChat", "AdminBan", "AdminBan", "Report", "Block", "FriendRequest" }),
                "유저 로그는 본인 행동과 본인이 대상인 행동을 최신순으로 보여주고, 실패한 행동은 남기지 않아야 합니다.");
            var report = targetLogs.Single(entry => entry.Kind == "Report");
            Check(report.AccountId == reporter.ToString() && report.AccountName == "로그검증0" && report.TargetName == "로그검증1"
                && report.RoomId == room.ToString() && Detail(report, "Reason") == "Abuse" && Detail(targetLogs[1], "Admin") == "운영자"
                && Detail(targetLogs[0], "Text") == "안녕", "로그 행에 행위자·대상 이름과 방·세부 내용을 담아야 합니다.");
            var page = await database.AdminAccountLogsAsync(target, targetLogs[2].Id);
            Check(page.Entries.Select(entry => entry.Id).SequenceEqual(targetLogs.Skip(3).Select(entry => entry.Id)) && page.NextBefore == 0,
                "이전 기록 조회는 기준 ID보다 오래된 로그만 이어서 보여줘야 합니다.");

            await database.RegisterDedicatedAsync(new RegisterDedicatedRequest { NodeId = "profile-test-node", PublicUrl = "ws://127.0.0.1:19070/play" }, true);
            await using (var command = new NpgsqlCommand("INSERT INTO \"RoomGameAuthority\" (\"RoomId\",\"NodeId\") VALUES ($1,'profile-test-node')", owner))
            { command.Parameters.AddWithValue(room); await command.ExecuteNonQueryAsync(); }
            foreach (Guid account in accounts)
            {
                await using var command = new NpgsqlCommand("INSERT INTO \"RoomGameAdmission\" (\"RoomId\",\"AccountId\") VALUES ($1,$2)", owner);
                command.Parameters.AddWithValue(room); command.Parameters.AddWithValue(account); await command.ExecuteNonQueryAsync();
            }
            var match = ReplayDatabaseFixture(accounts, room, DateTimeOffset.UtcNow.AddMinutes(-5));
            await database.RecordRoomActivityAsync(new RoomActivityBatchRequest("profile-test-node", new[]
            {
                new RoomActivityData("RoomEnter", room.ToString(), target.ToString(), Spectator: true),
                new RoomActivityData("MatchStart", room.ToString(), reporter.ToString(), match.MatchId),
                new RoomActivityData("RoomChat", room.ToString(), target.ToString(), Text: "그림 **")
            }));
            await ExpectProfileErrorAsync(() => database.RecordRoomActivityAsync(new RoomActivityBatchRequest("other-node",
                new[] { new RoomActivityData("RoomChat", room.ToString(), target.ToString(), Text: "위조") })), "InvalidActivityAuthority", 403);
            await ExpectProfileErrorAsync(() => database.RecordRoomActivityAsync(new RoomActivityBatchRequest("profile-test-node",
                new[] { new RoomActivityData("MatchReward", room.ToString(), target.ToString()) })), "InvalidActivity", 400);
            await ExpectProfileErrorAsync(() => database.RecordRoomActivityAsync(new RoomActivityBatchRequest("profile-test-node",
                new[] { new RoomActivityData("RoomChat", room.ToString(), target.ToString()) })), "InvalidActivity", 400);
            await database.RecordMatchAsync(match);
            await database.RecordMatchAsync(CloneProfileMatch(match));

            var roomLog = await database.AdminRoomLogsAsync(room, 0);
            Check(roomLog.Entries.Select(entry => entry.Kind).SequenceEqual(new[] { "MatchReward", "MatchReward", "MatchReward", "RoomChat", "MatchStart", "RoomEnter", "Report" })
                && roomLog.Entries.Where(entry => entry.Kind == "MatchReward").All(entry => entry.MatchId == match.MatchId && int.Parse(Detail(entry, "Coins")) > 0)
                && Detail(roomLog.Entries[3], "Text") == "그림 **" && Detail(roomLog.Entries[5], "Spectator") == "True",
                "방 로그는 방 활동과 경기 보상을 한 번씩만 남겨야 합니다.");
            var roomMatch = roomLog.Matches.Single();
            Check(roomMatch.MatchId == match.MatchId && roomMatch.Rounds.Select(round => round.Round).SequenceEqual(new[] { 1, 2 })
                && roomMatch.Rounds.All(round => round.StrokeCount == 2) && await ActivityCountAsync(owner, "MatchRoundDrawing") == 2,
                "방 로그의 라운드 그림은 다시보기 저장소를 참조하고 중복 저장하지 않아야 합니다.");
            var drawing = await database.AdminMatchDrawingAsync(match.MatchId, 2);
            Check(drawing.Drawing.Round == 2 && drawing.Drawing.Strokes.Length == 2 && drawing.Drawing.Players.Length == 3,
                "운영툴은 참가자가 아니어도 보관된 라운드 그림을 조회해야 합니다.");
            await ExpectProfileErrorAsync(() => database.AdminMatchDrawingAsync(Guid.NewGuid().ToString(), 1), "DrawingReplayUnavailable", 404);

            long stored = await ActivityCountAsync(owner, "ActivityLog");
            await using (var age = new NpgsqlCommand("UPDATE \"ActivityLog\" SET \"CreatedAt\"=now()-interval '31 days' WHERE \"Kind\"='FriendRequest'", owner))
                await age.ExecuteNonQueryAsync();
            var cleanup = typeof(ServerDatabase).GetMethod("CleanupExpiredDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
            Check(await ActivityCountAsync(owner, "ActivityLog") == stored - 1
                && (await database.AdminAccountLogsAsync(target, 0)).Entries.All(entry => entry.Kind != "FriendRequest"),
                "보관 기간(기본 30일)이 지난 로그만 정리해야 합니다.");
            Report("유저·방 활동 로그 원자 기록·대상 조회·페이지·노드 권한·그림 공유 조회·보관 기간 정리 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner); await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ActivityCountAsync(NpgsqlConnection connection, string table)
    {
        Check(table is "ActivityLog" or "MatchRoundDrawing", "활동 로그 검증은 고정 테이블만 조회해야 합니다.");
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"" + table + "\"", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
