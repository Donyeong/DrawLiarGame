using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _socialList;
        private bool _socialEventsAttached, _socialActionBusy;
        private string _sentInviteRoomCode = "";
        private readonly Dictionary<string, float> _sentInvites = new Dictionary<string, float>();

        private void AttachSocialEvents()
        {
            if (lobby == null || _socialEventsAttached) return;
            lobby.SocialChanged += OnSocialChanged;
            lobby.SocialNotice += Toast;
            _socialEventsAttached = true;
        }

        private void DetachSocialEvents()
        {
            if (lobby == null || !_socialEventsAttached) return;
            lobby.SocialChanged -= OnSocialChanged;
            lobby.SocialNotice -= Toast;
            _socialEventsAttached = false;
        }

        private void SocialBell(VisualElement parent)
        {
            if (!lobby.IsAuthenticated) return;
            var button = IconButton(parent, "알림", DrawUIIcon.Kind.Bell,
                () => OpenUtilityPopup(LobbyScreen.Notifications), "social-notifications");
            button.AddToClassList("social-bell");
            var badge = RawText(button, "", "social-badge");
            badge.pickingMode = PickingMode.Ignore;
            badge.languageDirection = LanguageDirection.LTR;
            RefreshSocialControls();
        }

        private void RoomFriendInviteButton(VisualElement parent, string classes)
        {
            if (!lobby.IsOnlineRoom) return;
            Button(parent, "친구 초대", () => OpenUtilityPopup(LobbyScreen.InviteFriends), classes).name = "room-invite-friends";
        }

        private void OnSocialChanged()
        {
            RefreshSocialControls();
            RefreshPublicProfileFriendship();
            if (_socialActionBusy) return;
            RefreshFriends();
            RefreshSocialList();
            RefreshPublicProfileActions();
        }

        private void RefreshSocialControls()
        {
            if (root == null || lobby == null) return;
            int count = lobby.NotificationCount;
            root.Query<Button>(className: "social-bell").ForEach(button =>
            {
                var badge = button.Q<Label>(className: "social-badge");
                if (badge == null) return;
                SetRawText(badge, count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture));
                badge.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                SetTooltip(button, count > 0 ? "알림 {0}개" : "알림", count > 0 ? new object[] { count } : Array.Empty<object>());
                button.SetEnabled(lobby.IsAuthenticated);
            });
            root.Query<Button>("room-invite-friends").ForEach(button => button.SetEnabled(lobby.IsOnlineRoom));
            if (_sentInviteRoomCode != lobby.RoomCode)
            {
                _sentInviteRoomCode = lobby.RoomCode;
                _sentInvites.Clear();
            }
            bool busy = runningAction || lobby.IsBusy || _socialActionBusy;
            root.Query<Button>(className: "social-action").ForEach(button => button.SetEnabled(!busy));
            root.Query<Button>(className: "social-invite-send").ForEach(button =>
            {
                string accountId = button.userData as string;
                bool sent = accountId != null && _sentInvites.TryGetValue(accountId, out float until) && Time.unscaledTime < until;
                bool inThisRoom = network.State?.Players.Any(player => player.AccountId == accountId && player.IsConnected) == true;
                SetText(button, sent ? "초대됨" : "초대");
                SetTooltip(button, inThisRoom ? "이미 같은 방에 있습니다." : sent ? "초대됨" : "친구 초대");
                button.SetEnabled(!busy && lobby.IsOnlineRoom && !sent && !inThisRoom);
            });
        }

        private void SocialForm(VisualElement form, LobbyScreen screen)
        {
            _socialList = Box(form, "social-list");
            _socialList.name = screen == LobbyScreen.Notifications ? "social-inbox-list" : "room-invite-list";
            RefreshSocialList();
        }

        private void RefreshSocialList()
        {
            if (_socialList?.panel == null || _socialActionBusy) return;
            var scroll = _socialList.GetFirstAncestorOfType<ScrollView>();
            Vector2 offset = scroll?.scrollOffset ?? Vector2.zero;
            _socialList.Clear();
            if (_utilityPopupScreen == LobbyScreen.InviteFriends)
            {
                if (!lobby.IsOnlineRoom) return;
                if (lobby.Friends.Friends.Length == 0) Text(_socialList, "친구가 없습니다.", "social-empty muted");
                foreach (var friend in lobby.Friends.Friends)
                {
                    var row = Box(_socialList, "social-card social-invite-card row");
                    FriendProfileIdentity(row, friend);
                    var send = Button(row, "초대", () => RunSocialAction(async () =>
                    {
                        await lobby.InviteFriendAsync(friend.AccountId);
                        _sentInvites[friend.AccountId] = Time.unscaledTime + 30;
                    }), "primary social-invite-send");
                    send.name = "room-invite-" + friend.AccountId;
                    send.userData = friend.AccountId;
                }
            }
            else if (_utilityPopupScreen == LobbyScreen.Notifications)
            {
                if (lobby.NotificationCount == 0) Text(_socialList, "알림이 없습니다.", "social-empty muted");
                if (lobby.Friends.Incoming.Length > 0) Text(_socialList, "받은 요청", "section-title");
                foreach (var friend in lobby.Friends.Incoming)
                {
                    var row = Box(_socialList, "social-card");
                    FriendProfileIdentity(row, friend);
                    var actions = Box(row, "row social-card-actions");
                    Button(actions, "거절", () => RunSocialAction(() => lobby.RespondFriendAsync(friend.AccountId, false)),
                        "secondary social-action").name = "notification-friend-reject-" + friend.AccountId;
                    Button(actions, "수락", () => RunSocialAction(() => lobby.RespondFriendAsync(friend.AccountId, true)),
                        "primary social-action").name = "notification-friend-accept-" + friend.AccountId;
                }
                var invitations = lobby.Inbox.RoomInvitations;
                if (invitations.Length > 0) Text(_socialList, "방 초대", "section-title");
                foreach (var invitation in invitations)
                {
                    var row = Box(_socialList, "social-card");
                    FriendProfileIdentity(row, invitation.Sender);
                    RawText(row, invitation.RoomName, "social-room-name");
                    var actions = Box(row, "row social-card-actions");
                    Button(actions, "거절", () => RunSocialAction(() => lobby.RespondRoomInvitationAsync(invitation.InvitationId, false)),
                        "secondary social-action").name = "notification-invite-reject-" + invitation.InvitationId;
                    Button(actions, "수락", () => AcceptRoomInvitation(invitation),
                        "primary social-action").name = "notification-invite-accept-" + invitation.InvitationId;
                }
            }
            RefreshSocialControls();
            if (scroll != null) scroll.scrollOffset = offset;
        }

        private void RunSocialAction(Func<Task> action)
        {
            Run(async () =>
            {
                _socialActionBusy = true;
                RefreshSocialControls();
                try { await action(); }
                finally
                {
                    _socialActionBusy = false;
                    RefreshPublicProfileFriendship();
                    RefreshSocialList();
                    RefreshSocialControls();
                }
            });
        }

        private void AcceptRoomInvitation(RoomInvitationData invitation)
        {
            if (runningAction || lobby.IsBusy || _socialActionBusy) return;
            if (lobby.IsOnlineRoom && invitation.RoomCode == lobby.RoomCode)
            {
                Toast("이미 같은 방에 있습니다.");
                return;
            }
            if (!lobby.IsOnlineRoom)
            {
                JoinRoomInvitation(invitation, false, overlay);
                return;
            }
            var modal = Modal("방 초대", false);
            modal.name = "room-invite-confirm";
            modal.AddToClassList("utility-popup");
            modal.AddToClassList("social-confirm-popup");
            RawText(modal, invitation.RoomName, "social-confirm-room");
            Text(modal, "현재 방에서 나가 초대받은 방으로 이동할까요?", "subtitle");
            var confirmation = overlay;
            var actions = Box(modal, "row social-confirm-actions");
            Button(actions, "취소", () => OpenUtilityPopup(LobbyScreen.Notifications), "secondary grow", DrawSound.UiCancel).name = "room-invite-confirm-cancel";
            Button(actions, "이동", () => JoinRoomInvitation(invitation, true, confirmation), "primary grow social-action").name = "room-invite-confirm-accept";
        }

        private void JoinRoomInvitation(RoomInvitationData invitation, bool leaveCurrentRoom, VisualElement origin)
        {
            RunSocialAction(async () =>
            {
                await lobby.RespondRoomInvitationAsync(invitation.InvitationId, true, leaveCurrentRoom);
                if (overlay == origin || _utilityPopupScreen == LobbyScreen.Notifications) CloseModal();
            });
        }
    }
}
