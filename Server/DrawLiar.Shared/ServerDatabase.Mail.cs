using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private const string AVAILABLE_MAIL_CONDITION = """
        ("m"."AccountId"=$1 OR "m"."AccountId" IS NULL) AND "m"."ExpiresAt">clock_timestamp()
        AND NOT EXISTS (SELECT 1 FROM "MailClaim" "c" WHERE "c"."AccountId"=$1 AND "c"."MailId"="m"."Id")
        """;

    public async Task<MailListResponse> MailsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var mails = new List<MailData>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT "m"."Id","m"."Title","m"."Message","m"."CreatedAt","m"."ExpiresAt","m"."Coins","m"."PaidGems"
            FROM "Mail" "m" WHERE
            """ + AVAILABLE_MAIL_CONDITION + " ORDER BY \"m\".\"CreatedAt\" DESC,\"m\".\"Id\" DESC LIMIT 100", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            mails.Add(new MailData
            {
                MailId = reader.GetGuid(0).ToString(), Title = reader.GetString(1), Message = reader.GetString(2),
                CreatedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(3)),
                ExpiresAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(4)),
                Coins = reader.GetInt32(5), PaidGems = reader.GetInt32(6)
            });
        return new MailListResponse { Mails = mails.ToArray() };
    }

    public async Task<MailClaimResponse> ClaimMailAsync(Guid accountId, MailClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.MailIds == null || request.MailIds.Length is < 1 or > MailClaimRequest.MAX_MAILS)
            throw new ApiException("InvalidMail");
        var mailIds = new HashSet<Guid>();
        foreach (string value in request.MailIds)
        {
            if (!Guid.TryParse(value, out Guid id)) throw new ApiException("InvalidMail");
            mailIds.Add(id);
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        var claimed = new List<(Guid Id, int Coins, int PaidGems)>();
        await using (var claim = Command(connection, transaction, """
            WITH "eligible" AS MATERIALIZED (
                SELECT "m"."Id","m"."Coins","m"."PaidGems" FROM "Mail" "m"
                WHERE "m"."Id"=ANY($2) AND ("m"."AccountId"=$1 OR "m"."AccountId" IS NULL)
                    AND "m"."ExpiresAt">clock_timestamp()
                ORDER BY "m"."Id" FOR SHARE OF "m"
            ), "claimed" AS (
                INSERT INTO "MailClaim" ("AccountId","MailId") SELECT $1,"Id" FROM "eligible"
                ON CONFLICT ("AccountId","MailId") DO NOTHING RETURNING "MailId"
            )
            SELECT "e"."Id","e"."Coins","e"."PaidGems" FROM "eligible" "e"
            JOIN "claimed" "c" ON "c"."MailId"="e"."Id" ORDER BY "e"."Id"
            """, accountId, mailIds.ToArray()))
        await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                claimed.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2)));
        long totalCoins = claimed.Sum(mail => (long)mail.Coins);
        long totalPaidGems = claimed.Sum(mail => (long)mail.PaidGems);
        if (totalCoins > int.MaxValue) throw new ApiException("CoinLimit", 409);
        if (totalPaidGems > int.MaxValue) throw new ApiException("PaidGemLimit", 409);
        int coins = (int)totalCoins, paidGems = (int)totalPaidGems;
        if (claimed.Count > 0)
        {
            await using (var credit = Command(connection, transaction, """
                UPDATE "Account" SET "Coins"="Coins"+$2 WHERE "Id"=$1 AND "Coins"<=$3
                """, accountId, coins, int.MaxValue - coins))
                if (await credit.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("CoinLimit", 409);
            int balance;
            await using (var credit = Command(connection, transaction, """
                UPDATE "Account" SET "PaidGems"="PaidGems"+$2 WHERE "Id"=$1 AND "PaidGems"<=$3 RETURNING "PaidGems"
                """, accountId, paidGems, int.MaxValue - paidGems))
                balance = await credit.ExecuteScalarAsync(cancellationToken) is int value ? value : throw new ApiException("PaidGemLimit", 409);
            balance -= paidGems;
            foreach (var mail in claimed)
            {
                if (mail.PaidGems == 0) continue;
                balance += mail.PaidGems;
                await using var ledger = Command(connection, transaction, """
                    INSERT INTO "CommerceLedger" ("AccountId","OperationId","ProductId","Kind","PaidGemsDelta","BalanceAfter")
                    VALUES ($1,$2,'Mail','MailReward',$3,$4)
                    """, accountId, mail.Id, mail.PaidGems, balance);
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        var response = new MailClaimResponse
        {
            ClaimedCount = claimed.Count, Coins = coins, PaidGems = paidGems,
            Profile = await ReadProfile(connection, transaction, accountId)
        };
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<AdminMailSendResponse> AdminSendMailAsync(AdminMailRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ApiException("InvalidMail");
        string title = (request.Title ?? "").Trim();
        string message = new((request.Message ?? "").Where(value => !char.IsControl(value) || value is '\r' or '\n').ToArray());
        if (title.Length is < 1 or > AdminMailRequest.MAX_TITLE_LENGTH || title.Any(char.IsControl)
            || message.Length > AdminMailRequest.MAX_MESSAGE_LENGTH
            || request.Coins is < 0 or > AdminMailRequest.MAX_COINS
            || request.PaidGems is < 0 or > AdminMailRequest.MAX_PAID_GEMS
            || request.Coins + request.PaidGems <= 0
            || request.ExpiresInDays is < 1 or > AdminMailRequest.MAX_EXPIRES_IN_DAYS)
            throw new ApiException("InvalidMail");
        var recipients = new HashSet<Guid>();
        if (request.SendToAll)
        {
            if (request.AccountIds is { Length: > 0 }) throw new ApiException("InvalidMail");
        }
        else
        {
            if (request.AccountIds == null) throw new ApiException("InvalidMail");
            foreach (string value in request.AccountIds)
            {
                if (!Guid.TryParse(value, out Guid id)) throw new ApiException("InvalidMail");
                recipients.Add(id);
                if (recipients.Count > AdminMailRequest.MAX_RECIPIENTS) throw new ApiException("InvalidMail");
            }
            if (recipients.Count == 0) throw new ApiException("InvalidMail");
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (request.SendToAll)
        {
            await using var insert = Command(connection, transaction, """
                INSERT INTO "Mail" ("Id","Title","Message","Coins","PaidGems","ExpiresAt")
                VALUES ($1,$2,$3,$4,$5,now()+$6*interval '1 day')
                """, Guid.NewGuid(), title, message, request.Coins, request.PaidGems, request.ExpiresInDays);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            Guid[] accountIds = recipients.Order().ToArray();
            int found = 0;
            await using (var accounts = Command(connection, transaction, """
                SELECT "Id" FROM "Account" WHERE "Id"=ANY($1) ORDER BY "Id" FOR KEY SHARE
                """, (object)accountIds))
            await using (var reader = await accounts.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) found++;
            if (found != accountIds.Length) throw new ApiException("AccountNotFound", 404);
            await using var insert = Command(connection, transaction, """
                INSERT INTO "Mail" ("Id","AccountId","Title","Message","Coins","PaidGems","ExpiresAt")
                SELECT "i"."Id","i"."AccountId",$3,$4,$5,$6,now()+$7*interval '1 day'
                FROM unnest($1::uuid[],$2::uuid[]) AS "i"("Id","AccountId")
                """, accountIds.Select(_ => Guid.NewGuid()).ToArray(), accountIds, title, message,
                request.Coins, request.PaidGems, request.ExpiresInDays);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AdminMailSendResponse { Created = request.SendToAll ? 1 : recipients.Count };
    }

    public async Task<AdminMailData[]> AdminMailsAsync(CancellationToken cancellationToken = default)
    {
        var mails = new List<AdminMailData>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT "m"."Id","m"."AccountId",COALESCE("a"."DisplayName",''),"m"."Title","m"."Message",
                "m"."CreatedAt","m"."ExpiresAt","m"."Coins","m"."PaidGems",
                (SELECT count(*) FROM "MailClaim" "c" WHERE "c"."MailId"="m"."Id")
            FROM "Mail" "m" LEFT JOIN "Account" "a" ON "a"."Id"="m"."AccountId"
            ORDER BY "m"."CreatedAt" DESC,"m"."Id" DESC LIMIT 200
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            mails.Add(new AdminMailData
            {
                MailId = reader.GetGuid(0).ToString(), AccountId = reader.IsDBNull(1) ? "" : reader.GetGuid(1).ToString(),
                AccountName = reader.GetString(2), Title = reader.GetString(3), Message = reader.GetString(4),
                CreatedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(5)),
                ExpiresAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(6)),
                Coins = reader.GetInt32(7), PaidGems = reader.GetInt32(8), ClaimCount = (int)Math.Min(reader.GetInt64(9), int.MaxValue)
            });
        return mails.ToArray();
    }

    public async Task AdminCancelMailAsync(Guid mailId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "UPDATE \"Mail\" SET \"ExpiresAt\"=LEAST(\"ExpiresAt\",now()) WHERE \"Id\"=$1", mailId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("MailUnavailable", 404);
    }

    private static async Task<int> ReadMailCount(NpgsqlConnection connection, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, "SELECT count(*) FROM \"Mail\" \"m\" WHERE " + AVAILABLE_MAIL_CONDITION, accountId);
        return (int)Math.Min(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)), int.MaxValue);
    }
}
