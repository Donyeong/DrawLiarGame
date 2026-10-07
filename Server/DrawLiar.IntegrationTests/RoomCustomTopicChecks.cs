using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private const string TOPIC_CONFIG_NODE = "dedicated-reward-qa";

    private static async Task VerifyRoomCustomTopicsAsync()
    {
        var original = new GameData { Topics = new[] { new TopicData { Name = "기존주제", Words = new[] { "기존단어" } } } };
        var replacement = new GameData { Topics = original.Topics.Concat(new[]
        { new TopicData { Name = "추가주제", Words = new[] { "추가사과", "추가배" } } }).ToArray() };
        var game = new GameSession(new RoomSettings { Topics = new[] { "기존주제" } }, original, 3);
        for (int id = 1; id <= 3; id++) Check(game.Join(id, "설정검증" + id, 0, 0), "주제 변경 참가자를 준비해야 합니다.");
        var requested = game.Settings.Copy(); requested.Topics = new[] { "추가주제" };
        Check(game.CanConfigure(requested, replacement) && game.Settings.Topics.SequenceEqual(new[] { "기존주제" })
            && !game.Snapshot(1, 1, 0).AvailableTopics.Contains("추가주제"), "주제 변경 사전 검증은 현재 설정과 데이터를 수정하지 않아야 합니다.");
        var invalid = requested.Copy(); invalid.Topics = new[] { "없는주제" };
        Check(!game.Configure(invalid, replacement) && game.Settings.Topics.SequenceEqual(new[] { "기존주제" })
            && !game.Snapshot(1, 1, 0).AvailableTopics.Contains("추가주제"), "거부된 주제 변경은 설정과 데이터 모두를 보존해야 합니다.");
        var insufficient = new GameData { Topics = new[] { new TopicData { Name = "추가주제", Words = new[] { "사 과", " 사과 " } } } };
        var mismatch = requested.Copy(); mismatch.LiarMode = LiarMode.Mismatch;
        Check(!game.Configure(mismatch, insufficient) && game.Settings.LiarMode == LiarMode.Classic,
            "미스매치에 서로 다른 단어가 없으면 데이터와 라이어 방식 모두를 변경하지 않아야 합니다.");
        Check(game.Configure(requested, replacement) && game.CanStart && game.Snapshot(1, 1, 0).AvailableTopics.Contains("기존주제"),
            "새 커스텀 주제를 선택하면서 기존 주제 데이터도 유지해야 합니다.");
        Check(game.Start(0) && Enumerable.Range(1, 3).Where(id => !game.Snapshot(id, 1, 0).LocalIsLiar)
            .All(id => new[] { "추가사과", "추가배" }.Contains(game.Snapshot(id, 1, 0).Word)),
            "변경 후 시작한 라운드는 추가한 주제의 실제 단어를 배정해야 합니다.");
        Check(!game.Configure(new RoomSettings { Topics = new[] { "기존주제" } }, original)
            && game.Settings.Topics.SequenceEqual(requested.Topics), "진행 중 주제 변경은 현재 라운드와 설정을 보존해야 합니다.");
        await VerifyCustomTopicDedicatedAsync(original);
        Report("커스텀 주제 사전 검증·설정/데이터 원자적 변경·미스매치 단어 검증·DS 권한/실패/복구·실제 라운드 배정 검증");
    }

    private static async Task VerifyCustomTopicDedicatedAsync(GameData builtIn)
    {
        var existing = new[] { new ServerTopicData { Name = "전임주제", Words = new[] { "전임단어" } } };
        var added = new ServerTopicData { Name = "직접추가", Words = new[] { "새사과", "새배" } };
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "host",
            Settings = new ServerRoomSettings { Topics = new[] { "기존주제" } } };
        var room = new DedicatedRoom(roomData, builtIn, existing, 0, () => { });
        var socket = new PolicySocket();
        await using var host = new GameConnection(socket, "host", "topic-test", CancellationToken.None);
        await using var member = new GameConnection(new PolicySocket(), "member", "topic-test", CancellationToken.None);
        await using var third = new GameConnection(new PolicySocket(), "third", "topic-test", CancellationToken.None);
        await using var observer = new GameConnection(new PolicySocket(), "observer", "topic-test", CancellationToken.None);
        RedeemTicketResponse Ticket(string account, bool spectator = false) => new() { AccountId = account, Room = roomData,
            IsSpectator = spectator, SpectatorOnly = spectator, Profile = new ProfileData { AccountId = account, DisplayName = account } };
        Check(room.Join(host, Ticket("host"), 0) && room.Join(member, Ticket("member"), 0)
            && room.Join(third, Ticket("third"), 0) && room.Join(observer, Ticket("observer", true), 0), "DS 주제 검증 참가자를 준비해야 합니다.");
        var settings = new RoomSettings { RoomName = "새주제방", Topics = new[] { added.Name }, LiarMode = LiarMode.Mismatch, RoundCount = 1 };
        int persisted = 0;
        ConfigureRoomRequest? wireRequest = null;
        RoomConfigurationData? confirmed = null;
        var customTopics = existing;
        Task<RoomConfigurationData> Persist(ConfigureRoomRequest request, CancellationToken cancellation)
        {
            persisted++; wireRequest = request;
            customTopics = ServerDatabase.MergeCustomTopics(customTopics, request.CustomTopics);
            confirmed = new RoomConfigurationData { RoomId = roomData.RoomId, Settings = request.Settings,
                CustomTopics = customTopics, Version = request.ExpectedVersion + 1 };
            return Task.FromResult(confirmed);
        }
        GameplayEnvelope Envelope(RoomSettings value, params ServerTopicData[] delta) => new()
        { Type = "request", Kind = "configure", Settings = value, CustomTopics = delta, RequestId = Guid.NewGuid().ToString("N") };
        foreach (var unauthorized in new[] { member, observer }) await room.ConfigureAsync(unauthorized, Envelope(settings, added), .1, Persist);
        Check(persisted == 0 && room.Status().Settings.Topics.SequenceEqual(new[] { "기존주제" }), "비방장과 관전자는 새 주제를 서버에 저장할 수 없어야 합니다.");
        var invalid = settings.Copy(); invalid.Topics = new[] { "없는주제" };
        await room.ConfigureAsync(host, Envelope(invalid, added), .2, Persist);
        await room.ConfigureAsync(host, Envelope(settings, new ServerTopicData { Name = added.Name, Words = new[] { "사 과", "사과" } }), .3, Persist);
        Check(persisted == 0 && !socket.LastState!.AvailableTopics.Contains(added.Name), "미지 주제와 미스매치용 단어 부족은 저장 이전에 거부해야 합니다.");
        var failed = settings.Copy(); failed.LiarMode = LiarMode.Classic;
        await room.ConfigureAsync(host, Envelope(failed, added), .4,
            (_, cancellation) => Task.FromException<RoomConfigurationData>(new ApiException("InvalidTopics")));
        Check(room.Status().Settings.Topics.SequenceEqual(new[] { "기존주제" }) && !socket.LastState!.AvailableTopics.Contains(added.Name),
            "게임서버 저장 실패 후에도 현재 주제 데이터가 추가되면 안 됩니다.");
        await room.ConfigureAsync(host, Envelope(settings, added), .5, Persist);
        await WaitAsync(() => socket.LastState?.AvailableTopics.Contains(added.Name) == true, 3);
        Check(persisted == 1 && wireRequest!.CustomTopics!.Length == 1 && wireRequest.CustomTopics[0].Words.SequenceEqual(added.Words)
            && socket.LastState!.AvailableTopics.Contains(existing[0].Name) && room.Status().Settings.Topics.SequenceEqual(new[] { added.Name }),
            "DS는 커스텀 변경분을 전송하고 서버 ACK의 전체 주제 목록을 기존 주제와 함께 적용해야 합니다.");
        foreach (var value in added.Words.Concat(existing[0].Words))
            Check(!JsonSerializer.Serialize(socket.LastState, Json).Contains(value, StringComparison.Ordinal), "대기실 공용 snapshot에 주제의 제시어를 노출하면 안 됩니다.");
        var stale = new RoomConfigurationData { RoomId = roomData.RoomId, Version = 0, Settings = roomData.Settings, CustomTopics = existing };
        room.ApplyConfiguration(stale);
        Check(room.Status().ConfigurationVersion == confirmed!.Version && room.Status().Settings.Topics.SequenceEqual(new[] { added.Name }),
            "오래된 heartbeat는 확인된 설정과 새 주제를 되돌리면 안 됩니다.");
        var restored = new DedicatedRoom(new ServerRoomData { RoomId = roomData.RoomId, OwnerAccountId = "host",
            Settings = roomData.Settings }, builtIn, existing, 0, () => { });
        var restoredSocket = new PolicySocket();
        await using var restoredHost = new GameConnection(restoredSocket, "host", "topic-test", CancellationToken.None);
        Check(restored.Join(restoredHost, Ticket("host"), 0), "복구할 DS 대기실을 준비해야 합니다.");
        restored.ApplyConfiguration(confirmed);
        await WaitAsync(() => restoredSocket.LastState?.AvailableTopics.Contains(added.Name) == true, 3);
        Check(restored.Status().Settings.Topics.SequenceEqual(new[] { added.Name }), "heartbeat 복구는 추가한 주제의 데이터와 선택을 함께 복원해야 합니다.");
        room.Receive(host, new GameplayEnvelope { Type = "request", Kind = "start" }, 1);
        await WaitAsync(() => socket.LastState?.Phase == GamePhase.RoleReveal, 3);
        Check(added.Words.Contains(socket.LastState!.Word), "실제 DS의 미스매치 라운드도 추가된 단어를 사용해야 합니다.");
        await room.ConfigureAsync(host, Envelope(settings, added), 1.1, Persist);
        Check(persisted == 1, "진행 중에는 방장도 주제 변경을 저장할 수 없어야 합니다.");
        for (int index = 1; index <= 30 && room.Status().IsInProgress; index++) room.Tick(1 + index * 1000);
        var afterMatch = settings.Copy(); afterMatch.LiarMode = LiarMode.Classic; afterMatch.Topics = new[] { "종료후주제" };
        await room.ConfigureAsync(host, Envelope(afterMatch, new ServerTopicData { Name = "종료후주제", Words = new[] { "종료후사과" } }), 31000, Persist);
        Check(persisted == 2 && room.Status().Settings.Topics.SequenceEqual(afterMatch.Topics)
            && customTopics.Select(topic => topic.Name).Order().SequenceEqual(new[] { existing[0].Name, added.Name, "종료후주제" }.Order()),
            "최종 결과 이후에도 기존 주제를 모두 유지하면서 새로운 커스텀 주제를 선택할 수 있어야 합니다.");
    }

    private static async Task VerifyRoomCustomTopicsDatabaseAsync(string mainUrl)
    {
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "방 주제 DB 검증은 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_ROOM_TOPIC_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_ROOM_TOPIC_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25539 && scoped.Database == "postgres"
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_room_topic_test_[0-9a-f]{32}$"), "방 주제 검증은 격리된 PostgreSQL 스키마만 사용합니다.");
        using var main = Client(mainUrl); await RequireHealthAsync(main);
        var users = new[] { await GuestAndEnterAsync(main), await GuestAndEnterAsync(main), await GuestAndEnterAsync(main) };
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "방 주제 검증은 전용 로컬 게임서버만 사용합니다.");
        using var api = Client(users[0].Login.GameServerUrl); await RequireHealthAsync(api);
        await RewardClusterPostAsync(api, "/internal/dedicated/register", new RegisterDedicatedRequest
        { NodeId = TOPIC_CONFIG_NODE, PublicUrl = "ws://127.0.0.1:25570/play", Capacity = 32 });
        var existing = new ServerTopicData { Name = "전임방주제", Words = new[] { "전임사과", "전임배" } };
        var assignment = await PostAsync<DedicatedAssignment>(api, "/api/rooms", new CreateRoomRequest
        { Settings = new ServerRoomSettings { Topics = new[] { existing.Name } }, CustomTopics = new[] { existing } }, users[0].Session.SessionToken);
        var ticket = await RewardClusterPostAsync<RedeemTicketResponse>(api, "/internal/tickets/redeem",
            new RedeemTicketRequest { NodeId = TOPIC_CONFIG_NODE, RoomId = assignment.RoomId, JoinTicket = assignment.JoinTicket });
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        var published = await PostAsync<JsonElement>(api, WORKSHOP_PATH,
            new { Name = "다운받은주제", LanguageCode = "ko-KR", Words = new[] { "구름", "별" } }, users[2].Session.SessionToken);
        var downloaded = await GetAsync<JsonElement>(api, WORKSHOP_PATH + "/" + WorkshopId(published), users[0].Session.SessionToken);
        Check(downloaded.GetProperty("Topic").GetProperty("CreatorAccountId").GetString() == users[2].Login.AccountId,
            "방장이 다른 사용자의 창작마당 콘텐츠를 내려받아야 합니다.");
        var newTopic = new ServerTopicData { Name = downloaded.GetProperty("Topic").GetProperty("Name").GetString()!, Words = WorkshopWords(downloaded) };
        ConfigureRoomRequest Request(params ServerTopicData[] delta) => new()
        {
            NodeId = TOPIC_CONFIG_NODE, RoomId = assignment.RoomId, OwnerAccountId = users[0].Login.AccountId,
            OperationId = Guid.NewGuid().ToString(), ExpectedVersion = 0,
            Settings = new ServerRoomSettings { RoomName = "변경한 주제방", Topics = new[] { newTopic.Name }, LiarMode = 1 }, CustomTopics = delta
        };
        string initial = await CustomTopicRoomRowAsync(owner, assignment.RoomId);
        using (var unauthenticated = await SendAsync(api, "/internal/rooms/configure", Request(newTopic), null))
            Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "내부 주제 변경은 클러스터 인증을 요구해야 합니다.");
        var nonHost = Request(newTopic); nonHost.OwnerAccountId = users[1].Login.AccountId;
        await CustomTopicConfigureErrorAsync(api, nonHost, HttpStatusCode.Forbidden, "RoomConfigurationDenied");
        var stale = Request(newTopic); stale.ExpectedVersion = 9;
        await CustomTopicConfigureErrorAsync(api, stale, HttpStatusCode.Conflict, "RoomConfigurationChanged");
        foreach (var invalid in new[]
        {
            Array.Empty<ServerTopicData>(),
            new[] { new ServerTopicData { Name = "과일", Words = new[] { "사과", "배" } } },
            new[] { new ServerTopicData { Name = newTopic.Name, Words = new[] { "사 과", "사과" } } },
            new[] { new ServerTopicData { Name = newTopic.Name, Words = new[] { "bad\nword", "배" } } },
            new[] { new ServerTopicData { Name = newTopic.Name, Words = Array.Empty<string>() } },
            new[] { new ServerTopicData { Name = newTopic.Name, Words = new[] { new string('가', 41), "배" } } },
            new[] { new ServerTopicData { Name = newTopic.Name, Words = Enumerable.Range(0, 201).Select(index => "단어" + index).ToArray() } },
            new[] { newTopic, new ServerTopicData { Name = " " + newTopic.Name + " ", Words = new[] { "구름" } } },
            Enumerable.Range(0, 101).Select(index => new ServerTopicData { Name = "한도" + index, Words = new[] { "사과" } }).ToArray(),
            new[] { newTopic }.Concat(Enumerable.Range(0, 99).Select(index => new ServerTopicData { Name = "합산한도" + index, Words = new[] { "사과" } })).ToArray(),
            Enumerable.Range(0, 99).Select(index => new ServerTopicData { Name = "바이트" + index, Words = Enumerable.Range(0, 2).Select(word => new string('가', 38) + word).ToArray() }).ToArray()
        })
        {
            await CustomTopicConfigureErrorAsync(api, Request(invalid), HttpStatusCode.BadRequest, "InvalidTopics");
            Check(await CustomTopicRoomRowAsync(owner, assignment.RoomId) == initial, "잘못된 주제 변경은 설정·데이터·설정 버전·입장 버전을 모두 보존해야 합니다.");
        }
        var acceptedRequest = Request(newTopic);
        var accepted = await RewardClusterPostAsync<RoomConfigurationData>(api, "/internal/rooms/configure", acceptedRequest);
        Check(accepted.Version == 1 && accepted.AccessVersion == ticket.Room.AccessVersion
            && accepted.CustomTopics!.Length == 2 && accepted.CustomTopics.Single(topic => topic.Name == existing.Name).Words.SequenceEqual(existing.Words)
            && accepted.CustomTopics.Single(topic => topic.Name == newTopic.Name).Words.SequenceEqual(newTopic.Words),
            "설정 저장 ACK는 추가 주제와 다른 방장의 기존 주제를 모두 보존하고 접근 버전을 바꾸지 않아야 합니다.");
        string stored = await CustomTopicRoomRowAsync(owner, assignment.RoomId);
        var replay = await RewardClusterPostAsync<RoomConfigurationData>(api, "/internal/rooms/configure", acceptedRequest);
        Check(JsonSerializer.Serialize(replay, Json) == JsonSerializer.Serialize(accepted, Json)
            && await CustomTopicRoomRowAsync(owner, assignment.RoomId) == stored, "동일 작업 재시도는 전체 커스텀 데이터와 버전을 그대로 반환해야 합니다.");
        await CustomTopicConfigureErrorAsync(api, Request(newTopic), HttpStatusCode.Conflict, "RoomConfigurationChanged");
        Check(await CustomTopicRoomRowAsync(owner, assignment.RoomId) == stored, "오래된 초안은 저장된 주제를 되돌릴 수 없어야 합니다.");
        var state = new RoomStatusData { RoomId = assignment.RoomId, OwnerAccountId = users[0].Login.AccountId,
            PlayerCount = 1, PlayerAccountIds = new[] { users[0].Login.AccountId }, Settings = ticket.Room.Settings, ConfigurationVersion = 0 };
        var heartbeat = await RewardClusterPostAsync<DedicatedHeartbeatResponse>(api, "/internal/dedicated/heartbeat",
            new DedicatedHeartbeatRequest { NodeId = TOPIC_CONFIG_NODE, Rooms = new[] { state } });
        var refreshed = heartbeat.Configurations.Single(configuration => configuration.RoomId == assignment.RoomId);
        Check(JsonSerializer.Serialize(refreshed, Json) == JsonSerializer.Serialize(accepted, Json),
            "오래된 DS heartbeat는 DB에 저장한 전체 커스텀 주제와 최신 설정을 받아야 합니다.");
        var joined = await JoinAsync(api, assignment.RoomId, users[1], false);
        var secondTicket = await RewardClusterPostAsync<RedeemTicketResponse>(api, "/internal/tickets/redeem",
            new RedeemTicketRequest { NodeId = TOPIC_CONFIG_NODE, RoomId = assignment.RoomId, JoinTicket = joined.JoinTicket });
        Check(secondTicket.Room.ConfigurationVersion == 1 && secondTicket.CustomTopics.Length == 2
            && secondTicket.Room.Settings.Topics.SequenceEqual(new[] { newTopic.Name }), "새 입장권과 DS 재생성도 변경한 주제 전체를 받아야 합니다.");
        var inProgress = await VerifyCustomTopicHttpWireAsync(api, ticket, secondTicket, users[2], accepted, existing, newTopic);
        await RewardClusterPostAsync<DedicatedHeartbeatResponse>(api, "/internal/dedicated/heartbeat",
            new DedicatedHeartbeatRequest { NodeId = TOPIC_CONFIG_NODE, Rooms = new[] { inProgress } });
        string running = await CustomTopicRoomRowAsync(owner, assignment.RoomId);
        var whilePlaying = Request(newTopic); whilePlaying.ExpectedVersion = 2;
        await CustomTopicConfigureErrorAsync(api, whilePlaying, HttpStatusCode.Forbidden, "RoomConfigurationDenied");
        Check(await CustomTopicRoomRowAsync(owner, assignment.RoomId) == running, "GS도 진행 중의 설정·커스텀 주제 변경을 원자적으로 거부해야 합니다.");
        Report("실제 PostgreSQL·HTTP 타인 창작마당 다운로드→기존방 주제 추가/보존·클러스터/방장 권한·유효성/크기/미스매치·stale/재시도·heartbeat/새 입장·DS configure→저장→새 라운드 검증");
        await VerifyCustomTopicWebSocketAsync(main);
    }

    private static async Task<RoomStatusData> VerifyCustomTopicHttpWireAsync(HttpClient api, RedeemTicketResponse hostTicket,
        RedeemTicketResponse memberTicket, TestUser thirdUser, RoomConfigurationData configuration, ServerTopicData existing, ServerTopicData added)
    {
        using var stream = typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.BuiltInGameData.json")!;
        var builtIn = JsonSerializer.Deserialize<GameData>(stream, Json)!;
        var room = new DedicatedRoom(hostTicket.Room, builtIn, hostTicket.CustomTopics, 0, () => { });
        var socket = new PolicySocket();
        await using var host = new GameConnection(socket, hostTicket.AccountId, "topic-http-test", CancellationToken.None);
        await using var member = new GameConnection(new PolicySocket(), memberTicket.AccountId, "topic-http-test", CancellationToken.None);
        room.ApplyConfiguration(configuration);
        Check(room.Join(host, hostTicket, 0) && room.Join(member, memberTicket, 0), "실제 HTTP 설정을 반영한 DS에 기존 참가자가 입장해야 합니다.");
        var assignment = await JoinAsync(api, hostTicket.Room.RoomId, thirdUser, false);
        var thirdTicket = await RewardClusterPostAsync<RedeemTicketResponse>(api, "/internal/tickets/redeem",
            new RedeemTicketRequest { NodeId = TOPIC_CONFIG_NODE, RoomId = assignment.RoomId, JoinTicket = assignment.JoinTicket });
        await using var third = new GameConnection(new PolicySocket(), thirdTicket.AccountId, "topic-http-test", CancellationToken.None);
        Check(room.Join(third, thirdTicket, 0), "실제 세 번째 입장권을 DS에서 사용해야 합니다.");
        var replaced = new ServerTopicData { Name = added.Name, Words = new[] { "갱신사과", "갱신배" } };
        var requested = JsonSerializer.Deserialize<RoomSettings>(JsonSerializer.Serialize(configuration.Settings, Json), Json)!;
        RoomConfigurationData? response = null;
        await room.ConfigureAsync(host, new GameplayEnvelope { Type = "request", Kind = "configure", Settings = requested,
            CustomTopics = new[] { replaced }, RequestId = "custom-topic-http-wire" }, .1, async (request, cancellation) =>
            {
                request.NodeId = TOPIC_CONFIG_NODE;
                response = await RewardClusterPostAsync<RoomConfigurationData>(api, "/internal/rooms/configure", request);
                return response;
            });
        Check(response?.Version == 2 && response.CustomTopics!.Single(topic => topic.Name == existing.Name).Words.SequenceEqual(existing.Words)
            && response.CustomTopics.Single(topic => topic.Name == added.Name).Words.SequenceEqual(replaced.Words),
            "실제 DS configure wire의 동명 수정은 해당 주제만 바꾸고 다른 방 주제는 보존해야 합니다.");
        room.Receive(host, new GameplayEnvelope { Type = "request", Kind = "start" }, .2);
        await WaitAsync(() => socket.LastState?.Phase == GamePhase.RoleReveal, 3);
        Check(replaced.Words.Contains(socket.LastState!.Word), "실제 HTTP에 저장된 수정 단어를 다음 DS 라운드에서 사용해야 합니다.");
        return room.Status();
    }

    private static async Task CustomTopicConfigureErrorAsync(HttpClient api, ConfigureRoomRequest request, HttpStatusCode status, string code)
    {
        using var response = await RewardClusterSendAsync(api, "/internal/rooms/configure", request);
        var error = JsonSerializer.Deserialize<ApiError>(await response.Content.ReadAsStringAsync(), Json);
        Check(response.StatusCode == status && error?.Code == code,
            $"잘못된 방 주제 변경은 {(int)status}/{code}이어야 합니다. 실제 {(int)response.StatusCode}/{error?.Code}");
    }

    private static async Task VerifyCustomTopicWebSocketAsync(HttpClient main)
    {
        var users = new[] { await GuestAndEnterAsync(main), await GuestAndEnterAsync(main), await GuestAndEnterAsync(main) };
        using var api = Client(users[0].Login.GameServerUrl);
        var original = new ServerTopicData { Name = "연결기존주제", Words = new[] { "기존사과", "기존배" } };
        var assignment = await PostAsync<DedicatedAssignment>(api, "/api/rooms", new CreateRoomRequest
        { Settings = new ServerRoomSettings { Topics = new[] { original.Name } }, CustomTopics = new[] { original } }, users[0].Session.SessionToken);
        Check(assignment.DedicatedUrl == "ws://127.0.0.1:25570/play", "커스텀 주제 WebSocket 검증은 전용 로컬 DS만 사용합니다.");
        var peers = new List<Peer>();
        try
        {
            for (int index = 0; index < 3; index++)
            {
                var admission = index == 0 ? assignment : await JoinAsync(api, assignment.RoomId, users[index], false);
                peers.Add(await Peer.ConnectAsync(admission, ValidateCertificate));
            }
            var before = await peers[0].WaitStateAsync(state => state.Phase == GamePhase.Lobby && state.Players.Count(player => player.IsConnected) == 3);
            var selected = before.Settings.Copy(); selected.Topics = new[] { "연결추가주제" }; selected.LiarMode = LiarMode.Mismatch;
            var added = new ServerTopicData { Name = selected.Topics[0], Words = new[] { "연결새사과", "연결새배" } };
            async Task Configure(Peer peer, string requestId, bool accepted)
            {
                await peer.SendAsync(new GameplayEnvelope { Type = "request", Kind = "configure", Settings = selected,
                    CustomTopics = new[] { added }, RequestId = requestId });
                await WaitAsync(() => peer.Trace.Any(message => message.Type == "configured" && message.RequestId == requestId), 10);
                Check(peer.Trace.Single(message => message.Type == "configured" && message.RequestId == requestId).Accepted == accepted,
                    "실제 configure WebSocket ACK가 새 주제 저장의 권한과 성공 여부를 알려야 합니다.");
            }
            await Configure(peers[1], "custom-topic-ws-denied", false);
            await Configure(peers[0], "custom-topic-ws-accepted", true);
            foreach (var peer in peers)
            {
                var snapshot = await peer.WaitStateAsync(state => state.Settings.Topics.SequenceEqual(selected.Topics));
                Check(snapshot.AvailableTopics.Contains(original.Name) && snapshot.AvailableTopics.Contains(added.Name),
                    "실제 DS의 모든 연결은 기존 주제와 새 주제의 이름을 함께 받아야 합니다.");
                foreach (var word in original.Words.Concat(added.Words))
                    Check(!JsonSerializer.Serialize(snapshot, Json).Contains(word, StringComparison.Ordinal), "실제 대기실 WebSocket JSON에도 커스텀 제시어를 노출하면 안 됩니다.");
            }
            await peers[0].SendAsync(new GameplayEnvelope { Type = "request", Kind = "start" });
            var roles = new List<RoomSnapshot>();
            foreach (var peer in peers) roles.Add(await peer.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal));
            Check(roles.All(state => added.Words.Contains(state.Word)) && roles.Select(state => state.Word).Distinct().Count() == 2
                && roles.All(state => !state.LocalIsLiar && state.Players.All(player => !player.IsLiar)),
                "실제 WebSocket configure→GS/DB→DS 미스매치 시작은 추가한 두 단어와 비밀 규칙을 유지해야 합니다.");
            Report("실제 WebSocket 새 CustomTopics 필드 송수신·비방장 ACK 거부·방장 저장 ACK·참가자 snapshot·추가 단어 미스매치 시작 검증");
        }
        finally
        {
            foreach (var peer in peers) { if (!peer.IsClosed) await peer.LeaveAsync(); await peer.DisposeAsync(); }
        }
    }

    private static async Task<string> CustomTopicRoomRowAsync(NpgsqlConnection owner, string roomId)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('Settings',"Settings",'CustomTopics',"CustomTopics",'Version',"ConfigurationVersion",
                'AccessVersion',"AccessVersion",'PasswordHash',"PasswordHash",'OperationId',"LastConfigurationId")::text
            FROM "Room" WHERE "RoomId"=$1
            """, owner);
        command.Parameters.AddWithValue(Guid.Parse(roomId));
        return await command.ExecuteScalarAsync() as string ?? throw new InvalidDataException("검증 방이 없습니다.");
    }
}
