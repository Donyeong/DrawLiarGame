using System.Net;
using System.Net.Http.Json;
using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static PurchaseBatchRequest PurchaseBatch(params string[] productIds) => new()
    { ProductIds = productIds, OperationId = Guid.NewGuid().ToString() };

    private static async Task VerifyPurchaseBatchDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."))
        { SearchPath = "drawliar_purchase_batch_test_" + Guid.NewGuid().ToString("N"), Pooling = false, IncludeErrorDetail = false };
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439 && scoped.Database == "postgres",
            "일괄 구매 DB 검증은 전용 로컬 PostgreSQL 25439만 사용합니다.");
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{scoped.SearchPath}\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            using var database = new ServerDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build());
            await database.InitializeAsync();
            Guid account = await database.DevelopmentAccountAsync("일괄 구매 검증");
            await SetPurchaseBatchCoinsAsync(owner, account, 50000);
            var initial = await database.ProfileAsync(account);
            var outfit = AvatarParts.Slots.Select(slot => AvatarParts.Items.Last(part => part.Slot == slot)).ToArray();
            var batch = PurchaseBatch(outfit.Select(part => part.Id).ToArray());
            var reverse = new PurchaseBatchRequest { ProductIds = batch.ProductIds.Reverse().ToArray(), OperationId = batch.OperationId };
            var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => database.PurchaseBatchAsync(account, index % 2 == 0 ? batch : reverse)));
            int coins = 50000 - outfit.Sum(part => part.Price);
            Check(parallel.All(profile => profile.Coins == coins && profile.Accessory == initial.Accessory
                && outfit.All(part => AvatarParts.IsOwned(profile.OwnedAccessories, (long)part.Accessory)))
                && await PurchaseBatchReceiptCountAsync(owner, account) == 1,
                "7부위·64비트 파츠의 병렬 구매와 순서 변경 재시도는 한 번만 차감하고 자동 장착하면 안 됩니다.");
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account,
                new PurchaseBatchRequest { ProductIds = ["sparkle-face"], OperationId = batch.OperationId }), "OperationConflict", 409);
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account,
                new PurchaseRequest { ProductId = outfit[0].Id, OperationId = batch.OperationId }), "OperationConflict", 409);

            var single = new PurchaseRequest { ProductId = "pink-wig", OperationId = Guid.NewGuid().ToString() };
            coins -= 220;
            Check((await database.PurchaseAsync(account, single)).Coins == coins && (await database.PurchaseAsync(account, single)).Coins == coins,
                "기존 단건 구매와 중복 요청은 동일하게 동작해야 합니다.");
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account,
                new PurchaseBatchRequest { ProductIds = [single.ProductId], OperationId = single.OperationId }), "OperationConflict", 409);
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account,
                new PurchaseRequest { ProductId = single.ProductId, OperationId = Guid.NewGuid().ToString() }), "AlreadyOwned", 409);
            var ownedAndNew = await database.PurchaseBatchAsync(account, PurchaseBatch(outfit[0].Id, "pink-wig", "sparkle-face"));
            coins -= 160;
            Check(ownedAndNew.Coins == coins && AvatarParts.IsOwned(ownedAndNew.OwnedAccessories, (long)AvatarAccessory.SparkleFace),
                "보유 상품을 함께 보내도 새 상품만 차감해야 합니다.");
            Check((await database.PurchaseBatchAsync(account, PurchaseBatch(outfit[0].Id, "pink-wig"))).Coins == coins,
                "모두 보유한 일괄 구매는 추가 차감 없이 성공해야 합니다.");

            var nextHead = await database.PurchaseBatchAsync(account, PurchaseBatch("rose-buns"));
            Check(!AvatarParts.IsOwned(nextHead.OwnedAccessories, (long)AvatarAccessory.SharkHood),
                "확장 코드 1과 2를 함께 소유해도 코드 3을 보유한 것으로 취급하면 안 됩니다.");
            coins -= 240 + 220;
            Check((await database.PurchaseBatchAsync(account, PurchaseBatch("shark-hood"))).Coins == coins,
                "확장 코드 3의 새 구매는 정확한 가격을 차감해야 합니다.");
            var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.PurchaseBatchAsync(account, PurchaseBatch("mellow-face"))));
            coins -= 160;
            Check(racing.All(profile => profile.Coins == coins), "서로 다른 operation의 같은 상품 병렬 구매도 소유권 잠금으로 한 번만 차감해야 합니다.");
            Report("일괄 구매 7부위·64비트 소유·병렬 멱등성·단건 호환·요청 충돌·보유 제외·확장 코드 정밀 검증");

            var before = await database.ProfileAsync(account);
            long receipts = await PurchaseBatchReceiptCountAsync(owner, account);
            string available = AvatarParts.Items.First(part => !AvatarParts.IsOwned(before.OwnedAccessories, (long)part.Accessory)).Id;
            var invalid = new[]
            {
                PurchaseBatch(), PurchaseBatch(available, available),
                PurchaseBatch(ServerDatabase.ShopProducts.Take(8).Select(product => product.Id).ToArray()),
                new PurchaseBatchRequest { ProductIds = null!, OperationId = Guid.NewGuid().ToString() },
                PurchaseBatch(" ")
            };
            foreach (var request in invalid) await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account, request), "InvalidPurchaseBatch", 400);
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account, PurchaseBatch(available, "not-a-product")), "ProductUnavailable", 400);
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account,
                new PurchaseBatchRequest { ProductIds = [available], OperationId = Guid.Empty.ToString() }), "InvalidOperation", 400);
            await SetPurchaseBatchCoinsAsync(owner, account, 1);
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(account, PurchaseBatch(available)), "InsufficientCoins", 409);
            var after = await database.ProfileAsync(account);
            Check(after.Coins == 1 && after.OwnedAccessories.SequenceEqual(before.OwnedAccessories)
                && await PurchaseBatchReceiptCountAsync(owner, account) == receipts,
                "잘못된 상품·중복·개수·operation·잔액 부족 요청은 소유와 영수증을 부분 저장하면 안 됩니다.");

            await SetPurchaseBatchCoinsAsync(owner, account, coins);
            var rollbackParts = AvatarParts.Items.Where(part => !AvatarParts.IsOwned(after.OwnedAccessories, (long)part.Accessory))
                .OrderBy(part => part.Id, StringComparer.Ordinal).Take(2).ToArray();
            var rollback = PurchaseBatch(rollbackParts.Select(part => part.Id).ToArray());
            await using (var constraint = new NpgsqlCommand($"ALTER TABLE \"OwnedAccessory\" ADD CONSTRAINT \"PurchaseBatchRollback\" CHECK (\"Accessory\" <> {(long)rollbackParts[1].Accessory}) NOT VALID", owner))
                await constraint.ExecuteNonQueryAsync();
            try
            {
                try { await database.PurchaseBatchAsync(account, rollback); throw new InvalidOperationException("부분 저장 실패를 주입하지 못했습니다."); }
                catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation) { }
            }
            finally
            { await using var remove = new NpgsqlCommand("ALTER TABLE \"OwnedAccessory\" DROP CONSTRAINT \"PurchaseBatchRollback\"", owner); await remove.ExecuteNonQueryAsync(); }
            after = await database.ProfileAsync(account);
            Check(after.Coins == coins && after.OwnedAccessories.SequenceEqual(before.OwnedAccessories)
                && await PurchaseBatchReceiptCountAsync(owner, account) == receipts,
                "두 번째 소유 저장 실패는 이미 실행한 차감과 첫 번째 소유 저장까지 전부 롤백해야 합니다.");
            Check((await database.PurchaseBatchAsync(account, rollback)).Coins == coins - rollbackParts.Sum(part => part.Price),
                "실패한 operation은 복구 후 재시도하여 정확히 한 번 구매할 수 있어야 합니다.");

            Guid painter = await database.DevelopmentAccountAsync("화가 세트 검증");
            var set = await database.PurchaseBatchAsync(painter, PurchaseBatch("brush", "beret", "painter"));
            Check(set.Coins == 320 && AvatarParts.IsOwned(set.OwnedAccessories, (long)AvatarAccessory.Painter),
                "화가 세트와 구성품을 함께 보내도 중복 구성품을 추가 차감하면 안 됩니다.");
            await database.AdminBanAsync(painter, true);
            await ExpectAvatarErrorAsync(() => database.PurchaseBatchAsync(painter, PurchaseBatch("mellow-face")), "AccountUnavailable", 403);
            Report("입력·판매 상품·잔액 검증, 중간 DB 실패 원자 롤백/재시도, 화가 세트 중복 구성품, 정지 계정 거부 검증");
        }
        finally
        { await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{scoped.SearchPath}\" CASCADE", owner); await drop.ExecuteNonQueryAsync(); }
    }

    private static async Task SetPurchaseBatchCoinsAsync(NpgsqlConnection connection, Guid account, int coins)
    {
        await using var update = new NpgsqlCommand("UPDATE \"Account\" SET \"Coins\"=$2 WHERE \"Id\"=$1", connection);
        update.Parameters.AddWithValue(account); update.Parameters.AddWithValue(coins);
        await update.ExecuteNonQueryAsync();
    }

    private static async Task<long> PurchaseBatchReceiptCountAsync(NpgsqlConnection connection, Guid account)
    {
        await using var count = new NpgsqlCommand("SELECT count(*) FROM \"PurchaseReceipt\" WHERE \"AccountId\"=$1", connection);
        count.Parameters.AddWithValue(account);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task VerifyPurchaseBatchHttpAsync(string mainUrl)
    {
        Check(mainUrl == "http://127.0.0.1:25550", "일괄 구매 HTTP는 전용 로컬 메인서버만 사용합니다.");
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439
            && (scoped.SearchPath ?? "").StartsWith("drawliar_purchase_batch_http_test_", StringComparison.Ordinal),
            "일괄 구매 HTTP는 전용 PostgreSQL 임시 스키마만 사용합니다.");
        using var main = Client(mainUrl);
        var user = await GuestAndEnterAsync(main);
        Check(user.Login.GameServerUrl == "http://127.0.0.1:25560", "일괄 구매 HTTP는 전용 로컬 게임서버만 사용합니다.");
        using var game = Client(user.Login.GameServerUrl);
        string token = user.Session.SessionToken;
        var before = await GetAsync<ProfileData>(game, "/api/profile", token);
        const string path = "/api/shop/purchase-batch";
        var batch = PurchaseBatch("beret", "block-body", "sparkle-face");
        using (var unauthorized = await SendAsync(game, path, batch, null))
            Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "일괄 구매는 게임 세션 인증을 요구해야 합니다.");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => PostAsync<ProfileData>(game, path,
            new PurchaseBatchRequest { ProductIds = index % 2 == 0 ? batch.ProductIds : batch.ProductIds.Reverse().ToArray(), OperationId = batch.OperationId }, token)));
        Check(results.All(profile => profile.Coins == 120 && profile.Accessory == before.Accessory
            && AvatarParts.IsOwned(profile.OwnedAccessories, (long)AvatarAccessory.BlockBody)
            && AvatarParts.IsOwned(profile.OwnedAccessories, (long)AvatarAccessory.SparkleFace)),
            "HTTP 일괄 구매는 기존 보유 베레모를 제외하고 380코인만 차감하며 64비트 소유와 기존 장착을 보존해야 합니다.");
        foreach (var invalid in new[]
        {
            (PurchaseBatch("pink-wig"), HttpStatusCode.Conflict, "InsufficientCoins"),
            (PurchaseBatch("mellow-face", "missing"), HttpStatusCode.BadRequest, "ProductUnavailable"),
            (PurchaseBatch("beret", "beret"), HttpStatusCode.BadRequest, "InvalidPurchaseBatch"),
            (new PurchaseBatchRequest { ProductIds = ["beret"], OperationId = batch.OperationId }, HttpStatusCode.Conflict, "OperationConflict")
        })
        {
            using var response = await SendAsync(game, path, invalid.Item1, token);
            Check(response.StatusCode == invalid.Item2 && (await response.Content.ReadFromJsonAsync<ApiError>(Json))?.Code == invalid.Item3,
                "HTTP 일괄 구매는 요청에 맞는 오류 코드로 전체 구매를 거부해야 합니다.");
        }
        var after = await GetAsync<ProfileData>(game, "/api/profile", token);
        Check(after.Coins == 120 && after.OwnedAccessories.SequenceEqual(results[0].OwnedAccessories),
            "거부한 HTTP 구매는 잔액과 소유 목록을 변경하면 안 됩니다.");
        await PostNoContentAsync(game, "/api/session/logout", new { }, token);
        await PostNoContentAsync(main, "/api/session/logout", new { }, user.Login.SessionToken);
        Report("실제 HTTP 일괄 구매 route/JSON·게임 세션 인증·병렬 재시도·오류 상태·64비트 소유·장착 보존 검증");
    }
}
