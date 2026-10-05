using System.Text.Json;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<RoomConfigurationData> ConfigureRoomAsync(ConfigureRoomRequest request, CancellationToken cancellationToken = default)
    {
        Guid roomId = ServerRuntime.AccountId(request.RoomId), owner = ServerRuntime.AccountId(request.OwnerAccountId);
        if (!Guid.TryParse(request.OperationId, out Guid operationId) || request.ExpectedVersion < 0) throw new ApiException("InvalidOperation");
        var settings = request.Settings ?? throw new ApiException("InvalidSettings");
        ValidateSettings(settings);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? currentHash;
        ServerRoomSettings current;
        long version, accessVersion;
        Guid currentOwner;
        bool inProgress;
        Guid? lastOperation;
        string[] customTopics;
        await using (var command = Command(connection, transaction, """
            SELECT r."OwnerAccountId",r."IsInProgress",r."Settings"::text,r."PasswordHash",r."ConfigurationVersion",
                r."AccessVersion",r."LastConfigurationId",r."CustomTopics"::text
            FROM "Room" r JOIN "DedicatedNode" d ON d."NodeId"=r."NodeId"
            WHERE r."RoomId"=$1 AND r."NodeId"=$2 AND d."HeartbeatAt">now()-interval '30 seconds'
                AND r."UpdatedAt">now()-interval '90 seconds' FOR UPDATE OF r
            """, roomId, request.NodeId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("RoomUnavailable", 404);
            currentOwner = reader.GetGuid(0);
            inProgress = reader.GetBoolean(1);
            current = JsonSerializer.Deserialize<ServerRoomSettings>(reader.GetString(2), ServerRuntime.Json)!;
            currentHash = reader.IsDBNull(3) ? null : reader.GetString(3);
            version = reader.GetInt64(4);
            accessVersion = reader.GetInt64(5);
            lastOperation = reader.IsDBNull(6) ? null : reader.GetGuid(6);
            customTopics = (JsonSerializer.Deserialize<ServerTopicData[]>(reader.GetString(7), ServerRuntime.Json) ?? []).Select(topic => topic.Name).ToArray();
        }
        if (lastOperation == operationId)
            return new RoomConfigurationData { RoomId = request.RoomId, Settings = current, Version = version, AccessVersion = accessVersion };
        if (currentOwner != owner || inProgress) throw new ApiException("RoomConfigurationDenied", 403);
        await using (var account = Command(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR SHARE", owner))
            if (await account.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);
        if (version != request.ExpectedVersion) throw new ApiException("RoomConfigurationChanged", 409);
        var knownTopics = new HashSet<string>(_builtInTopicNames.Value.Concat(customTopics), StringComparer.Ordinal);
        if (settings.Topics.Length == 0 || settings.Topics.Any(topic => !knownTopics.Contains(topic))) throw new ApiException("InvalidTopics");
        string? passwordHash = null;
        if (settings.IsPrivate)
        {
            if (!string.IsNullOrEmpty(request.Password)) passwordHash = RoomPassword.Hash(request.Password);
            else passwordHash = current.IsPrivate && currentHash != null ? currentHash : throw new ApiException("RoomPasswordRequired", 403);
        }
        bool changeAccess = current.IsPrivate != settings.IsPrivate || currentHash != passwordHash;
        if (changeAccess) accessVersion++;
        version++;
        await using (var update = Command(connection, transaction, """
            UPDATE "Room" SET "Settings"=$2::jsonb,"PasswordHash"=$3,"ConfigurationVersion"=$4,
                "AccessVersion"=$5,"LastConfigurationId"=$6,"UpdatedAt"=now() WHERE "RoomId"=$1
            """, roomId, JsonSerializer.Serialize(settings, ServerRuntime.Json), passwordHash, version, accessVersion, operationId))
            await update.ExecuteNonQueryAsync(cancellationToken);
        if (changeAccess)
        {
            await using (var tickets = Command(connection, transaction, "DELETE FROM \"JoinTicket\" WHERE \"RoomId\"=$1 AND \"AccessVersion\"<>$2", roomId, accessVersion))
                await tickets.ExecuteNonQueryAsync(cancellationToken);
            await using (var admissions = Command(connection, transaction, """
                DELETE FROM "RoomAdmission" a USING "Room" r WHERE a."RoomId"=$1 AND r."RoomId"=a."RoomId"
                    AND a."AccessVersion"<>$2 AND NOT (r."ConfirmedPlayerAccountIds" ? a."AccountId"::text
                        OR r."ConfirmedSpectatorAccountIds" ? a."AccountId"::text)
                """, roomId, accessVersion))
                await admissions.ExecuteNonQueryAsync(cancellationToken);
            await using var roster = Command(connection, transaction, """
                UPDATE "Room" SET "PlayerAccountIds"="ConfirmedPlayerAccountIds","SpectatorAccountIds"="ConfirmedSpectatorAccountIds",
                    "PlayerCount"=LEAST("PlayerCount",jsonb_array_length("ConfirmedPlayerAccountIds")),
                    "SpectatorCount"=LEAST("SpectatorCount",jsonb_array_length("ConfirmedSpectatorAccountIds")) WHERE "RoomId"=$1
                """, roomId);
            await roster.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new RoomConfigurationData { RoomId = request.RoomId, Settings = settings, Version = version, AccessVersion = accessVersion };
    }
}
