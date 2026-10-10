using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DrawLiar;

await Integration.RunAsync(args);

internal static partial class Integration
{
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };
    private static string _pin = "";
    private static int _tunnelLogged;

    public static async Task RunAsync(string[] args)
    {
        if (args.Contains("--room-creation-only"))
        {
            await VerifyRoomCreationAsync();
            return;
        }
        if (args.Contains("--commerce-only"))
        {
            await VerifyCommerceAsync();
            return;
        }
        if (args.Contains("--commerce-database-only"))
        {
            await VerifyCommerceAsync();
            await VerifyCommerceDatabaseAsync();
            return;
        }
        if (args.Contains("--account-level-only") || args.Contains("--account-level-http-only"))
        {
            VerifyAccountLevelRules();
            if (args.Contains("--account-level-http-only"))
            {
                string localUrl = args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550";
                await VerifyMatchRewardsAsync(localUrl);
                await VerifyAccountLevelHttpAsync(localUrl);
            }
            return;
        }
        if (args.Contains("--room-kick-only") || args.Contains("--room-kick-http-only"))
        {
            await VerifyRoomKickWireAsync();
            if (args.Contains("--room-kick-http-only"))
                await VerifyRoomKickHttpAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--purchase-batch-only") || args.Contains("--purchase-batch-http-only"))
        {
            await VerifyPurchaseBatchDatabaseAsync();
            if (args.Contains("--purchase-batch-http-only"))
                await VerifyPurchaseBatchHttpAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--room-custom-topics-only") || args.Contains("--room-custom-topics-database-only"))
        {
            await VerifyRoomCustomTopicsAsync();
            if (args.Contains("--room-custom-topics-database-only"))
                await VerifyRoomCustomTopicsDatabaseAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--optional-liar-only") || args.Contains("--optional-liar-database-only"))
        {
            await VerifyOptionalLiarAsync();
            if (args.Contains("--optional-liar-database-only"))
                await VerifyOptionalLiarDatabaseAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--match-reward-only"))
        {
            await VerifyMatchRewardsAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--topic-workshop-rules-only"))
        {
            VerifyTopicWorkshopRules();
            return;
        }
        if (args.Contains("--topic-workshop-only"))
        {
            VerifyTopicWorkshopRules();
            await VerifyTopicWorkshopAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--mismatch-only"))
        {
            await VerifyMismatchAsync();
            return;
        }
        if (args.Contains("--avatar-rules-only") || args.Contains("--avatar-database-only") || args.Contains("--avatar-http-database-only"))
        {
            VerifyAvatarPartRules();
            if (args.Contains("--avatar-database-only") || args.Contains("--avatar-http-database-only")) await VerifyAvatarDatabaseAsync();
            if (args.Contains("--avatar-http-database-only"))
                await VerifyAvatarHttpAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--midround-only"))
        {
            await VerifyMidRoundAsync();
            return;
        }
        if (args.Contains("--midround-database-only"))
        {
            await VerifyMidRoundDatabaseAsync();
            return;
        }
        if (args.Contains("--drawing-only"))
        {
            await VerifyDrawingHistoryAsync();
            return;
        }
        if (args.Contains("--social-only"))
        {
            await VerifySocialAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--friend-cancel-only"))
        {
            await VerifyFriendCancellationAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550");
            return;
        }
        if (args.Contains("--room-password-only") || args.Contains("--legacy-room-password-only"))
        {
            await VerifyRoomPasswordAsync(args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "http://127.0.0.1:25550",
                args.Contains("--legacy-room-password-only"));
            return;
        }
        if (args.Contains("--profile-rules-only"))
        {
            await VerifyProfileRulesAsync();
            return;
        }
        if (args.Contains("--profile-database-only"))
        {
            await VerifyProfileDatabaseAsync();
            return;
        }
        DrawLiar.Editor.GameSelfCheck.Run();
        VerifyAccountLevelRules();
        VerifyAvatarPartRules();
        await VerifyRoomPolicyAsync();
        await VerifyRoomCustomTopicsAsync();
        await VerifyConsensusWireAsync();
        await VerifyCoinTossWireAsync();
        await VerifyDrawingHistoryAsync();
        await VerifyMidRoundAsync();
        await VerifyOptionalLiarAsync();
        await VerifyProfileRulesAsync();
        VerifyGuestCredentialRules();
        if (args.Contains("--lobby-chat-only"))
        {
            await VerifyLobbyChatAsync();
            return;
        }
        if (args.Contains("--lobby-chat-database-only"))
        {
            await VerifyLobbyChatDatabaseAsync();
            return;
        }
        if (args.Contains("--guest-database-only"))
        {
            await VerifyGuestDatabaseAsync();
            return;
        }
        await VerifyLobbyChatAsync();
        if (args.Contains("--rules-only")) return;
        string mainUrl = args.Length > 0 ? args[0] : "http://127.0.0.1:19050";
        string adminUrl = args.Length > 1 ? args[1] : "http://127.0.0.1:19080";
        _pin = Environment.GetEnvironmentVariable("DRAWLIAR_CERT_PIN")?.Replace(":", "").ToUpperInvariant() ?? "";
        if (_pin.Length != 0 && (_pin.Length != 64 || !_pin.All(Uri.IsHexDigit))) throw new ArgumentException("인증서 핀은 DER SHA256 64자리 HEX여야 합니다.");
        using var main = Client(mainUrl);
        using var admin = Client(adminUrl);
        await RequireHealthAsync(main);
        await VerifyGuestApiAsync(main);
        if (args.Contains("--auth-only")) return;
        if (!args.Contains("--public-only"))
        {
            await RequireHealthAsync(admin);
            using var unauthorizedAdmin = await admin.GetAsync("/api/accounts");
            Check(unauthorizedAdmin.StatusCode == HttpStatusCode.Unauthorized, "관리 API는 관리자 인증을 요구해야 합니다.");
        }
        if (args.Contains("--rooms-only"))
        {
            await VerifyRoomLifecycleAsync(main);
            return;
        }
        var peers = new List<Peer>();
        try
        {
            string runId = Guid.NewGuid().ToString("N")[..8];
            var users = new List<TestUser>();
            for (int index = 0; index < 5; index++) users.Add(await GuestAndEnterAsync(main));
            using var game = Client(users[0].Login.GameServerUrl);
            await RequireHealthAsync(game);
            await AssertRejectedAsync(game, "/api/session/enter", new EnterGameRequest { AssignmentToken = new string('0', 64) }, null);
            await AssertRejectedAsync(game, "/api/session/enter", new EnterGameRequest { AssignmentToken = users[0].Login.AssignmentToken }, null);
            var reassigned = await PostAsync<LoginResponse>(main, "/api/session/assign", new { }, users[4].Login.SessionToken);
            Check(reassigned.AccountId == users[4].Login.AccountId && reassigned.SessionToken == users[4].Login.SessionToken,
                "다시 배정한 메인 응답 JSON을 전달해야 합니다.");
            var reassignedSession = await PostAsync<GameSessionResponse>(game, "/api/session/enter",
                new EnterGameRequest { AssignmentToken = reassigned.AssignmentToken }, null);
            users[4] = new TestUser(reassigned, reassignedSession);
            Report("PostgreSQL 게스트 인증 및 1회성 게임서버 배정 검증");
            await VerifyOutgameAsync(game, users);

            var room = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Settings = new ServerRoomSettings
                {
                    RoomName = "검증방" + runId, AllowMidRoundJoin = false, MaxPlayers = 8, LiarCount = 1,
                    Topics = new[] { "통합과일" }, RoleSeconds = 3, DrawSeconds = 30,
                    DiscussionSeconds = 5, RebuttalSeconds = 0, VoteSeconds = 5,
                    RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5, RoundCount = 1
                },
                CustomTopics = new[] { new ServerTopicData { Name = "통합과일", Words = new[] { "검증사과" } } }
            }, users[0].Session.SessionToken);
            var host = await Peer.ConnectAsync(room, ValidateCertificate);
            peers.Add(host);
            var hostInitial = await host.WaitStateAsync(state => state.Phase == GamePhase.Lobby);
            Check(hostInitial.IsHost, "방 생성자가 호스트여야 합니다.");
            await AssertTicketRejectedAsync(room);
            var roomB = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Settings = new ServerRoomSettings { RoomName = "분리방" + runId, Topics = new[] { "과일" } }
            }, users[4].Session.SessionToken);
            var isolated = await Peer.ConnectAsync(roomB, ValidateCertificate);
            peers.Add(isolated);
            var isolatedState = await isolated.WaitStateAsync(state => state.Phase == GamePhase.Lobby);
            Check(isolatedState.Players.Length == 1 && isolatedState.Settings.RoomName != hostInitial.Settings.RoomName,
                "방 사이의 명단·설정이 분리되어야 합니다.");
            await isolated.LeaveAsync();

            var lobbySpectatorAssignment = await JoinAsync(game, room.RoomId, users[3], true);
            var lobbySpectator = await Peer.ConnectAsync(lobbySpectatorAssignment, ValidateCertificate);
            peers.Add(lobbySpectator);
            await lobbySpectator.WaitStateAsync(state => state.Phase == GamePhase.Lobby && state.LocalIsSpectator);
            var active = new List<Peer> { host };
            for (int index = 1; index <= 2; index++)
            {
                var assignment = await JoinAsync(game, room.RoomId, users[index], false);
                var peer = await Peer.ConnectAsync(assignment, ValidateCertificate);
                peers.Add(peer);
                active.Add(peer);
                await peer.WaitStateAsync(state => state.Phase == GamePhase.Lobby);
            }
            await host.WaitStateAsync(state => state.Players.Count(player => player.IsConnected && !player.IsSpectator) == 3);
            await host.SendAsync(new GameplayEnvelope { Type = "request", Kind = "start" });
            var roles = new List<RoomSnapshot>();
            foreach (var peer in active) roles.Add(await peer.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal));
            var spectatorRole = await lobbySpectator.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal);
            Check(roles.All(state => !state.LocalIsSpectator) && spectatorRole.LocalIsSpectator && spectatorRole.Word == "",
                "먼저 입장한 관전자가 기존 참가자의 자리를 차지하면 안 됩니다.");
            Check(roles.Count(state => state.LocalIsLiar) == 1, "라이어 수는 서버가 결정해야 합니다.");
            Check(roles.All(state => state.Players.All(player => !player.IsLiar)), "공개 전 타인의 역할을 숨겨야 합니다.");
            Check(roles.All(state => state.Word == (state.LocalIsLiar ? "" : "검증사과")), "정답은 시민에게만 전달해야 합니다.");
            Report("방 분리·입장권 재사용 거부·역할과 정답 비공개·방 전용 커스텀 주제 검증");
            await lobbySpectator.DisposeAsync();

            var drawing = await host.WaitStateAsync(state => state.Phase == GamePhase.Drawing);
            var artist = active.Single(peer => peer.Latest?.LocalPlayerId == drawing.ArtistId);
            for (int index = 0; index < 4; index++)
            {
                await artist.SendAsync(new GameplayEnvelope
                {
                    Type = "stroke", Stroke = new DrawStroke
                    {
                        X1 = index * .1f, Y1 = .2f, X2 = index * .1f + .05f, Y2 = .3f,
                        Size = .01f, R = (byte)(10 + index), CanvasVersion = drawing.CanvasVersion
                    }
                });
                await Task.Delay(40);
            }
            await WaitAsync(() => active.All(peer => peer.Trace.Count(message => message.Type == "stroke") == 4), 8);
            foreach (var peer in active)
            {
                var strokes = peer.Trace.Where(message => message.Type == "stroke").ToArray();
                Check(strokes.Select(message => message.Stroke.R).SequenceEqual(new byte[] { 10, 11, 12, 13 }), "선 순서를 보존해야 합니다.");
                Check(strokes.Zip(strokes.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value), "서버 순번은 증가해야 합니다.");
            }
            var spectatorAssignment = await JoinAsync(game, room.RoomId, users[3], true);
            var spectator = await Peer.ConnectAsync(spectatorAssignment, ValidateCertificate);
            peers.Add(spectator);
            var spectatorState = await spectator.WaitStateAsync(state => state.LocalIsSpectator);
            Check(spectatorState.Word == "", "관전자에게 정답이 노출되면 안 됩니다.");
            var replay = spectator.Trace.Where(message => message.Type == "canvas").ToArray();
            Check(replay.Length > 0 && replay[0].Reset && replay.SelectMany(message => message.Strokes ?? Array.Empty<DrawStroke>())
                .Select(stroke => stroke.R).SequenceEqual(new byte[] { 10, 11, 12, 13 }), "도중 입장의 캔버스 복원이 정확해야 합니다.");
            await spectator.SendAsync(new GameplayEnvelope
            {
                Type = "stroke", Target = artist.Latest!.LocalPlayerId,
                Stroke = new DrawStroke { X1 = .1f, Y1 = .1f, X2 = .2f, Y2 = .2f, Size = .01f, R = 99, CanvasVersion = spectatorState.CanvasVersion }
            });
            await spectator.SendAsync(new GameplayEnvelope { Type = "request", Kind = "vote", Target = hostInitial.LocalPlayerId });
            await Task.Delay(500);
            Check(!active.Any(peer => peer.Trace.Any(message => message.Type == "stroke" && message.Stroke.R == 99)), "관전자의 그림 권한을 거부해야 합니다.");
            Check(spectator.Latest!.Players.Single(player => player.Id == spectatorState.LocalPlayerId).HasVoted == false,
                "관전자의 투표 권한을 거부해야 합니다.");
            Report("선 순서·도중 관전 캔버스 복원·권한 위조 거부 검증");
            await Task.Delay(1100);
            for (int index = 0; index < 80; index++)
                await artist.SendAsync(new GameplayEnvelope
                {
                    Type = "stroke", Stroke = new DrawStroke
                    {
                        X1 = .1f, Y1 = .1f, X2 = .2f, Y2 = .2f, Size = .01f, R = 77,
                        CanvasVersion = drawing.CanvasVersion
                    }
                });
            await artist.SendAsync(new GameplayEnvelope { Type = "request", Kind = "chat", Text = "연결유지" });
            await WaitAsync(() => artist.Trace.Any(message => message.Type == "chat" && message.Line.Text == "연결유지"), 5);
            Check(!artist.IsClosed && artist.Trace.Count(message => message.Type == "stroke" && message.Stroke.R == 77) <= 60,
                "순간적인 그리기 초과는 선만 제한하고 연결을 유지해야 합니다.");
            Report("그리기 속도 제한의 연결 유지 검증");

            int stableId = hostInitial.LocalPlayerId;
            bool originalRole = roles[0].LocalIsLiar;
            await host.DisposeAsync();
            await WaitAsync(() => active.Skip(1).Any(peer => peer.Latest?.IsHost == true), 5);
            var newHost = active.Skip(1).Single(peer => peer.Latest?.IsHost == true);
            string newOwner = users[active.IndexOf(newHost)].Login.AccountId;
            await WaitRoomOwnerAsync(game, users[1].Session.SessionToken, room.RoomId, newOwner);
            var reconnectAssignment = await JoinAsync(game, room.RoomId, users[0], false);
            var reconnect = await Peer.ConnectAsync(reconnectAssignment, ValidateCertificate);
            peers.Add(reconnect);
            var reconnectState = await reconnect.WaitStateAsync(state => state.LocalPlayerId >= 0);
            Check(reconnectState.LocalPlayerId == stableId && reconnectState.LocalIsLiar == originalRole && !reconnectState.LocalIsSpectator,
                "재접속은 동일한 계정의 플레이어 ID·역할을 복원해야 합니다.");
            Check(!reconnectState.IsHost, "호스트 이전 후 재접속이 소유권을 빼앗으면 안 됩니다.");
            Report("호스트 이탈·게임서버 소유권 반영·계정 기준 재접속 복원 검증");

            var malformedAssignment = await JoinAsync(game, room.RoomId, users[3], true);
            var malformed = await Peer.ConnectAsync(malformedAssignment, ValidateCertificate);
            peers.Add(malformed);
            await malformed.WaitStateAsync(state => state.LocalIsSpectator);
            await malformed.SendRawAsync("{");
            await malformed.WaitClosedAsync(5);
            var observerAssignment = await JoinAsync(game, room.RoomId, users[4], true);
            var observer = await Peer.ConnectAsync(observerAssignment, ValidateCertificate);
            peers.Add(observer);
            await observer.WaitStateAsync(state => state.LocalIsSpectator);
            await active[1].DisposeAsync();
            await active[2].DisposeAsync();
            await reconnect.WaitStateAsync(state => state.IsHost && state.Phase == GamePhase.MatchResults);
            await reconnect.SendAsync(new GameplayEnvelope { Type = "request", Kind = "lobby" });
            var lobbyState = await reconnect.WaitStateAsync(state => state.Phase == GamePhase.Lobby);
            var observerLobby = await observer.WaitStateAsync(state => state.Phase == GamePhase.Lobby);
            Check(observerLobby.LocalIsSpectator && observerLobby.Word == ""
                && lobbyState.Players.Count(player => player.IsConnected && !player.IsSpectator) == 1,
                "명시적 관전 참가자는 빈자리가 있어도 다음 대기방에서 관전을 유지해야 합니다.");
            Report("기존 참가자 좌석 보존·명시적 관전 선택의 경기 종료 후 유지 검증");
            await PostNoContentAsync(main, "/api/session/logout", new { }, users[0].Login.SessionToken);
            await reconnect.WaitClosedAsync(20);
            await AssertRejectedAsync(game, "/api/rooms", new CreateRoomRequest(), users[0].Session.SessionToken);
            Report("잘못된 프레임 종료·메인 로그아웃의 GS/DS 세션 폐기 검증");
            Console.WriteLine("DrawLiar 전체 HTTP/WebSocket 통합 검증 PASS");
        }
        finally
        {
            foreach (var peer in peers)
            {
                try { if (!peer.IsClosed) await peer.LeaveAsync(); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or TimeoutException or InvalidOperationException or ObjectDisposedException) { }
                finally { await peer.DisposeAsync(); }
            }
        }
    }

    private static async Task<TestUser> GuestAndEnterAsync(HttpClient main)
    {
        var login = await PostAsync<LoginResponse>(main, "/api/auth/guest", NewGuestRequest(), null);
        Check(!string.IsNullOrEmpty(login.AccountId) && login.Profile.IsGuest && !login.Profile.HasGoogleAccount,
            "새 게스트 계정과 프로필을 발급해야 합니다.");
        using var game = Client(login.GameServerUrl);
        var session = await PostAsync<GameSessionResponse>(game, "/api/session/enter", new EnterGameRequest { AssignmentToken = login.AssignmentToken }, null);
        Check(session.Profile.AccountId == login.AccountId, "메인과 게임서버 계정이 일치해야 합니다.");
        return new TestUser(login, session);
    }

    private static async Task VerifyOutgameAsync(HttpClient game, List<TestUser> users)
    {
        string token = users[0].Session.SessionToken;
        var shop = await GetAsync<ShopResponse>(game, "/api/shop", token);
        Check(shop.Products.Any(product => product.Id == "beret") && shop.Products.Any(product => product.Id == "brush"), "상점 상품을 조회해야 합니다.");
        string operation = Guid.NewGuid().ToString();
        var request = new PurchaseRequest { ProductId = "crown", OperationId = operation };
        int initialCoins = users[0].Login.Profile.Coins;
        var first = await PostAsync<ProfileData>(game, "/api/shop/purchase", request, token);
        var repeated = await PostAsync<ProfileData>(game, "/api/shop/purchase", request, token);
        Check(first.Coins == initialCoins - 150 && repeated.Coins == first.Coins, "동일 구매 재시도는 한 번만 차감해야 합니다.");
        using (var conflict = await SendAsync(game, "/api/shop/purchase", new PurchaseRequest { ProductId = "brush", OperationId = operation }, token))
            Check(conflict.StatusCode == HttpStatusCode.Conflict, "동일 작업 ID의 다른 상품은 거부해야 합니다.");
        var brush = await PostAsync<ProfileData>(game, "/api/shop/purchase", new PurchaseRequest
        {
            ProductId = "brush", OperationId = Guid.NewGuid().ToString()
        }, token);
        Check(brush.Coins == initialCoins - 250, "두 상품을 각각 차감해야 합니다.");
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/profile")
        {
            Content = JsonContent.Create(new UpdateProfileRequest
            {
                DisplayName = brush.DisplayName, AvatarColor = 2, Accessory = 6
            }, options: Json)
        })
        {
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await game.SendAsync(patch);
            response.EnsureSuccessStatusCode();
        }
        var saved = await GetAsync<ProfileData>(game, "/api/profile", token);
        Check(saved.Accessory == 6 && saved.AvatarColor == 2 && saved.Coins == initialCoins - 250,
            "구매한 액세서리의 조합과 커스터마이징을 저장해야 합니다.");
        await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = users[1].Login.AccountId }, token);
        var incoming = await GetAsync<FriendListResponse>(game, "/api/friends", users[1].Session.SessionToken);
        Check(incoming.Incoming.Any(friend => friend.AccountId == users[0].Login.AccountId), "친구 요청을 받은 계정에서 조회해야 합니다.");
        await PostNoContentAsync(game, "/api/friends/respond", new FriendRespondRequest
        {
            AccountId = users[0].Login.AccountId, Accept = true
        }, users[1].Session.SessionToken);
        var accepted = await GetAsync<FriendListResponse>(game, "/api/friends", token);
        Check(accepted.Friends.Any(friend => friend.AccountId == users[1].Login.AccountId), "승인한 친구를 조회해야 합니다.");
        using (var remove = new HttpRequestMessage(HttpMethod.Delete, "/api/friends/" + users[1].Login.AccountId))
        {
            remove.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await game.SendAsync(remove);
            response.EnsureSuccessStatusCode();
            Check(response.StatusCode == HttpStatusCode.NoContent, "친구 삭제는 204를 반환해야 합니다.");
        }
        var removed = await GetAsync<FriendListResponse>(game, "/api/friends", token);
        Check(!removed.Friends.Any(friend => friend.AccountId == users[1].Login.AccountId), "삭제한 친구를 제거해야 합니다.");
        Report("상점 구매의 중복 차감 방지·프로필 저장·친구 요청·승인·삭제 검증");
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidDataException("응답이 비어 있습니다: " + path);
    }

    private static Task<DedicatedAssignment> JoinAsync(HttpClient game, string roomId, TestUser user, bool spectator, string password = "") =>
        PostAsync<DedicatedAssignment>(game, "/api/rooms/" + Uri.EscapeDataString(roomId) + "/join", new JoinRoomRequest { AsSpectator = spectator, Password = password }, user.Session.SessionToken);

    private static async Task WaitRoomOwnerAsync(HttpClient game, string token, string roomId, string owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/rooms");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await game.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var rooms = await response.Content.ReadFromJsonAsync<RoomListResponse>(Json, timeout.Token);
            if (rooms?.Rooms.Any(room => room.RoomId == roomId && room.OwnerAccountId == owner) == true) return;
            await Task.Delay(1000, timeout.Token);
        }
    }

    private static async Task AssertTicketRejectedAsync(DedicatedAssignment room)
    {
        var duplicate = await Peer.ConnectAsync(room, ValidateCertificate);
        await using (duplicate) await duplicate.WaitClosedAsync(5);
        Check(duplicate.Latest == null, "사용한 입장권으로 명단을 받으면 안 됩니다.");
    }

    private static HttpClient Client(string url)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) => ValidateCertificate(null!, certificate, chain, errors);
        return new HttpClient(handler) { BaseAddress = Endpoint(url), Timeout = TimeSpan.FromSeconds(15) };
    }

    internal static Uri Endpoint(string url)
    {
        var uri = new Uri(url);
        if (Environment.GetEnvironmentVariable("DRAWLIAR_USE_SSH_TUNNEL") != "1" || uri.Host != "34.158.195.192") return uri;
        string? offsetSetting = Environment.GetEnvironmentVariable("DRAWLIAR_TEST_TUNNEL_PORT_OFFSET");
        int offset = string.IsNullOrEmpty(offsetSetting) ? 0 : int.Parse(offsetSetting);
        int port = uri.Port + offset;
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(offset));
        var tunnel = new UriBuilder(uri) { Host = "127.0.0.1", Port = port };
        if (Interlocked.Exchange(ref _tunnelLogged, 1) == 0) Console.WriteLine("TEST_TUNNEL");
        return tunnel.Uri;
    }

    internal static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        _pin.Length == 0 ? errors == SslPolicyErrors.None : certificate != null
            && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == _pin;

    private static async Task RequireHealthAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body, string? token)
    {
        using var response = await SendAsync(client, path, body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidDataException("응답이 비어 있습니다: " + path);
    }

    private static async Task PostNoContentAsync(HttpClient client, string path, object body, string? token)
    {
        using var response = await SendAsync(client, path, body, token);
        response.EnsureSuccessStatusCode();
        if (path == "/api/session/logout" || path == "/api/friends/request" || path == "/api/friends/respond" || path == "/api/friends/cancel")
            Check(response.StatusCode == HttpStatusCode.NoContent, "완료 응답은 204를 반환해야 합니다: " + path);
    }

    private static async Task AssertRejectedAsync(HttpClient client, string path, object body, string? token)
    {
        using var response = await SendAsync(client, path, body, token);
        Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.BadRequest,
            "위조·폐기 인증은 거부해야 합니다: " + path);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task WaitAsync(Func<bool> predicate, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!predicate()) await Task.Delay(100, timeout.Token);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Report(string message) => Console.WriteLine("PASS: " + message);
    private sealed record TestUser(LoginResponse Login, GameSessionResponse Session);
}

internal sealed class Peer : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private int _disposed;
    private RoomSnapshot? _latest;

    public ConcurrentQueue<GameplayEnvelope> Trace { get; } = new();
    public RoomSnapshot? Latest => Volatile.Read(ref _latest);
    public bool IsClosed => _closed.Task.IsCompleted;
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;
    public string? CloseDescription => _socket.CloseStatusDescription;

    public static async Task<Peer> ConnectAsync(DedicatedAssignment assignment, RemoteCertificateValidationCallback certificateValidation)
    {
        var peer = new Peer();
        peer._socket.Options.RemoteCertificateValidationCallback = certificateValidation;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await peer._socket.ConnectAsync(Integration.Endpoint(assignment.DedicatedUrl), timeout.Token);
        await peer.SendAsync(new GameplayEnvelope { Type = "hello", Ticket = assignment.JoinTicket, RoomId = assignment.RoomId });
        peer._reader = peer.ReadAsync();
        return peer;
    }

    public Task SendAsync(GameplayEnvelope message) => SendRawAsync(JsonSerializer.Serialize(message, Integration.Json));
    public async Task SendRawAsync(string json) => await _socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(),
        WebSocketMessageType.Text, true, _lifetime.Token);

    public async Task<RoomSnapshot> WaitStateAsync(Func<RoomSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (Latest is not { } state || !predicate(state))
        {
            if (_closed.Task.IsCompleted) throw new InvalidOperationException("상태를 받기 전에 소켓이 닫혔습니다.");
            await Task.Delay(20, timeout.Token);
        }
        return Latest!;
    }

    public async Task WaitClosedAsync(int seconds) => await _closed.Task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public async Task LeaveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "leave", timeout.Token);
        await WaitClosedAsync(5);
        await DisposeAsync();
    }

    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[4096];
            while (!_lifetime.IsCancellationRequested)
            {
                using var data = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    data.Write(buffer, 0, result.Count);
                    if (data.Length > 65536) throw new InvalidDataException("서버 프레임이 허용 범위를 초과했습니다.");
                } while (!result.EndOfMessage);
                var message = JsonSerializer.Deserialize<GameplayEnvelope>(data.GetBuffer().AsSpan(0, (int)data.Length), Integration.Json)!;
                Trace.Enqueue(message);
                if (message.Type == "state") Volatile.Write(ref _latest, message.State);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _closed.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _socket.Abort();
        await _reader;
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
