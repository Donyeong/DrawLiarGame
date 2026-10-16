using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private string _cosmeticCategory = CosmeticRules.NICKNAME_EFFECT;
        private string _cosmeticPreviewId = "", _cosmeticPreviewAccount = "";
        private VisualElement _cosmeticPreview, _cosmeticGrid;

        private void CosmeticCataloguePage(VisualElement page)
        {
            string account = lobby.Profile?.AccountId ?? "";
            if (_cosmeticPreviewAccount != account)
            {
                _cosmeticPreviewAccount = account;
                _cosmeticPreviewId = "";
                _cosmeticCategory = CosmeticRules.NICKNAME_EFFECT;
            }
            _commerceCatalogue = Box(page, "shop-panel commerce-catalog cosmetic-catalog grow");
            _commerceCatalogue.name = "cosmetic-catalog";
            var scroll = DrawSmoothScroll.Create();
            scroll.name = "cosmetic-scroll";
            scroll.AddToClassList("shop-scroll");
            VisualElement catalogueBody = _commerceCatalogue;
            if (IsMobile)
            {
                scroll.AddToClassList("cosmetic-mobile-scroll");
                _commerceCatalogue.Add(scroll);
                catalogueBody = Box(scroll, "cosmetic-scroll-body");
            }
            VisualElement categoryParent = catalogueBody;
            if (IsMobile)
            {
                var filters = Box(_commerceCatalogue, "row cosmetic-filters");
                _commerceCatalogue.Insert(0, filters);
                var categoryScroll = DrawSmoothScroll.Create(ScrollViewMode.Horizontal);
                categoryScroll.AddToClassList("cosmetic-category-scroll");
                filters.Add(categoryScroll);
                categoryParent = categoryScroll;
                IconButton(filters, "새로고침", DrawUIIcon.Kind.Refresh, () => Run(() => lobby.RefreshCommerceAsync()), "commerce-refresh");
            }
            else
            {
                var heading = Box(catalogueBody, "row commerce-heading");
                Text(heading, "보석 장식", "section-title grow");
                IconButton(heading, "새로고침", DrawUIIcon.Kind.Refresh, () => Run(() => lobby.RefreshCommerceAsync()), "commerce-refresh");
                Text(catalogueBody, "한 번 구매하면 계속 사용할 수 있어요.", "commerce-description cosmetic-permanent");
            }
            var categories = Box(categoryParent, "row cosmetic-categories");
            foreach (string category in CosmeticRules.Categories)
            {
                var tab = Button(categories, CosmeticCategoryName(category), () => SelectCosmeticCategory(category), "secondary cosmetic-category");
                tab.name = "cosmetic-category-" + category;
                tab.userData = category;
            }
            if (categoryParent is ScrollView categoryView) KeepShopFilterSelectionVisible(categoryView, "commerce-selected");
            if (IsMobile)
            {
                var workspace = Box(catalogueBody, "row cosmetic-workspace cosmetic-scrolling-workspace");
                _cosmeticPreview = Box(workspace, "cosmetic-preview-panel");
                _cosmeticPreview.style.display = DisplayStyle.None;
                _cosmeticGrid = Box(workspace, "row commerce-grid cosmetic-grid");
            }
            else
            {
                var workspace = Box(_commerceCatalogue, "row cosmetic-workspace grow");
                var previewScroll = DrawSmoothScroll.Create();
                previewScroll.name = "cosmetic-preview-scroll";
                previewScroll.AddToClassList("shop-scroll");
                previewScroll.AddToClassList("cosmetic-preview-scroll");
                workspace.Add(previewScroll);
                _cosmeticPreview = Box(previewScroll, "cosmetic-preview-panel");
                workspace.Add(scroll);
                _cosmeticGrid = Box(scroll, "row commerce-grid cosmetic-grid");
            }
            _cosmeticPreview.name = "cosmetic-preview";
            _cosmeticGrid.name = "cosmetic-products";
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => SizeCosmeticCards());
            _cosmeticGrid.RegisterCallback<GeometryChangedEvent>(_ => SizeCosmeticCards());
            RefreshCosmeticCatalogueView();
        }

        private static string CosmeticCategoryName(string category)
        {
            switch (category)
            {
                case CosmeticRules.NICKNAME_EFFECT: return "닉네임 효과";
                case CosmeticRules.AVATAR_BACKGROUND: return "캐릭터 배경";
                case CosmeticRules.AVATAR_FRAME: return "프로필 테두리";
                case CosmeticRules.CHAT_BUBBLE: return "채팅 말풍선";
                default: return "장식";
            }
        }

        private void SelectCosmeticCategory(string category)
        {
            if (!CosmeticRules.IsCategory(category) || _cosmeticCategory == category) return;
            _cosmeticCategory = category;
            _cosmeticPreviewId = "";
            var scroll = _cosmeticGrid?.GetFirstAncestorOfType<ScrollView>();
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            RefreshCosmeticCatalogueView();
        }

        private void RefreshCosmeticCatalogueView()
        {
            if (_cosmeticGrid?.panel == null || _cosmeticPreview?.panel == null) return;
            if (_cosmeticPreviewAccount != lobby.Profile?.AccountId) _cosmeticPreviewId = "";
            _cosmeticGrid.Clear();
            var products = (lobby.Commerce?.Products ?? Array.Empty<CommerceProduct>())
                .Where(product => product?.Kind == CommerceRules.COSMETIC && product.Category == _cosmeticCategory)
                .OrderBy(product => product.PricePaidGems);
            foreach (var product in products) CosmeticProductCard(_cosmeticGrid, product);
            if (_cosmeticGrid.childCount == 0) Text(_cosmeticGrid, "판매 중인 상품이 없습니다.", "muted");
            RefreshCosmeticPreview();
            RefreshCosmeticControls();
            SizeCosmeticCards();
        }

        private void CosmeticProductCard(VisualElement parent, CommerceProduct product)
        {
            var card = Box(parent, "commerce-product cosmetic-product");
            card.name = "cosmetic-product-" + product.Id;
            card.userData = product;
            VisualElement visualParent = card;
            if (IsMobile)
            {
                var preview = Button(card, "미리보기", () => PreviewCosmeticProduct(product), "secondary cosmetic-try-on cosmetic-item-preview");
                preview.name = "cosmetic-preview-" + product.Id;
                SetTooltip(preview, "미리보기");
                visualParent = preview;
            }
            var visual = Box(visualParent, "cosmetic-product-preview");
            if (IsMobile) visual.pickingMode = PickingMode.Ignore;
            CosmeticProductPreview(visual, product);
            var productName = Text(card, product.Name, "commerce-product-name");
            SetTooltip(productName, product.Name);
            Text(card, product.Description, "commerce-description");
            Text(card, lobby.OwnsCosmetic(product.Id) ? "보유 중" : IsMobile ? "{0} 보석" : "영구 소장 · {0} 보석", "commerce-price", product.PricePaidGems);
            var actions = Box(card, "cosmetic-product-actions");
            if (!IsMobile)
            {
                var preview = Button(actions, "미리보기", () => PreviewCosmeticProduct(product), "secondary cosmetic-try-on");
                preview.name = "cosmetic-preview-" + product.Id;
            }
            var action = Button(actions, "구매", () => UseCosmeticProduct(product), "primary cosmetic-product-action", DrawSound.UiConfirm);
            action.name = "cosmetic-buy-" + product.Id;
            action.userData = product;
            if (!lobby.OwnsCosmetic(product.Id) && (lobby.Profile?.PaidGems ?? 0) < product.PricePaidGems)
                Text(card, "보석이 부족합니다.", "commerce-description commerce-insufficient");
        }

        private CosmeticLoadout CosmeticPreviewLoadout(CommerceProduct product = null)
        {
            product ??= CosmeticRules.Find(_cosmeticPreviewId);
            return product == null ? CosmeticRules.Sanitize(lobby.Profile?.Cosmetics)
                : CosmeticRules.SetEquipped(lobby.Profile?.Cosmetics, product.Category, product.Id);
        }

        private void CosmeticProductPreview(VisualElement parent, CommerceProduct product)
        {
            var loadout = CosmeticPreviewLoadout(product);
            if (product.Category == CosmeticRules.NICKNAME_EFFECT)
            {
                var name = RawText(parent, lobby.Profile?.DisplayName ?? nickname, "cosmetic-card-nickname");
                DrawCosmeticPresentation.ApplyNickname(name, loadout);
            }
            else if (product.Category == CosmeticRules.CHAT_BUBBLE)
            {
                var bubble = Box(parent, "cosmetic-sample-bubble");
                Text(bubble, "함께 그려요!", "cosmetic-chat-text");
                DrawCosmeticPresentation.ApplyChatBubble(bubble, loadout);
            }
            else
            {
                var avatar = new AvatarElement(lobby.Profile?.AvatarColor ?? avatarColor, lobby.Profile?.Accessory ?? accessory);
                avatar.AddToClassList("cosmetic-card-avatar");
                parent.Add(avatar);
                DrawCosmeticPresentation.ApplyAvatar(avatar, loadout);
            }
        }

        private void PreviewCosmeticProduct(CommerceProduct product)
        {
            _cosmeticPreviewId = product.Id;
            RefreshCosmeticPreview();
            RefreshCosmeticControls();
            if (IsMobile)
            {
                ShowMobileCosmeticPreview();
                return;
            }
            var scroll = _cosmeticPreview?.GetFirstAncestorOfType<ScrollView>();
            if (scroll != null)
                scroll.scrollOffset = new Vector2(0, Mathf.Max(0, _cosmeticPreview.worldBound.y - scroll.contentContainer.worldBound.y));
        }

        private void ShowMobileCosmeticPreview()
        {
            var preview = _cosmeticPreview;
            var owner = _cosmeticGrid?.parent;
            if (preview?.panel == null || owner?.panel == null) return;
            var modal = Modal("미리보기", false);
            modal.name = "cosmetic-preview-popup";
            modal.AddToClassList("cosmetic-preview-popup");
            var scroll = DrawSmoothScroll.Create();
            scroll.AddToClassList("cosmetic-preview-popup-scroll");
            modal.Add(scroll);
            var description = Text(scroll, CosmeticRules.Find(_cosmeticPreviewId)?.Description ?? "", "commerce-description");
            description.name = "cosmetic-preview-description";
            preview.style.display = DisplayStyle.Flex;
            scroll.Add(preview);
            Button(modal, "닫기", CloseModal, "secondary cosmetic-preview-close", DrawSound.UiCancel).name = "cosmetic-preview-close";
            modal.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                if (!ReferenceEquals(evt.target, modal) || _cosmeticPreview != preview || preview.hierarchy.parent != scroll.contentContainer) return;
                preview.style.display = DisplayStyle.None;
                root.schedule.Execute(() =>
                {
                    if (_cosmeticPreview != preview || preview.hierarchy.parent != scroll.contentContainer) return;
                    if (owner.panel != null) owner.Insert(0, preview);
                    else
                    {
                        preview.RemoveFromHierarchy();
                        _cosmeticPreview = null;
                    }
                }).StartingIn(1);
            });
        }

        private void RefreshCosmeticPreview()
        {
            if (_cosmeticPreview?.panel == null) return;
            _cosmeticPreview.Clear();
            var product = CosmeticRules.Find(_cosmeticPreviewId);
            var description = overlay?.Q<Label>("cosmetic-preview-description");
            if (description != null)
            {
                SetText(description, product?.Description ?? "");
                description.style.display = product == null ? DisplayStyle.None : DisplayStyle.Flex;
            }
            var loadout = CosmeticPreviewLoadout(product);
            var identity = Box(_cosmeticPreview, "cosmetic-preview-identity");
            var avatar = new AvatarElement(lobby.Profile?.AvatarColor ?? avatarColor, lobby.Profile?.Accessory ?? accessory);
            avatar.AddToClassList("cosmetic-preview-avatar");
            identity.Add(avatar);
            DrawCosmeticPresentation.ApplyAvatar(avatar, loadout);
            var details = Box(identity, "cosmetic-preview-details");
            var name = RawText(details, lobby.Profile?.DisplayName ?? nickname, "cosmetic-preview-nickname");
            DrawCosmeticPresentation.ApplyNickname(name, loadout);
            var bubble = Box(details, "cosmetic-sample-bubble");
            Text(bubble, "함께 그려요!", "cosmetic-chat-text");
            DrawCosmeticPresentation.ApplyChatBubble(bubble, loadout);
            var controls = Box(_cosmeticPreview, "cosmetic-preview-controls");
            Text(controls, product == null ? "현재 착용 모습" : product.Name, "cosmetic-preview-title");
            if (product != null)
            {
                Text(controls, "미리보기 중", "cosmetic-preview-status");
                var selected = (lobby.Commerce?.Products ?? Array.Empty<CommerceProduct>()).FirstOrDefault(candidate => candidate?.Id == product.Id);
                if (selected != null)
                {
                    var apply = Button(controls, "구매", () => UseCosmeticProduct(selected), "primary cosmetic-product-action", DrawSound.UiConfirm);
                    apply.name = "cosmetic-preview-apply";
                    apply.userData = selected;
                }
            }
            var reset = Button(controls, "착용 모습 보기", () =>
            {
                _cosmeticPreviewId = "";
                RefreshCosmeticPreview();
                RefreshCosmeticControls();
            }, "secondary cosmetic-preview-reset", DrawSound.UiCancel);
            reset.name = "cosmetic-preview-reset";
            var basic = Button(controls, "기본 효과로 변경", () => SaveCosmeticSelection(_cosmeticCategory, ""), "secondary cosmetic-clear", DrawSound.UiCancel);
            basic.name = "cosmetic-clear";
            basic.userData = _cosmeticCategory;
        }

        private void UseCosmeticProduct(CommerceProduct product)
        {
            if (lobby.OwnsCosmetic(product.Id)) SaveCosmeticSelection(product.Category, product.Id);
            else ConfirmCommercePurchase(product);
        }

        private void SaveCosmeticSelection(string category, string productId)
        {
            if (runningAction || lobby.IsBusy || CosmeticRules.GetEquipped(lobby.Profile?.Cosmetics, category) == productId) return;
            string account = lobby.Profile?.AccountId;
            Run(async () =>
            {
                RefreshCosmeticControls();
                await lobby.SaveCosmeticAsync(category, productId, destroyCancellationToken);
                if (account != lobby.Profile?.AccountId) return;
                _cosmeticPreviewId = "";
                RefreshCosmeticCatalogueView();
            });
        }

        private void ConfirmCosmeticPurchase(CommerceProduct product)
        {
            string account = lobby.Profile.AccountId;
            var modal = Modal("장식 구매");
            modal.name = "cosmetic-confirm";
            var preview = Box(modal, "cosmetic-product-preview");
            CosmeticProductPreview(preview, product);
            Text(modal, "{0} 구매 · {1} 보석", "commerce-confirm-description", L.Text(product.Name), product.PricePaidGems);
            Text(modal, product.Description, "commerce-description cosmetic-confirm-effect");
            Text(modal, "한 번 구매하면 계속 사용할 수 있어요.", "commerce-description");
            var actions = Box(modal, "cosmetic-confirm-actions");
            void Purchase(bool equip) => Run(async () =>
            {
                if (account != lobby.Profile?.AccountId || modal.panel == null) return;
                modal.SetEnabled(false);
                RefreshCosmeticControls();
                try
                {
                    if (!lobby.OwnsCosmetic(product.Id))
                        await lobby.PurchaseCommerceAsync(product.Id, product.PricePaidGems, destroyCancellationToken);
                    if (account != lobby.Profile?.AccountId) return;
                    if (equip && lobby.OwnsCosmetic(product.Id))
                        await lobby.SaveCosmeticAsync(product.Category, product.Id, destroyCancellationToken);
                    if (account != lobby.Profile?.AccountId) return;
                    _cosmeticPreviewId = equip ? "" : product.Id;
                    if (overlay?.Q<VisualElement>("cosmetic-confirm") == modal) CloseModal();
                    RefreshCosmeticCatalogueView();
                }
                finally { if (modal.panel != null) modal.SetEnabled(true); }
            });
            Button(actions, "구매 후 착용", () => Purchase(true), "primary", DrawSound.UiConfirm).name = "cosmetic-confirm-equip";
            Button(actions, "구매", () => Purchase(false), "secondary", DrawSound.UiConfirm).name = "cosmetic-confirm-buy";
            Button(actions, "취소", CloseModal, "secondary", DrawSound.UiCancel).name = "cosmetic-confirm-cancel";
        }

        private void RefreshCosmeticControls()
        {
            if (_shopSection != ShopSection.PremiumCosmetics || _commerceCatalogue?.panel == null) return;
            bool busy = runningAction || lobby.IsBusy;
            root.Query<Button>(className: "cosmetic-product-action").ForEach(button =>
            {
                var product = button.userData as CommerceProduct;
                if (product == null) return;
                bool owned = lobby.OwnsCosmetic(product.Id);
                bool equipped = CosmeticRules.GetEquipped(lobby.Profile?.Cosmetics, product.Category) == product.Id;
                SetText(button, owned ? equipped ? "착용 중" : "착용" : "구매");
                button.SetEnabled(!busy && (owned ? !equipped : lobby.CanPurchaseCommerce(product)));
            });
            root.Query<Button>(className: "cosmetic-clear").ForEach(button =>
                button.SetEnabled(!busy && !string.IsNullOrEmpty(CosmeticRules.GetEquipped(lobby.Profile?.Cosmetics, button.userData as string))));
            root.Query<Button>(className: "cosmetic-preview-reset").ForEach(button => button.SetEnabled(!string.IsNullOrEmpty(_cosmeticPreviewId)));
            root.Query<Button>(className: "cosmetic-category").ForEach(button =>
                button.EnableInClassList("commerce-selected", button.userData as string == _cosmeticCategory));
            root.Query<VisualElement>(className: "cosmetic-product").ForEach(card =>
            {
                var product = card.userData as CommerceProduct;
                card.EnableInClassList("cosmetic-selected", product?.Id == _cosmeticPreviewId);
                card.EnableInClassList("cosmetic-equipped", product != null && CosmeticRules.GetEquipped(lobby.Profile?.Cosmetics, product.Category) == product.Id);
            });
        }

        private void SizeCosmeticCards()
        {
            float width = IsMobile ? _cosmeticGrid?.contentRect.width ?? 0
                : _cosmeticGrid?.GetFirstAncestorOfType<ScrollView>()?.contentViewport.contentRect.width ?? 0;
            if (width <= 0) return;
            float gap = IsMobile ? 8 : 12;
            int columns = IsMobile ? MobileShopColumns(width, gap)
                : width >= 760 ? 3 : width >= 410 ? 2 : 1;
            float cardWidth = Mathf.Max(0, Mathf.Floor((width - gap * (columns - 1)) / columns) - 1);
            int index = 0;
            foreach (var card in _cosmeticGrid.Children().Where(element => element.ClassListContains("cosmetic-product")))
            {
                card.style.width = cardWidth;
                card.style.marginRight = index++ % columns == columns - 1 ? 0 : gap;
            }
        }
    }
}
