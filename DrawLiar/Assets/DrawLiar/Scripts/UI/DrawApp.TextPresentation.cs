using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private sealed class PresentationTrack
        {
            public DrawAnimatedText Text;
            public float Starts, CharacterSeconds;
        }

        private readonly List<PresentationTrack> _presentationTracks = new List<PresentationTrack>();
        private string _presentationScope = "";
        private bool _presentationClosed, _presentationNeedsView, _presentationUsesPhaseClock;
        private double _presentationStartedAt;
        private float _presentationElapsed;
        private VisualElement _presentationOverlay, _revealVerdictCard, _revealIdentityCard, _revealAvatars;
        private Label _revealCaption;
        private Button _revealConfirm;
        private IVisualElementScheduledItem _presentationJob;
        private DrawLiarRevealPlan _liarRevealPlan;
        private bool _identityShown, _avatarsShown, _liarIdentityReleased;

        private static bool HasTextPresentation(GamePhase phase) => phase == GamePhase.RoleReveal
            || phase == GamePhase.LiarReveal || phase == GamePhase.RoundResults || phase == GamePhase.MatchResults;

        private void PrepareTextPresentation(RoomSnapshot state)
        {
            string scope = $"{lobby.RoomCode}/{state.MatchId}/{state.Round}/{state.Phase}";
            if (_presentationScope == scope) return;
            bool previousView = _presentationOverlay != null && ReferenceEquals(overlay, _presentationOverlay);
            ClearTextPresentationView();
            if (previousView) CloseModal();
            _presentationScope = scope;
            _presentationClosed = false;
            _liarIdentityReleased = false;
            _presentationNeedsView = HasTextPresentation(state.Phase);
            _liarRevealPlan = state.Phase == GamePhase.LiarReveal ? DrawLiarRevealPlan.Create(state) : null;
        }

        private bool HideLiarIdentity(RoomSnapshot state)
        {
            if (state.Phase != GamePhase.LiarReveal || _presentationClosed || _liarIdentityReleased || _liarRevealPlan == null) return false;
            if (Mathf.Max(_presentationElapsed, PhasePresentationElapsed()) < _liarRevealPlan.IdentityFinishes) return true;
            _liarIdentityReleased = true;
            return false;
        }

        private float PhasePresentationElapsed() => Mathf.Max(0,
            _timerClock.Duration - _timerClock.Remaining(Time.realtimeSinceStartupAsDouble));

        private void BeginTextPresentation(RoomSnapshot state, bool phaseClock)
        {
            _presentationOverlay = overlay;
            _presentationNeedsView = false;
            _presentationUsesPhaseClock = phaseClock;
            _presentationStartedAt = Time.realtimeSinceStartupAsDouble;
            _presentationElapsed = 0;
            _presentationJob = overlay.schedule.Execute(RefreshTextPresentation).Every(16);
            overlay.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, _presentationOverlay)) ClearTextPresentationView();
            });
        }

        private Label PresentationText(VisualElement parent, string text, string classes, string name,
            float starts = 0, float characterSeconds = .045f)
        {
            if (_presentationUsesPhaseClock && _liarRevealPlan == null)
            {
                float duration = DrawAnimatedText.MeasureDuration(text, characterSeconds);
                float budget = Mathf.Max(.15f, _timerClock.Duration - starts - .4f);
                if (duration > budget) characterSeconds *= budget / duration;
            }
            var animated = DrawAnimatedText.Create(parent, text, classes, name);
            _presentationTracks.Add(new PresentationTrack { Text = animated, Starts = starts, CharacterSeconds = characterSeconds });
            return animated.Element;
        }

        private void RefreshTextPresentation()
        {
            if (_presentationOverlay == null || !ReferenceEquals(overlay, _presentationOverlay)) return;
            float elapsed = _presentationUsesPhaseClock ? PhasePresentationElapsed()
                : (float)(Time.realtimeSinceStartupAsDouble - _presentationStartedAt);
            _presentationElapsed = Mathf.Max(_presentationElapsed, elapsed);
            foreach (var track in _presentationTracks)
            {
                bool started = _presentationElapsed >= track.Starts;
                track.Text.Element.style.opacity = started ? 1 : 0;
                if (started && !track.Text.IsComplete) track.Text.Seek(_presentationElapsed - track.Starts, track.CharacterSeconds);
            }
            if (_liarRevealPlan == null || _revealIdentityCard == null)
            {
                if (_presentationTracks.All(track => track.Text.IsComplete)) _presentationJob?.Pause();
                return;
            }
            bool identity = _presentationElapsed >= _liarRevealPlan.IdentityStarts;
            if (identity && !_identityShown)
            {
                _identityShown = true;
                if (_revealVerdictCard != null) _revealVerdictCard.style.display = DisplayStyle.None;
                _revealIdentityCard.style.display = DisplayStyle.Flex;
                SetText(_revealCaption, "라이어의 정체");
                if (_presentationElapsed < _liarRevealPlan.IdentityStarts + .3f) Enter(_revealIdentityCard, 250, 12);
            }
            bool avatars = _presentationElapsed >= _liarRevealPlan.IdentityFinishes;
            if (avatars && !_avatarsShown)
            {
                _avatarsShown = true;
                _liarIdentityReleased = true;
                _revealAvatars.style.display = DisplayStyle.Flex;
                _revealConfirm.SetEnabled(true);
                if (_presentationElapsed < _liarRevealPlan.IdentityFinishes + .3f) DrawUIMotion.Stagger(_revealAvatars, 80, 300, 10);
                var state = network.State;
                if (state != null && inRoom) { RefreshSecret(state); RefreshPlayerStates(state); }
            }
            if (avatars && _presentationTracks.All(track => track.Text.IsComplete)) _presentationJob?.Pause();
        }

        private void RestoreTextPresentation(RoomSnapshot state)
        {
            if (!_presentationNeedsView || _presentationClosed || overlay != null) return;
            if (state.Phase == GamePhase.RoleReveal) RoleReveal(state);
            else if (state.Phase == GamePhase.LiarReveal) RevealLiars(state);
            else if (state.Phase == GamePhase.RoundResults || state.Phase == GamePhase.MatchResults) Results(state);
        }

        private void ClearTextPresentationView()
        {
            _presentationJob?.Pause();
            _presentationJob = null;
            foreach (var track in _presentationTracks) track.Text.Dispose();
            _presentationTracks.Clear();
            _presentationOverlay = null;
            _revealVerdictCard = _revealIdentityCard = _revealAvatars = null;
            _revealCaption = null;
            _revealConfirm = null;
            _identityShown = _avatarsShown = false;
            _presentationElapsed = 0;
            _presentationNeedsView = !_presentationClosed;
        }

        private void CloseTextPresentation()
        {
            if (_presentationOverlay == null) return;
            if (ReferenceEquals(overlay, _presentationOverlay)) _presentationClosed = true;
            ClearTextPresentationView();
            if (network.State != null && inRoom && role != null)
            { RefreshSecret(network.State); RefreshPlayerStates(network.State); }
        }

        private void ResetTextPresentation()
        {
            ClearTextPresentationView();
            _presentationScope = "";
            _presentationClosed = false;
            _presentationNeedsView = false;
            _liarRevealPlan = null;
            _liarIdentityReleased = false;
        }

        private void RebuildTextPresentationLanguage()
        {
            var state = network.State;
            if (state == null || !inRoom) return;
            if (_liarRevealPlan != null) _liarRevealPlan = DrawLiarRevealPlan.Create(state);
            if (_presentationOverlay == null || !ReferenceEquals(overlay, _presentationOverlay)) return;
            ClearTextPresentationView();
            CloseModal();
        }
    }
}
