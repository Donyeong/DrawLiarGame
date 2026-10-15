using DrawLiar;
using DrawLiar.Server;
using Npgsql;

internal static partial class Integration
{
    private static async Task VerifyPlayerSafetyAsync(ServerDatabase database, NpgsqlConnection owner)
    {
        Guid blocker = await database.DevelopmentAccountAsync("차단자"), blocked = await database.DevelopmentAccountAsync("차단대상");
        async Task<long> Friendships() => Convert.ToInt64(await new NpgsqlCommand(
            "SELECT count(*) FROM \"Friendship\" WHERE (\"FromAccountId\"=$1 AND \"ToAccountId\"=$2) OR (\"FromAccountId\"=$2 AND \"ToAccountId\"=$1)", owner)
            { Parameters = { new() { Value = blocker }, new() { Value = blocked } } }.ExecuteScalarAsync());
        async Task<string> Code(Func<Task> action)
        {
            try { await action(); return ""; }
            catch (ApiException exception) { return exception.Code; }
        }

        await database.RequestFriendAsync(blocked, blocker);
        Check(await Friendships() == 1, "차단 검증용 친구 요청이 저장돼야 합니다.");
        await database.SetBlockAsync(blocker, blocked, true);
        await database.SetBlockAsync(blocker, blocked, true);
        Check(await Friendships() == 0 && (await database.BlocksAsync(blocker)).AccountIds.SequenceEqual(new[] { blocked.ToString() }),
            "차단은 중복 없이 저장되고 두 계정의 친구 관계·요청을 지워야 합니다.");
        await database.RequestFriendAsync(blocked, blocker);
        Check(await Friendships() == 0, "나를 차단한 상대에게 보낸 친구 요청은 알리지 않고 전달하지 않아야 합니다.");
        Check(await Code(() => database.RequestFriendAsync(blocker, blocked)) == "BlockedPlayer", "내가 차단한 상대에게는 친구 요청을 보낼 수 없어야 합니다.");
        Check(await Code(() => database.SetBlockAsync(blocker, blocker, true)) == "InvalidOperation", "자기 자신은 차단할 수 없습니다.");
        await database.SetBlockAsync(blocker, blocked, false);
        Check((await database.BlocksAsync(blocker)).AccountIds.Length == 0, "차단 해제가 저장돼야 합니다.");

        var report = new PlayerReportRequest { TargetAccountId = blocked.ToString(), Reason = PlayerReportRequest.REASON_ABUSE, Context = "욕설\u0007" + new string('가', 600) };
        await database.ReportAsync(blocker, report);
        await database.ReportAsync(blocker, report);
        var open = await database.AdminReportsAsync("Open");
        Check(open.Length == 1 && open[0].Context.Length == PlayerReportRequest.MAX_CONTEXT_LENGTH && !open[0].Context.Contains('\u0007'),
            "같은 대상·사유 신고는 하루에 한 번만 받고, 내용은 제어 문자를 지운 500자로 저장해야 합니다.");
        Check(await Code(() => database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = blocker.ToString(), Reason = "Abuse" })) == "InvalidReport"
            && await Code(() => database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = blocked.ToString(), Reason = "Unknown" })) == "InvalidReport",
            "자기 신고와 알 수 없는 사유는 거부해야 합니다.");
        foreach (string reason in PlayerReportRequest.REASONS.Where(reason => reason != PlayerReportRequest.REASON_ABUSE))
            await database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = blocked.ToString(), Reason = reason });
        for (int index = 0; index < 20 - PlayerReportRequest.REASONS.Length; index++)
            await database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = (await database.DevelopmentAccountAsync("신고대상" + index)).ToString(), Reason = "Spam" });
        string overLimit = (await database.DevelopmentAccountAsync("한도초과")).ToString();
        Check(await Code(() => database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = blocked.ToString(), Reason = "Abuse", Context = "새 내용" })) == ""
            && await Code(() => database.ReportAsync(blocker, new PlayerReportRequest { TargetAccountId = overLimit, Reason = "Spam" })) == "ReportLimit",
            "신고자별 하루 20건 한도를 지키고, 이미 접수된 중복 신고는 한도 오류 없이 무시해야 합니다.");
        await database.AdminSetReportStatusAsync(Guid.Parse(open[0].ReportId), "Resolved");
        Check((await database.AdminReportsAsync("Resolved")).Single().ReportId == open[0].ReportId, "운영툴에서 신고 처리 상태를 바꿀 수 있어야 합니다.");
        Check(await Code(() => database.AdminSetReportStatusAsync(Guid.NewGuid(), "Resolved")) == "ReportUnavailable", "없는 신고 처리는 거부해야 합니다.");
    }
}
