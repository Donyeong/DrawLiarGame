using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyTopicLanguageAsync()
    {
        var socket = new PolicySocket();
        var peer = new GameConnection(socket, "language0", "test", CancellationToken.None);
        var roomData = new ServerRoomData
        {
            RoomId = Guid.NewGuid().ToString(), OwnerAccountId = "language0",
            Settings = new ServerRoomSettings { Topics = Array.Empty<string>(), TopicLanguage = "en" }
        };
        var builtIn = new GameData
        {
            Topics = new[]
            {
                new TopicData { Name = "과일", Words = new[] { "사과" } },
                new TopicData { Name = "Fruit", LanguageCode = "en", Words = new[] { "apple" },
                    Aliases = new[] { new WordAlias { Word = "apple", Accept = new[] { "apples" } } } },
                new TopicData { Name = "果物", LanguageCode = "ja-JP", Words = new[] { "りんご" } }
            }
        };
        var room = new DedicatedRoom(roomData, builtIn, new[] { new ServerTopicData { Name = "우리 주제", Words = new[] { "단어" } } }, 0, () => { });
        Check(room.Join(peer, new RedeemTicketResponse
        {
            AccountId = peer.AccountId, Room = roomData,
            Profile = new ProfileData { AccountId = peer.AccountId, DisplayName = "언어 검증" }
        }, 0), "주제 언어 검증 참가자가 입장해야 합니다.");
        await WaitAsync(() => socket.LastState != null, 3);
        var state = socket.LastState!;
        Check(state.AvailableTopics.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "Fruit", "우리 주제" }.OrderBy(name => name, StringComparer.Ordinal))
            && state.Settings.Topics.SequenceEqual(new[] { "Fruit", "우리 주제" }) && state.Settings.TopicLanguage == "en",
            "데디케이티드는 방의 주제 언어에 맞는 기본 주제와 방의 사용자 주제만 노출하고 기본 선택해야 합니다.");

        var invalid = new ServerRoomSettings { Topics = new[] { "과일" }, TopicLanguage = "xx" };
        ServerDatabase.ValidateSettings(invalid);
        Check(invalid.TopicLanguage == ServerRoomSettings.DEFAULT_TOPIC_LANGUAGE, "알 수 없는 주제 언어는 한국어 기본값으로 정규화해야 합니다.");
        Report("방 주제 언어별 기본 주제 노출·기본 선택·언어 코드 정규화 검증");
    }
}
