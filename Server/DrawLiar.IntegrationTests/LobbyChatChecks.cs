using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using DrawLiar;
using DrawLiar.GameServer;
using DrawLiar.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyLobbyChatAsync()
    {
        var clock = new LobbyClock();
        string firstToken = new('a', 64), secondToken = new('b', 64), expiredToken = new('c', 64);
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        var identities = new ConcurrentDictionary<string, LobbyChatIdentity>();
        identities[firstToken] = new(firstId, "첫화가", clock.GetUtcNow().AddHours(12));
        identities[secondToken] = new(secondId, "둘째화가", clock.GetUtcNow().AddHours(12));
        identities[expiredToken] = new(Guid.NewGuid(), "만료화가", clock.GetUtcNow().AddSeconds(-1));
        var hub = new LobbyChatHub((token, cancellation) => identities.TryGetValue(token, out var identity)
            ? Task.FromResult(identity) : Task.FromException<LobbyChatIdentity>(new ApiException("Unauthorized", 401)),
            clock, TimeSpan.FromMilliseconds(50));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/lobby", hub.HandleAsync);
        await app.StartAsync();
        var address = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/ws/lobby");
        foreach (bool disposed in new[] { false, true })
        {
            using var socket = new ShutdownLobbySocket(firstToken, disposed);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var context = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = timeout.Token };
            context.Features.Set<IHttpWebSocketFeature>(new ShutdownLobbySocketFeature(socket));
            await hub.HandleAsync(context).WaitAsync(timeout.Token);
            Check(socket.State == WebSocketState.Aborted, "송신·종료 중 소켓 상태 오류는 인증 연결과 송신 작업을 정리해야 합니다.");
        }
        await using (var invalid = await LobbyPeer.OpenAsync(address, new('0', 64)))
            Check((await invalid.WaitAsync(message => message.Type == "notice")).Code == "InvalidSession", "인증되지 않은 로비 채팅 연결을 거부해야 합니다.");
        await using (var expired = await LobbyPeer.OpenAsync(address, expiredToken))
            Check((await expired.WaitAsync(message => message.Type == "notice")).Code == "InvalidSession", "만료된 로비 세션을 거부해야 합니다.");
        await using var first = await LobbyPeer.OpenAsync(address, firstToken);
        var initial = await first.WaitAsync(message => message.Type == "history");
        Check(initial.Messages.Length == 0 && initial.MemberCount == 1, "첫 입장에 빈 기록과 인증된 접속 인원을 전달해야 합니다.");
        foreach (string requestId in new[] { "", new string('a', 65), "bad id", "한글" })
        {
            await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "확인 없는 발언", RequestId = requestId });
            Check((await first.WaitAsync(message => message.Type == "notice")).Code == "InvalidChat",
                "확인 응답을 연결할 수 없는 요청 ID는 메시지 기록과 속도 제한을 사용하지 않고 거부해야 합니다.");
        }
        await using var second = await LobbyPeer.OpenAsync(address, secondToken);
        Check((await second.WaitAsync(message => message.Type == "history")).MemberCount == 2, "다른 계정 입장 시 접속 인원을 늘려야 합니다.");
        await first.WaitAsync(message => message.Type == "presence" && message.MemberCount == 2);
        await using var duplicate = await LobbyPeer.OpenAsync(address, firstToken);
        Check((await duplicate.WaitAsync(message => message.Type == "history")).MemberCount == 2, "같은 계정의 복수 연결은 한 명으로 세어야 합니다.");
        await using (var excess = await LobbyPeer.OpenAsync(address, firstToken))
            Check((await excess.WaitAsync(message => message.Type == "notice")).Code == "ChatUnavailable", "계정당 두 개를 넘는 연결을 제한해야 합니다.");
        await first.SendAsync(new LobbyChatEnvelope
        {
            Type = "chat", Text = "  그림 좋아요 <b>그대로</b>  ", RequestId = "first",
            Message = new LobbyChatMessage { AccountId = secondId.ToString(), DisplayName = "위조이름", Id = 999 }
        }, true);
        var accepted = await first.WaitAsync(message => message.Type == "chat" && message.RequestId == "first");
        Check(accepted.Message.AccountId == firstId.ToString() && accepted.Message.DisplayName == "첫화가"
            && accepted.Message.Text == "그림 좋아요 <b>그대로</b>" && accepted.Message.Id == 1
            && DateTimeOffset.Parse(accepted.Message.SentAt) == clock.GetUtcNow(), "서버는 발신 계정·이름·순번·시간을 결정하고 사용자 문자를 보존해야 합니다.");
        foreach (var peer in new[] { second, duplicate })
        {
            var broadcast = await peer.WaitAsync(message => message.Type == "chat");
            Check(broadcast.Message.Id == accepted.Message.Id && broadcast.RequestId == "", "채팅을 모든 로비 연결에 전달하고 요청 확인은 발신 연결에만 보내야 합니다.");
        }
        await duplicate.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "너무 빠름", RequestId = "rate" });
        Check((await duplicate.WaitAsync(message => message.Type == "notice" && message.RequestId == "rate")).Code == "ChatRateLimited",
            "복수 연결과 재접속으로 계정당 발언 속도 제한을 우회하면 안 됩니다.");
        for (int index = 0; index < 4; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "연속" + index, RequestId = "burst" + index });
            await first.WaitAsync(message => message.Type == "chat" && message.RequestId == "burst" + index);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "여섯번째", RequestId = "burstLimit" });
        Check((await first.WaitAsync(message => message.Type == "notice" && message.RequestId == "burstLimit")).Code == "ChatRateLimited",
            "10초에 다섯 개를 넘는 발언을 거부해야 합니다.");
        foreach (string text in new[] { "", "   ", new string('가', 201), "줄\n바꿈", "탭\t문자" })
        {
            await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = text, RequestId = "invalid" });
            Check((await first.WaitAsync(message => message.Type == "notice" && message.RequestId == "invalid")).Code == "InvalidChat",
                "빈 문자열·너무 긴 문자열·제어 문자를 거부해야 합니다.");
        }
        identities[firstToken] = identities[firstToken] with { DisplayName = "변경한이름" };
        for (int index = 0; index < 55; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = index == 54 ? new string('가', 200) : "기록" + index, RequestId = "history" + index });
            var message = await first.WaitAsync(message => message.Type == "chat" && message.RequestId == "history" + index);
            Check(message.Message.DisplayName == "변경한이름", "발언 시 현재 서버 프로필 이름을 반영해야 합니다.");
        }
        await duplicate.DisposeAsync();
        await second.WaitAsync(message => message.Type == "presence" && message.MemberCount == 2);
        await using var reconnect = await LobbyPeer.OpenAsync(address, firstToken);
        var history = await reconnect.WaitAsync(message => message.Type == "history");
        Check(history.Messages.Length == 50 && history.Messages.First().Id == 11 && history.Messages.Last().Id == 60
            && history.Messages.Zip(history.Messages.Skip(1), (left, right) => right.Id == left.Id + 1).All(value => value)
            && history.Messages.Last().Text.Length == 200, "최근 50개 기록을 서버 순서대로 복원하고 최대 길이 메시지를 허용해야 합니다.");
        await reconnect.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "재접속우회", RequestId = "reconnectRate" });
        Check((await reconnect.WaitAsync(message => message.Type == "notice" && message.RequestId == "reconnectRate")).Code == "ChatRateLimited",
            "같은 계정의 새 연결에 기존 발언 속도 제한을 유지해야 합니다.");
        await first.DisposeAsync();
        await reconnect.DisposeAsync();
        await second.WaitAsync(message => message.Type == "presence" && message.MemberCount == 1);
        identities.TryRemove(secondToken, out _);
        Check((await second.WaitAsync(message => message.Type == "notice" && message.Code == "InvalidSession")).Code == "InvalidSession",
            "로그아웃·차단된 연결은 재검증으로 종료해야 합니다.");
        await using var last = await LobbyPeer.OpenAsync(address, firstToken);
        Check((await last.WaitAsync(message => message.Type == "history")).MemberCount == 1, "인증 해제된 세션은 접속 인원에서 제거해야 합니다.");
        await last.SendBytesAsync(new byte[4096], WebSocketMessageType.Binary);
        Check((await last.WaitAsync(message => message.Type == "notice")).Code == "InvalidChat", "잘못된 프레임을 종료하고 다른 참가자의 연결은 보호해야 합니다.");
        Report("로비 WebSocket 인증·만료·계정 위조 거부·분할 프레임·다중 접속 인원·서버 발언 확인·계정별 속도 제한·50개 순서 복원·프로필 갱신·로그아웃 연결 종료 검증");
        await app.StopAsync();
    }

    private static async Task VerifyLobbyChatDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        string schema = "drawliar_chat_test_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false, IncludeErrorDetail = false };
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            Guid account = await database.DevelopmentAccountAsync("채팅화가");
            var main = await database.IssueSessionAsync(account, "main");
            var game = await database.IssueSessionAsync(account, "game:chat-test", ServerRuntime.Hash(main.Token));
            var identity = await database.AuthenticateLobbyAsync(game.Token, "game:chat-test", CancellationToken.None);
            Check(identity.Session.AccountId == account && identity.DisplayName == "채팅화가", "로비 채팅은 게임서버 세션과 서버 프로필 이름을 읽어야 합니다.");
            await ExpectAuthErrorAsync(() => database.AuthenticateLobbyAsync(main.Token, "game:chat-test", CancellationToken.None), "Unauthorized");
            await ExpectAuthErrorAsync(() => database.AuthenticateLobbyAsync(game.Token, "game:another-node", CancellationToken.None), "Unauthorized");
            await database.UpdateProfileAsync(account, new UpdateProfileRequest { DisplayName = "새채팅이름" });
            Check((await database.AuthenticateLobbyAsync(game.Token, "game:chat-test", CancellationToken.None)).DisplayName == "새채팅이름",
                "닉네임 변경은 다음 발언 인증에 반영해야 합니다.");
            await database.LogoutAsync(await database.AuthenticateAsync(main.Token, "main"));
            await ExpectAuthErrorAsync(() => database.AuthenticateLobbyAsync(game.Token, "game:chat-test", CancellationToken.None), "Unauthorized");
            var expired = await database.IssueSessionAsync(account, "game:chat-test", expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
            await ExpectAuthErrorAsync(() => database.AuthenticateLobbyAsync(expired.Token, "game:chat-test", CancellationToken.None), "Unauthorized");
            var banned = await database.IssueSessionAsync(account, "game:chat-test");
            await database.AdminBanAsync(account, true);
            await ExpectAuthErrorAsync(() => database.AuthenticateLobbyAsync(banned.Token, "game:chat-test", CancellationToken.None), "Unauthorized");
            Report("PostgreSQL 로비 채팅 게임서버 scope·현재 닉네임·상위 세션 로그아웃·만료·차단 인증 거부 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class LobbyClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class ShutdownLobbySocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class ShutdownLobbySocket(string token, bool disposed) : WebSocket
    {
        private readonly byte[] _hello = JsonSerializer.SerializeToUtf8Bytes(new LobbyChatEnvelope { Type = "auth", SessionToken = token }, Json);
        private readonly CancellationTokenSource _aborted = new();
        private bool _authenticated;
        private int _disposed;
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _aborted.Cancel(); }
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            _aborted.Dispose();
        }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("종료된 소켓"));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.FromException(disposed ? new ObjectDisposedException("소켓") : new InvalidOperationException("종료된 소켓"));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_authenticated)
            {
                _authenticated = true;
                _hello.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(_hello.Length, WebSocketMessageType.Text, true);
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            await Task.Delay(Timeout.Infinite, stop.Token);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
    }

    private sealed class LobbyPeer : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Channel<LobbyChatEnvelope> _received = Channel.CreateUnbounded<LobbyChatEnvelope>();
        private readonly CancellationTokenSource _lifetime = new();
        private Task _reader = Task.CompletedTask;
        private int _disposed;
        public static async Task<LobbyPeer> OpenAsync(Uri uri, string token)
        {
            var peer = new LobbyPeer();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await peer._socket.ConnectAsync(uri, timeout.Token);
            peer._reader = peer.ReadAsync();
            await peer.SendAsync(new LobbyChatEnvelope { Type = "auth", SessionToken = token });
            return peer;
        }
        public async Task SendAsync(LobbyChatEnvelope envelope, bool fragmented = false)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (fragmented)
            {
                await _socket.SendAsync(bytes.AsMemory(0, 5), WebSocketMessageType.Text, false, _lifetime.Token);
                await _socket.SendAsync(bytes.AsMemory(5), WebSocketMessageType.Text, true, _lifetime.Token);
            }
            else await SendBytesAsync(bytes, WebSocketMessageType.Text);
        }
        public Task SendBytesAsync(byte[] bytes, WebSocketMessageType type) => _socket.SendAsync(new ArraySegment<byte>(bytes), type, true, _lifetime.Token);
        public async Task<LobbyChatEnvelope> WaitAsync(Func<LobbyChatEnvelope, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await _received.Reader.ReadAsync(timeout.Token);
                if (predicate(message)) return message;
            }
        }
        private async Task ReadAsync()
        {
            try
            {
                byte[] buffer = new byte[65536];
                while (!_lifetime.IsCancellationRequested)
                {
                    int count = 0;
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer.AsMemory(count), _lifetime.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        count += result.Count;
                    } while (!result.EndOfMessage);
                    _received.Writer.TryWrite(JsonSerializer.Deserialize<LobbyChatEnvelope>(buffer.AsSpan(0, count), Json)!);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
            finally { _received.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
            }
            _lifetime.Cancel();
            _socket.Abort();
            await _reader;
            _socket.Dispose();
            _lifetime.Dispose();
        }
    }
}
