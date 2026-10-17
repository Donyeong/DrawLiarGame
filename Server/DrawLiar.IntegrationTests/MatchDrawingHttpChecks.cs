using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DrawLiar;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyMatchDrawingHttpAsync(ServerDatabase database, string connectionString, MatchResultRequest request, Guid participant, Guid outsider)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "DrawLiar.GameServer", "bin", configuration, "net10.0", "DrawLiar.GameServer.dll"));
        Check(File.Exists(serverPath), "로컬 HTTP 검증 전에 GameServer를 컴파일해야 합니다.");
        const string url = "http://127.0.0.1:25448";
        const string node = "drawing-replay-http";
        Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(address => address.Port == 25448),
            "그림 HTTP 검증 포트는 다른 서버가 사용하지 않아야 합니다.");
        string clusterKey = ServerRuntime.NewToken();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var start = new ProcessStartInfo("dotnet")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverPath); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__DrawLiarDatabase"] = connectionString;
        start.Environment["Cluster__Key"] = clusterKey;
        start.Environment["GameServer__NodeId"] = node;
        start.Environment["GameServer__PublicUrl"] = url;
        start.Environment["GameServer__MainServerUrl"] = "http://127.0.0.1:25449";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("그림 검증용 게임서버를 시작할 수 없습니다.");
        void RecordDiagnostic(string? line) { if (line == null) return; diagnostics.Enqueue(line); while (diagnostics.Count > 30) diagnostics.TryDequeue(out _); }
        process.OutputDataReceived += (_, line) => RecordDiagnostic(line.Data); process.ErrorDataReceived += (_, line) => RecordDiagnostic(line.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
            bool ready = false;
            for (int attempt = 0; attempt < 60 && !process.HasExited; attempt++)
            {
                try { using var probe = await client.GetAsync("/api/matches/invalid/drawings"); ready = true; break; }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Check(ready, "그림 검증용 게임서버가 로컬 주소에서 응답해야 합니다.");
            string drawingPath = "/api/matches/" + request.MatchId + "/drawings";
            using (var response = await client.GetAsync(drawingPath))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "그림 API는 게임 세션 인증을 요구해야 합니다.");
            var session = await database.IssueSessionAsync(participant, "game:" + node);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            using (var response = await client.GetAsync(drawingPath))
            {
                var result = JsonSerializer.Deserialize<MatchDrawingListResponse>(await response.Content.ReadAsStringAsync(), Json);
                Check(response.StatusCode == HttpStatusCode.OK && result?.Rounds.Length == 2
                    && response.Headers.CacheControl?.NoStore == true, "인증된 참가자에게만 다시보기 목록을 캐시 없이 반환해야 합니다.");
            }
            using (var response = await client.GetAsync(drawingPath + "/2"))
            {
                var result = JsonSerializer.Deserialize<MatchDrawingResponse>(await response.Content.ReadAsStringAsync(), Json);
                Check(response.StatusCode == HttpStatusCode.OK && result?.Drawing.Round == 2 && result.Drawing.Strokes.Length == 2,
                    "실제 HTTP 경로에서 선택한 라운드의 원본 그림을 반환해야 합니다.");
            }
            var other = await database.IssueSessionAsync(outsider, "game:" + node);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Token);
            using (var response = await client.GetAsync(drawingPath + "/1"))
                Check(response.StatusCode == HttpStatusCode.Forbidden, "게임 세션이 있어도 해당 경기의 미참가자는 그림 조회를 거부해야 합니다.");
            string body = JsonSerializer.Serialize(request, Json) + new string(' ', 256 * 1024);
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("/internal/dedicated/matches", content))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "큰 경기 본문도 클러스터 인증 이전에 읽거나 수락하면 안 됩니다.");
            using var clusterClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
            clusterClient.DefaultRequestHeaders.Add("X-Cluster-Key", clusterKey);
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await clusterClient.PostAsync("/internal/dedicated/matches", content))
                Check(response.StatusCode == HttpStatusCode.NoContent, "인증된 경기 결과 경로만 128KiB를 넘는 그림 보고서를 수락해야 합니다.");
            string activity = JsonSerializer.Serialize(new RoomActivityBatchRequest(request.NodeId,
                new[] { new RoomActivityData("RoomChat", request.RoomId, participant.ToString(), Text: "HTTP 활동") }), DrawLiar.DedicatedServer.GameplayWire.Json);
            using (var content = new StringContent(activity, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync("/internal/dedicated/activity", content))
                Check(response.StatusCode == HttpStatusCode.Unauthorized, "방 활동 기록은 클러스터 인증을 요구해야 합니다.");
            using (var content = new StringContent(activity, Encoding.UTF8, "application/json"))
            using (var response = await clusterClient.PostAsync("/internal/dedicated/activity", content))
                Check(response.StatusCode == HttpStatusCode.NoContent
                    && (await database.AdminRoomLogsAsync(Guid.Parse(request.RoomId), 0)).Entries.Any(entry => entry.Kind == "RoomChat" && entry.Detail.Contains("HTTP 활동")),
                    "데디케이티드 직렬화 형식의 방 활동 기록을 실제 게임서버가 저장해야 합니다.");
            Report("실제 GameServer HTTP 그림 API 인증·참가자 조회·미참가자 거부·no-store·인증 후 경기 본문 한도 확장·방 활동 수신 검증");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("그림 HTTP 검증 실패: " + string.Join(Environment.NewLine, diagnostics), exception);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
        }
    }
}
