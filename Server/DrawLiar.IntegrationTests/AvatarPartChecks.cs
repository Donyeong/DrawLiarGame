using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Microsoft.Extensions.Configuration;
using Npgsql;

internal static partial class Integration
{
    private static void VerifyAvatarPartRules()
    {
        int painter = (int)AvatarAccessory.Painter;
        int outfit = (int)(AvatarAccessory.Crown | AvatarAccessory.RoundGlasses | AvatarAccessory.Scarf
            | AvatarAccessory.Cape | AvatarAccessory.Palette | AvatarAccessory.Surprised | AvatarAccessory.StarSweater);
        Check(AvatarParts.IsValid(0) && AvatarParts.IsValid(painter) && AvatarParts.IsValid(outfit),
            "기존 화가 세트와 일곱 부위의 조합은 유효해야 합니다.");
        const int UNKNOWN_PART = 1 << 22;
        foreach (int invalid in new[] { -1, UNKNOWN_PART, 5, 96, 640, 2050,
            (int)(AvatarAccessory.Wink | AvatarAccessory.Happy), (int)(AvatarAccessory.PainterApron | AvatarAccessory.StripedShirt) })
            Check(!AvatarParts.IsValid(invalid), "알 수 없는 비트와 같은 부위의 중복 착용은 거부해야 합니다.");
        int replacement = AvatarParts.Equip(painter | (int)AvatarAccessory.Scarf, (int)AvatarAccessory.Crown);
        Check(replacement == (int)(AvatarAccessory.Crown | AvatarAccessory.Brush | AvatarAccessory.Scarf),
            "머리 교체는 손과 목의 착용품을 보존해야 합니다.");
        replacement = AvatarParts.Equip(replacement, (int)AvatarAccessory.Palette);
        Check(replacement == (int)(AvatarAccessory.Crown | AvatarAccessory.Palette | AvatarAccessory.Scarf)
            && AvatarParts.Remove(replacement, AvatarPartSlot.Neck) == (int)(AvatarAccessory.Crown | AvatarAccessory.Palette)
            && AvatarParts.Equip(replacement, UNKNOWN_PART) == replacement, "부위 교체·해제와 잘못된 입력은 다른 부위를 바꾸면 안 됩니다.");
        Check(AvatarParts.Sanitize(-1) == 0 && AvatarParts.Sanitize(outfit | UNKNOWN_PART) == outfit
            && AvatarParts.Sanitize(5) == (int)AvatarAccessory.Beret, "게임 상태는 알 수 없는 비트와 부위 충돌을 정규화해야 합니다.");
        Check(Enum.GetValues<AvatarPartSlot>().Select(slot => (int)slot).SequenceEqual(Enumerable.Range(0, 7))
            && AvatarParts.Slots.SequenceEqual(new[] { AvatarPartSlot.Head, AvatarPartSlot.Expression, AvatarPartSlot.Face,
                AvatarPartSlot.Body, AvatarPartSlot.Neck, AvatarPartSlot.Back, AvatarPartSlot.Hand })
            && AvatarParts.Name(AvatarPartSlot.Face) == "얼굴 장식" && AvatarParts.Name(AvatarPartSlot.Expression) == "표정"
            && AvatarParts.Name(AvatarPartSlot.Body) == "몸통" && painter == 3 && (int)AvatarAccessory.Palette == 2048,
            "기존 부위와 비트 ID를 유지하고 표정·몸통을 끝에 추가해야 합니다.");
        int legacyOutfit = outfit & 4095;
        foreach (var expression in AvatarParts.Items.Where(part => part.Slot == AvatarPartSlot.Expression))
            foreach (var body in AvatarParts.Items.Where(part => part.Slot == AvatarPartSlot.Body))
            {
                int combination = legacyOutfit | (int)expression.Accessory | (int)body.Accessory;
                Check(AvatarParts.IsValid(combination) && AvatarParts.Sanitize(combination) == combination,
                    "표정과 몸통은 얼굴 장식을 포함한 기존 부위와 함께 착용해야 합니다.");
                int changed = AvatarParts.Equip(combination, (int)AvatarAccessory.Wink);
                Check((changed & AvatarParts.Mask(AvatarPartSlot.Expression)) == (int)AvatarAccessory.Wink
                    && (changed & ~AvatarParts.Mask(AvatarPartSlot.Expression)) == (combination & ~AvatarParts.Mask(AvatarPartSlot.Expression))
                    && AvatarParts.Remove(combination, AvatarPartSlot.Body) == (legacyOutfit | (int)expression.Accessory),
                    "표정 교체와 몸통 해제는 다른 여섯 부위를 보존해야 합니다.");
            }
        Check(AvatarParts.Sanitize((int)(AvatarAccessory.Wink | AvatarAccessory.Happy | AvatarAccessory.PainterApron | AvatarAccessory.StarSweater))
            == (int)(AvatarAccessory.Wink | AvatarAccessory.PainterApron), "표정·몸통 중복은 각 부위 한 개씩 정규화해야 합니다.");
        var products = ServerDatabase.ShopProducts;
        int[] definedParts = Enum.GetValues<AvatarAccessory>().Select(value => (int)value)
            .Where(value => value > 0 && (value & (value - 1)) == 0).Order().ToArray();
        Check(definedParts.SequenceEqual(AvatarParts.Items.Select(part => (int)part.Accessory).Order())
            && AvatarParts.Items.Select(part => part.Id).Distinct().Count() == AvatarParts.Items.Count
            && definedParts.Aggregate(0, (mask, part) => mask | part) == AvatarParts.ALL_MASK,
            "정의된 모든 단일 파츠 비트는 고유한 공유 품목과 상점 상품으로 제공해야 합니다.");
        Check(products.Length == 23 && products.Select(product => product.Id).Distinct().Count() == 23
            && products[2].Id == "painter" && products[2].Price == 180 && products[2].Accessory == painter,
            "상점은 기존 세트와 고유한 스물두 파츠를 제공해야 합니다.");
        Check(AvatarParts.Items.Count(part => part.Slot == AvatarPartSlot.Expression && part.Price is >= 100 and <= 140) == 5
            && AvatarParts.Items.Count(part => part.Slot == AvatarPartSlot.Body && part.Price is >= 140 and <= 200) == 5,
            "새 표정과 몸통은 각5종을 기존 코인 가격 범위로 제공해야 합니다.");
        foreach (var part in AvatarParts.Items)
            Check(products.Count(product => product.Id == part.Id && product.Name == part.Name && product.Price == part.Price
                && product.Accessory == (int)part.Accessory) == 1, "서버 상품은 공유 파츠의 이름·ID·가격·비트를 중복 없이 사용해야 합니다.");
        Check(products.Select(product => product.Accessory).Order().SequenceEqual(Enum.GetValues<AvatarAccessory>()
                .Where(value => value != AvatarAccessory.None).Select(value => (int)value).Order())
            && products.All(product => AvatarParts.IsValid(product.Accessory) && product.Price > 0),
            "단일 파츠와 기존 세트 전체를 상점에서 제공하고 구매 가능한 유효한 비트를 사용해야 합니다.");
        var session = new GameSession(new RoomSettings(), new GameData());
        Check(session.Join(1, "파츠검증", 2, outfit), "일곱 부위를 착용한 참가자가 입장해야 합니다.");
        Check(session.Snapshot(1, 1, 0).Players.Single().Accessory == outfit, "입장 상태는 새 착용 비트를 보존해야 합니다.");
        session.UpdateProfile(1, "파츠검증", 3, replacement);
        Check(session.Snapshot(1, 1, 0).Players.Single().Accessory == replacement, "대기방 프로필 갱신도 새 비트를 보존해야 합니다.");
        Report("일곱 부위·25종 표정/몸통 조합·충돌/미지 비트 거부·기존 세트·23종 공유 상품·게임 상태 보존 검증");
    }

    private static async Task VerifyAvatarDatabaseAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다.");
        string schema = "drawliar_avatar_test_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false, IncludeErrorDetail = false };
        Check(scoped.Host == "127.0.0.1" && scoped.Port == 25439 && scoped.Database == "postgres", "파츠 검증은 전용 로컬 PostgreSQL 25439만 사용합니다.");
        await using var owner = new NpgsqlConnection(scoped.ConnectionString);
        await owner.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DrawLiarDatabase"] = scoped.ConnectionString }).Build();
            using var database = new ServerDatabase(configuration);
            await database.InitializeAsync();
            Guid account = await database.DevelopmentAccountAsync("파츠화가");
            Guid friend = await database.DevelopmentAccountAsync("친구화가");
            Guid legacy = await database.DevelopmentAccountAsync("기존화가");
            await using (var coins = new NpgsqlCommand("UPDATE \"Account\" SET \"Coins\"=5000 WHERE \"Id\"=$1", owner))
            {
                coins.Parameters.AddWithValue(account);
                await coins.ExecuteNonQueryAsync();
            }
            var legacyPurchase = await database.PurchaseAsync(legacy, new PurchaseRequest { ProductId = "painter", OperationId = Guid.NewGuid().ToString() });
            var legacyProfile = await database.UpdateProfileAsync(legacy, new UpdateProfileRequest
                { DisplayName = "기존화가", AvatarColor = 1, Accessory = 3 });
            Check(legacyPurchase.Coins == 320 && legacyProfile.Accessory == 3 && legacyProfile.OwnedAccessories.Contains(3),
                "기존 비트 3의 세트 구매·소유권·착용은 유지되어야 합니다.");
            foreach (int part in new[] { 1, 2 })
                Check((await database.UpdateProfileAsync(legacy, new UpdateProfileRequest
                    { DisplayName = "기존화가", AvatarColor = 1, Accessory = part })).Accessory == part,
                    "기존 세트 소유자는 세트의 각 파츠도 따로 착용할 수 있어야 합니다.");
            foreach (string productId in new[] { "beret", "brush", "painter" })
                await ExpectAvatarErrorAsync(() => database.PurchaseAsync(legacy, new PurchaseRequest
                    { ProductId = productId, OperationId = Guid.NewGuid().ToString() }), "AlreadyOwned", 409);
            Check((await database.ProfileAsync(legacy)).Coins == 320, "세트로 보유한 단품의 재구매 거부는 재화를 차감하면 안 됩니다.");

            Guid partial = await database.DevelopmentAccountAsync("부분화가");
            await database.PurchaseAsync(partial, new PurchaseRequest { ProductId = "beret", OperationId = Guid.NewGuid().ToString() });
            var completedSet = await database.PurchaseAsync(partial, new PurchaseRequest { ProductId = "painter", OperationId = Guid.NewGuid().ToString() });
            Check(completedSet.OwnedAccessories.Aggregate(0, (mask, part) => mask | part) == (int)AvatarAccessory.Painter
                && completedSet.Coins == 220, "일부 파츠만 보유하면 기존 가격으로 세트를 구매하여 빠진 파츠를 보유해야 합니다.");

            var request = new PurchaseRequest { ProductId = "crown", OperationId = Guid.NewGuid().ToString() };
            var purchases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.PurchaseAsync(account, request)));
            Check(purchases.All(profile => profile.Coins == 4850 && profile.OwnedAccessories.Contains((int)AvatarAccessory.Crown)),
                "새 파츠의 병렬 구매 재시도는 한 번만 차감해야 합니다.");
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account, new PurchaseRequest
                { ProductId = "brush", OperationId = request.OperationId }), "OperationConflict", 409);
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account, new PurchaseRequest
                { ProductId = "crown", OperationId = Guid.NewGuid().ToString() }), "AlreadyOwned", 409);
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account, new PurchaseRequest
                { ProductId = "unknown", OperationId = Guid.NewGuid().ToString() }), "ProductUnavailable", 400);

            UpdateProfileRequest Outfit(int accessories) => new() { DisplayName = "파츠화가", AvatarColor = 4, Accessory = accessories };
            await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account, Outfit((int)AvatarAccessory.WizardHat)), "AccessoryNotOwned", 403);
            foreach (var part in AvatarParts.Items.Where(part => part.Slot is AvatarPartSlot.Expression or AvatarPartSlot.Body))
                await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account, Outfit((int)part.Accessory)), "AccessoryNotOwned", 403);
            foreach (int invalid in new[] { -1, 1 << 22, 5, 96, 640, 2050,
                (int)(AvatarAccessory.Wink | AvatarAccessory.Happy), (int)(AvatarAccessory.PainterApron | AvatarAccessory.StripedShirt) })
                await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account, Outfit(invalid)), "InvalidAvatar", 400);
            Check((await database.ProfileAsync(account)).Accessory == 0 && (await database.ProfileAsync(account)).Coins == 4850,
                "거부된 구매·착용 요청은 저장된 재화와 착용을 바꾸면 안 됩니다.");

            var bodyRequest = new PurchaseRequest { ProductId = "star-sweater", OperationId = Guid.NewGuid().ToString() };
            var bodyPurchases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.PurchaseAsync(account, bodyRequest)));
            Check(bodyPurchases.All(profile => profile.Coins == 4650 && profile.OwnedAccessories.Contains((int)AvatarAccessory.StarSweater)),
                "높은 몸통 비트의 구매 재시도도 한 번만 차감하고 저장해야 합니다.");
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account, new PurchaseRequest
                { ProductId = "star-sweater", OperationId = Guid.NewGuid().ToString() }), "AlreadyOwned", 409);
            foreach (var part in AvatarParts.Items.Where(part => part.Id != "crown" && part.Id != "star-sweater"))
                await database.PurchaseAsync(account, new PurchaseRequest { ProductId = part.Id, OperationId = Guid.NewGuid().ToString() });
            int outfit = (int)(AvatarAccessory.Crown | AvatarAccessory.RoundGlasses | AvatarAccessory.Scarf
                | AvatarAccessory.Cape | AvatarAccessory.Palette | AvatarAccessory.Surprised | AvatarAccessory.StarSweater);
            var saved = await database.UpdateProfileAsync(account, Outfit(outfit));
            Check(saved.Accessory == outfit && saved.Coins == 5000 - AvatarParts.Items.Sum(part => part.Price)
                && saved.OwnedAccessories.Aggregate(0, (mask, item) => mask | item) == AvatarParts.ALL_MASK,
                "스물두 파츠 구매의 재화와 일곱 부위 착용·소유 비트를 저장해야 합니다.");
            await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account,
                Outfit(outfit | (int)AvatarAccessory.WizardHat)), "InvalidAvatar", 400);
            await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account,
                Outfit(outfit | (int)AvatarAccessory.Wink)), "InvalidAvatar", 400);
            await ExpectAvatarErrorAsync(() => database.UpdateProfileAsync(account,
                Outfit(outfit | (int)AvatarAccessory.PainterApron)), "InvalidAvatar", 400);
            Check((await database.ProfileAsync(account)).Accessory == outfit, "모두 보유한 파츠도 같은 부위에 중복 착용할 수 없습니다.");
            await ExpectAvatarErrorAsync(() => database.PurchaseAsync(account, new PurchaseRequest
                { ProductId = "painter", OperationId = Guid.NewGuid().ToString() }), "AlreadyOwned", 409);
            using (var reopened = new ServerDatabase(configuration))
            {
                var restored = await reopened.ProfileAsync(account);
                Check(restored.Accessory == outfit && restored.AvatarColor == 4 && restored.Coins == saved.Coins
                    && restored.OwnedAccessories.SequenceEqual(saved.OwnedAccessories), "새 DB 연결은 모든 파츠·착용·재화를 복원해야 합니다.");
            }
            await database.RequestFriendAsync(account, friend);
            await database.RespondFriendAsync(friend, account, true);
            Check((await database.FriendsAsync(friend)).Friends.Single().Accessory == outfit,
                "친구 조회에도 새 착용 비트 전체를 전달해야 합니다.");
            await VerifyDedicatedAvatarAsync(saved);
            Report("격리 PostgreSQL 새 파츠 구매·병렬 중복 방지·미보유/충돌 거부·영속·친구·데디케이티드 입장 보존 검증");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", owner);
            await drop.ExecuteNonQueryAsync();
            Report("파츠 검증 임시 스키마 삭제");
        }
    }

    private static async Task VerifyDedicatedAvatarAsync(ProfileData profile)
    {
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = profile.AccountId };
        var room = new DedicatedRoom(roomData, new GameData(), Array.Empty<ServerTopicData>(), 0, () => { });
        var socket = new PolicySocket();
        await using var connection = new GameConnection(socket, profile.AccountId, "fixture", CancellationToken.None);
        Check(room.Join(connection, new RedeemTicketResponse { AccountId = profile.AccountId, Profile = profile, Room = roomData }, 0),
            "서버가 저장한 파츠 프로필로 데디케이티드에 입장해야 합니다.");
        await WaitAsync(() => socket.LastState?.Players.Length == 1, 3);
        Check(socket.LastState!.Players.Single().Accessory == profile.Accessory,
            "실제 데디케이티드 상태 JSON에도 착용 비트 전체가 보존되어야 합니다.");
    }

    private static async Task ExpectAvatarErrorAsync(Func<Task<ProfileData>> action, string code, int status)
    {
        try { await action(); }
        catch (ApiException exception) when (exception.Code == code && exception.Status == status) { return; }
        throw new InvalidOperationException("잘못된 파츠 요청은 " + code + "로 거부해야 합니다.");
    }
}
