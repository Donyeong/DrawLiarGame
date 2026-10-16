namespace DrawLiar.DedicatedServer;

internal sealed partial class DedicatedRoom
{
    private void SessionChangedLocked()
    {
        _dirty = true;
        if (_session.Phase == GamePhase.Lobby || _session.Phase == GamePhase.RoleReveal && _session.Round == 1)
        {
            _roundDrawings.Clear();
            _roundDrawingBytes = 0;
            _lastCapturedRound = 0;
            _drawingsIncomplete = false;
            return;
        }
        if (_session.Phase != GamePhase.RoundResults || _lastCapturedRound >= _session.Round) return;
        _lastCapturedRound = _session.Round;
        if (_session.Round > DrawingReplayRules.MAX_ROUNDS || _roundDrawingBytes >= DrawingReplayRules.MAX_COMPRESSED_MATCH_BYTES)
        {
            _drawingsIncomplete = true;
            return;
        }
        var state = _session.Snapshot(-1, -1, 0);
        var source = _session.Settings.Mode == DrawingMode.Individual ? _drawingHistory : _canvas;
        var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        var drawing = new RoundDrawingData
        {
            Round = state.Round, Topic = state.Topic, Word = state.Word,
            Players = state.DrawingOrder.Select(id => new ReplayPlayerData
            {
                PlayerId = id, AccountId = accounts.GetValueOrDefault(id, ""), DisplayName = _session.PlayerName(id)
            }).ToArray(),
            Strokes = source.Select(stroke => new ReplayStrokeData
            {
                X1 = stroke.X1, Y1 = stroke.Y1, X2 = stroke.X2, Y2 = stroke.Y2,
                Size = stroke.Size, StartSize = stroke.StartSize, R = stroke.R, G = stroke.G, B = stroke.B,
                Eraser = stroke.Eraser, AuthorPlayerId = stroke.AuthorPlayerId
            }).ToArray()
        };
        if (!DrawLiar.Server.DrawingArchiveCodec.TryEncode(drawing, out var archive)
            || archive.Payload.LongLength > DrawingReplayRules.MAX_COMPRESSED_MATCH_BYTES - _roundDrawingBytes)
        {
            _drawingsIncomplete = true;
            return;
        }
        _roundDrawings.Add(archive);
        _roundDrawingBytes += archive.Payload.LongLength;
    }

    private void ClearCanvasLocked()
    {
        if (_drawingEpoch != _session.DrawingEpoch)
        {
            _drawingHistory.Clear();
            _drawingEpoch = _session.DrawingEpoch;
        }
        _canvas.Clear();
        BroadcastLocked(new GameplayEnvelope
        {
            Type = "canvas", Version = _session.CanvasVersion, Round = _session.Round, DrawingEpoch = _drawingEpoch,
            Reset = true, Complete = true, Strokes = Array.Empty<DrawStroke>()
        });
    }

    private bool ClearOwnLocked(GameConnection connection, GameplayEnvelope envelope, double now)
    {
        _session.Tick(now);
        if (!_session.CanDraw(connection.PlayerId) || envelope.Version != _session.CanvasVersion
            || envelope.Round != _session.Round || envelope.DrawingEpoch != _drawingEpoch
            || envelope.Target != 0 && envelope.Target != connection.PlayerId) return false;
        int removed = _canvas.RemoveAll(stroke => stroke.AuthorPlayerId == connection.PlayerId);
        if (removed == 0) return true;
        _drawingHistory.RemoveAll(stroke => stroke.AuthorPlayerId == connection.PlayerId && stroke.CanvasVersion == _session.CanvasVersion);
        BroadcastLocked(new GameplayEnvelope
        {
            Type = "clearOwn", Target = connection.PlayerId, Version = _session.CanvasVersion,
            Round = _session.Round, DrawingEpoch = _drawingEpoch
        });
        return true;
    }

    private void QueueReplayLocked(GameConnection connection, double now)
    {
        if (_canvas.Count == 0)
            QueueCanvas(Array.Empty<DrawStroke>(), true, true);
        else
            for (int index = 0; index < _canvas.Count; index += REPLAY_BATCH_SIZE)
                QueueCanvas(_canvas.GetRange(index, Math.Min(REPLAY_BATCH_SIZE, _canvas.Count - index)).ToArray(), index == 0, index + REPLAY_BATCH_SIZE >= _canvas.Count);
        int hostId = _playerIds.TryGetValue(_ownerAccountId, out int id) ? id : -1;
        var snapshot = _session.Snapshot(connection.PlayerId, hostId, now);
        snapshot.CanStart &= !_configurationPending && !_configurationSyncRequired;
        var accounts = _playerIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        foreach (var player in snapshot.Players) player.AccountId = accounts.TryGetValue(player.Id, out string? account) ? account : "";
        connection.Queue(new GameplayEnvelope { Type = "state", State = snapshot, Sequence = ++_sequence });
        void QueueCanvas(DrawStroke[] strokes, bool reset, bool complete) => connection.Queue(new GameplayEnvelope
        {
            Type = "canvas", Version = _session.CanvasVersion, Round = _session.Round, DrawingEpoch = _drawingEpoch,
            Reset = reset, Complete = complete, Strokes = strokes, Sequence = ++_sequence
        });
    }

    private void QueueAuthorDrawingLocked(GameConnection connection, GameplayEnvelope envelope, double now)
    {
        if (envelope.Round != _session.Round || envelope.DrawingEpoch != _drawingEpoch || envelope.Version != _session.CanvasVersion
            || envelope.RequestId == null || envelope.RequestId.Length > 80 || envelope.RequestId.Any(char.IsControl)
            || !_session.Snapshot(connection.PlayerId, -1, now).DrawingOrder.Contains(envelope.Target))
        {
            AuthorDrawingUnavailableLocked(connection, envelope, "DrawingUnavailable");
            return;
        }
        var source = _session.Settings.Mode == DrawingMode.Individual ? _drawingHistory : _canvas;
        var strokes = source.Where(stroke => stroke.AuthorPlayerId == envelope.Target).ToArray();
        if (strokes.Length == 0) Queue(Array.Empty<DrawStroke>(), true, true);
        else
            for (int index = 0; index < strokes.Length; index += HISTORY_BATCH_SIZE)
                Queue(strokes.Skip(index).Take(HISTORY_BATCH_SIZE).ToArray(), index == 0, index + HISTORY_BATCH_SIZE >= strokes.Length);

        void Queue(DrawStroke[] batch, bool reset, bool complete) => connection.Queue(new GameplayEnvelope
        {
            Type = "authorDrawing", Target = envelope.Target, RequestId = envelope.RequestId, Round = _session.Round,
            DrawingEpoch = _drawingEpoch, Version = _session.CanvasVersion, Reset = reset, Complete = complete,
            Strokes = batch, Accepted = true, Sequence = ++_sequence
        });
    }

    private void AuthorDrawingUnavailableLocked(GameConnection connection, GameplayEnvelope envelope, string code) => connection.Queue(new GameplayEnvelope
    {
        Type = "authorDrawing", Target = envelope.Target,
        RequestId = envelope.RequestId != null && envelope.RequestId.Length <= 80 && !envelope.RequestId.Any(char.IsControl) ? envelope.RequestId : "",
        Round = _session.Round, DrawingEpoch = _drawingEpoch, Version = _session.CanvasVersion, Complete = true, Accepted = false,
        Code = code, Strokes = Array.Empty<DrawStroke>(), Sequence = ++_sequence
    });
}
