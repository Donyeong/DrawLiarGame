using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyRoomCreationAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_ROOM_CREATION_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_ROOM_CREATION_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25939 && scoped.Username == "room_creation_qa"
            && Regex.IsMatch(scoped.Database ?? "", "^drawliar_room_creation_[0-9a-f]{32}$")
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_room_creation_[0-9a-f]{32}$"),
            "방 생성 검증은 격리 localhost25939 클러스터·전용 사용자·고유 DB/schema만 허용합니다.");
        scoped.Pooling = false; scoped.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        string schema = scoped.SearchPath!;
        await using (var create = new NpgsqlCommand("CREATE SCHEMA \"" + schema + "\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            const string GAME_NODE = "game-room-creation-qa", DEDICATED_NODE = "dedicated-room-creation-qa";
            await database.RegisterGameAsync(new RegisterGameRequest { NodeId = GAME_NODE, PublicUrl = "http://127.0.0.1:25960" }, true);
            await database.RegisterDedicatedAsync(new RegisterDedicatedRequest
                { NodeId = DEDICATED_NODE, PublicUrl = "ws://127.0.0.1:25970/play", Capacity = 8 }, true);
            async Task<ServerSession> Enter(GuestLoginRequest request)
            {
                var login = await database.GuestLoginAsync(request);
                var main = await database.AuthenticateAsync(login.Token, "main");
                var assigned = await database.AssignGameAsync(main, login.Token);
                var entered = await database.EnterGameAsync(assigned.AssignmentToken, GAME_NODE);
                return await database.AuthenticateAsync(entered.SessionToken, "game:" + GAME_NODE);
            }
            Task<DedicatedAssignment> Create(ServerSession session, string name) => database.CreateRoomAsync(session,
                new CreateRoomRequest { Settings = new ServerRoomSettings { RoomName = name, Topics = new[] { "과일" } } });
            Task<RedeemTicketResponse> Redeem(DedicatedAssignment assignment) => database.RedeemTicketAsync(
                new RedeemTicketRequest { NodeId = DEDICATED_NODE, RoomId = assignment.RoomId, JoinTicket = assignment.JoinTicket });
            async Task<DedicatedHeartbeatResponse> Heartbeat(DedicatedRoom room)
            {
                var status = room.Status();
                var result = await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest
                    { NodeId = DEDICATED_NODE, Rooms = new[] { status } });
                room.AcknowledgeAdmissions(status.AdmissionIds);
                return result;
            }
            var credentials = NewGuestRequest();
            var session = await Enter(credentials);
            var other = await Enter(NewGuestRequest());
            var first = await Create(session, "기존방");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Create(session, "추가방" + index)));
            var assignments = concurrent.Prepend(first).ToArray();
            Check(assignments.Select(room => room.RoomId).Distinct().Count() == 5
                && assignments.Select(room => room.RoomCode).Distinct().Count() == 5
                && (await database.RoomsAsync(null, true)).Rooms.Length == 5,
                "미사용 입장권이 있는 이전 방도 유지하면서 동일 계정의 병렬 방 생성을 허용해야 합니다.");
            Check((await database.RoomsAsync(null)).Rooms.Length == 0, "아직 아무도 입장하지 않은 방은 공개 목록에서 숨겨야 합니다.");
            Report("기존 미사용 입장권이 있는 상태의 동일 계정 4개 병렬 방 생성");
            var hostTicket = await Redeem(first);
            var memberTicket = await Redeem(await database.JoinRoomAsync(other, first.RoomCode, false));
            var data = new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } };
            var room = new DedicatedRoom(hostTicket.Room, data, Array.Empty<ServerTopicData>(), 0, () => { });
            await using var host = new GameConnection(new PolicySocket(), hostTicket.AccountId, hostTicket.SessionToken, CancellationToken.None);
            await using var member = new GameConnection(new PolicySocket(), memberTicket.AccountId, memberTicket.SessionToken, CancellationToken.None);
            Check(room.Join(host, hostTicket, 0) && room.Join(member, memberTicket, 0), "실제 계정·입장권으로 기존 방을 입장시켜야 합니다.");
            await Heartbeat(room);
            host.Abort(); room.Disconnect(host, 1);
            Check(room.Status().OwnerAccountId == other.AccountId.ToString() && room.Status().PlayerCount == 1 && !room.Status().Closed,
                "방장 연결 종료 시 남은 참가자에게 위임하고 기존 방을 유지해야 합니다.");
            var reentered = await Enter(credentials);
            Check(reentered.AccountId == session.AccountId && reentered.Hash != session.Hash, "재실행은 동일 계정의 새 게임 세션을 발급해야 합니다.");
            var fresh = await Create(reentered, "재실행후새방");
            Check((await database.RoomsAsync(null, true)).Rooms.Single(value => value.RoomId == first.RoomId).OwnerAccountId
                == session.AccountId.ToString(), "소유권 변경 heartbeat 전에도 이전 소유자의 새 방 생성이 가능해야 합니다.");
            await Heartbeat(room);
            Check((await database.RoomsAsync(null)).Rooms.Single(value => value.RoomId == first.RoomId).OwnerAccountId == other.AccountId.ToString(),
                "새 방 생성은 기존 방의 참가자·위임 결과를 삭제하거나 덮어쓰면 안 됩니다.");
            var validPending = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Create(reentered, "유효입장권방" + index)));
            Report("게임 종료·재로그인 후 heartbeat 전/후 새 방 생성과 기존 방장 위임 보존");
            var freshTicket = await Redeem(fresh);
            var abandoned = new DedicatedRoom(freshTicket.Room, data, Array.Empty<ServerTopicData>(), 0, () => { });
            await using var crashed = new GameConnection(new PolicySocket(), freshTicket.AccountId, freshTicket.SessionToken, CancellationToken.None);
            Check(abandoned.Join(crashed, freshTicket, 0), "단독 방장의 비정상 종료 검증을 준비해야 합니다.");
            await Heartbeat(abandoned);
            crashed.Abort(); abandoned.Disconnect(crashed, 1); await Heartbeat(abandoned);
            var afterCrash = await Create(reentered, "종료유예중새방");
            Check(!(await database.RoomsAsync(null)).Rooms.Any(value => value.RoomId == fresh.RoomId)
                && (await database.RoomsAsync(null, true)).Rooms.Any(value => value.RoomId == fresh.RoomId),
                "아무도 없는 장애 방은 공개 목록에서 숨기고 재접속 유예 중에도 새 방 생성을 허용해야 합니다.");
            Check(!abandoned.Tick(120.9) && abandoned.Tick(121), "기존 120초 장애 재접속 유예를 보존해야 합니다.");
            Check((await Heartbeat(abandoned)).ClosedRoomIds.Contains(fresh.RoomId), "빈 장애 방은 재접속 유예 후 삭제해야 합니다.");
            room.Tick(122); await member.ReceiveAsync(); room.Disconnect(member, 123);
            Check(room.Status().Closed && (await Heartbeat(room)).ClosedRoomIds.Contains(first.RoomId),
                "위임된 마지막 참가자가 정상 퇴장하면 빈 방을 삭제해야 합니다.");
            Check((await database.RoomsAsync(null, true)).Rooms.Select(value => value.RoomId).Order()
                .SequenceEqual(validPending.Select(value => value.RoomId).Append(afterCrash.RoomId).Order()),
                "빈 방 정리는 다른 방의 유효한 입장권이나 방을 삭제하면 안 됩니다.");
            var lastTicket = await Redeem(afterCrash);
            var lastRoom = new DedicatedRoom(lastTicket.Room, data, Array.Empty<ServerTopicData>(), 0, () => { });
            await using var lastHost = new GameConnection(new PolicySocket(), lastTicket.AccountId, lastTicket.SessionToken, CancellationToken.None);
            Check(lastRoom.Join(lastHost, lastTicket, 0), "정상 퇴장 검증의 단독 방장을 입장시켜야 합니다.");
            await Heartbeat(lastRoom); await lastHost.ReceiveAsync(); lastRoom.Disconnect(lastHost, 1);
            Check(lastRoom.Status().Closed && (await Heartbeat(lastRoom)).ClosedRoomIds.Contains(afterCrash.RoomId),
                "단독 방장이 정상 퇴장하면 재접속 대기 없이 빈 방을 삭제해야 합니다.");
            for (int index = 0; index < 6; index++) await Create(reentered, "정원검증" + index);
            bool capacityRejected = false;
            try { await Create(reentered, "정원초과"); }
            catch (ApiException error) when (error.Code == "DedicatedUnavailable" && error.Status == 503) { capacityRejected = true; }
            Check(capacityRejected && (await database.RoomsAsync(null, true)).Rooms.Length == 8,
                "계정당 제한을 제거해도 데디케이티드 노드의 전체 방 정원은 보존해야 합니다.");
            Report("동일 계정 병렬/재실행 방 생성·미사용 입장권 보존·이전 방 유지·방장 위임·정상/장애 빈 방 정리·노드 정원 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA \"" + schema + "\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
            Report("방 생성 검증 임시 스키마 삭제");
        }
    }
}
