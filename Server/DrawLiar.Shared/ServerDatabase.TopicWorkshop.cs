using System.Data;
using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private const int WORKSHOP_PAGE_SIZE = 20;
    private const int MAX_WORKSHOP_PAGE_SIZE = 50;
    private static readonly Lazy<TopicWorkshopPolicy> _workshopPolicy = new(() =>
    {
        using var stream = typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.TopicWorkshopPolicy.json")
            ?? throw new InvalidDataException("주제 창작마당 정책이 없습니다.");
        var policy = JsonSerializer.Deserialize<TopicWorkshopPolicy>(stream, ServerRuntime.Json);
        if (policy == null || !TopicWorkshopRules.ValidatePolicy(policy))
            throw new InvalidDataException("주제 창작마당 정책이 올바르지 않습니다.");
        return policy;
    });

    public static TopicWorkshopPolicy WorkshopPolicy => JsonSerializer.Deserialize<TopicWorkshopPolicy>(
        JsonSerializer.Serialize(_workshopPolicy.Value, ServerRuntime.Json), ServerRuntime.Json)!;

    public async Task<TopicWorkshopListResponse> ListWorkshopTopicsAsync(Guid accountId, string? languageCode = null,
        bool mine = false, int offset = 0, int limit = WORKSHOP_PAGE_SIZE, CancellationToken cancellationToken = default)
    {
        string language = WorkshopLanguage(languageCode);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MAX_WORKSHOP_PAGE_SIZE);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var response = new TopicWorkshopListResponse { Offset = offset, Limit = limit, Policy = WorkshopPolicy };
        await using (var count = Command(connection, transaction, "SELECT count(*) FROM \"TopicWorkshop\" WHERE \"CreatorAccountId\"=$1", accountId))
            response.OwnCount = ProfileCount(Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)));
        const string filter = """
            FROM "TopicWorkshop" t JOIN "Account" a ON a."Id"=t."CreatorAccountId"
            WHERE NOT a."IsBanned" AND ($2::text='' OR t."LanguageCode"=$2) AND (NOT $3::boolean OR t."CreatorAccountId"=$1)
            """;
        await using (var count = Command(connection, transaction, "SELECT count(*) " + filter, accountId, language, mine))
            response.Total = ProfileCount(Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)));
        var entries = new List<TopicWorkshopEntry>();
        await using (var command = Command(connection, transaction, """
            SELECT t."Id",t."CreatorAccountId",a."DisplayName",t."Name",t."LanguageCode",jsonb_array_length(t."Words"),t."DownloadCount",t."CreatedAt"
            """ + " " + filter + " ORDER BY t.\"CreatedAt\" DESC,t.\"Id\" DESC OFFSET $4 LIMIT $5", accountId, language, mine, offset, limit))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) entries.Add(ReadWorkshopEntry(reader, accountId));
        response.Items = entries.ToArray();
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<TopicWorkshopDetailResponse> PublishWorkshopTopicAsync(Guid accountId, TopicWorkshopPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TopicWorkshopRules.TryNormalize(request, _workshopPolicy.Value, out var normalized, out string code))
            throw new ApiException(code);
        if (_builtInTopicNames.Value.Contains(normalized.Name)) throw new ApiException("TopicWorkshopNameConflict", 409);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string creator;
        await using (var command = Command(connection, transaction,
            "SELECT \"DisplayName\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", accountId))
            creator = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new ApiException("AccountUnavailable", 403);
        await using (var duplicate = Command(connection, transaction,
            "SELECT 1 FROM \"TopicWorkshop\" WHERE \"CreatorAccountId\"=$1 AND \"LanguageCode\"=$2 AND \"Name\"=$3", accountId, normalized.LanguageCode, normalized.Name))
            if (await duplicate.ExecuteScalarAsync(cancellationToken) != null) throw new ApiException("TopicWorkshopNameConflict", 409);
        await using (var count = Command(connection, transaction, "SELECT count(*) FROM \"TopicWorkshop\" WHERE \"CreatorAccountId\"=$1", accountId))
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) >= _workshopPolicy.Value.Limits.MaxUploadsPerAccount)
                throw new ApiException("TopicWorkshopUploadLimit", 409);
        Guid id = Guid.NewGuid();
        DateTimeOffset createdAt;
        await using (var command = Command(connection, transaction, """
            INSERT INTO "TopicWorkshop" ("Id","CreatorAccountId","Name","LanguageCode","Words")
            VALUES ($1,$2,$3,$4,$5::jsonb) ON CONFLICT DO NOTHING RETURNING "CreatedAt"
            """, id, accountId, normalized.Name, normalized.LanguageCode, JsonSerializer.Serialize(normalized.Words, ServerRuntime.Json)))
            createdAt = await command.ExecuteScalarAsync(cancellationToken) is DateTime timestamp
                ? new DateTimeOffset(timestamp) : throw new ApiException("TopicWorkshopNameConflict", 409);
        await transaction.CommitAsync(cancellationToken);
        return new TopicWorkshopDetailResponse
        {
            Topic = new TopicWorkshopEntry
            {
                Id = id.ToString(), CreatorAccountId = accountId.ToString(), CreatorName = creator,
                Name = normalized.Name, LanguageCode = normalized.LanguageCode, WordCount = normalized.Words.Length,
                CreatedAt = ServerRuntime.Timestamp(createdAt), IsMine = true
            },
            Words = normalized.Words
        };
    }

    public async Task<TopicWorkshopDetailResponse> PreviewWorkshopTopicAsync(Guid accountId, string topicId,
        CancellationToken cancellationToken = default)
    {
        Guid id = WorkshopTopicId(topicId);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT t."Id",t."CreatorAccountId",a."DisplayName",t."Name",t."LanguageCode",jsonb_array_length(t."Words"),t."DownloadCount",t."CreatedAt",t."Words"::text
            FROM "TopicWorkshop" t JOIN "Account" a ON a."Id"=t."CreatorAccountId"
            WHERE t."Id"=$1 AND NOT a."IsBanned"
            """, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("TopicWorkshopUnavailable", 404);
        return new TopicWorkshopDetailResponse
        {
            Topic = ReadWorkshopEntry(reader, accountId),
            Words = JsonSerializer.Deserialize<string[]>(reader.GetString(8), ServerRuntime.Json)!
        };
    }

    public async Task<TopicWorkshopDetailResponse> DownloadWorkshopTopicAsync(Guid accountId, string topicId,
        CancellationToken cancellationToken = default)
    {
        Guid id = WorkshopTopicId(topicId);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            UPDATE "TopicWorkshop" t SET "DownloadCount"=CASE WHEN t."DownloadCount"<2147483647 THEN t."DownloadCount"+1 ELSE t."DownloadCount" END
            FROM "Account" a WHERE t."Id"=$1 AND a."Id"=t."CreatorAccountId" AND NOT a."IsBanned"
            RETURNING t."Id",t."CreatorAccountId",a."DisplayName",t."Name",t."LanguageCode",jsonb_array_length(t."Words"),t."DownloadCount",t."CreatedAt",t."Words"::text
            """, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("TopicWorkshopUnavailable", 404);
        return new TopicWorkshopDetailResponse
        {
            Topic = ReadWorkshopEntry(reader, accountId),
            Words = JsonSerializer.Deserialize<string[]>(reader.GetString(8), ServerRuntime.Json)!
        };
    }

    public async Task DeleteWorkshopTopicAsync(Guid accountId, string topicId, CancellationToken cancellationToken = default)
    {
        Guid id = WorkshopTopicId(topicId);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var account = Command(connection, transaction,
            "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", accountId))
            if (await account.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);
        await using (var command = Command(connection, transaction,
            "DELETE FROM \"TopicWorkshop\" WHERE \"Id\"=$1 AND \"CreatorAccountId\"=$2", id, accountId))
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ApiException("TopicWorkshopUnavailable", 404);
        await transaction.CommitAsync(cancellationToken);
    }

    private static TopicWorkshopEntry ReadWorkshopEntry(NpgsqlDataReader reader, Guid accountId) => new()
    {
        Id = reader.GetGuid(0).ToString(), CreatorAccountId = reader.GetGuid(1).ToString(), CreatorName = reader.GetString(2),
        Name = reader.GetString(3), LanguageCode = reader.GetString(4), WordCount = reader.GetInt32(5),
        DownloadCount = ProfileCount(reader.GetInt64(6)), CreatedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(7)),
        IsMine = reader.GetGuid(1) == accountId
    };

    private static string WorkshopLanguage(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return "";
        return _workshopPolicy.Value.LanguageCodes.FirstOrDefault(code => string.Equals(code, value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ApiException("TopicWorkshopInvalidLanguage");
    }

    private static Guid WorkshopTopicId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new ApiException("TopicWorkshopUnavailable", 404);
}
