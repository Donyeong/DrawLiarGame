using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _judgmentPanel, _guessPopupOverlay;
        private string _judgmentPanelKey = "", _guessScope = "", _guessAccountId, _guessDraft = "";
        private bool _guessSubmitted, _guessPopupAutoOpened;
        private TextField _guessField;
        private Button _guessSubmit;
        private Label _guessTimer;
        private DrawGameTimer _guessCountdown;
#if ENABLE_INPUT_SYSTEM
        private UnityEngine.InputSystem.Keyboard _guessKeyboard;
        private bool _guessComposing;
        private int _guessCompositionFrame = -1;
#endif

        private void CreateJudgmentPanel(VisualElement parent)
        {
            _judgmentPanel = Box(parent, "judgment-panel");
            _judgmentPanel.name = "judgment-panel";
            _judgmentPanel.style.display = DisplayStyle.None;
            _judgmentPanelKey = "";
            _judgmentCounts = _judgmentProgress = null;
            voteProgress = null;
        }

        private void RefreshJudgmentPanel(RoomSnapshot state, PlayerView local)
        {
            if (_judgmentPanel == null) return;
            bool nomination = IsNominationPhase(state);
            bool visible = nomination || state.Phase == GamePhase.Rebuttal && state.HasAccused;
            _judgmentPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _judgmentPanel.EnableInClassList("nomination-panel", nomination);
            if (!visible) { _judgmentPanelKey = ""; _judgmentCounts = _judgmentProgress = null; voteProgress = null; return; }
            if (nomination) { RefreshNominationPanel(state, local); return; }
            string key = $"judgment/{state.BallotVersion}/{state.AccusedPlayerId}/{state.Settings.LiarMode}/{state.LocalPlayerId}/{state.LocalIsSpectator}/{local?.IsConnected}/{local?.HasJudged}/{_judgmentSubmitted}/{state.LocalJudgmentApprove}/{state.IsJudgmentCoinToss}";
            if (key != _judgmentPanelKey)
            {
                _judgmentPanelKey = key;
                _judgmentPanel.Clear();
                voteProgress = null;
                if (IsNoLiarAccused(state)) Text(_judgmentPanel, "라이어가 없다고 생각하나요?", "nomination-prompt").name = "judgment-no-liar-prompt";
                _judgmentCounts = Text(_judgmentPanel, "", "judgment-counts");
                _judgmentCounts.name = "judgment-counts";
                _judgmentProgress = Text(_judgmentPanel, "", "judgment-progress");
                _judgmentProgress.name = "judgment-progress";
                if (state.IsJudgmentCoinToss) Text(_judgmentPanel, "동전 던지기", "judgment-state");
                else if (state.LocalIsSpectator) Text(_judgmentPanel, "관전 중", "judgment-state");
                else if (local?.Id == state.AccusedPlayerId) Text(_judgmentPanel, "반론 중", "judgment-state");
                else if (local?.IsConnected == true && !local.IsSpectator)
                {
                    var actions = Box(_judgmentPanel, "row judgment-actions");
                    var yes = Button(actions, "찬성", () => SubmitJudgment(true), "primary judgment-approve", DrawSound.UiConfirm);
                    yes.name = "judgment-approve";
                    var no = Button(actions, "반대", () => SubmitJudgment(false), "secondary judgment-reject", DrawSound.UiConfirm);
                    no.name = "judgment-reject";
                    yes.SetEnabled(CanJudge(state, local));
                    no.SetEnabled(CanJudge(state, local));
                    if (local.HasJudged) Text(_judgmentPanel, state.LocalJudgmentApprove ? "찬성 제출 완료" : "반대 제출 완료", "judgment-state");
                    else if (_judgmentSubmitted) Text(_judgmentPanel, "제출 중", "judgment-state");
                }
            }
            SetText(_judgmentCounts, "찬성 {0} · 반대 {1}", state.ApprovalCount, state.RejectionCount);
            SetText(_judgmentProgress, "찬반 투표 {0}/{1}", state.JudgmentVotesCast, state.JudgmentVoterCount);
        }

        private void RefreshNominationPanel(RoomSnapshot state, PlayerView local)
        {
            var target = _hasSelectedPlayer ? state.Players.FirstOrDefault(player => player.Id == selectedPlayerId) : null;
            bool noLiarSelected = IsNoLiarSelected(state);
            string key = $"nomination/{state.Phase}/{state.BallotVersion}/{state.Settings.LiarMode}/{state.LocalPlayerId}/{state.LocalIsSpectator}/{local?.IsConnected}/{local?.IsSpectator}/{local?.HasVoted}/{selectedPlayerId}/{_hasSelectedPlayer}/{target?.Name}/{voteSubmitted}";
            if (key != _judgmentPanelKey)
            {
                _judgmentPanelKey = key;
                _judgmentPanel.Clear();
                _judgmentCounts = _judgmentProgress = null;
                bool spectator = state.LocalIsSpectator || local?.IsSpectator == true;
                string prompt = spectator ? "관전 중" : local?.HasVoted == true ? "지목 완료" : voteSubmitted ? "제출 중"
                    : state.Settings.LiarMode == LiarMode.Optional ? "의심스러운 사람 또는 ‘라이어 없음’을 선택하세요" : "의심스러운 사람을 지목하세요";
                Text(_judgmentPanel, prompt, "nomination-prompt").name = "nomination-guidance";
                if (!spectator && local?.IsConnected == true && !local.HasVoted)
                {
                    if (target != null) RawText(_judgmentPanel, target.Name, "nomination-target").name = "nomination-target";
                    else if (noLiarSelected) Text(_judgmentPanel, "라이어 없음", "nomination-target").name = "nomination-target";
                    VisualElement actions = _judgmentPanel;
                    if (state.Settings.LiarMode == LiarMode.Optional)
                    {
                        actions = Box(_judgmentPanel, "row nomination-actions");
                        var none = Button(actions, "라이어 없음", SelectNoLiar, "secondary grow nomination-none");
                        none.name = "nomination-no-liar";
                        none.EnableInClassList("create-mode-selected", noLiarSelected);
                        none.SetEnabled(CanSelectNoLiar(state));
                    }
                    var submit = Button(actions, voteSubmitted ? "제출 중" : "지목하기", SubmitVote, "primary nomination-submit", DrawSound.UiConfirm);
                    submit.name = "nomination-submit";
                    submit.SetEnabled(noLiarSelected ? CanSelectNoLiar(state) : target != null && CanSelectVote(state, target));
                }
                voteProgress = Text(_judgmentPanel, "", "nomination-progress");
                voteProgress.name = "nomination-progress";
            }
            int voted = state.Players.Count(player => player.HasVoted && !player.IsSpectator && player.IsConnected);
            int total = state.Players.Count(player => !player.IsSpectator && player.IsConnected);
            SetText(voteProgress, "지목 {0}/{1}", voted, total);
        }

        private static bool CanGuess(RoomSnapshot state, PlayerView local) => state != null && state.Phase == GamePhase.Guessing
            && state.LocalIsLiar && !state.LocalIsSpectator && state.RemainingSeconds > 0 && local != null
            && local.IsConnected && !local.IsSpectator && !local.HasGuessed;

        private string GuessScope(RoomSnapshot state) => $"{state.Round}/{state.LocalPlayerId}/{lobby.Profile?.AccountId}";

        private void PrepareGuessingInput(RoomSnapshot state, PlayerView local)
        {
            if (state.Phase != GamePhase.Guessing || !state.LocalIsLiar || state.LocalIsSpectator || local?.IsConnected != true
                || local.IsSpectator || state.RemainingSeconds <= 0)
            {
                ResetGuessingInput();
                return;
            }
            string scope = GuessScope(state);
            if (_guessScope != scope)
            {
                ResetGuessingInput();
                _guessScope = scope;
                _guessAccountId = lobby.Profile?.AccountId;
            }
        }

        private void RefreshGuessingInput(RoomSnapshot state, PlayerView local)
        {
            if (_guessScope == "") return;
            if (local.HasGuessed || _guessSubmitted)
            {
                if (_guessPopupOverlay != null && overlay == _guessPopupOverlay) CloseModal();
                if (local.HasGuessed) _guessDraft = "";
                return;
            }
            if (!_guessPopupAutoOpened && _roomPasswordOverlay == null) OpenGuessingPopup();
            UpdateGameTimers(state);
        }

        private void OpenGuessingPopup()
        {
            var state = network.State;
            var local = state?.Players.FirstOrDefault(player => player.Id == state.LocalPlayerId);
            if (!CanGuess(state, local) || _guessSubmitted || _roomPasswordOverlay != null || !isActiveAndEnabled) return;
            if (_guessPopupOverlay != null && overlay == _guessPopupOverlay) { _guessField?.Focus(); return; }
            if (_guessScope != GuessScope(state))
            {
                ResetGuessingInput();
                _guessScope = GuessScope(state);
                _guessAccountId = lobby.Profile?.AccountId;
            }
            var modal = Modal("", false);
            modal.AddToClassList("utility-popup");
            modal.AddToClassList("guess-popup");
            modal.name = "guess-popup";
            var popup = _guessPopupOverlay = overlay;
            popup.AddToClassList("guess-popup-overlay");
            popup.name = "guess-popup-overlay";
            if (popup.parent != root) root.Add(popup);
            _guessPopupAutoOpened = true;
            var heading = Box(modal, "row utility-popup-header");
            Text(heading, "정답 입력", "utility-popup-title grow");
            _guessCountdown = new DrawGameTimer { name = "guess-countdown" };
            _guessCountdown.AddToClassList("guess-timer-control");
            _guessTimer = _guessCountdown.Value;
            _guessTimer.AddToClassList("guess-timer");
            heading.Add(_guessCountdown);
            UpdateGameTimers(state);
            IconButton(heading, "닫기", DrawUIIcon.Kind.Close, CloseModal, "utility-popup-x");
            Text(modal, "주제 · {0}", "subtitle", state.Topic);
            var field = _guessField = new TextField { maxLength = 40, value = _guessDraft, name = "liar-guess-input" };
            Placeholder(field, "제시어 입력");
            field.AddToClassList("guess-input");
            field.textEdition.autoCorrection = false;
            modal.Add(field);
            _guessSubmit = Button(modal, "정답 제출", SubmitGuess, "primary guess-submit", DrawSound.UiConfirm);
            _guessSubmit.name = "guess-submit";
            _guessSubmit.SetEnabled(!string.IsNullOrWhiteSpace(field.value));
            field.RegisterValueChangedCallback(evt =>
            {
                if (_guessField != field || !ReferenceEquals(evt.target, field)) return;
                _guessDraft = evt.newValue;
                _guessSubmit?.SetEnabled(!_guessSubmitted && !string.IsNullOrWhiteSpace(evt.newValue));
            });
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
#if ENABLE_INPUT_SYSTEM && (UNITY_EDITOR || !UNITY_WEBGL)
                if (_guessComposing || _guessCompositionFrame == Time.frameCount) return;
#endif
                SubmitGuess();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_guessPopupOverlay == popup && ReferenceEquals(evt.target, popup)) CloseModal();
            });
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Tab) return;
                var controls = modal.Query<VisualElement>().ToList().Where(control => control.canGrabFocus && control.enabledInHierarchy
                    && control.tabIndex >= 0 && control.resolvedStyle.visibility == Visibility.Visible && IsVisible(control)).ToList();
                if (controls.Count == 0) return;
                int index = controls.IndexOf(root.focusController?.focusedElement as VisualElement);
                int next = index < 0 ? (evt.shiftKey ? controls.Count - 1 : 0) : (index + (evt.shiftKey ? -1 : 1) + controls.Count) % controls.Count;
                root.focusController?.IgnoreEvent(evt);
                controls[next].Focus();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
#if ENABLE_INPUT_SYSTEM
            _guessKeyboard = UnityEngine.InputSystem.Keyboard.current;
            if (_guessKeyboard != null)
            {
                _guessKeyboard.onIMECompositionChange += OnGuessComposition;
                _guessKeyboard.onTextInput += OnGuessTextInput;
            }
#endif
            field.Focus();
            DrawUIMotion.ShowModal(popup, modal);
        }

        private void SubmitGuess()
        {
            var state = network.State;
            var local = state?.Players.FirstOrDefault(player => player.Id == state.LocalPlayerId);
            if (_guessField == null || _guessSubmitted || !CanGuess(state, local) || _guessScope != GuessScope(state)
                || string.IsNullOrWhiteSpace(_guessField.value)) return;
            _guessDraft = _guessField.value;
            _guessSubmitted = true;
            _guessSubmit?.SetEnabled(false);
            network.Guess(_guessDraft);
            RefreshState(state);
        }

        private void ClearGuessingPopup()
        {
#if ENABLE_INPUT_SYSTEM
            if (_guessKeyboard != null)
            {
                _guessKeyboard.onIMECompositionChange -= OnGuessComposition;
                _guessKeyboard.onTextInput -= OnGuessTextInput;
            }
            _guessKeyboard = null;
            _guessComposing = false;
            _guessCompositionFrame = -1;
#endif
            _guessField?.Blur();
            _guessField?.SetValueWithoutNotify("");
            _guessField = null;
            _guessSubmit = null;
            _guessTimer = null;
            _guessCountdown = null;
            _guessPopupOverlay = null;
        }

        private void ResetGuessingInput(bool immediate = false)
        {
            var popup = _guessPopupOverlay;
            if (popup != null && overlay == popup)
            {
                CloseModal();
                if (immediate) popup.RemoveFromHierarchy();
            }
            else ClearGuessingPopup();
            _guessDraft = _guessScope = "";
            _guessAccountId = null;
            _guessSubmitted = _guessPopupAutoOpened = false;
        }

#if ENABLE_INPUT_SYSTEM
        private void OnGuessComposition(UnityEngine.InputSystem.LowLevel.IMECompositionString composition)
        {
            _guessComposing = composition.Count > 0;
            _guessCompositionFrame = Time.frameCount;
        }

        private void OnGuessTextInput(char character)
        {
            if (!_guessComposing) return;
            _guessComposing = false;
            _guessCompositionFrame = Time.frameCount;
        }
#endif

        private void GuessResults(VisualElement parent, RoomSnapshot state)
        {
            if (state.Phase != GamePhase.RoundResults && state.Phase != GamePhase.MatchResults) return;
            var liars = state.Players.Where(player => player.IsLiar && player.GuessOutcome != GuessOutcome.Hidden).ToArray();
            if (liars.Length == 0) return;
            var results = Box(parent, "guess-results");
            results.name = "guess-results";
            Text(results, "라이어 답변", "section-title");
            foreach (var liar in liars)
            {
                var row = Box(results, "guess-result-row");
                row.name = "guess-result-" + liar.Id;
                var identity = Box(row, "row");
                RawText(identity, liar.Name, "guess-result-name grow");
                string outcome = liar.GuessOutcome == GuessOutcome.Correct ? "정답" : liar.GuessOutcome == GuessOutcome.Incorrect ? "오답" : "미제출";
                var status = Text(identity, outcome, "guess-result-status");
                status.AddToClassList(liar.GuessOutcome == GuessOutcome.Correct ? "guess-correct" : "guess-incorrect");
                if (liar.GuessOutcome != GuessOutcome.Unanswered) RawText(row, liar.Guess, "guess-result-answer");
            }
        }
    }
}
