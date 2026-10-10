using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _mobileHud, _mobileRoundCard, _mobilePlayStage, _mobilePlaySide;

        private void BindMobileSecretDetails()
        {
            _mobileSecret.name = "mobile-secret-details";
            _mobileSecret.focusable = true;
            _mobileSecret.tabIndex = 0;
            SetTooltip(_mobileSecret, "역할 확인");
            bool IsToggleTarget(EventBase evt) => evt.target is VisualElement target && (target == secretToggle || target.GetFirstAncestorOfType<Button>() == secretToggle);
            void OpenDetails()
            {
                var state = network.State;
                if (!IsMobile || !inRoom || state == null || state.Phase == GamePhase.Lobby || _roomPasswordOverlay != null) return;
                RoleInformation(state, !secretHidden);
            }
            _mobileSecret.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button != 0 || IsToggleTarget(evt)) return;
                OpenDetails();
                evt.StopPropagation();
            });
            _mobileSecret.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (IsToggleTarget(evt) || evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Space) return;
                OpenDetails();
                evt.StopPropagation();
            });
            _mobileSecret.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                if (IsToggleTarget(evt)) return;
                OpenDetails();
                evt.StopPropagation();
            });
            secretToggle.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
        }

        private void ResetMobileRoomView()
        {
            _mobileRoomStack = _mobileWorkspace = _mobileControls = _mobileSecret = _mobileCanvas = _mobileRoster = _mobileActions = _mobileChatSheet = null;
            _mobileHud = _mobileRoundCard = _mobilePlayStage = _mobilePlaySide = null;
            _mobileRoundInfo = _mobileRoundHint = _mobileLiveRound = _mobileRosterTitle = null;
            _mobileRoomCode = null;
            _mobileRoomScroll = _mobileRosterScroll = null;
        }

        private void ArrangeMobileRoom(RoomSnapshot state)
        {
            if (!IsMobile || _mobileWorkspace == null) return;
            bool waiting = state == null || state.Phase == GamePhase.Lobby;
            bool portrait = _mobileLayout.IsPortrait;
            bool landscapeNomination = !waiting && !portrait && IsNominationPhase(state);
            _judgmentPanel.EnableInClassList("landscape-nomination-panel", landscapeNomination);
            _mobileRoomStack.EnableInClassList("mobile-waiting", waiting);
            _mobileRoomStack.EnableInClassList("mobile-playing", !waiting);
            _mobileRoomStack.EnableInClassList("mobile-compact", !portrait || _mobileLayout.AvailableSize.y < 830);
            _mobileRoundCard.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
            _mobileLiveRound.style.display = waiting ? DisplayStyle.None : DisplayStyle.Flex;
            _mobilePlayStage.style.display = DisplayStyle.Flex;
            _mobilePlaySide.style.display = portrait ? DisplayStyle.None : DisplayStyle.Flex;
            _mobileRoomScroll.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
            _mobileRosterScroll.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
            if (!waiting && !portrait)
            {
                if (_mobileSecret.parent != _mobileHud) _mobileHud.Insert(1, _mobileSecret);
            }
            else if (_mobileSecret.parent != _mobileRoomStack) _mobileRoomStack.Insert(2, _mobileSecret);
            if (waiting)
            {
                if (_mobileControls.parent != _mobileRoomScroll.contentContainer) _mobileRoomScroll.Add(_mobileControls);
                if (_mobileRoster.parent != _mobileControls) _mobileControls.Add(_mobileRoster);
                if (playerStrip.parent != _mobileRosterScroll.contentContainer) _mobileRosterScroll.Add(playerStrip);
                if (_judgmentPanel.parent != _mobileActions) _mobileActions.Insert(0, _judgmentPanel);
                var sideParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                if (_mobileRoomScroll.parent != sideParent) sideParent.Add(_mobileRoomScroll);
                if (_mobileActions.parent != sideParent) sideParent.Add(_mobileActions);
            }
            else
            {
                if (_mobileControls.parent != _mobilePlayStage) _mobilePlayStage.Add(_mobileControls);
                if (playerStrip.parent != _mobileRoster) _mobileRoster.Add(playerStrip);
                var rosterParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                if (_mobileRoster.parent != rosterParent) rosterParent.Insert(0, _mobileRoster);
                var actionParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                if (_mobileActions.parent != actionParent) actionParent.Add(_mobileActions);
                var judgmentParent = portrait ? _mobileActions : landscapeNomination
                    ? _mobileActions.Q<VisualElement>(className: "mobile-action-row") : _mobilePlayStage;
                if (_judgmentPanel.parent != judgmentParent)
                {
                    if (portrait || landscapeNomination) judgmentParent.Insert(0, _judgmentPanel);
                    else judgmentParent.Add(_judgmentPanel);
                }
                var current = _judgmentPanel.Q<Label>("nomination-current");
                if (current != null)
                {
                    var currentRow = current.parent.ClassListContains("level-name-row") ? current.parent : current;
                    var summary = _judgmentPanel.Q<VisualElement>(className: "nomination-summary");
                    var actions = _judgmentPanel.Q<VisualElement>(className: "nomination-actions");
                    var currentParent = landscapeNomination ? summary : actions;
                    if (currentRow.parent != currentParent) currentParent.Insert(0, currentRow);
                    currentRow.style.display = landscapeNomination && voteSubmitted ? DisplayStyle.None : DisplayStyle.Flex;
                }
                var guidance = _judgmentPanel.Q<Label>("nomination-guidance");
                if (guidance != null) guidance.style.display = landscapeNomination && current != null && !voteSubmitted ? DisplayStyle.None : DisplayStyle.Flex;
            }
            RefreshMobileRoomGeometry();
        }

        private void RefreshMobileRoomGeometry()
        {
            if (!IsMobile || _mobileWorkspace == null || playerStrip?.panel == null) return;
            if (_mobileRoomStack.ClassListContains("mobile-waiting"))
            {
                SizeMobileWaitingPlayers();
                _mobilePlayStage.style.height = _mobileLayout.IsPortrait
                    ? new StyleLength(Mathf.Min(_mobileWorkspace.contentRect.width / DrawingSurface.AspectRatio, _mobileLayout.AvailableSize.y * .36f))
                    : new StyleLength(StyleKeyword.Null);
                SizeMobileCanvas();
                return;
            }
            _mobilePlayStage.style.height = StyleKeyword.Null;
            float width = _mobileRoster.contentRect.width;
            if (width > 0)
            {
                const int COLUMNS = 4;
                const float GAP = 6;
                bool compact = _mobileRoomStack.ClassListContains("mobile-compact");
                float cardWidth = Mathf.Floor((width - GAP * (COLUMNS - 1)) / COLUMNS);
                float avatarSize = Mathf.Min(compact ? 64 : 80, Mathf.Max(0, cardWidth - 10));
                float cardHeight = compact ? 144 : 160;
                int index = 0;
                foreach (var card in playerStrip.Children())
                {
                    card.style.width = cardWidth;
                    card.style.height = cardHeight;
                    card.style.marginRight = ++index % COLUMNS == 0 ? 0 : GAP;
                    card.style.marginBottom = index <= playerStrip.childCount - (playerStrip.childCount % COLUMNS == 0 ? COLUMNS : playerStrip.childCount % COLUMNS) ? GAP : 0;
                    var avatar = card.Q<AvatarElement>();
                    if (avatar != null) { avatar.style.width = avatarSize; avatar.style.height = avatarSize; }
                }
            }
            SizeMobileCanvas();
        }
    }
}
