using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private LobbyServiceBridge _advertisingEventBridge;
        private bool _refreshingAdvertising;

        private void AttachAdvertisingEvents()
        {
            if (lobby == null) lobby = GetComponent<LobbyServiceBridge>();
            if (_advertisingEventBridge == lobby) return;
            DetachAdvertisingEvents();
            if (lobby == null) return;
            _advertisingEventBridge = lobby;
            lobby.DailyAdRewardChanged += RefreshAdvertisingControls;
            RefreshAdvertisingControls();
        }

        private void DetachAdvertisingEvents()
        {
            if (_advertisingEventBridge != null)
            {
                _advertisingEventBridge.DailyAdRewardChanged -= RefreshAdvertisingControls;
                _advertisingEventBridge.SetAdvertisingBannerVisible(false);
            }
            _advertisingEventBridge = null;
            _mobileLayout?.SetBottomReservedPixels(0);
        }

        private void RefreshAdvertisingControls()
        {
            if (_refreshingAdvertising || root == null || lobby == null) return;
            _refreshingAdvertising = true;
            try
            {
                bool modalOpen = false;
                root.Query<VisualElement>(className: "overlay").ForEach(element =>
                { if (IsVisible(element)) modalOpen = true; });
                bool visible = isActiveAndEnabled && lobby.IsAuthenticated && !lobby.IsPainterSubscriber
                    && _mobileLayout?.IsKeyboardOpen != true && !modalOpen;
                lobby.SetAdvertisingBannerVisible(visible);
                _mobileLayout?.SetBottomReservedPixels(lobby.AdvertisingBannerHeightPixels);
                RefreshDailyAdRewardView();
            }
            finally { _refreshingAdvertising = false; }
        }
    }
}
