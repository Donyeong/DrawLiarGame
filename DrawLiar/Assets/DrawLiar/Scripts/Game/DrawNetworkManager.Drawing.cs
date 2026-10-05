using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class DrawNetworkManager
    {
        private sealed class AuthorDrawingCache
        {
            public readonly List<DrawStroke> Strokes = new List<DrawStroke>();
            public readonly IReadOnlyList<DrawStroke> View;
            public int Version;
            public bool Complete;
            public float RetryAfter;
            public AuthorDrawingCache() { View = Strokes.AsReadOnly(); }
        }
        private sealed class AuthorDrawingRequest
        {
            public string Id;
            public float SentAt;
            public bool Started;
        }
        private readonly Dictionary<int, AuthorDrawingCache> _authorDrawings = new Dictionary<int, AuthorDrawingCache>();
        private readonly Dictionary<int, AuthorDrawingRequest> _authorDrawingRequests = new Dictionary<int, AuthorDrawingRequest>();
        private int _drawingEpoch, _drawingRound, _historyStrokeCount;
        private int? _queuedAuthorDrawing;
        private float _nextAuthorRequest;
        private bool _canvasReplayComplete, _allRoundHistoryKnown;

        public event Action<int> AuthorDrawingChanged;

        public IReadOnlyList<DrawStroke> GetAuthorStrokes(int playerId)
        {
            if (State == null || State.DrawingEpoch != _drawingEpoch || State.Round != _drawingRound
                || !_authorDrawings.TryGetValue(playerId, out var drawing)
                || State.Settings.Mode == DrawingMode.Relay && drawing.Version != _canvasVersion)
                return Array.Empty<DrawStroke>();
            return drawing.View;
        }

        public void RequestAuthorDrawing(int playerId)
        {
            if (!IsConnected || State.Round <= 0 || State.DrawingEpoch != _drawingEpoch || State.Round != _drawingRound
                || State.CanvasVersion != _canvasVersion || State.DrawingOrder == null || Array.IndexOf(State.DrawingOrder, playerId) < 0) return;
            _queuedAuthorDrawing = null;
            _authorDrawings.TryGetValue(playerId, out var cached);
            if (_allRoundHistoryKnown || State.Settings.Mode == DrawingMode.Relay && _canvasReplayComplete
                || cached != null && cached.Complete) return;
            if (_authorDrawingRequests.Count > 0)
            {
                if (!_authorDrawingRequests.ContainsKey(playerId)) _queuedAuthorDrawing = playerId;
                return;
            }
            if (Time.unscaledTime < _nextAuthorRequest || cached != null && Time.unscaledTime < cached.RetryAfter)
            {
                _queuedAuthorDrawing = playerId;
                return;
            }
            string requestId = Guid.NewGuid().ToString("N");
            _authorDrawingRequests[playerId] = new AuthorDrawingRequest { Id = requestId, SentAt = Time.unscaledTime };
            Send(new GameplayEnvelope
            {
                Type = "request", Kind = "authorDrawing", Target = playerId, RequestId = requestId,
                Version = _canvasVersion, Round = _drawingRound, DrawingEpoch = _drawingEpoch
            });
        }

        public void CancelQueuedAuthorDrawing() => _queuedAuthorDrawing = null;

        public void ClearOwnStrokes()
        {
            if (!CanDraw || GetAuthorStrokes(LocalPlayerId).Count == 0) return;
            Send(new GameplayEnvelope
            {
                Type = "request", Kind = "clearOwn", Target = LocalPlayerId,
                Version = _canvasVersion, Round = _drawingRound, DrawingEpoch = _drawingEpoch
            });
        }

        private bool CurrentDrawingFrame(GameplayEnvelope message) => message.DrawingEpoch == _drawingEpoch && message.Round == _drawingRound;

        private void ResetDrawingHistory(int epoch, int round, bool observed)
        {
            _authorDrawings.Clear();
            _authorDrawingRequests.Clear();
            _queuedAuthorDrawing = null;
            _nextAuthorRequest = 0;
            _historyStrokeCount = 0;
            _drawingEpoch = epoch;
            _drawingRound = round;
            _canvasReplayComplete = false;
            _allRoundHistoryKnown = observed && round > 0;
            AuthorDrawingChanged?.Invoke(-1);
        }

        private void SynchronizeDrawingState(RoomSnapshot previous)
        {
            if (State == null) return;
            if (State.DrawingEpoch != _drawingEpoch || State.Round != _drawingRound)
            {
                ResetDrawingHistory(State.DrawingEpoch, State.Round, previous != null);
                _canvas.Clear();
                _canvasVersion = State.CanvasVersion;
                CanvasCleared?.Invoke();
            }
            if (State.Phase == GamePhase.RoleReveal && State.CanvasVersion == State.DrawingEpoch) _allRoundHistoryKnown = true;
        }

        private void ReceiveCanvas(GameplayEnvelope message)
        {
            if (message.Version < _canvasVersion || message.DrawingEpoch < _drawingEpoch || message.Version < message.DrawingEpoch || message.Round < 0) return;
            if (message.Reset)
            {
                if (!CurrentDrawingFrame(message)) ResetDrawingHistory(message.DrawingEpoch, message.Round, State != null);
                _canvas.Clear();
                _canvasVersion = message.Version;
                _canvasReplayComplete = false;
                CanvasCleared?.Invoke();
            }
            if (!CurrentDrawingFrame(message) || message.Version != _canvasVersion || message.Strokes == null) return;
            foreach (var stroke in message.Strokes) ReceiveStroke(stroke);
            if (!message.Complete) return;
            _canvasReplayComplete = true;
            foreach (var drawing in _authorDrawings.Values)
                if (drawing.Version == _canvasVersion) drawing.Complete = true;
        }

        private bool AddAuthorStroke(DrawStroke stroke)
        {
            if (_historyStrokeCount >= GameRules.MAX_ROUND_STROKES || stroke.CanvasVersion < _drawingEpoch) return false;
            if (!_authorDrawings.TryGetValue(stroke.AuthorPlayerId, out var drawing))
                _authorDrawings[stroke.AuthorPlayerId] = drawing = new AuthorDrawingCache();
            if (drawing.Strokes.Count >= MAXIMUM_STROKES || drawing.Version != 0 && drawing.Version != stroke.CanvasVersion) return false;
            drawing.Version = stroke.CanvasVersion;
            drawing.Strokes.Add(stroke);
            drawing.Complete |= _canvasReplayComplete && stroke.CanvasVersion == _canvasVersion;
            _historyStrokeCount++;
            return true;
        }

        private void ReceiveClearOwn(GameplayEnvelope message)
        {
            if (!CurrentDrawingFrame(message) || message.Version != _canvasVersion || message.Target <= 0) return;
            _canvas.RemoveAll(stroke => stroke.AuthorPlayerId == message.Target);
            if (_authorDrawings.TryGetValue(message.Target, out var drawing))
            {
                _historyStrokeCount -= drawing.Strokes.RemoveAll(stroke => stroke.CanvasVersion == message.Version);
                drawing.Complete = true;
            }
            CanvasCleared?.Invoke();
            foreach (var stroke in _canvas) StrokeReceived?.Invoke(stroke);
            AuthorDrawingChanged?.Invoke(message.Target);
        }

        private void ReceiveAuthorDrawing(GameplayEnvelope message)
        {
            if (!_authorDrawingRequests.TryGetValue(message.Target, out var request) || request.Id != message.RequestId) return;
            request.SentAt = Time.unscaledTime;
            if (!message.Accepted || !CurrentDrawingFrame(message) || message.Version != _canvasVersion)
            {
                if (message.Complete) _authorDrawingRequests.Remove(message.Target);
                if (!_authorDrawings.TryGetValue(message.Target, out var unavailable))
                    _authorDrawings[message.Target] = unavailable = new AuthorDrawingCache();
                unavailable.RetryAfter = Time.unscaledTime + (message.Code == "RateLimited" ? 3 : 1);
                if (message.Code == "RateLimited") _nextAuthorRequest = unavailable.RetryAfter;
                AuthorDrawingChanged?.Invoke(message.Target);
                return;
            }
            if (message.Strokes == null || message.Strokes.Length > 256 || !request.Started && !message.Reset
                || request.Started && message.Reset || message.Strokes.Any(stroke => stroke.AuthorPlayerId != message.Target
                    || stroke.CanvasVersion < _drawingEpoch || stroke.CanvasVersion > _canvasVersion
                    || stroke.CanvasVersion != message.Strokes[0].CanvasVersion
                    || !GameRules.ValidStroke(stroke, stroke.CanvasVersion))) return;
            if (!_authorDrawings.TryGetValue(message.Target, out var drawing))
                _authorDrawings[message.Target] = drawing = new AuthorDrawingCache();
            if (message.Reset)
            {
                _historyStrokeCount -= drawing.Strokes.Count;
                drawing.Strokes.Clear();
                drawing.Version = 0;
                drawing.Complete = false;
                request.Started = true;
            }
            if (drawing.Strokes.Count + message.Strokes.Length > MAXIMUM_STROKES
                || _historyStrokeCount + message.Strokes.Length > GameRules.MAX_ROUND_STROKES) return;
            if (message.Strokes.Length > 0 && drawing.Version != 0 && message.Strokes.Any(stroke => stroke.CanvasVersion != drawing.Version)) return;
            foreach (var stroke in message.Strokes) AddAuthorStroke(stroke);
            if (message.Complete) { drawing.Complete = true; _authorDrawingRequests.Remove(message.Target); }
            AuthorDrawingChanged?.Invoke(message.Target);
        }

        private void AdvanceDrawingRequests()
        {
            if (_authorDrawingRequests.Count > 0)
            {
                var request = _authorDrawingRequests.First();
                if (Time.unscaledTime - request.Value.SentAt < 10) return;
                _authorDrawingRequests.Clear();
                _queuedAuthorDrawing ??= request.Key;
                _nextAuthorRequest = Time.unscaledTime + 1;
            }
            if (_queuedAuthorDrawing.HasValue && Time.unscaledTime >= _nextAuthorRequest)
                RequestAuthorDrawing(_queuedAuthorDrawing.Value);
        }
    }
}
