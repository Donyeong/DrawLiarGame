using System.Linq;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private void Results(RoomSnapshot state)
        {
            if (state == null || (state.Phase != GamePhase.RoundResults && state.Phase != GamePhase.MatchResults)) return;
            var modal = Modal(state.Phase == GamePhase.MatchResults ? "최종 결과" : "라운드 결과", false);
            BeginTextPresentation(state, state.Phase == GamePhase.RoundResults);
            modal.name = "round-results-popup";
            var body = RoundResultBody(modal);
            var answer = Box(body, "round-reveal-card round-answer-card");
            PresentationText(answer, L.Format(HasKoreanFinalConsonant(state.Word) ? "정답은 {0}이었습니다." : "정답은 {0}였습니다.", state.Word),
                "round-reveal-statement", "results-answer", .12f, .035f);
            RoundLiarSummary(body, state, "results-no-liar", true);
            if (state.Settings.LiarMode == LiarMode.Mismatch && !string.IsNullOrEmpty(state.MismatchWord))
                Text(body, "다른 제시어 · {0}", "round-result-mismatch", state.MismatchWord).name = "results-mismatch-word";
            MatchRewardSummary(body, state);
            GuessResults(body, state);
            int scoreIndex = 0;
            foreach (var player in state.Players.Where(player => !player.IsSpectator).OrderByDescending(player => player.Score))
            {
                var row = Box(body, "score-row");
                var identity = Box(row, "row round-result-score-identity");
                var avatar = new AvatarElement(player.AvatarColor, player.Accessory);
                identity.Add(avatar);
                BindProfileTarget(avatar, player.AccountId);
                string template = state.Winners.Contains(player.Id)
                    ? (player.IsLiar ? "★ {0} · 라이어" : "★ {0} · 시민") : (player.IsLiar ? "{0} · 라이어" : "{0} · 시민");
                AddLevelBadge(Text(identity, template, "round-result-player-name", player.Name), player.Level, "result-" + player.Id, player.AccountId);
                Text(row, "{0}점 (+{1})", "player-score", player.Score, player.RoundPoints);
                if (state.Phase == GamePhase.MatchResults || PhasePresentationElapsed() < .4f)
                    Enter(row, 260, 8, 200 + 45 * scoreIndex);
                scoreIndex++;
            }
            Button(modal, "닫기", CloseModal, "primary round-result-dismiss", DrawSound.UiCancel).name = "results-close";
            HideMobileScrollers();
            RefreshTextPresentation();
        }

        private void RevealLiars(RoomSnapshot state)
        {
            if (state == null || state.Phase != GamePhase.LiarReveal) return;
            var modal = Modal("라이어 공개", false);
            BeginTextPresentation(state, true);
            modal.name = "liar-reveal-popup";
            var body = RoundResultBody(modal);
            _liarRevealPlan = _liarRevealPlan ?? DrawLiarRevealPlan.Create(state);
            _revealCaption = Text(body, _liarRevealPlan.HasVerdict ? "지목 결과" : "라이어의 정체", "reveal-sequence-caption");
            _revealCaption.name = "reveal-sequence-caption";
            if (_liarRevealPlan.HasVerdict)
            {
                _revealVerdictCard = Box(body, "round-reveal-card reveal-verdict-card");
                _revealVerdictCard.EnableInClassList("reveal-correct", _liarRevealPlan.IsCorrectAccusation);
                _revealVerdictCard.EnableInClassList("reveal-incorrect", !_liarRevealPlan.IsCorrectAccusation);
                for (int index = 0; index < 3; index++)
                    PresentationText(_revealVerdictCard, _liarRevealPlan.Verdict[index], "reveal-verdict-part reveal-verdict-part-" + index,
                        "reveal-verdict-part-" + index, _liarRevealPlan.VerdictStarts[index], _liarRevealPlan.VerdictCharacterSeconds);
            }
            _revealIdentityCard = Box(body, "round-reveal-card round-liar-card reveal-identity-card");
            _revealIdentityCard.style.display = DisplayStyle.None;
            _revealIdentityCard.EnableInClassList("round-no-liar-card", state.RevealedLiarCount == 0);
            PresentationText(_revealIdentityCard, _liarRevealPlan.Identity, "round-reveal-statement",
                "revealed-liars", _liarRevealPlan.IdentityStarts, _liarRevealPlan.IdentityCharacterSeconds);
            int knownLiars = state.Players.Count(player => player.IsLiar);
            if (knownLiars > 0 && state.RevealedLiarCount > knownLiars)
                Text(_revealIdentityCard, "라이어 {0}명", "round-reveal-count", state.RevealedLiarCount).name = "revealed-liar-count";
            var identities = _revealAvatars = Box(body, "row reveal-liars");
            identities.style.display = DisplayStyle.None;
            foreach (var player in state.Players.Where(player => player.IsLiar))
            {
                var identity = Box(identities, "reveal-liar");
                var avatar = new AvatarElement(player.AvatarColor, player.Accessory);
                avatar.AddToClassList("avatar-preview");
                identity.Add(avatar);
                BindProfileTarget(avatar, player.AccountId);
                LeveledName(identity, player.Name, player.Level, "player-name", "revealed-" + player.Id, player.AccountId);
                Text(identity, player.IsCaught ? "지목됨" : "지목 안 됨", "rules");
            }
            _revealConfirm = Button(modal, "확인", CloseModal, "primary round-result-dismiss", DrawSound.UiConfirm);
            _revealConfirm.name = "revealed-confirm";
            _revealConfirm.SetEnabled(false);
            HideMobileScrollers();
            RefreshTextPresentation();
        }

        private VisualElement RoundResultBody(VisualElement modal)
        {
            modal.AddToClassList("round-result-popup");
            var scroll = DrawSmoothScroll.Create();
            scroll.name = "round-result-scroll";
            scroll.AddToClassList("round-result-scroll");
            modal.Add(scroll);
            return Box(scroll, "round-result-content");
        }

        private void RoundLiarSummary(VisualElement parent, RoomSnapshot state, string emptyName, bool animate = false)
        {
            bool noLiar = state.Settings.LiarMode == LiarMode.Optional && state.RevealedLiarCount == 0;
            var names = state.Players.Where(player => player.IsLiar).Select(player => player.Name).ToArray();
            if (!noLiar && names.Length == 0 && state.RevealedLiarCount <= 0) return;
            var card = Box(parent, "round-reveal-card round-liar-card");
            if (noLiar)
            {
                card.AddToClassList("round-no-liar-card");
                if (animate) PresentationText(card, L.Text("이번 라운드에는 라이어가 없습니다."), "round-reveal-statement", emptyName, .6f, .025f);
                else Text(card, "이번 라운드에는 라이어가 없습니다.", "round-reveal-statement").name = emptyName;
                return;
            }
            if (names.Length > 0)
            {
                string source = HasKoreanFinalConsonant(names[names.Length - 1]) ? "라이어는 {0}이었습니다." : "라이어는 {0}였습니다.";
                if (animate) PresentationText(card, L.Format(source, string.Join(", ", names)), "round-reveal-statement", "results-liars", .6f, .025f);
                else Text(card, source, "round-reveal-statement", string.Join(", ", names)).name = "results-liars";
            }
            if (state.RevealedLiarCount > names.Length)
                Text(card, "라이어 {0}명", names.Length == 0 ? "round-reveal-statement" : "round-reveal-count",
                    state.RevealedLiarCount).name = "results-liar-count";
        }

        private static bool HasKoreanFinalConsonant(string value)
        {
            value = value?.TrimEnd();
            if (string.IsNullOrEmpty(value)) return true;
            char last = value[value.Length - 1];
            return last < '\uAC00' || last > '\uD7A3' || (last - '\uAC00') % 28 != 0;
        }
    }
}
