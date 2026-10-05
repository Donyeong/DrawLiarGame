using Npgsql;

namespace DrawLiar.Server;

public sealed record AdminAccountView(string AccountId, string DisplayName, int Coins, bool IsBanned);

public sealed partial class ServerDatabase
{
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
            int ownedMask = Convert.ToInt32(await Scalar(connection, transaction, "SELECT COALESCE(bit_or(\"Accessory\"),0) FROM \"OwnedAccessory\" WHERE \"AccountId\"=$1", accountId));
            if ((ownedMask & product.Accessory) == product.Accessory)
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
            SELECT a."Id",a."DisplayName",a."AvatarColor",a."Accessory",f."Accepted",f."FromAccountId"
            FROM "Friendship" f JOIN "Account" a ON a."Id"=CASE WHEN f."FromAccountId"=$1 THEN f."ToAccountId" ELSE f."FromAccountId" END
            WHERE (f."FromAccountId"=$1 OR f."ToAccountId"=$1) AND NOT a."IsBanned" ORDER BY a."DisplayName"
            """;
        await using var command = Command(connection, null, sql, accountId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var friend = new FriendData { AccountId = reader.GetGuid(0).ToString(), DisplayName = reader.GetString(1), AvatarColor = reader.GetInt32(2), Accessory = reader.GetInt32(3) };
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
        await Execute(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", accountId);
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\"", target) == null) throw new ApiException("AccountNotFound", 404);
        if (Convert.ToInt64(await Scalar(connection, transaction, "SELECT count(*) FROM \"Friendship\" WHERE \"FromAccountId\"=$1 OR \"ToAccountId\"=$1", accountId)) >= 200)
            throw new ApiException("FriendLimit", 409);
        await Execute(connection, transaction, "INSERT INTO \"Friendship\" (\"FromAccountId\",\"ToAccountId\") VALUES ($1,$2) ON CONFLICT DO NOTHING", accountId, target);
        await transaction.CommitAsync();
    }

    public async Task RespondFriendAsync(Guid accountId, Guid sender, bool accept)
    {
        await using var connection = await _source.OpenConnectionAsync();
        string sql = accept
            ? "UPDATE \"Friendship\" SET \"Accepted\"=true WHERE \"FromAccountId\"=$2 AND \"ToAccountId\"=$1 AND NOT \"Accepted\""
            : "DELETE FROM \"Friendship\" WHERE \"FromAccountId\"=$2 AND \"ToAccountId\"=$1 AND NOT \"Accepted\"";
        if (await Execute(connection, null, sql, accountId, sender) != 1) throw new ApiException("FriendRequestNotFound", 404);
    }

    public async Task CancelFriendRequestAsync(Guid accountId, Guid target, CancellationToken cancellationToken = default)
    {
        if (accountId == target) throw new ApiException("InvalidFriend");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "DELETE FROM \"Friendship\" WHERE \"FromAccountId\"=$1 AND \"ToAccountId\"=$2 AND NOT \"Accepted\"", accountId, target);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("FriendRequestNotFound", 404);
    }

    public async Task RemoveFriendAsync(Guid accountId, Guid target)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "DELETE FROM \"Friendship\" WHERE (\"FromAccountId\"=$1 AND \"ToAccountId\"=$2) OR (\"FromAccountId\"=$2 AND \"ToAccountId\"=$1)", accountId, target);
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
