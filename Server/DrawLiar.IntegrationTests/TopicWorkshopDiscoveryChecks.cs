using System.Net;
using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static string[] WorkshopPublishWords(int count = 10) => Enumerable.Range(0, count).Select(index => "제시어" + index).ToArray();

    private static void VerifyTopicWorkshopRules()
    {
        var policy = ServerDatabase.WorkshopPolicy;
        Check(TopicWorkshopRules.ValidatePolicy(policy) && policy.Limits.MinWordsPerTopic == 10
            && policy.Limits.MaxWordsPerTopic == 80 && policy.Limits.MaxUploadsPerAccount == 20, "게시 정책은 최소10·최대80·보관20이어야 합니다.");
        var shortTopic = new TopicWorkshopPublishRequest { Name = "짧은로컬", LanguageCode = "ko-KR", Words = new[] { "사과" } };
        Check(TopicWorkshopRules.TryNormalize(shortTopic, policy, out var local, out _) && local.Words.SequenceEqual(shortTopic.Words),
            "짧은 로컬 주제 저장은 게시 최소 개수와 독립적으로 유지해야 합니다.");
        Check(!TopicWorkshopRules.TryNormalize(shortTopic, policy, out _, out string code, requirePublishMinimum: true)
            && code == "TopicWorkshopTooFewWords", "게시할 때만 최소10개를 검증해야 합니다.");
        var duplicates = new TopicWorkshopPublishRequest { Name = "중복검증", LanguageCode = "en", Words = new[] { "ABC", "ＡＢＣ", "a b c" }.Concat(WorkshopPublishWords(8)).ToArray() };
        Check(!TopicWorkshopRules.TryNormalize(duplicates, policy, out _, out code, requirePublishMinimum: true)
            && code == "TopicWorkshopTooFewWords", "호환문자·공백·대소문자를 정규화한 중복 제거 후 최소10개여야 합니다.");
        foreach (int count in new[] { 10, 80 })
        {
            var request = new TopicWorkshopPublishRequest { Name = "경계검증", LanguageCode = "KO-kr", Words = WorkshopPublishWords(count) };
            Check(TopicWorkshopRules.TryNormalize(request, policy, out var normalized, out _, requirePublishMinimum: true)
                && normalized.Words.Length == count && normalized.LanguageCode == "ko-KR", "게시 최소/최대 경계와 언어 정규화는 콘텐츠를 보존해야 합니다.");
        }
        var excessive = new TopicWorkshopPublishRequest { Name = "초과검증", LanguageCode = "ko-KR", Words = WorkshopPublishWords(81) };
        Check(!TopicWorkshopRules.TryNormalize(excessive, policy, out _, out code, requirePublishMinimum: true)
            && code == "TopicWorkshopTooManyWords", "81개 입력은 부분 저장 없이 거부해야 합니다.");
        policy.Limits.MinWordsPerTopic = 81;
        Check(!TopicWorkshopRules.ValidatePolicy(policy) && ServerDatabase.WorkshopPolicy.Limits.MinWordsPerTopic == 10,
            "최소/최대가 뒤집힌 정책은 거부하고 응답 정책 수정은 서버 원본에 영향을 주면 안 됩니다.");
        Report("창작마당 게시10/80·보관20 정책·로컬 짧은 주제·중복 제거 후 최소 검증");
    }

    private static async Task VerifyWorkshopDiscoveryAsync(HttpClient game, NpgsqlConnection owner, TestUser author, TestUser viewer, TestUser other, string firstId)
    {
        string recommendationPath = WORKSHOP_PATH + "/" + firstId + "/recommend";
        var yes = new TopicWorkshopRecommendationRequest { IsRecommended = true };
        var no = new TopicWorkshopRecommendationRequest { IsRecommended = false };
        await WorkshopErrorAsync(game, HttpMethod.Post, recommendationPath, yes, null, HttpStatusCode.Unauthorized, "Unauthorized");
        await WorkshopErrorAsync(game, HttpMethod.Post, recommendationPath, yes, author.Login.SessionToken, HttpStatusCode.Unauthorized, "Unauthorized");
        foreach (string invalidId in new[] { "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString() })
            await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH + "/" + invalidId + "/recommend", yes, viewer.Session.SessionToken,
                HttpStatusCode.NotFound, "TopicWorkshopUnavailable");
        foreach (var request in new[]
        {
            new TopicWorkshopPublishRequest { Name = "최소거부", LanguageCode = "ko-KR", Words = WorkshopPublishWords(9) },
            new TopicWorkshopPublishRequest { Name = "중복거부", LanguageCode = "ko-KR", Words = WorkshopPublishWords(9).Concat(new[] { " 제시어0 " }).ToArray() }
        }) await WorkshopErrorAsync(game, HttpMethod.Post, WORKSHOP_PATH, request, author.Session.SessionToken, HttpStatusCode.BadRequest, "TopicWorkshopTooFewWords");

        var accounts = new[] { author, viewer, other };
        await Task.WhenAll(accounts.SelectMany(user => Enumerable.Range(0, 5)
            .Select(_ => PostAsync<TopicWorkshopEntry>(game, recommendationPath, yes, user.Session.SessionToken))));
        var first = await GetAsync<TopicWorkshopDetailResponse>(game, WORKSHOP_PATH + "/" + firstId + "/preview", viewer.Session.SessionToken);
        Check(first.Topic.RecommendationCount == 3 && first.Topic.IsRecommended, "동시 중복 추천은 작성자 포함 계정당1개만 집계해야 합니다.");
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM \"TopicWorkshopRecommendation\" WHERE \"TopicId\"=$1", owner))
        {
            command.Parameters.AddWithValue(Guid.Parse(firstId));
            Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 3, "추천 집계와 실제 고유 행 수가 일치해야 합니다.");
        }
        string stored = await WorkshopStoredTopicsAsync(owner);
        var unchanged = await PostAsync<TopicWorkshopEntry>(game, recommendationPath, yes, viewer.Session.SessionToken);
        Check(unchanged.RecommendationCount == 3 && unchanged.IsRecommended && await WorkshopStoredTopicsAsync(owner) == stored,
            "동일 원하는 상태 재요청은 집계와 게시물 버전을 변경하면 안 됩니다.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync<TopicWorkshopEntry>(game, recommendationPath, no, viewer.Session.SessionToken)));
        var viewerEntry = await GetAsync<TopicWorkshopDetailResponse>(game, WORKSHOP_PATH + "/" + firstId + "/preview", viewer.Session.SessionToken);
        var ownerEntry = await GetAsync<TopicWorkshopDetailResponse>(game, WORKSHOP_PATH + "/" + firstId + "/preview", author.Session.SessionToken);
        Check(viewerEntry.Topic.RecommendationCount == 2 && !viewerEntry.Topic.IsRecommended && ownerEntry.Topic.IsRecommended,
            "중복 해제는 정확히1개를 빼고 개인 추천 상태는 요청 계정에만 맞춰 반환해야 합니다.");
        foreach (var user in accounts) await PostAsync<TopicWorkshopEntry>(game, recommendationPath, no, user.Session.SessionToken);

        var ids = new List<string>();
        try
        {
            foreach (string name in new[] { "검색%_\\A", "검색XXA", "검색%_\\B" })
                ids.Add(WorkshopId(await PostAsync<JsonElement>(game, WORKSHOP_PATH,
                    new TopicWorkshopPublishRequest { Name = name, LanguageCode = "ko-KR", Words = WorkshopPublishWords() }, author.Session.SessionToken)));
            var commonCreatedAt = DateTime.UtcNow.AddDays(-1);
            await using (var command = new NpgsqlCommand("UPDATE \"TopicWorkshop\" SET \"CreatedAt\"=$2 WHERE \"Id\"=ANY($1)", owner))
            {
                command.Parameters.AddWithValue(ids.Select(Guid.Parse).ToArray()); command.Parameters.AddWithValue(commonCreatedAt);
                Check(await command.ExecuteNonQueryAsync() == 3, "동일 시각의 격리 게시물로 정렬 최종 키를 검증해야 합니다.");
            }
            foreach (var user in accounts)
                foreach (int index in new[] { 0, 2 }) await PostAsync<TopicWorkshopEntry>(game, WORKSHOP_PATH + "/" + ids[index] + "/recommend", yes, user.Session.SessionToken);
            await PostAsync<TopicWorkshopEntry>(game, WORKSHOP_PATH + "/" + ids[1] + "/recommend", yes, viewer.Session.SessionToken);
            for (int index = 0; index < 3; index++) await GetAsync<JsonElement>(game, WORKSHOP_PATH + "/" + ids[1], viewer.Session.SessionToken);
            for (int index = 0; index < 2; index++) await GetAsync<JsonElement>(game, WORKSHOP_PATH + "/" + ids[2], viewer.Session.SessionToken);
            string query = "?search=" + Uri.EscapeDataString("검색");
            string[] latestIds = ids.OrderByDescending(id => id, StringComparer.Ordinal).ToArray();
            foreach (var ordering in new[]
            {
                (Sort: "latest", Ids: latestIds),
                (Sort: "downloads", Ids: new[] { ids[1], ids[2], ids[0] }),
                (Sort: "popular", Ids: new[] { ids[2], ids[0], ids[1] })
            })
            {
                var listing = await WorkshopListAsync(game, viewer, query + "&sort=" + ordering.Sort);
                Check(WorkshopItems(listing).Select(item => item.GetProperty("Id").GetString()).SequenceEqual(ordering.Ids)
                    && listing.GetProperty("Total").GetInt32() == 3, "검색 결과의 " + ordering.Sort + " 정렬과 전체 수는 실제 집계·안정적인 최종 키를 사용해야 합니다.");
                var paged = new List<string>();
                for (int offset = 0; offset < 3; offset++)
                    paged.AddRange(WorkshopItems(await WorkshopListAsync(game, viewer, query + "&sort=" + ordering.Sort + "&limit=1&offset=" + offset))
                        .Select(item => item.GetProperty("Id").GetString()!));
                Check(paged.SequenceEqual(ordering.Ids), "같은 시각·집계의 페이지는 중복·누락 없이 정렬을 유지해야 합니다.");
            }
            var literal = await WorkshopListAsync(game, viewer, "?search=" + Uri.EscapeDataString("%_\\"));
            Check(WorkshopItems(literal).Select(item => item.GetProperty("Id").GetString()).Order().SequenceEqual(new[] { ids[0], ids[2] }.Order()),
                "%/_/역슬래시 검색은 SQL 패턴으로 확장하지 말고 글자 그대로 비교해야 합니다.");
            Check((await WorkshopListAsync(game, viewer, "?search=" + Uri.EscapeDataString("검색%_\\a"))).GetProperty("Total").GetInt32() == 1,
                "검색은 literal 부분검색과 대소문자 무시를 함께 지원해야 합니다.");
            Check((await WorkshopListAsync(game, author, query + "&mine=true&language=ko-KR")).GetProperty("Total").GetInt32() == 3
                && (await WorkshopListAsync(game, viewer, query + "&mine=true")).GetProperty("Total").GetInt32() == 0
                && (await WorkshopListAsync(game, viewer, query + "&language=en")).GetProperty("Total").GetInt32() == 0,
                "검색 조건은 개인 소유/언어 필터와 함께 적용해야 합니다.");
            await WorkshopListAsync(game, viewer, "?search=" + new string('x', TopicWorkshopRules.MAX_SEARCH_LENGTH));
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "?search=" + new string('x', TopicWorkshopRules.MAX_SEARCH_LENGTH + 1), null,
                viewer.Session.SessionToken, HttpStatusCode.BadRequest, "TopicWorkshopInvalidSearch");
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "?search=" + Uri.EscapeDataString("검색\n값"), null,
                viewer.Session.SessionToken, HttpStatusCode.BadRequest, "TopicWorkshopInvalidSearch");
            await WorkshopErrorAsync(game, HttpMethod.Get, WORKSHOP_PATH + "?sort=" + Uri.EscapeDataString("popular;DROP TABLE"), null,
                viewer.Session.SessionToken, HttpStatusCode.BadRequest, "TopicWorkshopInvalidSort");
        }
        finally
        {
            foreach (string id in ids) await WorkshopDeleteAsync(game, id, author.Session.SessionToken);
        }
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM \"TopicWorkshopRecommendation\" WHERE \"TopicId\"=ANY($1)", owner))
        {
            command.Parameters.AddWithValue(ids.Select(Guid.Parse).ToArray());
            Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 0, "게시물 삭제는 추천 고유 행도 함께 제거해야 합니다.");
        }
        Report("literal 검색·최신/다운로드/추천 정렬·안정 페이지·계정별 동시 추천/해제·개인 상태·삭제/권한 검증");
    }
}
