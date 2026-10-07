using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _workshopPreviewOverlay, _workshopPreviewModal, _workshopPreviewBody, _workshopPreviewOrigin;
        private CancellationTokenSource _workshopPreviewCancellation;
        private TopicWorkshopEntry _workshopPreviewEntry;
        private int _workshopPreviewVersion;

        private void OpenWorkshopPreview(TopicWorkshopEntry entry, VisualElement origin)
        {
            if (entry == null || !Guid.TryParse(entry.Id, out _) || !_workshopBrowse || _workshopBusy || _workshopLoading
                || lobby.IsBusy || !IsCurrentTopicWorkshop(_workshopVersion, _workshopAccountId, _workshopView)) return;
            CloseWorkshopPreview(false);
            root.focusController?.focusedElement?.Blur();
            _workshopPreviewEntry = entry;
            _workshopPreviewOrigin = origin;
            _workshopView.SetEnabled(false);
            var popup = _workshopPreviewOverlay = Box(root, "overlay enter utility-overlay workshop-preview-overlay");
            popup.name = "workshop-preview-overlay";
            var modal = _workshopPreviewModal = Box(popup, "modal utility-popup workshop-preview-popup");
            modal.name = "workshop-preview-popup";
            var header = Box(modal, "row utility-popup-header");
            Text(header, "제시어 미리보기", "utility-popup-title grow");
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, () => CloseWorkshopPreview(), "utility-popup-x").name = "workshop-preview-x";
            var scroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical);
            scroll.name = "workshop-preview-scroll";
            scroll.AddToClassList("utility-popup-scroll");
            modal.Add(scroll);
            _workshopPreviewBody = Box(scroll, "workshop-preview-body");
            Button(modal, "닫기", () => CloseWorkshopPreview(), "secondary utility-popup-close", DrawSound.UiCancel).name = "workshop-preview-close";
            popup.focusable = true;
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_workshopPreviewOverlay != popup || !ReferenceEquals(evt.target, popup)) return;
                CloseWorkshopPreview();
                evt.StopImmediatePropagation();
            });
            popup.RegisterCallback<KeyDownEvent>(WorkshopPreviewShortcut, TrickleDown.TrickleDown);
            popup.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup) && _workshopPreviewOverlay == popup) CloseWorkshopPreview(false);
            });
            DrawUIMotion.ShowModal(popup, modal);
            modal.Q<Button>("workshop-preview-x").Focus();
            HideMobileScrollers();
            BeginWorkshopPreviewLoad();
        }

        private void WorkshopPreviewShortcut(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseWorkshopPreview();
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.keyCode != KeyCode.Tab || _workshopPreviewModal == null) return;
            var controls = _workshopPreviewModal.Query<VisualElement>().ToList().Where(control => control.canGrabFocus
                && control.enabledInHierarchy && control.tabIndex >= 0 && control.resolvedStyle.visibility == Visibility.Visible
                && IsVisible(control)).ToList();
            if (controls.Count == 0) return;
            int index = controls.IndexOf(root.focusController?.focusedElement as VisualElement);
            int next = index < 0 ? (evt.shiftKey ? controls.Count - 1 : 0) : (index + (evt.shiftKey ? -1 : 1) + controls.Count) % controls.Count;
            root.focusController?.IgnoreEvent(evt);
            controls[next].Focus();
            evt.StopImmediatePropagation();
        }

        private void BeginWorkshopPreviewLoad()
        {
            if (_workshopPreviewOverlay == null) return;
            CancelWorkshopPreviewRequest();
            _workshopPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            int version = ++_workshopPreviewVersion, pageVersion = _workshopVersion;
            var popup = _workshopPreviewOverlay;
            string account = _workshopAccountId, id = _workshopPreviewEntry.Id;
            var token = _workshopPreviewCancellation.Token;
            _workshopPreviewBody.Clear();
            RawText(_workshopPreviewBody, _workshopPreviewEntry.Name, "workshop-preview-name");
            Text(_workshopPreviewBody, "처리 중…", "workshop-preview-message");
            _ = LoadWorkshopPreviewAsync(popup, version, pageVersion, account, id, token);
        }

        private bool IsCurrentWorkshopPreview(VisualElement popup, int version, int pageVersion, string account, CancellationToken token) =>
            !token.IsCancellationRequested && _workshopPreviewOverlay == popup && popup.panel != null
            && _workshopPreviewVersion == version && _workshopBrowse && IsCurrentTopicWorkshop(pageVersion, account, _workshopView);

        private async Task LoadWorkshopPreviewAsync(VisualElement popup, int version, int pageVersion, string account, string id, CancellationToken token)
        {
            try
            {
                var detail = await lobby.PreviewTopicWorkshopAsync(id, token);
                if (!IsCurrentWorkshopPreview(popup, version, pageVersion, account, token)) return;
                _workshopPreviewBody.Clear();
                var entry = detail.Topic;
                RawText(_workshopPreviewBody, entry.Name, "workshop-preview-name");
                var metadata = Box(_workshopPreviewBody, "row workshop-preview-metadata");
                RawText(metadata, entry.CreatorName, "workshop-creator grow");
                RawText(metadata, L.AvailableLanguages.FirstOrDefault(language => language.Code == entry.LanguageCode)?.DisplayName
                    ?? entry.LanguageCode, "workshop-entry-language");
                Text(_workshopPreviewBody, "제시어 {0}개 · 다운로드 {1}회", "rules workshop-preview-count", detail.Words.Length, entry.DownloadCount);
                var words = Box(_workshopPreviewBody, "row workshop-preview-words");
                for (int index = 0; index < detail.Words.Length; index++)
                    RawText(words, detail.Words[index], "workshop-preview-word").name = "workshop-preview-word-" + index;
                HideMobileScrollers();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!IsCurrentWorkshopPreview(popup, version, pageVersion, account, token)) return;
                _workshopPreviewBody.Clear();
                RawText(_workshopPreviewBody, _workshopPreviewEntry.Name, "workshop-preview-name");
                Text(_workshopPreviewBody, "제시어를 불러오지 못했습니다.", "workshop-preview-message");
                Button(_workshopPreviewBody, "다시 시도", BeginWorkshopPreviewLoad, "secondary").name = "workshop-preview-retry";
            }
        }

        private void CancelWorkshopPreviewRequest()
        {
            var cancellation = _workshopPreviewCancellation;
            _workshopPreviewCancellation = null;
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private void CloseWorkshopPreview(bool restoreFocus = true)
        {
            CancelWorkshopPreviewRequest();
            int version = ++_workshopPreviewVersion, pageVersion = _workshopVersion;
            string account = _workshopAccountId;
            var view = _workshopView;
            var parentOverlay = overlay;
            var parentWorkshop = _roomTopicWorkshopOverlay;
            var popup = _workshopPreviewOverlay;
            var modal = _workshopPreviewModal;
            var origin = _workshopPreviewOrigin;
            _workshopPreviewOverlay = _workshopPreviewModal = _workshopPreviewBody = _workshopPreviewOrigin = null;
            _workshopPreviewEntry = null;
            if (popup == null) return;
            if (_workshopView != null && lobby.IsAuthenticated && _workshopAccountId == lobby.Profile?.AccountId)
                _workshopView.SetEnabled(true);
            if (!restoreFocus) popup.RemoveFromHierarchy();
            else DrawUIMotion.HideModal(popup, modal);
            if (!restoreFocus || root == null) return;
            root.schedule.Execute(() =>
            {
                if (_workshopPreviewVersion == version && _workshopPreviewOverlay == null && origin?.panel != null
                    && IsCurrentTopicWorkshop(pageVersion, account, view) && overlay == parentOverlay
                    && _roomTopicWorkshopOverlay == parentWorkshop && _profileOverlay == null
                    && _roomPasswordOverlay == null && _roomCustomizeOverlay == null && !ChatInputHasFocus()) origin.Focus();
            }).StartingIn(180);
        }
    }
}
