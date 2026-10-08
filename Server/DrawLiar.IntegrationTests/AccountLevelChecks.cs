using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static void VerifyAccountLevelRules()
    {
        long threshold = 0;
        for (int level = 1; level <= 100; level++)
        {
            Check(AccountLevelRules.ExperienceForLevel(level) == threshold && AccountLevelRules.GetLevel(threshold) == level,
                "레벨 경계는 누적 경험치와 정확히 일치해야 합니다: " + level);
            if (level > 1) Check(AccountLevelRules.GetLevel(threshold - 1) == level - 1, "경계 직전 경험치는 이전 레벨이어야 합니다.");
            if (level < 100)
            {
                int required = 100 + (level - 1) * 50;
                Check(AccountLevelRules.RequiredExperienceForNextLevel(level) == required
                    && AccountLevelRules.ExperienceIntoLevel(threshold + required - 1) == required - 1
                    && AccountLevelRules.GetLevel(threshold + required - 1) == level, "다음 레벨 필요 경험치와 진행도를 보존해야 합니다.");
                threshold += required;
            }
        }
        Check(threshold == 252450 && AccountLevelRules.GetLevel(long.MaxValue) == 100
            && AccountLevelRules.RequiredExperienceForNextLevel(100) == 0 && AccountLevelRules.ExperienceIntoLevel(long.MaxValue) == 0
            && AccountLevelRules.GetLevel(-1) == 1 && AccountLevelRules.GetLevel(0) == 1,
            "최대 레벨100과 음수·기본 경험치 경계를 안전하게 처리해야 합니다.");
        int[] starts = { 1, 10, 25, 50, 75, 100 };
        int[] ends = { 9, 24, 49, 74, 99, 100 };
        for (int tier = 0; tier < starts.Length; tier++)
            Check(AccountLevelRules.GetBadgeTier(starts[tier]) == tier && AccountLevelRules.GetBadgeTier(ends[tier]) == tier,
                "레벨 뱃지6구간의 시작과 끝을 유지해야 합니다.");
        var profile = new ProfileData { Experience = long.MaxValue, Level = 100 };
        Check(JsonSerializer.Deserialize<ProfileData>(JsonSerializer.Serialize(profile, Json), Json)!.Experience == long.MaxValue,
            "경험치는 JSON 왕복에서도 Int64 정밀도를 유지해야 합니다.");
        var session = new GameSession(ProfileRoomSettings(1), ProfileGameData(), 91);
        Check(session.Join(1, "레벨화가", 0, 0, level: 75) && session.Join(2, "두번째", 0, 0)
            && session.Join(3, "세번째", 0, 0), "레벨 검증 참가자가 입장해야 합니다.");
        Check(session.PlayerLevel(1) == 75 && session.Snapshot(2, 1, 0).Players.Single(player => player.Id == 1).Level == 75,
            "Join의 인증된 레벨은 다른 참가자의 snapshot에도 전달해야 합니다.");
        session.UpdateProfile(1, "레벨화가", 0, 0, 100);
        Check(session.Start(0), "레벨 metadata는 경기 시작을 방해하면 안 됩니다.");
        session.Disconnect(1, 0.1);
        Check(session.Join(1, "레벨화가", 0, 0, level: 100) && session.PlayerLevel(1) == 100
            && session.Snapshot(2, 2, 0.2).Players.Single(player => player.Id == 1) is { Level: 100, Score: 0 },
            "프로필 갱신·재접속은 Lv100을 유지하고 경기 점수를 바꾸면 안 됩니다.");
        session.UpdateProfile(1, "레벨화가", 0, 0, int.MaxValue);
        session.UpdateProfile(2, "두번째", 0, 0, -1);
        Check(session.PlayerLevel(1) == 100 && session.PlayerLevel(2) == 1, "참가자 레벨은1~100 범위를 유지해야 합니다.");
        Report("레벨100개 전체 경계·진행도·6뱃지·Int64 JSON·Join/프로필/재접속 metadata 검증");
    }

    private static async Task VerifyAccountLevelHttpAsync(string mainUrl)
    {
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "레벨 검증은 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_REWARD_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_REWARD_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25539 && scoped.Database == "postgres"
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_reward_test_[0-9a-f]{32}$"), "레벨 검증은 전용 임시 PostgreSQL 스키마만 사용합니다.");
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
        using var main = Client(mainUrl);
        var users = new List<TestUser>();
        for (int index = 0; index < 3; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "레벨 검증은 전용 로컬 게임서버만 사용합니다.");
        using var game = Client(users[0].Login.GameServerUrl);
        string account = users[0].Login.AccountId, token = users[0].Session.SessionToken;
        var initial = await GetAsync<ProfileData>(game, "/api/profile", token);
        Check(initial.Experience == 0 && initial.Level == 1, "신규 계정은 XP0·Lv1로 시작해야 합니다.");
        var assignment = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest(), token);
        Check(assignment.DedicatedUrl == "ws://127.0.0.1:25570/play", "레벨 검증은 전용 로컬 데디케이티드만 사용합니다.");
        var peers = new List<Peer>();
        try
        {
            for (int index = 0; index < users.Count; index++)
            {
                var ticket = index == 0 ? assignment : await JoinAsync(game, assignment.RoomId, users[index], false);
                var peer = await Peer.ConnectAsync(ticket, ValidateCertificate); peers.Add(peer);
                await peer.WaitStateAsync(state => state.Players.Any(player => player.AccountId == users[index].Login.AccountId));
            }
            await peers[0].WaitStateAsync(state => state.Players.Length == 3);
            var payout = RewardFixture(users.Select(user => user.Login.AccountId).ToArray(), assignment.RoomId, 1, 3, new[] { 32, 0, 0 });
            await RewardClusterPostAsync(game, "/internal/dedicated/matches", payout);
            var receipt = await RewardGetAsync(game, payout.MatchId, token);
            Check(receipt.ExperienceReward == 102 && receipt.Profile.Experience == 102 && receipt.Profile.Level == 2,
                "실제 경기 보상은 코인과 함께 XP102를 지급하고 Lv2로 올려야 합니다.");
            var publicProfile = await GetAsync<JsonElement>(game, "/api/profiles/" + account, users[1].Session.SessionToken);
            Check(publicProfile.GetProperty("Level").GetInt32() == 2 && !publicProfile.TryGetProperty("Experience", out _),
                "공개 프로필은 현재 레벨을 전달하고 개인 XP를 공개하지 않아야 합니다.");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = users[1].Login.AccountId }, token);
            Check((await GetAsync<FriendListResponse>(game, "/api/friends", users[1].Session.SessionToken)).Incoming.Single().Level == 2,
                "받은 친구 신청의 레벨은 현재 계정 XP에서 계산해야 합니다.");
            await PostNoContentAsync(game, "/api/friends/respond", new FriendRespondRequest { AccountId = account, Accept = true }, users[1].Session.SessionToken);
            Check((await GetAsync<FriendListResponse>(game, "/api/friends", users[1].Session.SessionToken)).Friends.Single().Level == 2,
                "승인한 친구 목록에도 현재 레벨을 전달해야 합니다.");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = users[2].Login.AccountId }, token);
            Check((await GetAsync<FriendListResponse>(game, "/api/friends", token)).Outgoing.Single().Level == 1,
                "보낸 친구 신청은 상대 계정 레벨을 사용해야 합니다.");
            await VerifyAccountLevelRollbackAsync(database, owner, users, assignment.RoomId);
            await VerifyAccountLevelWorkshopAsync(game, users, owner);
            await VerifyAccountLevelInvitationAsync(main, game, users[0], owner, assignment.RoomId, assignment.RoomCode);
            await VerifyAccountLevelChatAsync(game, users, peers, owner, assignment.RoomId);
            Report("격리 PostgreSQL/HTTP/WS 경험치 지급·공개/친구/창작자/로비/인게임 레벨 전파 검증");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
    }

    private static async Task VerifyAccountLevelRollbackAsync(ServerDatabase database, NpgsqlConnection owner, List<TestUser> users, string roomId)
    {
        var fixture = RewardFixture(users.Select(user => user.Login.AccountId).ToArray(), roomId, 1, 3, new[] { 4, 4, 0 });
        var before = await Task.WhenAll(users.Select(user => database.ProfileAsync(Guid.Parse(user.Login.AccountId))));
        await using (var command = new NpgsqlCommand("ALTER TABLE \"AccountMatch\" ADD CONSTRAINT \"LevelQaRollback\" CHECK (\"MatchId\" <> '" + fixture.MatchId + "'::uuid)", owner))
            await command.ExecuteNonQueryAsync();
        try
        {
            bool rolledBack = false;
            try { await database.RecordMatchAsync(fixture); }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation) { rolledBack = true; }
            Check(rolledBack, "ledger 저장 실패를 실제 PostgreSQL에서 발생시켜야 합니다.");
            var after = await Task.WhenAll(users.Select(user => database.ProfileAsync(Guid.Parse(user.Login.AccountId))));
            Check(before.Zip(after).All(pair => pair.First.Coins == pair.Second.Coins && pair.First.Experience == pair.Second.Experience),
                "ledger 저장 실패는 모든 참가자의 코인·XP를 함께 롤백해야 합니다.");
            await using var exists = new NpgsqlCommand("SELECT count(*) FROM \"MatchRecord\" WHERE \"MatchId\"=$1", owner);
            exists.Parameters.AddWithValue(Guid.Parse(fixture.MatchId));
            Check(Convert.ToInt64(await exists.ExecuteScalarAsync()) == 0, "실패한 정산은 경기 레코드도 남기면 안 됩니다.");
        }
        finally
        {
            await using var command = new NpgsqlCommand("ALTER TABLE \"AccountMatch\" DROP CONSTRAINT \"LevelQaRollback\"", owner);
            await command.ExecuteNonQueryAsync();
        }
        Report("실제 ledger 제약 실패의 코인·XP·경기 레코드 원자적 롤백 검증");
    }

    private static async Task VerifyAccountLevelWorkshopAsync(HttpClient game, List<TestUser> users, NpgsqlConnection owner)
    {
        string token = users[0].Session.SessionToken, readerToken = users[1].Session.SessionToken;
        var published = await PostAsync<TopicWorkshopDetailResponse>(game, "/api/topic-workshop", new TopicWorkshopPublishRequest
        { Name = "레벨창작자", LanguageCode = "ko-KR", Words = Enumerable.Range(1, 10).Select(index => "제시어" + index).ToArray() }, token);
        Check(published.Topic.CreatorLevel == 2, "게시 응답에 인증된 작성자 레벨을 전달해야 합니다.");
        await SetAccountLevelExperienceAsync(owner, users[0], AccountLevelRules.ExperienceForLevel(75));
        string path = "/api/topic-workshop/" + published.Topic.Id;
        var list = await GetAsync<TopicWorkshopListResponse>(game, "/api/topic-workshop?search=" + Uri.EscapeDataString("레벨창작자"), readerToken);
        var preview = await GetAsync<TopicWorkshopDetailResponse>(game, path + "/preview", readerToken);
        var download = await GetAsync<TopicWorkshopDetailResponse>(game, path, readerToken);
        var recommended = await PostAsync<TopicWorkshopEntry>(game, path + "/recommend", new TopicWorkshopRecommendationRequest { IsRecommended = true }, readerToken);
        Check(list.Items.Single().CreatorLevel == 75 && preview.Topic.CreatorLevel == 75 && download.Topic.CreatorLevel == 75
            && recommended.CreatorLevel == 75 && preview.Words.SequenceEqual(published.Words),
            "검색·미리보기·다운로드·추천은 저장 시점 대신 현재 작성자 레벨과 정확한 제시어를 전달해야 합니다.");
    }

    private static async Task VerifyAccountLevelInvitationAsync(HttpClient main, HttpClient game, TestUser host, NpgsqlConnection owner,
        string roomId, string roomCode)
    {
        var receiver = await GuestAndEnterAsync(main);
        await SocialMakeFriendsAsync(game, host, receiver);
        await WaitSocialRosterAsync(owner, roomId, host.Login.AccountId, true);
        var invitation = await PostAsync<RoomInvitationData>(game, "/api/rooms/" + roomCode + "/invite",
            new FriendRequest { AccountId = receiver.Login.AccountId }, host.Session.SessionToken);
        var inbox = await GetAsync<SocialInboxResponse>(game, "/api/social/inbox", receiver.Session.SessionToken);
        Check(invitation.Sender.Level == 75 && inbox.RoomInvitations.Single().Sender.Level == 75
            && inbox.Friends.Friends.Single().Level == 75, "친구·초대 응답과 수신함 모두 현재 발신자 레벨을 전달해야 합니다.");
    }

    private static async Task VerifyAccountLevelChatAsync(HttpClient game, List<TestUser> users, List<Peer> peers, NpgsqlConnection owner, string roomId)
    {
        string account = users[0].Login.AccountId;
        var lobbyUri = new Uri("ws://127.0.0.1:25560/ws/lobby");
        await using var lobby = await LobbyPeer.OpenAsync(lobbyUri, users[0].Session.SessionToken);
        await lobby.WaitAsync(message => message.Type == "history");
        await lobby.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "레벨검증75", RequestId = "level75",
            Message = new LobbyChatMessage { Level = 100, AccountId = users[1].Login.AccountId } });
        Check((await lobby.WaitAsync(message => message.Type == "chat" && message.RequestId == "level75")).Message.Level == 75,
            "로비 채팅은 클라이언트의 위조 레벨 대신 인증된 현재 XP로 레벨을 계산해야 합니다.");
        string refreshId = Guid.NewGuid().ToString();
        await peers[0].SendAsync(new GameplayEnvelope { Type = "request", Kind = "refreshProfile", RequestId = refreshId });
        await peers[0].WaitStateAsync(state => state.Players.Single(player => player.AccountId == account).Level == 75);
        await WaitAsync(() => peers[0].Trace.Any(frame => frame.Type == "profile-refreshed" && frame.RequestId == refreshId && frame.Accepted), 10);
        await peers[0].SendAsync(new GameplayEnvelope { Type = "request", Kind = "chat", Text = "현재레벨75", Line = new ChatLine { Level = 100 } });
        await WaitAsync(() => peers[1].Trace.Any(frame => frame.Type == "chat" && frame.Line.Text == "현재레벨75"), 5);
        Check(peers[1].Trace.Last(frame => frame.Type == "chat" && frame.Line.Text == "현재레벨75").Line.Level == 75,
            "인게임 채팅은 실제 프로필 갱신 레벨을 쓰고 클라이언트 위조값을 무시해야 합니다.");
        await SetAccountLevelExperienceAsync(owner, users[0], AccountLevelRules.ExperienceForLevel(100));
        await Task.Delay(1050);
        await lobby.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "레벨검증100", RequestId = "level100" });
        Check((await lobby.WaitAsync(message => message.Type == "chat" && message.RequestId == "level100")).Message.Level == 100,
            "연결 중 XP가 갱신돼도 로비 발언마다 현재 레벨을 재인증해야 합니다.");
        await using var history = await LobbyPeer.OpenAsync(lobbyUri, users[1].Session.SessionToken);
        var remembered = await history.WaitAsync(message => message.Type == "history");
        Check(remembered.Messages.Single(message => message.Text == "레벨검증75").Level == 75
            && remembered.Messages.Single(message => message.Text == "레벨검증100").Level == 100,
            "로비 기록은 각 메시지의 인증된 전송 시점 레벨을 보존해야 합니다.");
        await peers[0].LeaveAsync();
        peers[0] = await Peer.ConnectAsync(await JoinAsync(game, roomId, users[0], false), ValidateCertificate);
        await peers[0].WaitStateAsync(state => state.Players.Single(player => player.AccountId == account).Level == 100);
        var privateProfile = await GetAsync<ProfileData>(game, "/api/profile", users[0].Session.SessionToken);
        using var forged = new HttpRequestMessage(HttpMethod.Patch, "/api/profile") { Content = JsonContent.Create(new
        { privateProfile.DisplayName, privateProfile.AvatarColor, privateProfile.Accessory, Experience = 0L, Level = 1 }, options: Json) };
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", users[0].Session.SessionToken);
        using var response = await game.SendAsync(forged); response.EnsureSuccessStatusCode();
        var saved = await response.Content.ReadFromJsonAsync<ProfileData>(Json);
        Check(saved!.Experience == AccountLevelRules.ExperienceForLevel(100) && saved.Level == 100,
            "프로필 저장은 클라이언트가 XP·레벨을 조작하게 하면 안 됩니다.");
    }

    private static async Task SetAccountLevelExperienceAsync(NpgsqlConnection owner, TestUser user, long experience)
    {
        await using var command = new NpgsqlCommand("UPDATE \"Account\" SET \"Experience\"=$2 WHERE \"Id\"=$1", owner);
        command.Parameters.AddWithValue(Guid.Parse(user.Login.AccountId)); command.Parameters.AddWithValue(experience);
        Check(await command.ExecuteNonQueryAsync() == 1, "격리 QA 계정의 레벨 fixture를 설정해야 합니다.");
    }
}
