using System.Diagnostics;

namespace DrawLiar.DedicatedServer;

internal sealed class RoomRegistry : BackgroundService
{
    private readonly object _gate = new();
    private const int MAX_PENDING_RESULTS = 2048;
    private readonly object _resultGate = new();
    private readonly Dictionary<string, MatchResultRequest> _pendingResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DedicatedRoom> _rooms = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _statusChanged = new(0, 1);
    private readonly GameData _data;
    private readonly int _capacity;
    private readonly string _nodeId;

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public RoomRegistry(IConfiguration configuration)
    {
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
                    .Where(word => word.Length > 0).Distinct().Take(200).ToArray()
            }).Where(topic => topic.Name.Length > 0 && topic.Words.Length > 0).Take(128).ToArray();
        if (_data.Topics.Length == 0) throw new InvalidDataException("게임 주제 데이터가 비어 있습니다.");
        _data.Scoring ??= new ScoreRules();
        _data.Scoring.LiarUncaught = Math.Clamp(_data.Scoring.LiarUncaught, 0, 1000);
        _data.Scoring.LiarCorrectGuess = Math.Clamp(_data.Scoring.LiarCorrectGuess, 0, 1000);
        _data.Scoring.CitizenCorrectVote = Math.Clamp(_data.Scoring.CitizenCorrectVote, 0, 1000);
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
                    NotifyStatusChanged, CanRecordMatch, QueueResult);
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
        lock (_resultGate) return _pendingResults.Count < MAX_PENDING_RESULTS - _capacity;
    }

    private void QueueResult(MatchResultRequest result)
    {
        result.NodeId = _nodeId;
        lock (_resultGate) _pendingResults.TryAdd(result.MatchId, result);
        NotifyStatusChanged();
    }

    public MatchResultRequest[] PendingResults()
    {
        lock (_resultGate) return _pendingResults.Values.Take(64).ToArray();
    }

    public void AcknowledgeResult(string matchId)
    {
        lock (_resultGate) _pendingResults.Remove(matchId);
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
}
