namespace DrawLiar.DedicatedServer;

internal sealed partial class DedicatedRoom
{
    private const int REPLAY_BATCH_SIZE = 256;
    private const int HISTORY_BATCH_SIZE = REPLAY_BATCH_SIZE;
    private const int MAX_PENDING_ADMISSIONS = 128;
    private readonly object _gate = new();
    private readonly GameSession _session;
    private readonly GameData _builtInData;
    private ServerTopicData[] _customTopics;
    private readonly Dictionary<string, int> _playerIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GameConnection> _connections = new(StringComparer.Ordinal);
    private readonly List<DrawStroke> _canvas = new();
    private readonly List<DrawStroke> _drawingHistory = new();
    private int _drawingEpoch;
    private readonly HashSet<string> _admittedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _reconnectUntil = new(StringComparer.Ordinal);
    private readonly Action _statusChanged;
    private readonly Func<bool> _canRecordMatch;
    private string _ownerAccountId;
    private int _nextPlayerId = 1;
    private long _sequence;
    private bool _dirty, _closeRequested;
    private double _lastSnapshot, _closeAfter;
    private bool _configurationPending, _configurationSyncRequired;
    private bool _kickPending;
    private string _pendingKickAccountId = "";
    private readonly HashSet<string> _kickedAccountIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, int Target)> _kickReceipts = new(StringComparer.Ordinal);
    private long _configurationVersion, _accessVersion;
    private RoomConfigurationData? _deferredConfiguration;

    public string RoomId { get; }

    public DedicatedRoom(ServerRoomData room, GameData data, ServerTopicData[] customTopics, double now, Action statusChanged,
        Func<bool>? canRecordMatch = null, Action<MatchResultRequest>? matchCompleted = null)
    {
        _statusChanged = statusChanged;
        _canRecordMatch = canRecordMatch ?? (() => true);
        _builtInData = data;
        _customTopics = DrawLiar.Server.ServerDatabase.MergeCustomTopics(null, customTopics);
        RoomId = room.RoomId;
        _ownerAccountId = room.OwnerAccountId;
        _configurationVersion = room.ConfigurationVersion;
        _accessVersion = room.AccessVersion;
        string nodeId = room.NodeId;
        _closeAfter = now + 120;
        var settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettings>(
            System.Text.Json.JsonSerializer.Serialize(room.Settings, GameplayWire.Json), GameplayWire.Json)!;
        if (settings.Topics == null || settings.Topics.Length == 0)
            settings.Topics = data.Topics.Select(topic => topic.Name).ToArray();
        var roomData = RoomGameData(_customTopics);
        _session = new GameSession(settings, roomData);
        _session.MatchCompleted += completed =>
        {
            var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            matchCompleted?.Invoke(new MatchResultRequest
            {
                NodeId = nodeId, RoomId = RoomId, MatchId = completed.MatchId, PlayedAt = DateTimeOffset.UtcNow.ToString("O"),
                Mode = (int)completed.Mode, RoundCount = completed.RoundCount,
                Players = completed.Players.Select(player => new MatchPlayerResult
                {
                    AccountId = accounts[player.PlayerId], Score = player.Score, Rank = player.Rank, Won = player.Won,
                    RoundsPlayed = player.RoundsPlayed, CitizenRounds = player.CitizenRounds, LiarRounds = player.LiarRounds,
                    CorrectVotes = player.CorrectVotes, CorrectGuesses = player.CorrectGuesses,
                    WeightedRoundParticipants = player.WeightedRoundParticipants, WeightedRoundScore = player.WeightedRoundScore
                }).ToArray()
            });
        };
        _session.Changed += () => _dirty = true;
        _session.CanvasCleared += ClearCanvasLocked;
    }

    private GameData RoomGameData(ServerTopicData[] customTopics) => new()
    {
        Scoring = _builtInData.Scoring,
        Topics = _builtInData.Topics.Select(topic => new TopicData { Name = topic.Name, Words = topic.Words.ToArray() })
            .Concat(customTopics.Select(topic => new TopicData { Name = topic.Name, Words = topic.Words.ToArray() })).ToArray()
    };

    public bool Join(GameConnection connection, RedeemTicketResponse ticket, double now)
    {
        lock (_gate)
        {
            if (_admittedIds.Count >= MAX_PENDING_ADMISSIONS || _configurationPending || _configurationSyncRequired || _kickPending
                || _kickedAccountIds.Contains(ticket.AccountId)
                || ticket.Room.AccessVersion != _accessVersion) return false;
            _session.Tick(now);
            if (!_playerIds.TryGetValue(ticket.AccountId, out int playerId))
            {
                playerId = _nextPlayerId++;
                _playerIds[ticket.AccountId] = playerId;
            }
            if (!_session.Contains(playerId) && !_session.Join(playerId, ticket.Profile.DisplayName,
                ticket.Profile.AvatarColor, ticket.Profile.Accessory, ticket.SpectatorOnly, ticket.SpectatorOnly, ticket.Profile.Level)) return false;
            if (_connections.TryGetValue(ticket.AccountId, out var previous)) previous.Abort();
            connection.PlayerId = playerId;
            _connections[ticket.AccountId] = connection;
            if (ticket.AdmissionId.Length > 0) _admittedIds.Add(ticket.AdmissionId);
            _reconnectUntil.Remove(ticket.AccountId);
            _closeRequested = false;
            _session.UpdateProfile(playerId, ticket.Profile.DisplayName, ticket.Profile.AvatarColor, ticket.Profile.Accessory, ticket.Profile.Level);
            EnsureHostLocked();
            QueueReplayLocked(connection, now);
            BroadcastSnapshotsLocked(now);
            _statusChanged();
            return true;
        }
    }

    public void Disconnect(GameConnection connection, double now)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection) return;
            _connections.Remove(connection.AccountId);
            _session.Disconnect(connection.PlayerId, now, !connection.LeftVoluntarily);
            RemoveUnusedPlayerIdsLocked();
            if (connection.LeftVoluntarily) _reconnectUntil.Remove(connection.AccountId);
            else _reconnectUntil[connection.AccountId] = now + 120;
            foreach (string account in _reconnectUntil.Where(pair => pair.Value <= now || pair.Key != connection.AccountId
                && (!_playerIds.TryGetValue(pair.Key, out int id) || !_session.HasParticipant(id))).Select(pair => pair.Key).ToArray())
            {
                if (_reconnectUntil[account] <= now && _playerIds.TryGetValue(account, out int expiredPlayerId)) _session.ReleaseSeat(expiredPlayerId);
                _reconnectUntil.Remove(account);
            }
            if (_connections.Count == 0)
            {
                _closeAfter = _reconnectUntil.Values.DefaultIfEmpty(now).Max();
                _closeRequested = now >= _closeAfter;
            }
            EnsureHostLocked();
            BroadcastSnapshotsLocked(now);
            _statusChanged();
        }
    }

    public void Receive(GameConnection connection, GameplayEnvelope envelope, double now)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection || !connection.IsAlive) return;
            if (!connection.AcceptRate(envelope.Type ?? "", now))
            {
                if (connection.FrameRateExceeded) connection.Abort();
                else if (envelope.Type == "request" && envelope.Kind == "authorDrawing") AuthorDrawingUnavailableLocked(connection, envelope, "RateLimited");
                else if (envelope.Type == "request" && envelope.Kind == "clearOwn") NoticeLocked(connection, "지금은 이 요청을 처리할 수 없어요.");
                return;
            }
            if (envelope.Type == "stroke")
            {
                _session.Tick(now);
                if (_dirty) BroadcastSnapshotsLocked(now);
                if (_session.Phase != GamePhase.Drawing || _session.ArtistId != connection.PlayerId
                    || !GameRules.ValidStroke(envelope.Stroke, _session.CanvasVersion)) return;
                if (_canvas.Count >= GameRules.MAX_CANVAS_STROKES || _drawingHistory.Count >= GameRules.MAX_ROUND_STROKES)
                {
                    NoticeLocked(connection, "이 캔버스에 더 이상 선을 추가할 수 없어요. 차례를 마쳐 주세요.");
                    return;
                }
                var stroke = envelope.Stroke;
                stroke.AuthorPlayerId = connection.PlayerId;
                if (stroke.Eraser) { stroke.R = stroke.G = stroke.B = 255; stroke.Eraser = false; }
                _canvas.Add(stroke);
                _drawingHistory.Add(stroke);
                BroadcastLocked(new GameplayEnvelope { Type = "stroke", Stroke = stroke, Round = _session.Round, DrawingEpoch = _drawingEpoch });
                return;
            }
            if (envelope.Type != "request") return;
            if (envelope.Kind == "authorDrawing")
            {
                _session.Tick(now);
                if (_dirty) BroadcastSnapshotsLocked(now);
                QueueAuthorDrawingLocked(connection, envelope, now);
                return;
            }
            bool host = _ownerAccountId == connection.AccountId;
            bool accepted = envelope.Kind switch
            {
                "start" => StartLocked(host, now),
                "lobby" => ReturnToLobbyLocked(host),
                "endTurn" => _session.EndTurn(connection.PlayerId, now),
                "clearOwn" => ClearOwnLocked(connection, envelope, now),
                "vote" => envelope.BallotVersion == _session.BallotVersion && _session.Vote(connection.PlayerId, envelope.Target, now),
                "judge" => envelope.BallotVersion == _session.BallotVersion
                    && _session.Judge(connection.PlayerId, envelope.Target, envelope.Approve, now),
                "guess" => _session.Guess(connection.PlayerId, envelope.Text, now),
                "chat" => ChatLocked(connection, envelope.Text, now),
                _ => false
            };
            if (!accepted) NoticeLocked(connection, "지금은 이 요청을 처리할 수 없어요.");
            RemoveUnusedPlayerIdsLocked();
            if (_dirty) BroadcastSnapshotsLocked(now);
        }
    }

    public bool Tick(double now)
    {
        lock (_gate)
        {
            _session.Tick(now);
            bool seatsReleased = false;
            foreach (var account in _reconnectUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                if (_playerIds.TryGetValue(account, out int playerId)) seatsReleased |= _session.ReleaseSeat(playerId);
                _reconnectUntil.Remove(account);
            }
            if (seatsReleased) _statusChanged();
            EnsureHostLocked();
            if (_dirty || now - _lastSnapshot >= 1) BroadcastSnapshotsLocked(now);
            if (!_closeRequested && _connections.Count == 0 && now >= _closeAfter)
            {
                _closeRequested = true;
                _statusChanged();
            }
            return _closeRequested;
        }
    }

    public RoomStatusData Status()
    {
        lock (_gate)
        {
            return new RoomStatusData
            {
                RoomId = RoomId, OwnerAccountId = _ownerAccountId,
                PlayerCount = _session.ActiveCount, SpectatorCount = _session.SpectatorCount,
                IsInProgress = _session.Phase != GamePhase.Lobby && _session.Phase != GamePhase.MatchResults,
                PlayerAccountIds = _playerIds.Where(pair => _session.HoldsPlayerSeat(pair.Value))
                    .Select(pair => pair.Key).ToArray(),
                SpectatorAccountIds = _playerIds.Where(pair => _session.HoldsSpectatorSeat(pair.Value))
                    .Select(pair => pair.Key).ToArray(),
                AdmissionIds = _admittedIds.ToArray(),
                Settings = System.Text.Json.JsonSerializer.Deserialize<ServerRoomSettings>(
                    System.Text.Json.JsonSerializer.Serialize(_session.Settings, GameplayWire.Json), GameplayWire.Json)!,
                Closed = _closeRequested,
                ConfigurationVersion = _configurationSyncRequired ? -1 : _configurationVersion
            };
        }
    }

    public void AcknowledgeAdmissions(string[] admissionIds)
    {
        lock (_gate) _admittedIds.ExceptWith(admissionIds);
    }

    public GameConnection[] Connections()
    {
        lock (_gate) return _connections.Values.Where(connection => connection.IsAlive).ToArray();
    }

    public void AbortAll()
    {
        lock (_gate)
            foreach (var connection in _connections.Values) connection.Abort();
    }

    private void EnsureHostLocked()
    {
        if (_configurationPending || _kickPending) return;
        if (_connections.TryGetValue(_ownerAccountId, out var owner) && owner.IsAlive) return;
        var candidate = _connections.Values.Where(connection => connection.IsAlive)
            .OrderBy(connection => _session.Snapshot(connection.PlayerId, -1, 0).LocalIsSpectator)
            .ThenBy(connection => connection.PlayerId).FirstOrDefault();
        if (candidate != null && candidate.AccountId != _ownerAccountId)
        {
            _ownerAccountId = candidate.AccountId;
            _dirty = true;
        }
    }

    private bool StartLocked(bool host, double now)
    {
        if (!host || _configurationPending || _configurationSyncRequired || !_canRecordMatch()) return false;
        foreach (var connection in _connections.Values.Where(connection => !connection.IsAlive).ToArray())
            Disconnect(connection, now);
        return _connections.Values.Count(connection => connection.IsAlive && !_session.IsSpectator(connection.PlayerId))
            >= GameRules.MinimumPlayers(_session.Settings.LiarMode) && _session.Start(now);
    }

    private bool ReturnToLobbyLocked(bool host)
    {
        if (!host || _configurationPending || _configurationSyncRequired || _session.Phase != GamePhase.MatchResults) return false;
        _session.ReturnToLobby();
        RemoveUnusedPlayerIdsLocked();
        return true;
    }

    private void RemoveUnusedPlayerIdsLocked()
    {
        foreach (var pair in _playerIds.Where(pair => !_session.HasParticipant(pair.Value)).ToArray())
            _playerIds.Remove(pair.Key);
    }

    private bool ChatLocked(GameConnection connection, string? text, double now)
    {
        text = GameRules.CleanText(text, 160);
        if (text.Length == 0 || !connection.AcceptChat(now)) return false;
        var line = new ChatLine { PlayerId = connection.PlayerId, Name = _session.PlayerName(connection.PlayerId), Text = text, Level = _session.PlayerLevel(connection.PlayerId) };
        var envelope = new GameplayEnvelope { Type = "chat", Line = line, Sequence = ++_sequence };
        bool spectatorOnly = _session.Phase != GamePhase.Lobby && _session.Phase != GamePhase.MatchResults
            && _session.Snapshot(connection.PlayerId, -1, now).LocalIsSpectator;
        foreach (var target in _connections.Values)
            if (!spectatorOnly || _session.Snapshot(target.PlayerId, -1, now).LocalIsSpectator) target.Queue(envelope);
        return true;
    }

    private void BroadcastSnapshotsLocked(double now)
    {
        int hostId = _playerIds.TryGetValue(_ownerAccountId, out int id) ? id : -1;
        var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        long sequence = ++_sequence;
        foreach (var connection in _connections.Values)
        {
            var snapshot = _session.Snapshot(connection.PlayerId, hostId, now);
            snapshot.CanStart &= !_configurationPending && !_configurationSyncRequired;
            foreach (var player in snapshot.Players)
                player.AccountId = accounts.TryGetValue(player.Id, out string? accountId) ? accountId : "";
            connection.Queue(new GameplayEnvelope
            {
                Type = "state", State = snapshot, Sequence = sequence
            });
        }
        _lastSnapshot = now;
        _dirty = false;
    }

    private void NoticeLocked(GameConnection connection, string text) => connection.Queue(new GameplayEnvelope
    {
        Type = "notice", Text = text, Sequence = ++_sequence
    });

    private void BroadcastLocked(GameplayEnvelope envelope)
    {
        envelope.Sequence = ++_sequence;
        foreach (var connection in _connections.Values) connection.Queue(envelope);
    }
}
