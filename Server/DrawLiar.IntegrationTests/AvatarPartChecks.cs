using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

internal static partial class Integration
{
    private static readonly string[] RetiredClothingIds = { "painter-apron", "striped-shirt", "polka-dot-shirt", "overalls", "star-sweater" };
    private static readonly long[] RetiredClothingBits = { 131072, 262144, 524288, 1048576, 2097152 };

    private static void VerifyAvatarPartRules()
    {
        long legacy = (long)(AvatarAccessory.Crown | AvatarAccessory.RoundGlasses | AvatarAccessory.Scarf | AvatarAccessory.Cape | AvatarAccessory.Palette);
        Check(AvatarParts.Slots.SequenceEqual(new[] { AvatarPartSlot.Head, AvatarPartSlot.Expression, AvatarPartSlot.Face,
                AvatarPartSlot.Body, AvatarPartSlot.Neck, AvatarPartSlot.Back, AvatarPartSlot.Hand })
            && (int)AvatarPartSlot.Clothing == 6 && (int)AvatarPartSlot.Body == 7 && AvatarParts.Mask(AvatarPartSlot.Clothing) == 0,
            "활성 카테고리는 일곱 부위이며 폐기한 옷 슬롯6과 몸통 슬롯7 번호를 재사용하면 안 됩니다.");
        Check(RetiredClothingBits.Aggregate(0L,(mask,bit)=>mask|bit) == AvatarParts.RETIRED_CLOTHING_MASK
            && (AvatarParts.ALL_MASK & AvatarParts.RETIRED_CLOTHING_MASK) == 0 && AvatarParts.Name(AvatarPartSlot.Expression)=="얼굴",
            "옷 비트는 활성 마스크에서 제외하고 얼굴 카테고리와 저장 호환 번호를 유지해야 합니다.");
        foreach(var face in AvatarParts.Items.Where(part=>part.Slot == AvatarPartSlot.Expression))
            foreach(var body in AvatarParts.Items.Where(part=>part.Slot == AvatarPartSlot.Body))
            {
                long active = legacy | (long)face.Accessory | (long)body.Accessory;
                Check(AvatarParts.IsValid(active) && AvatarParts.Sanitize(active) == active,"몸통6종과 얼굴14종은 기존 다섯 부위와 함께 착용해야 합니다.");
                foreach(long clothing in RetiredClothingBits)
                    Check(!AvatarParts.IsValid(active | clothing) && AvatarParts.Sanitize(active | clothing) == active
                        && AvatarParts.Equip(active,clothing) == active,"옛 옷은 옷만 제거하고 재착용은 거부해야 합니다.");
                Check(AvatarParts.Equip(active,(long)AvatarAccessory.BearBody) == (legacy | (long)face.Accessory | (long)AvatarAccessory.BearBody)
                    && AvatarParts.Remove(active,AvatarPartSlot.Body) == (legacy | (long)face.Accessory),"몸통 교체·해제는 나머지 부위를 보존해야 합니다.");
            }
        foreach(long invalid in new[] { -1L,long.MinValue,1L<<37,5L,96L,640L,2050L,(long)(AvatarAccessory.Wink|AvatarAccessory.CatFace),
            (long)(AvatarAccessory.RoundBody|AvatarAccessory.CatBody) }) Check(!AvatarParts.IsValid(invalid),"미지 비트·부호 비트·같은 부위 중복 착용은 거부해야 합니다.");
        Check(AvatarParts.Sanitize(-1)==0 && AvatarParts.Sanitize(5)==1 && AvatarParts.IsValid((long)AvatarAccessory.Painter),
            "잘못된 저장은 정규화하고 기존 비트3의 화가 세트는 보존해야 합니다.");
        Check(AvatarParts.DefaultAccessories.SequenceEqual(new[] { AvatarAccessory.Beret,AvatarAccessory.Wink,AvatarAccessory.RoundBody }),
            "기본 지급은 베레모·윙크·동그란 몸통 세 품목이어야 합니다.");
        var products = ServerDatabase.ShopProducts;
        long[] activeBits = Enum.GetValues<AvatarAccessory>().Select(part=>(long)part).Where(bit=>bit>0 && (bit&(bit-1))==0
            && (bit&AvatarParts.RETIRED_CLOTHING_MASK)==0).Order().ToArray();
        Check(AvatarParts.Items.Count==32 && products.Length==33 && products.Select(item=>item.Id).Distinct().Count()==33
            && activeBits.SequenceEqual(AvatarParts.Items.Select(item=>(long)item.Accessory).Order())
            && activeBits.Aggregate(0L,(mask,bit)=>mask|bit)==AvatarParts.ALL_MASK,"활성32파츠와 기존세트33상품을 중복 없이 제공해야 합니다.");
        Check(AvatarParts.Items.Count(part=>part.Slot==AvatarPartSlot.Body)==6 && AvatarParts.Items.Count(part=>part.Slot==AvatarPartSlot.Expression)==14
            && RetiredClothingIds.All(id=>products.All(product=>product.Id!=id)),"얼굴14종·몸통6종을 제공하고 옷 상품은 제거해야 합니다.");
        foreach(var part in AvatarParts.Items) Check(products.Count(item=>item.Id==part.Id && item.Name==part.Name && item.Price==part.Price
            && item.Accessory==(long)part.Accessory)==1 && AvatarParts.IsValid((long)part.Accessory),"상점은 공유 파츠의 ID·이름·가격·비트를 보존해야 합니다.");
        Check(products.Single(product=>product.Id=="painter").Price==180 && products.Single(product=>product.Id=="painter").Accessory==3,
            "기존 화가 세트의 ID·가격·비트는 유지해야 합니다.");
        long outfit = legacy | (long)(AvatarAccessory.CatFace|AvatarAccessory.BearBody);
        var session = new GameSession(new RoomSettings(),new GameData());
        Check(session.Join(1,"파츠검증",2,outfit|(long)AvatarAccessory.Overalls)
            && session.Snapshot(1,1,0).Players.Single().Accessory==outfit,"게임 입장은 옛 옷만 제거하고64비트 얼굴을 동기화해야 합니다.");
        session.UpdateProfile(1,"파츠검증",3,outfit|(long)AvatarAccessory.StarSweater);
        Check(session.Snapshot(1,1,0).Players.Single().Accessory==outfit,"게임 프로필 변경도 최상위 얼굴 비트를 보존해야 합니다.");
        Report("활성7부위·84얼굴/몸통 조합·옷5폐기/예약비트·33상품·기본3종·64비트/미지비트·게임 상태 정규화 검증");
    }

    private static async Task VerifyAvatarDatabaseAsync()
    {
        var scoped = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."))
        { SearchPath="drawliar_avatar_test_"+Guid.NewGuid().ToString("N"),Pooling=false,IncludeErrorDetail=false };
        Check(scoped.Host=="127.0.0.1" && scoped.Port==25439 && scoped.Database=="postgres","파츠 검증은 전용 PostgreSQL25439만 사용합니다.");
        await using var owner = new NpgsqlConnection(scoped.ConnectionString); await owner.OpenAsync();
        await using(var create = new NpgsqlCommand($"CREATE SCHEMA \"{scoped.SearchPath}\"",owner)) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
                { ["ConnectionStrings:DrawLiarDatabase"]=scoped.ConnectionString }).Build();
            using var database = new ServerDatabase(configuration); await database.InitializeAsync();
            await VerifyAvatarMigrationAsync(database,configuration,owner);
            Guid account=await database.DevelopmentAccountAsync("파츠 화가"),friend=await database.DevelopmentAccountAsync("친구 화가");
            await using(var coins=new NpgsqlCommand("UPDATE \"Account\" SET \"Coins\"=5000 WHERE \"Id\"=$1",owner))
            { coins.Parameters.AddWithValue(account); await coins.ExecuteNonQueryAsync(); }
            var purchase=new PurchaseRequest { ProductId="cat-face",OperationId=Guid.NewGuid().ToString() };
            var parallel=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>database.PurchaseAsync(account,purchase)));
            Check(parallel.All(profile=>profile.Coins==4840 && profile.OwnedAccessories.Contains((long)AvatarAccessory.CatFace)),
                "최상위64비트 얼굴의 병렬 구매 재시도는 한 번만 차감해야 합니다.");
            UpdateProfileRequest Outfit(long bits)=>new() { DisplayName="파츠 화가",AvatarColor=4,Accessory=bits };
            foreach(string id in RetiredClothingIds) await ExpectAvatarErrorAsync(()=>database.PurchaseAsync(account,
                new PurchaseRequest { ProductId=id,OperationId=Guid.NewGuid().ToString() }),"ProductUnavailable",400);
            foreach(long bit in RetiredClothingBits) await ExpectAvatarErrorAsync(()=>database.UpdateProfileAsync(account,Outfit(bit)),"InvalidAvatar",400);
            await ExpectAvatarErrorAsync(()=>database.UpdateProfileAsync(account,Outfit((long)AvatarAccessory.SkullFace)),"AccessoryNotOwned",403);
            await ExpectAvatarErrorAsync(()=>database.UpdateProfileAsync(account,Outfit(1L<<37)),"InvalidAvatar",400);
            foreach(var part in AvatarParts.Items.Where(part=>part.Id!="cat-face" && !AvatarParts.DefaultAccessories.Contains(part.Accessory)))
                await database.PurchaseAsync(account,new PurchaseRequest { ProductId=part.Id,OperationId=Guid.NewGuid().ToString() });
            long outfit=(long)(AvatarAccessory.Crown|AvatarAccessory.RoundGlasses|AvatarAccessory.Scarf|AvatarAccessory.Cape
                |AvatarAccessory.Palette|AvatarAccessory.CatFace|AvatarAccessory.BearBody);
            var saved=await database.UpdateProfileAsync(account,Outfit(outfit));
            Check(saved.Accessory==outfit && saved.Coins==5000-AvatarParts.Items.Where(part=>!AvatarParts.DefaultAccessories.Contains(part.Accessory)).Sum(part=>part.Price)
                && saved.OwnedAccessories.Aggregate(0L,(mask,item)=>mask|item)==AvatarParts.ALL_MASK,"활성 파츠의 구매·소유·64비트 얼굴이 영속되어야 합니다.");
            using(var reopened=new ServerDatabase(configuration))
                Check((await reopened.ProfileAsync(account)).OwnedAccessories.SequenceEqual(saved.OwnedAccessories)
                    && (await reopened.ProfileAsync(account)).Accessory==outfit,"새 연결도 bigint 소유와 착용을 복원해야 합니다.");
            await database.RequestFriendAsync(account,friend); await database.RespondFriendAsync(friend,account,true);
            Check((await database.FriendsAsync(friend)).Friends.Single().Accessory==outfit
                && (await database.PublicProfileAsync(friend,account)).Accessory==outfit,"친구·공개 프로필도64비트 외형을 전달해야 합니다.");
            await VerifyDedicatedAvatarAsync(saved);
            Report("격리 PostgreSQL64비트 구매/영속·병렬 멱등성·폐기 품목 거부·친구/프로필/DS 높은 얼굴 비트 검증");
        }
        finally { await using var drop=new NpgsqlCommand($"DROP SCHEMA \"{scoped.SearchPath}\" CASCADE",owner); await drop.ExecuteNonQueryAsync(); Report("파츠 검증 임시 스키마 삭제"); }
    }

    private static async Task VerifyDedicatedAvatarAsync(ProfileData profile)
    {
        var roomData=new ServerRoomData { RoomId=Guid.NewGuid().ToString(),OwnerAccountId=profile.AccountId };
        var room=new DedicatedRoom(roomData,new GameData(),Array.Empty<ServerTopicData>(),0,()=>{}); var socket=new PolicySocket();
        await using var connection=new GameConnection(socket,profile.AccountId,"fixture",CancellationToken.None);
        Check(room.Join(connection,new RedeemTicketResponse { AccountId=profile.AccountId,Profile=profile,Room=roomData },0),"bigint 외형으로 데디케이티드에 입장해야 합니다.");
        await WaitAsync(()=>socket.LastState?.Players.Length==1,3);
        Check(socket.LastState!.Players.Single().Accessory==profile.Accessory,"실제 DS 상태 JSON도64비트 얼굴을 보존해야 합니다.");
    }

    private static async Task VerifyAvatarMigrationAsync(ServerDatabase database,IConfiguration configuration,NpgsqlConnection owner)
    {
        long defaults=AvatarParts.DefaultAccessories.Aggregate(0L,(mask,part)=>mask|(long)part);
        void DefaultProfile(ProfileData profile)=>Check(profile.Coins==500 && profile.Accessory==0
            && profile.OwnedAccessories.Aggregate(0L,(mask,item)=>mask|item)==defaults && profile.OwnedAccessories.Count(item=>item!=0)==3,
            "신규 계정은 재화·착용을 보존하고 기본3종만 지급해야 합니다.");
        var request=NewGuestRequest(); var attempts=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>database.GuestLoginAsync(request)));
        var sessions=await Task.WhenAll(attempts.Select(session=>database.AuthenticateAsync(session.Token,"main")));
        Check(sessions.All(session=>session.AccountId==sessions[0].AccountId),"게스트 최초 병렬 접속은 계정 하나만 생성해야 합니다.");
        DefaultProfile(await database.ProfileAsync(sessions[0].AccountId));
        string subject="avatar-defaults-"+Guid.NewGuid().ToString("N");
        var google=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>database.GoogleAccountAsync(subject,"신규 Google",null)));
        Check(google.All(account=>account==google[0]),"Google 최초 병렬 접속은 계정 하나만 생성해야 합니다.");
        DefaultProfile(await database.ProfileAsync(google[0]));
        Guid legacy=await database.DevelopmentAccountAsync("옛 옷 화가");
        long active=(long)(AvatarAccessory.Crown|AvatarAccessory.Tearful|AvatarAccessory.CatBody);
        await using(var seed=new NpgsqlCommand("UPDATE \"Account\" SET \"Coins\"=137,\"Accessory\"=$2 WHERE \"Id\"=$1",owner))
        { seed.Parameters.AddWithValue(legacy); seed.Parameters.AddWithValue(active|(long)AvatarAccessory.Overalls); await seed.ExecuteNonQueryAsync(); }
        var receipts=new Dictionary<string,string>();
        for(int index=0;index<RetiredClothingIds.Length;index++)
        {
            await using(var owned=new NpgsqlCommand("INSERT INTO \"OwnedAccessory\" (\"AccountId\",\"Accessory\") VALUES ($1,$2)",owner))
            { owned.Parameters.AddWithValue(legacy); owned.Parameters.AddWithValue(RetiredClothingBits[index]); await owned.ExecuteNonQueryAsync(); }
            Guid operation=Guid.NewGuid(); receipts[RetiredClothingIds[index]]=operation.ToString();
            await using var receipt=new NpgsqlCommand("INSERT INTO \"PurchaseReceipt\" (\"AccountId\",\"OperationId\",\"ProductId\") VALUES ($1,$2,$3)",owner);
            receipt.Parameters.AddWithValue(legacy); receipt.Parameters.AddWithValue(operation); receipt.Parameters.AddWithValue(RetiredClothingIds[index]); await receipt.ExecuteNonQueryAsync();
        }
        foreach(string sql in new[] { "DELETE FROM \"SchemaVersion\" WHERE \"Version\"=15",
            "ALTER TABLE \"Account\" ALTER COLUMN \"Accessory\" TYPE integer USING \"Accessory\"::integer",
            "ALTER TABLE \"OwnedAccessory\" ALTER COLUMN \"Accessory\" TYPE integer USING \"Accessory\"::integer" })
        { await using var downgrade=new NpgsqlCommand(sql,owner); await downgrade.ExecuteNonQueryAsync(); }
        await database.InitializeAsync(); await database.InitializeAsync();
        var migrated=await database.ProfileAsync(legacy);
        Check(migrated.Coins==137 && migrated.Accessory==active && migrated.OwnedAccessories.All(item=>(item&AvatarParts.RETIRED_CLOTHING_MASK)==0),
            "015는 기존 integer를 bigint로 올리고 옷만 해제하며 재화·다른 외형을 보존해야 합니다.");
        using(var reopened=new ServerDatabase(configuration))
        { await reopened.InitializeAsync(); var restored=await reopened.ProfileAsync(legacy); Check(restored.Coins==137 && restored.Accessory==active
            && restored.OwnedAccessories.SequenceEqual(migrated.OwnedAccessories),"반복 초기화·재시작은 옷을 다시 착용하거나 환불하면 안 됩니다."); }
        await using(var counts=new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM "OwnedAccessory" WHERE "AccountId"=$1 AND ("Accessory" & $2)<>0),
                (SELECT count(*) FROM "PurchaseReceipt" WHERE "AccountId"=$1),
                (SELECT "Accessory" FROM "Account" WHERE "Id"=$1),
                (SELECT count(*) FROM "SchemaVersion" WHERE "Version"=14),
                (SELECT count(*) FROM "SchemaVersion" WHERE "Version"=15),
                (SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema()
                    AND table_name IN ('Account','OwnedAccessory') AND column_name='Accessory' AND data_type='bigint')
            """,owner))
        {
            counts.Parameters.AddWithValue(legacy); counts.Parameters.AddWithValue(AvatarParts.RETIRED_CLOTHING_MASK);
            await using var reader=await counts.ExecuteReaderAsync();
            Check(await reader.ReadAsync() && reader.GetInt64(0)==5 && reader.GetInt64(1)==5 && reader.GetInt64(2)==active
                && reader.GetInt64(3)==1 && reader.GetInt64(4)==1 && reader.GetInt64(5)==2,
                "015는 옷 소유5건·구매 이력5건·014 이력을 보존하고 두 컬럼을 bigint로 전환해야 합니다.");
        }
        foreach(string id in RetiredClothingIds) await ExpectAvatarErrorAsync(()=>database.PurchaseAsync(legacy,
            new PurchaseRequest { ProductId=id,OperationId=receipts[id] }),"ProductUnavailable",400);
        foreach(long bit in RetiredClothingBits) await ExpectAvatarErrorAsync(()=>database.UpdateProfileAsync(legacy,
            new UpdateProfileRequest { DisplayName="옛 옷 화가",AvatarColor=0,Accessory=active|bit }),"InvalidAvatar",400);
        Check((await database.PublicProfileAsync(legacy,legacy)).Accessory==active && (await database.ProfileAsync(legacy)).Coins==137,
            "옷의 이전 구매 재시도·재착용 거부 후 공개 프로필과 재화도 보존해야 합니다.");
        Report("기본3종 신규 게스트/Google 원자적 지급·015 integer→bigint·옷만 해제·소유/구매 이력/재화 보존·API 필터·멱등 마이그레이션 검증");
    }

    private static async Task VerifyAvatarHttpAsync(string mainUrl)
    {
        Check(mainUrl=="http://127.0.0.1:25550","파츠 HTTP는 전용 메인서버만 사용합니다.");
        var settings=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DRAWLIAR_AUTH_TEST_DATABASE")
            ?? throw new InvalidOperationException("DRAWLIAR_AUTH_TEST_DATABASE가 필요합니다."));
        Check(settings.Host=="127.0.0.1" && settings.Port==25439 && (settings.SearchPath??"").StartsWith("drawliar_avatar_http_test_",StringComparison.Ordinal),
            "파츠 HTTP는 전용 PostgreSQL 임시 스키마만 사용합니다.");
        await using var owner=new NpgsqlConnection(settings.ConnectionString); await owner.OpenAsync(); using var main=Client(mainUrl);
        var user=await GuestAndEnterAsync(main); Check(user.Login.GameServerUrl=="http://127.0.0.1:25560","파츠 HTTP는 전용 게임서버만 사용합니다.");
        using var game=Client(user.Login.GameServerUrl); string token=user.Session.SessionToken;
        var shop=await GetAsync<ShopResponse>(game,"/api/shop",token);
        Check(shop.Products.Length==33 && shop.Products.All(product=>!RetiredClothingIds.Contains(product.Id)),"HTTP는 옷을 제외한33상품만 전달해야 합니다.");
        Check(user.Login.Profile.Coins==500 && user.Login.Profile.OwnedAccessories.Count(item=>item!=0)==3
            && user.Login.Profile.OwnedAccessories.All(item=>(item&AvatarParts.RETIRED_CLOTHING_MASK)==0),"HTTP 첫 접속은 옷 없이 기본3종만 소유해야 합니다.");
        foreach(string id in RetiredClothingIds)
        {
            using var response=await SendAsync(game,"/api/shop/purchase",new PurchaseRequest { ProductId=id,OperationId=Guid.NewGuid().ToString() },token);
            Check(response.StatusCode==HttpStatusCode.BadRequest && (await response.Content.ReadFromJsonAsync<ApiError>(Json))?.Code=="ProductUnavailable",
                "폐기한 옷의 HTTP 구매는 불가해야 합니다.");
        }
        foreach(long bit in RetiredClothingBits) await AvatarHttpProfileAsync(game,token,bit,"InvalidAvatar",HttpStatusCode.BadRequest);
        var cat=new PurchaseRequest { ProductId="cat-face",OperationId=Guid.NewGuid().ToString() };
        var purchases=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>PostAsync<ProfileData>(game,"/api/shop/purchase",cat,token)));
        Check(purchases.All(profile=>profile.Coins==340 && profile.OwnedAccessories.Contains((long)AvatarAccessory.CatFace)),
            "bit36 얼굴의 HTTP 병렬 재시도는 정확히 한 번 차감해야 합니다.");
        long outfit=(long)(AvatarAccessory.Beret|AvatarAccessory.RoundBody|AvatarAccessory.CatFace);
        await AvatarHttpProfileAsync(game,token,outfit); var saved=await GetAsync<ProfileData>(game,"/api/profile",token);
        Check(saved.Accessory==outfit && saved.Coins==340,"bit36 얼굴은 HTTP 저장·재조회에 정확히 전달되어야 합니다.");
        foreach(long bit in RetiredClothingBits) await AvatarHttpProfileAsync(game,token,outfit|bit,"InvalidAvatar",HttpStatusCode.BadRequest);
        await AvatarHttpProfileAsync(game,token,1L<<37,"InvalidAvatar",HttpStatusCode.BadRequest);
        await AvatarHttpProfileAsync(game,token,(long)AvatarAccessory.SkullFace,"AccessoryNotOwned",HttpStatusCode.Forbidden);
        await using(var legacy=new NpgsqlCommand("INSERT INTO \"OwnedAccessory\" (\"AccountId\",\"Accessory\") VALUES ($1,131072)",owner))
        { legacy.Parameters.AddWithValue(Guid.Parse(user.Login.AccountId)); await legacy.ExecuteNonQueryAsync(); }
        var hidden=await GetAsync<ProfileData>(game,"/api/profile",token);
        Check(hidden.Accessory==outfit && hidden.Coins==340 && hidden.OwnedAccessories.All(item=>(item&AvatarParts.RETIRED_CLOTHING_MASK)==0),
            "DB에 보존된 옛 옷 소유를 HTTP에 다시 노출하거나64비트 외형을 바꾸면 안 됩니다.");
        Report("HTTP33상품·기본3종·옷5구매/착용 거부·bit36얼굴 병렬 구매/저장·미지/미보유 비트 거부·옛 소유 비노출 검증");
    }

    private static async Task AvatarHttpProfileAsync(HttpClient game,string token,long accessory,string? error=null,HttpStatusCode status=HttpStatusCode.OK)
    {
        using var request=new HttpRequestMessage(HttpMethod.Patch,"/api/profile")
        { Content=JsonContent.Create(new UpdateProfileRequest { DisplayName="파츠 HTTP 검증",AvatarColor=2,Accessory=accessory },options:Json) };
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token); using var response=await game.SendAsync(request);
        Check(response.StatusCode==status,"파츠 HTTP 착용은 예상 상태 코드를 반환해야 합니다.");
        if(error!=null) Check((await response.Content.ReadFromJsonAsync<ApiError>(Json))?.Code==error,"잘못된 HTTP 파츠 착용은 해당 오류 코드로 거부해야 합니다.");
        else Check((await response.Content.ReadFromJsonAsync<ProfileData>(Json))?.Accessory==accessory,"HTTP 응답은 유효한64비트 착용을 보존해야 합니다.");
    }

    private static async Task ExpectAvatarErrorAsync(Func<Task<ProfileData>> action,string code,int status)
    {
        try { await action(); } catch(ApiException error) when(error.Code==code && error.Status==status) { return; }
        throw new InvalidOperationException("잘못된 파츠 요청은 "+code+"로 거부해야 합니다.");
    }
}
