using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private const int PC_ROOM_CHAT_LIMIT = 100;
        private const int MOBILE_ROOM_CHAT_LIMIT = 60;
        private const float ROOM_CHAT_BOTTOM_TOLERANCE = 24f;
        private RoomChatScrollUpdate _roomChatScrollUpdate;
        private bool _pcChatExpanded;
        private int _chatFocusVersion;
        private int _chatShortcutFrame = -1;
        private IVisualElementScheduledItem _pcChatTailUpdate;
        private ScrollView _mobileRoomChatHistory;
        private Button _mobileRoomChatLatest;
        private int _mobileRoomChatUnread;
        private bool _mobileRoomChatFollowsLatest = true, _mobileRoomChatAdjusting, _mobileRoomChatWasVisible;
        private VisualElement _mobileRoomChatAnchor;
        private float _mobileRoomChatAnchorY;
        private Vector2 _mobileRoomChatOffset;
        private IVisualElementScheduledItem _mobileRoomChatTailUpdate;

        private void CreatePcRoomChat(VisualElement parent)
        {
            var chat = _pcRoomChat = Box(parent, "pc-room-chat pc-chat-collapsed");
            chat.name = "pc-room-chat";
            var header = Box(chat, "row pc-chat-header");
            Text(header, "채팅", "pc-chat-title grow");
            chatOpen = Button(header, "", OpenChat, "chat-open grow");
            chatOpen.name = "pc-chat-open";
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, CloseChat, "pc-chat-close").name = "pc-chat-close";
            chatHistory = DrawSmoothScroll.Create(ScrollViewMode.Vertical);
            Classes(chatHistory, "chat-history pc-chat-scroll");
            chatHistory.name = "pc-chat-history";
            chatHistory.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            chatHistory.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            chat.Add(chatHistory);
            chatbar = Box(chat, "chatbar");
            chatbar.style.display = DisplayStyle.Flex;
            chatInput = new TextField { maxLength = 160, name = "room-chat-input" };
            Placeholder(chatInput, "채팅 입력");
            chatInput.AddToClassList("chat-input");
            chatInput.textEdition.autoCorrection = false;
            chatbar.Add(chatInput);
            chatInput.RegisterCallback<FocusInEvent>(_ => SetPcRoomChatExpanded(true));
            chatInput.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitChatInput();
                    evt.StopImmediatePropagation();
                }
            });
            Button(chatbar, "보내기", SendChat, "secondary", DrawSound.UiConfirm).name = "room-chat-send";
            chat.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_pcChatExpanded) return;
                for (var target = evt.target as VisualElement; target != null && target != chat; target = target.parent)
                    if (target.ClassListContains("profile-trigger")) return;
                OpenChat();
            });
            chat.RegisterCallback<FocusOutEvent>(_ => chat.schedule.Execute(() =>
            {
                var focused = root?.focusController?.focusedElement as VisualElement;
                if (_pcRoomChat == chat && _pcChatExpanded && (focused == null || !chat.Contains(focused))) CollapsePcRoomChat(false);
            }).StartingIn(1));
            chatHistory.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_pcRoomChat == chat && !_pcChatExpanded) ScrollPcRoomChatToLatest();
            });
            chatHistory.contentContainer.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_pcRoomChat == chat && !_pcChatExpanded) ScrollPcRoomChatToLatest();
            });
        }

        private void SetPcRoomChatExpanded(bool expanded)
        {
            if (_pcRoomChat == null) return;
            bool changed = _pcChatExpanded != expanded;
            if (changed && _roomChatScrollUpdate != null) CancelRoomChatScrollUpdate(_roomChatScrollUpdate);
            _pcChatExpanded = expanded;
            _pcRoomChat.EnableInClassList("pc-chat-collapsed", !expanded);
            _pcRoomChat.EnableInClassList("pc-chat-expanded", expanded);
            chatbar.style.display = DisplayStyle.Flex;
            chatOpen.style.display = expanded ? DisplayStyle.None : DisplayStyle.Flex;
            chatHistory.verticalScrollerVisibility = expanded ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden;
            if (changed || !expanded) ScrollPcRoomChatToLatest();
        }

        private void CollapsePcRoomChat(bool focusCanvas)
        {
            _chatFocusVersion++;
            _chatTransitionVersion++;
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.Blur(chatInput, focusCanvas);
#endif
            if (FocusedTextField() == chatInput) root.focusController?.focusedElement?.Blur();
            SetPcRoomChatExpanded(false);
        }

        private void SubmitChatInput()
        {
            if (chatInput == null) return;
            _chatShortcutFrame = Time.frameCount;
            if (string.IsNullOrWhiteSpace(chatInput.value)) { CloseChat(); return; }
            DrawAudio.Instance?.Play(DrawSound.UiConfirm);
            SendChat();
        }

        private bool ChatInputHasFocus()
        {
            var focused = FocusedTextField();
            return focused != null && (focused == chatInput || focused == _lobbyChatInput);
        }

        private VisualElement ChatShortcutTarget()
        {
            if (_profileOverlay != null || _roomPasswordOverlay != null || _roomCustomizeOverlay != null
                || _roomTopicWorkshopOverlay != null || _workshopPreviewOverlay != null) return null;
            bool lobbyChatModal = !inRoom && _lobbyChatPanel?.ClassListContains("lobby-chat-in-modal") == true
                && overlay?.Contains(_lobbyChatPanel) == true;
            if (overlay != null && !lobbyChatModal) return null;
            if (!inRoom && (lobby?.IsAuthenticated != true || lobbyScreen != LobbyScreen.Main)) return null;
            var input = inRoom ? chatInput : _lobbyChatInput;
            var focused = FocusedTextField();
            if (focused != null && focused != input) return null;
            if (input?.panel != null && !input.isReadOnly && input.enabledInHierarchy) return input;
            return !inRoom && IsMobile && overlay == null && _mobileLobbyChatButton?.panel != null
                ? _mobileLobbyChatButton : null;
        }

        private void RoomChatOutsidePointer(PointerDownEvent evt)
        {
            if (!inRoom || IsMobile || !_pcChatExpanded || _pcRoomChat == null) return;
            if (evt.target is VisualElement target && _pcRoomChat.Contains(target)) return;
            CollapsePcRoomChat(false);
        }

        private void ScrollPcRoomChatToLatest()
        {
            var history = chatHistory;
            if (history == null || _pcRoomChat == null) return;
            _pcChatTailUpdate?.Pause();
            var chat = _pcRoomChat;
            int version = _chatTransitionVersion;
            void AlignTail()
            {
                if (_pcRoomChat != chat || history != chatHistory || version != _chatTransitionVersion) return;
                history.scrollOffset = new Vector2(0, Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue));
            }
            AlignTail();
            _pcChatTailUpdate = history.schedule.Execute(AlignTail).StartingIn(20);
        }

        private sealed class RoomChatScrollUpdate
        {
            public ScrollView History;
            public VisualElement Entry, Anchor;
            public Vector2 Offset;
            public float AnchorY;
            public bool FollowLatest, OwnMessage, MobileHidden;
            public int MobileMessages;
            public IVisualElementScheduledItem Scheduled;
            public EventCallback<DetachFromPanelEvent> Detached;
        }

        private void AppendRoomChatLine(ChatLine line)
        {
            var history = chatHistory;
            if (history == null) return;
            bool mobile = IsMobile;
            if (mobile) EnsureMobileRoomChatHistory(history);
            bool ownMessage = network?.State?.LocalPlayerId == line.PlayerId;
            bool mobileHidden = mobile && (!IsVisible(history) || !_mobileRoomChatWasVisible);
            bool atBottom = mobileHidden ? _mobileRoomChatFollowsLatest : RoomChatAtBottom(history);
            int limit = mobile ? MOBILE_ROOM_CHAT_LIMIT : PC_ROOM_CHAT_LIMIT;
            var update = _roomChatScrollUpdate;
            if (update != null && (update.History != history || history.contentContainer.childCount == 0))
            {
                CancelRoomChatScrollUpdate(update);
                update = null;
            }
            if (update == null)
            {
                if (mobile && !mobileHidden) CaptureMobileRoomChatPosition();
                update = new RoomChatScrollUpdate
                {
                    History = history,
                    Offset = mobileHidden ? _mobileRoomChatOffset : history.scrollOffset,
                    FollowLatest = mobile && atBottom,
                    MobileHidden = mobileHidden
                };
                int removed = Mathf.Max(0, history.contentContainer.childCount + 1 - limit);
                update.Anchor = mobileHidden ? _mobileRoomChatAnchor : RoomChatAnchor(history, update.Offset.y, removed);
                if (update.Anchor != null) update.AnchorY = mobileHidden ? _mobileRoomChatAnchorY : update.Anchor.layout.y;
                _roomChatScrollUpdate = update;
            }
            bool compact = !mobile && !_pcChatExpanded;
            if (!mobile) update.FollowLatest |= compact || atBottom || ownMessage;
            update.OwnMessage |= !mobile && (compact || ownMessage);
            if (mobile) update.MobileMessages++;
            var entry = Box(history, "chat-entry");
            var player = network?.State?.Players.FirstOrDefault(value => value.Id == line.PlayerId);
            var name = LeveledName(entry, line.Name, line.Level, "chat-name", "chat-" + line.PlayerId, player?.AccountId, cosmetics: line.Cosmetics);
            BindProfileTarget(name, player?.AccountId);
            var message = RawText(entry, line.Text, "chat-message");
            message.tooltip = line.Text;
            DrawCosmeticPresentation.ApplyChatBubble(message, line.Cosmetics);
            while (history.contentContainer.childCount > limit)
            {
                var oldest = history.contentContainer.ElementAt(0);
                if (update.Anchor == oldest)
                {
                    float shift = oldest.layout.y - update.AnchorY;
                    update.Anchor = history.contentContainer.ElementAt(1);
                    update.AnchorY = update.Anchor.layout.y - shift;
                }
                oldest.RemoveFromHierarchy();
            }
            update.Entry = entry;
            Enter(entry, 140, 2);
            if (update.Scheduled != null) return;
            var pending = update;
            pending.Detached = _ => CancelRoomChatScrollUpdate(pending);
            history.RegisterCallback(pending.Detached);
            pending.Scheduled = history.schedule.Execute(() =>
            {
                CancelRoomChatScrollUpdate(pending);
                if (!isActiveAndEnabled || history != chatHistory || pending.Entry?.panel == null) return;
                if (!mobile && !_pcChatExpanded) { ScrollPcRoomChatToLatest(); return; }
                bool visible = IsVisible(history);
                if (mobile && !visible)
                {
                    _mobileRoomChatWasVisible = false;
                    SetMobileRoomChatUnread(pending.FollowLatest ? 0 : _mobileRoomChatUnread + pending.MobileMessages, pending.FollowLatest);
                    return;
                }
                var expectedOffset = new Vector2(
                    Mathf.Clamp(pending.Offset.x, history.horizontalScroller.lowValue, Mathf.Max(history.horizontalScroller.lowValue, history.horizontalScroller.highValue)),
                    Mathf.Clamp(pending.Offset.y, history.verticalScroller.lowValue, Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue)));
                var scrollDelta = pending.MobileHidden ? Vector2.zero : history.scrollOffset - expectedOffset;
                bool readingEarlier = scrollDelta.y < -ROOM_CHAT_BOTTOM_TOLERANCE;
                bool followLatest = pending.FollowLatest && (pending.OwnMessage || !readingEarlier);
                if (mobile) _mobileRoomChatAdjusting = true;
                if (followLatest)
                {
                    history.ScrollTo(pending.Entry);
                    if (mobile)
                    {
                        SetMobileRoomChatUnread(0, true);
                        _mobileRoomChatWasVisible = true;
                        _mobileRoomChatAdjusting = false;
                    }
                    return;
                }
                var offset = pending.Offset + scrollDelta;
                if (pending.Anchor?.panel != null && pending.Anchor.hierarchy.parent == history.contentContainer)
                    offset.y += pending.Anchor.layout.y - pending.AnchorY;
                history.scrollOffset = new Vector2(
                    Mathf.Clamp(offset.x, history.horizontalScroller.lowValue, Mathf.Max(history.horizontalScroller.lowValue, history.horizontalScroller.highValue)),
                    Mathf.Clamp(offset.y, history.verticalScroller.lowValue, Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue)));
                if (mobile)
                {
                    SetMobileRoomChatUnread(_mobileRoomChatUnread + pending.MobileMessages, false);
                    CaptureMobileRoomChatPosition();
                    _mobileRoomChatWasVisible = true;
                    _mobileRoomChatAdjusting = false;
                }
            }).StartingIn(20);
        }

        private static bool RoomChatAtBottom(ScrollView history) => history.contentContainer.childCount == 0
            || !(history.contentViewport.contentRect.height > 0)
            || history.scrollOffset.y >= history.verticalScroller.highValue - ROOM_CHAT_BOTTOM_TOLERANCE;

        private void EnsureMobileRoomChatHistory(ScrollView history)
        {
            if (_mobileRoomChatHistory == history) return;
            ClearMobileRoomChatHistory();
            _mobileRoomChatHistory = history;
            _mobileRoomChatFollowsLatest = true;
            _mobileRoomChatWasVisible = IsVisible(history);
            var parent = history.parent;
            _mobileRoomChatLatest = Button(parent, "", ScrollMobileRoomChatToLatest, "secondary mobile-room-chat-latest");
            _mobileRoomChatLatest.name = "mobile-room-chat-latest";
            _mobileRoomChatLatest.style.display = DisplayStyle.None;
            _mobileRoomChatLatest.style.flexShrink = 0;
            _mobileRoomChatLatest.style.minHeight = 44;
            _mobileRoomChatLatest.style.height = StyleKeyword.Auto;
            _mobileRoomChatLatest.style.whiteSpace = WhiteSpace.Normal;
            _mobileRoomChatLatest.RemoveFromHierarchy();
            parent.Insert(parent.IndexOf(history) + 1, _mobileRoomChatLatest);
            history.verticalScroller.valueChanged += OnMobileRoomChatScrolled;
            history.contentViewport.RegisterCallback<GeometryChangedEvent>(OnMobileRoomChatGeometryChanged);
            history.contentContainer.RegisterCallback<GeometryChangedEvent>(OnMobileRoomChatGeometryChanged);
            history.RegisterCallback<DetachFromPanelEvent>(OnMobileRoomChatDetached);
        }

        private void SetMobileRoomChatUnread(int count, bool followLatest)
        {
            _mobileRoomChatFollowsLatest = followLatest;
            _mobileRoomChatUnread = Mathf.Clamp(count, 0, MOBILE_ROOM_CHAT_LIMIT);
            if (_mobileRoomChatLatest == null) return;
            SetText(_mobileRoomChatLatest, "새 메시지 {0}개", _mobileRoomChatUnread);
            _mobileRoomChatLatest.style.display = _mobileRoomChatUnread > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnMobileRoomChatScrolled(float value)
        {
            var history = _mobileRoomChatHistory;
            if (history == null || history != chatHistory) return;
            if (!IsVisible(history)) { _mobileRoomChatWasVisible = false; return; }
            if (_mobileRoomChatAdjusting || !_mobileRoomChatWasVisible || _roomChatScrollUpdate?.History == history) return;
            bool atBottom = RoomChatAtBottom(history);
            SetMobileRoomChatUnread(atBottom ? 0 : _mobileRoomChatUnread, atBottom);
            CaptureMobileRoomChatPosition();
        }

        private void OnMobileRoomChatGeometryChanged(GeometryChangedEvent evt)
        {
            var history = _mobileRoomChatHistory;
            if (history == null || history != chatHistory) return;
            if (!IsVisible(history) || !(history.contentViewport.contentRect.height > 0))
            {
                _mobileRoomChatWasVisible = false;
                return;
            }
            bool restorePosition = !_mobileRoomChatWasVisible;
            if ((!_mobileRoomChatFollowsLatest && !restorePosition) || _roomChatScrollUpdate?.History == history) return;
            _mobileRoomChatTailUpdate?.Pause();
            _mobileRoomChatTailUpdate = history.schedule.Execute(() =>
            {
                if (_mobileRoomChatHistory != history || history != chatHistory || !IsVisible(history)
                    || _roomChatScrollUpdate?.History == history) return;
                if (_mobileRoomChatFollowsLatest) ScrollMobileRoomChatToLatest();
                else if (restorePosition) RestoreMobileRoomChatPosition();
            }).StartingIn(1);
        }

        private void CaptureMobileRoomChatPosition()
        {
            var history = _mobileRoomChatHistory;
            if (history == null || !IsVisible(history)) return;
            _mobileRoomChatOffset = history.scrollOffset;
            _mobileRoomChatAnchor = RoomChatAnchor(history, _mobileRoomChatOffset.y, 0);
            if (_mobileRoomChatAnchor != null) _mobileRoomChatAnchorY = _mobileRoomChatAnchor.layout.y;
        }

        private void RestoreMobileRoomChatPosition()
        {
            var history = _mobileRoomChatHistory;
            float offset = _mobileRoomChatOffset.y;
            if (_mobileRoomChatAnchor?.hierarchy.parent == history.contentContainer)
                offset += _mobileRoomChatAnchor.layout.y - _mobileRoomChatAnchorY;
            else offset = 0;
            _mobileRoomChatAdjusting = true;
            history.scrollOffset = new Vector2(0, Mathf.Clamp(offset, history.verticalScroller.lowValue,
                Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue)));
            CaptureMobileRoomChatPosition();
            _mobileRoomChatWasVisible = true;
            _mobileRoomChatAdjusting = false;
        }

        private void ScrollMobileRoomChatToLatest()
        {
            var history = _mobileRoomChatHistory;
            if (history == null || history != chatHistory) return;
            if (_roomChatScrollUpdate?.History == history) CancelRoomChatScrollUpdate(_roomChatScrollUpdate);
            _mobileRoomChatAdjusting = true;
            SetMobileRoomChatUnread(0, true);
            history.scrollOffset = new Vector2(0, Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue));
            _mobileRoomChatWasVisible = true;
            _mobileRoomChatAdjusting = false;
        }

        private void OnMobileRoomChatDetached(DetachFromPanelEvent evt)
        {
            if (ReferenceEquals(evt.target, _mobileRoomChatHistory)) ClearMobileRoomChatHistory();
        }

        private void ClearMobileRoomChatHistory()
        {
            _mobileRoomChatTailUpdate?.Pause();
            _mobileRoomChatTailUpdate = null;
            if (_mobileRoomChatHistory != null)
            {
                _mobileRoomChatHistory.verticalScroller.valueChanged -= OnMobileRoomChatScrolled;
                _mobileRoomChatHistory.contentViewport.UnregisterCallback<GeometryChangedEvent>(OnMobileRoomChatGeometryChanged);
                _mobileRoomChatHistory.contentContainer.UnregisterCallback<GeometryChangedEvent>(OnMobileRoomChatGeometryChanged);
                _mobileRoomChatHistory.UnregisterCallback<DetachFromPanelEvent>(OnMobileRoomChatDetached);
            }
            _mobileRoomChatHistory = null;
            _mobileRoomChatLatest?.RemoveFromHierarchy();
            _mobileRoomChatLatest = null;
            _mobileRoomChatUnread = 0;
            _mobileRoomChatFollowsLatest = true;
            _mobileRoomChatAnchor = null;
            _mobileRoomChatOffset = Vector2.zero;
            _mobileRoomChatWasVisible = false;
            _mobileRoomChatAdjusting = false;
        }

        private static VisualElement RoomChatAnchor(ScrollView history, float offset, int removed)
        {
            for (int index = removed; index < history.contentContainer.childCount; index++)
            {
                var candidate = history.contentContainer.ElementAt(index);
                if (candidate.layout.yMax > offset) return candidate;
            }
            return null;
        }

        private void CancelRoomChatScrollUpdate(RoomChatScrollUpdate update)
        {
            update.Scheduled?.Pause();
            if (update.Detached != null) update.History.UnregisterCallback(update.Detached);
            if (_roomChatScrollUpdate == update) _roomChatScrollUpdate = null;
        }
    }
}
