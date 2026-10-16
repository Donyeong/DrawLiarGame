using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using DrawLiar;
using DrawLiar.DedicatedServer;
using DrawLiar.Server;

internal static partial class Integration
{
    private static async Task VerifyMatchDrawingCaptureAsync()
    {
        foreach (DrawingMode mode in Enum.GetValues<DrawingMode>())
            await VerifyCompletedDrawingCaptureAsync(mode);
        await VerifyPartialDrawingCaptureAsync();
        await VerifyEmptyDrawingCaptureAsync();
        await VerifyDrawingCaptureLimitAsync();
        VerifyDrawingArchiveCodec();
        await VerifyDrawingOutboxStorageAsync();
        Report("완료 라운드별 원본·작가·필압·삭제·개별 모드 보존·미완료 제외·재시작 분리·그림 포함 outbox 복원·메모리 해제·전송 배치 상한 검증");
    }

    private static GameSession CaptureSession(DrawingFixture fixture) =>
        (GameSession)typeof(DedicatedRoom).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Room)!;

    private static RoundDrawingData[] CaptureDrawings(MatchResultRequest result) =>
        result.DrawingArchives.Select(DrawingArchiveCodec.Decode).ToArray();

    private static async Task CaptureAdvanceAsync(DrawingFixture fixture)
    {
        var session = CaptureSession(fixture);
        fixture.Now += session.Snapshot(-1, -1, fixture.Now).RemainingSeconds + .02;
        fixture.Room.Tick(fixture.Now);
        await fixture.FlushAsync();
    }

    private static async Task CaptureFinishRoundAsync(DrawingFixture fixture)
    {
        for (int step = 0; step < 20 && fixture.State.Phase != GamePhase.RoundResults; step++)
            await CaptureAdvanceAsync(fixture);
        Check(fixture.State.Phase == GamePhase.RoundResults, "완료 그림 검증은 라운드 결과에 도달해야 합니다.");
    }

    private static async Task VerifyCompletedDrawingCaptureAsync(DrawingMode mode)
    {
        await using var fixture = new DrawingFixture(mode, 2);
        await fixture.StartAsync();
        int first = fixture.State.ArtistId;
        fixture.Paint(fixture.Connection(first));
        fixture.Send(fixture.Connection(first), fixture.Request("clearOwn", first));
        await fixture.FlushAsync();
        var pressure = DrawingFixture.Stroke(fixture.State.CanvasVersion);
        pressure.StartSize = .006f;
        fixture.Now += .02;
        fixture.Room.Receive(fixture.Connection(first), new GameplayEnvelope { Type = "stroke", Stroke = pressure }, fixture.Now);
        fixture.Send(fixture.Connection(first), new GameplayEnvelope { Type = "request", Kind = "endTurn" });
        await fixture.FlushAsync();
        int second = fixture.State.ArtistId;
        fixture.Paint(fixture.Connection(second), eraser: true);
        fixture.Send(fixture.Connection(second), new GameplayEnvelope { Type = "request", Kind = "endTurn" });
        await fixture.FlushAsync();
        int third = fixture.State.ArtistId;
        fixture.Paint(fixture.Connection(third));
        await CaptureFinishRoundAsync(fixture);
        Check(fixture.CapturedReports.Count == 0, "최종 라운드 전에는 경기 결과를 전송하면 안 됩니다.");
        await CaptureAdvanceAsync(fixture);
        Check(fixture.State.Round == 2 && fixture.State.Phase == GamePhase.RoleReveal, "두 번째 라운드는 새 epoch로 시작해야 합니다.");
        await CaptureAdvanceAsync(fixture);
        fixture.Paint(fixture.Connection(fixture.State.ArtistId));
        await CaptureFinishRoundAsync(fixture);
        var result = fixture.CapturedReports.Single();
        var rounds = CaptureDrawings(result);
        Check(rounds.Length == 2 && rounds.Select(round => round.Round).SequenceEqual(new[] { 1, 2 }) && !result.DrawingsIncomplete,
            "경기 결과는 완료 라운드마다 그림 한 개를 포함해야 합니다.");
        var drawing = rounds[0];
        Check(drawing.Topic == "과일" && drawing.Word == "사과" && drawing.Players.Length == 3
            && drawing.Players.All(player => player.AccountId == fixture.Connection(player.PlayerId).AccountId)
            && drawing.Players.Select(player => player.PlayerId).SequenceEqual(new[] { first, second, third }),
            "라운드 그림은 원래 그리기 순서와 서버 계정 매핑을 보존하고 관전자를 제외해야 합니다.");
        Check(drawing.Strokes.Length == 3 && drawing.Strokes[0].AuthorPlayerId == first && drawing.Strokes[0].StartSize == pressure.StartSize
            && drawing.Strokes[1].AuthorPlayerId == second && drawing.Strokes[1].R == 255 && drawing.Strokes[1].G == 255 && drawing.Strokes[1].B == 255,
            "자기 선 삭제를 반영하고 개별 모드의 이전 작가·압력·지우개 흰 선을 함께 보존해야 합니다.");
        Check(rounds[1].Strokes.Length == 1, "이전 라운드 그림은 다음 라운드에 섞이면 안 됩니다.");
        string frozen = JsonSerializer.Serialize(result, Json);
        await CaptureAdvanceAsync(fixture);
        await fixture.StartAsync();
        fixture.Paint(fixture.Connection(fixture.State.ArtistId));
        Check(JsonSerializer.Serialize(result, Json) == frozen, "경기 재시작과 새 그림은 이미 전송한 원본을 바꾸면 안 됩니다.");
        await CaptureFinishRoundAsync(fixture);
        await CaptureAdvanceAsync(fixture);
        await CaptureAdvanceAsync(fixture);
        await CaptureFinishRoundAsync(fixture);
        var restarted = CaptureDrawings(fixture.CapturedReports.Last());
        Check(fixture.CapturedReports.Count == 2 && restarted.Length == 2
            && restarted[0].Strokes.Length == 1 && restarted[1].Strokes.Length == 0,
            "재시작한 경기는 이전 그림 없이 자체 라운드와 빈 그림을 보존해야 합니다.");

        string path = Path.Combine(Path.GetTempPath(), "drawliar-replay-outbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var outbox = new MatchResultOutbox(path)) outbox.Enqueue(result);
            using var recovered = new MatchResultOutbox(path);
            Check(JsonSerializer.Serialize(recovered.Due(DateTimeOffset.UtcNow).Single(), Json) == frozen,
                "프로세스 재시작 후에도 경기당 하나의 outbox에서 그림과 전적을 함께 복원해야 합니다.");
            recovered.Acknowledge(result.MatchId);
        }
        finally { Directory.Delete(path, true); }
    }

    private static async Task VerifyPartialDrawingCaptureAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Individual, 3);
        await fixture.StartAsync();
        fixture.Paint(fixture.Connection(fixture.State.ArtistId));
        await CaptureFinishRoundAsync(fixture);
        await CaptureAdvanceAsync(fixture);
        await CaptureAdvanceAsync(fixture);
        fixture.Paint(fixture.Connection(fixture.State.ArtistId));
        var session = CaptureSession(fixture);
        foreach (int id in fixture.State.Players.Where(player => !player.IsSpectator).Select(player => player.Id).Take(2))
            session.Disconnect(id, fixture.Now += .01, false);
        var result = fixture.CapturedReports.Single();
        var rounds = CaptureDrawings(result);
        Check(result.RoundCount == 1 && rounds.Length == 1 && rounds[0].Round == 1 && rounds[0].Strokes.Length == 1,
            "중도 종료 시 마지막 미완료 라운드를 빼고 완료 라운드 그림만 전적과 함께 보존해야 합니다.");
    }

    private static async Task VerifyEmptyDrawingCaptureAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Relay);
        await fixture.StartAsync();
        await CaptureFinishRoundAsync(fixture);
        var round = CaptureDrawings(fixture.CapturedReports.Single()).Single();
        Check(round.Round == 1 && round.Strokes.Length == 0 && round.Players.Length == 3,
            "아무도 그리지 않은 완료 라운드도 빈 캔버스로 다시 볼 수 있어야 합니다.");
    }

    private static async Task VerifyDrawingCaptureLimitAsync()
    {
        await using var fixture = new DrawingFixture(DrawingMode.Relay);
        await fixture.StartAsync();
        typeof(DedicatedRoom).GetField("_roundDrawingBytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Room, (long)DrawingReplayRules.MAX_COMPRESSED_MATCH_BYTES);
        await CaptureFinishRoundAsync(fixture);
        var result = fixture.CapturedReports.Single();
        Check(result.RoundCount == 1 && result.Players.Length == 3 && result.DrawingArchives.Length == 0 && result.DrawingsIncomplete,
            "그림 보관 한도를 넘겨도 경기 전적과 보상 보고서는 보존하고 누락 여부를 명시해야 합니다.");
    }

    private static void VerifyDrawingArchiveCodec()
    {
        var round = new RoundDrawingData
        {
            Round = 1, Topic = "과일", Word = "사과",
            Players = new[] { new ReplayPlayerData { PlayerId = 1, AccountId = Guid.NewGuid().ToString(), DisplayName = "화가" } },
            Strokes = Enumerable.Range(0, DrawingReplayRules.MAX_STROKES_PER_ROUND).Select(_ => new ReplayStrokeData
            { X1 = .1f, Y1 = .2f, X2 = .3f, Y2 = .4f, Size = .01f, StartSize = .004f, AuthorPlayerId = 1 }).ToArray()
        };
        Check(DrawingArchiveCodec.TryEncode(round, out var encoded), "기존 그리기 상한까지 원본을 잘라내지 않고 압축해야 합니다.");
        var decoded = DrawingArchiveCodec.Decode(encoded);
        Check(decoded.Strokes.Length == DrawingReplayRules.MAX_STROKES_PER_ROUND && decoded.Strokes[^1].StartSize == .004f,
            "최대 라운드의 모든 선과 압력값은 압축 해제 후 유지되어야 합니다.");
        Reject(new RoundDrawingArchiveData { Round = 1, Payload = new byte[] { 1, 2, 3, 4 } });
        Reject(new RoundDrawingArchiveData { Round = 2, Payload = encoded.Payload });
        Reject(new RoundDrawingArchiveData { Round = 1, Payload = new byte[DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES + 1] });
        using var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.SmallestSize, true))
        {
            var whitespace = Enumerable.Repeat((byte)' ', 4096).ToArray();
            for (int index = 0; index <= DrawingReplayRules.MAX_ROUND_JSON_BYTES / whitespace.Length; index++) gzip.Write(whitespace);
        }
        Reject(new RoundDrawingArchiveData { Round = 1, Payload = payload.ToArray() });

        static void Reject(RoundDrawingArchiveData archive)
        {
            bool rejected = false;
            try { DrawingArchiveCodec.Decode(archive); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "손상·다른 라운드·압축/해제 크기 초과 그림은 안전하게 거부해야 합니다.");
        }
    }

    private static async Task VerifyDrawingOutboxStorageAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "drawliar-replay-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var outbox = new MatchResultOutbox(path))
            {
                var original = ReliabilityResult();
                original.DrawingArchives = new[] { new RoundDrawingArchiveData { Round = 1, Payload = new byte[] { 12, 34, 56 } } };
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => outbox.Enqueue(original))));
                Check(Directory.GetFiles(path, "*.json").Length == 1, "같은 경기의 동시 접수는 원본 파일을 중복 저장하면 안 됩니다.");
                AssertReleased(outbox);
                var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => outbox.Due(DateTimeOffset.UtcNow).Single())));
                reads[0].DrawingArchives[0].Payload[0] = 99;
                Check(reads.Skip(1).All(result => result.DrawingArchives[0].Payload[0] == 12)
                    && outbox.Due(DateTimeOffset.UtcNow).Single().DrawingArchives[0].Payload[0] == 12,
                    "동시 전송 조회와 호출자 변경은 저장된 경기 원본이나 다른 조회의 배열을 바꾸면 안 됩니다.");
                outbox.Retry(original.MatchId, DateTimeOffset.UtcNow);
                AssertReleased(outbox);
                Check(outbox.Due(DateTimeOffset.UtcNow).Length == 0 && outbox.Due(DateTimeOffset.UtcNow.AddSeconds(11)).Length == 1,
                    "디스크 기반 보고서도 재시도 시각을 보존하고 원본과 함께 다시 읽어야 합니다.");
                string file = Directory.GetFiles(path, "*.json").Single();
                File.WriteAllText(file, "{");
                Check(outbox.Due(DateTimeOffset.UtcNow.AddHours(1)).Length == 0
                    && Directory.GetFiles(Path.Combine(path, "quarantine")).Length == 1 && outbox.CanRecord(1),
                    "실행 중 손상된 원본은 격리하고 뒤 보고서의 전송을 막으면 안 됩니다.");
                var missing = ReliabilityResult();
                outbox.Enqueue(missing);
                File.Delete(Directory.GetFiles(path, "*.json").Single());
                Check(outbox.Due(DateTimeOffset.UtcNow).Length == 0 && outbox.CanRecord(1),
                    "외부에서 삭제된 보고서의 빈 항목은 반복 전송 대기열에 남으면 안 됩니다.");
            }
            using (var outbox = new MatchResultOutbox(path))
            {
                var block = new byte[DrawingReplayRules.MAX_COMPRESSED_ROUND_BYTES];
                for (int index = 0; index < 6; index++)
                {
                    var result = ReliabilityResult();
                    result.DrawingArchives = new[] { new RoundDrawingArchiveData { Round = 1, Payload = block } };
                    outbox.Enqueue(result);
                }
                AssertReleased(outbox);
                var firstBatch = outbox.Due(DateTimeOffset.UtcNow);
                Check(firstBatch.Length == 5 && firstBatch.Sum(result => result.DrawingArchives.Sum(archive => archive.Payload.LongLength)) < 64L * 1024 * 1024,
                    "그림 보고서의 전송 조회는 파일 크기 기준 64MiB 배치를 넘겨 한꺼번에 읽으면 안 됩니다.");
                foreach (var result in firstBatch) outbox.Acknowledge(result.MatchId);
                var last = outbox.Due(DateTimeOffset.UtcNow).Single();
                outbox.Acknowledge(last.MatchId);
                var large = ReliabilityResult();
                large.DrawingArchives = Enumerable.Range(1, 9).Select(round => new RoundDrawingArchiveData { Round = round, Payload = block }).ToArray();
                outbox.Enqueue(large);
                var trailing = ReliabilityResult();
                outbox.Enqueue(trailing);
                outbox.Retry(trailing.MatchId, DateTimeOffset.UtcNow);
                AssertReleased(outbox);
                var largeBatch = outbox.Due(DateTimeOffset.UtcNow);
                Check(largeBatch.Length == 1 && largeBatch[0].MatchId == large.MatchId && largeBatch[0].DrawingArchives.Length == 9,
                    "배치 상한보다 큰 정상 보고서는 원본을 자르지 않고 단독으로 읽어 전송해야 합니다.");
                outbox.Acknowledge(large.MatchId);
                Check(outbox.Due(DateTimeOffset.UtcNow.AddSeconds(11)).Length == 1, "큰 보고서 뒤의 작은 보고서도 다음 배치에서 처리해야 합니다.");
            }
        }
        finally { Directory.Delete(path, true); }

        static void AssertReleased(MatchResultOutbox outbox)
        {
            var entries = (System.Collections.IDictionary)typeof(MatchResultOutbox)
                .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(outbox)!;
            Check(entries.Values.Cast<object>().All(entry => entry.GetType().GetProperty("Result")!.GetValue(entry) == null),
                "원자적 파일 저장 후에는 대기열이 그림 배열이나 전체 보고서를 메모리에 붙잡으면 안 됩니다.");
        }
    }
}
