using System.Text.RegularExpressions;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyCommerceDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_COMMERCE_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_COMMERCE_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25839 && scoped.Username == "commerce_qa"
            && Regex.IsMatch(scoped.Database ?? "", "^drawliar_commerce_[0-9a-f]{32}$")
            && Regex.IsMatch(scoped.SearchPath ?? "", "^drawliar_commerce_[0-9a-f]{32}$"),
            "결제 DB 검증은 새 격리 localhost25839 클러스터·전용 사용자·고유 DB/schema만 허용합니다.");
        scoped.Pooling = false; scoped.IncludeErrorDetail = false;
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        string schema = scoped.SearchPath!;
        await CommerceSql(owner, "CREATE SCHEMA \"" + schema + "\"");
        try
        {
            await CommerceSql(owner, "CREATE TABLE \"SchemaVersion\" (\"Version\" integer PRIMARY KEY,\"AppliedAt\" timestamptz NOT NULL DEFAULT now())");
            var assembly = typeof(ServerDatabase).Assembly;
            foreach (string name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
            {
                int version = int.Parse(name.Split('.')[^2].Split('_')[0]);
                if (version >= 19) continue;
                using var stream = assembly.GetManifestResourceStream(name)!; using var reader = new StreamReader(stream);
                await CommerceSql(owner, await reader.ReadToEndAsync());
                await CommerceSql(owner, "INSERT INTO \"SchemaVersion\" (\"Version\") VALUES ($1)", version);
            }
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            Guid account = await database.DevelopmentAccountAsync("결제DB검증");
            Guid other = await database.DevelopmentAccountAsync("결제다른계정");
            await database.InitializeAsync(); await database.InitializeAsync();
            var fresh = await database.ProfileAsync(account);
            Check(fresh.Coins == 500 && fresh.PaidGems == 0 && !fresh.HasPainterSubscription && !fresh.ShowSubscriberBadge
                && fresh.SubscriptionExpiresAt == "" && (await CommerceScalar(owner, "SELECT count(*) FROM \"SchemaVersion\" WHERE \"Version\"=19")) == 1,
                "V18 기존 계정은 V19 두 번 초기화에도 코인 유지·보석0·구독 없음으로 이전해야 합니다.");
            CheckPublicProfileFields(await database.PublicProfileAsync(other, account));
            var provider = new CommerceTestProvider(); var service = new CommerceService(database, new[] { provider });
            var operation = Guid.NewGuid().ToString();
            var prepared = await service.PrepareAsync(account, Prepare("gems-550", operation));
            Check((await service.PrepareAsync(account, Prepare("gems-550", operation))).Order.OrderId == prepared.Order.OrderId,
                "PostgreSQL Prepare 같은 operation은 주문 스냅샷을 재사용해야 합니다.");
            await CommerceError(() => service.PrepareAsync(account, Prepare("gems-100", operation)), "OperationConflict");
            await CommerceError(() => service.ConfirmAsync(other, Confirm(prepared, "valid")), "PaymentOrderNotFound");
            foreach (string invalid in new[] { "unpaid", "quantity", "order", "provider", "scope", "sku", "binding", "empty-transaction" })
                await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, invalid)), "InvalidPayment");
            await CommerceSql(owner, """
                CREATE FUNCTION commerce_fail_ledger() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'isolated rollback test'; END; $$;
                CREATE TRIGGER commerce_fail_ledger BEFORE INSERT ON "CommerceLedger" FOR EACH ROW EXECUTE FUNCTION commerce_fail_ledger();
                """);
            try { await service.ConfirmAsync(account, Confirm(prepared, "valid")); throw new InvalidOperationException("원장 오류가 결제 성공으로 처리되었습니다."); }
            catch (PostgresException error) when (error.SqlState == "P0001") { }
            Check((await database.ProfileAsync(account)).PaidGems == 0
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceTransaction\"") == 0
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceLedger\"") == 0
                && (await database.CommerceOrderAsync(account, Guid.Parse(prepared.Order.OrderId), default)).Order.State == "Prepared",
                "원장 저장 실패는 거래 소유권·지급·완료 상태 모두를 원자적으로 롤백해야 합니다.");
            await CommerceSql(owner, "DROP TRIGGER commerce_fail_ledger ON \"CommerceLedger\"; DROP FUNCTION commerce_fail_ledger()");
            var confirmed = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ConfirmAsync(account, Confirm(prepared, "valid"))));
            Check(confirmed.All(response => response.Order.State == "Completed" && response.Profile.PaidGems == 550)
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceTransaction\"") == 1
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceLedger\"") == 1,
                "실제 DB 동시 확정12회는 결제1건·원장1건·550보석만 지급해야 합니다.");
            var stolen = await service.PrepareAsync(other, Prepare("gems-550"));
            await CommerceError(() => service.ConfirmAsync(other, Confirm(stolen, "valid")), "PaymentTransactionAlreadyClaimed");
            var replay = await service.PrepareAsync(account, Prepare("gems-550"));
            await CommerceError(() => service.ConfirmAsync(account, Confirm(replay, "valid")), "PaymentTransactionAlreadyClaimed");
            Check((await database.ProfileAsync(other)).PaidGems == 0 && (await database.ProfileAsync(account)).PaidGems == 550,
                "DB 전역 스토어 거래 UNIQUE는 다른 주문·계정에서 재사용을 거부해야 합니다.");
            await CommerceError(() => database.SetSubscriberBadgeAsync(account, true, default), "SubscriptionRequired");
            var purchase = new CommercePurchaseRequest { ProductId = CommerceRules.PAINTER_PRODUCT_ID,
                OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 299 };
            await CommerceError(() => database.PurchaseCommerceAsync(account, purchase, default), "ProductChanged");
            purchase.ExpectedPricePaidGems = 300;
            var before = DateTimeOffset.UtcNow;
            var subscriptions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.PurchaseCommerceAsync(account, purchase, default)));
            var expiry = DateTimeOffset.Parse(subscriptions[0].SubscriptionExpiresAt);
            Check(subscriptions.All(profile => profile.PaidGems == 250 && profile.SubscriptionExpiresAt == subscriptions[0].SubscriptionExpiresAt)
                && expiry >= before.AddDays(30) && expiry <= DateTimeOffset.UtcNow.AddDays(30),
                "구독 동시 구매8회는 보석300 차감·UTC30일 권한을 한 번만 적용해야 합니다.");
            await database.SetSubscriberBadgeAsync(account, true, default);
            Check((await database.PublicProfileAsync(other, account)).ShowSubscriberBadge,
                "활성 구독의 표시 선택은 공개 프로필에 반영되어야 합니다.");
            provider.TransactionId = "transaction-2";
            var refill = await service.PrepareAsync(account, Prepare("gems-550"));
            await service.ConfirmAsync(account, Confirm(refill, "valid"));
            purchase.OperationId = Guid.NewGuid().ToString();
            var extended = await database.PurchaseCommerceAsync(account, purchase, default);
            Check(DateTimeOffset.Parse(extended.SubscriptionExpiresAt) == expiry.AddDays(30) && extended.PaidGems == 500,
                "잔여 구독 재구매는 기존 만료 시각에 정확히30일을 더해야 합니다.");
            await CommerceSql(owner, "UPDATE \"Account\" SET \"SubscriptionExpiresAt\"=now()-interval '1 second' WHERE \"Id\"=$1", account);
            Check(!(await database.ProfileAsync(account)).ShowSubscriberBadge && !(await database.PublicProfileAsync(other, account)).ShowSubscriberBadge,
                "실제 DB 만료는 본인·공개 뱃지 모두 숨겨야 합니다.");
            await CommerceError(() => database.SetSubscriberBadgeAsync(account, true, default), "SubscriptionRequired");
            purchase.OperationId = Guid.NewGuid().ToString(); before = DateTimeOffset.UtcNow;
            var renewed = await database.PurchaseCommerceAsync(account, purchase, default);
            Check(DateTimeOffset.Parse(renewed.SubscriptionExpiresAt) >= before.AddDays(30) && renewed.PaidGems == 200,
                "만료 구독 재구매는 현재 서버 시각에서30일을 계산해야 합니다.");
            await CommerceError(() => database.PurchaseCommerceAsync(account, new CommercePurchaseRequest
            { ProductId = CommerceRules.PAINTER_PRODUCT_ID, OperationId = Guid.NewGuid().ToString(), ExpectedPricePaidGems = 300 }, default), "InsufficientPaidGems");
            provider.TransactionId = "transaction-1";
            await service.ReviewReversalAsync("Steam", "refund"); await service.ReviewReversalAsync("Steam", "refund");
            Check(await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceReview\"") == 1
                && (await database.ProfileAsync(account)).PaidGems == 200 && (await database.ProfileAsync(account)).Coins == 500,
                "환불 감사는 한 번 기록하고 기존재화·원장을 삭제하지 않아야 합니다.");
            await CommerceError(() => service.ConfirmAsync(account, Confirm(prepared, "valid")), "PaymentReviewRequired");
            provider.TransactionId = "transaction-namespace-refill";
            var namespaceRefill = await service.PrepareAsync(account, Prepare("gems-100"));
            await service.ConfirmAsync(account, Confirm(namespaceRefill, "valid"));
            provider.TransactionId = "transaction-namespace-prepared";
            var namespaceOrder = await service.PrepareAsync(account, Prepare("gems-550"));
            var namespacePurchase = new CommercePurchaseRequest { ProductId = CommerceRules.PAINTER_PRODUCT_ID,
                OperationId = namespaceOrder.Order.OrderId, ExpectedPricePaidGems = 300 };
            var namespaceBefore = await database.ProfileAsync(account);
            await database.PurchaseCommerceAsync(account, namespacePurchase, default);
            var namespaceConfirmed = await service.ConfirmAsync(account, Confirm(namespaceOrder, "valid"));
            Check(namespaceConfirmed.Profile.PaidGems == 550
                && DateTimeOffset.Parse(namespaceConfirmed.Profile.SubscriptionExpiresAt) == DateTimeOffset.Parse(namespaceBefore.SubscriptionExpiresAt).AddDays(30)
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceLedger\" WHERE \"OperationId\"=$1", Guid.Parse(namespaceOrder.Order.OrderId)) == 2,
                "준비된 보석 주문 ID를 구독 operation으로 먼저 사용해도 두 원장 namespace가 분리되어 실제 결제 보상을 지급해야 합니다.");
            var namespaceRepeated = await database.PurchaseCommerceAsync(account, namespacePurchase, default);
            Check(namespaceRepeated.PaidGems == 550 && namespaceRepeated.SubscriptionExpiresAt == namespaceConfirmed.Profile.SubscriptionExpiresAt,
                "서로 다른 namespace가 있어도 구독 자체의 같은 operation 재시도는 중복 차감·연장하지 않아야 합니다.");
            var completedOrderPurchase = new CommercePurchaseRequest { ProductId = CommerceRules.PAINTER_PRODUCT_ID,
                OperationId = namespaceRefill.Order.OrderId, ExpectedPricePaidGems = 300 };
            var completedOrderSubscription = await database.PurchaseCommerceAsync(account, completedOrderPurchase, default);
            Check(completedOrderSubscription.PaidGems == 250
                && DateTimeOffset.Parse(completedOrderSubscription.SubscriptionExpiresAt) == DateTimeOffset.Parse(namespaceConfirmed.Profile.SubscriptionExpiresAt).AddDays(30)
                && await CommerceScalar(owner, "SELECT count(*) FROM \"CommerceLedger\" WHERE \"OperationId\"=$1", Guid.Parse(namespaceRefill.Order.OrderId)) == 2,
                "완료된 보석 주문 ID와 구독 operation도 별도 namespace로 저장해야 합니다.");
            Report("격리 PostgreSQL V18→V19·재초기화·동시 확정/구매·전역 거래 소유권·원장 실패 전체 롤백·UTC30일 연장/만료·뱃지·가격·환불 감사·원장 namespace 충돌 회귀 검증");
        }
        finally { await CommerceSql(owner, "DROP SCHEMA \"" + schema + "\" CASCADE"); }
    }

    private static async Task CommerceSql(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> CommerceScalar(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
