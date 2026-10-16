using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyAdvertisingDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439, "광고 검증은 로컬 임시 PostgreSQL 25439 포트만 사용합니다.");
        scoped.SearchPath = "drawliar_advertising_" + Guid.NewGuid().ToString("N"); scoped.Pooling = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        async Task<int> Sql(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, owner);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return await command.ExecuteNonQueryAsync();
        }
        async Task<long> Count(string table)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM \"" + table + "\"", owner);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        await Sql("CREATE SCHEMA \"" + scoped.SearchPath + "\"");
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync(); await database.InitializeAsync();
            var values = new Dictionary<string, string?>
            {
                ["Advertising:Enabled"] = "true", ["Advertising:Android:Enabled"] = "true",
                ["Advertising:Android:BannerAdUnitId"] = "ca-app-pub-3940256099942544/6300978111",
                ["Advertising:Android:RewardedAdUnitId"] = "ca-app-pub-3940256099942544/5224354917",
                ["Advertising:Web:Enabled"] = "true", ["Advertising:Web:PublisherId"] = "ca-pub-1234567890123456",
                ["Advertising:Web:BannerSlotId"] = "1234567890", ["Advertising:Web:AllowClientVerification"] = "true"
            };
            var settings = new AdvertisingSettings(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            using var http = new HttpClient(new AdKeyHandler("{}"));
            var service = new AdvertisingService(database, settings, new AdMobSsvVerifier(http));
            Guid account = await database.DevelopmentAccountAsync("광고보상검증"), other = await database.DevelopmentAccountAsync("광고다른계정");
            const string session = "test-session";
            AdsRewardBeginRequest Request(string platform, string? operation = null) => new() { Platform = platform, OperationId = operation ?? Guid.NewGuid().ToString() };
            var pc = await service.StatusAsync(account, "Windows");
            Check(!pc.BannerEnabled && !pc.RewardedEnabled && !pc.CanClaimDailyReward && pc.BannerAdUnitId == "", "PC에는 광고·보상 상품·빨간 점이 없어야 합니다.");
            await AdsError(() => service.BeginAsync(account, session, Request("Windows")), "InvalidAdReward");
            var status = await service.StatusAsync(account, AdsRules.ANDROID);
            Check(status.BannerEnabled && status.CanClaimDailyReward && status.RewardPaidGems == 5, "미수령 모바일 사용자는 배너·일일 보상·빨간 점을 받을 수 있어야 합니다.");
            var begin = Request(AdsRules.ANDROID);
            var first = await service.BeginAsync(account, session, begin);
            Check(first.State == AdsRules.PENDING && first.Profile.PaidGems == 0
                && (await service.BeginAsync(account, session, begin)).Ticket.Ticket == first.Ticket.Ticket, "광고 시작·재시도는 보석을 지급하지 않고 같은 티켓을 재사용해야 합니다.");
            await AdsError(() => service.RewardAsync(other, AdsRules.ANDROID, first.Ticket.Ticket), "AdRewardNotFound");
            await AdsError(() => service.CompleteWebAsync(account, session, new AdsRewardCompleteRequest { Platform = AdsRules.WEB_GL, Ticket = first.Ticket.Ticket }), "InvalidAdReward");
            var verified = new VerifiedAdReward(first.Ticket.Ticket, first.Ticket.UserId, "ad-transaction-1", DateTimeOffset.UtcNow);
            await AdsError(() => database.CompleteVerifiedAdRewardAsync(verified with { UserId = new string('f', 64) }, settings), "InvalidAdVerification");
            await AdsError(() => database.CompleteVerifiedAdRewardAsync(verified with { RewardedAt = DateTimeOffset.UtcNow.AddDays(1) }, settings), "InvalidAdVerification");
            await Sql("CREATE FUNCTION fail_ad_ledger() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'isolated rollback test'; END; $$; CREATE TRIGGER fail_ad_ledger BEFORE INSERT ON \"CommerceLedger\" FOR EACH ROW EXECUTE FUNCTION fail_ad_ledger()");
            try { await database.CompleteVerifiedAdRewardAsync(verified, settings); throw new InvalidOperationException("광고 원장 실패가 무시되었습니다."); }
            catch (PostgresException exception) when (exception.SqlState == "P0001") { }
            Check((await database.ProfileAsync(account)).PaidGems == 0 && await Count("AdRewardDailyClaim") == 0
                && await Count("AdRewardTransaction") == 0 && (await service.RewardAsync(account, AdsRules.ANDROID, first.Ticket.Ticket)).State == AdsRules.PENDING,
                "원장 실패 시 보석·일일한도·광고거래·완료 상태를 모두 롤백해야 합니다.");
            await Sql("DROP TRIGGER fail_ad_ledger ON \"CommerceLedger\"; DROP FUNCTION fail_ad_ledger()");
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => database.CompleteVerifiedAdRewardAsync(verified, settings)));
            var done = await service.RewardAsync(account, AdsRules.ANDROID, first.Ticket.Ticket);
            Check(done.State == AdsRules.COMPLETED && done.Profile.PaidGems == 5 && done.Status.ClaimedToday && !done.Status.CanClaimDailyReward
                && await Count("AdRewardDailyClaim") == 1 && await Count("AdRewardTransaction") == 1 && await Count("CommerceLedger") == 1,
                "동시 SSV 12회는 하루 보석5·원장1건만 지급하고 빨간 점을 해제해야 합니다.");
            var daily = await service.BeginAsync(account, session, Request(AdsRules.WEB_GL));
            Check(daily.State == AdsRules.COMPLETED && daily.Profile.PaidGems == 5, "다른 플랫폼·다른 기기에서도 같은 날 보상은 중복 지급하면 안 됩니다.");
            await Sql("UPDATE \"AdRewardDailyClaim\" SET \"RewardDay\"=\"RewardDay\"-1 WHERE \"AccountId\"=$1", account);
            await Sql("UPDATE \"AdRewardTicket\" SET \"RewardDay\"=\"RewardDay\"-1 WHERE \"AccountId\"=$1", account);
            Check((await service.StatusAsync(account, AdsRules.ANDROID)).CanClaimDailyReward, "UTC 날짜가 바뀌면 이전 일자 수령 기록과 독립적으로 다시 수령할 수 있어야 합니다.");
            var nextDay = await service.BeginAsync(account, session, Request(AdsRules.ANDROID));
            Check(nextDay.State == AdsRules.PENDING && nextDay.Ticket.Ticket != first.Ticket.Ticket, "새 날짜는 새 광고 티켓을 발급해야 합니다.");
            await database.CompleteVerifiedAdRewardAsync(new VerifiedAdReward(nextDay.Ticket.Ticket, nextDay.Ticket.UserId,
                "ad-transaction-next-day", DateTimeOffset.UtcNow), settings);
            Check((await database.ProfileAsync(account)).PaidGems == 10, "서로 다른 UTC 일자는 각각 한 번 보상을 지급해야 합니다.");
            var stolen = await service.BeginAsync(other, session, Request(AdsRules.ANDROID));
            await AdsError(() => database.CompleteVerifiedAdRewardAsync(new VerifiedAdReward(stolen.Ticket.Ticket, stolen.Ticket.UserId,
                "ad-transaction-1", DateTimeOffset.UtcNow), settings), "AdTransactionAlreadyClaimed");
            var web = await service.BeginAsync(other, session, Request(AdsRules.WEB_GL));
            var complete = new AdsRewardCompleteRequest { Platform = AdsRules.WEB_GL, Ticket = web.Ticket.Ticket };
            await AdsError(() => service.CompleteWebAsync(other, "another-session", complete), "InvalidAdReward");
            await AdsError(() => service.CompleteWebAsync(other, session, complete), "AdRewardNotReady");
            await Sql("UPDATE \"AdRewardTicket\" SET \"CreatedAt\"=clock_timestamp()-interval '31 seconds' WHERE \"Ticket\"=$1", web.Ticket.Ticket);
            var webCompleted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CompleteWebAsync(other, session, complete)));
            Check(webCompleted.All(response => response.State == AdsRules.COMPLETED && response.Profile.PaidGems == 5), "명시적으로 허용한 웹 완료도 동일 세션·시간·원자적 일일한도를 지켜야 합니다.");
            Guid expired = await database.DevelopmentAccountAsync("광고만료검증");
            var expiring = await service.BeginAsync(expired, session, Request(AdsRules.WEB_GL));
            await Sql("UPDATE \"AdRewardTicket\" SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"Ticket\"=$1", expiring.Ticket.Ticket);
            await AdsError(() => service.CompleteWebAsync(expired, session, new AdsRewardCompleteRequest { Platform = AdsRules.WEB_GL, Ticket = expiring.Ticket.Ticket }), "AdRewardExpired");
            Check((await service.RewardAsync(expired, AdsRules.WEB_GL, expiring.Ticket.Ticket)).State == AdsRules.EXPIRED, "만료 티켓은 지급하지 않고 만료 상태를 알려야 합니다.");
            var wrongDay = await service.BeginAsync(expired, session, Request(AdsRules.WEB_GL));
            await Sql("UPDATE \"AdRewardTicket\" SET \"RewardDay\"=\"RewardDay\"-1 WHERE \"Ticket\"=$1", wrongDay.Ticket.Ticket);
            await AdsError(() => service.CompleteWebAsync(expired, session, new AdsRewardCompleteRequest { Platform = AdsRules.WEB_GL, Ticket = wrongDay.Ticket.Ticket }), "AdRewardExpired");
            Guid subscriber = await database.DevelopmentAccountAsync("광고구독검증");
            await Sql("UPDATE \"Account\" SET \"SubscriptionExpiresAt\"=clock_timestamp()+interval '1 day' WHERE \"Id\"=$1", subscriber);
            var subscribed = await service.StatusAsync(subscriber, AdsRules.ANDROID);
            Check(subscribed.AdsRemoved && !subscribed.BannerEnabled && subscribed.CanClaimDailyReward
                && subscribed.BannerAdUnitId == "" && subscribed.RewardedAdUnitId == "", "화가패스는 광고 ID와 배너를 숨기고 일일 보상은 유지해야 합니다.");
            var subscriberGrants = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.BeginAsync(subscriber, session, Request(AdsRules.ANDROID))));
            Check(subscriberGrants.All(response => response.State == AdsRules.COMPLETED && response.Profile.PaidGems == 5), "구독자는 광고 없이 하루 보상을 한 번만 즉시 수령해야 합니다.");
            Guid upgraded = await database.DevelopmentAccountAsync("광고중구독전환");
            var upgradeOperation = Request(AdsRules.ANDROID);
            var beforeUpgrade = await service.BeginAsync(upgraded, session, upgradeOperation);
            await Sql("UPDATE \"Account\" SET \"PaidGems\"=300 WHERE \"Id\"=$1", upgraded);
            await database.PurchaseCommerceAsync(upgraded, new CommercePurchaseRequest
            { ProductId = CommerceRules.PAINTER_PRODUCT_ID, OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 300 }, default);
            var upgradedGrants = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.BeginAsync(upgraded, session, upgradeOperation)));
            Check(upgradedGrants.All(response => response.State == AdsRules.COMPLETED && response.Profile.PaidGems == 5
                && response.Ticket.Ticket == beforeUpgrade.Ticket.Ticket && response.Status.AdsRemoved),
                "광고 취소 후 화가패스를 구매하면 같은 작업의 대기 티켓을 광고 없이 한 번만 즉시 수령해야 합니다.");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.CompleteVerifiedAdRewardAsync(new VerifiedAdReward(
                beforeUpgrade.Ticket.Ticket, beforeUpgrade.Ticket.UserId, "upgrade-late-ssv", DateTimeOffset.UtcNow), settings)));
            Check((await database.ProfileAsync(upgraded)).PaidGems == 5,
                "구독으로 수령한 기존 광고 티켓에 늦은 SSV가 도착해도 보상을 중복 지급하면 안 됩니다.");
            Guid expiredUpgrade = await database.DevelopmentAccountAsync("만료광고구독전환");
            var expiredUpgradeOperation = Request(AdsRules.ANDROID);
            var expiredBeforeUpgrade = await service.BeginAsync(expiredUpgrade, session, expiredUpgradeOperation);
            await Sql("UPDATE \"AdRewardTicket\" SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"Ticket\"=$1", expiredBeforeUpgrade.Ticket.Ticket);
            await Sql("UPDATE \"Account\" SET \"PaidGems\"=300 WHERE \"Id\"=$1", expiredUpgrade);
            await database.PurchaseCommerceAsync(expiredUpgrade, new CommercePurchaseRequest
            { ProductId = CommerceRules.PAINTER_PRODUCT_ID, OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 300 }, default);
            Check((await service.BeginAsync(expiredUpgrade, session, expiredUpgradeOperation)).State == AdsRules.EXPIRED
                && (await database.ProfileAsync(expiredUpgrade)).PaidGems == 0,
                "구독 전 만료된 티켓은 되살리지 않아야 합니다.");
            Check((await service.BeginAsync(expiredUpgrade, session, Request(AdsRules.ANDROID))).Profile.PaidGems == 5,
                "만료 티켓을 가진 구독자도 새 작업으로 당일 무료 보상을 수령할 수 있어야 합니다.");
            Guid overflow = await database.DevelopmentAccountAsync("광고잔액한도");
            await Sql("UPDATE \"Account\" SET \"PaidGems\"=$2,\"SubscriptionExpiresAt\"=clock_timestamp()+interval '1 day' WHERE \"Id\"=$1", overflow, int.MaxValue);
            long claimCount = await Count("AdRewardDailyClaim"), ledgerCount = await Count("CommerceLedger");
            await AdsError(() => service.BeginAsync(overflow, session, Request(AdsRules.WEB_GL)), "PaidGemLimit");
            Check((await database.ProfileAsync(overflow)).PaidGems == int.MaxValue && await Count("AdRewardDailyClaim") == claimCount
                && await Count("CommerceLedger") == ledgerCount, "잔액 상한 실패는 티켓·일일한도·원장 지급을 롤백해야 합니다.");
            values["Advertising:Web:AllowClientVerification"] = "false";
            var safe = new AdvertisingService(database, new AdvertisingSettings(new ConfigurationBuilder().AddInMemoryCollection(values).Build()), new AdMobSsvVerifier(http));
            Check(!(await safe.StatusAsync(expired, AdsRules.WEB_GL)).CanClaimDailyReward, "웹 클라이언트 신뢰가 비활성이면 보상 빨간 점을 켜면 안 됩니다.");
            await AdsError(() => safe.CompleteWebAsync(other, session, complete), "AdsUnavailable");
            Console.WriteLine("광고 DB 원자지급·동시요청·계정/세션/플랫폼/일자·만료·거래중복·구독무광고·웹 opt-in·잔액상한 검증 통과");
        }
        finally { await Sql("DROP SCHEMA \"" + scoped.SearchPath + "\" CASCADE"); }
    }
}
