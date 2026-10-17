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
        }

        private void ArrangeMobileRoom(RoomSnapshot state)
        {
            if (!IsMobile || _mobileWorkspace == null) return;
            void Place(VisualElement element, VisualElement parent, int index)
            {
                if (element.parent == parent && parent.IndexOf(element) == index) return;
                element.RemoveFromHierarchy();
                parent.Insert(Mathf.Min(index, parent.childCount), element);
            }
            bool waiting = state == null || state.Phase == GamePhase.Lobby;
            bool portrait = _mobileLayout.IsPortrait;
            bool landscapeNomination = !waiting && !portrait && IsNominationPhase(state);
            _judgmentPanel.EnableInClassList("landscape-nomination-panel", landscapeNomination);
            _mobileRoomStack.EnableInClassList("mobile-waiting", waiting);
            _mobileRoomStack.EnableInClassList("mobile-playing", !waiting);
            _mobileRoomStack.EnableInClassList("mobile-compact", !portrait || _mobileLayout.AvailableSize.y < 830);
            var roundParent = waiting && !portrait ? _mobileHud : _mobileRoomStack;
            if (_mobileRoundCard.parent != roundParent)
            {
                if (roundParent == _mobileHud) roundParent.Insert(0, _mobileRoundCard);
                else roundParent.Insert(1, _mobileRoundCard);
            }
            _mobileRoundCard.style.display = waiting ? DisplayStyle.Flex : DisplayStyle.None;
            _mobileLiveRound.style.display = waiting ? DisplayStyle.None : DisplayStyle.Flex;
            _mobilePlayStage.style.display = DisplayStyle.Flex;
            _mobilePlaySide.style.display = portrait ? DisplayStyle.None : DisplayStyle.Flex;
            if (!waiting && !portrait)
            {
                if (_mobileSecret.parent != _mobileHud) _mobileHud.Insert(1, _mobileSecret);
            }
            else if (_mobileSecret.parent != _mobileRoomStack) _mobileRoomStack.Insert(2, _mobileSecret);
            if (waiting)
            {
                if (_mobileControls.parent != _mobilePlayStage) _mobilePlayStage.Add(_mobileControls);
                if (playerStrip.parent != _mobileRoster) _mobileRoster.Add(playerStrip);
                if (_judgmentPanel.parent != _mobileActions) _mobileActions.Insert(0, _judgmentPanel);
                var sideParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                Place(_mobileRoster, sideParent, portrait ? 2 : 0);
                Place(_mobileActions, sideParent, portrait ? 3 : 1);
            }
            else
            {
                if (_mobileControls.parent != _mobilePlayStage) _mobilePlayStage.Add(_mobileControls);
                if (playerStrip.parent != _mobileRoster) _mobileRoster.Add(playerStrip);
                var rosterParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                Place(_mobileRoster, rosterParent, portrait ? 2 : 0);
                var actionParent = portrait ? _mobileWorkspace : _mobilePlaySide;
                Place(_mobileActions, actionParent, portrait ? 3 : 1);
                var judgmentParent = portrait ? _mobileActions : _mobilePlayStage;
                if (_judgmentPanel.parent != judgmentParent)
                {
                    if (portrait) judgmentParent.Insert(0, _judgmentPanel);
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
                _mobilePlayStage.style.height = StyleKeyword.Null;
                SizeMobileCanvas();
                return;
            }
            _mobilePlayStage.style.height = StyleKeyword.Null;
            float width = playerStrip.contentRect.width;
            if (float.IsFinite(width) && width > 0 && float.IsFinite(_mobileWorkspace.contentRect.height) && _mobileWorkspace.contentRect.height > 0)
            {
                const float GAP = 6;
                int columns = Mathf.Clamp(Mathf.FloorToInt((width + GAP) / 78), 2, 8);
                if (width < 620) columns = Mathf.Min(columns, 4);
                int rows = Mathf.Max(1, Mathf.CeilToInt(playerStrip.childCount / (float)columns));
                bool portrait = _mobileLayout.IsPortrait;
                float actionsHeight = float.IsFinite(_mobileActions.layout.height) ? Mathf.Max(32, _mobileActions.layout.height) : 32;
                float controlsHeight = float.IsFinite(_mobileControls.layout.height) ? Mathf.Max(0, _mobileControls.layout.height) : 0;
                float available = _mobileWorkspace.contentRect.height - actionsHeight - 12 - GAP * (rows - 1)
                    - (portrait ? controlsHeight + width / DrawingSurface.AspectRatio + 6 : 0);
                float cardHeight = Mathf.Clamp(Mathf.Floor(available / rows), 80, portrait ? 128 : 92);
                float cardWidth = Mathf.Max(0, Mathf.Floor((width - GAP * (columns - 1)) / columns) - 1);
                float avatarSize = Mathf.Max(28, Mathf.Min(portrait ? 72 : 44, Mathf.Min(cardWidth - 10, cardHeight - 50)));
                playerStrip.style.height = rows * cardHeight + GAP * (rows - 1);
                int index = 0;
                foreach (var card in playerStrip.Children())
                {
                    card.style.width = cardWidth;
                    card.style.height = cardHeight;
                    card.style.marginRight = ++index % columns == 0 ? 0 : GAP;
                    card.style.marginBottom = index <= playerStrip.childCount - (playerStrip.childCount % columns == 0 ? columns : playerStrip.childCount % columns) ? GAP : 0;
                    var avatar = card.Q<AvatarElement>();
                    if (avatar != null) { avatar.style.width = avatarSize; avatar.style.height = avatarSize; }
                }
            }
            SizeMobileCanvas();
        }
    }
}
