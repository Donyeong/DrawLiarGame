using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifySocialAsync(string mainUrl)
    {
        const string ROOM_PASSWORD = "SocialQA4821";
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "소셜 검증은 전용 로컬 메인서버 25550만 사용합니다.");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_SOCIAL_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_SOCIAL_TEST_DATABASE가 필요합니다."));
        Check(settings.Host == "127.0.0.1" && settings.Port == 25539 && settings.Database == "postgres"
            && Regex.IsMatch(settings.SearchPath ?? "", "^drawliar_social_test_[0-9a-f]{32}$"),
            "소셜 검증은 무작위 스키마를 사용하는 전용 로컬 PostgreSQL 25539만 사용합니다.");
        await using var owner = new NpgsqlConnection(settings.ConnectionString);
        await owner.OpenAsync();
        using var main = Client(mainUrl);
        await RequireHealthAsync(main);
        var users = new List<TestUser>();
        for (int index = 0; index < 14; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "소셜 검증 게임서버는 전용 로컬 25560이어야 합니다.");
        using var game = Client(users[0].Login.GameServerUrl);
        await RequireHealthAsync(game);
        var peers = new List<Peer>();
        var hostUser = users[0];
        var receiver = users[1];
        var outsider = users[2];
        var memberUser = users[3];
        var lateUser = users[4];
        var observerUser = users[5];
        var expiryUser = users[6];
        var removedFriend = users[7];
        var requestOnly = users[8];
        string token = hostUser.Session.SessionToken;
        try
        {
            await ExpectSocialErrorAsync(game, HttpMethod.Get, "/api/social/inbox", null, null, HttpStatusCode.Unauthorized, "Unauthorized");
            await ExpectSocialErrorAsync(game, HttpMethod.Get, "/api/social/inbox", null, hostUser.Login.SessionToken,
                HttpStatusCode.Unauthorized, "Unauthorized");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = receiver.Login.AccountId }, token);
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = receiver.Login.AccountId }, token);
            var receiverInbox = await SocialInboxAsync(game, receiver);
            Check(receiverInbox.Friends.Incoming.Length == 1 && receiverInbox.Friends.Incoming[0].AccountId == hostUser.Login.AccountId,
                "재시도한 친구 신청은 받는 계정에 한 건만 표시해야 합니다.");
            Check((await SocialInboxAsync(game, hostUser)).Friends.Incoming.Length == 0
                && (await SocialInboxAsync(game, hostUser)).Friends.Outgoing.Single().AccountId == receiver.Login.AccountId
                && (await SocialInboxAsync(game, outsider)).Friends.Incoming.Length == 0,
                "친구 신청은 타 계정의 받은 알림에 노출하면 안 됩니다.");
            await PostNoContentAsync(game, "/api/friends/respond", new FriendRespondRequest
            { AccountId = hostUser.Login.AccountId, Accept = true }, receiver.Session.SessionToken);
            Check((await SocialInboxAsync(game, receiver)).Friends.Incoming.Length == 0
                && (await SocialInboxAsync(game, receiver)).Friends.Friends.Single().AccountId == hostUser.Login.AccountId
                && (await SocialInboxAsync(game, hostUser)).Friends.Friends.Single().AccountId == receiver.Login.AccountId,
                "친구 신청 수락 후 알림을 제거하고 양쪽 친구 목록을 갱신해야 합니다.");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = requestOnly.Login.AccountId }, token);
            await PostNoContentAsync(game, "/api/friends/respond", new FriendRespondRequest
            { AccountId = hostUser.Login.AccountId, Accept = false }, requestOnly.Session.SessionToken);
            Check((await SocialInboxAsync(game, requestOnly)).Friends.Incoming.Length == 0
                && !(await SocialInboxAsync(game, hostUser)).Friends.Outgoing.Any(friend => friend.AccountId == requestOnly.Login.AccountId),
                "친구 신청 거절 후 다시 조회해도 알림을 되살리면 안 됩니다.");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = requestOnly.Login.AccountId }, token);
            foreach (var friend in new[] { memberUser, lateUser, observerUser, expiryUser, removedFriend })
                await SocialMakeFriendsAsync(game, hostUser, friend);
            await SocialMakeFriendsAsync(game, memberUser, receiver);
            await SocialMakeFriendsAsync(game, memberUser, outsider);
            await SocialMakeFriendsAsync(game, outsider, receiver);
            Report("친구 신청 수신자 격리·중복·수락·거절·세션 인증 검증");

            var room = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Password = ROOM_PASSWORD,
                Settings = new ServerRoomSettings
                {
                    RoomName = "소셜 검증", IsPrivate = true, Topics = new[] { "과일" },
                    RoleSeconds = 15, DrawSeconds = 180, RoundCount = 1
                }
            }, token);
            Check(new Uri(room.DedicatedUrl) == new Uri("ws://127.0.0.1:25570/play"), "소셜 검증은 전용 로컬 데디케이티드 25570만 사용합니다.");
            string invitePath = "/api/rooms/" + room.RoomCode + "/invite";
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = receiver.Login.AccountId },
                token, HttpStatusCode.NotFound, "RoomUnavailable");
            var host = await Peer.ConnectAsync(room, ValidateCertificate);
            peers.Add(host);
            await host.WaitStateAsync(state => state.IsHost);
            await WaitSocialRosterAsync(owner, room.RoomId, hostUser.Login.AccountId, true);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = receiver.Login.AccountId },
                outsider.Session.SessionToken, HttpStatusCode.Forbidden, "NotRoomMember");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = requestOnly.Login.AccountId },
                token, HttpStatusCode.Forbidden, "NotFriends");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = outsider.Login.AccountId },
                token, HttpStatusCode.Forbidden, "NotFriends");
            var memberAssignment = await JoinAsync(game, room.RoomCode, memberUser, false, ROOM_PASSWORD);
            var member = await Peer.ConnectAsync(memberAssignment, ValidateCertificate);
            peers.Add(member);
            await member.WaitStateAsync(state => !state.IsHost && !state.LocalIsSpectator);
            await WaitSocialRosterAsync(owner, room.RoomId, memberUser.Login.AccountId, true);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = memberUser.Login.AccountId },
                token, HttpStatusCode.Conflict, "AlreadyInRoom");

            var duplicates = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostAsync<RoomInvitationData>(game, invitePath,
                new FriendRequest { AccountId = receiver.Login.AccountId }, token)));
            var invitation = duplicates[0];
            Check(duplicates.All(value => value.InvitationId == invitation.InvitationId && value.ExpiresAt == invitation.ExpiresAt)
                && invitation.RoomId == room.RoomId && invitation.RoomCode == room.RoomCode && invitation.RoomName == "소셜 검증"
                && invitation.Sender.AccountId == hostUser.Login.AccountId
                && DateTimeOffset.Parse(invitation.ExpiresAt) > DateTimeOffset.UtcNow.AddMinutes(4)
                && DateTimeOffset.Parse(invitation.ExpiresAt) <= DateTimeOffset.UtcNow.AddMinutes(5),
                "동시 재초대는 같은 ID와 만료를 유지하며 받은 알림을 중복 생성하면 안 됩니다.");
            Check((await SocialInboxAsync(game, receiver)).RoomInvitations.Single().InvitationId == invitation.InvitationId
                && (await SocialInboxAsync(game, hostUser)).RoomInvitations.Length == 0
                && (await SocialInboxAsync(game, outsider)).RoomInvitations.Length == 0,
                "방 초대는 지정한 친구의 받은 알림에만 전달해야 합니다.");
            string respondPath = SocialRespondPath(invitation);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, respondPath, new RoomInvitationRespondRequest { Accept = true },
                outsider.Session.SessionToken, HttpStatusCode.NotFound, "RoomInvitationUnavailable");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, respondPath, new RoomInvitationRespondRequest { Accept = true },
                receiver.Login.SessionToken, HttpStatusCode.Unauthorized, "Unauthorized");
            var declined = await PostAsync<DedicatedAssignment>(game, respondPath, new RoomInvitationRespondRequest { Accept = false }, receiver.Session.SessionToken);
            Check(string.IsNullOrEmpty(declined.JoinTicket) && (await SocialInboxAsync(game, receiver)).RoomInvitations.Length == 0,
                "초대 거절은 입장권을 발급하지 않고 받은 알림을 제거해야 합니다.");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, invitePath, new FriendRequest { AccountId = receiver.Login.AccountId },
                token, HttpStatusCode.TooManyRequests, "RoomInvitationCooldown");
            await AgeSocialInvitationAsync(owner, invitation.InvitationId, false);
            invitation = await PostAsync<RoomInvitationData>(game, invitePath, new FriendRequest { AccountId = receiver.Login.AccountId }, token);
            var acceptResponses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(game, SocialRespondPath(invitation),
                new RoomInvitationRespondRequest { Accept = true, Password = ROOM_PASSWORD }, receiver.Session.SessionToken)));
            DedicatedAssignment accepted;
            try
            {
                Check(acceptResponses.Count(response => response.StatusCode == HttpStatusCode.OK) == 1
                    && acceptResponses.Count(response => response.StatusCode == HttpStatusCode.NotFound) == 1,
                    "같은 초대의 동시 수락은 한 번만 성공해야 합니다.");
                accepted = await acceptResponses.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<DedicatedAssignment>(Json)
                    ?? throw new InvalidOperationException("초대 입장권이 없습니다.");
            }
            finally { foreach (var response in acceptResponses) response.Dispose(); }
            Check(accepted.RoomId == room.RoomId && accepted.RoomCode == room.RoomCode && accepted.JoinTicket.Length == 64
                && (await SocialInboxAsync(game, receiver)).RoomInvitations.Length == 0,
                "수락한 초대는 기존 데디케이티드 입장권을 발급하고 받은 알림을 제거해야 합니다.");
            var admitted = await Peer.ConnectAsync(accepted, ValidateCertificate);
            peers.Add(admitted);
            await admitted.WaitStateAsync(state => !state.LocalIsSpectator);
            await AssertTicketRejectedAsync(accepted);
            await WaitSocialRosterAsync(owner, room.RoomId, receiver.Login.AccountId, true);
            Report("친구만·실제 방 참가자만 초대·동시 재시도 중복 억제·수신자 응답 권한·거절 쿨다운·동시 수락·일회용 입장권 검증");

            var fillers = new List<Peer>();
            foreach (var fillerUser in users.Skip(9))
            {
                var fillerAssignment = await JoinAsync(game, room.RoomCode, fillerUser, false, ROOM_PASSWORD);
                var filler = await Peer.ConnectAsync(fillerAssignment, ValidateCertificate);
                fillers.Add(filler); peers.Add(filler);
                await filler.WaitStateAsync(state => !state.LocalIsSpectator);
            }
            await host.WaitStateAsync(state => state.Players.Count(player => player.IsConnected && !player.IsSpectator) == 8);
            await WaitSocialDatabaseAsync(owner, "SELECT \"PlayerCount\" FROM \"Room\" WHERE \"RoomId\"=$1", value => value is 8, Guid.Parse(room.RoomId));
            var full = await PostAsync<RoomInvitationData>(game, invitePath, new FriendRequest { AccountId = lateUser.Login.AccountId }, token);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(full), new RoomInvitationRespondRequest { Accept = true, Password = ROOM_PASSWORD },
                lateUser.Session.SessionToken, HttpStatusCode.Conflict, "RoomFull");
            Check((await SocialInboxAsync(game, lateUser)).RoomInvitations.Single().InvitationId == full.InvitationId,
                "방이 가득 차면 초대를 소비하지 않고 빈자리 이후 다시 수락할 수 있어야 합니다.");
            var nonHostInvitation = await PostAsync<RoomInvitationData>(game, "/api/rooms/" + room.RoomId + "/invite",
                new FriendRequest { AccountId = outsider.Login.AccountId }, memberUser.Session.SessionToken);
            Check(nonHostInvitation.Sender.AccountId == memberUser.Login.AccountId,
                "방장은 물론 실제 일반 참가자도 자신의 친구를 초대할 수 있어야 합니다.");
            await member.LeaveAsync();
            await WaitSocialRosterAsync(owner, room.RoomId, memberUser.Login.AccountId, false);
            var lateAssignment = await PostAsync<DedicatedAssignment>(game, SocialRespondPath(full),
                new RoomInvitationRespondRequest { Accept = true, Password = ROOM_PASSWORD }, lateUser.Session.SessionToken);
            var late = await Peer.ConnectAsync(lateAssignment, ValidateCertificate);
            peers.Add(late);
            await late.WaitStateAsync(state => !state.LocalIsSpectator);
            foreach (var filler in fillers) await filler.LeaveAsync();
            await host.WaitStateAsync(state => state.Players.Count(player => player.IsConnected && !player.IsSpectator) == 3);
            await host.SendAsync(new GameplayEnvelope { Type = "request", Kind = "start" });
            await host.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal);
            await WaitSocialProgressAsync(owner, room.RoomId);
            var ongoing = await PostAsync<RoomInvitationData>(game, invitePath, new FriendRequest { AccountId = observerUser.Login.AccountId }, token);
            var observerAssignment = await PostAsync<DedicatedAssignment>(game, SocialRespondPath(ongoing),
                new RoomInvitationRespondRequest { Accept = true, Password = ROOM_PASSWORD }, observerUser.Session.SessionToken);
            var observer = await Peer.ConnectAsync(observerAssignment, ValidateCertificate);
            peers.Add(observer);
            var observerState = await observer.WaitStateAsync(state => state.LocalIsSpectator);
            Check(observerState.Word == "" && !observerState.LocalIsLiar,
                "진행 중 초대 수락은 관전으로 입장하며 비밀 제시어와 역할을 노출하면 안 됩니다.");
            Report("가득 찬 방의 초대 보존·빈자리 재수락·일반 참가자 초대·진행 중 관전 입장과 비밀정보 보호 검증");

            var expiring = await PostAsync<RoomInvitationData>(game, invitePath, new FriendRequest { AccountId = expiryUser.Login.AccountId }, token);
            await AgeSocialInvitationAsync(owner, expiring.InvitationId, true);
            Check((await SocialInboxAsync(game, expiryUser)).RoomInvitations.Length == 0, "만료된 초대는 받은 알림에서 제거해야 합니다.");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(expiring), new RoomInvitationRespondRequest { Accept = true },
                expiryUser.Session.SessionToken, HttpStatusCode.NotFound, "RoomInvitationUnavailable");
            var unfriended = await PostAsync<RoomInvitationData>(game, invitePath, new FriendRequest { AccountId = removedFriend.Login.AccountId }, token);
            using (var remove = new HttpRequestMessage(HttpMethod.Delete, "/api/friends/" + removedFriend.Login.AccountId))
            {
                remove.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await game.SendAsync(remove);
                response.EnsureSuccessStatusCode();
            }
            Check((await SocialInboxAsync(game, removedFriend)).RoomInvitations.Length == 0, "친구 삭제 후 기존 초대를 받은 알림에 남기면 안 됩니다.");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(unfriended), new RoomInvitationRespondRequest { Accept = true },
                removedFriend.Session.SessionToken, HttpStatusCode.Forbidden, "NotFriends");
            var nextRoom = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            { Password = ROOM_PASSWORD, Settings = new ServerRoomSettings { RoomName = "초대 종료 검증", IsPrivate = true, Topics = new[] { "과일" } } }, memberUser.Session.SessionToken);
            var nextHost = await Peer.ConnectAsync(nextRoom, ValidateCertificate);
            peers.Add(nextHost);
            await nextHost.WaitStateAsync(state => state.IsHost);
            await WaitSocialRosterAsync(owner, nextRoom.RoomId, memberUser.Login.AccountId, true);
            var closed = await PostAsync<RoomInvitationData>(game, "/api/rooms/" + nextRoom.RoomCode + "/invite",
                new FriendRequest { AccountId = receiver.Login.AccountId }, memberUser.Session.SessionToken);
            await nextHost.LeaveAsync();
            await WaitSocialRoomDeletedAsync(owner, nextRoom.RoomId);
            Check(!(await SocialInboxAsync(game, receiver)).RoomInvitations.Any(value => value.InvitationId == closed.InvitationId),
                "종료한 방의 초대는 알림에서 제거해야 합니다.");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, SocialRespondPath(closed), new RoomInvitationRespondRequest { Accept = true },
                receiver.Session.SessionToken, HttpStatusCode.NotFound, "RoomInvitationUnavailable");
            Check((await SocialInboxAsync(game, outsider)).RoomInvitations.Any(value => value.InvitationId == nonHostInvitation.InvitationId),
                "초대자가 퇴장해도 방과 친구 관계가 유지되면 이미 발송한 초대를 유지해야 합니다.");
            await PostAsync<DedicatedAssignment>(game, SocialRespondPath(nonHostInvitation), new RoomInvitationRespondRequest { Accept = false }, outsider.Session.SessionToken);
            Report("만료·친구 삭제·방 종료 초대 정리와 수락 거부·초대자만 퇴장한 활성 방의 초대 유지 검증");
            Console.WriteLine("DrawLiar 소셜 알림·방 초대 HTTP/PostgreSQL/WebSocket 검증 PASS");
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

    private static Task<SocialInboxResponse> SocialInboxAsync(HttpClient game, TestUser user) =>
        GetAsync<SocialInboxResponse>(game, "/api/social/inbox", user.Session.SessionToken);

    private static string SocialRespondPath(RoomInvitationData invitation) => "/api/room-invitations/" + invitation.InvitationId + "/respond";

    private static async Task SocialMakeFriendsAsync(HttpClient game, TestUser sender, TestUser receiver)
    {
        await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = receiver.Login.AccountId }, sender.Session.SessionToken);
        await PostNoContentAsync(game, "/api/friends/respond", new FriendRespondRequest { AccountId = sender.Login.AccountId, Accept = true }, receiver.Session.SessionToken);
    }

    private static async Task ExpectSocialErrorAsync(HttpClient game, HttpMethod method, string path, object? body, string? token,
        HttpStatusCode expected, string code)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await game.SendAsync(request);
        var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
        Check(response.StatusCode == expected && error?.Code == code,
            $"소셜 거부 응답은 {(int)expected}/{code}여야 합니다. 실제: {(int)response.StatusCode}/{error?.Code}");
    }

    private static async Task AgeSocialInvitationAsync(NpgsqlConnection owner, string invitationId, bool expire)
    {
        await using var command = new NpgsqlCommand(expire
            ? "UPDATE \"RoomInvitation\" SET \"CreatedAt\"=now()-interval '10 minutes',\"RespondedAt\"=NULL,\"ExpiresAt\"=now()-interval '1 minute' WHERE \"Id\"=$1"
            : "UPDATE \"RoomInvitation\" SET \"CreatedAt\"=now()-interval '1 minute',\"RespondedAt\"=now()-interval '1 minute' WHERE \"Id\"=$1", owner);
        command.Parameters.AddWithValue(Guid.Parse(invitationId));
        Check(await command.ExecuteNonQueryAsync() == 1, "시간 검증에 사용할 격리 초대가 있어야 합니다.");
    }

    private static Task WaitSocialRosterAsync(NpgsqlConnection owner, string roomId, string accountId, bool present) =>
        WaitSocialDatabaseAsync(owner, "SELECT COALESCE((\"PlayerAccountIds\" @> to_jsonb(ARRAY[$2::text]) OR \"SpectatorAccountIds\" @> to_jsonb(ARRAY[$2::text])),false) FROM \"Room\" WHERE \"RoomId\"=$1",
            value => value is bool flag && flag == present, Guid.Parse(roomId), accountId);

    private static Task WaitSocialProgressAsync(NpgsqlConnection owner, string roomId) =>
        WaitSocialDatabaseAsync(owner, "SELECT \"IsInProgress\" FROM \"Room\" WHERE \"RoomId\"=$1", value => value is true, Guid.Parse(roomId));

    private static Task WaitSocialRoomDeletedAsync(NpgsqlConnection owner, string roomId) =>
        WaitSocialDatabaseAsync(owner, "SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1", value => value == null, Guid.Parse(roomId));

    private static async Task WaitSocialDatabaseAsync(NpgsqlConnection owner, string sql, Func<object?, bool> predicate, params object[] values)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            await using var command = new NpgsqlCommand(sql, owner);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            if (predicate(await command.ExecuteScalarAsync(timeout.Token))) return;
            await Task.Delay(100, timeout.Token);
        }
    }
}
