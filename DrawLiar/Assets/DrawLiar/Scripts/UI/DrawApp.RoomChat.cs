using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private const int PC_ROOM_CHAT_LIMIT = 100;
        private const int MOBILE_ROOM_CHAT_LIMIT = 6;
        private const float ROOM_CHAT_BOTTOM_TOLERANCE = 24f;
        private RoomChatScrollUpdate _roomChatScrollUpdate;

        private sealed class RoomChatScrollUpdate
        {
            public ScrollView History;
            public VisualElement Entry, Anchor;
            public Vector2 Offset;
            public float AnchorY;
            public bool FollowLatest, OwnMessage;
            public IVisualElementScheduledItem Scheduled;
            public EventCallback<DetachFromPanelEvent> Detached;
        }

        private void AppendRoomChatLine(ChatLine line)
        {
            var history = chatHistory;
            if (history == null) return;
            bool mobile = IsMobile;
            bool ownMessage = network?.State?.LocalPlayerId == line.PlayerId;
            bool atBottom = history.contentContainer.childCount == 0 || !(history.contentViewport.contentRect.height > 0)
                || history.scrollOffset.y >= history.verticalScroller.highValue - ROOM_CHAT_BOTTOM_TOLERANCE;
            int limit = mobile ? MOBILE_ROOM_CHAT_LIMIT : PC_ROOM_CHAT_LIMIT;
            var update = _roomChatScrollUpdate;
            if (update != null && (update.History != history || history.contentContainer.childCount == 0))
            {
                CancelRoomChatScrollUpdate(update);
                update = null;
            }
            if (update == null)
            {
                update = new RoomChatScrollUpdate { History = history, Offset = history.scrollOffset };
                if (!mobile)
                {
                    int removed = Mathf.Max(0, history.contentContainer.childCount + 1 - limit);
                    update.Anchor = RoomChatAnchor(history, update.Offset.y, removed);
                    if (update.Anchor != null) update.AnchorY = update.Anchor.layout.y;
                }
                _roomChatScrollUpdate = update;
            }
            update.FollowLatest |= mobile || atBottom || ownMessage;
            update.OwnMessage |= mobile || ownMessage;
            var entry = Box(history, "chat-entry");
            var name = RawText(entry, line.Name, "chat-name");
            var player = network?.State?.Players.FirstOrDefault(value => value.Id == line.PlayerId);
            BindProfileTarget(name, player?.AccountId);
            var message = RawText(entry, line.Text, "chat-message");
            message.tooltip = line.Text;
            while (history.contentContainer.childCount > limit) history.contentContainer.ElementAt(0).RemoveFromHierarchy();
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
                bool readingEarlier = history.scrollOffset.y < pending.Offset.y - ROOM_CHAT_BOTTOM_TOLERANCE;
                if (pending.FollowLatest && (pending.OwnMessage || !readingEarlier))
                {
                    history.ScrollTo(pending.Entry);
                    return;
                }
                var offset = history.scrollOffset;
                if (pending.Anchor?.panel != null && pending.Anchor.hierarchy.parent == history.contentContainer)
                    offset.y += pending.Anchor.layout.y - pending.AnchorY;
                history.scrollOffset = new Vector2(
                    Mathf.Clamp(offset.x, history.horizontalScroller.lowValue, Mathf.Max(history.horizontalScroller.lowValue, history.horizontalScroller.highValue)),
                    Mathf.Clamp(offset.y, history.verticalScroller.lowValue, Mathf.Max(history.verticalScroller.lowValue, history.verticalScroller.highValue)));
            }).StartingIn(20);
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
