using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private const string KICK_NODE = "dedicated-kick-qa";

    private static async Task VerifyRoomKickHttpAsync(string mainUrl)
    {
        Check(mainUrl == "http://127.0.0.1:25550", "강퇴 검증은 전용 로컬 메인서버만 사용합니다.");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_KICK_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_KICK_TEST_DATABASE가 필요합니다."));
        Check(settings.Host == "127.0.0.1" && settings.Port == 25539 && settings.Database == "postgres"
            && Regex.IsMatch(settings.SearchPath ?? "", "^drawliar_kick_test_[0-9a-f]{32}$"), "강퇴 검증은 전용 무작위 PostgreSQL 스키마만 사용합니다.");
        using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = settings.ConnectionString }).Build());
        await using var owner = new NpgsqlConnection(settings.ConnectionString); await owner.OpenAsync();
        using var main = Client(mainUrl); await RequireHealthAsync(main);
        var users = new List<TestUser>(); var peers = new List<Peer>();
        for (int index = 0; index < 7; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "강퇴 검증은 로컬 게임서버만 사용합니다.");
        using var game = Client(users[0].Login.GameServerUrl);
        try
        {
            await using (var command = new NpgsqlCommand("SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=17", owner))
                Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1, "격리 DB에 강퇴 migration17을 적용해야 합니다.");
            var assignment = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
                { Settings = new ServerRoomSettings { RoomName = "강퇴검증방", Topics = new[] { "과일" } } }, users[0].Session.SessionToken);
            Check(assignment.DedicatedUrl == "ws://127.0.0.1:25570/play", "강퇴 검증은 전용 로컬 데디케이티드만 사용합니다.");
            var host = await Peer.ConnectAsync(assignment, ValidateCertificate); peers.Add(host);
            await host.WaitStateAsync(state => state.IsHost);
            async Task<Peer> Join(int index, bool spectator = false)
            {
                var ticket = await PostAsync<DedicatedAssignment>(game, "/api/rooms/" + assignment.RoomCode + "/join",
                    new JoinRoomRequest { AsSpectator = spectator }, users[index].Session.SessionToken);
                var peer = await Peer.ConnectAsync(ticket, ValidateCertificate); peers.Add(peer);
                await peer.WaitStateAsync(state => state.LocalIsSpectator == spectator);
                return peer;
            }
            var member = await Join(1); var spectator = await Join(2, true); var successor = await Join(3);
            await host.WaitStateAsync(state => state.Players.Length == 4);
            async Task<GameplayEnvelope> Kick(Peer actor, int target, string? operation = null)
            {
                string id = operation ?? Guid.NewGuid().ToString();
                int previousReplies = actor.Trace.Count(frame => frame.Type == "kickResult" && frame.RequestId == id);
                await actor.SendAsync(new GameplayEnvelope { Type = "request", Kind = "kick", Target = target, RequestId = id });
                await WaitAsync(() => actor.Trace.Count(frame => frame.Type == "kickResult" && frame.RequestId == id) > previousReplies, 10);
                return actor.Trace.Last(frame => frame.Type == "kickResult" && frame.RequestId == id);
            }
            int memberId = member.Latest!.LocalPlayerId;
            var staleTicket = await PostAsync<DedicatedAssignment>(game, "/api/rooms/" + assignment.RoomId + "/join", new JoinRoomRequest(), users[1].Session.SessionToken);
            Check((await Kick(member, host.Latest!.LocalPlayerId)).Code == "RoomKickDenied"
                && (await Kick(host, host.Latest.LocalPlayerId)).Code == "RoomKickDenied", "실제 wire에서도 비방장과 자기 강퇴를 거부해야 합니다.");
            string operation = Guid.NewGuid().ToString();
            Check((await Kick(host, memberId, operation)).Accepted, "실제 방장이 참가자 강퇴에 성공해야 합니다.");
            await member.WaitClosedAsync(5);
            Check(member.Trace.Last().Type == "kicked" && member.Trace.Last().Code == "RoomKicked"
                && member.CloseStatus == WebSocketCloseStatus.PolicyViolation && member.CloseDescription == "RoomKicked",
                "실제 소켓은 kicked JSON 다음 PolicyViolation/RoomKicked로 닫혀야 합니다.");
            Check((await Kick(host, memberId, operation)).Accepted, "같은 operation 재전송은 성공을 재확인해야 합니다.");
            await KickApiErrorAsync(() => database.RedeemTicketAsync(new RedeemTicketRequest { NodeId = KICK_NODE, RoomId = assignment.RoomId, JoinTicket = staleTicket.JoinTicket }), "InvalidTicket");
            Check((await GetAsync<ProfileData>(game, "/api/profile", users[1].Session.SessionToken)).AccountId == users[1].Login.AccountId
                && (await database.AuthenticateAsync(users[1].Login.SessionToken, "main")).AccountId.ToString() == users[1].Login.AccountId,
                "방 강퇴는 해당 dedicated 세션만 폐기하고 main/game 로그인과 프로필을 보존해야 합니다.");
            foreach (string id in new[] { assignment.RoomId, assignment.RoomCode })
                foreach (bool asSpectator in new[] { false, true })
                    await WorkshopErrorAsync(game, HttpMethod.Post, "/api/rooms/" + id + "/join", new JoinRoomRequest { AsSpectator = asSpectator },
                        users[1].Session.SessionToken, HttpStatusCode.Forbidden, "RoomKicked");
            using (var restarted = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DrawLiarDatabase"] = settings.ConnectionString }).Build()))
            {
                var account = await restarted.AuthenticateAsync(users[1].Session.SessionToken, "game:game-kick-qa");
                await KickApiErrorAsync(() => restarted.JoinRoomAsync(account, assignment.RoomId, false), "RoomKicked");
            }
            await host.WaitStateAsync(state => state.Players.All(player => player.Id != memberId));
            Check((await Kick(host, spectator.Latest!.LocalPlayerId)).Accepted, "실제 방장도 관전자를 강퇴해야 합니다.");
            await spectator.WaitClosedAsync(5);
            Check(spectator.CloseDescription == "RoomKicked", "관전자 강퇴도 동일 종료 계약을 사용해야 합니다.");
            int formerHost = host.Latest!.LocalPlayerId;
            await host.LeaveAsync();
            await successor.WaitStateAsync(state => state.IsHost);
            var former = await Join(0);
            Check(former.Latest!.LocalPlayerId != successor.Latest!.LocalPlayerId
                && (await Kick(former, successor.Latest.LocalPlayerId)).Code == "RoomKickDenied", "방장 이전 후 이전 방장 재입장은 권한을 복구하면 안 됩니다.");
            Check((await Kick(successor, former.Latest.LocalPlayerId)).Accepted, "새 방장은 이전 방장도 강퇴할 수 있어야 합니다.");
            await former.WaitClosedAsync(5);
            Check((await Kick(successor, formerHost)).Code == "RoomKickTargetUnavailable", "오래된 playerId의 강퇴는 다른 참가자에게 적용하면 안 됩니다.");
            var otherRoom = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
                { Settings = new ServerRoomSettings { RoomName = "다른방검증", Topics = new[] { "과일" } } }, users[6].Session.SessionToken);
            var otherHost = await Peer.ConnectAsync(otherRoom, ValidateCertificate); peers.Add(otherHost);
            var allowed = await PostAsync<DedicatedAssignment>(game, "/api/rooms/" + otherRoom.RoomId + "/join", new JoinRoomRequest(), users[1].Session.SessionToken);
            var otherMember = await Peer.ConnectAsync(allowed, ValidateCertificate); peers.Add(otherMember);
            await otherMember.WaitStateAsync(state => !state.LocalIsSpectator);
            Check(otherMember.Latest!.Players.Any(player => player.AccountId == users[1].Login.AccountId), "강퇴는 해당 방에만 적용하고 다른 방 입장은 허용해야 합니다.");
            await VerifyKickDatabaseRacesAsync(database, owner, users[4], users[5], users[6], otherRoom);
            Report("실격리 HTTP/WebSocket 강퇴·terminal 종료·UUID/코드/관전 재입장 차단·재시작 지속·방장 이전·다른방 허용 검증");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
            foreach (var user in users) await PostNoContentAsync(game, "/api/session/logout", new { }, user.Session.SessionToken);
        }
    }

    private static async Task VerifyKickDatabaseRacesAsync(ServerDatabase database, NpgsqlConnection owner,
        TestUser host, TestUser target, TestUser otherHost, DedicatedAssignment otherRoom)
    {
        var hostSession = await database.AuthenticateAsync(host.Session.SessionToken, "game:game-kick-qa");
        var targetSession = await database.AuthenticateAsync(target.Session.SessionToken, "game:game-kick-qa");
        var room = await database.CreateRoomAsync(hostSession, new CreateRoomRequest { Settings = new ServerRoomSettings { RoomName = "강퇴경합검증", Topics = new[] { "과일" } } });
        RedeemTicketRequest Redeem(DedicatedAssignment value) => new() { NodeId = KICK_NODE, RoomId = value.RoomId, JoinTicket = value.JoinTicket };
        var hostAdmission = await database.RedeemTicketAsync(Redeem(room));
        var targetAdmission = await database.RedeemTicketAsync(Redeem(await database.JoinRoomAsync(targetSession, room.RoomId, false)));
        var status = new RoomStatusData { RoomId = room.RoomId, OwnerAccountId = host.Login.AccountId, PlayerCount = 2,
            PlayerAccountIds = new[] { host.Login.AccountId, target.Login.AccountId }, Settings = room.RoomId == hostAdmission.Room.RoomId ? hostAdmission.Room.Settings : new() };
        await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest { NodeId = KICK_NODE, Rooms = new[] { status } });
        KickRoomRequest Request() => new() { NodeId = KICK_NODE, RoomId = room.RoomId, OwnerAccountId = host.Login.AccountId,
            OwnerSessionToken = hostAdmission.SessionToken, TargetAccountId = target.Login.AccountId, TargetSessionToken = targetAdmission.SessionToken,
            OperationId = Guid.NewGuid().ToString() };
        var forged = Request(); forged.OwnerAccountId = target.Login.AccountId; forged.OwnerSessionToken = targetAdmission.SessionToken;
        forged.TargetAccountId = host.Login.AccountId; forged.TargetSessionToken = hostAdmission.SessionToken;
        await KickApiErrorAsync(() => database.KickRoomAsync(forged), "RoomKickDenied");
        await using (var blocker = await owner.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand("SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 FOR UPDATE", owner, blocker))
            { command.Parameters.AddWithValue(Guid.Parse(room.RoomId)); await command.ExecuteScalarAsync(); }
            var stale = database.KickRoomAsync(Request());
            await Task.Delay(100);
            await using (var command = new NpgsqlCommand("UPDATE \"Room\" SET \"OwnerAccountId\"=$2 WHERE \"RoomId\"=$1", owner, blocker))
            { command.Parameters.AddWithValue(Guid.Parse(room.RoomId)); command.Parameters.AddWithValue(Guid.Parse(target.Login.AccountId)); await command.ExecuteNonQueryAsync(); }
            await blocker.CommitAsync();
            await KickApiErrorAsync(() => stale, "RoomKickDenied");
        }
        await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest { NodeId = KICK_NODE, Rooms = new[] { status } });
        var oldRequest = Request();
        await database.LogoutAsync(await database.AuthenticateAsync(hostAdmission.SessionToken, "dedicated:" + KICK_NODE));
        await KickApiErrorAsync(() => database.KickRoomAsync(oldRequest), "RoomKickDenied");
        hostAdmission = await database.RedeemTicketAsync(Redeem(await database.JoinRoomAsync(hostSession, room.RoomId, false)));
        var heldTicket = await database.JoinRoomAsync(targetSession, room.RoomId, false);
        var request = Request();
        RedeemTicketResponse? renewed = null;
        await using (var blocker = await owner.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand("SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 FOR UPDATE", owner, blocker))
            { command.Parameters.AddWithValue(Guid.Parse(room.RoomId)); await command.ExecuteScalarAsync(); }
            var redeemTask = database.RedeemTicketAsync(Redeem(heldTicket));
            await Task.Delay(100);
            var kickTasks = Enumerable.Range(0, 5).Select(_ => database.KickRoomAsync(request)).ToArray();
            await blocker.CommitAsync();
            try { renewed = await redeemTask; }
            catch (ApiException exception) { Check(exception.Code is "InvalidTicket" or "RoomKicked", "경합에서 강퇴 이후의 입장권 교환은 차단 오류여야 합니다."); }
            var results = await Task.WhenAll(kickTasks);
            Check(results.All(result => result.AccountId == target.Login.AccountId), "재교환과 같은 operation5개 경합은 계정 차단을 원자적으로 확정해야 합니다.");
        }
        Check(renewed == null || !await database.CheckDedicatedSessionAsync(new SessionCheckRequest { AccountId = target.Login.AccountId, SessionToken = renewed.SessionToken }),
            "교환 직전 캡처한 토큰이 갱신되어도 같은방 새 dedicated 세션을 폐기하고 동일 operation을 한 번 적용해야 합니다.");
        await KickApiErrorAsync(() => database.RedeemTicketAsync(Redeem(heldTicket)), "InvalidTicket");
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM \"RoomKick\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", owner))
        {
            command.Parameters.AddWithValue(Guid.Parse(room.RoomId)); command.Parameters.AddWithValue(Guid.Parse(target.Login.AccountId));
            Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1, "동시 같은 operation은 영속 차단을 정확히1개만 저장해야 합니다.");
        }
        var sync = await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest { NodeId = KICK_NODE, Rooms = new[] { status } });
        Check(sync.Kicks.Single(data => data.RoomId == room.RoomId).AccountIds.SequenceEqual(new[] { target.Login.AccountId }),
            "커밋 후 응답을 놓친 DS도 heartbeat로 영속 강퇴를 다시 적용해야 합니다.");
        var otherAdmission = await database.RedeemTicketAsync(Redeem(await database.JoinRoomAsync(targetSession, otherRoom.RoomId, false)));
        Check(await database.CheckDedicatedSessionAsync(new SessionCheckRequest { AccountId = target.Login.AccountId, SessionToken = otherAdmission.SessionToken }),
            "다른 방에서 발급한 세션은 유효해야 합니다.");
        await database.KickRoomAsync(request);
        Check(await database.CheckDedicatedSessionAsync(new SessionCheckRequest { AccountId = target.Login.AccountId, SessionToken = otherAdmission.SessionToken }),
            "오래된 강퇴 operation 재요청은 다른방의 새 세션을 폐기하면 안 됩니다.");
        Report("실PG 현재방장 rowlock 재검증·폐기 actor token·입장권 토큰갱신 우회 차단·동시 idempotency·heartbeat 복구·다른방 세션 보존 검증");
    }

    private static async Task KickApiErrorAsync<T>(Func<Task<T>> action, string code)
    {
        try { await action(); throw new InvalidOperationException("거부해야 하는 강퇴 요청이 성공했습니다."); }
        catch (ApiException exception) { Check(exception.Code == code, "강퇴 거부는 " + code + "이어야 합니다. 실제: " + exception.Code); }
    }
}
