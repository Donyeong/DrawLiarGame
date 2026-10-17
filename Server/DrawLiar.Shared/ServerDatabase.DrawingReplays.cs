using System.Data;
using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private readonly int _drawingReplayRetentionDays;
    private sealed record PreparedDrawing(RoundDrawingSummary Summary, byte[] Payload, string Hash);

    public async Task<MatchDrawingListResponse> MatchDrawingsAsync(Guid accountId, string matchId, CancellationToken cancellationToken = default)
    {
        Guid id = ParseDrawingMatchId(matchId);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var metadata = await RequireDrawingAccessAsync(connection, transaction, accountId, id, cancellationToken);
        var rounds = new List<RoundDrawingSummary>();
        await using (var command = Command(connection, transaction, """
            SELECT "Round","Topic","Word","PlayerCount","StrokeCount" FROM "MatchRoundDrawing"
            WHERE "MatchId"=$1 AND "ExpiresAt">now() ORDER BY "Round"
            """, id))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                rounds.Add(new RoundDrawingSummary
                {
                    Round = reader.GetInt32(0), Topic = reader.GetString(1), Word = reader.GetString(2),
                    PlayerCount = reader.GetInt32(3), StrokeCount = reader.GetInt32(4)
                });
        if (rounds.Count == 0) throw new ApiException("DrawingReplayUnavailable", 404);
        await transaction.CommitAsync(cancellationToken);
        return new MatchDrawingListResponse
        { MatchId = id.ToString(), ExpiresAt = ServerRuntime.Timestamp(metadata.ExpiresAt), RetentionDays = metadata.RetentionDays,
            IsPartial = metadata.IsPartial, Rounds = rounds.ToArray() };
    }

    public Task<MatchDrawingResponse> MatchDrawingAsync(Guid accountId, string matchId, int round, CancellationToken cancellationToken = default) =>
        ReadMatchDrawingAsync(accountId, matchId, round, cancellationToken);

    // 운영툴은 참가 여부와 관계없이 같은 보관 그림을 조회한다.
    public Task<MatchDrawingResponse> AdminMatchDrawingAsync(string matchId, int round, CancellationToken cancellationToken = default) =>
        ReadMatchDrawingAsync(null, matchId, round, cancellationToken);

    private async Task<MatchDrawingResponse> ReadMatchDrawingAsync(Guid? accountId, string matchId, int round, CancellationToken cancellationToken)
    {
        Guid id = ParseDrawingMatchId(matchId);
        if (round is < 1 or > DrawingReplayRules.MAX_ROUNDS) throw new ApiException("InvalidDrawingRound");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var metadata = await RequireDrawingAccessAsync(connection, transaction, accountId, id, cancellationToken);
        byte[] payload;
        await using (var command = Command(connection, transaction, """
            SELECT "Payload" FROM "MatchRoundDrawing" WHERE "MatchId"=$1 AND "Round"=$2 AND "ExpiresAt">now()
            """, id, round))
            payload = await command.ExecuteScalarAsync(cancellationToken) as byte[] ?? throw new ApiException("DrawingReplayUnavailable", 404);
        RoundDrawingData drawing = DrawingArchiveCodec.Decode(new RoundDrawingArchiveData { Round = round, Payload = payload });
        if (!DrawingReplayRules.ValidRound(drawing)) throw new InvalidDataException("저장된 그림 데이터가 올바르지 않습니다.");
        if (drawing.Round != round) throw new InvalidDataException("그림 라운드가 일치하지 않습니다.");
        await transaction.CommitAsync(cancellationToken);
        return new MatchDrawingResponse { MatchId = id.ToString(), ExpiresAt = ServerRuntime.Timestamp(metadata.ExpiresAt), Drawing = drawing };
    }

    private static Guid ParseDrawingMatchId(string matchId) =>
        Guid.TryParse(matchId, out Guid id) && id != Guid.Empty ? id : throw new ApiException("InvalidMatch");

    private static async Task<(DateTimeOffset ExpiresAt, int RetentionDays, bool IsPartial)> RequireDrawingAccessAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid? accountId, Guid matchId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT m."DrawingExpiresAt",m."DrawingRetentionDays",m."DrawingExpiresAt">clock_timestamp(),m."DrawingsIncomplete"
            FROM "MatchRecord" m WHERE m."MatchId"=$1
                AND ($2::uuid IS NULL OR EXISTS (SELECT 1 FROM "AccountMatch" a WHERE a."MatchId"=m."MatchId" AND a."AccountId"=$2))
            """, matchId, accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw accountId == null ? new ApiException("DrawingReplayUnavailable", 404) : new ApiException("DrawingReplayForbidden", 403);
        if (reader.IsDBNull(0)) throw new ApiException("DrawingReplayUnavailable", 404);
        if (!reader.GetBoolean(2)) throw new ApiException("DrawingReplayExpired", 410);
        return (reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetBoolean(3));
    }

    private static PreparedDrawing[] PrepareDrawings(MatchResultRequest request)
    {
        if (request.DrawingArchives == null || request.DrawingArchives.Length > DrawingReplayRules.MAX_ROUNDS
            || request.DrawingArchives.Length > request.RoundCount
            || request.DrawingArchives.Length != 0 && !request.DrawingsIncomplete && request.DrawingArchives.Length != request.RoundCount)
            throw new ApiException("InvalidDrawingReplay");
        var prepared = new List<PreparedDrawing>();
        var roundIds = new HashSet<int>();
        var accounts = request.Players.Select(player => Guid.Parse(player.AccountId)).ToHashSet();
        long totalBytes = 0;
        foreach (var archive in request.DrawingArchives)
        {
            if (archive == null || archive.Round < 1 || archive.Round > request.RoundCount || !roundIds.Add(archive.Round)
                || archive.Payload == null || archive.Payload.Length == 0 || archive.Payload.Length > DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES
                || (totalBytes += archive.Payload.Length) > DrawingReplayRules.MAX_COMPRESSED_MATCH_BYTES)
                throw new ApiException("InvalidDrawingReplay");
        }
        foreach (var archive in request.DrawingArchives.OrderBy(archive => archive.Round))
        {
            RoundDrawingData drawing;
            try { drawing = DrawingArchiveCodec.Decode(archive); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            { throw new ApiException("InvalidDrawingReplay"); }
            if (!DrawingReplayRules.ValidRound(drawing) || drawing.Round != archive.Round
                || drawing.Players.Any(player => !accounts.Contains(Guid.Parse(player.AccountId)))) throw new ApiException("InvalidDrawingReplay");
            var normalized = new RoundDrawingData
            {
                Round = drawing.Round, Topic = drawing.Topic, Word = drawing.Word,
                Players = drawing.Players.OrderBy(player => player.PlayerId).Select(player => new ReplayPlayerData
                { PlayerId = player.PlayerId, AccountId = Guid.Parse(player.AccountId).ToString(), DisplayName = player.DisplayName }).ToArray(),
                Strokes = drawing.Strokes
            };
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ServerRuntime.Json)));
            prepared.Add(new PreparedDrawing(new RoundDrawingSummary
            {
                Round = normalized.Round, Topic = normalized.Topic, Word = normalized.Word,
                PlayerCount = normalized.Players.Length, StrokeCount = normalized.Strokes.Length
            }, archive.Payload, hash));
        }
        return prepared.ToArray();
    }

    private static string? DrawingPayloadHash(PreparedDrawing[] drawings, bool incomplete) => drawings.Length == 0 && !incomplete ? null
        : ServerRuntime.Hash(incomplete + "\n" + string.Join("\n", drawings.Select(drawing => drawing.Summary.Round + ":" + drawing.Hash)));

    private static async Task StoreDrawingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid matchId,
        PreparedDrawing[] drawings, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        if (expiresAt == null || expiresAt <= DateTimeOffset.UtcNow) return;
        foreach (var prepared in drawings)
        {
            var drawing = prepared.Summary;
            await using var command = Command(connection, transaction, """
                INSERT INTO "MatchRoundDrawing" ("MatchId","Round","Topic","Word","PlayerCount","StrokeCount","Payload","ExpiresAt")
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
                """, matchId, drawing.Round, drawing.Topic, drawing.Word, drawing.PlayerCount, drawing.StrokeCount, prepared.Payload, expiresAt.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CleanupDrawingReplaysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            WITH "expired" AS (
                SELECT "MatchId","Round" FROM "MatchRoundDrawing" WHERE "ExpiresAt"<=now()
                ORDER BY "ExpiresAt","MatchId","Round" LIMIT $1 FOR UPDATE SKIP LOCKED
            ) DELETE FROM "MatchRoundDrawing" d USING "expired" e WHERE d."MatchId"=e."MatchId" AND d."Round"=e."Round"
            """, EXPIRY_CLEANUP_BATCH_SIZE);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
