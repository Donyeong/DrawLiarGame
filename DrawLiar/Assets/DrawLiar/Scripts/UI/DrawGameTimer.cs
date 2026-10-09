using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    internal sealed class DrawGameClock
    {
        private RoomSnapshot _snapshot;
        private double _receivedAt;
        private float _reportedRemaining;
        private GamePhase _phase;
        private int _round, _artist, _ballot;
        private bool _coinToss;

        public float Duration { get; private set; }
        public int Period { get; private set; }
        public bool IsVisible { get; private set; }

        public void Observe(RoomSnapshot snapshot, double now)
        {
            if (snapshot == null) { Reset(); return; }
            int artist = snapshot.Phase == GamePhase.Drawing ? snapshot.ArtistId : -1;
            int ballot = snapshot.Phase == GamePhase.Discussion || snapshot.Phase == GamePhase.Voting || snapshot.Phase == GamePhase.Rebuttal
                ? snapshot.BallotVersion : -1;
            float duration = PhaseDuration(snapshot);
            bool changed = _snapshot == null || _phase != snapshot.Phase || _round != snapshot.Round || _artist != artist
                || _ballot != ballot || _coinToss != snapshot.IsJudgmentCoinToss || Duration != duration;
            if (ReferenceEquals(_snapshot, snapshot) && !changed) return;
            _snapshot = snapshot;
            _phase = snapshot.Phase;
            _round = snapshot.Round;
            _artist = artist;
            _ballot = ballot;
            _coinToss = snapshot.IsJudgmentCoinToss;
            Duration = duration;
            IsVisible = snapshot.Phase != GamePhase.Lobby && snapshot.Phase != GamePhase.MatchResults;
            float remaining = snapshot.RemainingSeconds;
            _reportedRemaining = float.IsNaN(remaining) || float.IsInfinity(remaining) ? 0 : Mathf.Clamp(remaining, 0, Duration);
            _receivedAt = now;
            if (changed) Period++;
        }

        public float Remaining(double now) => Mathf.Clamp(_reportedRemaining - (float)Math.Max(0, now - _receivedAt), 0, Duration);

        public void Reset()
        {
            _snapshot = null;
            Duration = _reportedRemaining = 0;
            IsVisible = false;
            Period++;
        }

        private static float PhaseDuration(RoomSnapshot snapshot)
        {
            if (snapshot.Phase == GamePhase.Rebuttal && snapshot.IsJudgmentCoinToss) return GameRules.JUDGMENT_COIN_TOSS_SECONDS;
            var settings = snapshot.Settings;
            if (settings == null) return 0;
            int seconds;
            switch (snapshot.Phase)
            {
                case GamePhase.RoleReveal: seconds = settings.RoleSeconds; break;
                case GamePhase.Drawing: seconds = settings.DrawSeconds; break;
                case GamePhase.Discussion: seconds = settings.DiscussionSeconds; break;
                case GamePhase.Rebuttal: seconds = settings.RebuttalSeconds > 0 ? settings.RebuttalSeconds : settings.VoteSeconds; break;
                case GamePhase.Voting: seconds = settings.VoteSeconds; break;
                case GamePhase.LiarReveal: return GameRules.RevealDuration(settings.RevealSeconds, snapshot.RevealedLiarCount);
                case GamePhase.Guessing: seconds = settings.GuessSeconds; break;
                case GamePhase.RoundResults: seconds = settings.ResultSeconds; break;
                default: seconds = 0; break;
            }
            return Mathf.Max(0, seconds);
        }
    }

    internal sealed class DrawGameTimer : VisualElement
    {
        private static readonly List<TimeValue> RESET_DURATION = new List<TimeValue> { new TimeValue(0, TimeUnit.Second) };
        private static readonly List<TimeValue> COUNTDOWN_DURATION = new List<TimeValue> { new TimeValue(100, TimeUnit.Millisecond) };
        private readonly VisualElement _fill;
        private int _period = -1, _seconds = -1, _resetFrame;
        private bool _reset;
        private float _ratio = -1;

        public Label Value { get; }

        public DrawGameTimer()
        {
            AddToClassList("game-timer");
            pickingMode = PickingMode.Ignore;
            languageDirection = LanguageDirection.LTR;
            Value = new Label { pickingMode = PickingMode.Ignore, languageDirection = LanguageDirection.LTR };
            Value.AddToClassList("timer");
            Value.AddToClassList("timer-value");
            var font = Resources.Load<Font>("DrawLiar/Fonts/BarlowCondensed-SemiBold");
            if (font != null) Value.style.unityFontDefinition = FontDefinition.FromFont(font);
            Add(Value);
            var track = new VisualElement { pickingMode = PickingMode.Ignore, languageDirection = LanguageDirection.LTR };
            track.AddToClassList("timer-track");
            Add(track);
            _fill = new VisualElement { pickingMode = PickingMode.Ignore, languageDirection = LanguageDirection.LTR };
            _fill.AddToClassList("timer-fill");
            track.Add(_fill);
        }

        public void Refresh(float remaining, float duration, int period, bool visible)
        {
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            int seconds = Mathf.Max(0, Mathf.CeilToInt(remaining));
            if (seconds != _seconds)
            {
                _seconds = seconds;
                Value.text = $"{seconds / 60:00}:{seconds % 60:00}";
            }
            bool urgent = visible && remaining <= 5;
            EnableInClassList("timer-urgent", urgent);
            Value.EnableInClassList("timer-urgent", urgent);
            if (_period != period)
            {
                _period = period;
                _fill.style.transitionDuration = RESET_DURATION;
                _reset = true;
                _resetFrame = Time.frameCount;
            }
            else if (_reset && _resetFrame != Time.frameCount)
            {
                _fill.style.transitionDuration = COUNTDOWN_DURATION;
                _reset = false;
            }
            float ratio = duration > 0 ? Mathf.Clamp01(remaining / duration) : 0;
            if (Mathf.Approximately(_ratio, ratio)) return;
            _ratio = ratio;
            _fill.style.width = Length.Percent(ratio * 100);
        }
    }
}
