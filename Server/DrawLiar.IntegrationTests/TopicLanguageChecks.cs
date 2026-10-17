using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using System.Text.Json;

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
        string[] expectedLanguages = { "ko", "en", "ja", "zh-CN", "de", "fr", "es", "es-419", "pt", "it", "nl", "da", "no", "sv", "fi", "pl", "cs", "sk", "hu", "ro", "ru", "uk", "tr", "ar", "iw", "hi", "id", "th", "vi" };
        Check(ServerRoomSettings.TOPIC_LANGUAGES.SequenceEqual(expectedLanguages)
            && ServerDatabase.WorkshopPolicy.LanguageCodes.SequenceEqual(expectedLanguages), "방과 창작마당은 지정된 순서의 29개 언어를 지원해야 합니다.");
        foreach (var (oldCode, currentCode) in new[] { ("KO_kr", "ko"), ("ja-JP", "ja"), ("fr-FR", "fr"), ("de-DE", "de"),
            ("hi-IN", "hi"), ("he", "iw"), ("nb", "no"), ("nn-NO", "no"), ("zh-TW", "zh-TW"), ("pt-BR", "pt-BR") })
            Check(ServerRoomSettings.NormalizeTopicLanguage(oldCode) == currentCode, "이전 언어 코드와 지역별 게임 팩을 보존해야 합니다: " + oldCode);

        using var stream = typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.BuiltInGameData.json")!;
        var packs = JsonSerializer.Deserialize<GameData>(stream, Json)!;
        foreach (string language in expectedLanguages.Concat(new[] { "zh-TW", "pt-BR" }))
        {
            var topics = packs.Topics.Where(topic => ServerRoomSettings.TopicLanguageOf(topic.LanguageCode) == language).ToArray();
            Check(topics.Length == 20 && topics.Select(topic => GameRules.NormalizeGuess(topic.Name)).Distinct().Count() == 20,
                language + " 기본 주제 팩은 서로 다른 주제 20개를 포함해야 합니다.");
            var localizedSocket = new PolicySocket();
            var localizedPeer = new GameConnection(localizedSocket, "localized", "test", CancellationToken.None);
            var localizedRoom = new ServerRoomData
            {
                RoomId = Guid.NewGuid().ToString(), OwnerAccountId = localizedPeer.AccountId,
                Settings = new ServerRoomSettings { TopicLanguage = language }
            };
            var localized = new DedicatedRoom(localizedRoom, packs, Array.Empty<ServerTopicData>(), 0, () => { });
            Check(localized.Join(localizedPeer, new RedeemTicketResponse
            {
                AccountId = localizedPeer.AccountId, Room = localizedRoom,
                Profile = new ProfileData { AccountId = localizedPeer.AccountId, DisplayName = "팩 검증" }
            }, 0), language + " 주제 팩을 사용하는 방에 입장해야 합니다.");
            await WaitAsync(() => localizedSocket.LastState != null, 3);
            Check(localizedSocket.LastState!.AvailableTopics.SequenceEqual(topics.Select(topic => topic.Name))
                && localizedSocket.LastState.Settings.TopicLanguage == language,
                language + " 방은 해당 팩의 원문 주제만 노출해야 합니다.");
        }
        Report("방 주제 언어별 기본 주제 노출·기본 선택·언어 코드 정규화 검증");
    }
}
