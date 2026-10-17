using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyMailDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439, "우편 검증은 로컬 임시 PostgreSQL 25439 포트만 사용합니다.");
        scoped.SearchPath = "drawliar_mail_" + Guid.NewGuid().ToString("N");
        scoped.Pooling = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken cancellationToken = timeout.Token;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync(cancellationToken);
        async Task<int> Sql(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, owner);
            foreach (object value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        async Task<long> Number(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, owner);
            foreach (object value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        await Sql("CREATE SCHEMA \"" + scoped.SearchPath + "\"");
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            await database.InitializeAsync();
            Guid account = await database.DevelopmentAccountAsync("우편수령검증");
            Guid other = await database.DevelopmentAccountAsync("우편다른계정");
            var initial = await database.ProfileAsync(account);
            AdminMailRequest Request(string title, int coins = 17, int gems = 3) => new()
            {
                AccountIds = [account.ToString()], Title = title, Message = "우편 보상", Coins = coins, PaidGems = gems
            };
            MailClaimRequest Claim(params string[] ids) => new() { MailIds = ids };
            async Task<AdminMailData> Sent(string title) => (await database.AdminMailsAsync(cancellationToken)).Single(mail => mail.Title == title);
            async Task<long> Claims(Guid id) => await Number("SELECT count(*) FROM \"MailClaim\" WHERE \"AccountId\"=$1", id);
            async Task<long> Ledgers(Guid id) => await Number("SELECT count(*) FROM \"CommerceLedger\" WHERE \"AccountId\"=$1 AND \"Kind\"='MailReward'", id);

            Check((await database.MailsAsync(account, cancellationToken)).Mails.Length == 0
                && (await database.SocialInboxAsync(account, cancellationToken)).MailCount == 0, "새 계정의 우편 목록·받은편지함 수는 비어야 합니다.");
            var individualRequest = Request("  개별 보상  ");
            individualRequest.AccountIds = [account.ToString(), account.ToString()];
            individualRequest.Message = "첫 줄\n둘째 줄\r\n제어\t\0\u0001제거";
            Check((await database.AdminSendMailAsync(individualRequest, cancellationToken)).Created == 1, "중복 대상 계정은 우편 한 건만 생성해야 합니다.");
            var individual = await Sent("개별 보상");
            Check(individual.AccountId == account.ToString() && individual.AccountName == initial.DisplayName
                && individual.Message == "첫 줄\n둘째 줄\r\n제어제거" && individual.ClaimCount == 0
                && DateTimeOffset.Parse(individual.ExpiresAt) - DateTimeOffset.Parse(individual.CreatedAt) == TimeSpan.FromDays(30),
                "운영 우편은 제목을 trim하고 개행 외 제어문자를 제거하며 계정 이름·기본 30일 만료를 반환해야 합니다.");
            var list = (await database.MailsAsync(account, cancellationToken)).Mails;
            Check(list.Length == 1 && list[0].MailId == individual.MailId && list[0].Title == individual.Title
                && list[0].Message == individual.Message && list[0].Coins == 17 && list[0].PaidGems == 3
                && list[0].CreatedAt == individual.CreatedAt && list[0].ExpiresAt == individual.ExpiresAt
                && (await database.SocialInboxAsync(account, cancellationToken)).MailCount == 1
                && (await database.MailsAsync(other, cancellationToken)).Mails.Length == 0,
                "개별 우편은 본인 목록·받은편지함 수에만 표시해야 합니다.");
            var unauthorized = await database.ClaimMailAsync(other, Claim(individual.MailId), cancellationToken);
            Check(unauthorized.ClaimedCount == 0 && unauthorized.Coins == 0 && unauthorized.PaidGems == 0
                && await Claims(other) == 0 && await Ledgers(other) == 0, "타인의 개별 우편은 수령·원장 기록 없이 건너뛰어야 합니다.");
            var received = await database.ClaimMailAsync(account, Claim(individual.MailId, individual.MailId, Guid.NewGuid().ToString()), cancellationToken);
            Check(received.ClaimedCount == 1 && received.Coins == 17 && received.PaidGems == 3
                && received.Profile.Coins == initial.Coins + 17 && received.Profile.PaidGems == initial.PaidGems + 3
                && await Claims(account) == 1 && await Ledgers(account) == 1,
                "중복 우편 ID·없는 우편을 섞어도 실제 새 우편 한 건의 코인·보석만 지급해야 합니다.");
            Check(await Number("""
                SELECT count(*) FROM "CommerceLedger" WHERE "AccountId"=$1 AND "OperationId"=$2
                    AND "Kind"='MailReward' AND "ProductId"='Mail' AND "PaidGemsDelta"=3 AND "BalanceAfter"=$3
                """, account, Guid.Parse(individual.MailId), received.Profile.PaidGems) == 1,
                "보석 지급 원장은 MailReward·Mail·MailId와 보석 변동·지급 후 잔액을 저장해야 합니다.");
            var retry = await database.ClaimMailAsync(account, Claim(individual.MailId), cancellationToken);
            Check(retry.ClaimedCount == 0 && retry.Coins == 0 && retry.PaidGems == 0
                && retry.Profile.Coins == received.Profile.Coins && retry.Profile.PaidGems == received.Profile.PaidGems
                && await Claims(account) == 1 && await Ledgers(account) == 1
                && (await database.MailsAsync(account, cancellationToken)).Mails.Length == 0
                && (await database.SocialInboxAsync(account, cancellationToken)).MailCount == 0
                && (await Sent("개별 보상")).ClaimCount == 1,
                "수령 재시도는 멱등이어야 하며 수령한 우편은 목록·받은편지함 수에서 사라져야 합니다.");

            await database.AdminSendMailAsync(Request("동시 수령"), cancellationToken);
            var concurrentMail = await Sent("동시 수령");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => database.ClaimMailAsync(account, Claim(concurrentMail.MailId), cancellationToken)));
            var concurrentProfile = await database.ProfileAsync(account);
            Check(concurrent.Sum(result => result.ClaimedCount) == 1 && concurrent.Sum(result => result.Coins) == 17
                && concurrent.Sum(result => result.PaidGems) == 3 && concurrentProfile.Coins == initial.Coins + 34
                && concurrentProfile.PaidGems == initial.PaidGems + 6 && await Claims(account) == 2 && await Ledgers(account) == 2,
                "동시 수령 12회는 같은 계정 우편을 한 번만 원자적으로 지급해야 합니다.");

            var broadcastRequest = Request("전체 보상", 11, 2);
            broadcastRequest.AccountIds = [];
            broadcastRequest.SendToAll = true;
            Check((await database.AdminSendMailAsync(broadcastRequest, cancellationToken)).Created == 1, "전체 발송은 우편 한 행만 생성해야 합니다.");
            var broadcast = await Sent("전체 보상");
            Check(broadcast.AccountId == "" && broadcast.AccountName == ""
                && await Number("SELECT count(*) FROM \"Mail\" WHERE \"Id\"=$1 AND \"AccountId\" IS NULL", Guid.Parse(broadcast.MailId)) == 1,
                "전체 우편은 NULL 수신 계정 한 행과 빈 대상 이름을 반환해야 합니다.");
            Guid newcomer = await database.DevelopmentAccountAsync("우편신규가입");
            foreach (Guid recipient in new[] { account, other, newcomer })
            {
                var before = await database.ProfileAsync(recipient);
                Check((await database.MailsAsync(recipient, cancellationToken)).Mails.Any(mail => mail.MailId == broadcast.MailId)
                    && (await database.SocialInboxAsync(recipient, cancellationToken)).MailCount == 1, "발송 후 가입한 계정을 포함해 전체 우편을 조회할 수 있어야 합니다.");
                var first = await database.ClaimMailAsync(recipient, Claim(broadcast.MailId), cancellationToken);
                var repeated = await database.ClaimMailAsync(recipient, Claim(broadcast.MailId), cancellationToken);
                Check(first.ClaimedCount == 1 && first.Coins == 11 && first.PaidGems == 2
                    && first.Profile.Coins == before.Coins + 11 && first.Profile.PaidGems == before.PaidGems + 2
                    && repeated.ClaimedCount == 0 && repeated.Profile.Coins == first.Profile.Coins
                    && repeated.Profile.PaidGems == first.Profile.PaidGems, "전체 우편은 계정별 한 번씩만 지급해야 합니다.");
            }
            Check((await Sent("전체 보상")).ClaimCount == 3, "전체 우편의 운영 수령 수는 서로 다른 수령 계정을 집계해야 합니다.");
            await database.AdminCancelMailAsync(Guid.Parse(broadcast.MailId), cancellationToken);
            Check((await database.ProfileAsync(account)).Coins == concurrentProfile.Coins + 11
                && (await database.ProfileAsync(account)).PaidGems == concurrentProfile.PaidGems + 2,
                "발송 취소는 이미 지급한 보상을 회수하면 안 됩니다.");

            await database.AdminSendMailAsync(Request("취소 보상"), cancellationToken);
            await database.AdminSendMailAsync(Request("만료 보상"), cancellationToken);
            var cancelled = await Sent("취소 보상");
            var expired = await Sent("만료 보상");
            await database.AdminCancelMailAsync(Guid.Parse(cancelled.MailId), cancellationToken);
            await database.AdminCancelMailAsync(Guid.Parse(cancelled.MailId), cancellationToken);
            await Sql("UPDATE \"Mail\" SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"Id\"=$1", Guid.Parse(expired.MailId));
            var beforeExpired = await database.ProfileAsync(account);
            long claimsBeforeExpired = await Claims(account), ledgersBeforeExpired = await Ledgers(account);
            var skipped = await database.ClaimMailAsync(account, Claim(cancelled.MailId, expired.MailId, individual.MailId), cancellationToken);
            Check(skipped.ClaimedCount == 0 && skipped.Coins == 0 && skipped.PaidGems == 0
                && skipped.Profile.Coins == beforeExpired.Coins && skipped.Profile.PaidGems == beforeExpired.PaidGems
                && await Claims(account) == claimsBeforeExpired && await Ledgers(account) == ledgersBeforeExpired
                && (await database.MailsAsync(account, cancellationToken)).Mails.Length == 0
                && (await database.SocialInboxAsync(account, cancellationToken)).MailCount == 0,
                "취소·만료·기수령 우편은 목록과 받은편지함에서 숨기고 수령 시 조용히 건너뛰어야 합니다.");
            await MailError(() => database.AdminCancelMailAsync(Guid.NewGuid(), cancellationToken), "MailUnavailable", 404);

            long mailCount = await Number("SELECT count(*) FROM \"Mail\"");
            var invalidRequests = new List<AdminMailRequest>();
            void Invalid(Action<AdminMailRequest> change) { var request = Request("입력 검증"); change(request); invalidRequests.Add(request); }
            Invalid(request => request.Title = " ");
            Invalid(request => request.Title = new string('가', AdminMailRequest.MAX_TITLE_LENGTH + 1));
            Invalid(request => request.Message = new string('가', AdminMailRequest.MAX_MESSAGE_LENGTH + 1));
            Invalid(request => request.Coins = -1);
            Invalid(request => request.Coins = AdminMailRequest.MAX_COINS + 1);
            Invalid(request => request.PaidGems = -1);
            Invalid(request => request.PaidGems = AdminMailRequest.MAX_PAID_GEMS + 1);
            Invalid(request => { request.Coins = 0; request.PaidGems = 0; });
            Invalid(request => request.ExpiresInDays = 0);
            Invalid(request => request.ExpiresInDays = AdminMailRequest.MAX_EXPIRES_IN_DAYS + 1);
            Invalid(request => request.SendToAll = true);
            Invalid(request => request.AccountIds = []);
            Invalid(request => request.AccountIds = ["invalid-account"]);
            Invalid(request => request.AccountIds = Enumerable.Range(0, AdminMailRequest.MAX_RECIPIENTS + 1).Select(_ => Guid.NewGuid().ToString()).ToArray());
            foreach (var request in invalidRequests)
                await MailError(() => database.AdminSendMailAsync(request, cancellationToken), "InvalidMail", 400);
            var missing = Request("없는 계정");
            missing.AccountIds = [account.ToString(), Guid.NewGuid().ToString()];
            await MailError(() => database.AdminSendMailAsync(missing, cancellationToken), "AccountNotFound", 404);
            Check(await Number("SELECT count(*) FROM \"Mail\"") == mailCount, "잘못된 운영 입력·일부 존재하지 않는 수신 계정은 우편을 생성하면 안 됩니다.");
            foreach (var request in new[]
            {
                Claim(), Claim("invalid-mail"), Claim(individual.MailId, "invalid-mail"), new MailClaimRequest { MailIds = null! },
                Claim(Enumerable.Range(0, MailClaimRequest.MAX_MAILS + 1).Select(_ => Guid.NewGuid().ToString()).ToArray())
            })
                await MailError(() => database.ClaimMailAsync(account, request, cancellationToken), "InvalidMail", 400);

            Guid overflow = await database.DevelopmentAccountAsync("우편잔액상한");
            foreach (string title in new[] { "상한 보상 하나", "상한 보상 둘" })
            {
                var request = Request(title, 2, 3);
                request.AccountIds = [overflow.ToString()];
                await database.AdminSendMailAsync(request, cancellationToken);
            }
            string[] overflowIds = [(await Sent("상한 보상 하나")).MailId, (await Sent("상한 보상 둘")).MailId];
            await Sql("UPDATE \"Account\" SET \"Coins\"=$2,\"PaidGems\"=20 WHERE \"Id\"=$1", overflow, int.MaxValue - 3);
            await MailError(() => database.ClaimMailAsync(overflow, Claim(overflowIds), cancellationToken), "CoinLimit", 409);
            var afterCoinLimit = await database.ProfileAsync(overflow);
            Check(afterCoinLimit.Coins == int.MaxValue - 3 && afterCoinLimit.PaidGems == 20
                && await Claims(overflow) == 0 && await Ledgers(overflow) == 0
                && (await database.SocialInboxAsync(overflow, cancellationToken)).MailCount == 2,
                "코인 합계 오버플로는 모든 우편 수령·보석·코인·원장을 롤백해야 합니다.");
            await Sql("UPDATE \"Account\" SET \"Coins\"=10,\"PaidGems\"=$2 WHERE \"Id\"=$1", overflow, int.MaxValue - 5);
            await MailError(() => database.ClaimMailAsync(overflow, Claim(overflowIds), cancellationToken), "PaidGemLimit", 409);
            var afterGemLimit = await database.ProfileAsync(overflow);
            Check(afterGemLimit.Coins == 10 && afterGemLimit.PaidGems == int.MaxValue - 5
                && await Claims(overflow) == 0 && await Ledgers(overflow) == 0
                && (await database.MailsAsync(overflow, cancellationToken)).Mails.Length == 2,
                "보석 합계 오버플로는 모든 우편 수령·코인·보석·원장을 롤백해야 합니다.");
            await Sql("UPDATE \"Account\" SET \"PaidGems\"=$2 WHERE \"Id\"=$1", overflow, int.MaxValue - 6);
            var boundary = await database.ClaimMailAsync(overflow, Claim(overflowIds), cancellationToken);
            Check(boundary.ClaimedCount == 2 && boundary.Coins == 4 && boundary.PaidGems == 6
                && boundary.Profile.Coins == 14 && boundary.Profile.PaidGems == int.MaxValue
                && await Claims(overflow) == 2 && await Ledgers(overflow) == 2,
                "오버플로 롤백 후 재수령은 정상 지급하고 int 최대 잔액까지 허용해야 합니다.");

            Guid limited = await database.DevelopmentAccountAsync("우편목록한도");
            await Sql("""
                INSERT INTO "Mail" ("Id","AccountId","Title","Message","Coins","PaidGems","CreatedAt","ExpiresAt")
                SELECT gen_random_uuid(),$1,'목록 한도','',1,0,now()-i*interval '1 second',now()+interval '1 day'
                FROM generate_series(1,105) i
                """, limited);
            var limitedMails = (await database.MailsAsync(limited, cancellationToken)).Mails;
            Check(limitedMails.Length == MailClaimRequest.MAX_MAILS
                && limitedMails.Select(mail => DateTimeOffset.Parse(mail.CreatedAt)).SequenceEqual(limitedMails
                    .Select(mail => DateTimeOffset.Parse(mail.CreatedAt)).OrderDescending())
                && (await database.SocialInboxAsync(limited, cancellationToken)).MailCount == 105,
                "목록은 최신순 100개로 제한하고 받은편지함 수는 남은 우편 전체를 집계해야 합니다.");
            var coinOnly = await database.ClaimMailAsync(limited, Claim(limitedMails.Select(mail => mail.MailId).ToArray()), cancellationToken);
            Check(coinOnly.ClaimedCount == 100 && coinOnly.Coins == 100 && coinOnly.PaidGems == 0
                && await Ledgers(limited) == 0 && (await database.MailsAsync(limited, cancellationToken)).Mails.Length == 5
                && (await database.SocialInboxAsync(limited, cancellationToken)).MailCount == 5,
                "100개 일괄 수령 후 다음 우편을 조회하고 코인 전용 우편은 보석 원장을 만들지 않아야 합니다.");
            var maximum = Request(new string('가', AdminMailRequest.MAX_TITLE_LENGTH), AdminMailRequest.MAX_COINS, AdminMailRequest.MAX_PAID_GEMS);
            maximum.Message = new string('나', AdminMailRequest.MAX_MESSAGE_LENGTH);
            maximum.ExpiresInDays = AdminMailRequest.MAX_EXPIRES_IN_DAYS;
            maximum.AccountIds = [account.ToString(), other.ToString(), account.ToString()];
            Check((await database.AdminSendMailAsync(maximum, cancellationToken)).Created == 2,
                "최대 길이·보상·기간의 개별 다중 발송은 중복 제거 후 계정마다 한 행을 생성해야 합니다.");
            var maximumMails = (await database.AdminMailsAsync(cancellationToken)).Where(mail => mail.Title == maximum.Title).ToArray();
            Check(maximumMails.Length == 2 && maximumMails.Select(mail => mail.AccountId).ToHashSet().SetEquals(maximum.AccountIds)
                && maximumMails.All(mail => mail.Message == maximum.Message && mail.Coins == maximum.Coins && mail.PaidGems == maximum.PaidGems
                    && DateTimeOffset.Parse(mail.ExpiresAt) - DateTimeOffset.Parse(mail.CreatedAt) == TimeSpan.FromDays(AdminMailRequest.MAX_EXPIRES_IN_DAYS)),
                "허용된 최대 입력과 다중 대상은 각 우편에 정확히 저장해야 합니다.");
            await Sql("""
                INSERT INTO "Mail" ("Id","AccountId","Title","Message","Coins","PaidGems","CreatedAt","ExpiresAt")
                SELECT gen_random_uuid(),$1,'운영 목록 한도','',1,0,now()-i*interval '1 second',now()+interval '1 day'
                FROM generate_series(1,220) i
                """, limited);
            var adminMails = await database.AdminMailsAsync(cancellationToken);
            Check(adminMails.Length == 200 && adminMails.Select(mail => DateTimeOffset.Parse(mail.CreatedAt))
                .SequenceEqual(adminMails.Select(mail => DateTimeOffset.Parse(mail.CreatedAt)).OrderDescending()),
                "운영 목록은 수령·만료 여부와 무관하게 최근 발송 우편 200개만 최신순으로 반환해야 합니다.");
            Console.WriteLine("우편 DB 개별·전체/신규계정·목록/받은편지함·동시/멱등 수령·원장·취소/만료·입력 검증·잔액 상한 롤백 검증 통과");
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DROP SCHEMA \"" + scoped.SearchPath + "\" CASCADE", owner);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task MailError(Func<Task> action, string code, int status)
    {
        try { await action(); throw new InvalidOperationException("우편 오류가 누락되었습니다: " + code); }
        catch (ApiException exception) when (exception.Code == code)
        {
            Check(exception.Status == status, "우편 오류 HTTP 상태가 다릅니다: " + code);
        }
    }
}
