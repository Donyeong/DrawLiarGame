using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DrawLiar.Server;
using Microsoft.Extensions.Logging;

namespace DrawLiar.DedicatedServer;

internal sealed class RoomRegistry : BackgroundService
{
    private readonly object _gate = new();
    private const int MAX_PENDING_RESULTS = 2048;
    private const int MAX_PENDING_ACTIVITIES = 8192;
    private readonly MatchResultOutbox _outbox;
    private readonly Dictionary<string, DedicatedRoom> _rooms = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _statusChanged = new(0, 1);
    private readonly GameData _data;
    private readonly int _capacity;
    private readonly string _nodeId;
    private readonly ILogger<RoomRegistry>? _logger;
    // 방 잠금 안에서 호출되므로 다른 잠금을 잡지 않는 별도 잠금을 쓴다.
    private readonly object _activityGate = new();
    private readonly Queue<(long Sequence, RoomActivityData Activity)> _activities = new();
    private long _activitySequence;
    private bool _activityOverflowed;

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public RoomRegistry(IConfiguration configuration, ILogger<RoomRegistry>? logger = null)
    {
        _logger = logger;
        _capacity = Math.Clamp(configuration.GetValue("Dedicated:Capacity", 32), 1, 128);
        _nodeId = configuration["Dedicated:NodeId"] ?? "dedicated-1";
        string path = configuration["Dedicated:GameDataPath"] ?? Path.Combine(AppContext.BaseDirectory, "GameData.json");
        _data = System.Text.Json.JsonSerializer.Deserialize<GameData>(File.ReadAllText(path), GameplayWire.Json)
            ?? throw new InvalidDataException("게임 주제 데이터가 없습니다.");
        _data.Topics = (_data.Topics ?? Array.Empty<TopicData>()).Where(topic => topic != null)
            .Select(topic => new TopicData
            {
                Name = GameRules.CleanText(topic.Name, 40),
                Words = (topic.Words ?? Array.Empty<string>()).Select(word => GameRules.CleanText(word, 40))
                    .Where(word => word.Length > 0).Distinct().Take(200).ToArray(),
                LanguageCode = ServerRoomSettings.TopicLanguageOf(topic.LanguageCode),
                Aliases = (topic.Aliases ?? Array.Empty<WordAlias>()).Where(alias => alias?.Accept != null)
                    .Select(alias => new WordAlias { Word = GameRules.CleanText(alias.Word, 40),
                        Accept = alias.Accept.Select(value => GameRules.CleanText(value, 40)).Where(value => value.Length > 0).Take(16).ToArray() })
                    .Where(alias => alias.Word.Length > 0 && alias.Accept.Length > 0).ToArray()
            }).Where(topic => topic.Name.Length > 0 && topic.Words.Length > 0
                && ServerRoomSettings.TryNormalizeTopicLanguage(topic.LanguageCode, out _))
            .GroupBy(topic => topic.LanguageCode).SelectMany(language => language.Take(128)).ToArray();
        if (_data.Topics.Length == 0) throw new InvalidDataException("게임 주제 데이터가 비어 있습니다.");
        _data.Scoring ??= new ScoreRules();
        _data.Scoring.LiarUncaught = Math.Clamp(_data.Scoring.LiarUncaught, 0, 1000);
        _data.Scoring.LiarCorrectGuess = Math.Clamp(_data.Scoring.LiarCorrectGuess, 0, 1000);
        _data.Scoring.CitizenCorrectVote = Math.Clamp(_data.Scoring.CitizenCorrectVote, 0, 1000);
        _outbox = new MatchResultOutbox(configuration["Dedicated:OutboxPath"] ?? DefaultOutboxPath(_nodeId),
            message => logger?.LogError("경기 outbox 저장 오류: {FailureType}", message));
    }

    internal static string DefaultOutboxPath(string nodeId)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("Dedicated:OutboxPath 설정이 필요합니다.");
            root = Path.Combine(profile, ".local", "share");
        }
        string node = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nodeId)))[..16];
        return Path.Combine(root, "DrawLiar", "Dedicated", node, "outbox");
    }

    public int Capacity => _capacity;

    public DedicatedRoom? Join(GameConnection connection, RedeemTicketResponse ticket)
    {
        lock (_gate)
        {
            bool created = false;
            if (!_rooms.TryGetValue(ticket.Room.RoomId, out var room))
            {
                if (_rooms.Count >= _capacity) return null;
                _rooms[ticket.Room.RoomId] = room = new DedicatedRoom(ticket.Room, _data, ticket.CustomTopics, Now,
                    NotifyStatusChanged, CanRecordMatch, QueueResult, QueueActivity);
                created = true;
            }
            if (room.Join(connection, ticket, Now)) return room;
            if (created) _rooms.Remove(ticket.Room.RoomId);
            return null;
        }
    }

    public RoomStatusData[] Statuses()
    {
        lock (_gate) return _rooms.Values.Select(room => room.Status()).ToArray();
    }

    private bool CanRecordMatch()
    {
        return _outbox.CanRecord(MAX_PENDING_RESULTS - _capacity);
    }

    internal void QueueResult(MatchResultRequest result)
    {
        result.NodeId = _nodeId;
        _outbox.Enqueue(result);
        NotifyStatusChanged();
    }

    public MatchResultRequest[] PendingResults()
    {
        return _outbox.Due(DateTimeOffset.UtcNow);
    }

    public void AcknowledgeResult(string matchId)
    {
        _outbox.Acknowledge(matchId);
    }

    public void RetryResult(string matchId) => _outbox.Retry(matchId, DateTimeOffset.UtcNow);

    // ponytail: 방 활동 로그는 메모리에만 보관한다. 게임서버 장애 중 상한을 넘거나 프로세스가 끝나면 오래된 기록부터 잃는다.
    internal void QueueActivity(RoomActivityData activity)
    {
        lock (_activityGate)
        {
            if (_activities.Count >= MAX_PENDING_ACTIVITIES)
            {
                _activities.Dequeue();
                if (!_activityOverflowed) _logger?.LogWarning("전송 대기 방 활동 로그가 상한을 넘어 오래된 기록을 버립니다.");
                _activityOverflowed = true;
            }
            _activities.Enqueue((++_activitySequence, activity));
        }
    }

    public (long LastSequence, RoomActivityData[] Entries) PendingActivities(int limit)
    {
        lock (_activityGate)
        {
            var batch = _activities.Take(limit).ToArray();
            return (batch.Length == 0 ? 0 : batch[^1].Sequence, batch.Select(entry => entry.Activity).ToArray());
        }
    }

    public void AcknowledgeActivities(long lastSequence)
    {
        lock (_activityGate)
        {
            while (_activities.TryPeek(out var entry) && entry.Sequence <= lastSequence) _activities.Dequeue();
            _activityOverflowed = false;
        }
    }

    public void QuarantineResult(string matchId, int status) => _outbox.Quarantine(matchId, status);

    public void ValidateConfigurationStateSize(ConfigureRoomRequest request)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(request.RoomId, out var room))
                throw new DrawLiar.Server.ApiException("RoomConfigurationChanged", 409);
            room.ValidateConfigurationStateSize(request);
        }
    }

    public void AcknowledgeHeartbeat(RoomStatusData[] statuses, string[] roomIds, RoomConfigurationData[]? configurations = null, RoomKickData[]? kicks = null)
    {
        lock (_gate)
        {
            foreach (var status in statuses)
                if (_rooms.TryGetValue(status.RoomId, out var room)) room.AcknowledgeAdmissions(status.AdmissionIds);
            foreach (var configuration in configurations ?? [])
                if (_rooms.TryGetValue(configuration.RoomId, out var room)) room.ApplyConfiguration(configuration);
            foreach (var data in kicks ?? [])
                if (_rooms.TryGetValue(data.RoomId, out var room)) room.ApplyKicks(data);
            foreach (string roomId in roomIds)
                if (_rooms.TryGetValue(roomId, out var room) && room.Status().Closed) _rooms.Remove(roomId);
        }
    }

    private void NotifyStatusChanged()
    {
        try { _statusChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitForStatusChangeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _statusChanged.WaitAsync(timeout, cancellationToken);

    public GameConnection[] Connections()
    {
        lock (_gate) return _rooms.Values.SelectMany(room => room.Connections()).ToArray();
    }

    public void AbortAll()
    {
        lock (_gate)
            foreach (var room in _rooms.Values) room.AbortAll();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                lock (_gate)
                {
                    foreach (var room in _rooms.Values) room.Tick(Now);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { AbortAll(); }
    }

    public override void Dispose()
    {
        _outbox.Dispose();
        base.Dispose();
    }
}

internal sealed class MatchResultOutbox : IDisposable
{
    private const long MAX_BATCH_BYTES = 64L * 1024 * 1024;
    private const long MAX_FILE_BYTES = DrawingReplayRules.MAX_REQUEST_BYTES + 4096;
    private sealed class Entry
    {
        public MatchResultRequest? Result { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Persisted { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string MatchId { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public long StoredLength { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path, _quarantine;
    private readonly Action<string> _storageError;
    private readonly FileStream _lease;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public MatchResultOutbox(string path, Action<string>? storageError = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Dedicated:OutboxPath가 비어 있습니다.");
        _path = Path.GetFullPath(path);
        _quarantine = Path.Combine(_path, "quarantine");
        _storageError = storageError ?? (_ => { });
        Directory.CreateDirectory(_quarantine);
        _lease = new FileStream(Path.Combine(_path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Restore(); }
        catch { _lease.Dispose(); throw; }
    }

    private string FilePath(string matchId) => Path.Combine(_path,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matchId))) + ".json");

    private void Restore()
    {
        foreach (string file in Directory.EnumerateFiles(_path).Where(file => file.EndsWith(".json", StringComparison.Ordinal)
            || file.EndsWith(".json.tmp", StringComparison.Ordinal)).OrderBy(file => file.EndsWith(".tmp", StringComparison.Ordinal)))
        {
            Entry? entry;
            try
            {
                entry = ReadEntry(file);
                if (entry?.Result == null || string.IsNullOrWhiteSpace(entry.Result.MatchId)
                    || !string.Equals(file, FilePath(entry.Result.MatchId), StringComparison.Ordinal)
                        && !string.Equals(file, FilePath(entry.Result.MatchId) + ".tmp", StringComparison.Ordinal))
                    throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                File.Move(file, Path.Combine(_quarantine, Path.GetFileName(file) + ".corrupt-" + Guid.NewGuid().ToString("N")));
                _storageError("CorruptOutboxEntry");
                continue;
            }
            if (_entries.ContainsKey(entry.Result.MatchId))
            {
                // 원자적 교체 전에 남은 임시 파일보다 확정 파일을 우선한다.
                File.Delete(file);
                continue;
            }
            if (file.EndsWith(".tmp", StringComparison.Ordinal)) File.Move(file, FilePath(entry.Result.MatchId));
            entry.MatchId = entry.Result.MatchId;
            entry.StoredLength = new FileInfo(FilePath(entry.MatchId)).Length;
            entry.Result = null;
            entry.Persisted = true;
            _entries.Add(entry.MatchId, entry);
        }
    }

    private static Entry ReadEntry(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MAX_FILE_BYTES) throw new InvalidDataException();
        var entry = JsonSerializer.Deserialize<Entry>(stream, GameplayWire.Json);
        if (entry?.Result == null || string.IsNullOrWhiteSpace(entry.Result.MatchId)) throw new JsonException();
        return entry;
    }

    private MatchResultRequest ReadResult(Entry entry)
    {
        var result = ReadEntry(FilePath(entry.MatchId)).Result!;
        if (result.MatchId != entry.MatchId) throw new JsonException();
        return result;
    }

    private void Persist(Entry entry)
    {
        string destination = FilePath(entry.MatchId);
        var persisted = new Entry
        {
            Result = entry.Result ?? ReadResult(entry), Failures = entry.Failures, NextAttemptUtc = entry.NextAttemptUtc
        };
        using (var file = new FileStream(destination + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, persisted, GameplayWire.Json);
            file.Flush(true);
        }
        File.Move(destination + ".tmp", destination, true);
        entry.StoredLength = new FileInfo(destination).Length;
        entry.Result = null;
        entry.Persisted = true;
    }

    private void TryPersist(Entry entry)
    {
        try { Persist(entry); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            entry.Persisted = false;
            _storageError(exception.GetType().Name);
            if (entry.Result == null) RemoveUnreadable(entry, exception);
        }
    }

    private void RemoveUnreadable(Entry entry, Exception exception)
    {
        if (exception is not (JsonException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)) return;
        string file = FilePath(entry.MatchId);
        try
        {
            if (File.Exists(file)) File.Move(file, Path.Combine(_quarantine, Path.GetFileName(file) + ".corrupt-" + Guid.NewGuid().ToString("N")));
            _entries.Remove(entry.MatchId);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { _storageError(failure.GetType().Name); }
    }

    public bool CanRecord(int capacity)
    {
        lock (_gate) return _entries.Count < capacity && _entries.Values.All(entry => entry.Persisted);
    }

    public void Enqueue(MatchResultRequest result)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(result.MatchId)) return;
            var entry = new Entry { Result = result, MatchId = result.MatchId };
            _entries.Add(result.MatchId, entry);
            TryPersist(entry);
        }
    }

    public MatchResultRequest[] Due(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(entry => !entry.Persisted).ToArray()) TryPersist(entry);
            var results = new List<MatchResultRequest>();
            long selectedBytes = 0;
            foreach (var entry in _entries.Values.Where(entry => entry.Persisted && entry.NextAttemptUtc <= now)
                .OrderBy(entry => entry.NextAttemptUtc).Take(64).ToArray())
            {
                if (results.Count != 0 && entry.StoredLength > MAX_BATCH_BYTES - selectedBytes) break;
                try
                {
                    results.Add(ReadResult(entry));
                    selectedBytes += entry.StoredLength;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    entry.Persisted = false;
                    _storageError(exception.GetType().Name);
                    RemoveUnreadable(entry, exception);
                }
                if (selectedBytes >= MAX_BATCH_BYTES) break;
            }
            return results.ToArray();
        }
    }

    public void Acknowledge(string matchId)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(matchId)) return;
            File.Delete(FilePath(matchId));
            File.Delete(FilePath(matchId) + ".tmp");
            _entries.Remove(matchId);
        }
    }

    public void Retry(string matchId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(matchId, out var entry)) return;
            entry.Failures = Math.Min(entry.Failures + 1, 4);
            entry.NextAttemptUtc = now.AddSeconds(Math.Min(60, 5 * (1 << entry.Failures)));
            TryPersist(entry);
        }
    }

    public void Quarantine(string matchId, int status)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(matchId)) return;
            File.Move(FilePath(matchId), Path.Combine(_quarantine, Path.GetFileName(FilePath(matchId)) + ".http" + status), true);
            File.Delete(FilePath(matchId) + ".tmp");
            _entries.Remove(matchId);
        }
    }

    public void Dispose() => _lease.Dispose();
}
