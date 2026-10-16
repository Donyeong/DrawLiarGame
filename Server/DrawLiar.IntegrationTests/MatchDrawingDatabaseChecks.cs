using System.Reflection;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyMatchDrawingDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString);
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439, "그림 보관 검증은 로컬 임시 PostgreSQL 25439 포트만 사용합니다.");
        string schema = "drawliar_drawing_replay_" + Guid.NewGuid().ToString("N");
        scoped.SearchPath = schema; scoped.Pooling = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var command = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await command.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            Guid[] accounts = new Guid[4];
            for (int index = 0; index < accounts.Length; index++) accounts[index] = await database.DevelopmentAccountAsync("그림보관검증" + index);
            await database.RegisterDedicatedAsync(new RegisterDedicatedRequest
            { NodeId = "profile-test-node", PublicUrl = "ws://127.0.0.1:19070/play" }, true);
            Guid room = Guid.NewGuid();
            await using (var command = new NpgsqlCommand("INSERT INTO \"RoomGameAuthority\" (\"RoomId\",\"NodeId\") VALUES ($1,'profile-test-node')", owner))
            { command.Parameters.AddWithValue(room); await command.ExecuteNonQueryAsync(); }
            foreach (Guid account in accounts.Take(3))
            {
                await using var command = new NpgsqlCommand("INSERT INTO \"RoomGameAdmission\" (\"RoomId\",\"AccountId\") VALUES ($1,$2)", owner);
                command.Parameters.AddWithValue(room); command.Parameters.AddWithValue(account); await command.ExecuteNonQueryAsync();
            }
            var request = ReplayDatabaseFixture(accounts.Take(3).ToArray(), room, DateTimeOffset.UtcNow.AddHours(-1));
            int coinsBefore = (await database.ProfileAsync(accounts[0])).Coins;
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => database.RecordMatchAsync(CloneProfileMatch(request))));
            Check(await ReplayRowCountAsync(owner, "MatchRoundDrawing") == 2 && await ReplayRowCountAsync(owner, "AccountMatch") == 3
                && await ReplayRowCountAsync(owner, "MatchRecord") == 1, "공유 경기의 라운드 그림은 참가자 수와 무관하게 두 행만 한 번 저장해야 합니다.");
            var list = await database.MatchDrawingsAsync(accounts[0], request.MatchId);
            Check(list.Rounds.Select(round => round.Round).SequenceEqual(new[] { 1, 2 }) && list.RetentionDays == 30 && !list.IsPartial
                && (DateTimeOffset.Parse(list.ExpiresAt) - DateTimeOffset.Parse(request.PlayedAt).AddDays(30)).Duration() < TimeSpan.FromMilliseconds(1),
                "완료된 두 라운드를 경기 시간 기준 30일 보관해야 합니다.");
            foreach (Guid account in accounts.Take(3))
            {
                var drawing = await database.MatchDrawingAsync(account, request.MatchId, 2);
                Check(drawing.Drawing.Round == 2 && drawing.Drawing.Strokes.Length == 2 && drawing.Drawing.Strokes[1].Eraser
                    && drawing.Drawing.Players.Length == 3, "모든 실제 참가자는 동일한 작가·필압·지우개 원본 그림을 조회해야 합니다.");
            }
            var own = (await database.PublicProfileAsync(accounts[0], accounts[0])).RecentMatches.Single();
            var outsider = (await database.PublicProfileAsync(accounts[3], accounts[0])).RecentMatches.Single();
            Check(own.CanViewDrawings && own.DrawingExpiresAt == list.ExpiresAt && !outsider.CanViewDrawings,
                "조회자 자신의 경기 참가 권한을 검사해 다시보기 버튼을 제공해야 합니다.");
            await ExpectProfileErrorAsync(() => database.MatchDrawingsAsync(accounts[3], request.MatchId), "DrawingReplayForbidden", 403);
            await ExpectProfileErrorAsync(() => database.MatchDrawingAsync(accounts[3], request.MatchId, 1), "DrawingReplayForbidden", 403);
            await ExpectProfileErrorAsync(() => database.MatchDrawingAsync(accounts[0], request.MatchId, 3), "DrawingReplayUnavailable", 404);
            int coinsAfter = (await database.ProfileAsync(accounts[0])).Coins;
            Check(coinsAfter > coinsBefore, "그림과 함께 정상 경기 보상을 한 번 지급해야 합니다.");
            await database.RecordMatchAsync(CloneProfileMatch(request));
            Check((await database.ProfileAsync(accounts[0])).Coins == coinsAfter, "그림 재전송이 경기 보상을 중복 지급하면 안 됩니다.");
            var reordered = CloneProfileMatch(request);
            reordered.DrawingArchives = reordered.DrawingArchives.Reverse().ToArray();
            await database.RecordMatchAsync(reordered);
            var conflict = CloneProfileMatch(request);
            var changed = DrawingArchiveCodec.Decode(conflict.DrawingArchives[0]); changed.Word = "수정사과";
            Check(DrawingArchiveCodec.TryEncode(changed, out conflict.DrawingArchives[0]), "변경된 그림 압축에 성공해야 합니다.");
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(conflict), "MatchResultConflict", 409);
            await VerifyReplayInvalidPayloadsAsync(database, request, accounts[3]);
            Check(await ReplayRowCountAsync(owner, "MatchRecord") == 1 && (await database.ProfileAsync(accounts[0])).Coins == coinsAfter,
                "변조·손상 그림은 전적과 보상을 부분 저장하면 안 됩니다.");
            await VerifyMatchDrawingHttpAsync(database, scoped.ConnectionString, request, accounts[0], accounts[3]);

            var partial = ReplayDatabaseFixture(accounts.Take(3).ToArray(), room, DateTimeOffset.UtcNow.AddMinutes(-30));
            partial.DrawingArchives = partial.DrawingArchives.Take(1).ToArray(); partial.DrawingsIncomplete = true;
            await database.RecordMatchAsync(partial);
            Check((await database.MatchDrawingsAsync(accounts[1], partial.MatchId)).IsPartial
                && (await database.MatchDrawingsAsync(accounts[1], partial.MatchId)).Rounds.Length == 1,
                "보관 한도에 도달한 경기에서도 전적·보상을 저장하고 실제 보관 라운드만 부분 보관으로 알려야 합니다.");
            var old = ReplayDatabaseFixture(accounts.Take(3).ToArray(), room, DateTimeOffset.UtcNow.AddDays(-31));
            await database.RecordMatchAsync(old);
            Check(await ReplayRowCountAsync(owner, "MatchRoundDrawing") == 3, "늦게 도착한 만료 경기의 그림 보관 기간을 연장하면 안 됩니다.");
            await ExpectProfileErrorAsync(() => database.MatchDrawingsAsync(accounts[0], old.MatchId), "DrawingReplayExpired", 410);
            await using (var expire = new NpgsqlCommand("""
                WITH "expired" AS (UPDATE "MatchRecord" SET "DrawingExpiresAt"=now()-interval '10 seconds' WHERE "MatchId"=$1 RETURNING "MatchId")
                UPDATE "MatchRoundDrawing" SET "ExpiresAt"=now()-interval '10 seconds' WHERE "MatchId" IN (SELECT "MatchId" FROM "expired")
                """, owner))
            { expire.Parameters.AddWithValue(Guid.Parse(request.MatchId)); await expire.ExecuteNonQueryAsync(); }
            await ExpectProfileErrorAsync(() => database.MatchDrawingAsync(accounts[0], request.MatchId, 1), "DrawingReplayExpired", 410);
            var cleanup = typeof(ServerDatabase).GetMethod("CleanupExpiredDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)cleanup.Invoke(database, new object[] { CancellationToken.None })!;
            Check(await ReplayRowCountAsync(owner, "MatchRoundDrawing") == 1 && await ReplayRowCountAsync(owner, "MatchRecord") == 3
                && await ReplayRowCountAsync(owner, "AccountMatch") == 9, "만료 정리는 그림 payload만 삭제하고 전체 전적과 보상을 보존해야 합니다.");
            await database.RecordMatchAsync(CloneProfileMatch(request));
            Check(await ReplayRowCountAsync(owner, "MatchRoundDrawing") == 1, "만료 후 아웃박스 재시도가 삭제된 그림을 복구하면 안 됩니다.");
            var expired = (await database.PublicProfileAsync(accounts[0], accounts[0])).RecentMatches.Single(match => match.MatchId == request.MatchId);
            Check(!expired.CanViewDrawings && DateTimeOffset.Parse(expired.DrawingExpiresAt) < DateTimeOffset.UtcNow,
                "그림이 삭제된 뒤에도 전적에서 보관 만료를 구분해야 합니다.");
            Report("공유 라운드 그림 저장·원자적 보상·병렬 멱등성·참가자 권한·압축 검증·30일 만료·payload 정리·지연 아웃박스 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner); await drop.ExecuteNonQueryAsync();
        }
    }

    private static MatchResultRequest ReplayDatabaseFixture(Guid[] accounts, Guid room, DateTimeOffset playedAt)
    {
        var request = ProfileMatchFixture(accounts, room, playedAt, 0);
        foreach (var player in request.Players) { player.WeightedRoundParticipants = 6; player.WeightedRoundScore = 3L * player.Score; }
        request.DrawingArchives = Enumerable.Range(1, 2).Select(round =>
        {
            var drawing = new RoundDrawingData
            {
                Round = round, Topic = "과일", Word = "사과",
                Players = accounts.Select((account, index) => new ReplayPlayerData
                { PlayerId = index + 1, AccountId = account.ToString(), DisplayName = "화가" + index }).ToArray(),
                Strokes = new[]
                {
                    new ReplayStrokeData { X1=.1f,Y1=.2f,X2=.5f,Y2=.6f,Size=.02f,StartSize=.005f,R=240,AuthorPlayerId=1 },
                    new ReplayStrokeData { X1=.2f,Y1=.3f,X2=.7f,Y2=.8f,Size=.01f,Eraser=true,AuthorPlayerId=2 }
                }
            };
            Check(DrawingArchiveCodec.TryEncode(drawing, out var archive), "검증 그림을 압축해야 합니다.");
            return archive;
        }).ToArray();
        return request;
    }

    private static async Task VerifyReplayInvalidPayloadsAsync(ServerDatabase database, MatchResultRequest source, Guid outsider)
    {
        async Task Invalid(Action<MatchResultRequest> alter)
        {
            var request = CloneProfileMatch(source); request.MatchId = Guid.NewGuid().ToString(); alter(request);
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(request), "InvalidDrawingReplay", 400);
        }
        await Invalid(request => request.DrawingArchives[1].Round = 1);
        await Invalid(request => request.DrawingArchives[0].Payload = new byte[] { 1, 2, 3 });
        await Invalid(request => request.DrawingArchives[0].Payload = new byte[DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES + 1]);
        await Invalid(request =>
        {
            var drawing = DrawingArchiveCodec.Decode(request.DrawingArchives[0]); drawing.Players[0].AccountId = outsider.ToString();
            DrawingArchiveCodec.TryEncode(drawing, out request.DrawingArchives[0]);
        });
        await Invalid(request =>
        {
            var drawing = DrawingArchiveCodec.Decode(request.DrawingArchives[0]); drawing.Strokes[0].X1 = 5;
            DrawingArchiveCodec.TryEncode(drawing, out request.DrawingArchives[0]);
        });
        await Invalid(request =>
        {
            var drawing = DrawingArchiveCodec.Decode(request.DrawingArchives[0]); drawing.Strokes[0].AuthorPlayerId = 999;
            DrawingArchiveCodec.TryEncode(drawing, out request.DrawingArchives[0]);
        });
    }

    private static async Task<long> ReplayRowCountAsync(NpgsqlConnection connection, string table)
    {
        Check(table is "MatchRecord" or "AccountMatch" or "MatchRoundDrawing", "그림 검증은 고정 테이블만 조회해야 합니다.");
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"" + table + "\"", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
