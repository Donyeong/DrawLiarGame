using DrawLiar.Server;

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
    private readonly List<RoundDrawingArchiveData> _roundDrawings = new();
    private long _roundDrawingBytes;
    private int _lastCapturedRound;
    private bool _drawingsIncomplete;
    private int _drawingEpoch;
    private readonly HashSet<string> _admittedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _reconnectUntil = new(StringComparer.Ordinal);
    private readonly Action _statusChanged;
    private readonly Func<bool> _canRecordMatch;
    private readonly Action<RoomActivityData> _activity;
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
        Func<bool>? canRecordMatch = null, Action<MatchResultRequest>? matchCompleted = null, Action<RoomActivityData>? activity = null)
    {
        _statusChanged = statusChanged;
        _canRecordMatch = canRecordMatch ?? (() => true);
        _activity = activity ?? (_ => { });
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
        settings.TopicLanguage = ServerRoomSettings.NormalizeTopicLanguage(settings.TopicLanguage);
        var roomData = RoomGameData(_customTopics, settings.TopicLanguage);
        if (settings.Topics == null || settings.Topics.Length == 0)
            settings.Topics = roomData.Topics.Select(topic => topic.Name).ToArray();
        _session = new GameSession(settings, roomData);
        _session.MatchCompleted += completed =>
        {
            var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            matchCompleted?.Invoke(new MatchResultRequest
            {
                NodeId = nodeId, RoomId = RoomId, MatchId = completed.MatchId, PlayedAt = DateTimeOffset.UtcNow.ToString("O"),
                Mode = (int)completed.Mode, RoundCount = completed.RoundCount, HostAccountId = _ownerAccountId,
                DrawingArchives = _roundDrawings.ToArray(), DrawingsIncomplete = _drawingsIncomplete,
                Players = completed.Players.Select(player => new MatchPlayerResult
                {
                    AccountId = accounts[player.PlayerId], Score = player.Score, Rank = player.Rank, Won = player.Won,
                    RoundsPlayed = player.RoundsPlayed, CitizenRounds = player.CitizenRounds, LiarRounds = player.LiarRounds,
                    CorrectVotes = player.CorrectVotes, CorrectGuesses = player.CorrectGuesses,
                    WeightedRoundParticipants = player.WeightedRoundParticipants, WeightedRoundScore = player.WeightedRoundScore
                }).ToArray()
            });
            _roundDrawings.Clear();
        };
        _session.Changed += SessionChangedLocked;
        _session.CanvasCleared += ClearCanvasLocked;
    }

    // 방의 주제 언어에 맞는 기본 주제와 방의 사용자 주제만 담는다. 상태의 주제 목록도 이 범위로 줄어든다.
    private GameData RoomGameData(ServerTopicData[] customTopics, string topicLanguage) => new()
    {
        Scoring = _builtInData.Scoring,
        Topics = _builtInData.Topics.Where(topic => ServerRoomSettings.TopicLanguageOf(topic.LanguageCode) == topicLanguage)
            .Select(topic => new TopicData { Name = topic.Name, Words = topic.Words.ToArray(), Aliases = topic.Aliases })
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
                ticket.Profile.AvatarColor, ticket.Profile.Accessory, ticket.SpectatorOnly, ticket.SpectatorOnly, ticket.Profile.Level,
                ticket.Profile.Cosmetics)) return false;
            if (_connections.TryGetValue(ticket.AccountId, out var previous)) previous.Abort();
            connection.PlayerId = playerId;
            connection.SubscriptionExpiresAt = ticket.Profile.SubscriptionExpiresAt ?? "";
            _connections[ticket.AccountId] = connection;
            if (ticket.AdmissionId.Length > 0) _admittedIds.Add(ticket.AdmissionId);
            _reconnectUntil.Remove(ticket.AccountId);
            _closeRequested = false;
            _session.UpdateProfile(playerId, ticket.Profile.DisplayName, ticket.Profile.AvatarColor, ticket.Profile.Accessory,
                ticket.Profile.Level, ticket.Profile.Cosmetics);
            EnsureHostLocked();
            QueueReplayLocked(connection, now);
            BroadcastSnapshotsLocked(now);
            _statusChanged();
            _activity(new RoomActivityData("RoomEnter", RoomId, ticket.AccountId, Spectator: _session.IsSpectator(playerId)));
            return true;
        }
    }

    public void Disconnect(GameConnection connection, double now)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection) return;
            _connections.Remove(connection.AccountId);
            _activity(new RoomActivityData("RoomLeave", RoomId, connection.AccountId, Voluntary: connection.LeftVoluntarily));
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
                if (!_session.CanDraw(connection.PlayerId) || !GameRules.ValidStroke(envelope.Stroke, _session.CanvasVersion)) return;
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
                "guess" => GuessLocked(connection, envelope.Text, now),
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
        if (_connections.Values.Count(connection => connection.IsAlive && !_session.IsSpectator(connection.PlayerId))
            < GameRules.MinimumPlayers(_session.Settings.LiarMode) || !_session.Start(now)) return false;
        _activity(new RoomActivityData("MatchStart", RoomId, _ownerAccountId, _session.Snapshot(-1, -1, now).MatchId));
        return true;
    }

    // 정답 입력은 채팅 필터를 적용하지 않은 원문으로 남긴다.
    private bool GuessLocked(GameConnection connection, string? text, double now)
    {
        if (!_session.Guess(connection.PlayerId, text, now)) return false;
        _activity(new RoomActivityData("RoomGuess", RoomId, connection.AccountId, Text: GameRules.CleanText(text, 40)));
        return true;
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
        var lobbyAuthors = _session.Phase == GamePhase.Lobby ? _canvas.Select(stroke => stroke.AuthorPlayerId).ToHashSet() : null;
        foreach (var pair in _playerIds.Where(pair => !_session.HasParticipant(pair.Value) && lobbyAuthors?.Contains(pair.Value) != true).ToArray())
            _playerIds.Remove(pair.Key);
    }

    private bool ChatLocked(GameConnection connection, string? text, double now)
    {
        text = GameRules.CleanText(text, 160);
        if (text.Length == 0 || !connection.AcceptChat(now)) return false;
        var line = new ChatLine { PlayerId = connection.PlayerId, Name = _session.PlayerName(connection.PlayerId), Text = ChatProfanityFilter.Mask(text),
            Level = _session.PlayerLevel(connection.PlayerId), Cosmetics = _session.PlayerCosmetics(connection.PlayerId) };
        _activity(new RoomActivityData("RoomChat", RoomId, connection.AccountId, Text: line.Text));
        var envelope = new GameplayEnvelope { Type = "chat", Line = line, Sequence = ++_sequence };
        bool spectatorOnly = _session.Phase != GamePhase.Lobby && _session.Phase != GamePhase.MatchResults
            && _session.Snapshot(connection.PlayerId, -1, now).LocalIsSpectator;
        foreach (var target in _connections.Values)
            if (!spectatorOnly || _session.Snapshot(target.PlayerId, -1, now).LocalIsSpectator) target.Queue(envelope);
        return true;
    }

    public void ValidateConfigurationStateSize(ConfigureRoomRequest request)
    {
        lock (_gate)
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<RoomSettings>(
                System.Text.Json.JsonSerializer.Serialize(request.Settings, GameplayWire.Json), GameplayWire.Json)!;
            settings.Validate();
            var customTopics = DrawLiar.Server.ServerDatabase.MergeCustomTopics(_customTopics, request.CustomTopics);
            var data = RoomGameData(customTopics, settings.TopicLanguage);
            ValidateStateSize(settings, data.Topics.Where(topic => !string.IsNullOrWhiteSpace(topic.Name)
                && topic.Words != null && topic.Words.Any(word => !string.IsNullOrWhiteSpace(word))).Select(topic => topic.Name));
        }
    }

    public static void ValidateStateSize(RoomSettings settings, IEnumerable<string> availableTopicNames)
    {
        // 관전·재입장으로 남는 경기 참가자까지 GameSession의 64명 상한을 예약한다.
        const int MAX_RECORDED_PLAYERS = 64;
        string escapedWord = new('\'', 40);
        var cosmetics = new CosmeticLoadout();
        foreach (string category in CosmeticRules.Categories)
        {
            string longest = CosmeticRules.CreateProducts().Where(product => product.Category == category)
                .OrderByDescending(product => product.Id.Length).Select(product => product.Id).FirstOrDefault() ?? "";
            cosmetics = CosmeticRules.SetEquipped(cosmetics, category, longest);
        }
        var snapshot = new RoomSnapshot
        {
            Settings = settings.Copy(), AvailableTopics = availableTopicNames.Distinct(StringComparer.Ordinal).ToArray(),
            MatchId = new string('f', 36), Phase = GamePhase.MatchResults, Round = int.MaxValue,
            ArtistId = int.MaxValue, Topic = escapedWord, Word = escapedWord, MismatchWord = escapedWord,
            LocalPlayerId = int.MaxValue, HostPlayerId = int.MaxValue, CanvasVersion = int.MaxValue,
            DrawingEpoch = int.MaxValue, BallotVersion = int.MaxValue, AccusedPlayerId = int.MaxValue,
            LocalVoteTargetId = int.MaxValue, RemainingSeconds = float.MaxValue,
            Summary = new string('\'', 160), DrawingOrder = Enumerable.Repeat(int.MaxValue, MAX_RECORDED_PLAYERS).ToArray(),
            Winners = Enumerable.Repeat(int.MaxValue, MAX_RECORDED_PLAYERS).ToArray(),
            Players = Enumerable.Range(0, MAX_RECORDED_PLAYERS).Select(index => new PlayerView
            {
                Id = int.MaxValue, AccountId = new string('f', 36), Name = new string('\'', 16), Level = int.MaxValue,
                Score = int.MinValue, RoundPoints = int.MinValue, VoteCount = int.MaxValue, Accessory = long.MaxValue,
                Cosmetics = cosmetics, Guess = index < GameRules.MAX_PLAYERS - 1 ? escapedWord : "",
                GuessOutcome = GuessOutcome.Unanswered
            }).ToArray()
        };
        if (GameConnection.Serialize(new GameplayEnvelope { Type = "state", State = snapshot, Sequence = long.MaxValue }).Length
            > GameplayWire.MAX_FRAME_BYTES)
            throw new DrawLiar.Server.ApiException("RoomStateTooLarge");
    }

    private void BroadcastSnapshotsLocked(double now)
    {
        int hostId = _playerIds.TryGetValue(_ownerAccountId, out int id) ? id : -1;
        var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        bool hostPainterPass = _connections.TryGetValue(_ownerAccountId, out var host)
            && CommerceRules.IsActive(host.SubscriptionExpiresAt, DateTimeOffset.UtcNow);
        long sequence = ++_sequence;
        foreach (var connection in _connections.Values)
        {
            var snapshot = _session.Snapshot(connection.PlayerId, hostId, now);
            snapshot.CanStart &= !_configurationPending && !_configurationSyncRequired;
            snapshot.HostHasPainterPass = hostPainterPass;
            foreach (var player in snapshot.Players)
                player.AccountId = accounts.TryGetValue(player.Id, out string? accountId) ? accountId : "";
            QueueSnapshotLocked(connection, snapshot, sequence);
        }
        _lastSnapshot = now;
        _dirty = false;
    }

    private void QueueSnapshotLocked(GameConnection connection, RoomSnapshot snapshot, long sequence)
    {
        byte[]? bytes = SerializeState(snapshot, sequence, out bool reduced);
        if (bytes != null && connection.QueueSerialized(bytes) && reduced && !connection.StateSizeNoticeSent)
        {
            connection.StateSizeNoticeSent = true;
            NoticeLocked(connection, "방 설정이 커 일부 주제 목록만 표시합니다. 주제 수를 줄여 다시 저장해 주세요.");
        }
    }

    internal static byte[]? SerializeState(RoomSnapshot snapshot, long sequence, out bool reduced)
    {
        var envelope = new GameplayEnvelope { Type = "state", State = snapshot, Sequence = sequence };
        byte[] bytes = GameConnection.Serialize(envelope);
        reduced = bytes.Length > GameplayWire.MAX_FRAME_BYTES;
        if (reduced)
        {
            // 기존 방은 전송 복사본의 주제 목록만 줄여 게임 진행 상태를 계속 전달한다.
            string[] names = snapshot.AvailableTopics;
            snapshot.AvailableTopics = [];
            bytes = GameConnection.Serialize(envelope);
            if (bytes.Length > GameplayWire.MAX_FRAME_BYTES)
            {
                names = snapshot.Settings.Topics ?? [];
                snapshot.Settings = snapshot.Settings.Copy();
                snapshot.Settings.Topics = [];
                bytes = GameConnection.Serialize(envelope);
                if (bytes.Length > GameplayWire.MAX_FRAME_BYTES) return null;
                bytes = FitTopicPrefix(names, topics => snapshot.Settings.Topics = topics, bytes);
            }
            else bytes = FitTopicPrefix(names, topics => snapshot.AvailableTopics = topics, bytes);
        }
        return bytes;

        byte[] FitTopicPrefix(string[] names, Action<string[]> assign, byte[] minimum)
        {
            int lower = 0, upper = names.Length;
            byte[] result = minimum;
            while (lower < upper)
            {
                int count = lower + (upper - lower + 1) / 2;
                assign(names.Take(count).ToArray());
                byte[] candidate = GameConnection.Serialize(envelope);
                if (candidate.Length <= GameplayWire.MAX_FRAME_BYTES) { lower = count; result = candidate; }
                else upper = count - 1;
            }
            return result;
        }
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
