using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _updateOverlay;
        private bool _updateEventsAttached;
        private bool _updateNoticeShown;

        private void AttachClientVersionEvents()
        {
            if (lobby == null || _updateEventsAttached) return;
            lobby.ClientVersionChanged += ShowClientUpdate;
            _updateEventsAttached = true;
            ShowClientUpdate();
        }

        private void DetachClientVersionEvents()
        {
            if (lobby == null || !_updateEventsAttached) return;
            lobby.ClientVersionChanged -= ShowClientUpdate;
            _updateEventsAttached = false;
        }

        // 일반 업데이트는 실행 중 한 번만 안내하고, 강제 업데이트는 닫을 수 없는 창으로 접속을 막는다.
        private void ShowClientUpdate()
        {
            var version = lobby.ClientVersion;
            bool required = version.UpdateRequired;
            if (root == null || !required && (!version.UpdateAvailable || _updateNoticeShown)) return;
            if (_updateOverlay != null && (!required || _updateOverlay.ClassListContains("update-required"))) return;
            _updateOverlay?.RemoveFromHierarchy();
            _updateNoticeShown = true;
            var popup = _updateOverlay = Box(root, "overlay enter utility-overlay update-overlay");
            popup.EnableInClassList("update-required", required);
            var modal = Box(popup, "modal utility-popup update-popup");
            Text(modal, "업데이트 안내", "utility-popup-title");
            Text(modal, required ? "새 버전으로 업데이트해야 이용할 수 있습니다." : "새 버전이 출시되었습니다.", "rules");
            var actions = Box(modal, "row social-confirm-actions");
            if (!required) Button(actions, "나중에", CloseClientUpdate, "secondary grow", DrawSound.UiCancel);
            Button(actions, "업데이트", lobby.OpenStore, "primary grow");
            DrawUIMotion.ShowModal(popup, modal);
        }

        private void CloseClientUpdate()
        {
            if (_updateOverlay?.ClassListContains("update-required") != false) return;
            _updateOverlay.RemoveFromHierarchy();
            _updateOverlay = null;
        }
    }
}
