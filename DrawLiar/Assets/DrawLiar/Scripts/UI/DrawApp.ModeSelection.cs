using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private const string CLASSIC_MODE_TOOLTIP = "라이어는 제시어를 모릅니다. 그림을 보고 라이어를 찾으세요.\n최소 {0}명";
        private const string MISMATCH_MODE_TOOLTIP = "한 명에게만 다른 제시어가 주어집니다. 누구도 자신의 역할을 알 수 없습니다.\n최소 {0}명";
        private const string OPTIONAL_MODE_TOOLTIP = "라운드마다 라이어가 0명 또는 1명입니다. ‘라이어 없음’에도 투표할 수 있습니다.\n최소 {0}명";
        private const string RELAY_MODE_TOOLTIP = "같은 캔버스에 차례로 이어 그립니다.\n최소 {0}명";
        private const string INDIVIDUAL_MODE_TOOLTIP = "각자 캔버스에 차례로 그립니다. 토론 때 참가자의 그림을 비교하세요.\n최소 {0}명";
        private const string CLASSIC_MODE_DESCRIPTION = "라이어는 제시어를 모릅니다. 그림을 보고 라이어를 찾으세요.";
        private const string MISMATCH_MODE_DESCRIPTION = "한 명에게만 다른 제시어가 주어집니다. 누구도 자신의 역할을 알 수 없습니다.";
        private const string OPTIONAL_MODE_DESCRIPTION = "라운드마다 라이어가 0명 또는 1명입니다. ‘라이어 없음’에도 투표할 수 있습니다.";
        private const string RELAY_MODE_DESCRIPTION = "같은 캔버스에 차례로 이어 그립니다.";
        private const string INDIVIDUAL_MODE_DESCRIPTION = "각자 캔버스에 차례로 그립니다. 토론 때 참가자의 그림을 비교하세요.";

        private Label _roomLiarMode, _roomDrawingMode;
        private VisualElement _modeTooltipTarget;
        private Label _modeTooltip;

        private void CreateRoomSecretContext(VisualElement parent, string topicTitle)
        {
            var context = Box(parent, "room-secret-context");
            var modes = Box(context, "room-mode-badges");
            _roomLiarMode = Text(modes, "", "room-mode-badge");
            _roomLiarMode.name = "room-liar-mode";
            _roomDrawingMode = Text(modes, "", "room-mode-badge");
            _roomDrawingMode.name = "room-drawing-mode";
            BindRoomModeTooltip(_roomLiarMode);
            BindRoomModeTooltip(_roomDrawingMode);
            topic = Text(context, topicTitle, "topic");
            topic.name = "room-topic-title";
        }

        private void BindRoomModeTooltip(Label badge)
        {
            badge.pickingMode = PickingMode.Position;
            BindModeTooltip(badge);
            badge.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_modeTooltipTarget == badge) PositionModeTooltip();
            });
            badge.RegisterCallback<ClickEvent>(evt =>
            {
                if (!IsMobile || evt.button != 0) return;
                HideModeTooltip();
                _modeTooltipTarget = badge;
                ShowModeTooltip(badge);
                evt.StopPropagation();
            });
        }

        private void RefreshRoomModeLabels(RoomSnapshot state)
        {
            if (_roomLiarMode == null || _roomDrawingMode == null) return;
            var liarMode = state.Settings.LiarMode;
            RefreshRoomModeBadge(_roomLiarMode,
                liarMode == LiarMode.Mismatch ? "미스매치" : liarMode == LiarMode.Optional ? "불확정" : "일반",
                liarMode == LiarMode.Mismatch ? MISMATCH_MODE_DESCRIPTION : liarMode == LiarMode.Optional ? OPTIONAL_MODE_DESCRIPTION : CLASSIC_MODE_DESCRIPTION);
            bool individual = state.Settings.Mode == DrawingMode.Individual;
            RefreshRoomModeBadge(_roomDrawingMode, individual ? "한 명씩 그리기" : "릴레이 그리기",
                individual ? INDIVIDUAL_MODE_DESCRIPTION : RELAY_MODE_DESCRIPTION);
        }

        private void RefreshRoomModeBadge(Label badge, string title, string description)
        {
            string previous = badge.tooltip;
            SetText(badge, title);
            SetTooltip(badge, description);
            if (previous != badge.tooltip && _modeTooltipTarget == badge && _modeTooltip != null && IsVisible(_modeTooltip))
            {
                SetText(_modeTooltip, description);
                PositionModeTooltip();
            }
        }

        private void RoomModeChoices(VisualElement parent, RoomSettings settings, string prefix, Action changed = null)
        {
            var liarGroup = Box(parent, "create-mode");
            liarGroup.name = prefix + "-liar-mode";
            Text(liarGroup, "라이어 방식", "create-label");
            var liarRow = Box(liarGroup, "row create-modes liar-mode-choices");
            var liarButtons = new List<Button>();
            var drawingGroup = Box(parent, "create-mode");
            drawingGroup.name = prefix + "-drawing-mode";
            Text(drawingGroup, "그리기 방식", "create-label");
            var drawingRow = Box(drawingGroup, "row create-modes");
            var drawingButtons = new List<Button>();

            void RefreshChoices()
            {
                for (int index = 0; index < liarButtons.Count; index++)
                    liarButtons[index].EnableInClassList("create-mode-selected", index == (int)settings.LiarMode);
                for (int index = 0; index < drawingButtons.Count; index++)
                    drawingButtons[index].EnableInClassList("create-mode-selected", index == (int)settings.Mode);
                int minimum = GameRules.MinimumPlayers(settings.LiarMode);
                if (drawingButtons.Count == 2)
                {
                    SetTooltip(drawingButtons[0], RELAY_MODE_TOOLTIP, minimum);
                    SetTooltip(drawingButtons[1], INDIVIDUAL_MODE_TOOLTIP, minimum);
                }
                HideModeTooltip();
            }

            void AddLiarMode(LiarMode mode, string title, string suffix, string tooltip)
            {
                var button = Button(liarRow, title, () =>
                {
                    settings.LiarMode = mode;
                    RefreshChoices();
                    changed?.Invoke();
                }, "secondary grow create-mode-button");
                button.name = prefix + "-liar-" + suffix;
                liarButtons.Add(button);
                SetTooltip(button, tooltip, GameRules.MinimumPlayers(mode));
                BindModeTooltip(button);
            }

            void AddDrawingMode(DrawingMode mode, string title, string suffix)
            {
                var button = Button(drawingRow, title, () =>
                {
                    settings.Mode = mode;
                    RefreshChoices();
                    changed?.Invoke();
                }, "secondary grow create-mode-button");
                button.name = prefix + "-mode-" + suffix;
                drawingButtons.Add(button);
                BindModeTooltip(button);
            }

            AddLiarMode(LiarMode.Classic, "일반", "classic", CLASSIC_MODE_TOOLTIP);
            AddLiarMode(LiarMode.Mismatch, "미스매치", "mismatch", MISMATCH_MODE_TOOLTIP);
            AddLiarMode(LiarMode.Optional, "불확정", "optional", OPTIONAL_MODE_TOOLTIP);
            AddDrawingMode(DrawingMode.Relay, "릴레이 그리기", "relay");
            AddDrawingMode(DrawingMode.Individual, "한 명씩 그리기", "individual");
            liarButtons[liarButtons.Count - 1].AddToClassList("mode-choice-last");
            drawingButtons[drawingButtons.Count - 1].AddToClassList("mode-choice-last");
            RefreshChoices();
        }

        private void BindModeTooltip(VisualElement target)
        {
            target.RegisterCallback<PointerEnterEvent>(evt =>
            {
                if (!ReferenceEquals(evt.target, target) || evt.pointerType != UnityEngine.UIElements.PointerType.mouse) return;
                HideModeTooltip();
                _modeTooltipTarget = target;
                ShowModeTooltip(target);
            });
            target.RegisterCallback<PointerLeaveEvent>(evt =>
            {
                if (evt.pointerType == UnityEngine.UIElements.PointerType.mouse && _modeTooltipTarget == target) HideModeTooltip();
            });
            target.RegisterCallback<PointerDownEvent>(_ => HideModeTooltip());
            target.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (_modeTooltipTarget == target) HideModeTooltip();
            });
        }

        private void ShowModeTooltip(VisualElement target)
        {
            if (IsRoomModeTooltipBlocked(target))
            {
                HideModeTooltip();
                return;
            }
            if (!isActiveAndEnabled || _modeTooltipTarget != target || target.panel != root?.panel
                || !IsVisible(target) || !LOCALIZED_TOOLTIPS.TryGetValue(target, out var binding)) return;
            if (_modeTooltip == null)
            {
                _modeTooltip = new Label { name = "mode-tooltip", enableRichText = false, pickingMode = PickingMode.Ignore };
                _modeTooltip.AddToClassList("mode-tooltip");
                _modeTooltip.RegisterCallback<GeometryChangedEvent>(_ => PositionModeTooltip());
            }
            root.Add(_modeTooltip);
            SetText(_modeTooltip, binding.Source, binding.Arguments);
            _modeTooltip.style.width = Mathf.Min(360, Mathf.Max(0, root.contentRect.width - 24));
            _modeTooltip.style.display = DisplayStyle.Flex;
            PositionModeTooltip();
        }

        private bool IsRoomModeTooltipBlocked(VisualElement target) => target != null
            && (target == _roomLiarMode || target == _roomDrawingMode)
            && (overlay != null || _profileOverlay != null || _roomPasswordOverlay != null || _roomCustomizeOverlay != null);

        private void PositionModeTooltip()
        {
            if (_modeTooltipTarget?.panel == null || _modeTooltip?.panel == null
                || _modeTooltip.resolvedStyle.display == DisplayStyle.None) return;
            var target = _modeTooltipTarget.worldBound;
            var origin = root.WorldToLocal(target.position);
            float width = _modeTooltip.resolvedStyle.width;
            float height = _modeTooltip.resolvedStyle.height;
            if (!float.IsFinite(width) || !float.IsFinite(height)) return;
            float left = Mathf.Clamp(origin.x + (target.width - width) * .5f, 12, Mathf.Max(12, root.contentRect.width - width - 12));
            float top = origin.y + target.height + 8;
            if (top + height > root.contentRect.height - 12) top = origin.y - height - 8;
            _modeTooltip.style.left = left;
            _modeTooltip.style.top = Mathf.Clamp(top, 12, Mathf.Max(12, root.contentRect.height - height - 12));
        }

        private void HideModeTooltip()
        {
            _modeTooltipTarget = null;
            if (_modeTooltip != null) _modeTooltip.style.display = DisplayStyle.None;
        }

        private static bool IsNoLiarAccused(RoomSnapshot state) => state != null && state.HasAccused
            && state.Settings.LiarMode == LiarMode.Optional && state.AccusedPlayerId == GameRules.NO_LIAR_TARGET;

        private bool IsNoLiarSelected(RoomSnapshot state) => _hasSelectedPlayer && state != null
            && state.Settings.LiarMode == LiarMode.Optional && selectedPlayerId == GameRules.NO_LIAR_TARGET;

        private bool CanSelectNoLiar(RoomSnapshot state) => state != null && state.Settings.LiarMode == LiarMode.Optional
            && CanNominate(state);

        private bool CanNominate(RoomSnapshot state) => IsNominationPhase(state) && state.RemainingSeconds > 0 && !state.LocalIsSpectator && !voteSubmitted
            && Array.Exists(state.Players, player => player.Id == state.LocalPlayerId && player.IsConnected && !player.IsSpectator);

        private void SelectNoLiar()
        {
            var state = network.State;
            if (!CanSelectNoLiar(state)) return;
            selectedPlayerId = GameRules.NO_LIAR_TARGET;
            _hasSelectedPlayer = true;
            RefreshState(state);
        }
    }
}
