using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _pcRoomSecret, _pcRoomControls;

        private void RefreshPcRoomGeometry(VisualElement frame)
        {
            if (IsMobile || _pcCenter?.panel == null || frame == null || surface == null || frame.contentRect.height <= 0) return;
            var stage = _pcCenter.parent;
            var workspace = stage.parent;
            bool compact = stage.contentRect.height < 480;
            stage.EnableInClassList("pc-room-compact", compact);
            if (_pcLeftPlayers != null && _pcRightPlayers != null)
            {
                float availableWidth = Mathf.Max(0, workspace.contentRect.width - _pcLeftPlayers.resolvedStyle.width
                    - _pcRightPlayers.resolvedStyle.width - _pcCenter.resolvedStyle.marginLeft - _pcCenter.resolvedStyle.marginRight);
                SetPcRoomWidthLimit(_pcRoomSecret, availableWidth);
                SetPcRoomWidthLimit(_pcRoomControls, availableWidth);
            }
            float horizontalInset = surface.resolvedStyle.borderLeftWidth + surface.resolvedStyle.borderRightWidth
                + surface.resolvedStyle.paddingLeft + surface.resolvedStyle.paddingRight;
            float verticalInset = surface.resolvedStyle.borderTopWidth + surface.resolvedStyle.borderBottomWidth
                + surface.resolvedStyle.paddingTop + surface.resolvedStyle.paddingBottom;
            float width = Mathf.Max(0, (frame.contentRect.height - verticalInset) * DrawingSurface.AspectRatio + horizontalInset);
            SetPcRoomWidthLimit(_pcCenter, width);
            SizePcPlayerRail(_pcLeftPlayers, compact);
            SizePcPlayerRail(_pcRightPlayers, compact);
        }

        private static void SetPcRoomWidthLimit(VisualElement element, float width)
        {
            if (element == null || !float.IsFinite(width)) return;
            if (element.style.maxWidth.keyword != StyleKeyword.Undefined || float.IsNaN(element.style.maxWidth.value.value)
                || Mathf.Abs(element.style.maxWidth.value.value - width) > .5f) element.style.maxWidth = width;
        }

        private static void SizePcPlayerRail(VisualElement rail, bool compact)
        {
            if (rail == null || rail.contentRect.height <= 0) return;
            float height = Mathf.Min(112, Mathf.Max(0, (rail.contentRect.height - 16) / 2));
            foreach (var card in rail.Children())
            {
                if (compact)
                {
                    if (card.style.height.keyword != StyleKeyword.Undefined || float.IsNaN(card.style.height.value.value)
                        || Mathf.Abs(card.style.height.value.value - height) > .5f)
                        card.style.height = height;
                }
                else if (card.style.height.keyword != StyleKeyword.Auto) card.style.height = StyleKeyword.Auto;
            }
        }

        private static void SizePcPlayerAvatar(VisualElement card, AvatarElement avatar)
        {
            var info = avatar.parent.Q<VisualElement>(className: "grow");
            if (info == null || card.contentRect.height <= 0) return;
            float size = Mathf.Max(0, Mathf.Min(128, card.contentRect.width,
                card.contentRect.height - info.resolvedStyle.height - avatar.resolvedStyle.marginBottom));
            if (avatar.style.width.keyword != StyleKeyword.Undefined || float.IsNaN(avatar.style.width.value.value)
                || Mathf.Abs(avatar.style.width.value.value - size) > .5f)
            {
                avatar.style.width = size;
                avatar.style.height = size;
            }
        }

        private static void CreateEmptyPlayerSlot(VisualElement parent, int index)
        {
            var card = Box(parent, "player player-empty");
            card.name = "empty-player-slot-" + index;
            card.pickingMode = PickingMode.Ignore;
            card.focusable = false;
            card.tabIndex = -1;
            var icon = new DrawUIIcon(DrawUIIcon.Kind.Profile);
            icon.AddToClassList("player-empty-icon");
            card.Add(icon);
            Text(card, "대기 중", "player-empty-label").pickingMode = PickingMode.Ignore;
        }
    }
}
