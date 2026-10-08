using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private Label _mobileWaitingRoomTitle, _roomTopicsRoomName, _roomTopicsCount;
        private Button _waitingRoomTopics;
        private VisualElement _roomTopicsPopup, _roomTopicsGrid;
        private ScrollView _roomTopicsScroll;
        private string _roomTopicsPublicRoomId;
        private bool _roomTopicsFromWaitingRoom;
        private string[] _defaultRoomTopics, _roomTopicsVisibleNames = Array.Empty<string>();

        private string[] RoomTopicNames(string[] selected)
        {
            var names = (selected ?? Array.Empty<string>()).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
            if (names.Length > 0) return names;
            if (_defaultRoomTopics == null)
            {
                var asset = Resources.Load<TextAsset>("DrawLiar/GameData");
                var data = asset != null ? JsonUtility.FromJson<GameData>(asset.text) : new GameData();
                _defaultRoomTopics = (data.Topics ?? Array.Empty<TopicData>()).Select(entry => entry.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
            }
            return _defaultRoomTopics;
        }

        private void CreatePublicRoomTopics(VisualElement parent, PublicRoomInfo room)
        {
            var names = RoomTopicNames(room.Topics);
            var row = Box(parent, "row room-topic-summary");
            var preview = RawText(row, string.Join(" · ", names.Take(3)), "room-topic-preview grow");
            preview.tooltip = string.Join(" · ", names);
            var button = Button(row, "", () => OpenPublicRoomTopics(room.Id), "secondary room-topics-open");
            button.name = "public-room-topics-" + room.Id;
            SetText(button, "주제 {0}개", names.Length);
        }

        private void RefreshWaitingRoomTopics(RoomSnapshot state)
        {
            bool waiting = state.Phase == GamePhase.Lobby;
            if (_waitingRoomTopics != null)
            {
                _waitingRoomTopics.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
                if (waiting) SetText(_waitingRoomTopics, "주제 {0}개", RoomTopicNames(state.Settings.Topics).Length);
            }
            if (_mobileWaitingRoomTitle != null)
            {
                _mobileWaitingRoomTitle.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
                SetRawText(_mobileWaitingRoomTitle, state.Settings.RoomName);
                _mobileWaitingRoomTitle.tooltip = state.Settings.RoomName;
            }
            RefreshRoomTopicsPopup();
        }

        private void OpenWaitingRoomTopics()
        {
            var state = network.State;
            if (!inRoom || state == null || state.Phase != GamePhase.Lobby) return;
            OpenRoomTopicsPopup(state.Settings.RoomName, RoomTopicNames(state.Settings.Topics), _waitingRoomTopics);
            _roomTopicsFromWaitingRoom = true;
        }

        private void OpenPublicRoomTopics(string roomId)
        {
            var room = lobby.PublicRooms.FirstOrDefault(entry => entry.Id == roomId);
            if (inRoom || room == null) return;
            OpenRoomTopicsPopup(room.Name, RoomTopicNames(room.Topics), root.Q<Button>("public-room-topics-" + roomId));
            _roomTopicsPublicRoomId = roomId;
        }

        private void OpenRoomTopicsPopup(string roomName, string[] names, VisualElement returnFocus)
        {
            _roomTopicsPopup = Modal("주제", false);
            _roomTopicsPopup.name = "room-topics-popup";
            _roomTopicsPopup.AddToClassList("room-topics-popup");
            _popupReturnFocus = returnFocus;
            _roomTopicsRoomName = RawText(_roomTopicsPopup, "", "room-topics-room-name");
            _roomTopicsRoomName.name = "room-topics-room-name";
            _roomTopicsCount = Text(_roomTopicsPopup, "", "muted room-topics-count");
            _roomTopicsCount.name = "room-topics-count";
            _roomTopicsScroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical);
            _roomTopicsScroll.name = "room-topics-scroll";
            _roomTopicsScroll.AddToClassList("room-topics-scroll");
            _roomTopicsPopup.Add(_roomTopicsScroll);
            _roomTopicsGrid = Box(_roomTopicsScroll, "room-topics-grid");
            _roomTopicsGrid.name = "room-topics-grid";
            Button(_roomTopicsPopup, "닫기", CloseModal, "secondary room-topics-close").name = "room-topics-close";
            RefreshRoomTopicsContent(roomName, names);
            HideMobileScrollers();
        }

        private void RefreshRoomTopicsPopup()
        {
            if (_roomTopicsPopup == null) return;
            if (_roomTopicsFromWaitingRoom)
            {
                var state = network.State;
                if (!inRoom || state == null || state.Phase != GamePhase.Lobby) { CloseModal(); return; }
                RefreshRoomTopicsContent(state.Settings.RoomName, RoomTopicNames(state.Settings.Topics));
            }
            else
            {
                var room = lobby.PublicRooms.FirstOrDefault(entry => entry.Id == _roomTopicsPublicRoomId);
                if (inRoom || room == null) { CloseModal(); return; }
                RefreshRoomTopicsContent(room.Name, RoomTopicNames(room.Topics));
            }
        }

        private void RefreshRoomTopicsContent(string roomName, string[] names)
        {
            if (_roomTopicsRoomName.text == roomName && _roomTopicsVisibleNames.SequenceEqual(names)) return;
            SetRawText(_roomTopicsRoomName, roomName);
            SetText(_roomTopicsCount, "주제 {0}개", names.Length);
            _roomTopicsVisibleNames = names;
            _roomTopicsGrid.Clear();
            for (int index = 0; index < names.Length; index++)
                RawText(_roomTopicsGrid, names[index], "room-topic-chip").name = "room-topic-" + index;
            _roomTopicsScroll.scrollOffset = Vector2.zero;
        }

        private void ClearRoomTopicsPopup()
        {
            _roomTopicsPopup = _roomTopicsGrid = null;
            _roomTopicsRoomName = _roomTopicsCount = null;
            _roomTopicsScroll = null;
            _roomTopicsPublicRoomId = null;
            _roomTopicsFromWaitingRoom = false;
            _roomTopicsVisibleNames = Array.Empty<string>();
        }
    }
}
