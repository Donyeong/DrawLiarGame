using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private const string WORKSHOP_PATH = "/api/topic-workshop";

    private static async Task VerifyTopicWorkshopAsync(string mainUrl)
    {
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "작업실 검증은 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_WORKSHOP_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_WORKSHOP_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25539 && scoped.Database == "postgres"
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_workshop_test_[0-9a-f]{32}$"),
            "작업실 검증은 무작위 스키마의 전용 PostgreSQL만 사용합니다.");
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        using var main = Client(mainUrl); await RequireHealthAsync(main);
        var users = new List<TestUser>();
        for (int index = 0; index < 3; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "작업실은 전용 로컬 게임서버만 검증합니다.");
        using var game = Client(users[0].Login.GameServerUrl); await RequireHealthAsync(game);
        var author = users[0]; var downloader = users[1]; var concurrentAuthor = users[2];
        try
        {
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH, null, null, HttpStatusCode.Unauthorized, "Unauthorized");
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH, null, author.Login.SessionToken, HttpStatusCode.Unauthorized, "Unauthorized");
            await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH, new { Name = "검증", LanguageCode = "ko-KR", Words = new[] { "사과" } }, null, HttpStatusCode.Unauthorized, "Unauthorized");
            var empty = await WorkshopListAsync(game, author);
            var policy = empty.GetProperty("Policy"); var limits = policy.GetProperty("Limits");
            int nameLimit = limits.GetProperty("NameMaxLength").GetInt32(), wordLimit = limits.GetProperty("WordMaxLength").GetInt32();
            int wordCount = limits.GetProperty("MaxWordsPerTopic").GetInt32(), quota = limits.GetProperty("MaxUploadsPerAccount").GetInt32();
            Check(nameLimit == 10 && wordLimit == 10 && wordCount == 30 && quota == 5,
                "서버 응답은 공유 데이터의 이름·단어10자, 단어30개, 계정5개 정책을 전달해야 합니다.");
            var languages = policy.GetProperty("LanguageCodes").EnumerateArray().Select(value => value.GetString()).ToArray();
            Check(languages.Length == 12 && languages.Distinct().Count() == 12 && languages.Contains("ko-KR") && languages.Contains("en"),
                "업로드 언어 정책은 지원12언어를 중복 없이 전달해야 합니다.");
            await using (var version = new NpgsqlCommand("SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=11", owner))
                Check(Convert.ToInt32(await version.ExecuteScalarAsync()) == 1, "격리 DB에 작업실 마이그레이션11을 적용해야 합니다.");
            foreach (var invalid in new object[]
            {
                new { Name = "", LanguageCode = "ko-KR", Words = new[] { "사과" } },
                new { Name = new string('가', nameLimit + 1), LanguageCode = "ko-KR", Words = new[] { "사과" } },
                new { Name = "줄\n바꿈", LanguageCode = "ko-KR", Words = new[] { "사과" } },
                new { Name = "빈단어", LanguageCode = "ko-KR", Words = Array.Empty<string>() },
                new { Name = "긴단어", LanguageCode = "ko-KR", Words = new[] { new string('나', wordLimit + 1) } },
                new { Name = "많은단어", LanguageCode = "ko-KR", Words = Enumerable.Range(0, wordCount + 1).Select(index => "단어" + index).ToArray() },
                new { Name = "중복초과", LanguageCode = "ko-KR", Words = Enumerable.Repeat("사과", wordCount + 1).ToArray() },
                new { Name = "잘못된언어", LanguageCode = "xx-YY", Words = new[] { "사과" } }
            }) await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH, invalid, author.Session.SessionToken, HttpStatusCode.BadRequest);
            Check((await WorkshopListAsync(game, author)).GetProperty("OwnCount").GetInt32() == 0,
                "잘못된 업로드를 자르거나 일부 저장하여 계정 슬롯을 사용하면 안 됩니다.");
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "?language=xx-YY", null,
                author.Session.SessionToken, HttpStatusCode.BadRequest, "TopicWorkshopInvalidLanguage");
            Report("작업실 인증·공유 정책·길이/개수 초과 거부·언어 검증");

            string exactName = new('가', nameLimit);
            string[] exactWords = new[] { new string('나', wordLimit) }.Concat(Enumerable.Range(1, wordCount - 1).Select(index => "단어" + index)).ToArray();
            var first = await PostAsync<JsonElement>(game, WORKSHOP_PATH, new { Name = exactName, LanguageCode = "ko-KR", Words = exactWords }, author.Session.SessionToken);
            string firstId = WorkshopId(first);
            Check(first.GetProperty("Topic").GetProperty("Name").GetString() == exactName && WorkshopWords(first).SequenceEqual(exactWords)
                && first.GetProperty("Topic").GetProperty("WordCount").GetInt32() == wordCount,
                "정확한 한도값의 업로드는 이름과 모든 단어를 그대로 저장해야 합니다.");
            await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH, new { Name = exactName, LanguageCode = "ko-KR", Words = new[] { "다른단어" } },
                author.Session.SessionToken, HttpStatusCode.Conflict, "TopicWorkshopNameConflict");
            using (var data = JsonDocument.Parse(typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.BuiltInGameData.json")!))
            {
                string builtin = data.RootElement.GetProperty("Topics").EnumerateArray().Select(value => value.GetProperty("Name").GetString()!).First(value => value.Length <= nameLimit);
                await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH, new { Name = builtin, LanguageCode = "ko-KR", Words = new[] { "사과" } },
                    author.Session.SessionToken, HttpStatusCode.Conflict, "TopicWorkshopNameConflict");
            }
            await PostAsync<JsonElement>(game, WORKSHOP_PATH, new { Name = "영어검증", LanguageCode = "en", Words = new[] { "alpha", "bravo" } }, author.Session.SessionToken);
            await PostAsync<JsonElement>(game, WORKSHOP_PATH, new { Name = exactName, LanguageCode = "ko-KR", Words = new[] { "타인의단어" } }, downloader.Session.SessionToken);
            var downloaded = await GetAsync<JsonElement>(game, WORKSHOP_PATH + "/" + firstId, downloader.Session.SessionToken);
            Check(WorkshopWords(downloaded).SequenceEqual(exactWords) && downloaded.GetProperty("Topic").GetProperty("Name").GetString() == exactName
                && downloaded.GetProperty("Topic").GetProperty("CreatorAccountId").GetString() == author.Login.AccountId
                && !downloaded.GetProperty("Topic").GetProperty("IsMine").GetBoolean()
                && downloaded.GetProperty("Topic").GetProperty("DownloadCount").GetInt32() == 1,
                "다른 사용자는 동일한 콘텐츠와 제작자 정보·증가한 다운로드 수를 받아야 합니다.");
            await GetAsync<JsonElement>(game, WORKSHOP_PATH + "/" + firstId, downloader.Session.SessionToken);
            var listing = await WorkshopListAsync(game, downloader, "?language=ko-KR");
            Check(WorkshopItems(listing).All(item => item.GetProperty("LanguageCode").GetString() == "ko-KR")
                && WorkshopItems(listing).Single(item => item.GetProperty("Id").GetString() == firstId).GetProperty("DownloadCount").GetInt32() == 2,
                "언어 필터와 목록의 다운로드 집계는 실제 저장된 값을 반영해야 합니다.");
            var english = await WorkshopListAsync(game, author, "?language=en&mine=true");
            Check(english.GetProperty("Total").GetInt32() == 1 && english.GetProperty("OwnCount").GetInt32() == 2
                && WorkshopItems(english).All(item => item.GetProperty("IsMine").GetBoolean()),
                "내 업로드 필터는 해당 언어만 보여주되 전체 언어의 개인 슬롯 수를 반환해야 합니다.");
            Report("한도 콘텐츠 보존·이름 충돌·다른 사용자의 동일 다운로드·언어/내 업로드·메타데이터 검증");

            var responses = await Task.WhenAll(Enumerable.Range(0, quota + 1).Select(index => SendAsync(game, WORKSHOP_PATH,
                new { Name = "동시" + index, LanguageCode = "ko-KR", Words = new[] { "사과", "배" } }, concurrentAuthor.Session.SessionToken)));
            var createdIds = new List<string>();
            try
            {
                Check(responses.Count(response => response.IsSuccessStatusCode) == quota
                    && responses.Count(response => response.StatusCode == HttpStatusCode.Conflict) == 1,
                    "비어 있는 계정의6개 동시 업로드는 정확히5개만 성공해야 합니다.");
                foreach (var response in responses)
                {
                    if (response.IsSuccessStatusCode) createdIds.Add(WorkshopId(await response.Content.ReadFromJsonAsync<JsonElement>(Json)));
                    else Check((await response.Content.ReadFromJsonAsync<ApiError>(Json))?.Code == "TopicWorkshopUploadLimit", "동시 생성의 초과 요청은 업로드 한도 오류여야 합니다.");
                }
            }
            finally { foreach (var response in responses) response.Dispose(); }
            var mine = await WorkshopListAsync(game, concurrentAuthor, "?mine=true");
            Check(mine.GetProperty("OwnCount").GetInt32() == quota && mine.GetProperty("Total").GetInt32() == quota,
                "동시 생성 이후 실제 DB 목록·개인 슬롯 수는5개여야 합니다.");
            await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH,
                new { Name = "한도중오류", LanguageCode = "ko-KR", Words = new[] { new string('나', wordLimit + 1) } },
                concurrentAuthor.Session.SessionToken, HttpStatusCode.BadRequest);
            await WorkshopErrorAsync(game, HttpMethod.Delete, WORKSHOP_PATH + "/" + createdIds[0], null,
                downloader.Session.SessionToken, HttpStatusCode.NotFound, "TopicWorkshopUnavailable");
            await WorkshopDeleteAsync(game, createdIds[0], concurrentAuthor.Session.SessionToken);
            Check((await WorkshopListAsync(game, concurrentAuthor, "?mine=true")).GetProperty("OwnCount").GetInt32() == quota - 1,
                "본인 삭제는 업로드 슬롯을 즉시 반환해야 합니다.");
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "/" + createdIds[0], null,
                downloader.Session.SessionToken, HttpStatusCode.NotFound, "TopicWorkshopUnavailable");
            await PostAsync<JsonElement>(game, WORKSHOP_PATH, new { Name = "슬롯복원", LanguageCode = "ko-KR", Words = new[] { "사과" } }, concurrentAuthor.Session.SessionToken);
            Check((await WorkshopListAsync(game, concurrentAuthor, "?mine=true")).GetProperty("OwnCount").GetInt32() == quota,
                "반환된 슬롯은 새로운 업로드에 사용할 수 있어야 합니다.");
            var all = await WorkshopListAsync(game, downloader); int total = all.GetProperty("Total").GetInt32();
            var pagedIds = new List<string>();
            for (int offset = 0; offset < total; offset += 2)
            {
                var page = await WorkshopListAsync(game, downloader, "?offset=" + offset + "&limit=2");
                Check(page.GetProperty("Offset").GetInt32() == offset && page.GetProperty("Limit").GetInt32() == 2
                    && page.GetProperty("Total").GetInt32() == total && WorkshopItems(page).Length <= 2,
                    "페이지는 요청 offset/limit과 동일한 전체 수를 유지해야 합니다.");
                pagedIds.AddRange(WorkshopItems(page).Select(item => item.GetProperty("Id").GetString()!));
            }
            Check(pagedIds.Count == total && pagedIds.Distinct().Count() == total, "페이지를 모두 읽으면 중복·누락 없이 동일한 목록을 얻어야 합니다.");
            var capped = await WorkshopListAsync(game, downloader, "?offset=-10&limit=999");
            Check(capped.GetProperty("Offset").GetInt32() == 0 && capped.GetProperty("Limit").GetInt32() == 50,
                "페이지 입력은 음수 offset을0으로, 과도한 limit을50으로 정규화해야 합니다.");
            Report("실제 PostgreSQL6개 동시 업로드·정확5슬롯·삭제 권한/슬롯 반환·페이지 중복/누락 검증");

            using (var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build()))
            {
                const string NODE = "dedicated-workshop-qa";
                await database.RegisterDedicatedAsync(new RegisterDedicatedRequest { NodeId = NODE, PublicUrl = "ws://127.0.0.1:25570/play" }, true);
                var room = await PostAsync<DedicatedAssignment>(game, "/api/rooms", new CreateRoomRequest
                {
                    Settings = new ServerRoomSettings { Topics = new[] { exactName } },
                    CustomTopics = new[] { new ServerTopicData { Name = exactName, Words = WorkshopWords(downloaded) } }
                }, downloader.Session.SessionToken);
                var ticket = await database.RedeemTicketAsync(new RedeemTicketRequest { NodeId = NODE, RoomId = room.RoomId, JoinTicket = room.JoinTicket });
                Check(ticket.CustomTopics.Length == 1 && ticket.CustomTopics[0].Name == exactName
                    && ticket.CustomTopics[0].Words.SequenceEqual(exactWords) && ticket.Room.Settings.Topics.SequenceEqual(new[] { exactName }),
                    "다운로드한 콘텐츠는 실제 방 생성·입장권 교환의 커스텀 주제에 빠짐없이 전달되어야 합니다.");
            }
            await WorkshopSetBannedAsync(owner, author.Login.AccountId, true);
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "/" + firstId, null,
                downloader.Session.SessionToken, HttpStatusCode.NotFound, "TopicWorkshopUnavailable");
            Check(WorkshopItems(await WorkshopListAsync(game, downloader)).All(item => item.GetProperty("CreatorAccountId").GetString() != author.Login.AccountId),
                "제재된 계정의 콘텐츠는 다른 사용자에게 목록·다운로드로 노출하면 안 됩니다.");
            using (var banned = await SendAsync(game, WORKSHOP_PATH, new { Name = "제재업로드", LanguageCode = "ko-KR", Words = new[] { "사과" } }, author.Session.SessionToken))
                Check(banned.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "제재된 작성자는 기존 세션으로 업로드할 수 없어야 합니다.");
            await WorkshopSetBannedAsync(owner, author.Login.AccountId, false);
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "/" + Guid.NewGuid(), null,
                downloader.Session.SessionToken, HttpStatusCode.NotFound, "TopicWorkshopUnavailable");
            Report("실제 방 생성으로 다운로드 데이터 전달·제재 계정/미존재 콘텐츠 접근 검증");
        }
        finally
        {
            await WorkshopSetBannedAsync(owner, author.Login.AccountId, false);
            foreach (var user in users) await PostNoContentAsync(game, "/api/session/logout", new { }, user.Session.SessionToken);
        }
    }

    private static Task<JsonElement> WorkshopListAsync(HttpClient game, TestUser user, string query = "") =>
        GetAsync<JsonElement>(game, WORKSHOP_PATH + query, user.Session.SessionToken);
    private static string WorkshopId(JsonElement detail) => detail.GetProperty("Topic").GetProperty("Id").GetString()!;
    private static string[] WorkshopWords(JsonElement detail) => detail.GetProperty("Words").EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static JsonElement[] WorkshopItems(JsonElement list) => list.GetProperty("Items").EnumerateArray().ToArray();
    private static async Task WorkshopErrorAsync(HttpClient game, HttpMethod method, string path, object? body, string? token, HttpStatusCode expected, string? code = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await game.SendAsync(request);
        var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
        Check(response.StatusCode == expected && !string.IsNullOrEmpty(error?.Code) && (code == null || error.Code == code),
            $"작업실 거부 응답은 {(int)expected}/{code ?? "검증 오류"}여야 합니다. 실제: {(int)response.StatusCode}/{error?.Code}");
    }
    private static async Task WorkshopDeleteAsync(HttpClient game, string id, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, WORKSHOP_PATH + "/" + id);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await game.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.NoContent, "본인 작업실 삭제는204로 성공해야 합니다.");
    }
    private static async Task WorkshopSetBannedAsync(NpgsqlConnection owner, string account, bool banned)
    {
        await using var command = new NpgsqlCommand("UPDATE \"Account\" SET \"IsBanned\"=$2 WHERE \"Id\"=$1", owner);
        command.Parameters.AddWithValue(Guid.Parse(account)); command.Parameters.AddWithValue(banned);
        Check(await command.ExecuteNonQueryAsync() == 1, "격리 검증 계정의 제재 상태만 갱신해야 합니다.");
    }
}
