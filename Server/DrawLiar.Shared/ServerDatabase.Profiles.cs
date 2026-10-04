using System.Data;
using System.Text.Json;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<PublicProfileData> PublicProfileAsync(Guid requester, Guid target, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var profile = new PublicProfileData { AccountId = target.ToString() };
        await using (var command = Command(connection, transaction,
            "SELECT \"DisplayName\",\"AvatarColor\",\"Accessory\",\"CreatedAt\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\"", target))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("AccountNotFound", 404);
            profile.DisplayName = reader.GetString(0);
            profile.AvatarColor = reader.GetInt32(1);
            profile.Accessory = reader.GetInt32(2);
            profile.JoinedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(3));
        }
        if (requester == target) profile.Friendship = "Self";
        else
        {
            await using var relationship = Command(connection, transaction, """
                SELECT "Accepted","FromAccountId" FROM "Friendship"
                WHERE ("FromAccountId"=$1 AND "ToAccountId"=$2) OR ("FromAccountId"=$2 AND "ToAccountId"=$1)
                """, requester, target);
            await using var reader = await relationship.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                profile.Friendship = reader.GetBoolean(0) ? "Friends" : reader.GetGuid(1) == requester ? "Outgoing" : "Incoming";
        }
        await using (var command = Command(connection, transaction, """
            SELECT count(*),count(*) FILTER (WHERE "Won"),COALESCE(sum("Score"),0),COALESCE(max("Score"),0),
                COALESCE(sum("RoundsPlayed"),0),COALESCE(sum("CitizenRounds"),0),COALESCE(sum("LiarRounds"),0),
                COALESCE(sum("CorrectVotes"),0),COALESCE(sum("CorrectGuesses"),0)
            FROM "AccountMatch" WHERE "AccountId"=$1
            """, target))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            profile.Stats = new ProfileStatsData
            {
                MatchesPlayed = ProfileCount(reader.GetInt64(0)), MatchesWon = ProfileCount(reader.GetInt64(1)),
                TotalScore = ProfileCount(reader.GetInt64(2)), BestScore = reader.GetInt32(3),
                RoundsPlayed = ProfileCount(reader.GetInt64(4)), CitizenRounds = ProfileCount(reader.GetInt64(5)),
                LiarRounds = ProfileCount(reader.GetInt64(6)), CorrectVotes = ProfileCount(reader.GetInt64(7)),
                CorrectGuesses = ProfileCount(reader.GetInt64(8))
            };
        }
        var recent = new List<ProfileMatchData>();
        await using (var command = Command(connection, transaction, """
            SELECT m."MatchId",m."PlayedAt",a."Score",a."Rank",m."PlayerCount",m."Mode",a."Won",m."RoundCount"
            FROM "AccountMatch" a JOIN "MatchRecord" m ON m."MatchId"=a."MatchId"
            WHERE a."AccountId"=$1 ORDER BY m."PlayedAt" DESC,m."MatchId" DESC LIMIT 10
            """, target))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                recent.Add(new ProfileMatchData
                {
                    MatchId = reader.GetGuid(0).ToString(), PlayedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(1)),
                    Score = reader.GetInt32(2), Rank = reader.GetInt32(3), PlayerCount = reader.GetInt32(4),
                    Mode = reader.GetInt32(5), Won = reader.GetBoolean(6), RoundCount = reader.GetInt32(7)
                });
        profile.RecentMatches = recent.ToArray();
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    private static int ProfileCount(long value) => (int)Math.Min(int.MaxValue, value);

    public async Task RecordMatchAsync(MatchResultRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = ValidateMatchResult(request);
        Guid matchId = Guid.Parse(normalized.MatchId), roomId = Guid.Parse(normalized.RoomId);
        DateTimeOffset playedAt = DateTimeOffset.Parse(normalized.PlayedAt);
        string payloadHash = ServerRuntime.Hash(JsonSerializer.Serialize(normalized, ServerRuntime.Json));
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "match:" + normalized.MatchId))
            await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var command = Command(connection, transaction,
            "SELECT \"PayloadHash\" FROM \"MatchRecord\" WHERE \"MatchId\"=$1", matchId))
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is string existing)
            {
                if (!ServerRuntime.FixedEquals(existing, payloadHash)) throw new ApiException("MatchResultConflict", 409);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }
        await using (var command = Command(connection, transaction, """
            SELECT r."RoomId" FROM "RoomGameAuthority" r JOIN "DedicatedNode" n ON n."NodeId"=r."NodeId"
            WHERE r."RoomId"=$1 AND r."NodeId"=$2
            """, roomId, normalized.NodeId))
            if (await command.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("InvalidMatchAuthority", 403);
        Guid[] accounts = normalized.Players.Select(player => Guid.Parse(player.AccountId)).ToArray();
        await using (var command = Command(connection, transaction,
            "SELECT count(*) FROM \"RoomGameAdmission\" WHERE \"RoomId\"=$1 AND \"AccountId\"=ANY($2)", roomId, accounts))
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != accounts.Length)
                throw new ApiException("InvalidMatchParticipant", 403);
        await using (var command = Command(connection, transaction, """
            INSERT INTO "MatchRecord" ("MatchId","RoomId","PlayedAt","PlayerCount","Mode","RoundCount","PayloadHash")
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            """, matchId, roomId, playedAt, accounts.Length, normalized.Mode, normalized.RoundCount, payloadHash))
            await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var player in normalized.Players)
        {
            await using var command = Command(connection, transaction, """
                INSERT INTO "AccountMatch" ("MatchId","AccountId","Score","Rank","Won","RoundsPlayed","CitizenRounds","LiarRounds","CorrectVotes","CorrectGuesses")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                """, matchId, Guid.Parse(player.AccountId), player.Score, player.Rank, player.Won,
                player.RoundsPlayed, player.CitizenRounds, player.LiarRounds, player.CorrectVotes, player.CorrectGuesses);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static MatchResultRequest ValidateMatchResult(MatchResultRequest request)
    {
        if (request == null || !Guid.TryParse(request.MatchId, out Guid matchId) || matchId == Guid.Empty
            || !Guid.TryParse(request.RoomId, out Guid roomId) || roomId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.NodeId) || request.NodeId.Length > 64
            || request.NodeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
            || !DateTimeOffset.TryParse(request.PlayedAt, out var playedAt) || playedAt < DateTimeOffset.UnixEpoch
            || playedAt > DateTimeOffset.UtcNow.AddMinutes(5) || request.Mode is < 0 or > 1 || request.RoundCount < 1
            || request.Players == null || request.Players.Length is < 3 or > 8)
            throw new ApiException("InvalidMatchResult");
        var accounts = new HashSet<Guid>();
        var players = new List<MatchPlayerResult>();
        foreach (var player in request.Players)
        {
            if (player == null || !Guid.TryParse(player.AccountId, out Guid account) || account == Guid.Empty || !accounts.Add(account)
                || player.Score < 0 || player.Rank < 1 || player.Rank > request.Players.Length
                || player.RoundsPlayed < 1 || player.RoundsPlayed > request.RoundCount || player.CitizenRounds < 0 || player.LiarRounds < 0
                || (long)player.CitizenRounds + player.LiarRounds != player.RoundsPlayed
                || player.CorrectVotes < 0 || player.CorrectVotes > player.CitizenRounds
                || player.CorrectGuesses < 0 || player.CorrectGuesses > player.LiarRounds)
                throw new ApiException("InvalidMatchResult");
            players.Add(new MatchPlayerResult
            {
                AccountId = account.ToString(), Score = player.Score, Rank = player.Rank, Won = player.Won,
                RoundsPlayed = player.RoundsPlayed, CitizenRounds = player.CitizenRounds, LiarRounds = player.LiarRounds,
                CorrectVotes = player.CorrectVotes, CorrectGuesses = player.CorrectGuesses
            });
        }
        if (players.Any(player => player.Rank != 1 + players.Count(other => other.Score > player.Score)))
            throw new ApiException("InvalidMatchResult");
        return new MatchResultRequest
        {
            NodeId = request.NodeId, RoomId = roomId.ToString(), MatchId = matchId.ToString(),
            PlayedAt = ServerRuntime.Timestamp(playedAt.ToUniversalTime()), Mode = request.Mode, RoundCount = request.RoundCount,
            Players = players.OrderBy(player => player.AccountId, StringComparer.Ordinal).ToArray()
        };
    }
}
