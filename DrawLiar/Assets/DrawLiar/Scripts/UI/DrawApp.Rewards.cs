using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private bool _matchRewardEventsAttached;

        private void AttachMatchRewardEvents()
        {
            if (lobby == null || _matchRewardEventsAttached) return;
            lobby.MatchRewardsChanged += RefreshMatchRewards;
            _matchRewardEventsAttached = true;
            RefreshMatchRewards();
        }

        private void DetachMatchRewardEvents()
        {
            if (lobby != null && _matchRewardEventsAttached) lobby.MatchRewardsChanged -= RefreshMatchRewards;
            _matchRewardEventsAttached = false;
        }

        private void MatchRewardSummary(VisualElement parent, RoomSnapshot state)
        {
            if (!state.IsMatchComplete || !state.LocalRewardEligible || string.IsNullOrEmpty(state.MatchId)) return;
            var label = Text(parent, "보상 정산 중…", "section-title match-reward");
            label.name = "match-reward"; label.userData = state.MatchId;
            var experience = Text(parent, "", "match-experience-reward");
            experience.name = "match-experience-reward"; experience.userData = state.MatchId;
            RefreshMatchRewardLabel(label);
            RefreshMatchExperienceReward(experience);
        }

        private void RefreshMatchRewardLabel(Label label)
        {
            var reward = lobby.MatchReward(label.userData as string);
            if (reward?.Recorded == true) SetText(label, "획득 {0} 코인", reward.CoinReward);
            else SetText(label, "보상 정산 중…");
        }

        private void RefreshMatchRewards()
        {
            if (root == null || !isActiveAndEnabled) return;
            root.Query<Label>(className: "match-reward").ForEach(RefreshMatchRewardLabel);
            root.Query<Label>(className: "match-experience-reward").ForEach(RefreshMatchExperienceReward);
            RefreshAccountLevels();
            root.Query<Label>(className: "lobby-coins").ForEach(label => SetText(label, "{0} 코인", lobby.Profile?.Coins ?? 0));
            root.Query<Label>(className: "shop-coins").ForEach(label => SetText(label, "{0} 코인", lobby.Profile?.Coins ?? 0));
            RefreshShop();
        }

        private void RefreshMatchExperienceReward(Label label)
        {
            var reward = lobby.MatchReward(label.userData as string);
            label.style.display = reward?.Recorded == true ? DisplayStyle.Flex : DisplayStyle.None;
            if (reward?.Recorded == true) SetText(label, "획득 경험치 {0}", reward.ExperienceReward);
        }
    }
}
