using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyDrawingHistoryAsync()
    {
        await VerifyLobbyDrawingAsync(DrawingMode.Relay);
        await VerifyLobbyDrawingAsync(DrawingMode.Individual);
        await VerifyRelayDrawingAsync();
        await VerifyIndividualDrawingAsync();
        await VerifyDrawingBatchAsync();
        Report("대기실 공동 낙서·시작 초기화·서버 작가 지정·자기 선 삭제·개인 그림 조회·재접속·epoch 정리·64KiB 배치 검증");
    }

    private static async Task VerifyLobbyDrawingAsync(DrawingMode mode)
    {
        await using var fixture = new DrawingFixture(mode);
        await fixture.FlushAsync();
        int host = fixture.Host.PlayerId;
        int second = fixture.State.Players.First(player => !player.IsSpectator && player.Id != host).Id;
        int third = fixture.State.Players.First(player => !player.IsSpectator && player.Id != host && player.Id != second).Id;
        int version = fixture.State.CanvasVersion, epoch = fixture.State.DrawingEpoch;
        Check(fixture.State.Phase == GamePhase.Lobby && fixture.State.Round == 0 && fixture.State.DrawingOrder.Length == 0,
            "대기실 낙서는 경기의 라운드와 그리기 순서를 만들면 안 됩니다.");
        fixture.Paint(fixture.Host, spoofAuthor: second);
        fixture.Paint(fixture.Connection(second), eraser: true, spoofAuthor: host);
        fixture.Paint(fixture.Connection(third));
        await fixture.FlushAsync();
        var strokes = fixture.Socket(host).Frames.Where(frame => frame.Type == "stroke").Select(frame => frame.Stroke).ToArray();
        Check(strokes.Length == 3 && strokes.Select(stroke => stroke.AuthorPlayerId).SequenceEqual(new[] { host, second, third })
            && strokes.All(stroke => stroke.CanvasVersion == version) && !strokes[1].Eraser && strokes[1].R == 255,
            "대기실은 방식과 차례에 관계없이 일반 참가자가 같은 종이에 그리고 서버가 실제 작가를 지정해야 합니다.");
        fixture.Paint(fixture.Observer);
        fixture.Room.Receive(fixture.Host, new GameplayEnvelope { Type = "stroke", Stroke = DrawingFixture.Stroke(version - 1) }, fixture.Now += .02);
        await fixture.FlushAsync();
        Check(fixture.Socket(host).Frames.Count(frame => frame.Type == "stroke") == 3,
            "대기실에서도 관전자와 오래된 캔버스 버전의 선을 거부해야 합니다.");
        foreach (var invalid in new[]
        {
            new GameplayEnvelope { Type = "request", Kind = "clearOwn", Target = second, Version = version, Round = 0, DrawingEpoch = epoch },
            new GameplayEnvelope { Type = "request", Kind = "clearOwn", Target = host, Version = version - 1, Round = 0, DrawingEpoch = epoch },
            new GameplayEnvelope { Type = "request", Kind = "clearOwn", Target = host, Version = version, Round = 1, DrawingEpoch = epoch },
            new GameplayEnvelope { Type = "request", Kind = "clearOwn", Target = host, Version = version, Round = 0, DrawingEpoch = epoch + 1 }
        }) fixture.Send(fixture.Host, invalid);
        fixture.Send(fixture.Observer, fixture.Request("clearOwn", fixture.Observer.PlayerId));
        await fixture.FlushAsync();
        Check(fixture.Socket(host).Frames.All(frame => frame.Type != "clearOwn"),
            "대기실에서 다른 사람의 선과 관전자 삭제, 버전·라운드·epoch 위조를 거부해야 합니다.");
        fixture.Send(fixture.Host, fixture.Request("clearOwn", host));
        fixture.Paint(fixture.Connection(second));
        await fixture.FlushAsync();
        var late = await fixture.JoinAsync("lobby-late", false);
        var replay = late.Socket.Frames.Where(frame => frame.Type == "canvas").SelectMany(frame => frame.Strokes).ToArray();
        Check(replay.Length == 3 && replay.Count(stroke => stroke.AuthorPlayerId == second) == 2
            && replay.Single(stroke => stroke.AuthorPlayerId != second).AuthorPlayerId == third,
            "대기실 중도 입장은 개인 그리기 설정에서도 공동 낙서 전체와 삭제 결과를 받아야 합니다.");
        var oldSecond = fixture.Connection(second);
        fixture.Room.Disconnect(oldSecond, fixture.Now += .02);
        await oldSecond.DisposeAsync();
        var rejoined = await fixture.JoinAsync(oldSecond.AccountId, false);
        Check(rejoined.Connection.PlayerId == second
            && rejoined.Socket.Frames.Where(frame => frame.Type == "canvas").SelectMany(frame => frame.Strokes).Count() == 3,
            "대기실 재접속은 종이에 남아 있는 작가 ID를 복원하고 공동 낙서를 빠짐없이 받아야 합니다.");
        fixture.Send(rejoined.Connection, fixture.Request("clearOwn", second));
        await fixture.FlushAsync();
        Check(rejoined.Socket.Frames.Last(frame => frame.Type == "clearOwn").Target == second,
            "대기실 재접속 후 이전에 그린 자기 선도 삭제할 수 있어야 합니다.");
        var remaining = await fixture.JoinAsync("lobby-observer", true);
        Check(remaining.Socket.Frames.Where(frame => frame.Type == "canvas").SelectMany(frame => frame.Strokes).Single().AuthorPlayerId == third,
            "자기 선 삭제는 다른 대기실 참가자의 그림을 보존해야 합니다.");
        string unavailable = fixture.RequestHistory(remaining.Connection, third);
        await fixture.FlushAsync();
        Check(remaining.Socket.Frames.Single(frame => frame.RequestId == unavailable).Code == "DrawingUnavailable",
            "대기실 공동 낙서는 별도 경기 작가 조회 권한을 만들면 안 됩니다.");
        var departed = fixture.Connection(third);
        fixture.Room.Disconnect(departed, fixture.Now += .02);
        await departed.DisposeAsync();
        await fixture.StartAsync();
        var canvas = (List<DrawStroke>)typeof(DedicatedRoom).GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Room)!;
        var history = (List<DrawStroke>)typeof(DedicatedRoom).GetField("_drawingHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Room)!;
        var authors = (Dictionary<string, int>)typeof(DedicatedRoom).GetField("_playerIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Room)!;
        Check(fixture.State.Round == 1 && fixture.State.DrawingEpoch > epoch && fixture.State.CanvasVersion > version
            && canvas.Count == 0 && history.Count == 0 && !authors.ContainsKey(departed.AccountId),
            "경기를 시작하면 대기실 종이·그림 기록·퇴장한 작가 보존값을 모두 정리해야 합니다.");
        int activeArtist = fixture.State.ArtistId;
        int previous = fixture.Socket(host).Frames.Count(frame => frame.Type == "stroke");
        fixture.Room.Receive(fixture.Connection(activeArtist), new GameplayEnvelope { Type = "stroke", Stroke = DrawingFixture.Stroke(version) }, fixture.Now += .02);
        fixture.Paint(fixture.Connection(fixture.State.Players.First(player => !player.IsSpectator && player.Id != activeArtist).Id));
        await fixture.FlushAsync();
        Check(fixture.Socket(host).Frames.Count(frame => frame.Type == "stroke") == previous,
            "경기 시작 뒤에는 대기실 버전의 선과 현재 차례가 아닌 참가자의 그림을 거부해야 합니다.");
        fixture.Paint(fixture.Connection(activeArtist));
        for (int index = 0; index < 20 && fixture.Room.Status().IsInProgress; index++) { fixture.Now += 1000; fixture.Room.Tick(fixture.Now); }
        await fixture.FlushAsync();
        Check(fixture.State.Phase == GamePhase.MatchResults, "대기실 복귀 검증을 위해 경기를 완료해야 합니다.");
        fixture.Send(fixture.Host, new GameplayEnvelope { Type = "request", Kind = "lobby" });
        await fixture.FlushAsync();
        Check(fixture.State.Phase == GamePhase.Lobby && fixture.State.Round == 0 && canvas.Count == 0 && history.Count == 0,
            "경기에서 대기실로 돌아오면 이전 경기 그림과 기록 없이 새 공동 종이를 열어야 합니다.");
        fixture.Paint(fixture.Host);
        await fixture.FlushAsync();
        Check(canvas.Count == 1 && canvas[0].AuthorPlayerId == host, "대기실 복귀 후에도 일반 참가자가 즉시 낙서할 수 있어야 합니다.");
        await fixture.StartAsync();
        Check(canvas.Count == 0 && history.Count == 0, "다음 경기 시작 때도 대기실 낙서를 다시 비워야 합니다.");
    }

    private static async Task VerifyRelayDrawingAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Relay);
        await fixture.StartAsync();
        int first = fixture.State.ArtistId;
        Check(fixture.State.DrawingOrder.Length == 3 && fixture.State.DrawingOrder.Distinct().Count() == 3
            && !fixture.State.DrawingOrder.Contains(fixture.Observer.PlayerId), "순서는 관전자를 제외한 현재 라운드 참가자여야 합니다.");
        var expectedOrder = fixture.State.DrawingOrder.ToArray();
        fixture.State.DrawingOrder[0] = 999;
        await fixture.RefreshAsync();
        Check(fixture.State.DrawingOrder.SequenceEqual(expectedOrder), "공개 순서 배열을 수정해도 서버 순서가 바뀌면 안 됩니다.");
        var artist = fixture.Connection(first);
        fixture.Paint(artist, true, 999);
        await fixture.FlushAsync();
        var stroke = fixture.Socket(first).Frames.Last(frame => frame.Type == "stroke").Stroke;
        Check(stroke.AuthorPlayerId == first && !stroke.Eraser && stroke.R == 255 && stroke.G == 255 && stroke.B == 255,
            "작가 위조를 무시하고 지우개 요청은 흰색 선으로 처리해야 합니다.");
        int strokeCount = fixture.Socket(first).Frames.Count(frame => frame.Type == "stroke");
        fixture.Paint(fixture.Observer);
        fixture.Paint(fixture.Connection(expectedOrder.First(id => id != first)));
        await fixture.FlushAsync();
        Check(fixture.Socket(first).Frames.Count(frame => frame.Type == "stroke") == strokeCount, "관전자와 다른 차례 참가자는 그릴 수 없습니다.");
        foreach (var invalid in new[]
        {
            withTarget(fixture.Request("clearOwn", first), expectedOrder.First(id => id != first)),
            withVersion(fixture.Request("clearOwn", first), fixture.State.CanvasVersion - 1),
            withRound(fixture.Request("clearOwn", first), fixture.State.Round + 1),
            withEpoch(fixture.Request("clearOwn", first), fixture.State.DrawingEpoch + 1)
        }) fixture.Send(artist, invalid);
        await fixture.FlushAsync();
        Check(!fixture.Socket(first).Frames.Any(frame => frame.Type == "clearOwn"), "위조 대상과 오래된 버전·라운드·epoch 삭제를 거부해야 합니다.");
        fixture.Send(artist, fixture.Request("clearOwn", first));
        await fixture.FlushAsync();
        Check(fixture.Socket(first).Frames.Count(frame => frame.Type == "clearOwn") == 1, "현재 작가는 자기 선을 삭제할 수 있어야 합니다.");
        fixture.Paint(artist);
        fixture.Send(artist, new GameplayEnvelope { Type = "request", Kind = "endTurn" });
        await fixture.FlushAsync();
        int second = fixture.State.ArtistId;
        Check(second != first && fixture.State.CanvasVersion == stroke.CanvasVersion, "릴레이는 다음 작가에게 같은 캔버스를 유지해야 합니다.");
        fixture.Paint(fixture.Connection(second));
        fixture.Paint(fixture.Connection(second));
        fixture.Send(fixture.Connection(second), fixture.Request("clearOwn", second));
        await fixture.FlushAsync();
        var replay = await fixture.JoinAsync("late", true);
        Check(replay.Socket.Frames.Where(frame => frame.Type == "canvas").SelectMany(frame => frame.Strokes).Single().AuthorPlayerId == first,
            "자기 선 삭제는 이전 작가 선을 보존하고 중도 관전자에게 같은 결과를 재생해야 합니다.");
        var restored = await fixture.JoinAsync(fixture.Connection(second).AccountId, false);
        Check(restored.Connection.PlayerId == second && restored.Socket.Frames.Where(frame => frame.Type == "canvas").SelectMany(frame => frame.Strokes).Count() == 1,
            "재접속은 같은 작가 ID를 복원하고 삭제된 선을 다시 보내면 안 됩니다.");
        string emptyId = fixture.RequestHistory(replay.Connection, second);
        await fixture.FlushAsync();
        var empty = replay.Socket.Frames.Single(frame => frame.Type == "authorDrawing" && frame.RequestId == emptyId);
        Check(empty.Accepted && empty.Complete && empty.Reset && empty.Strokes.Length == 0, "빈 작가 그림도 완료 응답을 보내야 합니다.");

        fixture.Now += 2;
        int notices = replay.Socket.Frames.Count(frame => frame.Type == "notice");
        for (int index = 0; index < 10; index++) fixture.Room.Receive(replay.Connection,
            new GameplayEnvelope { Type = "request", Kind = "unknown" }, fixture.Now);
        fixture.Room.Receive(replay.Connection, fixture.Request("clearOwn", second), fixture.Now);
        fixture.Room.Receive(replay.Connection, fixture.Request("authorDrawing", first, "limited"), fixture.Now);
        await fixture.FlushAsync();
        Check(replay.Socket.Frames.Count(frame => frame.Type == "notice") == notices + 11
            && replay.Socket.Frames.Any(frame => frame.Type == "authorDrawing" && frame.RequestId == "limited" && frame.Code == "RateLimited" && frame.Complete),
            "제한된 자기 선 삭제와 그림 조회는 요청자에게 실패를 알려야 합니다.");
        Check(!fixture.Socket(first).Frames.Any(frame => frame.RequestId == "limited"), "그림 조회 실패는 다른 참가자에게 전달하면 안 됩니다.");

        GameplayEnvelope withTarget(GameplayEnvelope request, int value) { request.Target = value; return request; }
        GameplayEnvelope withVersion(GameplayEnvelope request, int value) { request.Version = value; return request; }
        GameplayEnvelope withRound(GameplayEnvelope request, int value) { request.Round = value; return request; }
        GameplayEnvelope withEpoch(GameplayEnvelope request, int value) { request.DrawingEpoch = value; return request; }
    }

    private static async Task VerifyIndividualDrawingAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Individual);
        await fixture.StartAsync();
        int first = fixture.State.ArtistId, oldEpoch = fixture.State.DrawingEpoch, oldVersion = fixture.State.CanvasVersion;
        fixture.Paint(fixture.Connection(first));
        fixture.Send(fixture.Connection(first), new GameplayEnvelope { Type = "request", Kind = "endTurn" });
        await fixture.FlushAsync();
        int second = fixture.State.ArtistId;
        Check(fixture.State.CanvasVersion > oldVersion && fixture.State.DrawingEpoch == oldEpoch, "개인 모드는 차례마다 캔버스만 교체하고 라운드 epoch는 유지해야 합니다.");
        var late = await fixture.JoinAsync("late", true);
        Check(late.Socket.Frames.Where(frame => frame.Type == "canvas").All(frame => frame.Strokes.Length == 0)
            && late.Socket.Frames.All(frame => frame.Type != "authorDrawing"), "중도 입장은 현재 캔버스만 받고 이전 개인 그림을 일괄 받으면 안 됩니다.");
        string historyId = fixture.RequestHistory(late.Connection, first);
        await fixture.FlushAsync();
        var history = late.Socket.Frames.Single(frame => frame.Type == "authorDrawing" && frame.RequestId == historyId);
        Check(history.Accepted && history.Reset && history.Complete && history.Strokes.Single().CanvasVersion == oldVersion
            && history.Strokes[0].AuthorPlayerId == first, "현재 라운드 이전 작가의 그림을 요청 시 복원해야 합니다.");
        fixture.Paint(fixture.Connection(second));
        fixture.Send(fixture.Connection(second), fixture.Request("clearOwn", second));
        string preservedId = fixture.RequestHistory(late.Connection, first);
        await fixture.FlushAsync();
        Check(late.Socket.Frames.Single(frame => frame.Type == "authorDrawing" && frame.RequestId == preservedId).Strokes.Length == 1,
            "현재 작가 삭제는 이전 개인 그림을 보존해야 합니다.");
        fixture.Paint(fixture.Connection(second));
        fixture.Room.Tick(fixture.Now + fixture.State.RemainingSeconds);
        fixture.Now += fixture.State.RemainingSeconds;
        await fixture.FlushAsync();
        int afterDeadline = fixture.Socket(first).Frames.Count(frame => frame.Type == "stroke");
        fixture.Room.Receive(fixture.Connection(second), new GameplayEnvelope { Type = "stroke", Stroke = DrawingFixture.Stroke(oldVersion) }, fixture.Now);
        await fixture.FlushAsync();
        Check(fixture.Socket(first).Frames.Count(frame => frame.Type == "stroke") == afterDeadline, "차례 기한 이후의 선을 거부해야 합니다.");
        for (int index = 0; index < 20 && fixture.Room.Status().IsInProgress; index++) { fixture.Now += 1000; fixture.Room.Tick(fixture.Now); }
        await fixture.FlushAsync();
        Check(fixture.State.Phase == GamePhase.MatchResults, "새 경기의 epoch 검증을 위해 경기를 종료해야 합니다.");
        fixture.Send(fixture.Host, new GameplayEnvelope { Type = "request", Kind = "lobby" });
        await fixture.FlushAsync();
        Check(fixture.State.DrawingOrder.Length == 0, "대기실에는 이전 라운드 순서를 남기면 안 됩니다.");
        await fixture.StartAsync();
        Check(fixture.State.Round == 1 && fixture.State.DrawingEpoch > oldEpoch, "새 경기 1라운드는 이전 경기와 다른 epoch를 사용해야 합니다.");
        var stale = fixture.Request("authorDrawing", first, "old-epoch"); stale.DrawingEpoch = oldEpoch;
        fixture.Send(late.Connection, stale);
        string cleaned = fixture.RequestHistory(late.Connection, first);
        await fixture.FlushAsync();
        Check(late.Socket.Frames.Single(frame => frame.RequestId == "old-epoch").Code == "DrawingUnavailable"
            && late.Socket.Frames.Single(frame => frame.RequestId == cleaned).Strokes.Length == 0,
            "같은 라운드 번호여도 이전 경기 조회를 거부하고 지난 그림을 비워야 합니다.");
    }

    private static async Task VerifyDrawingBatchAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Individual);
        await fixture.StartAsync();
        int author = fixture.State.ArtistId;
        var history = (List<DrawStroke>)typeof(DedicatedRoom).GetField("_drawingHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Room)!;
        history.AddRange(Enumerable.Range(0, GameRules.MAX_CANVAS_STROKES).Select(_ =>
        {
            var stroke = DrawingFixture.Stroke(fixture.State.CanvasVersion);
            stroke.AuthorPlayerId = author; stroke.X1 = .123456789f; stroke.Y1 = .987654321f;
            stroke.X2 = .00000011920929f; stroke.Y2 = .999999881f; stroke.Size = .0123456789f;
            stroke.R = stroke.G = stroke.B = 255; return stroke;
        }));
        string requestId = fixture.RequestHistory(fixture.Observer, author);
        await fixture.FlushAsync();
        var batches = fixture.Socket(fixture.Observer.PlayerId).Frames.Where(frame => frame.Type == "authorDrawing" && frame.RequestId == requestId).ToArray();
        Check(batches.Length == 47 && batches.Sum(frame => frame.Strokes.Length) == GameRules.MAX_CANVAS_STROKES
            && batches[0].Reset && batches[^1].Complete && batches.Skip(1).All(frame => !frame.Reset)
            && batches.SkipLast(1).All(frame => !frame.Complete), "최대 작가 그림은 256개씩 순서대로 시작·완료 프레임을 보내야 합니다.");
        Check(batches.All(frame => frame.Strokes.Length <= 256 && JsonSerializer.SerializeToUtf8Bytes(frame, GameplayWire.Json).Length <= GameplayWire.MAX_FRAME_BYTES)
            && fixture.Observer.IsAlive, "그림 조회는 서버의 64KiB 프레임 한도와 출력 큐를 초과하여 연결을 끊으면 안 됩니다.");
    }

    private sealed class DrawingFixture : IAsyncDisposable
    {
        private readonly List<(GameConnection Connection, DrawingSocket Socket)> _peers = new();
        private readonly ServerRoomData _roomData;
        public DedicatedRoom Room { get; }
        public GameConnection Host => _peers[0].Connection;
        public GameConnection Observer => _peers[3].Connection;
        public RoomSnapshot State => _peers[0].Socket.LastState!;
        public double Now;
        public DrawingFixture(DrawingMode mode)
        {
            _roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "host", Settings = new ServerRoomSettings
            { Mode = (int)mode, Topics = new[] { "과일" }, RoleSeconds = 3, DrawSeconds = 180, DiscussionSeconds = 5, RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5, RoundCount = 1 } };
            Room = new DedicatedRoom(_roomData, new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } }, Array.Empty<ServerTopicData>(), 0, () => { });
            foreach (string account in new[] { "host", "second", "third", "observer" }) Add(account, account == "observer");
        }
        private (GameConnection Connection, DrawingSocket Socket) Add(string account, bool spectator)
        {
            var socket = new DrawingSocket(); var connection = new GameConnection(socket, account, "test", CancellationToken.None);
            Check(Room.Join(connection, new RedeemTicketResponse { AccountId = account, Room = _roomData, IsSpectator = spectator, SpectatorOnly = spectator,
                Profile = new ProfileData { AccountId = account, DisplayName = account } }, Now), "그림 검증 참가자가 입장해야 합니다.");
            var peer = (connection, socket); _peers.Add(peer); return peer;
        }
        public async Task<(GameConnection Connection, DrawingSocket Socket)> JoinAsync(string account, bool spectator)
        { Now += 1.1; var peer = Add(account, spectator); await FlushAsync(); return peer; }
        public GameConnection Connection(int playerId) => _peers.Last(peer => peer.Connection.PlayerId == playerId).Connection;
        public DrawingSocket Socket(int playerId) => _peers.Last(peer => peer.Connection.PlayerId == playerId).Socket;
        public void Send(GameConnection connection, GameplayEnvelope envelope) { Now += 1.1; Room.Receive(connection, envelope, Now); }
        public void Paint(GameConnection connection, bool eraser = false, int spoofAuthor = 0)
        { var stroke = Stroke(State.CanvasVersion); stroke.Eraser = eraser; stroke.AuthorPlayerId = spoofAuthor; Now += .02; Room.Receive(connection, new GameplayEnvelope { Type = "stroke", Stroke = stroke }, Now); }
        public static DrawStroke Stroke(int version) => new() { X1 = .1f, Y1 = .2f, X2 = .3f, Y2 = .4f, Size = .01f, R = 12, G = 34, B = 56, CanvasVersion = version };
        public GameplayEnvelope Request(string kind, int target, string requestId = "") => new()
        { Type = "request", Kind = kind, Target = target, Version = State.CanvasVersion, Round = State.Round, DrawingEpoch = State.DrawingEpoch, RequestId = requestId };
        public string RequestHistory(GameConnection connection, int author)
        { string id = Guid.NewGuid().ToString("N"); Send(connection, Request("authorDrawing", author, id)); return id; }
        public async Task StartAsync()
        { Send(Host, new GameplayEnvelope { Type = "request", Kind = "start" }); await FlushAsync(); Now += 3; Room.Tick(Now); await FlushAsync(); Check(State.Phase == GamePhase.Drawing, "그리기 단계를 준비해야 합니다."); }
        public async Task RefreshAsync() { Now += 1.1; Room.Tick(Now); await FlushAsync(); }
        public async Task FlushAsync()
        {
            string id = Guid.NewGuid().ToString("N");
            var live = _peers.Where(peer => peer.Connection.IsAlive).ToArray();
            foreach (var peer in live) Check(peer.Connection.Queue(new GameplayEnvelope { Type = "barrier", RequestId = id }), "검증 프레임을 전송할 수 있어야 합니다.");
            await WaitAsync(() => live.All(peer => peer.Socket.Frames.Any(frame => frame.Type == "barrier" && frame.RequestId == id)), 3);
        }
        public async ValueTask DisposeAsync() { foreach (var peer in _peers) await peer.Connection.DisposeAsync(); }
    }

    private sealed class DrawingSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private RoomSnapshot? _lastState;
        public readonly ConcurrentQueue<GameplayEnvelope> Frames = new();
        public RoomSnapshot? LastState => Volatile.Read(ref _lastState);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken cancellationToken)
        { var frame = JsonSerializer.Deserialize<GameplayEnvelope>(buffer.AsSpan(), GameplayWire.Json)!; Frames.Enqueue(frame); if (frame.Type == "state") Volatile.Write(ref _lastState, frame.State); return Task.CompletedTask; }
    }
}
