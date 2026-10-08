using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _pcRoomShell, _pcRoomSecret;

        private void RefreshPcRoomGeometry(VisualElement frame)
        {
            if (IsMobile || _pcCenter?.panel == null || _pcRoomShell?.parent == null || _pcLeftPlayers == null || _pcRightPlayers == null
                || frame == null || surface == null || frame.contentRect.height <= 0) return;
            if (Screen.width > 0 && Screen.height > 0)
            {
                int referenceHeight = Mathf.Max(900, Mathf.CeilToInt(Screen.height * 1280f / Screen.width));
                if (panelSettings.referenceResolution.y != referenceHeight)
                {
                    panelSettings.referenceResolution = new Vector2Int(1600, referenceHeight);
                    return;
                }
            }
            var stage = _pcCenter.parent;
            float horizontalInset = surface.resolvedStyle.borderLeftWidth + surface.resolvedStyle.borderRightWidth
                + surface.resolvedStyle.paddingLeft + surface.resolvedStyle.paddingRight;
            float verticalInset = surface.resolvedStyle.borderTopWidth + surface.resolvedStyle.borderBottomWidth
                + surface.resolvedStyle.paddingTop + surface.resolvedStyle.paddingBottom;
            float sides = _pcLeftPlayers.resolvedStyle.width + _pcRightPlayers.resolvedStyle.width
                + _pcCenter.resolvedStyle.marginLeft + _pcCenter.resolvedStyle.marginRight;
            float stageHeight = Mathf.Max(0, (_pcRoomShell.parent.contentRect.width - sides - horizontalInset)
                / DrawingSurface.AspectRatio + verticalInset);
            if (stage.style.maxHeight.keyword != StyleKeyword.Undefined || float.IsNaN(stage.style.maxHeight.value.value)
                || Mathf.Abs(stage.style.maxHeight.value.value - stageHeight) > .5f) stage.style.maxHeight = stageHeight;
            float width = Mathf.Max(0, (frame.contentRect.height - verticalInset) * DrawingSurface.AspectRatio + horizontalInset);
            SetPcRoomWidthLimit(_pcCenter, width);
            SetPcRoomWidthLimit(_pcRoomShell, width + sides);
            if (_pcRoomChat != null)
            {
                float height = Mathf.Max(112, stage.parent.contentRect.height * .65f);
                if (_pcRoomChat.style.maxHeight.keyword != StyleKeyword.Undefined || float.IsNaN(_pcRoomChat.style.maxHeight.value.value)
                    || Mathf.Abs(_pcRoomChat.style.maxHeight.value.value - height) > .5f) _pcRoomChat.style.maxHeight = height;
            }
        }

        private static void SetPcRoomWidthLimit(VisualElement element, float width)
        {
            if (element == null || !float.IsFinite(width)) return;
            if (element.style.maxWidth.keyword != StyleKeyword.Undefined || float.IsNaN(element.style.maxWidth.value.value)
                || Mathf.Abs(element.style.maxWidth.value.value - width) > .5f) element.style.maxWidth = width;
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
