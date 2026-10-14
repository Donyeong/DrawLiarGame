using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static void VerifyCosmeticRules()
    {
        var products = CosmeticRules.CreateProducts();
        Check(products.Length == 20 && products.Select(product => product.Id).Distinct().Count() == 20,
            "꾸미기는 고유한 영구 상품 스무 가지여야 합니다.");
        foreach (string category in CosmeticRules.Categories)
            Check(products.Where(product => product.Category == category).Select(product => product.PricePaidGems)
                .SequenceEqual(new[] { 80, 100, 120, 140, 160 }), "카테고리마다 다섯 상품의 보석 가격이 일치해야 합니다.");
        Check(products.All(product => product.Kind == CommerceRules.COSMETIC && product.SubscriptionDays == 0
            && product.PaidGems == 0 && product.StoreProductId == "" && product.Name.Length > 0 && product.Description.Length > 0),
            "영구 꾸미기는 충전/구독 권한과 분리해야 합니다.");
        products[0].PricePaidGems = -1;
        Check(CosmeticRules.CreateProducts()[0].PricePaidGems == 80, "조회용 상품 변경은 공유 카탈로그를 변경하면 안 됩니다.");
        var original = new CosmeticLoadout { NicknameEffect = "nickname-prism", AvatarBackground = "background-ocean" };
        var equipped = CosmeticRules.SetEquipped(original, CosmeticRules.CHAT_BUBBLE, "bubble-pixel");
        Check(equipped.NicknameEffect == original.NicknameEffect && equipped.AvatarBackground == original.AvatarBackground
            && original.ChatBubble == "", "한 카테고리 착용은 다른 카테고리와 원본을 보존해야 합니다.");
        var sanitized = CosmeticRules.Sanitize(new CosmeticLoadout { NicknameEffect = "background-ocean", AvatarFrame = "unknown" });
        Check(sanitized.NicknameEffect == "" && sanitized.AvatarFrame == "", "존재하지 않거나 다른 카테고리의 착용값은 제거해야 합니다.");
        var owned = CosmeticRules.SanitizeOwned(new[] { "nickname-prism", "nickname-prism", "invalid", "bubble-pixel" });
        var kept = CosmeticRules.KeepOwned(equipped, owned);
        Check(owned.Length == 2 && kept.NicknameEffect == "nickname-prism" && kept.AvatarBackground == "" && kept.ChatBubble == "bubble-pixel",
            "보유 목록의 중복/잘못된 ID와 미보유 착용값을 제거해야 합니다.");
        var session = new GameSession(new RoomSettings(), new GameData());
        session.Join(1, "꾸미기화가", 0, 0, cosmetics: equipped);
        equipped.NicknameEffect = "nickname-gold";
        var snapshot = session.Snapshot(1, 1, 0);
        Check(snapshot.Players.Single().Cosmetics.NicknameEffect == "nickname-prism", "입장값은 복사하여 게임 상태를 보존해야 합니다.");
        snapshot.Players.Single().Cosmetics.NicknameEffect = "nickname-sunset";
        Check(session.PlayerCosmetics(1).NicknameEffect == "nickname-prism", "공개 스냅샷 변경은 게임 상태를 바꾸면 안 됩니다.");
        Report("꾸미기 4개 카테고리/20개 영구 상품·독립 착용·보유 검증·입장/스냅샷 복사 검증");
    }

    private static async Task VerifyCosmeticWireAsync()
    {
        var socket = new KickSocket();
        await using var peer = new GameConnection(socket, "cosmetic-wire", "test", CancellationToken.None);
        var model = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = peer.AccountId,
            Settings = new ServerRoomSettings { Topics = new[] { "과일" } } };
        var room = new DedicatedRoom(model, new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } } },
            [], RoomRegistry.Now, () => { });
        var loadout = new CosmeticLoadout { NicknameEffect = "nickname-prism", ChatBubble = "bubble-cloud" };
        Check(room.Join(peer, new RedeemTicketResponse { AccountId = peer.AccountId, Room = model,
            Profile = new ProfileData { AccountId = peer.AccountId, DisplayName = "꾸미기화가", Cosmetics = loadout } }, RoomRegistry.Now),
            "서버 프로필로 방에 입장해야 합니다.");
        await WaitAsync(() => socket.Latest?.Players.Single().Cosmetics.NicknameEffect == "nickname-prism", 3);
        room.Receive(peer, new GameplayEnvelope { Type = "request", Kind = "chat", Text = "꾸미기 확인",
            Line = new ChatLine { Cosmetics = new CosmeticLoadout { ChatBubble = "bubble-neon" } } }, RoomRegistry.Now);
        await WaitAsync(() => socket.Frames.Any(frame => frame.Type == "chat"), 3);
        Check(socket.Frames.Single(frame => frame.Type == "chat").Line.Cosmetics.ChatBubble == "bubble-cloud",
            "방 채팅은 클라이언트 착용 위조를 무시하고 서버 참가자의 꾸미기를 전달해야 합니다.");
        var next = new CosmeticLoadout { NicknameEffect = "nickname-neon", AvatarFrame = "frame-stars", ChatBubble = "bubble-memo" };
        await room.RefreshProfileAsync(peer, new GameplayEnvelope { Type = "request", Kind = "refreshProfile", RequestId = "cosmetic-refresh" },
            RoomRegistry.Now + 1, (_, _) => Task.FromResult(new ProfileData { AccountId = peer.AccountId, DisplayName = "새꾸미기", Cosmetics = next }));
        await WaitAsync(() => socket.Frames.Any(frame => frame.Type == "profile-refreshed" && frame.Accepted)
            && socket.Latest?.Players.Single().Cosmetics.AvatarFrame == "frame-stars", 3);
        Check(socket.Latest!.Players.Single().Cosmetics.NicknameEffect == "nickname-neon", "인증된 프로필 새로고침은 방에도 새 착용을 반영해야 합니다.");
        Report("데디케이티드 JSON 입장/채팅·착용 위조 차단·인증 프로필 새로고침 검증");
    }

    private static async Task VerifyCosmeticDatabaseAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        Guid account = await database.DevelopmentAccountAsync("꾸미기DB검증"), other = await database.DevelopmentAccountAsync("꾸미기친구");
        var products = CosmeticRules.CreateProducts();
        CommercePurchaseRequest Purchase(CommerceProduct product, string? operation = null) => new()
            { ProductId = product.Id, ExpectedPricePaidGems = product.PricePaidGems, OperationId = operation ?? Guid.NewGuid().ToString() };
        await CommerceError(() => database.PurchaseCommerceAsync(account, Purchase(products[0]), default), "InsufficientPaidGems");
        await CommerceError(() => database.EquipCosmeticAsync(account, new CosmeticEquipRequest
            { Category = CosmeticRules.NICKNAME_EFFECT, ProductId = products[0].Id }, default), "CosmeticNotOwned");
        await CommerceError(() => database.EquipCosmeticAsync(account, new CosmeticEquipRequest
            { Category = CosmeticRules.CHAT_BUBBLE, ProductId = products[0].Id }, default), "InvalidCosmetic");
        var provider = new CommerceTestProvider { TransactionId = "cosmetic-credit-1" };
        var service = new CommerceService(database, new[] { provider });
        await service.ConfirmAsync(account, Confirm(await service.PrepareAsync(account, Prepare("gems-1200")), "valid"));
        provider.TransactionId = "cosmetic-credit-2";
        await service.ConfirmAsync(account, Confirm(await service.PrepareAsync(account, Prepare("gems-1200")), "valid"));
        var mismatch = Purchase(products[0]); mismatch.ExpectedPricePaidGems--;
        await CommerceError(() => database.PurchaseCommerceAsync(account, mismatch, default), "ProductChanged");
        await CommerceSql(owner, """
            CREATE FUNCTION cosmetic_fail_ledger() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'isolated cosmetic rollback test'; END; $$;
            CREATE TRIGGER cosmetic_fail_ledger BEFORE INSERT ON "CommerceLedger" FOR EACH ROW EXECUTE FUNCTION cosmetic_fail_ledger();
            """);
        try { await database.PurchaseCommerceAsync(account, Purchase(products[0]), default); throw new InvalidOperationException("원장 저장 오류가 구매 성공으로 처리되었습니다."); }
        catch (PostgresException error) when (error.SqlState == "P0001") { }
        await CommerceSql(owner, "DROP TRIGGER cosmetic_fail_ledger ON \"CommerceLedger\"; DROP FUNCTION cosmetic_fail_ledger()");
        Check((await database.ProfileAsync(account)).PaidGems == 2400 && (await database.ProfileAsync(account)).OwnedCosmetics.Length == 0,
            "꾸미기 구매의 원장 실패는 보석 차감과 보유권을 모두 롤백해야 합니다.");
        var first = Purchase(products[0]);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.PurchaseCommerceAsync(account, first, default)));
        Check(concurrent.All(profile => profile.PaidGems == 2320 && profile.OwnedCosmetics.SequenceEqual(new[] { products[0].Id })),
            "동일 operation 동시 구매는 보석 차감과 보유권을 한 번만 적용해야 합니다.");
        await CommerceError(() => database.PurchaseCommerceAsync(account, Purchase(products[1], first.OperationId), default), "OperationConflict");
        Check((await database.PurchaseCommerceAsync(account, Purchase(products[0]), default)).PaidGems == 2320,
            "이미 보유한 영구 상품은 새 operation으로 재요청해도 중복 차감하면 안 됩니다.");
        foreach (var product in products.Skip(1)) await database.PurchaseCommerceAsync(account, Purchase(product), default);
        await Task.WhenAll(CosmeticRules.Categories.Select(category => database.EquipCosmeticAsync(account, new CosmeticEquipRequest
            { Category = category, ProductId = products.Last(product => product.Category == category).Id }, default)));
        var equipped = await database.ProfileAsync(account);
        Check(equipped.PaidGems == 0 && equipped.Coins == 500 && equipped.SubscriptionExpiresAt == "" && equipped.OwnedCosmetics.Length == 20
            && CosmeticRules.Categories.All(category => CosmeticRules.GetEquipped(equipped.Cosmetics, category) == products.Last(product => product.Category == category).Id),
            "스무 상품의 총 2400보석 구매와 네 카테고리 동시 착용은 코인/구독을 보존하고 독립적으로 저장해야 합니다.");
        Check((await database.PurchaseCommerceAsync(account, first, default)).PaidGems == 0, "응답 유실 재시도는 잔액0에서도 원래 구매를 확인해야 합니다.");
        var publicProfile = await database.PublicProfileAsync(other, account);
        CheckPublicProfileFields(publicProfile);
        Check(publicProfile.Cosmetics.ChatBubble == "bubble-petal", "공개 프로필은 저장된 착용만 전달해야 합니다.");
        await database.RequestFriendAsync(account, other); await database.RespondFriendAsync(other, account, true);
        Check((await database.FriendsAsync(other)).Friends.Single().Cosmetics.AvatarFrame == "frame-stars", "친구 목록에 착용값을 전달해야 합니다.");
        var game = await database.IssueSessionAsync(account, "game:cosmetic-test");
        Check((await database.AuthenticateLobbyAsync(game.Token, "game:cosmetic-test", default)).Cosmetics.ChatBubble == "bubble-petal",
            "로비 채팅 인증은 서버에 저장된 착용을 읽어야 합니다.");
        var cleared = await database.EquipCosmeticAsync(account, new CosmeticEquipRequest { Category = CosmeticRules.CHAT_BUBBLE }, default);
        Check(cleared.Cosmetics.ChatBubble == "" && cleared.Cosmetics.NicknameEffect == "nickname-sunset"
            && cleared.Cosmetics.AvatarFrame == "frame-stars" && cleared.OwnedCosmetics.Length == 20,
            "기본으로 해제해도 다른 카테고리 착용과 영구 보유권을 보존해야 합니다.");
        Report("꾸미기 PostgreSQL 가격/보유 검증·원자적 롤백·동시 멱등 구매·20개 구매/장착·공개/친구/로비 전달·해제 검증");
    }
}
