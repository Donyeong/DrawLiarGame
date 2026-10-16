using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private sealed record StoredAdTicket(Guid Id, string Ticket, Guid AccountId, string UserId, string SessionHash,
        string Platform, string Provider, DateOnly RewardDay, int RewardPaidGems, string State,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
    private const string AD_TICKET_COLUMNS = "\"Id\",\"Ticket\",\"AccountId\",\"UserId\",\"SessionHash\",\"Platform\",\"Provider\",\"RewardDay\",\"RewardPaidGems\",\"State\",\"CreatedAt\",\"ExpiresAt\"";

    public async Task<AdsStateResponse> AdvertisingStatusAsync(Guid accountId, string platform, AdvertisingSettings settings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await ReadAdStatus(connection, null, accountId, platform, settings, await AdNow(connection, null, cancellationToken), cancellationToken);
    }

    public async Task<AdsRewardResponse> BeginAdRewardAsync(Guid accountId, string sessionHash, Guid operationId,
        string platform, AdvertisingSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        var now = await AdNow(connection, transaction, cancellationToken);
        StoredAdTicket? ticket = null;
        await using (var previous = Command(connection, transaction,
            "SELECT " + AD_TICKET_COLUMNS + " FROM \"AdRewardTicket\" WHERE \"AccountId\"=$1 AND \"OperationId\"=$2", accountId, operationId))
        await using (var reader = await previous.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) ticket = ReadAdTicket(reader);
        if (ticket != null && ticket.Platform != platform) throw new ApiException("OperationConflict", 409);
        if (ticket == null)
        {
            await using (var claimed = Command(connection, transaction,
                "SELECT " + string.Join(',', AD_TICKET_COLUMNS.Split(',').Select(column => "\"t\"." + column))
                + " FROM \"AdRewardDailyClaim\" \"c\" JOIN \"AdRewardTicket\" \"t\" ON \"t\".\"Id\"=\"c\".\"TicketId\" WHERE \"c\".\"AccountId\"=$1 AND \"c\".\"RewardDay\"=$2",
                accountId, DateOnly.FromDateTime(now.UtcDateTime)))
            await using (var reader = await claimed.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken)) ticket = ReadAdTicket(reader);
        }
        if (ticket is { State: AdsRules.PENDING } && ticket.ExpiresAt > now
            && ticket.RewardDay == DateOnly.FromDateTime(now.UtcDateTime)
            && settings.PlatformEnabled(platform) && settings.DailyRewardEnabled)
        {
            var profile = await ReadProfile(connection, transaction, accountId);
            if (CommerceRules.IsPainterSubscriber(profile, now))
                ticket = await GrantAdReward(connection, transaction, ticket, now, cancellationToken);
        }
        if (ticket == null)
        {
            var profile = await ReadProfile(connection, transaction, accountId);
            bool subscriber = CommerceRules.IsPainterSubscriber(profile, now);
            if (!settings.PlatformEnabled(platform) || !settings.DailyRewardEnabled
                || !subscriber && !settings.RewardProviderEnabled(platform)) throw new ApiException("AdsUnavailable", 503);
            if (!subscriber && platform == AdsRules.WEB_GL && now.AddSeconds(settings.WebMinimumSeconds) >= AdsRules.NextReset(now))
                throw new ApiException("AdRewardExpired", 409);
            await using (var count = Command(connection, transaction,
                "SELECT count(*) FROM \"AdRewardTicket\" WHERE \"AccountId\"=$1 AND \"RewardDay\"=$2", accountId, DateOnly.FromDateTime(now.UtcDateTime)))
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) >= 32) throw new ApiException("AdRewardLimit", 429);
            ticket = new StoredAdTicket(Guid.NewGuid(), ServerRuntime.NewToken(), accountId, ServerRuntime.NewToken(), sessionHash,
                platform, subscriber ? "Subscription" : platform == AdsRules.ANDROID ? "AdMob" : "GoogleH5",
                DateOnly.FromDateTime(now.UtcDateTime), settings.RewardPaidGems, AdsRules.PENDING, now,
                now.AddSeconds(AdsRules.TICKET_LIFETIME_SECONDS) < AdsRules.NextReset(now)
                    ? now.AddSeconds(AdsRules.TICKET_LIFETIME_SECONDS) : AdsRules.NextReset(now));
            await using (var insert = Command(connection, transaction, """
                INSERT INTO "AdRewardTicket" ("Id","Ticket","AccountId","OperationId","UserId","SessionHash","Platform","Provider","RewardDay","RewardPaidGems","CreatedAt","ExpiresAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """, ticket.Id, ticket.Ticket, accountId, operationId, ticket.UserId, sessionHash, platform, ticket.Provider,
                ticket.RewardDay, ticket.RewardPaidGems, now, ticket.ExpiresAt))
                await insert.ExecuteNonQueryAsync(cancellationToken);
            if (subscriber) ticket = await GrantAdReward(connection, transaction, ticket, now, cancellationToken);
        }
        var response = await ReadAdResponse(connection, transaction, ticket, platform, settings, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<AdsRewardResponse> AdRewardAsync(Guid accountId, string platform, string token, AdvertisingSettings settings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var ticket = await FindAdTicket(connection, null, token, cancellationToken);
        if (ticket.AccountId != accountId || ticket.Platform != platform) throw new ApiException("AdRewardNotFound", 404);
        return await ReadAdResponse(connection, null, ticket, platform, settings,
            await AdNow(connection, null, cancellationToken), cancellationToken);
    }

    public async Task<AdsRewardResponse> CompleteWebAdRewardAsync(Guid accountId, string sessionHash, string token,
        AdvertisingSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.RewardProviderEnabled(AdsRules.WEB_GL)) throw new ApiException("AdsUnavailable", 503);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, accountId, cancellationToken);
        var ticket = await FindAdTicket(connection, transaction, token, cancellationToken);
        if (ticket.AccountId != accountId || ticket.Platform != AdsRules.WEB_GL || ticket.Provider != "GoogleH5"
            || !ServerRuntime.FixedEquals(ticket.SessionHash, sessionHash)) throw new ApiException("InvalidAdReward", 403);
        var now = await AdNow(connection, transaction, cancellationToken);
        if (ticket.State != AdsRules.COMPLETED)
        {
            ValidateAdLifetime(ticket, now);
            if (now < ticket.CreatedAt.AddSeconds(settings.WebMinimumSeconds)) throw new ApiException("AdRewardNotReady", 409);
            ticket = await GrantAdReward(connection, transaction, ticket, now, cancellationToken);
        }
        var response = await ReadAdResponse(connection, transaction, ticket, AdsRules.WEB_GL, settings, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task CompleteVerifiedAdRewardAsync(VerifiedAdReward verified, AdvertisingSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.RewardProviderEnabled(AdsRules.ANDROID)) throw new ApiException("AdsUnavailable", 503);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var initial = await FindAdTicket(connection, null, verified.Ticket, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCommerceAccount(connection, transaction, initial.AccountId, cancellationToken);
        var ticket = await FindAdTicket(connection, transaction, verified.Ticket, cancellationToken);
        if (ticket.Provider != "AdMob" || ticket.Platform != AdsRules.ANDROID
            || !ServerRuntime.FixedEquals(ticket.UserId, verified.UserId)) throw new ApiException("InvalidAdVerification", 403);
        string hash = ServerRuntime.Hash(verified.TransactionId);
        await using (var claim = Command(connection, transaction, """
            INSERT INTO "AdRewardTransaction" ("Provider","TransactionHash","TicketId","CreatedAt") VALUES ('AdMob',$1,$2,clock_timestamp())
            ON CONFLICT DO NOTHING
            """, hash, ticket.Id))
            await claim.ExecuteNonQueryAsync(cancellationToken);
        await using (var owner = Command(connection, transaction,
            "SELECT \"TicketId\" FROM \"AdRewardTransaction\" WHERE \"Provider\"='AdMob' AND \"TransactionHash\"=$1", hash))
            if (await owner.ExecuteScalarAsync(cancellationToken) is not Guid id || id != ticket.Id)
                throw new ApiException("AdTransactionAlreadyClaimed", 409);
        if (ticket.State != AdsRules.COMPLETED)
        {
            var now = await AdNow(connection, transaction, cancellationToken);
            ValidateAdLifetime(ticket, now);
            if (verified.RewardedAt < ticket.CreatedAt.AddSeconds(-2) || verified.RewardedAt >= ticket.ExpiresAt
                || verified.RewardedAt > now.AddMinutes(2)) throw new ApiException("InvalidAdVerification", 403);
            await GrantAdReward(connection, transaction, ticket, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateAdLifetime(StoredAdTicket ticket, DateTimeOffset now)
    {
        if (now >= ticket.ExpiresAt || ticket.RewardDay != DateOnly.FromDateTime(now.UtcDateTime))
            throw new ApiException("AdRewardExpired", 410);
    }

    private static async Task<StoredAdTicket> GrantAdReward(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StoredAdTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        int claimed;
        await using (var claim = Command(connection, transaction, """
            INSERT INTO "AdRewardDailyClaim" ("AccountId","RewardDay","TicketId","RewardPaidGems","CreatedAt")
            VALUES ($1,$2,$3,$4,$5) ON CONFLICT ("AccountId","RewardDay") DO NOTHING
            """, ticket.AccountId, ticket.RewardDay, ticket.Id, ticket.RewardPaidGems, now))
            claimed = await claim.ExecuteNonQueryAsync(cancellationToken);
        if (claimed == 1)
        {
            int balance;
            await using (var credit = Command(connection, transaction, """
                UPDATE "Account" SET "PaidGems"="PaidGems"+$2 WHERE "Id"=$1 AND "PaidGems"<=$3 RETURNING "PaidGems"
                """, ticket.AccountId, ticket.RewardPaidGems, int.MaxValue - ticket.RewardPaidGems))
                balance = await credit.ExecuteScalarAsync(cancellationToken) is int value ? value : throw new ApiException("PaidGemLimit", 409);
            await using (var ledger = Command(connection, transaction, """
                INSERT INTO "CommerceLedger" ("AccountId","OperationId","ProductId","Kind","PaidGemsDelta","BalanceAfter")
                VALUES ($1,$2,$3,'AdvertisingReward',$4,$5)
                """, ticket.AccountId, ticket.Id, AdsRules.PRODUCT_ID, ticket.RewardPaidGems, balance))
                await ledger.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var finish = Command(connection, transaction,
            "UPDATE \"AdRewardTicket\" SET \"State\"='Completed',\"CompletedAt\"=$2 WHERE \"Id\"=$1", ticket.Id, now))
            await finish.ExecuteNonQueryAsync(cancellationToken);
        return ticket with { State = AdsRules.COMPLETED };
    }

    private static async Task<AdsStateResponse> ReadAdStatus(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid accountId, string platform, AdvertisingSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var account = Command(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\"", accountId))
            if (await account.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);
        var profile = await ReadProfile(connection, transaction, accountId);
        bool subscriber = CommerceRules.IsPainterSubscriber(profile, now), claimed;
        await using (var claim = Command(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM \"AdRewardDailyClaim\" WHERE \"AccountId\"=$1 AND \"RewardDay\"=$2)", accountId, DateOnly.FromDateTime(now.UtcDateTime)))
            claimed = (bool)(await claim.ExecuteScalarAsync(cancellationToken))!;
        bool enabled = settings.PlatformEnabled(platform);
        bool rewarded = enabled && settings.DailyRewardEnabled && (subscriber || settings.RewardProviderEnabled(platform));
        return new AdsStateResponse
        {
            Platform = platform, BannerEnabled = enabled && !subscriber, RewardedEnabled = rewarded, AdsRemoved = subscriber,
            CanClaimDailyReward = rewarded && !claimed && (subscriber || platform != AdsRules.WEB_GL
                || now.AddSeconds(settings.WebMinimumSeconds) < AdsRules.NextReset(now)),
            ClaimedToday = claimed, RewardPaidGems = settings.RewardPaidGems,
            NextResetAt = ServerRuntime.Timestamp(AdsRules.NextReset(now)), ServerTimeUnixSeconds = now.ToUnixTimeSeconds(),
            BannerAdUnitId = enabled && platform == AdsRules.ANDROID && !subscriber ? settings.AndroidBannerAdUnitId : "",
            RewardedAdUnitId = rewarded && platform == AdsRules.ANDROID && !subscriber ? settings.AndroidRewardedAdUnitId : "",
            WebPublisherId = enabled && platform == AdsRules.WEB_GL && !subscriber ? settings.WebPublisherId : "",
            WebBannerSlotId = enabled && platform == AdsRules.WEB_GL && !subscriber ? settings.WebBannerSlotId : ""
        };
    }

    private static async Task<AdsRewardResponse> ReadAdResponse(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        StoredAdTicket ticket, string platform, AdvertisingSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string state = ticket.State == AdsRules.COMPLETED ? AdsRules.COMPLETED : now >= ticket.ExpiresAt
            || ticket.RewardDay != DateOnly.FromDateTime(now.UtcDateTime) ? AdsRules.EXPIRED : AdsRules.PENDING;
        return new AdsRewardResponse
        {
            Ticket = new AdsRewardTicket { Ticket = ticket.Ticket, UserId = ticket.UserId,
                ExpiresAt = ServerRuntime.Timestamp(ticket.ExpiresAt), State = state }, State = state,
            Profile = await ReadProfile(connection, transaction, ticket.AccountId),
            Status = await ReadAdStatus(connection, transaction, ticket.AccountId, platform, settings, now, cancellationToken)
        };
    }

    private static async Task<StoredAdTicket> FindAdTicket(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string token, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT " + AD_TICKET_COLUMNS + " FROM \"AdRewardTicket\" WHERE \"Ticket\"=$1", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAdTicket(reader) : throw new ApiException("AdRewardNotFound", 404);
    }

    private static StoredAdTicket ReadAdTicket(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateOnly>(7),
        reader.GetInt32(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11));

    private static async Task<DateTimeOffset> AdNow(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT clock_timestamp()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetFieldValue<DateTimeOffset>(0);
    }
}
