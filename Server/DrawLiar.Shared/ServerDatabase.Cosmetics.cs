using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private static CosmeticLoadout ReadCosmetics(string json)
    {
        try { return CosmeticRules.Sanitize(JsonSerializer.Deserialize<CosmeticLoadout>(json, ServerRuntime.Json)!); }
        catch (JsonException) { return new CosmeticLoadout(); }
    }

    private static async Task<string[]> ReadOwnedCosmetics(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var owned = new List<string>();
        await using var command = Command(connection, transaction,
            "SELECT \"ProductId\" FROM \"OwnedCosmetic\" WHERE \"AccountId\"=$1 ORDER BY \"ProductId\"", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) owned.Add(reader.GetString(0));
        return CosmeticRules.SanitizeOwned(owned);
    }

    public async Task<ProfileData> EquipCosmeticAsync(Guid accountId, CosmeticEquipRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || !CosmeticRules.IsCategory(request.Category) || request.ProductId == null
            || request.ProductId.Length > 0 && CosmeticRules.Find(request.ProductId)?.Category != request.Category)
            throw new ApiException("InvalidCosmetic");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        var profile = await ReadProfile(connection, transaction, accountId);
        if (request.ProductId.Length > 0 && !profile.OwnedCosmetics.Contains(request.ProductId, StringComparer.Ordinal))
            throw new ApiException("CosmeticNotOwned", 403);
        profile.Cosmetics = CosmeticRules.SetEquipped(profile.Cosmetics, request.Category, request.ProductId);
        await using (var equip = Command(connection, transaction,
            "UPDATE \"Account\" SET \"Cosmetics\"=$2::jsonb WHERE \"Id\"=$1", accountId,
            JsonSerializer.Serialize(profile.Cosmetics, ServerRuntime.Json)))
            await equip.ExecuteNonQueryAsync(cancellationToken);
        await LogActivity(connection, transaction, "CosmeticEquip", accountId, new { request.Category, request.ProductId }, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    private static async Task PurchaseCosmetic(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        Guid operationId, CommerceProduct product, CancellationToken cancellationToken)
    {
        bool owned;
        await using (var ownership = Command(connection, transaction,
            "SELECT 1 FROM \"OwnedCosmetic\" WHERE \"AccountId\"=$1 AND \"ProductId\"=$2", accountId, product.Id))
            owned = await ownership.ExecuteScalarAsync(cancellationToken) != null;
        int price = owned ? 0 : product.PricePaidGems;
        int balance;
        await using (var debit = Command(connection, transaction, """
            UPDATE "Account" SET "PaidGems"="PaidGems"-$2
            WHERE "Id"=$1 AND "PaidGems">=$2 RETURNING "PaidGems"
            """, accountId, price))
            balance = await debit.ExecuteScalarAsync(cancellationToken) is int value
                ? value : throw new ApiException("InsufficientPaidGems", 409);
        if (!owned)
        {
            await using var grant = Command(connection, transaction,
                "INSERT INTO \"OwnedCosmetic\" (\"AccountId\",\"ProductId\") VALUES ($1,$2)", accountId, product.Id);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var ledger = Command(connection, transaction, """
            INSERT INTO "CommerceLedger" ("AccountId","OperationId","ProductId","Kind","PaidGemsDelta","BalanceAfter")
            VALUES ($1,$2,$3,$4,$5,$6)
            """, accountId, operationId, product.Id, product.Kind, -price, balance);
        await ledger.ExecuteNonQueryAsync(cancellationToken);
        await LogActivity(connection, transaction, "Purchase", accountId, new { ProductIds = new[] { product.Id }, PaidGems = price }, cancellationToken: cancellationToken);
    }
}
