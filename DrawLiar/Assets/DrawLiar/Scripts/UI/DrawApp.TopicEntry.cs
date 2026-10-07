using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private readonly Dictionary<string, ServerTopicData> _roomOptionsCustomTopics = new Dictionary<string, ServerTopicData>();
        private Action _roomOptionsRefreshTopics;
        private VisualElement _roomTopicWorkshopOverlay;
        private RoomSettings _workshopRoomSettings;
        private string _workshopRoomCode;
        private Label _workshopTitle;

        private bool IsRoomTopicWorkshop => _roomTopicWorkshopOverlay?.panel != null && _roomOptionsEditor?.panel != null
            && ReferenceEquals(_workshopRoomSettings, _roomOptionsDraft) && CanEditRoomOptions(network.State)
            && _workshopRoomCode == lobby.RoomCode;

        private RoomSettings TopicWorkshopSettings => _roomTopicWorkshopOverlay != null ? IsRoomTopicWorkshop ? _workshopRoomSettings : null : draft;

        private void TopicWorkshopHeading(VisualElement parent, string styleClass)
        {
            _workshopTitle = Text(parent, _workshopBrowse ? "창작마당" : "나만의 주제", styleClass);
            _workshopTitle.name = "workshop-title";
        }

        private void TopicEntryButtons(VisualElement parent, string prefix)
        {
            var actions = Box(parent, "row topic-entry-actions");
            Button(actions, "나만의 주제 만들기", () => OpenTopicWorkshop(false), "secondary grow topic-entry-button").name = prefix + "-custom-topics";
            Button(actions, "창작마당", () => OpenTopicWorkshop(true), "secondary grow topic-entry-button topic-entry-last").name = prefix + "-workshop";
        }

        private void OpenTopicWorkshop(bool browse)
        {
            if (!inRoom)
            {
                Navigate(LobbyScreen.Topics);
                SelectTopicWorkshopTab(browse);
                return;
            }
            if (_roomTopicWorkshopOverlay != null || _roomOptionsSaving || _roomOptionsEditor?.panel == null
                || _roomOptionsDraft == null || !CanEditRoomOptions(network.State)) return;
            root.focusController?.focusedElement?.Blur();
            BeginTopicWorkshopPage();
            _workshopRoomSettings = _roomOptionsDraft;
            _workshopRoomCode = lobby.RoomCode;
            _roomOptionsEditor.SetEnabled(false);
            overlay.style.display = DisplayStyle.None;
            var popup = _roomTopicWorkshopOverlay = Box(root, "overlay enter utility-overlay room-topic-workshop-overlay");
            popup.name = "room-topic-workshop-overlay";
            var modal = Box(popup, "modal utility-popup room-topic-workshop-popup");
            modal.name = "room-topic-workshop-popup";
            var header = Box(modal, "row utility-popup-header");
            TopicWorkshopHeading(header, "utility-popup-title grow");
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, () => CloseRoomTopicWorkshop(), "utility-popup-x").name = "room-topic-workshop-x";
            var scroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical);
            scroll.name = "room-topic-workshop-scroll";
            scroll.AddToClassList("utility-popup-scroll");
            modal.Add(scroll);
            BuildTopicWorkshopForm(Box(scroll, "workshop-page-form"));
            SelectTopicWorkshopTab(browse);
            Button(modal, "← 뒤로", () => CloseRoomTopicWorkshop(), "secondary utility-popup-close").name = "room-topic-workshop-back";
            popup.focusable = true;
            popup.Focus();
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_roomTopicWorkshopOverlay != popup || !ReferenceEquals(evt.target, popup)) return;
                CloseRoomTopicWorkshop();
                evt.StopImmediatePropagation();
            });
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != UnityEngine.KeyCode.Escape) return;
                CloseRoomTopicWorkshop();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            popup.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup) && _roomTopicWorkshopOverlay == popup) CloseRoomTopicWorkshop(false);
            });
            DrawUIMotion.ShowModal(popup, modal);
            HideMobileScrollers();
        }

        private void CloseRoomTopicWorkshop(bool refreshOptions = true)
        {
            if (_roomTopicWorkshopOverlay == null) return;
            bool restore = IsRoomTopicWorkshop;
            var popup = _roomTopicWorkshopOverlay;
            _roomTopicWorkshopOverlay = null;
            _workshopRoomSettings = null;
            _workshopRoomCode = null;
            EndTopicWorkshopPage();
            _roomOptionsEditor?.SetEnabled(!_roomOptionsSaving);
            if (overlay != null) overlay.style.display = DisplayStyle.Flex;
            DrawUIMotion.HideModal(popup, popup.Q<VisualElement>(className: "modal"));
            if (restore && refreshOptions)
            {
                _roomOptionsRefreshTopics?.Invoke();
                _roomOptionsEditor?.Q<Button>("room-options-topics")?.Focus();
            }
        }

        private void SelectWorkshopRoomTopic(string name)
        {
            if (!IsRoomTopicWorkshop) return;
            var topic = GameDataStore.LoadCustomTopics().FirstOrDefault(value => value.Name == name);
            if (topic == null) return;
            _roomOptionsCustomTopics[name] = new ServerTopicData { Name = name, Words = (topic.Words ?? Array.Empty<string>()).ToArray() };
        }
    }
}
