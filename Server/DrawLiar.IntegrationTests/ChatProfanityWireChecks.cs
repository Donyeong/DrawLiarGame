using DrawLiar;

internal static partial class Integration
{
    private static async Task VerifyChatProfanityWireAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Relay);
        await fixture.FlushAsync();
        fixture.Send(fixture.Host, new GameplayEnvelope
        {
            Type = "request", Kind = "chat", Text = "  그림 시발 fUcK  ",
            Line = new ChatLine { PlayerId = fixture.Observer.PlayerId, Name = "위조", Text = "원문 위조" }
        });
        await fixture.FlushAsync();
        foreach (var player in fixture.State.Players)
        {
            var line = fixture.Socket(player.Id).Frames.Single(frame => frame.Type == "chat").Line;
            Check(line.Text == "그림 ** ****" && line.PlayerId == fixture.Host.PlayerId && line.Name == "host",
                "방 채팅은 비속어를 서버에서 가리고 실제 발신자 정보로 모든 참가자와 관전자에게 전달해야 합니다.");
        }

        await fixture.StartAsync();
        fixture.Send(fixture.Host, new GameplayEnvelope { Type = "request", Kind = "chat", Text = "씨앗과 수박을 그려요" });
        await fixture.FlushAsync();
        foreach (var player in fixture.State.Players)
            Check(fixture.Socket(player.Id).Frames.Last(frame => frame.Type == "chat").Line.Text == "씨앗과 수박을 그려요",
                "경기 중 일반 참가자의 평범한 채팅은 문자를 보존하여 관전자에게도 전달해야 합니다.");

        var activePlayers = fixture.State.Players.Where(player => !player.IsSpectator).ToArray();
        int previous = fixture.Socket(fixture.Host.PlayerId).Frames.Count(frame => frame.Type == "chat");
        fixture.Send(fixture.Observer, new GameplayEnvelope { Type = "request", Kind = "chat", Text = "관전 시 발 F U C K" });
        await fixture.FlushAsync();
        var observer = fixture.Socket(fixture.Observer.PlayerId).Frames.Last(frame => frame.Type == "chat");
        Check(observer.Line.Text == "관전 *** *******" && observer.Line.PlayerId == fixture.Observer.PlayerId,
            "관전자 채팅도 공백으로 나눈 비속어를 가려야 합니다.");
        Check(activePlayers.All(player => fixture.Socket(player.Id).Frames.Count(frame => frame.Type == "chat") == previous),
            "비속어 필터를 적용해도 경기 중 관전자 채팅은 일반 참가자에게 전달하면 안 됩니다.");
        Report("방 채팅 서버 비속어 가림·발신자 위조 거부·일반 메시지 보존·진행 중 관전자 채팅 분리 검증");
    }
}
