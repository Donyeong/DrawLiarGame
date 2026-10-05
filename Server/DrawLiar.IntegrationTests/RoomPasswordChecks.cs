using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyRoomPasswordAsync(string mainUrl, bool legacyOnly = false)
    {
        const string INITIAL_PASSWORD = "ABcd_QA4096";
        const string CHANGED_PASSWORD = "Fresh_QA6581";
        const string FINAL_PASSWORD = "Final_QA7319";
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "방 비밀번호 검증은 전용 로컬 메인서버 25550만 사용합니다.");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_SOCIAL_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_SOCIAL_TEST_DATABASE가 필요합니다."));
        Check(settings.Host == "127.0.0.1" && settings.Port == 25539 && settings.Database == "postgres"
            && Regex.IsMatch(settings.SearchPath ?? "", "^drawliar_social_test_[0-9a-f]{32}$"),
            "방 비밀번호 검증은 무작위 스키마를 사용하는 전용 로컬 PostgreSQL 25539만 사용합니다.");
        await using var owner = new NpgsqlConnection(settings.ConnectionString);
        await owner.OpenAsync();
        using var main = Client(mainUrl);
        await RequireHealthAsync(main);
        var users = new List<TestUser>();
        for (int index = 0; index < 9; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "방 비밀번호 검증 게임서버는 전용 로컬 25560이어야 합니다.");
        using var game = Client(users[0].Login.GameServerUrl);
        var hostUser = users[0];
        var memberUser = users[1];
        var observerUser = users[2];
        var invitedUser = users[3];
        var outsider = users[4];
        var publicUser = users[5];
        var persistedUser = users[6];
        var saltUser = users[7];
        var limitedUser = users[8];
        var peers = new List<Peer>();
        try
        {
            CreateRoomRequest PrivateRoom(string password) => new()
            {
                Password = password,
                Settings = new ServerRoomSettings
                {
                    RoomName = "비밀번호 검증", IsPrivate = true, Topics = new[] { "과일" }, RoundCount = 1,
                    RoleSeconds = 3, DrawSeconds = 30, DiscussionSeconds = 5, RebuttalSeconds = 5,
                    VoteSeconds = 5, RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5
                }
            };
            await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms", PrivateRoom(""), hostUser.Session.SessionToken,
                HttpStatusCode.Forbidden, "RoomPasswordRequired");
            foreach (string invalid in new[] { "abc", new string('a', 33), "Bad\nPassword" })
                await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms", PrivateRoom(invalid), hostUser.Session.SessionToken,
                    HttpStatusCode.BadRequest, "InvalidRoomPasswordFormat");
            var room = await PostAsync<DedicatedAssignment>(game, "/api/rooms", PrivateRoom(INITIAL_PASSWORD), hostUser.Session.SessionToken);
            Check(new Uri(room.DedicatedUrl) == new Uri("ws://127.0.0.1:25570/play"), "방 비밀번호 검증은 전용 로컬 데디케이티드 25570만 사용합니다.");
            var host = await Peer.ConnectAsync(room, ValidateCertificate);
            peers.Add(host);
            await host.WaitStateAsync(state => state.IsHost);
            await WaitSocialRosterAsync(owner, room.RoomId, hostUser.Login.AccountId, true);
            string initialHash = await RoomPasswordHashAsync(owner, room.RoomId);
            Check(initialHash.Length > 32 && initialHash != INITIAL_PASSWORD && !initialHash.Contains(INITIAL_PASSWORD, StringComparison.Ordinal),
                "비공개 방 비밀번호를 원문으로 저장하면 안 됩니다.");
            var salted = await PostAsync<DedicatedAssignment>(game, "/api/rooms", PrivateRoom(INITIAL_PASSWORD), saltUser.Session.SessionToken);
            Check(await RoomPasswordHashAsync(owner, salted.RoomId) != initialHash,
                "같은 비밀번호의 서로 다른 방은 독립적인 salt를 사용해야 합니다.");
            var shortPassword = await PostAsync<DedicatedAssignment>(game, "/api/rooms", PrivateRoom("Z7q!"), limitedUser.Session.SessionToken);
            Check((await RoomPasswordHashAsync(owner, shortPassword.RoomId)).Length > 32,
                "허용 범위의 최소 네 글자 비밀번호로 비공개 방을 생성할 수 있어야 합니다.");
            await AssertRoomPasswordMetadataAsync(game, owner, hostUser, room.RoomId, host.Latest!, INITIAL_PASSWORD, initialHash);
            Check(!(await GetAsync<RoomListResponse>(game, "/api/rooms", outsider.Session.SessionToken)).Rooms.Any(value => value.RoomId == room.RoomId),
                "비공개 방은 공개 검색 목록에 노출되면 안 됩니다.");
            Report("비공개 생성 비밀번호 필수·길이/제어문자 검증·salt 해시 저장·방장 최초 입장·비밀정보 비노출 검증");
            await VerifyLegacyPrivateRoomAsync(game, owner, salted, saltUser, outsider, INITIAL_PASSWORD, peers);
            if (legacyOnly)
            {
                Console.WriteLine("DrawLiar 이전 비공개 방 비밀번호 복구 HTTP/PostgreSQL/WebSocket 검증 PASS");
                return;
            }

            foreach (string identifier in new[] { room.RoomCode, room.RoomId })
            {
                foreach (bool spectator in new[] { false, true })
                {
                    await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + identifier + "/join",
                        new JoinRoomRequest { AsSpectator = spectator }, memberUser.Session.SessionToken,
                        HttpStatusCode.Forbidden, "RoomPasswordRequired");
                    await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + identifier + "/join",
                        new JoinRoomRequest { AsSpectator = spectator, Password = INITIAL_PASSWORD.ToLowerInvariant() }, memberUser.Session.SessionToken,
                        HttpStatusCode.Forbidden, "InvalidRoomPassword");
                }
            }
            Check(await RoomTicketCountAsync(owner, room.RoomId, memberUser.Login.AccountId) == 0,
                "비밀번호 누락·오답은 참가·관전 입장권을 발급하면 안 됩니다.");
            var memberAssignment = await JoinAsync(game, room.RoomCode, memberUser, false, INITIAL_PASSWORD);
            var member = await Peer.ConnectAsync(memberAssignment, ValidateCertificate);
            peers.Add(member);
            await member.WaitStateAsync(state => !state.LocalIsSpectator);
            await WaitSocialRosterAsync(owner, room.RoomId, memberUser.Login.AccountId, true);
            var observerAssignment = await JoinAsync(game, room.RoomId, observerUser, true, INITIAL_PASSWORD);
            var observer = await Peer.ConnectAsync(observerAssignment, ValidateCertificate);
            peers.Add(observer);
            await observer.WaitStateAsync(state => state.LocalIsSpectator);
            await SocialMakeFriendsAsync(game, hostUser, invitedUser);
            var invitation = await PostAsync<RoomInvitationData>(game, "/api/rooms/" + room.RoomCode + "/invite",
                new FriendRequest { AccountId = invitedUser.Login.AccountId }, hostUser.Session.SessionToken);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(invitation), new RoomInvitationRespondRequest { Accept = true },
                invitedUser.Session.SessionToken, HttpStatusCode.Forbidden, "RoomPasswordRequired");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(invitation), new RoomInvitationRespondRequest { Accept = true, Password = "wrong-password" },
                invitedUser.Session.SessionToken, HttpStatusCode.Forbidden, "InvalidRoomPassword");
            Check((await SocialInboxAsync(game, invitedUser)).RoomInvitations.Single().InvitationId == invitation.InvitationId
                && await RoomTicketCountAsync(owner, room.RoomId, invitedUser.Login.AccountId) == 0,
                "비밀번호 오류로 초대를 소비하거나 입장권을 발급하면 안 됩니다.");
            var invitedAssignment = await PostAsync<DedicatedAssignment>(game, SocialRespondPath(invitation),
                new RoomInvitationRespondRequest { Accept = true, Password = INITIAL_PASSWORD }, invitedUser.Session.SessionToken);
            var invited = await Peer.ConnectAsync(invitedAssignment, ValidateCertificate);
            peers.Add(invited);
            await invited.WaitStateAsync(state => !state.LocalIsSpectator);
            await AssertTicketRejectedAsync(invitedAssignment);
            using (var restartedDatabase = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DrawLiarDatabase"] = settings.ConnectionString }).Build()))
            {
                await restartedDatabase.InitializeAsync();
                var session = await restartedDatabase.AuthenticateAsync(persistedUser.Session.SessionToken, "game:game-social-qa");
                var persisted = await restartedDatabase.JoinRoomAsync(session, room.RoomCode, false, INITIAL_PASSWORD);
                Check(persisted.RoomId == room.RoomId && persisted.JoinTicket.Length == 64,
                    "새 데이터 계층 인스턴스도 저장된 방 비밀번호로 입장을 검증해야 합니다.");
                await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + room.RoomCode + "/join",
                    new JoinRoomRequest(), persistedUser.Session.SessionToken, HttpStatusCode.Forbidden, "RoomPasswordRequired");
                await restartedDatabase.RedeemTicketAsync(new RedeemTicketRequest
                { JoinTicket = persisted.JoinTicket, RoomId = room.RoomId, NodeId = "dedicated-social-qa" });
                await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + room.RoomCode + "/join",
                    new JoinRoomRequest(), persistedUser.Session.SessionToken, HttpStatusCode.Forbidden, "RoomPasswordRequired");
            }
            for (int index = 0; index < 15; index++)
                await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + room.RoomCode + "/join",
                    new JoinRoomRequest { Password = "wrong-password" }, limitedUser.Session.SessionToken,
                    HttpStatusCode.Forbidden, "InvalidRoomPassword");
            using (var limited = await SendAsync(game, "/api/rooms/" + room.RoomCode + "/join",
                new JoinRoomRequest { Password = INITIAL_PASSWORD }, limitedUser.Session.SessionToken))
                Check((int)limited.StatusCode == 429, "반복 비밀번호 추측은 기존 참가 요청 속도 제한을 적용해야 합니다.");
            Check(await RoomTicketCountAsync(owner, room.RoomId, limitedUser.Login.AccountId) == 0,
                "속도 제한된 비밀번호 요청은 입장권을 만들면 안 됩니다.");
            Report("코드/UUID·참가/관전 비밀번호 검증·초대 실패 보존·정상 초대 일회용 입장·새 DAL 재검증·추측 속도 제한 검증");

            string originalName = host.Latest!.Settings.RoomName;
            var unauthorized = host.Latest.Settings.Copy(); unauthorized.RoomName = "비방장 변경";
            await ConfigurePasswordRoomAsync(member, unauthorized, CHANGED_PASSWORD, false);
            await ConfigurePasswordRoomAsync(observer, unauthorized, CHANGED_PASSWORD, false);
            Check(host.Latest.Settings.RoomName == originalName && await RoomPasswordHashAsync(owner, room.RoomId) == initialHash,
                "비방장·관전자의 변경은 비밀번호와 옵션 모두에 영향을 주면 안 됩니다.");
            var keep = host.Latest.Settings.Copy(); keep.RoomName = "기존 비밀번호 유지";
            await ConfigurePasswordRoomAsync(host, keep, "", true);
            await WaitSocialDatabaseAsync(owner, "SELECT \"Settings\"->>'RoomName' FROM \"Room\" WHERE \"RoomId\"=$1",
                value => value as string == keep.RoomName, Guid.Parse(room.RoomId));
            Check(await RoomPasswordHashAsync(owner, room.RoomId) == initialHash, "비공개 방의 빈 비밀번호 변경은 기존 해시를 유지해야 합니다.");
            var changed = keep.Copy(); changed.RoomName = "비밀번호 교체";
            await ConfigurePasswordRoomAsync(host, changed, CHANGED_PASSWORD, true);
            string changedHash = await RoomPasswordHashAsync(owner, room.RoomId);
            Check(changedHash.Length > 32 && changedHash != initialHash, "새 비밀번호는 저장된 해시를 교체해야 합니다.");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + room.RoomCode + "/join",
                new JoinRoomRequest { Password = INITIAL_PASSWORD }, outsider.Session.SessionToken,
                HttpStatusCode.Forbidden, "InvalidRoomPassword");
            var changedAssignment = await JoinAsync(game, room.RoomCode, outsider, false, CHANGED_PASSWORD);
            Check(changedAssignment.RoomId == room.RoomId, "교체한 비밀번호로 입장권을 받을 수 있어야 합니다.");
            var publiclyOpen = changed.Copy(); publiclyOpen.RoomName = "공개 전환"; publiclyOpen.IsPrivate = false;
            await ConfigurePasswordRoomAsync(host, publiclyOpen, "", true);
            Check(await RoomPasswordHashAsync(owner, room.RoomId) == "", "공개 전환은 비밀번호 해시를 지워야 합니다.");
            Check((await GetAsync<RoomListResponse>(game, "/api/rooms", publicUser.Session.SessionToken)).Rooms.Any(value => value.RoomId == room.RoomId),
                "공개 전환한 방을 공개 검색에 표시해야 합니다.");
            var publiclyJoined = await JoinAsync(game, room.RoomCode, publicUser, false);
            Check(publiclyJoined.RoomId == room.RoomId, "공개 방은 비밀번호 없이 입장할 수 있어야 합니다.");
            var reclosed = publiclyOpen.Copy(); reclosed.RoomName = "다시 비공개"; reclosed.IsPrivate = true;
            await ConfigurePasswordRoomAsync(host, reclosed, "", false);
            Check(host.Latest!.Settings.RoomName == publiclyOpen.RoomName && !host.Latest.Settings.IsPrivate
                && await RoomPasswordHashAsync(owner, room.RoomId) == "", "비밀번호 없는 비공개 전환은 옵션도 함께 거절해야 합니다.");
            await ConfigurePasswordRoomAsync(host, reclosed, FINAL_PASSWORD, true);
            string finalHash = await RoomPasswordHashAsync(owner, room.RoomId);
            Check(finalHash.Length > 32 && finalHash != changedHash, "공개에서 비공개로 전환하면 새 비밀번호를 저장해야 합니다.");
            await AssertTicketRejectedAsync(changedAssignment);
            await AssertTicketRejectedAsync(publiclyJoined);
            await AssertRoomPasswordMetadataAsync(game, owner, hostUser, room.RoomId, host.Latest!, INITIAL_PASSWORD,
                CHANGED_PASSWORD, FINAL_PASSWORD, initialHash, changedHash, finalHash);
            Report("비방장·관전자 변경 거부·기존 비밀번호 유지·교체·공개 전환 해시 삭제·비공개 전환 필수·이전 입장권 폐기·서버 저장 ACK와 비노출 검증");

            await host.WaitStateAsync(state => state.Players.Count(player => player.IsConnected && !player.IsSpectator) == 3);
            await host.SendAsync(new GameplayEnvelope { Type = "request", Kind = "start" });
            var role = await host.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal);
            var inProgress = role.Settings.Copy(); inProgress.RoomName = "진행 중 변경";
            await ConfigurePasswordRoomAsync(host, inProgress, CHANGED_PASSWORD, false);
            Check(await RoomPasswordHashAsync(owner, room.RoomId) == finalHash, "진행 중에는 현재 방장도 비밀번호를 바꿀 수 없습니다.");
            await host.DisposeAsync();
            await member.WaitStateAsync(state => state.IsHost);
            await WaitSocialDatabaseAsync(owner, "SELECT \"OwnerAccountId\"::text FROM \"Room\" WHERE \"RoomId\"=$1",
                value => value as string == memberUser.Login.AccountId, Guid.Parse(room.RoomId));
            var reconnectAssignment = await JoinAsync(game, room.RoomCode, hostUser, false);
            var reconnected = await Peer.ConnectAsync(reconnectAssignment, ValidateCertificate);
            peers.Add(reconnected);
            var restored = await reconnected.WaitStateAsync(state => !state.LocalIsSpectator);
            Check(restored.LocalPlayerId == role.LocalPlayerId && restored.LocalIsLiar == role.LocalIsLiar && !restored.IsHost,
                "기존 경기 참가자의 비밀번호 없는 재접속은 ID·역할을 유지하고 이전 방장 권한을 되찾으면 안 됩니다.");
            await ConfigurePasswordRoomAsync(reconnected, inProgress, CHANGED_PASSWORD, false);
            await ConfigurePasswordRoomAsync(member, inProgress, CHANGED_PASSWORD, false);
            Check(await RoomPasswordHashAsync(owner, room.RoomId) == finalHash, "이전 방장과 진행 중인 새 방장의 변경은 저장하면 안 됩니다.");
            var active = new[] { reconnected, member, invited };
            for (int turn = 0; turn < 3; turn++)
            {
                var drawing = await member.WaitStateAsync(state => state.Phase == GamePhase.Drawing);
                var artist = active.Single(peer => peer.Latest!.LocalPlayerId == drawing.ArtistId);
                await artist.SendAsync(new GameplayEnvelope { Type = "request", Kind = "endTurn" });
                await WaitAsync(() => member.Latest!.Phase != GamePhase.Drawing || member.Latest.ArtistId != drawing.ArtistId, 5);
            }
            await WaitAsync(() => member.Latest?.Phase == GamePhase.MatchResults, 30);
            var afterMatch = member.Latest!.Settings.Copy(); afterMatch.RoomName = "새 방장 변경";
            await ConfigurePasswordRoomAsync(reconnected, afterMatch, CHANGED_PASSWORD, false);
            Check(await RoomPasswordHashAsync(owner, room.RoomId) == finalHash, "경기 종료 후에도 이전 방장은 비밀번호를 바꿀 수 없습니다.");
            await ConfigurePasswordRoomAsync(member, afterMatch, CHANGED_PASSWORD, true);
            Check(await RoomPasswordHashAsync(owner, room.RoomId) != finalHash, "권한을 이전받은 새 방장은 경기 종료 후 비밀번호를 변경할 수 있어야 합니다.");
            Report("진행 중 변경 거부·기존 참가자 비밀번호 없는 역할 재접속·방장 이전·종료 후 이전/현재 방장 권한 검증");
            await VerifyConfigurationRecoveryAsync(main, game, settings.ConnectionString, users);
            Console.WriteLine("DrawLiar 비공개 방 비밀번호 HTTP/PostgreSQL/WebSocket 검증 PASS");
        }
        finally
        {
            foreach (var peer in peers)
            {
                if (!peer.IsClosed)
                {
                    try { await peer.LeaveAsync(); }
                    catch (Exception exception) when (exception is OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
                }
                await peer.DisposeAsync();
            }
        }
    }

    private static async Task<string> RoomPasswordHashAsync(NpgsqlConnection owner, string roomId)
    {
        await using var command = new NpgsqlCommand("SELECT \"PasswordHash\" FROM \"Room\" WHERE \"RoomId\"=$1", owner);
        command.Parameters.AddWithValue(Guid.Parse(roomId));
        return await command.ExecuteScalarAsync() as string ?? "";
    }

    private static async Task VerifyLegacyPrivateRoomAsync(HttpClient game, NpgsqlConnection owner, DedicatedAssignment assignment,
        TestUser hostUser, TestUser receiver, string password, List<Peer> peers)
    {
        await using (var seed = new NpgsqlCommand("UPDATE \"Room\" SET \"PasswordHash\"=NULL WHERE \"RoomId\"=$1", owner))
        {
            seed.Parameters.AddWithValue(Guid.Parse(assignment.RoomId));
            Check(await seed.ExecuteNonQueryAsync() == 1, "기존 비공개 방의 해시 없는 상태를 격리 DB에 준비해야 합니다.");
        }
        var host = await Peer.ConnectAsync(assignment, ValidateCertificate);
        peers.Add(host);
        await host.WaitStateAsync(state => state.IsHost);
        await WaitSocialDatabaseAsync(owner, "SELECT \"ConfirmedPlayerAccountIds\" ? $2::text FROM \"Room\" WHERE \"RoomId\"=$1",
            value => value is true, Guid.Parse(assignment.RoomId), hostUser.Login.AccountId);
        foreach (string identifier in new[] { assignment.RoomCode, assignment.RoomId })
        {
            foreach (string supplied in new[] { "", password })
                await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/rooms/" + identifier + "/join",
                    new JoinRoomRequest { Password = supplied }, receiver.Session.SessionToken,
                    HttpStatusCode.Conflict, "RoomPasswordNotConfigured");
        }
        await SocialMakeFriendsAsync(game, hostUser, receiver);
        var invitation = await PostAsync<RoomInvitationData>(game, "/api/rooms/" + assignment.RoomCode + "/invite",
            new FriendRequest { AccountId = receiver.Login.AccountId }, hostUser.Session.SessionToken);
        foreach (string supplied in new[] { "", password })
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(invitation),
                new RoomInvitationRespondRequest { Accept = true, Password = supplied }, receiver.Session.SessionToken,
                HttpStatusCode.Conflict, "RoomPasswordNotConfigured");
        Check(await RoomTicketCountAsync(owner, assignment.RoomId, receiver.Login.AccountId) == 0
            && (await SocialInboxAsync(game, receiver)).RoomInvitations.Single().InvitationId == invitation.InvitationId,
            "비밀번호 미설정 거부는 입장권을 발급하거나 초대를 소비하면 안 됩니다.");
        var reconnect = await JoinAsync(game, assignment.RoomCode, hostUser, false);
        Check(reconnect.RoomId == assignment.RoomId && reconnect.JoinTicket.Length == 64,
            "비밀번호가 없는 이전 방도 이미 참가가 확정된 계정은 재접속할 수 있어야 합니다.");
        var configured = host.Latest!.Settings.Copy(); configured.RoomName = "이전 비공개 방 복구";
        await ConfigurePasswordRoomAsync(host, configured, password, true);
        Check((await RoomPasswordHashAsync(owner, assignment.RoomId)).Length > 32,
            "이전 비공개 방의 방장은 비밀번호 해시를 새로 저장할 수 있어야 합니다.");
        var joined = await JoinAsync(game, assignment.RoomCode, receiver, false, password);
        var joinedById = await JoinAsync(game, assignment.RoomId, receiver, false, password);
        Check(joined.RoomId == assignment.RoomId && joinedById.RoomId == assignment.RoomId,
            "비밀번호를 설정한 기존 방에는 코드와 UUID로 정상 입장권을 발급해야 합니다.");
        var accepted = await PostAsync<DedicatedAssignment>(game, SocialRespondPath(invitation),
            new RoomInvitationRespondRequest { Accept = true, Password = password }, receiver.Session.SessionToken);
        var guest = await Peer.ConnectAsync(accepted, ValidateCertificate);
        peers.Add(guest);
        await guest.WaitStateAsync(state => !state.LocalIsSpectator);
        Check((await SocialInboxAsync(game, receiver)).RoomInvitations.Length == 0,
            "방장이 비밀번호를 설정한 뒤 기존 초대로 실제 데디케이티드에 참가하고 알림을 제거해야 합니다.");
        Report("이전 비공개 방의 해시 없음 코드/UUID/초대 409·입장권 미발급·초대 보존·기존 참가자 재접속·방장 비밀번호 설정 후 참가 복구 검증");
    }

    private static async Task VerifyConfigurationRecoveryAsync(HttpClient main, HttpClient game, string connectionString, List<TestUser> users)
    {
        var owner = await GuestAndEnterAsync(main);
        var assignment = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
        { Password = "RecoveryQA92", Settings = new ServerRoomSettings { IsPrivate = true, Topics = new[] { "과일" } } }, owner.Session.SessionToken);
        using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DrawLiarDatabase"] = connectionString }).Build());
        var roomData = (await database.RoomsAsync(null, true)).Rooms.Single(room => room.RoomId == assignment.RoomId);
        var room = new DedicatedRoom(roomData,
            new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } },
            Array.Empty<ServerTopicData>(), 0, () => { });
        var socket = new PolicySocket();
        await using var host = new GameConnection(socket, owner.Login.AccountId, "test", CancellationToken.None);
        await using var second = new GameConnection(new PolicySocket(), users[4].Login.AccountId, "test", CancellationToken.None);
        await using var third = new GameConnection(new PolicySocket(), users[6].Login.AccountId, "test", CancellationToken.None);
        RedeemTicketResponse Ticket(TestUser user) => new()
        { AccountId = user.Login.AccountId, Room = roomData, Profile = user.Login.Profile };
        Check(room.Join(host, Ticket(owner), 0) && room.Join(second, Ticket(users[4]), 0) && room.Join(third, Ticket(users[6]), 0),
            "설정 복구 검증의 참가자를 준비해야 합니다.");
        await WaitAsync(() => socket.LastState?.CanStart == true, 3);
        foreach (Exception failure in new Exception[] { new HttpRequestException("QA"), new ApiException("ServerUnavailable", 500) })
        {
            var requested = socket.LastState!.Settings.Copy(); requested.RoomName = "저장하지 못한 변경";
            await room.ConfigureAsync(host, new GameplayEnvelope
            { Type = "request", Kind = "configure", Settings = requested, RequestId = Guid.NewGuid().ToString("N") }, 1,
                (_, cancellation) => Task.FromException<RoomConfigurationData>(failure));
            await WaitAsync(() => socket.LastState?.CanStart == false, 3);
            var status = room.Status();
            Check(status.ConfigurationVersion == -1 && status.Settings.RoomName != requested.RoomName,
                "저장 실패는 상태를 바꾸지 않고 heartbeat에 설정 조정 필요 상태를 전달해야 합니다.");
            var heartbeat = await database.DedicatedHeartbeatAsync(new DedicatedHeartbeatRequest
            { NodeId = roomData.NodeId, Rooms = new[] { status } });
            var configuration = heartbeat.Configurations.Single(value => value.RoomId == roomData.RoomId);
            Check(configuration.Version == roomData.ConfigurationVersion,
                "DB 버전이 바뀌지 않은 실패도 최신 설정을 다시 전달해야 합니다.");
            room.ApplyConfiguration(configuration);
            await WaitAsync(() => socket.LastState?.CanStart == true, 3);
            Check(room.Status().ConfigurationVersion == configuration.Version,
                "설정 조정 완료 후 같은 방에서 시작·옵션 변경을 다시 허용해야 합니다.");
        }
        var saved = socket.LastState!.Settings.Copy(); saved.RoomName = "복구 후 변경";
        await room.ConfigureAsync(host, new GameplayEnvelope
        { Type = "request", Kind = "configure", Settings = saved, RequestId = Guid.NewGuid().ToString("N") }, 2,
            async (request, cancellation) =>
            {
                request.NodeId = roomData.NodeId;
                return await database.ConfigureRoomAsync(request, cancellation);
            });
        await WaitAsync(() => socket.LastState?.Settings.RoomName == saved.RoomName && socket.LastState.CanStart, 3);
        Check((await database.RoomsAsync(null, true)).Rooms.Single(value => value.RoomId == roomData.RoomId).Name == saved.RoomName,
            "실패 복구 후 실제 GS에 옵션을 다시 저장할 수 있어야 합니다.");
        Report("설정 저장 HTTP 실패/500 주입·동일 DB 버전 heartbeat 조정·시작 권한 회복·실제 재저장 검증");
    }

    private static async Task<long> RoomTicketCountAsync(NpgsqlConnection owner, string roomId, string accountId)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"JoinTicket\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", owner);
        command.Parameters.AddWithValue(Guid.Parse(roomId)); command.Parameters.AddWithValue(Guid.Parse(accountId));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ConfigurePasswordRoomAsync(Peer peer, RoomSettings settings, string password, bool accept)
    {
        string requestId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new GameplayEnvelope { Type = "request", Kind = "configure", Settings = settings, Password = password, RequestId = requestId });
        await WaitAsync(() => peer.Trace.Any(message => message.Type == "configured" && message.RequestId == requestId), 10);
        var acknowledged = peer.Trace.Single(message => message.Type == "configured" && message.RequestId == requestId);
        Check(acknowledged.Accepted == accept, "방 설정 변경 ACK는 서버 저장 결과를 반영해야 합니다: " + acknowledged.Code);
        Check(acknowledged.Password == null, "방 설정 ACK에 입력한 비밀번호를 되돌려 보내면 안 됩니다.");
        if (accept) await peer.WaitStateAsync(state => state.Settings.RoomName == settings.RoomName && state.Settings.IsPrivate == settings.IsPrivate);
    }

    private static async Task AssertRoomPasswordMetadataAsync(HttpClient game, NpgsqlConnection owner, TestUser user, string roomId,
        RoomSnapshot snapshot, params string[] secrets)
    {
        var inbox = await SocialInboxAsync(game, user);
        var list = await GetAsync<RoomListResponse>(game, "/api/rooms", user.Session.SessionToken);
        await using var command = new NpgsqlCommand("SELECT \"Settings\"::text FROM \"Room\" WHERE \"RoomId\"=$1", owner);
        command.Parameters.AddWithValue(Guid.Parse(roomId));
        string persisted = await command.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("저장된 방 옵션이 없습니다.");
        foreach (string json in new[] { JsonSerializer.Serialize(snapshot, Json), JsonSerializer.Serialize(inbox, Json), JsonSerializer.Serialize(list, Json), persisted })
        {
            Check(!json.Contains("\"Password\"", StringComparison.Ordinal) && !json.Contains("PasswordHash", StringComparison.Ordinal),
                "방 snapshot·목록·알림·저장 옵션에 비밀번호 필드를 포함하면 안 됩니다.");
            foreach (string secret in secrets)
                Check(!json.Contains(secret, StringComparison.Ordinal), "방 snapshot·목록·알림·저장 옵션에 비밀번호나 해시를 노출하면 안 됩니다.");
        }
    }
}
