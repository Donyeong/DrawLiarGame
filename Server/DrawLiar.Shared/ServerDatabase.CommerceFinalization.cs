namespace DrawLiar.Server;

public sealed record PendingCommerceFinalization(CommerceStoredOrder Order, string ProtectedProof, int Attempts);

public interface ICommerceFinalizationRepository
{
    Task<CommercePaymentResponse> CompleteWithFinalizationAsync(CommerceStoredOrder order, string appScope,
        string transactionId, string protectedProof, CancellationToken cancellationToken);
    Task RequireCommerceTransactionAsync(CommerceStoredOrder order, string transactionId, CancellationToken cancellationToken);
    Task<PendingCommerceFinalization[]> ClaimCommerceFinalizationsAsync(CancellationToken cancellationToken);
    Task CompleteCommerceFinalizationAsync(Guid orderId, CancellationToken cancellationToken);
    Task RetryCommerceFinalizationAsync(Guid orderId, int attempts, CancellationToken cancellationToken);
}

public sealed partial class ServerDatabase : ICommerceFinalizationRepository
{
    public Task<CommercePaymentResponse> CompleteWithFinalizationAsync(CommerceStoredOrder order, string appScope,
        string transactionId, string protectedProof, CancellationToken cancellationToken) =>
        CompleteCommerceOrderCoreAsync(order, appScope, transactionId, protectedProof, cancellationToken);

    public async Task RequireCommerceTransactionAsync(CommerceStoredOrder order, string transactionId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT 1 FROM "CommerceTransaction" WHERE "OrderId"=$1 AND "AccountId"=$2 AND "Provider"=$3 AND "AppScope"=$4 AND "TransactionHash"=$5
            """, Guid.Parse(order.Order.OrderId), order.AccountId, order.Order.Provider, order.AppScope, ServerRuntime.Hash(transactionId));
        if (await command.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("InvalidPayment", 403);
    }

    public async Task<PendingCommerceFinalization[]> ClaimCommerceFinalizationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        string columns = string.Join(",", COMMERCE_ORDER_COLUMNS.Split(',').Select(column => "o." + column));
        await using var command = Command(connection, null, $$"""
            WITH due AS (
                SELECT "OrderId" FROM "CommerceFinalization" WHERE "NextAttemptAt"<=now()
                ORDER BY "NextAttemptAt" FOR UPDATE SKIP LOCKED LIMIT 8
            ), claimed AS (
                UPDATE "CommerceFinalization" f SET "NextAttemptAt"=now()+interval '5 minutes',"Attempts"=LEAST(f."Attempts"+1,1000000)
                FROM due WHERE f."OrderId"=due."OrderId" RETURNING f.*
            )
            SELECT {{columns}},o."AccountId",f."ProtectedProof",f."Attempts" FROM claimed f JOIN "CommerceOrder" o ON o."OrderId"=f."OrderId"
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PendingCommerceFinalization>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PendingCommerceFinalization(ReadCommerceOrder(reader, reader.GetGuid(11)), reader.GetString(12), reader.GetInt32(13)));
        return items.ToArray();
    }

    public async Task CompleteCommerceFinalizationAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, "DELETE FROM \"CommerceFinalization\" WHERE \"OrderId\"=$1", orderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryCommerceFinalizationAsync(Guid orderId, int attempts, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "UPDATE \"CommerceFinalization\" SET \"NextAttemptAt\"=now()+$2 WHERE \"OrderId\"=$1", orderId,
            TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, Math.Min(attempts, 6)))));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
