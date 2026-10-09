using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _profileOverlay, _profileModal, _profileBody, _profileReturnFocus;
        private CancellationTokenSource _profileCancellation;
        private string _profileAccountId, _profileViewerAccountId;
        private int _profileVersion;
        private bool _profileActionBusy;
        private PublicProfileData _loadedPublicProfile;
        private VisualElement _profileKickOverlay;
        private Button _profileKickButton, _profileKickConfirm;
        private bool _profileKickBusy;
        private int _profileKickPlayerId = -1, _profileKickActorId = -1, _profileKickVersion;
        private string _profileKickAccountId, _profileKickRoomCode;
        private VisualElement _roomModerationModal, _roomModerationList;
        private string _roomModerationKey;
        private VisualElement _roomCustomizeOverlay;
        private string _roomCustomizeAccountId;
        private VisualElement _roomCustomizeReturnFocus;

        private void BindProfileTarget(VisualElement target, string accountId, Func<bool> canOpen = null)
        {
            if (target == null || !Guid.TryParse(accountId, out _)) return;
            target.name = "profile-open-" + accountId;
            target.pickingMode = PickingMode.Position;
            target.focusable = true;
            target.tabIndex = 0;
            target.AddToClassList("profile-trigger");
            SetTooltip(target, "프로필 보기");
            target.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button != 0 || canOpen?.Invoke() == false) return;
                OpenPublicProfile(accountId, target);
                evt.StopImmediatePropagation();
            });
            target.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Space) return;
                if (canOpen?.Invoke() == false) return;
                OpenPublicProfile(accountId, target);
                evt.StopImmediatePropagation();
            });
            target.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                if (canOpen?.Invoke() == false) return;
                OpenPublicProfile(accountId, target);
                evt.StopImmediatePropagation();
            });
        }

        private void FriendProfileIdentity(VisualElement row, FriendData friend)
        {
            var identity = Box(row, "row friend-profile-identity grow");
            identity.Add(new AvatarElement(friend.AvatarColor, friend.Accessory));
            LeveledName(identity, friend.DisplayName, friend.Level, "player-name grow", "friend-" + friend.AccountId, friend.AccountId);
            BindProfileTarget(identity, friend.AccountId);
        }

        private void OpenPublicProfile(string accountId, VisualElement origin = null)
        {
            if (root == null || !lobby.IsAuthenticated || !Guid.TryParse(accountId, out _)) return;
            if (_profileOverlay != null && _profileAccountId == accountId) return;
            surface?.CancelDrawing();
            var returnFocus = origin ?? root.focusController?.focusedElement as VisualElement;
            ClosePublicProfile(false);
            _profileReturnFocus = returnFocus;
            _profileAccountId = accountId;
            _profileViewerAccountId = lobby.Profile?.AccountId;
            var popup = _profileOverlay = Box(root, "overlay enter utility-overlay profile-overlay");
            popup.name = "profile-overlay";
            var modal = _profileModal = Box(popup, "modal utility-popup public-profile-popup");
            modal.name = "public-profile-popup";
            var header = Box(modal, "row utility-popup-header");
            Text(header, "프로필", "utility-popup-title grow");
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, () => ClosePublicProfile(), "utility-popup-x").name = "public-profile-x";
            var scroll = DrawSmoothScroll.Create();
            scroll.AddToClassList("utility-popup-scroll");
            scroll.AddToClassList("public-profile-scroll");
            modal.Add(scroll);
            _profileBody = Box(scroll, "public-profile-body");
            _profileBody.name = "public-profile-body";
            var footer = Box(modal, "row public-profile-footer");
            _profileKickButton = Button(footer, "강퇴", OpenPublicProfileKick, "danger grow public-profile-kick");
            _profileKickButton.name = "public-profile-kick";
            Button(footer, _utilityPopupScreen == LobbyScreen.Friends ? "친구 목록으로" : "닫기",
                () => ClosePublicProfile(), "secondary grow utility-popup-close", DrawSound.UiCancel).name = "public-profile-close";
            popup.focusable = true;
            popup.Focus();
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_profileOverlay != popup || !ReferenceEquals(evt.target, popup)) return;
                ClosePublicProfile();
                evt.StopImmediatePropagation();
            });
            popup.RegisterCallback<KeyDownEvent>(PublicProfileShortcut, TrickleDown.TrickleDown);
            popup.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup) && _profileOverlay == popup) ClosePublicProfile(false);
            });
            DrawUIMotion.ShowModal(popup, modal);
            HideMobileScrollers();
            BeginPublicProfileLoad();
            RefreshPublicProfileActions();
        }

        private void PublicProfileShortcut(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                ClosePublicProfile();
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.keyCode != KeyCode.Tab || _profileModal == null) return;
            var controls = _profileModal.Query<VisualElement>().ToList().Where(control => control.canGrabFocus
                && control.enabledInHierarchy && control.tabIndex >= 0 && control.resolvedStyle.visibility == Visibility.Visible
                && IsVisible(control)).ToList();
            if (controls.Count == 0) return;
            int index = controls.IndexOf(root.focusController?.focusedElement as VisualElement);
            int next = index < 0 ? (evt.shiftKey ? controls.Count - 1 : 0)
                : (index + (evt.shiftKey ? -1 : 1) + controls.Count) % controls.Count;
            root.focusController?.IgnoreEvent(evt);
            controls[next].Focus();
            evt.StopImmediatePropagation();
        }

        private void BeginPublicProfileLoad()
        {
            if (_profileOverlay == null) return;
            CancelPublicProfileRequest();
            _profileActionBusy = false;
            _loadedPublicProfile = null;
            _profileCancellation = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            int version = ++_profileVersion;
            var popup = _profileOverlay;
            var token = _profileCancellation.Token;
            _profileBody.Clear();
            Text(_profileBody, "프로필을 불러오는 중…", "public-profile-message");
            _ = LoadPublicProfileAsync(popup, version, token);
        }

        private bool IsCurrentPublicProfile(VisualElement popup, int version, CancellationToken token)
        {
            return !token.IsCancellationRequested && _profileOverlay == popup && popup.panel != null
                && _profileVersion == version && lobby.IsAuthenticated && lobby.Profile?.AccountId == _profileViewerAccountId;
        }

        private async Task LoadPublicProfileAsync(VisualElement popup, int version, CancellationToken token)
        {
            try
            {
                var profile = await lobby.GetPublicProfileAsync(_profileAccountId, token);
                if (IsCurrentPublicProfile(popup, version, token)) RenderPublicProfile(profile);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!IsCurrentPublicProfile(popup, version, token)) return;
                _profileBody.Clear();
                Text(_profileBody, "프로필을 불러오지 못했습니다.", "public-profile-message");
                Button(_profileBody, "다시 시도", BeginPublicProfileLoad, "secondary").name = "public-profile-retry";
            }
        }

        private void RenderPublicProfile(PublicProfileData profile)
        {
            _loadedPublicProfile = profile;
            _profileBody.Clear();
            var hero = Box(_profileBody, "row public-profile-hero");
            var avatar = new AvatarElement(profile.AvatarColor, profile.Accessory);
            avatar.AddToClassList("public-profile-avatar");
            hero.Add(avatar);
            var identity = Box(hero, "grow public-profile-identity");
            LeveledName(identity, profile.DisplayName, profile.Level, "public-profile-name", "public-profile-name", profile.AccountId,
                profile.AccountId == lobby.Profile?.AccountId ? OwnSubscriberBadge : profile.ShowSubscriberBadge);
            var joined = Box(identity, "row public-profile-joined");
            Text(joined, "가입일", "public-profile-label");
            RawText(joined, PublicProfileDate(profile.JoinedAt), "public-profile-label");
            if (profile.AccountId == lobby.Profile?.AccountId) AccountExperience(_profileBody);
            var actions = Box(_profileBody, "row public-profile-friend-actions");
            RenderPublicProfileFriendship(actions, profile);
            Text(_profileBody, "통산 전적", "section-title");
            var stats = profile.Stats ?? new ProfileStatsData();
            var grid = Box(_profileBody, "row public-profile-stat-grid");
            PublicProfileStat(grid, "경기 수", stats.MatchesPlayed);
            PublicProfileStat(grid, "승리 수", stats.MatchesWon);
            PublicProfileStatValue(grid, "승률", (stats.MatchesPlayed == 0 ? 0 : (double)stats.MatchesWon / stats.MatchesPlayed).ToString("P0", PublicProfileCulture()));
            PublicProfileStat(grid, "총 점수", stats.TotalScore);
            PublicProfileStat(grid, "최고 점수", stats.BestScore);
            PublicProfileStat(grid, "플레이 라운드", stats.RoundsPlayed);
            PublicProfileStat(grid, "시민 라운드", stats.CitizenRounds);
            PublicProfileStat(grid, "라이어 라운드", stats.LiarRounds);
            PublicProfileStat(grid, "정답 투표", stats.CorrectVotes);
            PublicProfileStat(grid, "정답 추측", stats.CorrectGuesses);
            Text(_profileBody, "최근 경기", "section-title");
            var matches = profile.RecentMatches ?? Array.Empty<ProfileMatchData>();
            if (matches.Length == 0) Text(_profileBody, "완료한 경기 기록이 없습니다.", "public-profile-empty");
            foreach (var match in matches.Take(10))
            {
                if (match == null) continue;
                var card = Box(_profileBody, "public-profile-match");
                var heading = Box(card, "row");
                RawText(heading, PublicProfileDate(match.PlayedAt, true), "grow public-profile-label");
                Text(heading, match.Won ? "승리" : "패배", match.Won ? "public-profile-win" : "public-profile-loss");
                var summary = Box(card, "row public-profile-match-summary");
                Text(summary, "{0}위 / {1}명", "grow", match.Rank, match.PlayerCount);
                Text(summary, "{0}점", "public-profile-match-score", match.Score);
                var detail = Box(card, "row public-profile-match-detail");
                Text(detail, match.Mode == (int)DrawingMode.Individual ? "한 명씩 그리기" : "릴레이 그리기", "grow public-profile-label");
                Text(detail, "{0}라운드", "public-profile-label", match.RoundCount);
            }
        }

        private static void PublicProfileStat(VisualElement grid, string title, int value)
        {
            PublicProfileStatValue(grid, title, value.ToString("N0", PublicProfileCulture()));
        }

        private static void PublicProfileStatValue(VisualElement grid, string title, string value)
        {
            var stat = Box(grid, "public-profile-stat");
            RawText(stat, value, "public-profile-stat-value");
            Text(stat, title, "public-profile-label");
        }

        private void RenderPublicProfileFriendship(VisualElement actions, PublicProfileData profile)
        {
            switch (profile.Friendship)
            {
                case "Self":
                    Text(actions, "내 프로필", "public-profile-state");
                    if (profile.AccountId == lobby.Profile?.AccountId)
                        Button(actions, "꾸미기", OpenSelfCustomization, "primary").name = "public-profile-customize";
                    break;
                case "Friends": Text(actions, "친구", "public-profile-state"); break;
                case "Outgoing":
                    Text(actions, "수락 대기", "public-profile-state");
                    Button(actions, "신청 취소", () => StartPublicProfileFriendAction(() => lobby.CancelFriendRequestAsync(profile.AccountId)), "secondary").name = "public-profile-request-cancel";
                    break;
                case "Incoming":
                    Button(actions, "수락", () => StartPublicProfileFriendAction(() => lobby.RespondFriendAsync(profile.AccountId, true)), "primary").name = "public-profile-accept";
                    Button(actions, "거절", () => StartPublicProfileFriendAction(() => lobby.RespondFriendAsync(profile.AccountId, false)), "secondary").name = "public-profile-reject";
                    break;
                case "None":
                    Button(actions, "친구 요청", () => StartPublicProfileFriendAction(() => lobby.RequestFriendAsync(profile.AccountId)), "primary").name = "public-profile-request";
                    break;
            }
            RefreshPublicProfileActions();
        }

        private void RefreshPublicProfileActions()
        {
            _profileBody?.Q<VisualElement>(className: "public-profile-friend-actions")?.Query<Button>()
                .ForEach(button => button.SetEnabled(!_profileActionBusy && !_profileKickBusy && !lobby.IsBusy));
            RefreshPublicProfileKick();
        }

        private void OpenRoomModeration()
        {
            if (!inRoom || !lobby.IsOnlineRoom || network.State?.IsHost != true) return;
            var modal = _roomModerationModal = Modal("참가자 관리", false);
            modal.name = "room-moderation-popup";
            modal.AddToClassList("room-moderation-popup");
            var scroll = DrawSmoothScroll.Create();
            scroll.name = "room-moderation-scroll";
            scroll.AddToClassList("room-moderation-scroll"); modal.Add(scroll);
            _roomModerationList = Box(scroll, "room-moderation-list");
            _roomModerationList.name = "room-moderation-list";
            _roomModerationKey = null;
            var actions = Box(modal, "row room-moderation-actions");
            var back = Button(actions, "뒤로", RoomMenu, "secondary grow room-moderation-back", DrawSound.UiCancel);
            back.name = "room-moderation-back";
            Button(actions, "닫기", CloseModal, "secondary grow", DrawSound.UiCancel).name = "room-moderation-close";
            modal.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (!ReferenceEquals(evt.target, modal) || _roomModerationModal != modal) return;
                _roomModerationModal = _roomModerationList = null; _roomModerationKey = null;
            });
            RefreshRoomModeration(network.State); back.Focus();
        }

        private void RefreshRoomModeration(RoomSnapshot state)
        {
            var modal = _roomModerationModal;
            if (modal == null || _roomModerationList == null) return;
            if (overlay == null || modal.parent != overlay || modal.panel == null)
            {
                _roomModerationModal = _roomModerationList = null; _roomModerationKey = null;
                return;
            }
            if (!inRoom || !lobby.IsOnlineRoom || state?.IsHost != true) { CloseModal(); return; }
            var players = state.Players.Where(player => player.IsConnected && player.Id != state.LocalPlayerId
                && network.CanKickPlayer(player.Id) && Guid.TryParse(player.AccountId, out _)).ToArray();
            string key = string.Join("|", players.Select(player => $"{player.Id}/{player.AccountId}/{player.Name}/{player.Level}/{player.IsSpectator}"));
            if (key == _roomModerationKey) return;
            _roomModerationKey = key;
            var scroll = _roomModerationList.GetFirstAncestorOfType<ScrollView>();
            DrawSmoothScroll.Bind(scroll);
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            _roomModerationList.Clear();
            foreach (var player in players)
            {
                var row = Box(_roomModerationList, "row room-moderation-player");
                row.name = "room-moderation-player-" + player.Id;
                LeveledName(row, player.Name, player.Level, "grow room-moderation-name", "moderation-" + player.Id, player.AccountId);
                if (player.IsSpectator) Text(row, "관전", "room-moderation-spectator");
                Button profile = null;
                profile = Button(row, "프로필 보기", () =>
                {
                    if (_roomModerationModal != modal || modal.parent != overlay || !network.CanKickPlayer(player.Id)) return;
                    var current = network.State?.Players.FirstOrDefault(value => value.Id == player.Id && value.IsConnected);
                    if (current?.AccountId == player.AccountId) OpenPublicProfile(player.AccountId, profile);
                }, "secondary room-moderation-profile");
                profile.name = "room-moderation-profile-" + player.Id;
            }
        }

        private PlayerView PublicProfileKickTarget()
        {
            if (!inRoom || !lobby.IsOnlineRoom || _profileOverlay == null || !lobby.IsAuthenticated
                || lobby.Profile?.AccountId != _profileViewerAccountId) return null;
            var state = network.State;
            if (state == null || !state.IsHost) return null;
            return state.Players.FirstOrDefault(player => player.AccountId == _profileAccountId
                && player.IsConnected && player.Id != state.LocalPlayerId);
        }

        private void RefreshPublicProfileKick()
        {
            if (_profileKickButton == null) return;
            var target = PublicProfileKickTarget();
            bool allowed = target != null && network.CanKickPlayer(target.Id);
            _profileKickButton.style.display = target != null ? DisplayStyle.Flex : DisplayStyle.None;
            _profileKickButton.SetEnabled(allowed && !_profileKickBusy && !_profileActionBusy && !lobby.IsBusy);
            if (_profileKickOverlay == null) return;
            if (target == null || target.Id != _profileKickPlayerId || target.AccountId != _profileKickAccountId
                || lobby.RoomCode != _profileKickRoomCode || network.State.LocalPlayerId != _profileKickActorId
                || !allowed && !_profileKickBusy)
            {
                ClosePublicProfileKick(false);
                return;
            }
            _profileKickConfirm?.SetEnabled(allowed && !_profileKickBusy && !_profileActionBusy && !lobby.IsBusy);
        }

        private void OpenPublicProfileKick()
        {
            if (_profileKickBusy || _profileActionBusy || lobby.IsBusy) return;
            var target = PublicProfileKickTarget();
            if (target == null || !network.CanKickPlayer(target.Id)) return;
            ClosePublicProfileKick(false, true);
            _profileKickPlayerId = target.Id;
            _profileKickAccountId = target.AccountId;
            _profileKickRoomCode = lobby.RoomCode;
            _profileKickActorId = network.State.LocalPlayerId;
            _profileModal.SetEnabled(false);
            var popup = _profileKickOverlay = Box(root, "overlay enter utility-overlay profile-kick-overlay");
            popup.name = "profile-kick-overlay";
            var modal = Box(popup, "modal utility-popup profile-kick-popup");
            modal.name = "profile-kick-popup";
            Text(modal, "강퇴", "title");
            var scroll = DrawSmoothScroll.Create();
            scroll.AddToClassList("profile-kick-scroll"); modal.Add(scroll);
            Text(scroll, "{0}님을 강퇴할까요?", "profile-kick-question", target.Name);
            Text(scroll, "강퇴하면 이 방에 다시 입장할 수 없습니다.", "rules profile-kick-note");
            var actions = Box(modal, "row profile-kick-actions");
            var cancel = Button(actions, "취소", () => ClosePublicProfileKick(), "secondary grow", DrawSound.UiCancel);
            cancel.name = "profile-kick-cancel";
            _profileKickConfirm = Button(actions, "강퇴", SubmitPublicProfileKick, "danger grow");
            _profileKickConfirm.name = "profile-kick-confirm";
            popup.focusable = true;
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_profileKickOverlay != popup || !ReferenceEquals(evt.target, popup)) return;
                ClosePublicProfileKick(); evt.StopImmediatePropagation();
            });
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) { ClosePublicProfileKick(); evt.StopImmediatePropagation(); }
                else if (evt.keyCode == KeyCode.Tab)
                {
                    var buttons = modal.Query<Button>().ToList().Where(button => button.canGrabFocus && button.enabledInHierarchy).ToList();
                    if (buttons.Count == 0) return;
                    int index = buttons.IndexOf(root.focusController?.focusedElement as Button);
                    int next = index < 0 ? (evt.shiftKey ? buttons.Count - 1 : 0) : (index + (evt.shiftKey ? -1 : 1) + buttons.Count) % buttons.Count;
                    root.focusController?.IgnoreEvent(evt); buttons[next].Focus(); evt.StopImmediatePropagation();
                }
            }, TrickleDown.TrickleDown);
            popup.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, popup) && _profileKickOverlay == popup) ClosePublicProfileKick(false, true);
            });
            DrawUIMotion.ShowModal(popup, modal); cancel.Focus();
            RefreshPublicProfileActions();
        }

        private void SubmitPublicProfileKick()
        {
            if (_profileKickBusy || _profileActionBusy || lobby.IsBusy || _profileKickOverlay == null) return;
            var target = PublicProfileKickTarget();
            if (target == null || target.Id != _profileKickPlayerId || target.AccountId != _profileKickAccountId
                || lobby.RoomCode != _profileKickRoomCode || network.State.LocalPlayerId != _profileKickActorId
                || !network.CanKickPlayer(target.Id)) { ClosePublicProfileKick(false); return; }
            _profileKickBusy = true;
            RefreshPublicProfileActions();
            _ = PublicProfileKickAsync(target.Id, target.Name, _profileOverlay, _profileViewerAccountId, _profileKickRoomCode);
        }

        private async Task PublicProfileKickAsync(int playerId, string playerName, VisualElement profile, string viewer, string roomCode)
        {
            bool Current() => this != null && isActiveAndEnabled && inRoom && _profileOverlay == profile
                && lobby.Profile?.AccountId == viewer && lobby.RoomCode == roomCode;
            try
            {
                await network.KickPlayerAsync(playerId);
                if (Current()) { ClosePublicProfile(); Toast(DrawLocalization.Format("{0}님을 강퇴했습니다.", playerName)); }
            }
            catch (Exception exception)
            {
                if (Current()) { DrawAudio.Instance?.Play(DrawSound.UiError); Toast(exception.Message); }
            }
            finally { _profileKickBusy = false; RefreshPublicProfileActions(); }
        }

        private void ClosePublicProfileKick(bool restoreFocus = true, bool immediate = false)
        {
            var popup = _profileKickOverlay;
            var profile = _profileOverlay;
            var parentOverlay = overlay;
            int version = ++_profileKickVersion;
            _profileKickOverlay = null; _profileKickConfirm = null;
            _profileKickPlayerId = _profileKickActorId = -1;
            _profileKickAccountId = _profileKickRoomCode = null;
            _profileModal?.SetEnabled(true);
            if (popup == null) return;
            if (immediate) popup.RemoveFromHierarchy();
            else DrawUIMotion.HideModal(popup, popup.Q<VisualElement>(className: "modal"));
            if (!restoreFocus || root == null) return;
            root.schedule.Execute(() =>
            {
                if (this != null && isActiveAndEnabled && version == _profileKickVersion && _profileKickOverlay == null
                    && _profileOverlay == profile && overlay == parentOverlay && !ChatInputHasFocus()
                    && _profileKickButton?.panel != null && _profileKickButton.enabledInHierarchy)
                    _profileKickButton.Focus();
            }).StartingIn(180);
        }

        private void OpenSelfCustomization()
        {
            if (!lobby.IsAuthenticated || lobby.IsBusy || _profileAccountId != lobby.Profile?.AccountId) return;
            var returnFocus = _profileReturnFocus;
            ClosePublicProfile(false);
            if (!inRoom)
            {
                Navigate(LobbyScreen.Customize);
                return;
            }
            if (!lobby.IsOnlineRoom) return;
            SyncProfile();
            CloseModal();
            var popup = _roomCustomizeOverlay = Box(root, "overlay enter utility-overlay room-customize-overlay");
            popup.name = "room-customize-overlay";
            var modal = Box(popup, "modal utility-popup room-customize-popup");
            modal.name = "room-customize-popup";
            _roomCustomizeAccountId = lobby.Profile.AccountId;
            _roomCustomizeReturnFocus = returnFocus;
            var header = Box(modal, "row utility-popup-header");
            Text(header, "캐릭터 꾸미기", "utility-popup-title grow");
            IconButton(header, "닫기", DrawUIIcon.Kind.Close, () => CloseRoomCustomization(), "utility-popup-x");
            var preview = Box(modal, "room-customize-preview");
            preview.name = "room-customize-preview";
            ProfileForm(modal, preview, () =>
            {
                if (this != null && _roomCustomizeOverlay == popup) CloseRoomCustomization();
            }, true, () => CloseRoomCustomization());
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_roomCustomizeOverlay == popup && ReferenceEquals(evt.target, popup)) CloseRoomCustomization();
            });
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (overlay != null || _profileOverlay != null || _roomPasswordOverlay != null) return;
                if (evt.keyCode == KeyCode.Escape)
                {
                    CloseRoomCustomization();
                    evt.StopImmediatePropagation();
                    return;
                }
                if (evt.keyCode != KeyCode.Tab) return;
                var controls = modal.Query<VisualElement>().ToList().Where(control => control.canGrabFocus && control.enabledInHierarchy
                    && control.tabIndex >= 0 && control.resolvedStyle.visibility == Visibility.Visible && IsVisible(control)).ToList();
                if (controls.Count == 0) return;
                int index = controls.IndexOf(root.focusController?.focusedElement as VisualElement);
                int next = index < 0 ? (evt.shiftKey ? controls.Count - 1 : 0) : (index + (evt.shiftKey ? -1 : 1) + controls.Count) % controls.Count;
                root.focusController?.IgnoreEvent(evt);
                controls[next].Focus();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            DrawUIMotion.ShowModal(popup, modal);
            modal.Q<TextField>("customize-nickname")?.Focus();
            HideMobileScrollers();
        }

        private void CloseRoomCustomization(bool restoreFocus = true, bool immediate = false)
        {
            var popup = _roomCustomizeOverlay;
            var returnFocus = _roomCustomizeReturnFocus;
            string accountId = _roomCustomizeAccountId;
            _roomCustomizeOverlay = _roomCustomizeReturnFocus = null;
            _roomCustomizeAccountId = null;
            if (popup == null) return;
            popup.Q<TextField>("customize-nickname")?.Blur();
            if (immediate) popup.RemoveFromHierarchy();
            else DrawUIMotion.HideModal(popup, popup.Q<VisualElement>(className: "modal"));
            if (!restoreFocus || root == null) return;
            root.schedule.Execute(() =>
            {
                if (this == null || !isActiveAndEnabled || _roomCustomizeOverlay != null || overlay != null || _profileOverlay != null || ChatInputHasFocus()) return;
                var target = returnFocus?.panel != null ? returnFocus : root.Q<VisualElement>("profile-open-" + accountId);
                target?.Focus();
            }).StartingIn(180);
        }

        private void RestoreRoomCustomizationFocus()
        {
            var popup = _roomCustomizeOverlay;
            if (popup == null || root == null) return;
            root.schedule.Execute(() =>
            {
                if (this == null || !isActiveAndEnabled || _roomCustomizeOverlay != popup || popup.panel == null
                    || overlay != null || _profileOverlay != null || _roomPasswordOverlay != null) return;
                popup.Q<TextField>("customize-nickname")?.Focus();
            }).StartingIn(180);
        }

        private void RefreshPublicProfileFriendship()
        {
            var profile = _loadedPublicProfile;
            var actions = _profileBody?.Q<VisualElement>(className: "public-profile-friend-actions");
            if (profile == null || actions?.panel == null || _profileActionBusy) return;
            string state = profile.AccountId == lobby.Profile?.AccountId ? "Self"
                : lobby.Friends.Friends.Any(friend => friend.AccountId == profile.AccountId) ? "Friends"
                : lobby.Friends.Incoming.Any(friend => friend.AccountId == profile.AccountId) ? "Incoming"
                : lobby.Friends.Outgoing.Any(friend => friend.AccountId == profile.AccountId) ? "Outgoing" : "None";
            if (profile.Friendship == state) return;
            profile.Friendship = state;
            actions.Clear();
            RenderPublicProfileFriendship(actions, profile);
        }

        private void StartPublicProfileFriendAction(Func<Task> action)
        {
            if (_profileActionBusy || _profileKickBusy || _profileKickOverlay != null || _profileOverlay == null || lobby.IsBusy) return;
            _profileActionBusy = true;
            var actions = _profileBody.Q<VisualElement>(className: "public-profile-friend-actions");
            actions.Query<Button>().ForEach(button => button.SetEnabled(false));
            Text(actions, "처리 중…", "public-profile-state");
            _ = PublicProfileFriendActionAsync(action, _profileOverlay, _profileVersion, _profileCancellation.Token);
        }

        private async Task PublicProfileFriendActionAsync(Func<Task> action, VisualElement popup, int version, CancellationToken token)
        {
            try
            {
                await action();
                if (IsCurrentPublicProfile(popup, version, token)) BeginPublicProfileLoad();
            }
            catch (Exception exception)
            {
                if (!IsCurrentPublicProfile(popup, version, token)) return;
                DrawAudio.Instance?.Play(DrawSound.UiError);
                Toast(exception.Message);
                BeginPublicProfileLoad();
            }
        }

        private static CultureInfo PublicProfileCulture()
        {
            try { return CultureInfo.GetCultureInfo(DrawLocalization.CurrentLanguageCode); }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        private static string PublicProfileDate(string timestamp, bool includeTime = false)
        {
            return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToLocalTime().ToString(includeTime ? "g" : "d", PublicProfileCulture()) : "—";
        }

        private void CancelPublicProfileRequest()
        {
            var cancellation = _profileCancellation;
            _profileCancellation = null;
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private void ClosePublicProfile(bool restoreFocus = true)
        {
            ClosePublicProfileKick(false, true);
            CancelPublicProfileRequest();
            ++_profileVersion;
            var popup = _profileOverlay;
            var modal = _profileModal;
            var returnFocus = _profileReturnFocus;
            var accountId = _profileAccountId;
            _profileOverlay = _profileModal = _profileBody = _profileReturnFocus = null;
            _profileAccountId = _profileViewerAccountId = null;
            _profileActionBusy = false;
            _profileKickButton = null;
            _loadedPublicProfile = null;
            if (popup == null) return;
            DrawUIMotion.HideModal(popup, modal);
            if (!restoreFocus || root == null) return;
            root.schedule.Execute(() =>
            {
                if (_profileOverlay != null || ChatInputHasFocus()) return;
                var target = returnFocus?.panel != null ? returnFocus : root.Q<VisualElement>("profile-open-" + accountId);
                (target ?? overlay?.Q<Button>())?.Focus();
            }).StartingIn(180);
        }
    }
}
