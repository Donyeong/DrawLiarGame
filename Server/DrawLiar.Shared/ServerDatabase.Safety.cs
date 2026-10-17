using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private const int MAX_BLOCKS = 500;
    private const int DAILY_REPORT_LIMIT = 20;
    private static readonly string[] REPORT_STATUSES = { "Open", "Resolved", "Dismissed" };

    public async Task<BlockListResponse> BlocksAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var blocked = new List<string>();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            "SELECT \"BlockedAccountId\" FROM \"AccountBlock\" WHERE \"BlockerAccountId\"=$1 ORDER BY \"CreatedAt\" DESC LIMIT $2", accountId, MAX_BLOCKS);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) blocked.Add(reader.GetGuid(0).ToString());
        return new BlockListResponse { AccountIds = blocked.ToArray() };
    }

    // 차단하면 두 계정의 친구 관계와 대기 중인 친구 요청을 지운다. 방 초대는 친구에게만 보낼 수 있으므로 함께 막힌다.
    public async Task SetBlockAsync(Guid accountId, Guid targetId, bool blocked, CancellationToken cancellationToken = default)
    {
        if (accountId == targetId) throw new ApiException("InvalidOperation");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!blocked)
        {
            if (await Execute(connection, transaction, "DELETE FROM \"AccountBlock\" WHERE \"BlockerAccountId\"=$1 AND \"BlockedAccountId\"=$2", accountId, targetId) > 0)
                await LogActivity(connection, transaction, "Unblock", accountId, targetAccountId: targetId, cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await Execute(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", accountId);
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"Account\" WHERE \"Id\"=$1", targetId) == null) throw new ApiException("PlayerUnavailable", 404);
        bool exists = await Scalar(connection, transaction,
            "SELECT 1 FROM \"AccountBlock\" WHERE \"BlockerAccountId\"=$1 AND \"BlockedAccountId\"=$2", accountId, targetId) != null;
        if (!exists && Convert.ToInt64(await Scalar(connection, transaction,
                "SELECT count(*) FROM \"AccountBlock\" WHERE \"BlockerAccountId\"=$1", accountId)) >= MAX_BLOCKS)
            throw new ApiException("BlockLimit", 409);
        if (await Execute(connection, transaction,
                "INSERT INTO \"AccountBlock\" (\"BlockerAccountId\",\"BlockedAccountId\") VALUES ($1,$2) ON CONFLICT DO NOTHING", accountId, targetId) > 0)
            await LogActivity(connection, transaction, "Block", accountId, targetAccountId: targetId, cancellationToken: cancellationToken);
        await Execute(connection, transaction,
            "DELETE FROM \"Friendship\" WHERE (\"FromAccountId\"=$1 AND \"ToAccountId\"=$2) OR (\"FromAccountId\"=$2 AND \"ToAccountId\"=$1)", accountId, targetId);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsBlockingAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid blocker, Guid blocked) =>
        await Scalar(connection, transaction,
            "SELECT 1 FROM \"AccountBlock\" WHERE \"BlockerAccountId\"=$1 AND \"BlockedAccountId\"=$2", blocker, blocked) != null;

    // 같은 대상·사유의 처리 대기 신고는 하루에 한 번만 받고, 신고자별로 하루 20건까지 받는다.
    public async Task ReportAsync(Guid accountId, PlayerReportRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || Array.IndexOf(PlayerReportRequest.REASONS, request.Reason) < 0) throw new ApiException("InvalidReport");
        Guid targetId = ServerRuntime.AccountId(request.TargetAccountId);
        if (targetId == accountId) throw new ApiException("InvalidReport");
        string context = new string((request.Context ?? "").Trim().Where(character => character == '\n' || !char.IsControl(character)).ToArray());
        if (context.Length > PlayerReportRequest.MAX_CONTEXT_LENGTH) context = context[..PlayerReportRequest.MAX_CONTEXT_LENGTH];
        Guid? roomId = OptionalGuid(request.RoomId), workshopTopicId = OptionalGuid(request.WorkshopTopicId);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Execute(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", accountId);
        if (await Scalar(connection, transaction, "SELECT 1 FROM \"Account\" WHERE \"Id\"=$1", targetId) == null) throw new ApiException("PlayerUnavailable", 404);
        if (await Scalar(connection, transaction,
                "SELECT 1 FROM \"PlayerReport\" WHERE \"ReporterAccountId\"=$1 AND \"TargetAccountId\"=$2 AND \"Reason\"=$3 AND \"Status\"='Open' AND \"CreatedAt\">now()-interval '1 day'",
                accountId, targetId, request.Reason) != null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (Convert.ToInt64(await Scalar(connection, transaction,
                "SELECT count(*) FROM \"PlayerReport\" WHERE \"ReporterAccountId\"=$1 AND \"CreatedAt\">now()-interval '1 day'", accountId)) >= DAILY_REPORT_LIMIT)
            throw new ApiException("ReportLimit", 429);
        await Execute(connection, transaction,
            "INSERT INTO \"PlayerReport\" (\"Id\",\"ReporterAccountId\",\"TargetAccountId\",\"Reason\",\"Context\",\"RoomId\",\"WorkshopTopicId\") VALUES ($1,$2,$3,$4,$5,$6,$7)",
            Guid.NewGuid(), accountId, targetId, request.Reason, context, roomId, workshopTopicId);
        await LogActivity(connection, transaction, "Report", accountId, new { request.Reason }, targetId, roomId, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AdminReportData[]> AdminReportsAsync(string? status)
    {
        if (status != null && Array.IndexOf(REPORT_STATUSES, status) < 0) throw new ApiException("InvalidReport");
        var reports = new List<AdminReportData>();
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null,
            "SELECT p.\"Id\",p.\"ReporterAccountId\",r.\"DisplayName\",p.\"TargetAccountId\",t.\"DisplayName\",p.\"Reason\",p.\"Context\",p.\"RoomId\",p.\"WorkshopTopicId\",p.\"Status\",p.\"CreatedAt\" "
            + "FROM \"PlayerReport\" p JOIN \"Account\" r ON r.\"Id\"=p.\"ReporterAccountId\" JOIN \"Account\" t ON t.\"Id\"=p.\"TargetAccountId\" "
            + "WHERE ($1::text IS NULL OR p.\"Status\"=$1) ORDER BY p.\"CreatedAt\" DESC LIMIT 200", status);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            reports.Add(new AdminReportData
            {
                ReportId = reader.GetGuid(0).ToString(), ReporterAccountId = reader.GetGuid(1).ToString(), ReporterName = reader.GetString(2),
                TargetAccountId = reader.GetGuid(3).ToString(), TargetName = reader.GetString(4), Reason = reader.GetString(5), Context = reader.GetString(6),
                RoomId = reader.IsDBNull(7) ? "" : reader.GetGuid(7).ToString(), WorkshopTopicId = reader.IsDBNull(8) ? "" : reader.GetGuid(8).ToString(),
                Status = reader.GetString(9), CreatedAt = ServerRuntime.Timestamp(reader.GetFieldValue<DateTimeOffset>(10))
            });
        return reports.ToArray();
    }

    public async Task AdminSetReportStatusAsync(Guid reportId, string status)
    {
        if (Array.IndexOf(REPORT_STATUSES, status) < 0) throw new ApiException("InvalidReport");
        await using var connection = await _source.OpenConnectionAsync();
        if (await Execute(connection, null,
                "UPDATE \"PlayerReport\" SET \"Status\"=$2,\"ResolvedAt\"=CASE WHEN $2='Open' THEN NULL ELSE now() END WHERE \"Id\"=$1", reportId, status) == 0)
            throw new ApiException("ReportUnavailable", 404);
    }

    private static Guid? OptionalGuid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty ? parsed : throw new ApiException("InvalidReport");
}
