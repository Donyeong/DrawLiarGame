using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private int OwnLevel => Mathf.Clamp(lobby.Profile?.Level ?? 1, 1, AccountLevelRules.MAX_LEVEL);

        private static Label AddLevelBadge(Label label, int level, string locator, string accountId = null)
        {
            var parent = label.parent;
            int index = parent.IndexOf(label);
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("level-name-row");
            foreach (var className in label.GetClasses())
            {
                if (className == "grow") row.AddToClassList("grow");
                else row.AddToClassList("level-name-" + className);
            }
            label.RemoveFromHierarchy();
            parent.Insert(index, row);
            row.Add(new DrawLevelBadge(level) { name = "level-badge-" + locator, userData = accountId });
            label.AddToClassList("leveled-name"); row.Add(label);
            return label;
        }

        private static Label LeveledName(VisualElement parent, string name, int level, string classes, string locator, string accountId = null)
        {
            var label = RawText(parent, name, classes);
            label.name = locator;
            return AddLevelBadge(label, level, locator, accountId);
        }

        private static void RefreshNameLevel(Label label, PlayerView player)
        {
            var badge = label?.parent?.Q<DrawLevelBadge>();
            if (badge == null) return;
            badge.style.display = player == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (player != null) { badge.SetLevel(player.Level); badge.userData = player.AccountId; }
        }

        private void AccountExperience(VisualElement parent)
        {
            if (lobby.Profile == null) return;
            var progress = Box(parent, "account-level-progress");
            progress.name = "account-level-progress"; progress.userData = lobby.Profile.AccountId;
            var heading = Box(progress, "row account-level-progress-heading");
            Text(heading, "레벨 {0}", "grow account-level-title", OwnLevel).name = "account-level-title";
            Text(heading, "", "account-level-progress-text").name = "account-level-progress-text";
            var track = Box(progress, "account-level-track"); track.pickingMode = PickingMode.Ignore;
            Box(track, "account-level-fill").name = "account-level-fill";
            RefreshAccountExperience(progress);
        }

        private void RefreshAccountExperience(VisualElement progress)
        {
            var profile = lobby.Profile;
            if (profile == null || progress.userData as string != profile.AccountId) return;
            int level = AccountLevelRules.GetLevel(profile.Experience);
            long into = AccountLevelRules.ExperienceIntoLevel(profile.Experience);
            int required = AccountLevelRules.RequiredExperienceForNextLevel(level);
            SetText(progress.Q<Label>("account-level-title"), "레벨 {0}", level);
            var label = progress.Q<Label>("account-level-progress-text");
            if (level == AccountLevelRules.MAX_LEVEL) SetText(label, "최고 레벨");
            else SetText(label, "경험치 {0} / {1}", into, required);
            progress.Q<VisualElement>("account-level-fill").style.width = Length.Percent(required == 0 ? 100 : Mathf.Clamp01((float)into / required) * 100);
        }

        private void RefreshAccountLevels()
        {
            if (root == null || lobby.Profile == null) return;
            string accountId = lobby.Profile.AccountId;
            root.Query<DrawLevelBadge>().ForEach(badge => { if (badge.userData as string == accountId) badge.SetLevel(OwnLevel); });
            root.Query<VisualElement>(className: "account-level-progress").ForEach(RefreshAccountExperience);
        }
    }
}
