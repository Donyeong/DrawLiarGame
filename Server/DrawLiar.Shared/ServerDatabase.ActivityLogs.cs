using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Npgsql;

namespace DrawLiar.Server;

public sealed record AdminActivityLogData(long Id, string CreatedAt, string Kind, string AccountId, string AccountName,
    string TargetAccountId, string TargetName, string RoomId, string MatchId, string Detail);
public sealed record AdminActivityLogResponse(AdminActivityLogData[] Entries, long NextBefore);
public sealed record AdminRoomMatchData(string MatchId, string PlayedAt, int PlayerCount, int RoundCount, string DrawingExpiresAt,
    RoundDrawingSummary[] Rounds);
public sealed record AdminRoomLogResponse(string RoomId, AdminActivityLogData[] Entries, long NextBefore, AdminRoomMatchData[] Matches);
public sealed record RoomActivityData(string Kind, string RoomId, string AccountId, string MatchId = "", string Text = "",
    bool Spectator = false, bool Voluntary = false);
public sealed record RoomActivityBatchRequest(string NodeId, RoomActivityData[] Entries)
{
    public const int MAX_ENTRIES = 64;
}

// 유저·방 로그는 한 테이블에 한 번만 남긴다. 라운드 그림은 다시보기의 MatchRoundDrawing을 경기 ID로 참조한다.
public sealed partial class ServerDatabase
{
    private const int DEFAULT_ACTIVITY_LOG_RETENTION_DAYS = 30;
    private const int ACTIVITY_LOG_PAGE_SIZE = 200;
    private const int ACTIVITY_LOG_CLEANUP_BATCH_SIZE = 4096;
    private const int ADMIN_ROOM_MATCH_LIMIT = 50;
    private const int ROOM_ACTIVITY_TEXT_LENGTH = 200;
    private static readonly string[] ROOM_ACTIVITY_KINDS = { "RoomEnter", "RoomLeave", "RoomChat", "RoomGuess", "MatchStart" };
    private const string ACTIVITY_LOG_SELECT = """
        SELECT l."Id",l."CreatedAt",l."Kind",l."AccountId",a."DisplayName",l."TargetAccountId",t."DisplayName",l."RoomId",l."MatchId",l."Detail"::text
        FROM "ActivityLog" l LEFT JOIN "Account" a ON a."Id"=l."AccountId" LEFT JOIN "Account" t ON t."Id"=l."TargetAccountId"
        """;
    private static readonly JsonSerializerOptions _activityJson = new(ServerRuntime.Json) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private readonly int _activityLogRetentionDays;

    private static async Task LogActivity(NpgsqlConnection connection, NpgsqlTransaction? transaction, string kind, Guid? accountId,
        object? detail = null, Guid? targetAccountId = null, Guid? roomId = null, Guid? matchId = null, CancellationToken cancellationToken = default)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO "ActivityLog" ("Kind","AccountId","TargetAccountId","RoomId","MatchId","Detail") VALUES ($1,$2,$3,$4,$5,$6::json)
            """, kind, accountId, targetAccountId, roomId, matchId, JsonSerializer.Serialize(detail ?? new { }, _activityJson));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task LogActivityAsync(string kind, Guid accountId, object? detail = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await LogActivity(connection, null, kind, accountId, detail, cancellationToken: cancellationToken);
    }

    public async Task RecordRoomActivityAsync(RoomActivityBatchRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Entries == null || request.Entries.Length is < 1 or > RoomActivityBatchRequest.MAX_ENTRIES || string.IsNullOrEmpty(request.NodeId))
            throw new ApiException("InvalidActivity");
        var entries = request.Entries.Select(entry =>
        {
            Guid match = Guid.Empty;
            if (entry == null || Array.IndexOf(ROOM_ACTIVITY_KINDS, entry.Kind) < 0
                || !Guid.TryParse(entry.RoomId, out Guid room) || room == Guid.Empty || !Guid.TryParse(entry.AccountId, out Guid account) || account == Guid.Empty
                || !string.IsNullOrEmpty(entry.MatchId) && !Guid.TryParse(entry.MatchId, out match)
                || entry.Text == null || entry.Text.Length > ROOM_ACTIVITY_TEXT_LENGTH || entry.Text.Any(char.IsControl)
                || (entry.Kind is "RoomChat" or "RoomGuess") != (entry.Text.Length > 0))
                throw new ApiException("InvalidActivity");
            return (Entry: entry, Room: room, Account: account, Match: match == Guid.Empty ? (Guid?)null : match);
        }).ToArray();
        Guid[] rooms = entries.Select(entry => entry.Room).Distinct().ToArray();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var authority = Command(connection, transaction,
            "SELECT count(*) FROM \"RoomGameAuthority\" WHERE \"RoomId\"=ANY($1) AND \"NodeId\"=$2", rooms, request.NodeId))
            if (Convert.ToInt64(await authority.ExecuteScalarAsync(cancellationToken)) != rooms.Length)
                throw new ApiException("InvalidActivityAuthority", 403);
        foreach (var (entry, room, account, match) in entries)
            await LogActivity(connection, transaction, entry.Kind, account, entry.Kind switch
            {
                "RoomEnter" => new { entry.Spectator },
                "RoomLeave" => new { entry.Voluntary },
                "MatchStart" => null,
                _ => (object)new { entry.Text }
            }, roomId: room, matchId: match, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminActivityLogResponse> AdminAccountLogsAsync(Guid accountId, long before, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var entries = await ReadActivityLogs(connection, null, ACTIVITY_LOG_SELECT + """
             WHERE l."Id" IN ((SELECT "Id" FROM "ActivityLog" WHERE "AccountId"=$1 AND "Id"<$2 ORDER BY "Id" DESC LIMIT $3)
                UNION ALL (SELECT "Id" FROM "ActivityLog" WHERE "TargetAccountId"=$1 AND "Id"<$2 ORDER BY "Id" DESC LIMIT $3))
            ORDER BY l."Id" DESC LIMIT $3
            """, accountId, before, cancellationToken);
        return new AdminActivityLogResponse(entries, NextActivityCursor(entries));
    }

    public async Task<AdminRoomLogResponse> AdminRoomLogsAsync(Guid roomId, long before, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var entries = await ReadActivityLogs(connection, transaction, ACTIVITY_LOG_SELECT + """
             WHERE l."RoomId"=$1 AND l."Id"<$2 ORDER BY l."Id" DESC LIMIT $3
            """, roomId, before, cancellationToken);
        var matches = new List<AdminRoomMatchData>();
        await using (var command = Command(connection, transaction, """
            SELECT "MatchId","PlayedAt","PlayerCount","RoundCount","DrawingExpiresAt" FROM "MatchRecord"
            WHERE "RoomId"=$1 ORDER BY "PlayedAt" DESC LIMIT $2
            """, roomId, ADMIN_ROOM_MATCH_LIMIT))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                matches.Add(new AdminRoomMatchData(reader.GetGuid(0).ToString(), ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(1)),
                    reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? "" : ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(4)), []));
        var rounds = new Dictionary<string, List<RoundDrawingSummary>>();
        await using (var command = Command(connection, transaction, """
            SELECT "MatchId","Round","Topic","Word","PlayerCount","StrokeCount" FROM "MatchRoundDrawing"
            WHERE "MatchId"=ANY($1) AND "ExpiresAt">now() ORDER BY "MatchId","Round"
            """, matches.Select(match => Guid.Parse(match.MatchId)).ToArray()))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                string matchId = reader.GetGuid(0).ToString();
                if (!rounds.TryGetValue(matchId, out var list)) rounds.Add(matchId, list = new List<RoundDrawingSummary>());
                list.Add(new RoundDrawingSummary
                {
                    Round = reader.GetInt32(1), Topic = reader.GetString(2), Word = reader.GetString(3),
                    PlayerCount = reader.GetInt32(4), StrokeCount = reader.GetInt32(5)
                });
            }
        await transaction.CommitAsync(cancellationToken);
        return new AdminRoomLogResponse(roomId.ToString(), entries, NextActivityCursor(entries),
            matches.Select(match => match with { Rounds = rounds.GetValueOrDefault(match.MatchId)?.ToArray() ?? [] }).ToArray());
    }

    private static async Task<AdminActivityLogData[]> ReadActivityLogs(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        Guid id, long before, CancellationToken cancellationToken)
    {
        var entries = new List<AdminActivityLogData>();
        await using var command = Command(connection, transaction, sql, id, before > 0 ? before : long.MaxValue, ACTIVITY_LOG_PAGE_SIZE);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new AdminActivityLogData(reader.GetInt64(0), ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(1)), reader.GetString(2),
                OptionalId(reader, 3), reader.IsDBNull(4) ? "" : reader.GetString(4), OptionalId(reader, 5), reader.IsDBNull(6) ? "" : reader.GetString(6),
                OptionalId(reader, 7), OptionalId(reader, 8), reader.GetString(9)));
        return entries.ToArray();
    }

    private static string OptionalId(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetGuid(ordinal).ToString();

    private static long NextActivityCursor(AdminActivityLogData[] entries) => entries.Length == ACTIVITY_LOG_PAGE_SIZE ? entries[^1].Id : 0;

    private static async Task CleanupActivityLogsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int retentionDays,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            DELETE FROM "ActivityLog" WHERE "Id" IN (
                SELECT "Id" FROM "ActivityLog" WHERE "CreatedAt"<now()-make_interval(days => $1) ORDER BY "CreatedAt" LIMIT $2)
            """, retentionDays, ACTIVITY_LOG_CLEANUP_BATCH_SIZE);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
