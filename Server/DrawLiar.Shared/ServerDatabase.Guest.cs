namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<IssuedSession> GuestLoginAsync(GuestLoginRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.GuestId, "D", out Guid guestId) || guestId == Guid.Empty
            || request.GuestId != guestId.ToString("D") || !GuestCredential.IsValid(request.GuestSecret))
            throw new ApiException("InvalidGuestCredential", 401);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "guest:" + request.GuestId))
            await gate.ExecuteNonQueryAsync(cancellationToken);

        Guid accountId = Guid.Empty;
        string? storedHash = null;
        bool banned = false;
        await using (var command = Command(connection, transaction, "SELECT \"Id\",\"GuestSecretHash\",\"IsBanned\" FROM \"Account\" WHERE \"GuestId\"=$1 FOR UPDATE", guestId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                accountId = reader.GetGuid(0);
                storedHash = reader.GetString(1);
                banned = reader.GetBoolean(2);
            }
        }
        if (accountId == Guid.Empty)
        {
            accountId = Guid.NewGuid();
            await using var create = Command(connection, transaction,
                "INSERT INTO \"Account\" (\"Id\",\"DisplayName\",\"GuestId\",\"GuestSecretHash\") VALUES ($1,$2,$3,$4)",
                accountId, "화가_" + accountId.ToString("N")[..8], guestId, GuestCredential.Hash(request.GuestSecret));
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            if (banned || !GuestCredential.Verify(storedHash!, request.GuestSecret)) throw new ApiException("InvalidGuestCredential", 401);
            await using var identity = Command(connection, transaction, "SELECT 1 FROM \"ExternalIdentity\" WHERE \"AccountId\"=$1 LIMIT 1", accountId);
            if (await identity.ExecuteScalarAsync(cancellationToken) != null) throw new ApiException("ExternalLoginRequired", 401);
        }
        // Google 연동도 같은 계정 행을 잠그므로 자격 확인과 세션 발급 사이에 인증 방식이 바뀌지 않는다.
        var session = await IssueSession(connection, transaction, accountId, "main", null, DateTimeOffset.UtcNow.AddHours(12), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }
}
