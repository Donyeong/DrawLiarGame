using DrawLiar;
using DrawLiar.GameServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

internal static partial class Integration
{
    private static async Task VerifyLobbyChatThrottleAsync()
    {
        var clock = new LobbyClock();
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        string firstToken = new('d', 64), secondToken = new('e', 64);
        int authentications = 0;
        var hub = new LobbyChatHub((token, _) =>
        {
            Interlocked.Increment(ref authentications);
            return Task.FromResult(new LobbyChatIdentity(token == firstToken ? firstId : secondId,
                "채팅화가", clock.GetUtcNow().AddHours(12)));
        }, clock, TimeSpan.FromHours(1));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/lobby", hub.HandleAsync);
        await app.StartAsync();
        var address = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/ws/lobby");
        await using var first = await LobbyPeer.OpenAsync(address, firstToken);
        await first.WaitAsync(message => message.Type == "history");
        await using var duplicate = await LobbyPeer.OpenAsync(address, firstToken);
        await duplicate.WaitAsync(message => message.Type == "history");
        await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "정상 발언", RequestId = "accepted" });
        await first.WaitAsync(message => message.Type == "chat" && message.RequestId == "accepted");
        Check(Volatile.Read(ref authentications) == 3, "최초 연결과 허용한 발언에서만 인증·프로필을 조회해야 합니다.");
        for (int index = 0; index < 5; index++)
        {
            string requestId = "limited" + index;
            await duplicate.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "반복 초과", RequestId = requestId });
            Check((await duplicate.WaitAsync(message => message.Type == "notice" && message.RequestId == requestId)).Code == "ChatRateLimited",
                "같은 계정의 다른 연결에서 시도한 발언도 DB 조회 전에 제한해야 합니다.");
        }
        await duplicate.WaitClosedAsync();
        Check(Volatile.Read(ref authentications) == 3, "반복 초과 연결을 종료할 때까지 DB 인증·프로필 조회를 추가하면 안 됩니다.");
        await using var reconnect = await LobbyPeer.OpenAsync(address, firstToken);
        await reconnect.WaitAsync(message => message.Type == "history");
        await reconnect.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "재접속 초과", RequestId = "reconnect" });
        Check((await reconnect.WaitAsync(message => message.Type == "notice" && message.RequestId == "reconnect")).Code == "ChatRateLimited"
            && Volatile.Read(ref authentications) == 4, "연결을 다시 열어도 계정별 시도 한도를 초기화하면 안 됩니다.");
        await using var other = await LobbyPeer.OpenAsync(address, secondToken);
        await other.WaitAsync(message => message.Type == "history");
        await other.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "다른 계정", RequestId = "other" });
        await other.WaitAsync(message => message.Type == "chat" && message.RequestId == "other");
        Check(Volatile.Read(ref authentications) == 6, "초과 연결은 다른 계정의 정상 발언을 막으면 안 됩니다.");
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "간격 경과", RequestId = "next" });
        await first.WaitAsync(message => message.Type == "chat" && message.RequestId == "next");
        Check(Volatile.Read(ref authentications) == 7, "허용 간격 뒤 정상 발언은 다시 인증·프로필을 조회해야 합니다.");
        for (int index = 0; index < 3; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            string requestId = "burst" + index;
            await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "정상 간격", RequestId = requestId });
            await first.WaitAsync(message => message.Type == "chat" && message.RequestId == requestId);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await first.SendAsync(new LobbyChatEnvelope { Type = "chat", Text = "10초 한도 초과", RequestId = "window" });
        Check((await first.WaitAsync(message => message.Type == "notice" && message.RequestId == "window")).Code == "ChatRateLimited"
            && Volatile.Read(ref authentications) == 10, "10초당 다섯 번을 넘는 시도도 DB 조회 전에 거부해야 합니다.");
        Report("로비 채팅 DB 조회 전 계정 공용 시도 제한·반복 초과 연결 종료·재접속 한도 유지·다른 계정 발언 유지 검증");
        await app.StopAsync();
    }
}
