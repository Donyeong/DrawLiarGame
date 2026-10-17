using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _favoriteBrushPalette;
        private readonly List<Button> _favoriteBrushSwatches = new List<Button>();
        private Color32?[] _favoriteBrushColors = new Color32?[DrawBrushSettings.FAVORITE_COLOR_COUNT];
        private string _favoritePaletteAccount;

        private void CreateFavoriteBrushPalette(VisualElement parent)
        {
            _favoriteBrushSwatches.Clear();
            _favoritePaletteAccount = null;
            _favoriteBrushPalette = Box(parent, "brush-favorite-tools");
            _favoriteBrushPalette.name = "brush-favorite-tools";
            var grid = Box(_favoriteBrushPalette, "brush-quick-favorites");
            for (int index = 0; index < DrawBrushSettings.FAVORITE_COLOR_COUNT; index++)
            {
                int slot = index;
                var button = Button(grid, "", () =>
                {
                    if (!CanUseBrushPalette(_favoritePaletteAccount)) return;
                    if (_favoriteBrushColors[slot].HasValue) SelectBrushColor(_favoriteBrushColors[slot].Value);
                    else BrushColorEditor(slot);
                }, "swatch brush-favorite-swatch");
                button.name = "brush-favorite-" + slot;
                _favoriteBrushSwatches.Add(button);
            }
            _paletteButton = CreateColorPickerButton(_favoriteBrushPalette, BrushPalette);
            _paletteButton.name = "brush-palette-open";
            SetTooltip(_paletteButton, "더 많은 색상");
            RefreshFavoriteBrushControls();
        }

        private bool CanUseBrushPalette(string account) => surface != null && lobby?.IsPainterSubscriber == true
            && !string.IsNullOrEmpty(account) && account == lobby.Profile?.AccountId;

        private void RefreshFavoriteBrushControls(bool reload = false)
        {
            string account = lobby?.Profile?.AccountId;
            if (reload || _favoritePaletteAccount != account)
            {
                _favoritePaletteAccount = account;
                _favoriteBrushColors = DrawBrushSettings.LoadFavoriteColors(account);
            }
            for (int index = 0; index < _favoriteBrushSwatches.Count; index++)
            {
                var color = _favoriteBrushColors[index];
                SetFavoriteSwatch(_favoriteBrushSwatches[index], color, index);
                _favoriteBrushSwatches[index].EnableInClassList("selected",
                    color.HasValue && surface != null && !surface.Eraser && color.Value.Equals(surface.BrushColor));
            }
        }

        private static void SetFavoriteSwatch(Button button, Color32? color, int slot)
        {
            button.style.backgroundColor = color.HasValue ? (Color)color.Value : Color.clear;
            button.EnableInClassList("brush-favorite-empty", !color.HasValue);
            SetRawText(button, color.HasValue ? "" : "+");
            SetTooltip(button, color.HasValue ? "즐겨찾기 색상 {0}" : "빈 즐겨찾기 {0}", slot + 1);
        }

        private static Button CreateColorPickerButton(VisualElement parent, System.Action clicked)
        {
            var button = Button(parent, "", clicked, "secondary brush-picker-open");
            SetTooltip(button, "색상 선택");
            button.Add(new DrawUIIcon(DrawUIIcon.Kind.ColorPicker));
            return button;
        }

        private VisualElement CreatePalettePopup(string title, string account)
        {
            var body = Modal(title);
            body.name = "brush-palette-popup";
            body.userData = account;
            var modal = body;
            while (modal != null && !modal.ClassListContains("modal")) modal = modal.parent;
            modal?.AddToClassList("brush-palette-popup");
            return body;
        }

        private void BrushPalette()
        {
            string account = lobby?.Profile?.AccountId;
            if (!CanUseBrushPalette(account)) return;
            surface.CancelDrawing();
            RefreshFavoriteBrushControls(true);
            var modal = CreatePalettePopup("팔레트", account);
            var heading = Box(modal, "row brush-palette-heading");
            Text(heading, "기본 색상", "grow brush-palette-label");
            CreateColorPickerButton(heading, () => BrushColorEditor(FirstEmptyFavoriteSlot()));
            var grid = Box(modal, "brush-expanded-palette");
            grid.name = "brush-expanded-palette";
            for (int index = 0; index < DrawBrushSettings.Palette.Count; index++)
            {
                Color32 color = DrawBrushSettings.Palette[index];
                var swatch = Button(grid, "", () =>
                {
                    if (!CanUseBrushPalette(account)) return;
                    SelectBrushColor(color);
                    CloseModal();
                }, "swatch");
                swatch.name = "brush-expanded-color-" + index;
                swatch.style.backgroundColor = (Color)color;
                SetTooltip(swatch, color.Equals(DrawingSurface.PaperColor) ? "흰색" : "색상 {0}", index + 1);
                swatch.EnableInClassList("selected", !surface.Eraser && color.Equals(surface.BrushColor));
            }
            var favoriteHeading = Box(modal, "row brush-palette-heading");
            Text(favoriteHeading, "즐겨찾기", "grow brush-palette-label");
            bool editing = false;
            var favorites = Box(modal, "brush-popup-favorites");
            var edit = Button(favoriteHeading, "즐겨찾기 편집", () =>
            {
                if (!CanUseBrushPalette(account)) return;
                editing = !editing;
                favorites.EnableInClassList("brush-favorites-editing", editing);
                favoriteHeading.Q<Button>().EnableInClassList("tool-selected", editing);
            }, "secondary brush-favorites-edit");
            edit.name = "brush-favorites-edit";
            for (int index = 0; index < DrawBrushSettings.FAVORITE_COLOR_COUNT; index++)
            {
                int slot = index;
                Color32? color = _favoriteBrushColors[slot];
                var swatch = Button(favorites, "", () =>
                {
                    if (!CanUseBrushPalette(account)) return;
                    if (editing || !color.HasValue) BrushColorEditor(slot);
                    else { SelectBrushColor(color.Value); CloseModal(); }
                }, "swatch brush-favorite-swatch");
                swatch.name = "brush-popup-favorite-" + slot;
                SetFavoriteSwatch(swatch, color, slot);
                swatch.EnableInClassList("selected", color.HasValue && !surface.Eraser && color.Value.Equals(surface.BrushColor));
            }
            var actions = Box(modal, "row brush-palette-actions");
            Button(actions, "색 편집", () => BrushColorEditor(FirstEmptyFavoriteSlot()), "grow").name = "brush-color-edit";
            Button(actions, "닫기", CloseModal, "secondary grow");
        }

        private int FirstEmptyFavoriteSlot()
        {
            for (int index = 0; index < _favoriteBrushColors.Length; index++)
                if (!_favoriteBrushColors[index].HasValue) return index;
            for (int index = 0; index < _favoriteBrushColors.Length; index++)
                if (_favoriteBrushColors[index].Value.Equals(surface.BrushColor)) return index;
            return 0;
        }

        private void BrushColorEditor(int initialSlot)
        {
            string account = lobby?.Profile?.AccountId;
            if (!CanUseBrushPalette(account)) return;
            surface.CancelDrawing();
            RefreshFavoriteBrushControls(true);
            int selectedSlot = Mathf.Clamp(initialSlot, 0, DrawBrushSettings.FAVORITE_COLOR_COUNT - 1);
            var modal = CreatePalettePopup("색 편집", account);
            var picker = new DrawColorPicker { name = "brush-color-picker", Value = _favoriteBrushColors[selectedSlot] ?? surface.BrushColor };
            modal.Add(picker);
            Text(modal, "즐겨찾기", "brush-palette-label");
            var favorites = Box(modal, "brush-popup-favorites brush-editor-favorites");
            var slots = new List<Button>();
            Button remove = null;
            void RefreshSlots()
            {
                for (int index = 0; index < slots.Count; index++)
                {
                    SetFavoriteSwatch(slots[index], _favoriteBrushColors[index], index);
                    slots[index].EnableInClassList("selected", index == selectedSlot);
                }
                remove?.SetEnabled(_favoriteBrushColors[selectedSlot].HasValue);
            }
            for (int index = 0; index < DrawBrushSettings.FAVORITE_COLOR_COUNT; index++)
            {
                int slot = index;
                var swatch = Button(favorites, "", () =>
                {
                    if (!CanUseBrushPalette(account)) return;
                    selectedSlot = slot;
                    RefreshSlots();
                }, "swatch brush-favorite-swatch");
                swatch.name = "brush-save-slot-" + slot;
                slots.Add(swatch);
            }
            remove = Button(modal, "즐겨찾기 삭제", () =>
            {
                if (!CanUseBrushPalette(account)) return;
                DrawBrushSettings.SetFavoriteColor(account, selectedSlot, null);
                RefreshFavoriteBrushControls(true);
                RefreshSlots();
            }, "secondary brush-favorite-remove");
            remove.name = "brush-favorite-remove";
            var actions = Box(modal, "row brush-palette-actions");
            Button(actions, "즐겨찾기에 저장", () =>
            {
                if (!CanUseBrushPalette(account)) return;
                DrawBrushSettings.SetFavoriteColor(account, selectedSlot, picker.Value);
                RefreshFavoriteBrushControls(true);
                SelectBrushColor(picker.Value);
                CloseModal();
            }, "grow").name = "brush-favorite-save";
            Button(actions, "사용", () =>
            {
                if (!CanUseBrushPalette(account)) return;
                SelectBrushColor(picker.Value);
                CloseModal();
            }, "secondary grow").name = "brush-color-use";
            Button(actions, "취소", BrushPalette, "secondary grow");
            RefreshSlots();
        }
    }
}
