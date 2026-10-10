using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private readonly Dictionary<AvatarPartSlot, string> _shopPreviewProductIds = new Dictionary<AvatarPartSlot, string>();
        private string _shopPreviewAccountId, _shopCartKey;
        private VisualElement _shopCartPanel, _shopCartList;
        private ScrollView _shopCartScroll;
        private Label _shopCartEmpty, _shopCartTotal, _shopCartStatus;
        private Button _shopCartBuy;
        private bool _shopBatchPurchasing;

        private sealed class ShopPreviewItem
        {
            public ShopProduct Product;
            public AvatarPartSlot[] Slots;
            public bool Owned, Available;
        }

        private void ClearShopPreview()
        {
            _shopPreviewParts.Clear();
            _shopPreviewProductIds.Clear();
        }

        private void RemoveShopPreviewPart(AvatarPartSlot slot)
        {
            _shopPreviewParts.Remove(slot);
            _shopPreviewProductIds.Remove(slot);
        }

        private void ClearShopCartView()
        {
            _shopCartPanel = _shopCartList = null;
            _shopCartScroll = null;
            _shopCartEmpty = _shopCartTotal = _shopCartStatus = null;
            _shopCartBuy = null;
            _shopCartKey = null;
        }

        private void BuildShopCart(VisualElement parent)
        {
            _shopCartPanel = Box(parent, "shop-cart");
            _shopCartPanel.name = "shop-cart";
            Text(_shopCartPanel, "입혀본 아이템", "shop-cart-heading");
            _shopCartEmpty = Text(_shopCartPanel, "아직 입혀본 아이템이 없어요.", "shop-cart-empty");
            _shopCartScroll = DrawSmoothScroll.Create(ScrollViewMode.Vertical);
            _shopCartScroll.name = "shop-cart-scroll";
            _shopCartScroll.AddToClassList("shop-cart-scroll");
            _shopCartPanel.Add(_shopCartScroll);
            _shopCartList = Box(_shopCartScroll, "shop-cart-list");
            _shopCartList.name = "shop-cart-list";
            var footer = Box(_shopCartPanel, "shop-cart-footer");
            _shopCartTotal = Text(footer, "새 아이템 {0}개 · 총 {1} 코인", "shop-cart-total", 0, 0);
            _shopCartTotal.name = "shop-cart-total";
            _shopCartStatus = Text(footer, "", "shop-cart-status");
            _shopCartStatus.name = "shop-cart-status";
            _shopCartBuy = Button(footer, "일괄 구매", () => Run(PurchaseShopPreviewAsync), "primary shop-cart-buy", DrawSound.UiConfirm);
            _shopCartBuy.name = "shop-cart-buy";
            _shopCartKey = null;
        }

        private ShopPreviewItem[] ShopPreviewItems()
        {
            var offered = (lobby.Shop.Products ?? Array.Empty<ShopProduct>()).Where(product => product != null).ToArray();
            var catalogue = AvatarParts.CreateShopProducts().Select(product =>
                offered.FirstOrDefault(remote => remote.Id == product.Id && remote.Accessory == product.Accessory) ?? product).ToArray();
            var remaining = new HashSet<AvatarPartSlot>(_shopPreviewParts.Keys);
            var items = new List<ShopPreviewItem>();
            void Add(ShopProduct product, AvatarPartSlot[] slots)
            {
                items.Add(new ShopPreviewItem { Product = product, Slots = slots,
                    Owned = AvatarParts.IsOwned(lobby.Profile?.OwnedAccessories, product.Accessory),
                    Available = offered.Any(remote => remote.Id == product.Id && remote.Accessory == product.Accessory) });
                foreach (var slot in slots) remaining.Remove(slot);
            }
            foreach (string id in _shopPreviewProductIds.Values.Distinct())
            {
                var product = catalogue.FirstOrDefault(candidate => candidate.Id == id);
                if (product == null) continue;
                var slots = AvatarParts.Slots.Where(slot => AvatarParts.Get(product.Accessory, slot) != 0).ToArray();
                if (slots.Length > 0 && slots.All(slot => remaining.Contains(slot)
                    && _shopPreviewParts[slot] == AvatarParts.Get(product.Accessory, slot)
                    && _shopPreviewProductIds.TryGetValue(slot, out var selected) && selected == id)) Add(product, slots);
            }
            foreach (var slot in AvatarParts.Slots.Where(remaining.Contains))
            {
                var product = catalogue.FirstOrDefault(candidate => candidate.Accessory == _shopPreviewParts[slot]);
                if (product != null) Add(product, new[] { slot });
            }
            return AvatarParts.Slots.SelectMany(slot => items.Where(item => item.Slots[0] == slot)).ToArray();
        }

        private void RefreshShopCart()
        {
            if (_shopCartList == null) return;
            var items = ShopPreviewItems();
            var pending = items.Where(item => !item.Owned).ToArray();
            long total = pending.Sum(item => (long)item.Product.Price);
            bool busy = lobby.IsBusy || _shopBatchPurchasing;
            _shopPreviewReset?.SetEnabled(_shopPreviewParts.Count != 0 && !busy);
            string key = L.CurrentLanguageCode + "/" + avatarColor + "/" + string.Join("|", items.Select(item =>
                $"{item.Product.Id}/{item.Product.Name}/{item.Product.Price}/{item.Owned}/{item.Available}"));
            if (_shopCartKey != key)
            {
                _shopCartKey = key;
                _shopCartList.Clear();
                foreach (var item in items)
                {
                    var row = Box(_shopCartList, "row shop-cart-item");
                    row.name = "shop-cart-item-" + item.Product.Id;
                    var icon = new AvatarElement(avatarColor, item.Product.Accessory);
                    icon.AddToClassList("shop-cart-icon");
                    row.Add(icon);
                    var info = Box(row, "grow shop-cart-info");
                    Text(info, item.Product.Name, "shop-cart-name");
                    if (item.Owned) Text(info, "보유 중", "shop-cart-price shop-cart-owned");
                    else Text(info, "{0} 코인", "shop-cart-price", item.Product.Price);
                    IconButton(row, "제거", DrawUIIcon.Kind.Close, () =>
                    {
                        if (lobby.IsBusy || _shopBatchPurchasing) return;
                        foreach (var slot in item.Slots)
                            if (_shopPreviewParts.TryGetValue(slot, out var part) && part == AvatarParts.Get(item.Product.Accessory, slot)) RemoveShopPreviewPart(slot);
                        UpdateShopPreview();
                    }, "shop-cart-remove").name = "shop-cart-remove-" + item.Product.Id;
                }
                var scroll = _shopCartScroll;
                DrawSmoothScroll.Bind(scroll);
                scroll.schedule.Execute(() =>
                {
                    if (scroll == _shopCartScroll && scroll.panel != null)
                        scroll.scrollOffset = new Vector2(0, Mathf.Clamp(scroll.scrollOffset.y, 0, Mathf.Max(0, scroll.verticalScroller.highValue)));
                }).StartingIn(20);
            }
            _shopCartPanel.style.display = IsMobile && items.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _shopCartEmpty.style.display = items.Length == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _shopCartScroll.style.display = items.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            SetText(_shopCartTotal, "새 아이템 {0}개 · 총 {1} 코인", pending.Length, total);
            bool available = pending.All(item => item.Available);
            bool affordable = (lobby.Profile?.Coins ?? 0) >= total;
            string status = !available ? "현재 구매할 수 없는 아이템이 있어요." : !affordable ? "코인이 부족합니다." : "";
            SetText(_shopCartStatus, status);
            _shopCartStatus.style.display = string.IsNullOrEmpty(status) ? DisplayStyle.None : DisplayStyle.Flex;
            SetText(_shopCartBuy, items.Length > 0 && pending.Length == 0 ? "장착하기" : "일괄 구매");
            _shopCartBuy.SetEnabled(!busy && items.Length > 0 && available && affordable
                && (pending.Length > 0 || CurrentShopPreview() != (lobby.Profile?.Accessory ?? 0)));
            _shopCartList.Query<Button>().ForEach(button => button.SetEnabled(!busy));
        }

        private async Task PurchaseShopProductAsync(ShopProduct product)
        {
            string accountId = lobby.Profile?.AccountId;
            var parts = AvatarParts.Slots.Where(slot => AvatarParts.Get(product.Accessory, slot) != 0)
                .Select(slot => new KeyValuePair<AvatarPartSlot, long>(slot, AvatarParts.Get(product.Accessory, slot))).ToArray();
            await lobby.PurchaseAsync(product.Id);
            ShowShopPurchaseEquip(accountId, parts, true);
        }

        private async Task PurchaseShopPreviewAsync()
        {
            var items = ShopPreviewItems();
            if (items.Length == 0) return;
            string accountId = lobby.Profile?.AccountId;
            var parts = _shopPreviewParts.ToArray();
            var pending = items.Where(item => !item.Owned).ToArray();
            if (pending.Length == 0) { await EquipShopPreviewAsync(accountId, parts); return; }
            if (pending.Any(item => !item.Available)) throw new InvalidOperationException(L.Text("현재 구매할 수 없는 아이템이 있어요."));
            if ((lobby.Profile?.Coins ?? 0) < pending.Sum(item => (long)item.Product.Price)) throw new InvalidOperationException(L.Text("코인이 부족합니다."));
            _shopBatchPurchasing = true;
            RefreshShopCart();
            try { await lobby.PurchaseBatchAsync(pending.Select(item => item.Product.Id).ToArray()); }
            finally { _shopBatchPurchasing = false; RefreshShopCart(); }
            ShowShopPurchaseEquip(accountId, parts, false);
        }

        private void ShowShopPurchaseEquip(string accountId, KeyValuePair<AvatarPartSlot, long>[] parts, bool individual)
        {
            if (!isActiveAndEnabled || !lobby.IsAuthenticated || lobby.Profile?.AccountId != accountId || inRoom || lobbyScreen != LobbyScreen.Shop) return;
            var modal = Modal("구매 완료");
            overlay.Q<VisualElement>(className: "modal")?.AddToClassList("shop-purchase-popup");
            Text(modal, "구매한 아이템을 지금 장착할까요?", "shop-purchase-question");
            long outfit = lobby.Profile.Accessory;
            foreach (var part in parts) outfit = AvatarParts.Equip(outfit, part.Value);
            var avatar = new AvatarElement(lobby.Profile.AvatarColor, outfit);
            avatar.AddToClassList("shop-purchase-avatar");
            modal.Add(avatar);
            var actions = Box(modal, "row shop-purchase-actions");
            var prompt = overlay;
            Button(actions, individual ? "아니오" : "나중에", () =>
            {
                if (overlay == prompt) CloseModal();
            }, "secondary grow", DrawSound.UiCancel).name = "shop-purchase-later";
            Button(actions, individual ? "예" : "장착하기", () => Run(async () =>
            {
                if (overlay != prompt) return;
                actions.SetEnabled(false);
                try
                {
                    await EquipShopPreviewAsync(accountId, parts, individual);
                    if (overlay == prompt) CloseModal();
                }
                finally { if (overlay == prompt) actions.SetEnabled(true); }
            }), "primary grow", DrawSound.UiConfirm).name = "shop-purchase-equip";
        }

        private async Task EquipShopPreviewAsync(string accountId, KeyValuePair<AvatarPartSlot, long>[] parts, bool clearEquippedSlots = false)
        {
            var profile = lobby.Profile;
            if (profile == null || profile.AccountId != accountId) return;
            long outfit = profile.Accessory;
            foreach (var part in parts)
            {
                if (!AvatarParts.IsOwned(profile.OwnedAccessories, part.Value)) throw new InvalidOperationException(L.Text("보유한 아이템만 장착할 수 있습니다."));
                outfit = AvatarParts.Equip(outfit, part.Value);
            }
            await lobby.SaveProfileAsync(profile.DisplayName, profile.AvatarColor, outfit);
            if (lobby.Profile?.AccountId != accountId) return;
            foreach (var part in parts)
                if (clearEquippedSlots || (_shopPreviewParts.TryGetValue(part.Key, out var selected) && selected == part.Value)) RemoveShopPreviewPart(part.Key);
            UpdateShopPreview();
        }
    }
}
