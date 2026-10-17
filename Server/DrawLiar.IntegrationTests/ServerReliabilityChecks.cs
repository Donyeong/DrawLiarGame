using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class Integration
{
    private static async Task VerifyServerReliabilityAsync()
    {
        var lease = new SessionValidationLease(100);
        Check(lease.AllowsTransient(129.999) && !lease.AllowsTransient(130) && !lease.AllowsTransient(140),
            "일시 오류 유예는 마지막 성공부터 30초에 끝나고 오류 자체로 연장되면 안 됩니다.");
        lease.Succeed(150);
        Check(lease.AllowsTransient(179.999) && !lease.AllowsTransient(180), "성공한 재검증만 유예 기준을 갱신해야 합니다.");
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
            HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests })
            Check(SessionValidationLease.IsTransient(status) && !GameClusterClient.IsPermanentResultFailure(status),
                "일시 HTTP 오류는 인증 유예 및 보고서 재시도 대상으로 분류해야 합니다.");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.Conflict })
            Check(!SessionValidationLease.IsTransient(status) && GameClusterClient.IsPermanentResultFailure(status),
                "명시적 인증 실패와 영구 4xx 보고서 오류를 일시 오류로 취급하면 안 됩니다.");

        string root = Path.Combine(Path.GetTempPath(), "drawliar-reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyDurableOutbox(Path.Combine(root, "outbox"));
            await VerifyClusterRecoveryAsync(root);
        }
        finally { Directory.Delete(root, true); }
        Report("보고서 디스크 복원·ACK 후 삭제·보고서별 재시도·영구 4xx 격리·세션 일시 장애 유예·즉시 폐기 검증");
    }

    private static MatchResultRequest ReliabilityResult() => new()
    {
        NodeId = "reliability-node", RoomId = Guid.NewGuid().ToString(), MatchId = Guid.NewGuid().ToString(),
        PlayedAt = DateTimeOffset.UtcNow.ToString("O"), RoundCount = 1,
        Players = new[] { new MatchPlayerResult { AccountId = Guid.NewGuid().ToString(), Score = 7, Rank = 1 } }
    };

    private static void VerifyDurableOutbox(string path)
    {
        var first = ReliabilityResult();
        var second = ReliabilityResult();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (var outbox = new MatchResultOutbox(path))
        {
            outbox.Enqueue(first);
            outbox.Enqueue(second);
            Check(Directory.GetFiles(path, "*.json").Length == 2 && outbox.CanRecord(3),
                "전송하기 전에 각 보고서를 독립 파일에 저장해야 합니다.");
            bool locked = false;
            try { using var duplicate = new MatchResultOutbox(path); }
            catch (IOException) { locked = true; }
            Check(locked, "동일 outbox를 두 프로세스에서 동시에 소비하면 안 됩니다.");
            outbox.Retry(first.MatchId, now);
            Check(outbox.Due(now).Single().MatchId == second.MatchId,
                "한 보고서의 재시도 지연이 뒤 보고서의 전송을 막으면 안 됩니다.");
        }
        using (var recovered = new MatchResultOutbox(path))
        {
            Check(recovered.Due(now).Single().MatchId == second.MatchId && recovered.Due(now.AddSeconds(11)).Length == 2,
                "재시작 후 미승인 보고서와 보고서별 다음 재시도 시각을 복원해야 합니다.");
            recovered.Acknowledge(second.MatchId);
            recovered.Acknowledge(second.MatchId);
            recovered.Quarantine(first.MatchId, 400);
            Check(recovered.Due(now.AddDays(1)).Length == 0 && Directory.GetFiles(path, "*.json").Length == 0
                && Directory.GetFiles(Path.Combine(path, "quarantine")).Length == 1,
                "ACK된 보고서만 삭제하고 영구 오류 보고서는 별도 보존해야 합니다.");
        }
        using (var empty = new MatchResultOutbox(path))
            Check(empty.Due(now.AddDays(1)).Length == 0, "ACK 및 격리한 보고서는 재시작 후 재전송하면 안 됩니다.");

        var interrupted = ReliabilityResult();
        using (var outbox = new MatchResultOutbox(path)) outbox.Enqueue(interrupted);
        string completed = Directory.GetFiles(path, "*.json").Single();
        File.Move(completed, completed + ".tmp");
        File.WriteAllText(Path.Combine(path, "invalid.json"), "{");
        using (var recovered = new MatchResultOutbox(path))
        {
            Check(recovered.Due(now.AddDays(1)).Single().MatchId == interrupted.MatchId,
                "flush 후 rename 전에 중단된 임시 보고서도 복원하고 손상 파일은 뒤 보고서와 분리해야 합니다.");
            recovered.Acknowledge(interrupted.MatchId);
        }

        string failurePath = Path.Combine(path, "write-failure");
        using (var outbox = new MatchResultOutbox(failurePath))
        {
            var result = ReliabilityResult();
            string file = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.MatchId))) + ".json.tmp";
            string blocker = Path.Combine(failurePath, file);
            Directory.CreateDirectory(blocker);
            outbox.Enqueue(result);
            Check(!outbox.CanRecord(100) && outbox.Due(now.AddDays(1)).Length == 0,
                "디스크 저장 실패 시 새 경기를 막고 저장하지 못한 보고서를 전송 승인으로 잃으면 안 됩니다.");
            Directory.Delete(blocker);
            Check(outbox.Due(now.AddDays(1)).Single().MatchId == result.MatchId && outbox.CanRecord(100),
                "디스크 저장이 복구되면 메모리에 남긴 보고서를 먼저 내구화해야 합니다.");
            outbox.Acknowledge(result.MatchId);
        }
        Check(!RoomRegistry.DefaultOutboxPath("test").StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            "기본 outbox는 배포 바이너리 디렉터리와 분리된 사용자 데이터 경로여야 합니다.");
    }

    private static async Task VerifyClusterRecoveryAsync(string root)
    {
        int sessionMode = 503;
        var statuses = new ConcurrentDictionary<string, int>();
        var received = new ConcurrentDictionary<string, int>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/internal/dedicated/matches", async (HttpContext context) =>
        {
            var result = await context.Request.ReadFromJsonAsync<MatchResultRequest>(GameplayWire.Json);
            received.AddOrUpdate(result!.MatchId, 1, (_, count) => count + 1);
            context.Response.StatusCode = statuses[result.MatchId];
        });
        int activityStatus = 503;
        var activities = new ConcurrentQueue<DrawLiar.Server.RoomActivityBatchRequest>();
        app.MapPost("/internal/dedicated/activity", async (HttpContext context) =>
        {
            activities.Enqueue((await context.Request.ReadFromJsonAsync<DrawLiar.Server.RoomActivityBatchRequest>(GameplayWire.Json))!);
            context.Response.StatusCode = activityStatus;
        });
        app.MapPost("/internal/sessions/check", async (HttpContext context) =>
        {
            if (sessionMode == -1) { context.Abort(); return Results.Empty; }
            if (sessionMode == -2) return Results.Text("{", "application/json");
            if (sessionMode == -3) await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            return sessionMode >= 400 ? Results.StatusCode(sessionMode)
                : Results.Json(new SessionCheckResponse { Valid = sessionMode == 200 }, GameplayWire.Json);
        });
        await app.StartAsync();
        string gameData = Path.Combine(root, "GameData.json");
        string[] catalog = Enumerable.Range(0, 128).Select(index => index.ToString("D3") + new string('\'', 37)).ToArray();
        await File.WriteAllTextAsync(gameData, JsonSerializer.Serialize(new GameData
        {
            Topics = catalog.Select(name => new TopicData { Name = name, Words = new[] { "사과" } }).ToArray()
        }, GameplayWire.Json));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dedicated:GameDataPath"] = gameData, ["Dedicated:OutboxPath"] = Path.Combine(root, "cluster-outbox"),
            ["Dedicated:NodeId"] = "reliability-node", ["Dedicated:GameServerUrl"] = app.Urls.Single(),
            ["Cluster:Key"] = new string('k', 32)
        }).Build();
        using var rooms = new RoomRegistry(config);
        using var cluster = new GameClusterClient(config, app.Environment, rooms, NullLogger<GameClusterClient>.Instance);
        string account = Guid.NewGuid().ToString();
        var roomData = new ServerRoomData { RoomId = Guid.NewGuid().ToString(), OwnerAccountId = account,
            Settings = new ServerRoomSettings { Topics = new[] { catalog[0] } } };
        await using var joined = new GameConnection(new PolicySocket(), account, "token", CancellationToken.None);
        cluster.TrackValidatedSession(joined);
        var room = rooms.Join(joined, new RedeemTicketResponse { AccountId = account, Room = roomData,
            Profile = new ProfileData { AccountId = account, DisplayName = "검증화가" } });
        Check(room != null, "크기 검사용 방을 구성해야 합니다.");
        await cluster.FlushActivitiesAsync(CancellationToken.None);
        Check(activities.Count == 1 && rooms.PendingActivities(64).Entries.Length == 1, "일시 오류인 방 활동 로그는 대기열에 남겨 다시 보내야 합니다.");
        activityStatus = 204;
        await cluster.FlushActivitiesAsync(CancellationToken.None);
        Check(activities.Count == 2 && rooms.PendingActivities(64).Entries.Length == 0 && activities.Last().NodeId == "reliability-node"
            && activities.Last().Entries.Single() is { Kind: "RoomEnter" } entered && entered.AccountId == account && entered.RoomId == roomData.RoomId,
            "방 활동 로그는 노드 ID와 함께 보내고 성공한 뒤에만 대기열에서 지워야 합니다.");
        bool rejected = false;
        try
        {
            await cluster.ConfigureAsync(new ConfigureRoomRequest { RoomId = roomData.RoomId,
                Settings = new ServerRoomSettings { Topics = catalog } }, CancellationToken.None);
        }
        catch (DrawLiar.Server.ApiException exception) { rejected = exception.Code == "RoomStateTooLarge"; }
        Check(rejected && room!.Status().Settings.Topics.Length == 1 && joined.IsAlive,
            "실제 설정 승인 경로는 HTTP 저장 전에 최대 상태 크기를 거부하고 기존 설정·연결을 보존해야 합니다.");
        cluster.EnforceSessionLeases(RoomRegistry.Now + SessionValidationLease.GRACE_SECONDS + 1);
        Check(!joined.IsAlive, "검증 작업 큐가 밀려도 별도 만료 확인으로 마지막 성공 기준 유예 상한을 지켜야 합니다.");
        var results = Enumerable.Range(0, 4).Select(_ => ReliabilityResult()).ToArray();
        int[] codes = { 503, 204, 400, 429 };
        for (int index = 0; index < results.Length; index++)
        {
            statuses[results[index].MatchId] = codes[index];
            rooms.QueueResult(results[index]);
        }
        await cluster.FlushResultsAsync(CancellationToken.None);
        Check(results.All(result => received.TryGetValue(result.MatchId, out int count) && count == 1),
            "일시 오류와 영구 4xx 뒤 보고서도 같은 전송 회차에서 처리해야 합니다.");
        Check(rooms.PendingResults().Length == 0 && Directory.GetFiles(Path.Combine(root, "cluster-outbox"), "*.json").Length == 2
            && Directory.GetFiles(Path.Combine(root, "cluster-outbox", "quarantine")).Length == 1,
            "2xx만 ACK 삭제하고 503·429는 디스크에 재시도 대기, 400은 격리해야 합니다.");

        await using var connection = new GameConnection(new PolicySocket(), "session-user", "token", CancellationToken.None);
        cluster.TrackValidatedSession(connection);
        await cluster.ValidateSessionAsync(connection, CancellationToken.None);
        Check(connection.IsAlive, "최근 인증된 연결은 503에 즉시 끊기면 안 됩니다.");
        foreach (int mode in new[] { -1, -2, -3, 200 })
        {
            sessionMode = mode;
            await cluster.ValidateSessionAsync(connection, CancellationToken.None);
            Check(connection.IsAlive, "짧은 네트워크 오류·타임아웃·잘못된 응답은 제한된 유예를 받고 정상 재검증으로 복구해야 합니다.");
        }
        sessionMode = 201;
        await cluster.ValidateSessionAsync(connection, CancellationToken.None);
        Check(!connection.IsAlive, "Valid=false는 유예 중에도 즉시 연결에 반영해야 합니다.");
        await using var revoked = new GameConnection(new PolicySocket(), "revoked-user", "token", CancellationToken.None);
        cluster.TrackValidatedSession(revoked);
        sessionMode = 401;
        await cluster.ValidateSessionAsync(revoked, CancellationToken.None);
        Check(!revoked.IsAlive, "401 인증 폐기는 유예 없이 반영해야 합니다.");
        await app.StopAsync();
    }
}
