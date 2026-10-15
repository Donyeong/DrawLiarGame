using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _judgmentCoinOverlay;
        private DrawJudgmentCoin _judgmentCoin;
        private Label _judgmentCoinResult;
        private IVisualElementScheduledItem _judgmentCoinAnimation;
        private string _judgmentCoinScope = "";

        private void RefreshJudgmentCoinToss(RoomSnapshot state)
        {
            if (!isActiveAndEnabled || !inRoom || state == null || state.Phase != GamePhase.Rebuttal
                || !state.HasAccused || !state.IsJudgmentCoinToss)
            {
                ClearJudgmentCoinToss();
                return;
            }
            string scope = $"{state.MatchId}/{state.Round}/{state.BallotVersion}";
            if (_judgmentCoinOverlay != null && _judgmentCoinScope == scope) return;
            ClearJudgmentCoinToss();
            CloseModal();
            if (IsMobile) CloseChat();
            _judgmentCoinScope = scope;
            _judgmentCoinOverlay = Box(root, "judgment-coin-overlay");
            _judgmentCoinOverlay.name = "judgment-coin-overlay";
            _judgmentCoinOverlay.focusable = true;
            var card = Box(_judgmentCoinOverlay, "judgment-coin-card");
            card.name = "judgment-coin-card";
            card.style.minWidth = 0;
            Text(card, "동률이에요!", "judgment-coin-heading");
            Text(card, "이번 라운드 두 번째 동률부터 동전으로 결정해요.", "judgment-coin-note");
            _judgmentCoin = new DrawJudgmentCoin { name = "judgment-coin-visual" };
            card.Add(_judgmentCoin);
            _judgmentCoinResult = Text(card, "동전을 던지고 있어요…", "judgment-coin-result");
            _judgmentCoinResult.name = "judgment-coin-result";
            _judgmentCoinResult.style.minWidth = 0;
            _judgmentCoinResult.style.whiteSpace = WhiteSpace.Normal;
            _judgmentCoinOverlay.Focus();
            UpdateJudgmentCoinToss();
            _judgmentCoinAnimation = _judgmentCoinOverlay.schedule.Execute(UpdateJudgmentCoinToss).Every(16);
            Enter(card, 160, 6);
        }

        private void UpdateJudgmentCoinToss()
        {
            var state = network?.State;
            if (!isActiveAndEnabled || !inRoom || state == null || !state.IsJudgmentCoinToss
                || state.Phase != GamePhase.Rebuttal || !state.HasAccused)
            {
                ClearJudgmentCoinToss();
                return;
            }
            float remaining = _timerClock.Remaining(Time.realtimeSinceStartupAsDouble);
            _judgmentCoin?.UpdateFlip(remaining, state.JudgmentCoinApproved);
            bool settled = remaining <= DrawJudgmentCoin.RESULT_HOLD_SECONDS;
            if (!settled) SetText(_judgmentCoinResult, "동전을 던지고 있어요…");
            else if (IsNoLiarAccused(state))
                SetText(_judgmentCoinResult, state.JudgmentCoinApproved ? "‘라이어 없음’ 지목이 가결되었어요." : "‘라이어 없음’ 지목이 부결되었어요.");
            else
                SetText(_judgmentCoinResult, state.JudgmentCoinApproved ? "{0}님 지목이 가결되었어요." : "{0}님 지목이 부결되었어요.",
                    state.Players.FirstOrDefault(player => player.Id == state.AccusedPlayerId)?.Name ?? "");
            _judgmentCoinResult?.EnableInClassList("judgment-coin-approved", settled && state.JudgmentCoinApproved);
            _judgmentCoinResult?.EnableInClassList("judgment-coin-rejected", settled && !state.JudgmentCoinApproved);
        }

        private void ClearJudgmentCoinToss()
        {
            _judgmentCoinAnimation?.Pause();
            _judgmentCoinAnimation = null;
            _judgmentCoinOverlay?.RemoveFromHierarchy();
            _judgmentCoinOverlay = null;
            _judgmentCoin = null;
            _judgmentCoinResult = null;
            _judgmentCoinScope = "";
        }
    }
}
