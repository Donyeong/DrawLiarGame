using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<KickRoomResponse> KickRoomAsync(KickRoomRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.OperationId, out Guid operation) || operation == Guid.Empty) throw new ApiException("InvalidOperation");
        Guid roomId = ServerRuntime.AccountId(request.RoomId);
        Guid ownerId = ServerRuntime.AccountId(request.OwnerAccountId);
        Guid targetId = ServerRuntime.AccountId(request.TargetAccountId);
        if (ownerId == targetId || request.OwnerSessionToken?.Length != 64) throw new ApiException("RoomKickDenied", 403);
        if (request.TargetSessionToken?.Length != 64) throw new ApiException("RoomKickTargetUnavailable", 409);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var room = Command(connection, transaction,
            "SELECT \"OwnerAccountId\" FROM \"Room\" WHERE \"RoomId\"=$1 AND \"NodeId\"=$2 FOR UPDATE", roomId, request.NodeId))
            if (await room.ExecuteScalarAsync(cancellationToken) is not Guid currentOwner || currentOwner != ownerId)
                throw new ApiException("RoomKickDenied", 403);
        string scope = "dedicated:" + request.NodeId;
        await Execute(connection, transaction, "SELECT \"Hash\" FROM \"Session\" WHERE \"Hash\"=$1 FOR UPDATE", ServerRuntime.Hash(request.OwnerSessionToken));
        try
        {
            if ((await AuthenticateHash(connection, transaction, ServerRuntime.Hash(request.OwnerSessionToken), scope)).AccountId != ownerId)
                throw new ApiException("RoomKickDenied", 403);
        }
        catch (ApiException) { throw new ApiException("RoomKickDenied", 403); }
        if (await Scalar(connection, transaction,
            "SELECT 1 FROM \"Session\" WHERE \"Hash\"=$1 AND (\"DedicatedRoomId\" IS NULL OR \"DedicatedRoomId\"=$2)",
            ServerRuntime.Hash(request.OwnerSessionToken), roomId) == null) throw new ApiException("RoomKickDenied", 403);
        await using (var existing = Command(connection, transaction,
            "SELECT \"AccountId\",\"KickedByAccountId\" FROM \"RoomKick\" WHERE \"RoomId\"=$1 AND \"OperationId\"=$2", roomId, operation))
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetGuid(0) != targetId || reader.GetGuid(1) != ownerId) throw new ApiException("InvalidOperation");
                return new KickRoomResponse { RoomId = roomId.ToString(), AccountId = targetId.ToString(), OperationId = operation.ToString() };
            }
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"RoomKick\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", roomId, targetId) != null)
            throw new ApiException("RoomKickTargetUnavailable", 409);
        if (await Scalar(connection, transaction, """
            SELECT 1 FROM "Room" WHERE "RoomId"=$1 AND ("ConfirmedPlayerAccountIds" ? $2::text OR "ConfirmedSpectatorAccountIds" ? $2::text)
            """, roomId, targetId.ToString()) == null) throw new ApiException("RoomKickTargetUnavailable", 409);
        string targetHash = ServerRuntime.Hash(request.TargetSessionToken);
        await Execute(connection, transaction, "SELECT \"Hash\" FROM \"Session\" WHERE \"Hash\"=$1 FOR UPDATE", targetHash);
        if (await Scalar(connection, transaction,
            "SELECT 1 FROM \"Session\" WHERE \"Hash\"=$1 AND (\"AccountId\"<>$2 OR \"Scope\"<>$3)", targetHash, targetId, scope) != null)
            throw new ApiException("RoomKickTargetUnavailable", 409);
        await Execute(connection, transaction,
            "INSERT INTO \"RoomKick\" (\"RoomId\",\"AccountId\",\"KickedByAccountId\",\"OperationId\") VALUES ($1,$2,$3,$4)", roomId, targetId, ownerId, operation);
        await Execute(connection, transaction, "DELETE FROM \"JoinTicket\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", roomId, targetId);
        await Execute(connection, transaction, "DELETE FROM \"RoomAdmission\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", roomId, targetId);
        await Execute(connection, transaction,
            "DELETE FROM \"Session\" WHERE \"AccountId\"=$2 AND \"Scope\"=$3 AND (\"Hash\"=$1 OR \"DedicatedRoomId\"=$4)", targetHash, targetId, scope, roomId);
        await Execute(connection, transaction, """
            UPDATE "Room" SET "PlayerCount"=GREATEST(0,"PlayerCount"-CASE WHEN "PlayerAccountIds" ? $2::text THEN 1 ELSE 0 END),
                "SpectatorCount"=GREATEST(0,"SpectatorCount"-CASE WHEN "SpectatorAccountIds" ? $2::text THEN 1 ELSE 0 END),
                "PlayerAccountIds"="PlayerAccountIds"-$2::text,"SpectatorAccountIds"="SpectatorAccountIds"-$2::text,
                "ConfirmedPlayerAccountIds"="ConfirmedPlayerAccountIds"-$2::text,"ConfirmedSpectatorAccountIds"="ConfirmedSpectatorAccountIds"-$2::text
            WHERE "RoomId"=$1
            """, roomId, targetId.ToString());
        await transaction.CommitAsync(cancellationToken);
        return new KickRoomResponse { RoomId = roomId.ToString(), AccountId = targetId.ToString(), OperationId = operation.ToString() };
    }

    private static async Task EnsureNotKicked(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid roomId, Guid accountId)
    {
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"RoomKick\" WHERE \"RoomId\"=$1 AND \"AccountId\"=$2", roomId, accountId) != null)
            throw new ApiException("RoomKicked", 403);
    }
}
