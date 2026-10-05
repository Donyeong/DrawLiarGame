using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyMidRoundDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_MIDROUND_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_MIDROUND_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439 && scoped.Database == "postgres", "난입 검증은 전용 로컬 PostgreSQL 25439만 사용합니다.");
        string schema = "drawliar_midround_test_" + Guid.NewGuid().ToString("N");
        scoped.SearchPath = schema; scoped.Pooling = false; scoped.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await using (var downgrade = new NpgsqlCommand("""
                ALTER TABLE "MatchRecord" DROP CONSTRAINT "MatchRecord_PlayerCountBounds";
                ALTER TABLE "MatchRecord" ADD CONSTRAINT "MidRound_LegacyPlayers" CHECK ("PlayerCount" BETWEEN 3 AND 8);
                ALTER TABLE "AccountMatch" DROP CONSTRAINT "AccountMatch_RankBounds";
                ALTER TABLE "AccountMatch" ADD CONSTRAINT "MidRound_LegacyRank" CHECK ("Rank" BETWEEN 1 AND 8);
                ALTER TABLE "AccountMatch" ADD CONSTRAINT "MidRound_UnrelatedScore" CHECK ("Score"<100000);
                DELETE FROM "SchemaVersion" WHERE "Version"=10;
                """, owner)) await downgrade.ExecuteNonQueryAsync();
            await database.InitializeAsync(); await database.InitializeAsync();
            for (int index = 0; index < 2; index++)
            { await using var migration = new NpgsqlCommand(ProfileMigration("010_MidRoundStatistics.sql"), owner); await migration.ExecuteNonQueryAsync(); }
            var checks = await ProfileChecksAsync(owner);
            Check(checks["AccountMatch_RankBounds"] == "\"Rank\">=1AND\"Rank\"<=64" && checks.ContainsKey("MidRound_UnrelatedScore")
                && !checks.ContainsKey("MidRound_LegacyRank"), "마이그레이션은 이름이 바뀐 기존8명 순위 제한만 교체하고 다른 점수 제약을 유지해야 합니다.");
            var sessions = new List<ServerSession>();
            for (int index = 0; index < 16; index++)
            {
                Guid account = await database.DevelopmentAccountAsync("난입 검증" + index);
                var issued = await database.IssueSessionAsync(account, "game:midround-test");
                sessions.Add(await database.AuthenticateAsync(issued.Token, "game:midround-test"));
            }
            const string NODE = "midround-test";
            const string PASSWORD = "MidRoundQa2931";
            await database.RegisterDedicatedAsync(new RegisterDedicatedRequest { NodeId = NODE, PublicUrl = "ws://127.0.0.1:19070/play", Capacity = 32 }, true);
            var room = await database.CreateRoomAsync(sessions[0], new CreateRoomRequest
            { Password = PASSWORD, Settings = new ServerRoomSettings { IsPrivate = true, AllowMidRoundJoin = true, Topics = new[] { "과일" } } });
            var redeemed = new List<RedeemTicketResponse>();
            async Task<RedeemTicketResponse> Redeem(DedicatedAssignment ticket)
            { var response = await database.RedeemTicketAsync(new RedeemTicketRequest { NodeId = NODE, RoomId = ticket.RoomId, JoinTicket = ticket.JoinTicket }); redeemed.Add(response); return response; }
            await Redeem(room);
            for (int index = 1; index < 3; index++) await Redeem(await database.JoinRoomAsync(sessions[index], room.RoomId, false, PASSWORD));
            string[] seated = sessions.Take(3).Select(session => session.AccountId.ToString()).ToArray();
            var status = new RoomStatusData { RoomId = room.RoomId, OwnerAccountId = seated[0], PlayerCount = 3, PlayerAccountIds = seated,
                AdmissionIds = redeemed.Select(value => value.AdmissionId).ToArray(), Settings = redeemed[0].Room.Settings };
            async Task Heartbeat() => await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest { NodeId = NODE, Rooms = new[] { status } });
            await Heartbeat();
            var beforeStart = await database.JoinRoomAsync(sessions[3], room.RoomId, false, PASSWORD);
            status.IsInProgress = true; await Heartbeat();
            var late = await Redeem(beforeStart);
            Check(!late.IsSpectator && !late.SpectatorOnly && late.Room.Settings.AllowMidRoundJoin, "시작 전에 받은 일반 티켓도 난입을 허용한 현재 경기에서 참가자로 교환해야 합니다.");
            await ExpectProfileErrorAsync(() => database.JoinRoomAsync(sessions[4], room.RoomId, false), "RoomPasswordRequired", 403);
            await ExpectProfileErrorAsync(() => database.JoinRoomAsync(sessions[4], room.RoomId, false, "wrong"), "InvalidRoomPassword", 403);
            var explicitObserver = await Redeem(await database.JoinRoomAsync(sessions[4], room.RoomId, true, PASSWORD));
            Check(explicitObserver.IsSpectator && explicitObserver.SpectatorOnly, "명시 관전은 현재 경기 참가로 승격하면 안 됩니다.");
            var parallel = await Task.WhenAll(sessions.Skip(5).Take(5).Select(async session =>
            {
                try { return await database.JoinRoomAsync(session, room.RoomId, false, PASSWORD); }
                catch (ApiException exception) when (exception.Code == "RoomFull" && exception.Status == 409) { return null; }
            }));
            Check(parallel.Count(ticket => ticket != null) == 4, "기존4석과 병렬 예약4석까지만 허용해야 합니다.");
            foreach (var ticket in parallel.OfType<DedicatedAssignment>())
                Check(!(await Redeem(ticket)).IsSpectator, "예약한 일반 티켓은 진행 중에도 참가자 좌석이어야 합니다.");
            var actual = redeemed.Where(response => !response.IsSpectator).Select(response => response.AccountId).ToArray();
            Check(actual.Length == 8, "진행 중 동시 참가 정원은8명이어야 합니다.");
            status.PlayerCount = 8; status.PlayerAccountIds = actual; status.SpectatorCount = 1; status.SpectatorAccountIds = new[] { explicitObserver.AccountId };
            status.AdmissionIds = redeemed.Select(response => response.AdmissionId).ToArray(); await Heartbeat();
            var reconnect = await Redeem(await database.JoinRoomAsync(sessions[0], room.RoomId, false));
            Check(!reconnect.IsSpectator, "가득 찬 사설방의 확인된 참가자는 비밀번호 없이 원래 좌석으로 복원해야 합니다.");
            await ExpectProfileErrorAsync(() => database.JoinRoomAsync(sessions[10], room.RoomId, false, PASSWORD), "RoomFull", 409);
            status.PlayerAccountIds = actual.Where(account => account != sessions[1].AccountId.ToString()).ToArray(); status.PlayerCount = 7;
            status.AdmissionIds = new[] { reconnect.AdmissionId }; await Heartbeat();
            var ninth = await Redeem(await database.JoinRoomAsync(sessions[10], room.RoomId, false, PASSWORD));
            Check(!ninth.IsSpectator, "정상 퇴장 빈자리는 새 일반 참가자에게 배정해야 합니다.");

            var result = new MatchResultRequest { NodeId = NODE, RoomId = room.RoomId, MatchId = Guid.NewGuid().ToString(), PlayedAt = ServerRuntime.Timestamp(DateTimeOffset.UtcNow), RoundCount = 1,
                Players = actual.Concat(new[] { ninth.AccountId }).Select((account, index) => new MatchPlayerResult { AccountId = account, Score = 9 - index,
                    Rank = index + 1, Won = index == 0, RoundsPlayed = 1, CitizenRounds = 1 }).ToArray() };
            await database.RecordMatchAsync(result); await database.RecordMatchAsync(result);
            var profile = await database.PublicProfileAsync(sessions[0].AccountId, Guid.Parse(ninth.AccountId));
            Check(profile.Stats.MatchesPlayed == 1 && profile.RecentMatches.Single() is { PlayerCount: 9, Rank: 9 }, "9명 누적 전적은 중복 없이 저장되고9위도 조회해야 합니다.");
            var tooMany = CloneProfileMatch(result); tooMany.MatchId = Guid.NewGuid().ToString();
            tooMany.Players = Enumerable.Range(0, 65).Select(_ => new MatchPlayerResult { AccountId = Guid.NewGuid().ToString(), Rank = 1, RoundsPlayed = 1, CitizenRounds = 1 }).ToArray();
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(tooMany), "InvalidMatchResult", 400);
            var wrongRank = CloneProfileMatch(result); wrongRank.MatchId = Guid.NewGuid().ToString(); wrongRank.Players[^1].Rank = 8;
            await ExpectProfileErrorAsync(() => database.RecordMatchAsync(wrongRank), "InvalidMatchResult", 400);
            Report("격리PG 난입 티켓·명시관전·8석 병렬 예약·정상퇴장 재사용·비밀번호/재접속·9명 전적·64명 한도 검증");

            status.IsInProgress = false; status.PlayerAccountIds = status.PlayerAccountIds.Take(6).ToArray(); status.PlayerCount = 6;
            status.AdmissionIds = redeemed.Select(response => response.AdmissionId).ToArray(); await Heartbeat();
            var disabledSettings = status.Settings; disabledSettings.AllowMidRoundJoin = false;
            var disabledConfig = await database.ConfigureRoomAsync(new ConfigureRoomRequest { RoomId = room.RoomId, NodeId = NODE, OwnerAccountId = seated[0],
                ExpectedVersion = 0, OperationId = Guid.NewGuid().ToString(), Settings = disabledSettings });
            status.ConfigurationVersion = disabledConfig.Version; status.Settings = disabledConfig.Settings; await Heartbeat();
            var offTicket = await database.JoinRoomAsync(sessions[11], room.RoomId, false, PASSWORD);
            status.IsInProgress = true; await Heartbeat();
            var automaticObserver = await Redeem(offTicket);
            Check(automaticObserver.IsSpectator && !automaticObserver.SpectatorOnly, "난입 금지로 시작된 경기는 대기실 일반 티켓도 자동 관전으로 교환해야 합니다.");
            status.IsInProgress = false; await Heartbeat();
            status.IsInProgress = true; await Heartbeat();
            var provisionalOff = await database.JoinRoomAsync(sessions[12], room.RoomId, false, PASSWORD);
            status.IsInProgress = false; await Heartbeat();
            var enabledSettings = status.Settings; enabledSettings.AllowMidRoundJoin = true;
            var enabledConfig = await database.ConfigureRoomAsync(new ConfigureRoomRequest { RoomId = room.RoomId, NodeId = NODE, OwnerAccountId = seated[0],
                ExpectedVersion = disabledConfig.Version, OperationId = Guid.NewGuid().ToString(), Settings = enabledSettings });
            status.ConfigurationVersion = enabledConfig.Version; status.Settings = enabledConfig.Settings; status.IsInProgress = true; await Heartbeat();
            // 다시 입장하는 미확정 예약은 현재 정책에 따라 재분류한다.
            status.PlayerCount = 6; status.PlayerAccountIds = status.PlayerAccountIds.Take(6).ToArray(); await Heartbeat();
            var reclassified = await Redeem(provisionalOff);
            Check(!reclassified.IsSpectator && !reclassified.SpectatorOnly, "off에서 받은 미교환 일반 티켓을 on 경기에서 영구 관전으로 고정하면 안 됩니다.");
            var autoRestore = await Redeem(await database.JoinRoomAsync(sessions[11], room.RoomId, false, PASSWORD));
            Check(!autoRestore.IsSpectator, "미확정 자동 관전 계정의 새 입장도 최신 참가 정책으로 재분류해야 합니다.");
            Report("시작 전 티켓·off→on 옵션 교환·미확정 roster 재분류·동일 계정 예약 중복 제외 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner); await drop.ExecuteNonQueryAsync();
            Report("난입 검증 임시 스키마 삭제");
        }
    }
}
