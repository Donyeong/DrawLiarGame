using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private enum ShopSection { Cosmetics, Gems, Subscription }
        private ShopSection _shopSection;
        private VisualElement _commerceCatalogue;
        private bool _commerceEventsAttached;
        private bool OwnSubscriberBadge => lobby.IsPainterSubscriber && lobby.Profile?.ShowSubscriberBadge == true;

        private void AttachCommerceEvents()
        {
            if (_commerceEventsAttached || lobby == null) return;
            lobby.CommerceChanged += RefreshCommerceView;
            _commerceEventsAttached = true;
        }

        private void DetachCommerceEvents()
        {
            if (lobby != null && _commerceEventsAttached) lobby.CommerceChanged -= RefreshCommerceView;
            _commerceEventsAttached = false;
        }

        private void ShopCommerceNavigation(VisualElement page)
        {
            var wallet = Box(page, "row commerce-wallet");
            Text(wallet, "{0} 코인", "shop-coins", lobby.Profile?.Coins ?? 0);
            Text(wallet, "{0} 보석", "shop-gems", lobby.Profile?.PaidGems ?? 0);
            var restore = Button(wallet, "구매 복원", () => Run(() => lobby.RestoreCommerceAsync()), "secondary commerce-restore");
            restore.name = "commerce-restore";
            var tabs = Box(page, "row commerce-tabs");
            void Tab(ShopSection section, string title, string locator)
            {
                var button = Button(tabs, title, () =>
                {
                    if (_shopSection == section) return;
                    _shopSection = section;
                    Navigate(LobbyScreen.Shop);
                }, "secondary commerce-tab");
                button.name = "shop-section-" + locator;
                button.EnableInClassList("commerce-selected", _shopSection == section);
            }
            Tab(ShopSection.Cosmetics, "코스튬", "cosmetics");
            Tab(ShopSection.Gems, "보석", "gems");
            Tab(ShopSection.Subscription, "구독", "subscription");
            RefreshCommerceControls();
        }

        private void CommerceCataloguePage(VisualElement page)
        {
            var panel = Box(page, "shop-panel commerce-catalog grow");
            panel.name = "commerce-catalog";
            var header = Box(panel, "row commerce-heading");
            Text(header, _shopSection == ShopSection.Gems ? "보석 충전" : "화가 세트 구독", "section-title grow");
            IconButton(header, "새로고침", DrawUIIcon.Kind.Refresh, () => Run(() => lobby.RefreshCommerceAsync()), "commerce-refresh");
            var scroll = DrawSmoothScroll.Create();
            scroll.AddToClassList("shop-scroll"); panel.Add(scroll);
            _commerceCatalogue = Box(scroll, "commerce-content");
            _commerceCatalogue.name = "commerce-content";
            RefreshCommerceView();
        }

        private void RefreshCommerceView()
        {
            if (root == null) return;
            root.Query<Label>(className: "shop-gems").ForEach(label => SetText(label, "{0} 보석", lobby.Profile?.PaidGems ?? 0));
            root.Query<Label>(className: "shop-coins").ForEach(label => SetText(label, "{0} 코인", lobby.Profile?.Coins ?? 0));
            if (_commerceCatalogue?.panel != null && !inRoom && lobbyScreen == LobbyScreen.Shop && _shopSection != ShopSection.Cosmetics)
            {
                _commerceCatalogue.Clear();
                if (_shopSection == ShopSection.Subscription) SubscriberSettings(_commerceCatalogue, false);
                var grid = Box(_commerceCatalogue, "row commerce-grid");
                var products = lobby.Commerce?.Products ?? Array.Empty<CommerceProduct>();
                foreach (var product in products.Where(product => product != null &&
                    product.Kind == (_shopSection == ShopSection.Gems ? CommerceRules.PAID_GEMS : CommerceRules.PAINTER_SUBSCRIPTION)))
                    CommerceProductCard(grid, product);
                if (grid.childCount == 0) Text(grid, "판매 중인 상품이 없습니다.", "muted");
                grid.RegisterCallback<GeometryChangedEvent>(_ => SizeCommerceCards(grid));
                SizeCommerceCards(grid);
            }
            RefreshSubscriptionControls();
            RefreshCommerceControls();
        }

        private void CommerceProductCard(VisualElement parent, CommerceProduct product)
        {
            var card = Box(parent, "commerce-product"); card.name = "commerce-product-" + product.Id;
            if (product.Kind == CommerceRules.PAID_GEMS)
            {
                var gem = new DrawPremiumGem(); gem.AddToClassList("commerce-product-symbol"); card.Add(gem);
                Text(card, "보석 {0}개", "commerce-product-name", product.PaidGems);
                Text(card, "보석 잔액으로 구독 상품을 구매할 수 있어요.", "commerce-description");
            }
            else
            {
                var badge = new DrawSubscriberBadge(); badge.AddToClassList("commerce-product-symbol"); card.Add(badge);
                Text(card, product.Name, "commerce-product-name");
                Text(card, "구독 기간 {0}일", "commerce-description", product.SubscriptionDays);
                Text(card, "프로필에 구독자 뱃지를 표시할 수 있어요.", "commerce-description");
                Text(card, "구독 중 구매하면 남은 기간에 {0}일이 추가됩니다.", "commerce-description", product.SubscriptionDays);
            }
            var price = RawText(card, lobby.CommercePrice(product), "commerce-price");
            price.name = "commerce-price-" + product.Id;
            var buy = Button(card, product.Kind == CommerceRules.PAINTER_SUBSCRIPTION && lobby.IsPainterSubscriber ? "기간 연장" : "구매",
                () => ConfirmCommercePurchase(product), "primary commerce-buy", DrawSound.UiConfirm);
            buy.name = "commerce-buy-" + product.Id; buy.userData = product;
            buy.SetEnabled(lobby.CanPurchaseCommerce(product));
            if (product.Kind == CommerceRules.PAINTER_SUBSCRIPTION && (lobby.Profile?.PaidGems ?? 0) < product.PricePaidGems)
                Text(card, "보석이 부족합니다.", "commerce-description commerce-insufficient");
        }

        private void SizeCommerceCards(VisualElement grid)
        {
            float width = grid.contentRect.width;
            if (width <= 0) return;
            int columns = _shopSection == ShopSection.Subscription ? 1 : IsMobile ? (width >= 560 ? 2 : 1) : 3;
            float gap = 16;
            float cardWidth = Mathf.Max(0, Mathf.Floor((width - gap * (columns - 1)) / columns) - 1);
            int index = 0;
            foreach (var card in grid.Children().Where(element => element.ClassListContains("commerce-product")))
            {
                card.style.width = cardWidth; card.style.marginRight = index++ % columns == columns - 1 ? 0 : gap;
            }
        }

        private void ConfirmCommercePurchase(CommerceProduct product)
        {
            if (!lobby.CanPurchaseCommerce(product) || runningAction) return;
            string account = lobby.Profile.AccountId;
            var modal = Modal(product.Kind == CommerceRules.PAINTER_SUBSCRIPTION ? "화가 세트 구독" : "보석 충전");
            modal.name = "commerce-confirm";
            if (product.Kind == CommerceRules.PAINTER_SUBSCRIPTION)
                Text(modal, "{0} 보석으로 {1}일 구독을 구매할까요?", "commerce-confirm-description", product.PricePaidGems, product.SubscriptionDays);
            else Text(modal, "보석 {0}개를 구매할까요?", "commerce-confirm-description", product.PaidGems);
            RawText(modal, lobby.CommercePrice(product), "commerce-price");
            var actions = Box(modal, "row commerce-confirm-actions");
            Button(actions, "취소", CloseModal, "secondary grow", DrawSound.UiCancel).name = "commerce-cancel";
            var confirm = Button(actions, "구매", () => Run(async () =>
            {
                if (account != lobby.Profile?.AccountId || modal.panel == null) return;
                modal.SetEnabled(false);
                try
                {
                    await lobby.PurchaseCommerceAsync(product.Id, product.PricePaidGems);
                    if (account == lobby.Profile?.AccountId && overlay?.Q<VisualElement>("commerce-confirm") == modal) CloseModal();
                }
                finally { if (modal.panel != null) modal.SetEnabled(true); }
            }), "primary grow", DrawSound.UiConfirm);
            confirm.name = "commerce-confirm-buy";
        }

        private void SubscriberSettings(VisualElement parent, bool showShop = true)
        {
            if (lobby.Profile == null) return;
            var card = Box(parent, "subscriber-settings"); card.name = "subscriber-settings"; card.userData = lobby.Profile.AccountId;
            var heading = Box(card, "row subscriber-heading");
            Text(heading, "화가 세트", "grow subscriber-title");
            Text(heading, "", "subscriber-status");
            Text(card, "", "subscriber-expiry");
            Text(card, "", "subscriber-remaining");
            var toggle = new Toggle(L.Text("프로필에 구독자 뱃지 표시")); toggle.name = "subscriber-badge-toggle";
            SetText(toggle.labelElement, "프로필에 구독자 뱃지 표시"); toggle.AddToClassList("field"); card.Add(toggle);
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, toggle)) return;
                if (runningAction || lobby.IsBusy || !lobby.IsPainterSubscriber || card.userData as string != lobby.Profile?.AccountId)
                { toggle.SetValueWithoutNotify(OwnSubscriberBadge); return; }
                Run(async () =>
                {
                    toggle.SetEnabled(false);
                    try { await lobby.SaveSubscriberBadgeAsync(evt.newValue); }
                    finally { RefreshSubscriptionControls(); }
                });
            });
            if (showShop && !inRoom)
                Button(card, "구독 상품 보기", () =>
                {
                    _shopSection = ShopSection.Subscription; Navigate(LobbyScreen.Shop); Run(lobby.RefreshShopAsync);
                }, "secondary").name = "subscription-shop";
            RefreshSubscriberSettings(card);
        }

        private void RefreshSubscriberSettings(VisualElement card)
        {
            if (lobby.Profile == null || card.userData as string != lobby.Profile.AccountId) return;
            bool active = lobby.IsPainterSubscriber;
            SetText(card.Q<Label>(className: "subscriber-status"), active ? "구독 중" : "구독 없음");
            var expiryLabel = card.Q<Label>(className: "subscriber-expiry");
            var remaining = card.Q<Label>(className: "subscriber-remaining");
            expiryLabel.style.display = remaining.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            if (active && DateTimeOffset.TryParse(lobby.Profile.SubscriptionExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry))
            {
                SetText(expiryLabel, "만료: {0}", expiry.ToLocalTime().ToString("g", PublicProfileCulture()));
                SetText(remaining, "남은 기간: {0}일", Mathf.Max(1, (int)Math.Ceiling((expiry - lobby.CommerceUtcNow).TotalDays)));
            }
            var toggle = card.Q<Toggle>("subscriber-badge-toggle");
            toggle.SetValueWithoutNotify(OwnSubscriberBadge); toggle.SetEnabled(active && !lobby.IsBusy && !runningAction);
        }

        private void RefreshSubscriptionControls()
        {
            if (root == null) return;
            root.Query<VisualElement>(className: "subscriber-settings").ForEach(RefreshSubscriberSettings);
            string account = lobby.Profile?.AccountId;
            if (string.IsNullOrEmpty(account)) return;
            if (OwnSubscriberBadge)
                root.Query<DrawLevelBadge>().ForEach(level =>
                {
                    if (level.userData as string != account || level.parent.Q<DrawSubscriberBadge>() != null) return;
                    var badge = new DrawSubscriberBadge { name = "subscriber-badge-" + level.name, userData = account };
                    level.parent.Add(badge); SetTooltip(badge, "화가 세트 구독자");
                });
            root.Query<DrawSubscriberBadge>().ForEach(badge =>
            {
                if (badge.userData as string == account) badge.style.display = OwnSubscriberBadge ? DisplayStyle.Flex : DisplayStyle.None;
            });
        }

        private void RefreshCommerceControls()
        {
            if (root == null) return;
            root.Query<Button>(className: "commerce-buy").ForEach(button =>
            {
                var product = button.userData as CommerceProduct;
                if (product?.Kind == CommerceRules.PAINTER_SUBSCRIPTION) SetText(button, lobby.IsPainterSubscriber ? "기간 연장" : "구매");
                button.SetEnabled(!runningAction && lobby.CanPurchaseCommerce(product));
            });
            root.Query<Button>(className: "commerce-restore").ForEach(button => button.SetEnabled(!runningAction && !lobby.IsBusy && lobby.IsPlatformPaymentAvailable));
            RefreshSubscriptionControls();
        }
    }
}
