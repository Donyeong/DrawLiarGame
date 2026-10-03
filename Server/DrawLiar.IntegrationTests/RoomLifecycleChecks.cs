using System.Net;
using System.Net.Http.Json;
using DrawLiar;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyRoomLifecycleAsync(HttpClient main)
    {
        string runId = Guid.NewGuid().ToString("N")[..8];
        var users = new List<TestUser>();
        var peers = new List<Peer>();
        for (int index = 0; index < 10; index++) users.Add(await GuestAndEnterAsync(main));
        using var game = Client(users[0].Login.GameServerUrl);
        string token = users[0].Session.SessionToken;
        try
        {
            var room = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Settings = new ServerRoomSettings
                {
                    RoomName = "수명검증" + runId, MaxPlayers = 8, Topics = new[] { "과일" },
                    RoleSeconds = 3, DrawSeconds = 180
                }
            }, token);
            Check(Guid.TryParse(room.RoomId, out _) && RoomCodes.Normalize(room.RoomCode) == room.RoomCode,
                "내부 UUID와 사람이 입력하는 여섯 자리 코드를 함께 발급해야 합니다.");
            var host = await Peer.ConnectAsync(room, ValidateCertificate);
            peers.Add(host);
            var initial = await host.WaitStateAsync(state => state.IsHost);
            await WaitRoomAsync(game, token, room.RoomId, data => data?.PlayerCount == 1);
            string formatted = " " + room.RoomCode[..3].ToLowerInvariant() + "- " + room.RoomCode[3..].ToLowerInvariant() + " ";
            var claims = await Task.WhenAll(Enumerable.Range(1, 8).Select(async index =>
            {
                using var response = await SendAsync(game, "/api/rooms/" + Uri.EscapeDataString(formatted) + "/join",
                    new JoinRoomRequest(), users[index].Session.SessionToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
                    Check(error?.Code == "RoomFull", "동시 입장의 정원 초과만 거부해야 합니다.");
                    return (Index: index, Assignment: (DedicatedAssignment?)null);
                }
                response.EnsureSuccessStatusCode();
                return (Index: index, Assignment: await response.Content.ReadFromJsonAsync<DedicatedAssignment>(Json));
            }));
            Check(claims.Count(claim => claim.Assignment != null) == 7, "대기 중인 입장권도 정원에 포함해 일곱 자리만 예약해야 합니다.");
            int spareIndex = claims.Single(claim => claim.Assignment == null).Index;
            var active = new List<Peer> { host };
            var admittedPeers = await Task.WhenAll(claims.Where(claim => claim.Assignment != null).Select(async claim =>
            {
                Check(claim.Assignment!.RoomId == room.RoomId && claim.Assignment.RoomCode == room.RoomCode,
                    "정규화한 코드가 동일한 UUID의 방으로 연결되어야 합니다.");
                var peer = await Peer.ConnectAsync(claim.Assignment, ValidateCertificate);
                await peer.WaitStateAsync(state => !state.LocalIsSpectator);
                return peer;
            }));
            peers.AddRange(admittedPeers);
            active.AddRange(admittedPeers);
            using (var extra = await SendAsync(game, "/api/rooms/" + room.RoomCode + "/join", new JoinRoomRequest(), users[spareIndex].Session.SessionToken))
                Check(extra.StatusCode == HttpStatusCode.Conflict, "입장권 교환과 heartbeat 사이에도 정원 초과 배정을 막아야 합니다.");
            await host.WaitStateAsync(state => state.Players.Count(player => player.IsConnected && !player.IsSpectator) == 8);
            // 기존 UUID 입장 주소도 유지한다.
            var observerAssignment = await JoinAsync(game, room.RoomId, users[9], true);
            var observer = await Peer.ConnectAsync(observerAssignment, ValidateCertificate);
            peers.Add(observer);
            await observer.WaitStateAsync(state => state.LocalIsSpectator);
            await WaitRoomAsync(game, token, room.RoomId, data => data?.PlayerCount == 8 && data.SpectatorCount == 1);
            await host.SendAsync(new GameplayEnvelope { Type = "request", Kind = "start" });
            var roles = await host.WaitStateAsync(state => state.Phase == GamePhase.RoleReveal);
            await host.DisposeAsync();
            await active[1].WaitStateAsync(state => !state.Players.Single(player => player.Id == initial.LocalPlayerId).IsConnected);
            var reconnectAssignment = await JoinAsync(game, room.RoomCode, users[0], false);
            var reconnect = await Peer.ConnectAsync(reconnectAssignment, ValidateCertificate);
            peers.Add(reconnect);
            var restored = await reconnect.WaitStateAsync(state => !state.LocalIsSpectator);
            Check(restored.LocalPlayerId == initial.LocalPlayerId && restored.LocalIsLiar == roles.LocalIsLiar,
                "짧은 코드의 재접속에서도 기존 플레이어 ID와 역할을 유지해야 합니다.");
            active[0] = reconnect;
            var pending = await JoinAsync(game, room.RoomCode, users[spareIndex], true);
            await Task.WhenAll(active.Select(peer => peer.LeaveAsync()));
            await WaitRoomAsync(game, token, room.RoomId, data => data?.PlayerCount == 0 && data.SpectatorCount == 1);
            await observer.LeaveAsync();
            await WaitRoomAsync(game, token, room.RoomId, data => data == null);
            var reserved = await Peer.ConnectAsync(pending, ValidateCertificate);
            peers.Add(reserved);
            await reserved.WaitStateAsync(state => state.LocalIsSpectator);
            await WaitRoomAsync(game, token, room.RoomId, data => data?.SpectatorCount == 1);
            await reserved.LeaveAsync();
            await WaitRoomAsync(game, token, room.RoomId, data => data == null);
            Report("짧은 코드·UUID 호환·8명 동시 좌석 예약·관전자 잔류·빈방 숨김·입장권 보호·역할 재접속 검증");
            using (var closed = await SendAsync(game, "/api/rooms/" + room.RoomCode + "/join", new JoinRoomRequest(), token))
                Check(closed.StatusCode == HttpStatusCode.NotFound, "완료된 입장 lease는 즉시 해제하고 정상 퇴장한 빈방을 종료해야 합니다.");
            var abandoned = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Settings = new ServerRoomSettings { RoomName = "재접속검증" + runId, Topics = new[] { "과일" }, MaxPlayers = 8 }
            }, token);
            Check(abandoned.RoomCode != room.RoomCode, "별도 방은 독립된 코드를 가져야 합니다.");
            var disconnected = await Peer.ConnectAsync(abandoned, ValidateCertificate);
            peers.Add(disconnected);
            await disconnected.WaitStateAsync(state => state.IsHost);
            await WaitRoomAsync(game, token, abandoned.RoomId, data => data?.PlayerCount == 1);
            await disconnected.DisposeAsync();
            await WaitRoomAsync(game, token, abandoned.RoomId, data => data == null);
            var returnTicket = await JoinAsync(game, abandoned.RoomCode, users[0], false);
            var returned = await Peer.ConnectAsync(returnTicket, ValidateCertificate);
            peers.Add(returned);
            await returned.WaitStateAsync(state => state.IsHost);
            await returned.LeaveAsync();
            await WaitRoomAsync(game, token, abandoned.RoomId, data => data == null);
            var fresh = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
            {
                Settings = new ServerRoomSettings { RoomName = "즉시생성검증" + runId, Topics = new[] { "과일" }, MaxPlayers = 8 }
            }, token);
            var freshHost = await Peer.ConnectAsync(fresh, ValidateCertificate);
            peers.Add(freshHost);
            await freshHost.WaitStateAsync(state => state.IsHost);
            await freshHost.LeaveAsync();
            await WaitRoomAsync(game, token, fresh.RoomId, data => data == null);
            Report("정상 퇴장 직후 같은 호스트의 새 방 생성·비정상 단절 빈방 숨김과 재접속 복원 검증");
            Console.WriteLine("DrawLiar 방 코드·수명 HTTP/WebSocket 통합 검증 PASS");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
    }

    private static async Task WaitRoomAsync(HttpClient game, string token, string roomId, Func<ServerRoomData?, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var rooms = await GetAsync<RoomListResponse>(game, "/api/rooms", token);
            if (predicate(rooms.Rooms.SingleOrDefault(room => room.RoomId == roomId))) return;
            await Task.Delay(200, timeout.Token);
        }
    }
}
