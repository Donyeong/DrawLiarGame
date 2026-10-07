using System.Text.Json;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<ProfileData> PurchaseBatchAsync(Guid accountId, PurchaseBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.OperationId, out Guid operationId) || operationId == Guid.Empty)
            throw new ApiException("InvalidOperation");
        if (request.ProductIds == null || request.ProductIds.Length is < 1 or > PurchaseBatchRequest.MAX_PRODUCTS
            || request.ProductIds.Any(string.IsNullOrWhiteSpace)
            || request.ProductIds.Distinct(StringComparer.Ordinal).Count() != request.ProductIds.Length)
            throw new ApiException("InvalidPurchaseBatch");

        string[] productIds = request.ProductIds.Order(StringComparer.Ordinal).ToArray();
        var products = productIds.Select(id => ShopProducts.FirstOrDefault(product => product.Id == id)
            ?? throw new ApiException("ProductUnavailable")).ToArray();
        string receipt = "batch:" + JsonSerializer.Serialize(productIds);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var account = Command(connection, transaction,
            "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", accountId))
            if (await account.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);

        await using (var operation = Command(connection, transaction,
            "SELECT \"ProductId\" FROM \"PurchaseReceipt\" WHERE \"AccountId\"=$1 AND \"OperationId\"=$2", accountId, operationId))
        {
            var existing = await operation.ExecuteScalarAsync(cancellationToken);
            if (existing != null && (string)existing != receipt) throw new ApiException("OperationConflict", 409);
            if (existing == null)
            {
                var owned = (await ReadOwnedAccessories(connection, transaction, accountId)).ToList();
                var purchases = new List<ShopProduct>();
                foreach (var product in products.OrderByDescending(product => AvatarParts.Slots.Count(slot => AvatarParts.Get(product.Accessory, slot) != 0)))
                {
                    if (AvatarParts.IsOwned(owned, product.Accessory)) continue;
                    purchases.Add(product);
                    owned.Add(product.Accessory);
                }

                int price = checked(purchases.Sum(product => product.Price));
                await using (var debit = Command(connection, transaction,
                    "UPDATE \"Account\" SET \"Coins\"=\"Coins\"-$2 WHERE \"Id\"=$1 AND \"Coins\">=$2 AND NOT \"IsBanned\"", accountId, price))
                    if (await debit.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("InsufficientCoins", 409);
                foreach (var product in purchases)
                {
                    await using var grant = Command(connection, transaction,
                        "INSERT INTO \"OwnedAccessory\" (\"AccountId\",\"Accessory\") VALUES ($1,$2)", accountId, product.Accessory);
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var record = Command(connection, transaction,
                    "INSERT INTO \"PurchaseReceipt\" (\"AccountId\",\"OperationId\",\"ProductId\") VALUES ($1,$2,$3)", accountId, operationId, receipt);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var profile = await ReadProfile(connection, transaction, accountId);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }
}
