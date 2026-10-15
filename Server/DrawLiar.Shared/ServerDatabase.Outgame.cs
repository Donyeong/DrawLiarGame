using Npgsql;

namespace DrawLiar.Server;

public sealed record AdminAccountView(string AccountId, string DisplayName, int Coins, bool IsBanned);

public sealed partial class ServerDatabase
{
    private const int FRIEND_LIMIT = 200;
    private const int INCOMING_FRIEND_REQUEST_LIMIT = 100;
    private static readonly TimeSpan _friendRequestLifetime = TimeSpan.FromDays(30);
    public static ShopProduct[] ShopProducts { get; } = AvatarParts.CreateShopProducts();

    public async Task<ProfileData> PurchaseAsync(Guid accountId, PurchaseRequest request)
    {
        if (!Guid.TryParse(request.OperationId, out Guid operationId)) throw new ApiException("InvalidOperation");
        var product = ShopProducts.FirstOrDefault(value => value.Id == request.ProductId) ?? throw new ApiException("ProductUnavailable");
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", accountId);
        var existing = await Scalar(connection, transaction, "SELECT \"ProductId\" FROM \"PurchaseReceipt\" WHERE \"AccountId\"=$1 AND \"OperationId\"=$2", accountId, operationId);
        if (existing != null)
        {
            if ((string)existing != product.Id) throw new ApiException("OperationConflict", 409);
        }
        else
        {
            long[] owned = await ReadOwnedAccessories(connection, transaction, accountId);
            if (AvatarParts.IsOwned(owned, product.Accessory))
                throw new ApiException("AlreadyOwned", 409);
            int changed = await Execute(connection, transaction, "UPDATE \"Account\" SET \"Coins\"=\"Coins\"-$2 WHERE \"Id\"=$1 AND \"Coins\">=$2 AND NOT \"IsBanned\"", accountId, product.Price);
            if (changed != 1) throw new ApiException("InsufficientCoins", 409);
            await Execute(connection, transaction, "INSERT INTO \"OwnedAccessory\" (\"AccountId\",\"Accessory\") VALUES ($1,$2)", accountId, product.Accessory);
            await Execute(connection, transaction, "INSERT INTO \"PurchaseReceipt\" (\"AccountId\",\"OperationId\",\"ProductId\") VALUES ($1,$2,$3)", accountId, operationId, product.Id);
        }
        var profile = await ReadProfile(connection, transaction, accountId);
        await transaction.CommitAsync();
        return profile;
    }

    public async Task<FriendListResponse> FriendsAsync(Guid accountId)
    {
        var friends = new List<FriendData>();
        var incoming = new List<FriendData>();
        var outgoing = new List<FriendData>();
        await using var connection = await _source.OpenConnectionAsync();
        const string sql = """
            SELECT a."Id",a."DisplayName",a."AvatarColor",a."Accessory",f."Accepted",f."FromAccountId",a."Experience",a."Cosmetics"::text
            FROM "Friendship" f JOIN "Account" a ON a."Id"=CASE WHEN f."FromAccountId"=$1 THEN f."ToAccountId" ELSE f."FromAccountId" END
            WHERE (f."FromAccountId"=$1 OR f."ToAccountId"=$1) AND (f."Accepted" OR f."CreatedAt">$2)
                AND NOT a."IsBanned" ORDER BY a."DisplayName"
            """;
        await using var command = Command(connection, null, sql, accountId, DateTimeOffset.UtcNow - _friendRequestLifetime);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var friend = new FriendData { AccountId = reader.GetGuid(0).ToString(), DisplayName = reader.GetString(1), AvatarColor = reader.GetInt32(2), Accessory = AvatarParts.Sanitize(reader.GetInt64(3)), Level = AccountLevelRules.GetLevel(reader.GetInt64(6)), Cosmetics = ReadCosmetics(reader.GetString(7)) };
            if (reader.GetBoolean(4)) friends.Add(friend);
            else if (reader.GetGuid(5) == accountId) outgoing.Add(friend);
            else incoming.Add(friend);
        }
        return new FriendListResponse { Friends = friends.ToArray(), Incoming = incoming.ToArray(), Outgoing = outgoing.ToArray() };
    }

    public async Task RequestFriendAsync(Guid accountId, Guid target)
    {
        if (accountId == target) throw new ApiException("InvalidFriend");
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockFriendAccounts(connection, transaction, accountId, target);
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\"", target) == null) throw new ApiException("AccountNotFound", 404);
        if (await IsBlockingAsync(connection, transaction, accountId, target)) throw new ApiException("BlockedPlayer", 409);
        // 상대가 나를 차단했으면 차단 사실을 알리지 않고 요청을 전달하지 않는다.
        if (await IsBlockingAsync(connection, transaction, target, accountId))
        {
            await transaction.CommitAsync();
            return;
        }
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _friendRequestLifetime;
        await Execute(connection, transaction, """
            DELETE FROM "Friendship" WHERE (("FromAccountId"=$1 AND "ToAccountId"=$2)
                OR ("FromAccountId"=$2 AND "ToAccountId"=$1)) AND NOT "Accepted" AND "CreatedAt"<=$3
            """, accountId, target, cutoff);
        if (await Scalar(connection, transaction, """
            SELECT 1 FROM "Friendship" WHERE ("FromAccountId"=$1 AND "ToAccountId"=$2)
                OR ("FromAccountId"=$2 AND "ToAccountId"=$1)
            """, accountId, target) != null)
        {
            await transaction.CommitAsync();
            return;
        }
        const string countSql = """
            SELECT count(*) FROM "Friendship" WHERE ("FromAccountId"=$1 OR "ToAccountId"=$1)
                AND ("Accepted" OR "CreatedAt">$2)
            """;
        if (Convert.ToInt64(await Scalar(connection, transaction, countSql, accountId, cutoff)) >= FRIEND_LIMIT
            || Convert.ToInt64(await Scalar(connection, transaction, countSql, target, cutoff)) >= FRIEND_LIMIT)
            throw new ApiException("FriendLimit", 409);
        if (Convert.ToInt64(await Scalar(connection, transaction, """
            SELECT count(*) FROM "Friendship" WHERE "ToAccountId"=$1 AND NOT "Accepted" AND "CreatedAt">$2
            """, target, cutoff)) >= INCOMING_FRIEND_REQUEST_LIMIT)
            throw new ApiException("FriendLimit", 409);
        await Execute(connection, transaction, "INSERT INTO \"Friendship\" (\"FromAccountId\",\"ToAccountId\") VALUES ($1,$2) ON CONFLICT DO NOTHING", accountId, target);
        await transaction.CommitAsync();
    }

    public async Task RespondFriendAsync(Guid accountId, Guid sender, bool accept)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockFriendAccounts(connection, transaction, accountId, sender);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _friendRequestLifetime;
        if (await Scalar(connection, transaction, """
            SELECT 1 FROM "Friendship" WHERE "FromAccountId"=$2 AND "ToAccountId"=$1 AND NOT "Accepted" AND "CreatedAt">$3
            """, accountId, sender, cutoff) == null) throw new ApiException("FriendRequestNotFound", 404);
        if (accept)
        {
            const string countSql = "SELECT count(*) FROM \"Friendship\" WHERE (\"FromAccountId\"=$1 OR \"ToAccountId\"=$1) AND \"Accepted\"";
            if (Convert.ToInt64(await Scalar(connection, transaction, countSql, accountId)) >= FRIEND_LIMIT
                || Convert.ToInt64(await Scalar(connection, transaction, countSql, sender)) >= FRIEND_LIMIT)
                throw new ApiException("FriendLimit", 409);
        }
        string sql = accept
            ? "UPDATE \"Friendship\" SET \"Accepted\"=true WHERE \"FromAccountId\"=$2 AND \"ToAccountId\"=$1 AND NOT \"Accepted\" AND \"CreatedAt\">$3"
            : "DELETE FROM \"Friendship\" WHERE \"FromAccountId\"=$2 AND \"ToAccountId\"=$1 AND NOT \"Accepted\" AND \"CreatedAt\">$3";
        if (await Execute(connection, transaction, sql, accountId, sender, cutoff) != 1)
            throw new ApiException("FriendRequestNotFound", 404);
        await transaction.CommitAsync();
    }

    public async Task CancelFriendRequestAsync(Guid accountId, Guid target, CancellationToken cancellationToken = default)
    {
        if (accountId == target) throw new ApiException("InvalidFriend");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFriendAccounts(connection, transaction, accountId, target, cancellationToken);
        await using var command = Command(connection, transaction,
            "DELETE FROM \"Friendship\" WHERE \"FromAccountId\"=$1 AND \"ToAccountId\"=$2 AND NOT \"Accepted\" AND \"CreatedAt\">$3",
            accountId, target, DateTimeOffset.UtcNow - _friendRequestLifetime);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("FriendRequestNotFound", 404);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveFriendAsync(Guid accountId, Guid target)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockFriendAccounts(connection, transaction, accountId, target);
        await Execute(connection, transaction, "DELETE FROM \"Friendship\" WHERE (\"FromAccountId\"=$1 AND \"ToAccountId\"=$2) OR (\"FromAccountId\"=$2 AND \"ToAccountId\"=$1)", accountId, target);
        await transaction.CommitAsync();
    }

    private static async Task LockFriendAccounts(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid first, Guid second,
        CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction,
            "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=ANY($1) ORDER BY \"Id\" FOR UPDATE", new[] { first, second });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { }
    }

    public async Task<AdminAccountView[]> AdminAccountsAsync(string? search)
    {
        var profiles = new List<AdminAccountView>();
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null, "SELECT \"Id\",\"DisplayName\",\"Coins\",\"IsBanned\" FROM \"Account\" WHERE \"DisplayName\" ILIKE $1 ORDER BY \"CreatedAt\" DESC LIMIT 100", "%" + (search ?? "")[..Math.Min(search?.Length ?? 0, 32)] + "%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) profiles.Add(new AdminAccountView(reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        return profiles.ToArray();
    }

    public async Task AdminBanAsync(Guid accountId, bool isBanned)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (await Execute(connection, transaction, "UPDATE \"Account\" SET \"IsBanned\"=$2 WHERE \"Id\"=$1", accountId, isBanned) != 1) throw new ApiException("AccountNotFound", 404);
        if (isBanned) await Execute(connection, transaction, "DELETE FROM \"Session\" WHERE \"AccountId\"=$1", accountId);
        await transaction.CommitAsync();
    }
}
