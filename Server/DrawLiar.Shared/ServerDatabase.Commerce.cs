using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase : ICommerceRepository
{
    private const string COMMERCE_ORDER_COLUMNS = "\"OrderId\",\"OperationId\",\"ProductId\",\"Provider\",\"StoreProductId\",\"AccountBinding\",\"ProductJson\",\"State\",\"CreatedAt\",\"CompletedAt\",\"AppScope\"";

    public async Task<ProfileData> CommerceProfileAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await ReadProfile(connection, null, accountId);
    }

    private static async Task LockCommerceAccount(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", accountId);
        if (await command.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);
    }

    private static CommerceStoredOrder ReadCommerceOrder(NpgsqlDataReader reader, Guid accountId) => new(accountId, reader.GetGuid(1),
        new CommerceOrderData
        {
            OrderId = reader.GetGuid(0).ToString(), ProductId = reader.GetString(2), Provider = reader.GetString(3),
            StoreProductId = reader.GetString(4), AccountBinding = reader.GetString(5), State = reader.GetString(7),
            CreatedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(8)),
            CompletedAt = reader.IsDBNull(9) ? "" : ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(9))
        }, JsonSerializer.Deserialize<CommerceProduct>(reader.GetString(6), ServerRuntime.Json)
            ?? throw new InvalidOperationException("결제 상품 스냅샷이 없습니다."), reader.GetString(10));

    public async Task<CommerceStoredOrder> PrepareCommerceOrderAsync(CommerceStoredOrder candidate, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, candidate.AccountId, cancellationToken);
        CommerceStoredOrder? existing = null;
        await using (var command = Command(connection, transaction,
            "SELECT " + COMMERCE_ORDER_COLUMNS + " FROM \"CommerceOrder\" WHERE \"AccountId\"=$1 AND \"OperationId\"=$2",
            candidate.AccountId, candidate.OperationId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) existing = ReadCommerceOrder(reader, candidate.AccountId);
        if (existing != null)
        {
            if (existing.Order.ProductId != candidate.Order.ProductId || existing.Order.Provider != candidate.Order.Provider
                || existing.AppScope != candidate.AppScope)
                throw new ApiException("OperationConflict", 409);
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        await using (var pending = Command(connection, transaction,
            "SELECT count(*) FROM \"CommerceOrder\" WHERE \"AccountId\"=$1 AND \"State\"='Prepared'", candidate.AccountId))
            if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken)) >= 32) throw new ApiException("PaymentOrderLimit", 429);
        await using (var command = Command(connection, transaction, """
            INSERT INTO "CommerceOrder" ("OrderId","AccountId","OperationId","ProductId","Provider","StoreProductId","AccountBinding","ProductJson","CreatedAt","AppScope")
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9,$10)
            """, Guid.Parse(candidate.Order.OrderId), candidate.AccountId, candidate.OperationId, candidate.Order.ProductId,
            candidate.Order.Provider, candidate.Order.StoreProductId, candidate.Order.AccountBinding,
            JsonSerializer.Serialize(candidate.Product, ServerRuntime.Json), DateTimeOffset.Parse(candidate.Order.CreatedAt), candidate.AppScope))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidate;
    }

    public async Task<CommerceStoredOrder> CommerceOrderAsync(Guid accountId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT " + COMMERCE_ORDER_COLUMNS + " FROM \"CommerceOrder\" WHERE \"AccountId\"=$1 AND \"OrderId\"=$2 AND EXISTS (SELECT 1 FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\")",
            accountId, orderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("PaymentOrderNotFound", 404);
        return ReadCommerceOrder(reader, accountId);
    }

    public async Task<CommerceOrdersResponse> CommerceOrdersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT " + COMMERCE_ORDER_COLUMNS + " FROM \"CommerceOrder\" WHERE \"AccountId\"=$1 ORDER BY (\"State\"='Prepared') DESC,\"CreatedAt\" DESC LIMIT 100", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var orders = new List<CommerceOrderData>();
        while (await reader.ReadAsync(cancellationToken)) orders.Add(ReadCommerceOrder(reader, accountId).Order);
        return new CommerceOrdersResponse { Orders = orders.ToArray(), ServerTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
    }

    public async Task<CommercePaymentResponse> CompleteCommerceOrderAsync(CommerceStoredOrder verified, string appScope,
        string transactionId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, verified.AccountId, cancellationToken);
        Guid orderId = Guid.Parse(verified.Order.OrderId);
        CommerceStoredOrder stored;
        await using (var command = Command(connection, transaction,
            "SELECT " + COMMERCE_ORDER_COLUMNS + " FROM \"CommerceOrder\" WHERE \"AccountId\"=$1 AND \"OrderId\"=$2 FOR UPDATE", verified.AccountId, orderId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("PaymentOrderNotFound", 404);
            stored = ReadCommerceOrder(reader, verified.AccountId);
        }
        if (stored.Order.State == "ReviewRequired") throw new ApiException("PaymentReviewRequired", 409);
        if (stored.Order.State != "Completed")
        {
            string hash = ServerRuntime.Hash(transactionId);
            await using (var claim = Command(connection, transaction, """
                INSERT INTO "CommerceTransaction" ("Provider","AppScope","TransactionHash","OrderId","AccountId")
                VALUES ($1,$2,$3,$4,$5) ON CONFLICT ("Provider","AppScope","TransactionHash") DO NOTHING
                """, stored.Order.Provider, appScope, hash, orderId, stored.AccountId))
                await claim.ExecuteNonQueryAsync(cancellationToken);
            await using (var owner = Command(connection, transaction,
                "SELECT \"OrderId\" FROM \"CommerceTransaction\" WHERE \"Provider\"=$1 AND \"AppScope\"=$2 AND \"TransactionHash\"=$3",
                stored.Order.Provider, appScope, hash))
                if (await owner.ExecuteScalarAsync(cancellationToken) is not Guid claimedOrder || claimedOrder != orderId)
                    throw new ApiException("PaymentTransactionAlreadyClaimed", 409);
            if (stored.Product.Kind != CommerceRules.PAID_GEMS || stored.Product.PaidGems <= 0) throw new ApiException("InvalidPayment");
            int balance;
            await using (var credit = Command(connection, transaction, """
                UPDATE "Account" SET "PaidGems"="PaidGems"+$2 WHERE "Id"=$1 AND "PaidGems"<=$3 RETURNING "PaidGems"
                """, stored.AccountId, stored.Product.PaidGems, int.MaxValue - stored.Product.PaidGems))
                balance = await credit.ExecuteScalarAsync(cancellationToken) is int value ? value : throw new ApiException("PaidGemLimit", 409);
            await using (var ledger = Command(connection, transaction, """
                INSERT INTO "CommerceLedger" ("AccountId","OperationId","ProductId","Kind","PaidGemsDelta","BalanceAfter")
                VALUES ($1,$2,$3,$4,$5,$6)
                """, stored.AccountId, orderId, stored.Product.Id, CommerceRules.PAID_GEMS, stored.Product.PaidGems, balance))
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            var completed = DateTimeOffset.UtcNow;
            await using (var finish = Command(connection, transaction,
                "UPDATE \"CommerceOrder\" SET \"State\"='Completed',\"CompletedAt\"=$2 WHERE \"OrderId\"=$1", orderId, completed))
                await finish.ExecuteNonQueryAsync(cancellationToken);
            stored.Order.State = "Completed"; stored.Order.CompletedAt = ServerRuntime.Timestamp(completed);
        }
        var profile = await ReadProfile(connection, transaction, stored.AccountId);
        await transaction.CommitAsync(cancellationToken);
        return new CommercePaymentResponse { Order = stored.Order, Product = stored.Product, Profile = profile };
    }

    public async Task<ProfileData> PurchaseCommerceAsync(Guid accountId, CommercePurchaseRequest request, CancellationToken cancellationToken)
    {
        if (request == null || !Guid.TryParse(request.OperationId, out Guid operationId) || operationId == Guid.Empty)
            throw new ApiException("InvalidOperation");
        var product = CommerceRules.CreateProducts().FirstOrDefault(product => product.Id == request.ProductId
            && product.Kind == CommerceRules.PAINTER_SUBSCRIPTION) ?? throw new ApiException("ProductUnavailable");
        if (request.ExpectedPricePaidGems != product.PricePaidGems) throw new ApiException("ProductChanged", 409);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        bool purchased;
        await using (var receipt = Command(connection, transaction,
            "SELECT \"ProductId\" FROM \"CommerceLedger\" WHERE \"AccountId\"=$1 AND \"OperationId\"=$2 AND \"Kind\"=$3", accountId, operationId, CommerceRules.PAINTER_SUBSCRIPTION))
        {
            var previous = await receipt.ExecuteScalarAsync(cancellationToken);
            if (previous != null && (string)previous != product.Id) throw new ApiException("OperationConflict", 409);
            purchased = previous != null;
        }
        if (!purchased)
        {
            DateTimeOffset? previousExpiry;
            await using (var expiry = Command(connection, transaction, "SELECT \"SubscriptionExpiresAt\" FROM \"Account\" WHERE \"Id\"=$1", accountId))
            await using (var reader = await expiry.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                previousExpiry = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
            }
            var newExpiry = CommerceRules.ExtendSubscription(previousExpiry, DateTimeOffset.UtcNow, product.SubscriptionDays);
            int balance;
            await using (var debit = Command(connection, transaction, """
                UPDATE "Account" SET "PaidGems"="PaidGems"-$2,"SubscriptionExpiresAt"=$3
                WHERE "Id"=$1 AND "PaidGems">=$2 RETURNING "PaidGems"
                """, accountId, product.PricePaidGems, newExpiry))
                balance = await debit.ExecuteScalarAsync(cancellationToken) is int value ? value : throw new ApiException("InsufficientPaidGems", 409);
            await using var ledger = Command(connection, transaction, """
                INSERT INTO "CommerceLedger" ("AccountId","OperationId","ProductId","Kind","PaidGemsDelta","BalanceAfter","SubscriptionExpiresAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7)
                """, accountId, operationId, product.Id, product.Kind, -product.PricePaidGems, balance, newExpiry);
            await ledger.ExecuteNonQueryAsync(cancellationToken);
        }
        var profile = await ReadProfile(connection, transaction, accountId);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task<ProfileData> SetSubscriberBadgeAsync(Guid accountId, bool showBadge, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        await using (var badge = Command(connection, transaction, """
            UPDATE "Account" SET "ShowSubscriberBadge"=$2
            WHERE "Id"=$1 AND (NOT $2 OR "SubscriptionExpiresAt">now())
            """, accountId, showBadge))
            if (await badge.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("SubscriptionRequired", 403);
        var profile = await ReadProfile(connection, transaction, accountId);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task FlagCommerceTransactionForReviewAsync(VerifiedCommerceReversal reversal, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var find = Command(connection, transaction,
            "SELECT \"OrderId\",\"AccountId\" FROM \"CommerceTransaction\" WHERE \"Provider\"=$1 AND \"AppScope\"=$2 AND \"TransactionHash\"=$3",
            reversal.Provider, reversal.AppScope, ServerRuntime.Hash(reversal.TransactionId));
        Guid orderId, accountId;
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("PaymentOrderNotFound", 404);
            orderId = reader.GetGuid(0); accountId = reader.GetGuid(1);
        }
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        await using (var review = Command(connection, transaction,
            "INSERT INTO \"CommerceReview\" (\"OrderId\",\"Reason\") VALUES ($1,$2) ON CONFLICT (\"OrderId\") DO NOTHING", orderId, reversal.Reason))
            await review.ExecuteNonQueryAsync(cancellationToken);
        await using (var state = Command(connection, transaction,
            "UPDATE \"CommerceOrder\" SET \"State\"='ReviewRequired' WHERE \"OrderId\"=$1", orderId))
            await state.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
