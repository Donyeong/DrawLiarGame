using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DrawLiar;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyFriendCancellationAsync(string mainUrl)
    {
        Check(new Uri(mainUrl) == new Uri("http://127.0.0.1:25550"), "친구 신청 취소 검증은 전용 로컬 메인서버만 사용합니다.");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_SOCIAL_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_SOCIAL_TEST_DATABASE가 필요합니다."));
        Check(settings.Host == "127.0.0.1" && settings.Port == 25539 && settings.Database == "postgres"
            && Regex.IsMatch(settings.SearchPath ?? "", "^drawliar_social_test_[0-9a-f]{32}$"),
            "친구 신청 취소 검증은 무작위 스키마의 전용 로컬 PostgreSQL만 사용합니다.");
        await using var owner = new NpgsqlConnection(settings.ConnectionString);
        await owner.OpenAsync();
        using var main = Client(mainUrl);
        await RequireHealthAsync(main);
        var users = new List<TestUser>();
        for (int index = 0; index < 3; index++) users.Add(await GuestAndEnterAsync(main));
        Check(users.All(user => user.Login.GameServerUrl == "http://127.0.0.1:25560"), "취소 검증은 전용 로컬 게임서버만 사용합니다.");
        using var game = Client(users[0].Login.GameServerUrl);
        var sender = users[0];
        var receiver = users[1];
        var outsider = users[2];
        const string CANCEL_PATH = "/api/friends/cancel";
        var cancel = new FriendRequest { AccountId = receiver.Login.AccountId };
        var accept = new FriendRespondRequest { AccountId = sender.Login.AccountId, Accept = true };
        try
        {
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, cancel, null, HttpStatusCode.Unauthorized, "Unauthorized");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, cancel, sender.Login.SessionToken, HttpStatusCode.Unauthorized, "Unauthorized");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, new FriendRequest { AccountId = "invalid" },
                sender.Session.SessionToken, HttpStatusCode.BadRequest, "InvalidAccount");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, new FriendRequest { AccountId = sender.Login.AccountId },
                sender.Session.SessionToken, HttpStatusCode.BadRequest, "InvalidFriend");
            foreach (string accountId in new[] { Guid.NewGuid().ToString(), Guid.Empty.ToString(), receiver.Login.AccountId })
                await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, new FriendRequest { AccountId = accountId },
                    sender.Session.SessionToken, HttpStatusCode.NotFound, "FriendRequestNotFound");
            await PostNoContentAsync(game, "/api/friends/request", cancel, sender.Session.SessionToken);
            await CheckFriendCancellationStateAsync(game, sender, receiver, "Outgoing", "Incoming");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, new FriendRequest { AccountId = sender.Login.AccountId },
                receiver.Session.SessionToken, HttpStatusCode.NotFound, "FriendRequestNotFound");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH,
                new { AccountId = receiver.Login.AccountId, FromAccountId = sender.Login.AccountId }, outsider.Session.SessionToken,
                HttpStatusCode.NotFound, "FriendRequestNotFound");
            await CheckFriendCancellationStateAsync(game, sender, receiver, "Outgoing", "Incoming");
            Check((await GetAsync<FriendListResponse>(game, "/api/friends", outsider.Session.SessionToken)).Outgoing.Length == 0,
                "다른 계정이 발신자를 위조해 취소하거나 신청 상태를 만들 수 없어야 합니다.");
            Report("친구 신청 취소 인증·발신자 권한·역방향·잘못된 대상 검증");

            await PostNoContentAsync(game, CANCEL_PATH, cancel, sender.Session.SessionToken);
            await CheckFriendCancellationStateAsync(game, sender, receiver, "None", "None");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, cancel, sender.Session.SessionToken,
                HttpStatusCode.NotFound, "FriendRequestNotFound");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, "/api/friends/respond", accept, receiver.Session.SessionToken,
                HttpStatusCode.NotFound, "FriendRequestNotFound");
            await PostNoContentAsync(game, "/api/friends/request", cancel, sender.Session.SessionToken);
            await CheckFriendCancellationStateAsync(game, sender, receiver, "Outgoing", "Incoming");
            await PostNoContentAsync(game, "/api/friends/respond", accept, receiver.Session.SessionToken);
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, cancel, sender.Session.SessionToken,
                HttpStatusCode.NotFound, "FriendRequestNotFound");
            await CheckFriendCancellationStateAsync(game, sender, receiver, "Friends", "Friends");
            Report("취소 뒤 양쪽 목록·받은 알림·프로필 갱신·재신청·수락된 관계 보존 검증");

            for (int index = 0; index < 8; index++)
            {
                await RemoveCancellationFixtureFriendAsync(game, sender, receiver);
                await PostNoContentAsync(game, "/api/friends/request", cancel, sender.Session.SessionToken);
                var responses = await Task.WhenAll(SendAsync(game, CANCEL_PATH, cancel, sender.Session.SessionToken),
                    SendAsync(game, "/api/friends/respond", accept, receiver.Session.SessionToken));
                try
                {
                    Check(responses.Count(response => response.StatusCode == HttpStatusCode.NoContent) == 1
                        && responses.Count(response => response.StatusCode == HttpStatusCode.NotFound) == 1,
                        "동시 수락과 취소는 한 요청만 성공해야 합니다.");
                    var error = await responses.Single(response => response.StatusCode == HttpStatusCode.NotFound).Content.ReadFromJsonAsync<ApiError>(Json);
                    Check(error?.Code == "FriendRequestNotFound", "경쟁에서 처리된 신청은 동일한 미존재 오류를 반환해야 합니다.");
                    bool accepted = responses[1].StatusCode == HttpStatusCode.NoContent;
                    await using var command = new NpgsqlCommand("SELECT \"Accepted\" FROM \"Friendship\" WHERE \"FromAccountId\"=$1 AND \"ToAccountId\"=$2", owner);
                    command.Parameters.AddWithValue(Guid.Parse(sender.Login.AccountId));
                    command.Parameters.AddWithValue(Guid.Parse(receiver.Login.AccountId));
                    object? relation = await command.ExecuteScalarAsync();
                    Check(accepted ? relation is true : relation == null, "수락이 먼저 끝나면 친구가 유지되고 취소가 먼저 끝나면 신청이 없어야 합니다.");
                    await CheckFriendCancellationStateAsync(game, sender, receiver, accepted ? "Friends" : "None", accepted ? "Friends" : "None");
                }
                finally { foreach (var response in responses) response.Dispose(); }
            }
            await RemoveCancellationFixtureFriendAsync(game, sender, receiver);
            await PostNoContentAsync(game, "/api/friends/request", cancel, sender.Session.SessionToken);
            var repeats = await Task.WhenAll(SendAsync(game, CANCEL_PATH, cancel, sender.Session.SessionToken),
                SendAsync(game, CANCEL_PATH, cancel, sender.Session.SessionToken));
            try
            {
                Check(repeats.Count(response => response.StatusCode == HttpStatusCode.NoContent) == 1
                    && repeats.Count(response => response.StatusCode == HttpStatusCode.NotFound) == 1,
                    "같은 신청의 동시 취소는 한 번만 처리해야 합니다.");
            }
            finally { foreach (var response in repeats) response.Dispose(); }
            await CheckFriendCancellationStateAsync(game, sender, receiver, "None", "None");
            await PostNoContentAsync(game, "/api/friends/request", new FriendRequest { AccountId = sender.Login.AccountId }, receiver.Session.SessionToken);
            await CheckFriendCancellationStateAsync(game, sender, receiver, "Incoming", "Outgoing");
            await ExpectSocialErrorAsync(game, HttpMethod.Post, CANCEL_PATH, cancel, sender.Session.SessionToken,
                HttpStatusCode.NotFound, "FriendRequestNotFound");
            await PostNoContentAsync(game, CANCEL_PATH, new FriendRequest { AccountId = sender.Login.AccountId }, receiver.Session.SessionToken);
            await CheckFriendCancellationStateAsync(game, sender, receiver, "None", "None");
            Report("실제 PostgreSQL 동시 수락·취소 8회·중복 취소·역방향 재신청 검증");
        }
        finally
        {
            foreach (var user in users) await PostNoContentAsync(game, "/api/session/logout", new { }, user.Session.SessionToken);
        }
    }

    private static async Task CheckFriendCancellationStateAsync(HttpClient game, TestUser first, TestUser second, string firstState, string secondState)
    {
        foreach (var (user, other, expected) in new[] { (first, second, firstState), (second, first, secondState) })
        {
            var list = await GetAsync<FriendListResponse>(game, "/api/friends", user.Session.SessionToken);
            var inbox = await SocialInboxAsync(game, user);
            var profile = await GetAsync<PublicProfileData>(game, "/api/profiles/" + other.Login.AccountId, user.Session.SessionToken);
            Check(FriendCancellationState(list, other.Login.AccountId) == expected
                && FriendCancellationState(inbox.Friends, other.Login.AccountId) == expected && profile.Friendship == expected,
                "친구 목록·소셜 알림·공개 프로필은 동일한 신청 상태를 반환해야 합니다.");
        }
    }

    private static string FriendCancellationState(FriendListResponse list, string accountId)
    {
        bool friends = list.Friends.Any(friend => friend.AccountId == accountId);
        bool outgoing = list.Outgoing.Any(friend => friend.AccountId == accountId);
        bool incoming = list.Incoming.Any(friend => friend.AccountId == accountId);
        Check((friends ? 1 : 0) + (outgoing ? 1 : 0) + (incoming ? 1 : 0) <= 1, "한 관계를 여러 친구 상태에 동시에 표시하면 안 됩니다.");
        return friends ? "Friends" : outgoing ? "Outgoing" : incoming ? "Incoming" : "None";
    }

    private static async Task RemoveCancellationFixtureFriendAsync(HttpClient game, TestUser sender, TestUser receiver)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/friends/" + receiver.Login.AccountId);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sender.Session.SessionToken);
        using var response = await game.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.NoContent, "격리 검증 관계 정리는 기존 친구 삭제 API를 사용합니다.");
    }
}
