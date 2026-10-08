using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private const int INVITATION_LIMIT = 50;
    private const string INVITATION_SELECT = """
        SELECT i."Id",r."RoomId",r."RoomCode",r."Settings"->>'RoomName',
            a."Id",a."DisplayName",a."AvatarColor",a."Accessory",i."ExpiresAt",a."Experience"
        FROM "RoomInvitation" i JOIN "Room" r ON r."RoomId"=i."RoomId"
        JOIN "DedicatedNode" d ON d."NodeId"=r."NodeId"
        JOIN "Account" a ON a."Id"=i."FromAccountId"
        JOIN "Account" receiver ON receiver."Id"=i."ToAccountId"
        JOIN "Friendship" f ON (f."FromAccountId"=i."FromAccountId" AND f."ToAccountId"=i."ToAccountId")
            OR (f."FromAccountId"=i."ToAccountId" AND f."ToAccountId"=i."FromAccountId")
        WHERE i."ToAccountId"=$1 AND i."Pending" AND i."ExpiresAt">now() AND f."Accepted"
            AND NOT a."IsBanned" AND NOT receiver."IsBanned"
            AND d."HeartbeatAt">now()-interval '30 seconds' AND r."UpdatedAt">now()-interval '90 seconds'
            AND r."PlayerCount"+r."SpectatorCount">0
            AND NOT (r."PlayerAccountIds" ? $1::text OR r."SpectatorAccountIds" ? $1::text)
        """;

    public async Task<SocialInboxResponse> SocialInboxAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var friends = await FriendsAsync(accountId);
        var invitations = new List<RoomInvitationData>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await RemoveExpiredInvitations(connection, cancellationToken);
        await using var command = Command(connection, null, INVITATION_SELECT + " ORDER BY i.\"CreatedAt\" DESC LIMIT 50", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) invitations.Add(ReadInvitation(reader));
        return new SocialInboxResponse { Friends = friends, RoomInvitations = invitations.ToArray() };
    }

    public async Task<RoomInvitationData> InviteToRoomAsync(ServerSession session, string identifier, Guid target,
        CancellationToken cancellationToken = default)
    {
        if (session.AccountId == target) throw new ApiException("InvalidFriend");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await RemoveExpiredInvitations(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AuthenticateHash(connection, transaction, session.Hash, session.Scope, cancellationToken);
        foreach (Guid account in new[] { session.AccountId, target }.Order())
        {
            await using var accountLock = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "social:" + account);
            await accountLock.ExecuteNonQueryAsync(cancellationToken);
        }
        Guid roomId;
        await using (var room = Command(connection, transaction, """
            SELECT r."RoomId",r."PlayerAccountIds" ? $2::text OR r."SpectatorAccountIds" ? $2::text,
                r."PlayerAccountIds" ? $3::text OR r."SpectatorAccountIds" ? $3::text
            FROM "Room" r JOIN "DedicatedNode" d ON d."NodeId"=r."NodeId"
            WHERE (r."RoomId"::text=$1 OR r."RoomCode"=$1)
                AND d."HeartbeatAt">now()-interval '30 seconds' AND r."UpdatedAt">now()-interval '90 seconds'
                AND r."PlayerCount"+r."SpectatorCount">0 FOR UPDATE OF r
            """, RoomIdentifier(identifier), session.AccountId, target))
        await using (var reader = await room.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("RoomUnavailable", 404);
            roomId = reader.GetGuid(0);
            if (!reader.GetBoolean(1)) throw new ApiException("NotRoomMember", 403);
            if (reader.GetBoolean(2)) throw new ApiException("AlreadyInRoom", 409);
        }
        await RequireFriendship(connection, transaction, session.AccountId, target, cancellationToken);
        Guid id = Guid.NewGuid();
        await using (var existing = Command(connection, transaction, """
            SELECT "Id","Pending" AND "ExpiresAt">now(),COALESCE("RespondedAt","CreatedAt")>now()-interval '30 seconds'
            FROM "RoomInvitation" WHERE "RoomId"=$1 AND "FromAccountId"=$2 AND "ToAccountId"=$3 FOR UPDATE
            """, roomId, session.AccountId, target))
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetBoolean(1)) id = reader.GetGuid(0);
                else if (reader.GetBoolean(2)) throw new ApiException("RoomInvitationCooldown", 429);
            }
        }
        await using (var limit = Command(connection, transaction, """
            SELECT (SELECT count(*) FROM "RoomInvitation" WHERE "FromAccountId"=$1 AND "Pending" AND "ExpiresAt">now() AND "Id"<>$3),
                (SELECT count(*) FROM "RoomInvitation" WHERE "ToAccountId"=$2 AND "Pending" AND "ExpiresAt">now() AND "Id"<>$3)
            """, session.AccountId, target, id))
        await using (var reader = await limit.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) >= INVITATION_LIMIT || reader.GetInt64(1) >= INVITATION_LIMIT)
                throw new ApiException("RoomInvitationLimit", 429);
        }
        await using (var insert = Command(connection, transaction, """
            INSERT INTO "RoomInvitation" ("Id","RoomId","FromAccountId","ToAccountId") VALUES ($1,$2,$3,$4)
            ON CONFLICT ("RoomId","FromAccountId","ToAccountId") DO UPDATE
            SET "Id"=$1,"CreatedAt"=now(),"ExpiresAt"=now()+interval '5 minutes',"Pending"=true,"RespondedAt"=NULL
            WHERE NOT "RoomInvitation"."Pending" OR "RoomInvitation"."ExpiresAt"<=now()
            """, id, roomId, session.AccountId, target))
            await insert.ExecuteNonQueryAsync(cancellationToken);
        RoomInvitationData invitation;
        await using (var read = Command(connection, transaction, INVITATION_SELECT + " AND i.\"Id\"=$2", target, id))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("RoomInvitationUnavailable", 404);
            invitation = ReadInvitation(reader);
        }
        await transaction.CommitAsync(cancellationToken);
        return invitation;
    }

    public async Task<DedicatedAssignment> RespondRoomInvitationAsync(ServerSession session, string identifier, bool accept, string password = "",
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(identifier, out Guid id)) throw new ApiException("InvalidInvitation");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AuthenticateHash(connection, transaction, session.Hash, session.Scope, cancellationToken);
        Guid roomId;
        await using (var lookup = Command(connection, transaction, "SELECT \"RoomId\" FROM \"RoomInvitation\" WHERE \"Id\"=$1 AND \"ToAccountId\"=$2 AND \"Pending\" AND \"ExpiresAt\">now()", id, session.AccountId))
            roomId = await lookup.ExecuteScalarAsync(cancellationToken) is Guid found ? found : throw new ApiException("RoomInvitationUnavailable", 404);
        // 방 삭제와 초대 수락은 방 → 초대 순서로 잠가 중복 배정과 교착을 막는다.
        await using (var roomLock = Command(connection, transaction, "SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 FOR UPDATE", roomId))
            if (await roomLock.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("RoomInvitationUnavailable", 404);
        Guid sender;
        await using (var invitation = Command(connection, transaction, "SELECT \"FromAccountId\" FROM \"RoomInvitation\" WHERE \"Id\"=$1 AND \"ToAccountId\"=$2 AND \"Pending\" AND \"ExpiresAt\">now() FOR UPDATE", id, session.AccountId))
            sender = await invitation.ExecuteScalarAsync(cancellationToken) is Guid from ? from : throw new ApiException("RoomInvitationUnavailable", 404);
        var assignment = new DedicatedAssignment();
        if (accept)
        {
            await RequireFriendship(connection, transaction, sender, session.AccountId, cancellationToken);
            assignment = await JoinRoom(connection, transaction, session, roomId.ToString(), false, password);
        }
        await using (var respond = Command(connection, transaction, "UPDATE \"RoomInvitation\" SET \"Pending\"=false,\"RespondedAt\"=now() WHERE \"Id\"=$1", id))
            await respond.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return assignment;
    }

    private static string RoomIdentifier(string identifier) => Guid.TryParse(identifier, out Guid id) ? id.ToString() : RoomCodes.Normalize(identifier);

    private static async Task RequireFriendship(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sender, Guid target,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT 1 FROM "Friendship" f JOIN "Account" a ON a."Id"=$2
            JOIN "Account" sender ON sender."Id"=$1
            WHERE ((f."FromAccountId"=$1 AND f."ToAccountId"=$2) OR (f."FromAccountId"=$2 AND f."ToAccountId"=$1))
                AND f."Accepted" AND NOT a."IsBanned" AND NOT sender."IsBanned" FOR SHARE OF f,a,sender
            """, sender, target);
        if (await command.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("NotFriends", 403);
    }

    private static RoomInvitationData ReadInvitation(NpgsqlDataReader reader) => new()
    {
        InvitationId = reader.GetGuid(0).ToString(), RoomId = reader.GetGuid(1).ToString(), RoomCode = reader.GetString(2), RoomName = reader.GetString(3),
        Sender = new FriendData { AccountId = reader.GetGuid(4).ToString(), DisplayName = reader.GetString(5), AvatarColor = reader.GetInt32(6), Accessory = AvatarParts.Sanitize(reader.GetInt64(7)), Level = AccountLevelRules.GetLevel(reader.GetInt64(9)) },
        ExpiresAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(8))
    };

    private static async Task RemoveExpiredInvitations(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, "DELETE FROM \"RoomInvitation\" WHERE \"ExpiresAt\"<=now() AND COALESCE(\"RespondedAt\",\"CreatedAt\")<=now()-interval '30 seconds'");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
