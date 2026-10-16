using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private IDrawAdvertisingProvider _advertisingProvider;
        private CancellationTokenSource _advertisingLifetime, _advertisingProviderLifetime;
        private Task _advertisingRefresh;
        private string _advertisingSession = "", _advertisingAccount = "", _advertisingProviderKey = "";
        private string _advertisingOperation = "", _advertisingWatchedTicket = "";
        private AdsRewardTicket _advertisingPendingTicket;
        private bool _advertisingLoaded, _advertisingClaimBusy, _advertisingSubscriber, _advertisingProfileRefreshPending;
        private double _nextAdvertisingPoll;
        private int _advertisingRevision, _advertisingStateRevision;

        public AdsStateResponse Advertisements { get; private set; } = new AdsStateResponse();
        public bool IsDailyAdRewardBusy => _advertisingClaimBusy;
        public bool IsDailyAdRewardPending => CurrentAdvertisingState && _advertisingPendingTicket != null
            && _advertisingPendingTicket.Ticket == _advertisingWatchedTicket && !Advertisements.ClaimedToday;
        public bool CanClaimDailyAdReward => CurrentAdvertisingState && Advertisements.CanClaimDailyReward
            && (IsPainterSubscriber || IsDailyAdRewardPending || _advertisingProvider?.IsRewardedReady == true);
        public bool RequiresAdPrivacyOptions => CurrentAdvertisingState && !IsPainterSubscriber
            && !Advertisements.AdsRemoved && _advertisingProvider?.IsPrivacyOptionsRequired == true;
        public int AdvertisingBannerHeightPixels => _advertisingProvider?.BannerHeightPixels ?? 0;
        public event Action DailyAdRewardChanged;

        private bool CurrentAdvertisingState => _advertisingLoaded && IsAuthenticated && !_loggingOut
            && DrawAdvertisingProviders.IsSupportedPlatform && _advertisingSession == _gameSession
            && _advertisingAccount == Profile?.AccountId && Advertisements.Platform == DrawAdvertisingProviders.Platform
            && DateTimeOffset.TryParse(Advertisements.NextResetAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var reset) && reset > CommerceUtcNow;

        private void ObserveAdvertisingProfile()
        {
            if (!DrawAdvertisingProviders.IsSupportedPlatform) return;
            EnsureAdvertisingSession();
            bool subscriber = IsPainterSubscriber;
            if (subscriber != _advertisingSubscriber)
            {
                _advertisingSubscriber = subscriber;
                _advertisingLoaded = false;
                ++_advertisingStateRevision;
                DisposeAdvertisingProvider();
            }
            _nextAdvertisingPoll = 0;
            DailyAdRewardChanged?.Invoke();
        }

        private void EnsureAdvertisingSession()
        {
            if (_advertisingSession == _gameSession && _advertisingAccount == Profile?.AccountId) return;
            ResetAdvertising();
            if (!IsAuthenticated || Profile == null || _loggingOut) return;
            _advertisingSession = _gameSession; _advertisingAccount = Profile.AccountId;
            _advertisingLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _advertisingSubscriber = IsPainterSubscriber;
        }

        private void PollAdvertising()
        {
            if (!DrawAdvertisingProviders.IsSupportedPlatform || !IsAuthenticated || _loggingOut || Profile == null) return;
            EnsureAdvertisingSession();
            if (_advertisingSubscriber != IsPainterSubscriber) ObserveAdvertisingProfile();
            if (IsBusy || _advertisingClaimBusy || _advertisingRefresh != null && !_advertisingRefresh.IsCompleted) return;
            if (Time.realtimeSinceStartupAsDouble < _nextAdvertisingPoll) return;
            _ = PollAdvertisingAsync();
        }

        private async Task PollAdvertisingAsync()
        {
            try { await RefreshAdvertisingAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        public Task RefreshAdvertisingAsync(CancellationToken cancellationToken = default)
        {
            if (!DrawAdvertisingProviders.IsSupportedPlatform) return Task.CompletedTask;
            RequireLogin(); EnsureAdvertisingSession();
            if (_advertisingRefresh != null && !_advertisingRefresh.IsCompleted) return _advertisingRefresh;
            return _advertisingRefresh = RefreshAdvertisingCoreAsync(cancellationToken);
        }

        private async Task RefreshAdvertisingCoreAsync(CancellationToken cancellationToken)
        {
            string session = _advertisingSession, account = _advertisingAccount;
            int revision = _advertisingRevision;
            int stateRevision = _advertisingStateRevision;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_advertisingLifetime.Token, cancellationToken);
            var token = cancellation.Token;
            try
            {
                var status = await SendAsync<AdsStateResponse>(_gameServerUrl, "/api/ads?platform=" + DrawAdvertisingProviders.Platform,
                    "GET", bearer: session, cancellationToken: token);
                EnsureAdvertisingIdentity(session, account, revision, token);
                if (stateRevision != _advertisingStateRevision) return;
                _advertisingProfileRefreshPending |= status?.ClaimedToday == true && !Advertisements.ClaimedToday;
                ApplyAdvertisingStatus(status);
                if (_advertisingProfileRefreshPending)
                {
                    long profileRevision = _rewardProfileRevision;
                    var profile = await SendAsync<ProfileData>(_gameServerUrl, "/api/profile", "GET", bearer: session, cancellationToken: token);
                    EnsureAdvertisingIdentity(session, account, revision, token);
                    if (profileRevision == _rewardProfileRevision)
                    { _advertisingProfileRefreshPending = false; ApplyCommerceProfile(profile, account); }
                }
                await ConfigureAdvertisingProviderAsync(token);
            }
            finally
            {
                if (revision == _advertisingRevision && session == _gameSession)
                {
                    double delay = !_advertisingLoaded && stateRevision != _advertisingStateRevision ? 0 : 30;
                    if (_advertisingLoaded && DateTimeOffset.TryParse(Advertisements.NextResetAt, out var reset))
                        delay = Math.Min(delay, Math.Max(1, (reset - CommerceUtcNow).TotalSeconds));
                    _nextAdvertisingPoll = Time.realtimeSinceStartupAsDouble + delay;
                }
            }
        }

        private void ApplyAdvertisingStatus(AdsStateResponse status)
        {
            if (status == null || status.Platform != DrawAdvertisingProviders.Platform
                || status.RewardPaidGems < 0 || status.RewardPaidGems > AdsRules.MAX_REWARD_PAID_GEMS
                || status.ServerTimeUnixSeconds <= 0 || status.ServerTimeUnixSeconds > 253402300799
                || !DateTimeOffset.TryParse(status.NextResetAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                throw new InvalidOperationException("광고를 이용할 수 없습니다.");
            if (!string.IsNullOrEmpty(Advertisements.NextResetAt) && Advertisements.NextResetAt != status.NextResetAt)
            { _advertisingOperation = ""; _advertisingWatchedTicket = ""; _advertisingPendingTicket = null; }
            Advertisements = status; _advertisingLoaded = true;
            ++_advertisingStateRevision;
            CaptureCommerceClock(status.ServerTimeUnixSeconds);
            if (status.ClaimedToday) { _advertisingPendingTicket = null; _advertisingWatchedTicket = ""; }
            DailyAdRewardChanged?.Invoke();
        }

        private async Task ConfigureAdvertisingProviderAsync(CancellationToken token)
        {
            if (IsPainterSubscriber || Advertisements.AdsRemoved || !Advertisements.BannerEnabled && !Advertisements.RewardedEnabled)
            { DisposeAdvertisingProvider(); return; }
            string key = Advertisements.Platform + "|" + Advertisements.BannerEnabled + "|" + Advertisements.RewardedEnabled
                + "|" + Advertisements.BannerAdUnitId + "|" + Advertisements.RewardedAdUnitId
                + "|" + Advertisements.WebPublisherId + "|" + Advertisements.WebBannerSlotId;
            if (_advertisingProviderKey != key) DisposeAdvertisingProvider();
            if (_advertisingProvider == null)
            {
                _advertisingProvider = DrawAdvertisingProviders.Create();
                if (_advertisingProvider == null) return;
                _advertisingProviderKey = key;
                _advertisingProvider.Changed += OnAdvertisingProviderChanged;
                _advertisingProviderLifetime = CancellationTokenSource.CreateLinkedTokenSource(_advertisingLifetime.Token);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _advertisingProviderLifetime.Token);
            await _advertisingProvider.InitializeAsync(Advertisements, cancellation.Token);
            DailyAdRewardChanged?.Invoke();
        }

        private void OnAdvertisingProviderChanged()
        {
            if (_advertisingProvider?.IsDisposed == true) DisposeAdvertisingProvider();
            DailyAdRewardChanged?.Invoke();
        }

        public void SetAdvertisingBannerVisible(bool visible)
        {
            _advertisingProvider?.SetBannerVisible(visible && CurrentAdvertisingState && Advertisements.BannerEnabled
                && !Advertisements.AdsRemoved && !IsPainterSubscriber && !_advertisingClaimBusy);
        }

        public Task ShowAdPrivacyOptionsAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            if (!RequiresAdPrivacyOptions || _advertisingProvider == null) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_advertisingLifetime.Token, cancellationToken);
            try { await _advertisingProvider.ShowPrivacyOptionsAsync(cancellation.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new InvalidOperationException("광고 개인정보 설정을 불러오지 못했습니다."); }
            DailyAdRewardChanged?.Invoke();
        });

        public Task ClaimDailyAdRewardAsync(CancellationToken cancellationToken = default) => RunAsync(async () =>
        {
            RequireLogin(); EnsureAdvertisingSession();
            if (!DrawAdvertisingProviders.IsSupportedPlatform || _advertisingClaimBusy)
                throw new InvalidOperationException("광고를 이용할 수 없습니다.");
            string session = _advertisingSession, account = _advertisingAccount;
            int revision = _advertisingRevision;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_advertisingLifetime.Token, cancellationToken);
            var token = cancellation.Token;
            _advertisingClaimBusy = true; SetAdvertisingBannerVisible(false); DailyAdRewardChanged?.Invoke();
            try
            {
                if (!CurrentAdvertisingState) await RefreshAdvertisingAsync(token);
                EnsureAdvertisingIdentity(session, account, revision, token);
                if (Advertisements.ClaimedToday) throw new InvalidOperationException("오늘 무료 보석을 이미 받았습니다.");
                if (!CanClaimDailyAdReward) throw new InvalidOperationException("광고를 불러오지 못했습니다. 잠시 후 다시 시도하세요.");
                if (string.IsNullOrEmpty(_advertisingOperation)) _advertisingOperation = Guid.NewGuid().ToString("N");
                var response = await SendAsync<AdsRewardResponse>(_gameServerUrl, "/api/ads/rewards/begin", "POST",
                    new AdsRewardBeginRequest { Platform = DrawAdvertisingProviders.Platform, OperationId = _advertisingOperation }, session, token);
                EnsureAdvertisingIdentity(session, account, revision, token);
                ValidateAdvertisingReward(response);
                if (response.State == AdsRules.COMPLETED) { await ApplyAdvertisingRewardAsync(response, account, token); return; }
                if (response.State == AdsRules.EXPIRED)
                { _advertisingOperation = ""; _advertisingWatchedTicket = ""; throw new InvalidOperationException("광고 보상이 만료되었습니다. 다시 시도해 주세요."); }
                _advertisingPendingTicket = response.Ticket;
                if (_advertisingWatchedTicket != response.Ticket.Ticket)
                {
                    var provider = _advertisingProvider;
                    if (provider == null || !provider.IsRewardedReady)
                        throw new InvalidOperationException("광고를 불러오지 못했습니다. 잠시 후 다시 시도하세요.");
                    var playback = await provider.ShowRewardedAsync(response.Ticket, token);
                    EnsureAdvertisingIdentity(session, account, revision, token);
                    if (!playback.Completed) throw new InvalidOperationException("광고를 끝까지 시청하면 보석을 받을 수 있어요.");
                    _advertisingWatchedTicket = response.Ticket.Ticket;
                }
                double deadline = Time.realtimeSinceStartupAsDouble + 40;
                do
                {
                    try
                    {
                        response = DrawAdvertisingProviders.Platform == AdsRules.WEB_GL
                            ? await SendAsync<AdsRewardResponse>(_gameServerUrl, "/api/ads/rewards/complete-web", "POST",
                                new AdsRewardCompleteRequest { Platform = AdsRules.WEB_GL, Ticket = _advertisingWatchedTicket }, session, token)
                            : await SendAsync<AdsRewardResponse>(_gameServerUrl, "/api/ads/rewards/" + Uri.EscapeDataString(_advertisingWatchedTicket)
                                + "?platform=" + DrawAdvertisingProviders.Platform, "GET", bearer: session, cancellationToken: token);
                        EnsureAdvertisingIdentity(session, account, revision, token);
                        ValidateAdvertisingReward(response);
                        if (response.State == AdsRules.COMPLETED) { await ApplyAdvertisingRewardAsync(response, account, token); return; }
                        if (response.State == AdsRules.EXPIRED)
                        { _advertisingOperation = ""; _advertisingWatchedTicket = ""; throw new InvalidOperationException("광고 보상이 만료되었습니다. 다시 시도해 주세요."); }
                    }
                    catch (ApiRequestException error) when (error.Code == "AdRewardNotReady") { }
                    await DrawAsync.Delay(TimeSpan.FromMilliseconds(1500), token);
                } while (Time.realtimeSinceStartupAsDouble < deadline);
                SetStatus("광고 보상을 확인하고 있습니다. 잠시 후 다시 확인해 주세요.");
            }
            finally
            {
                if (revision == _advertisingRevision && session == _gameSession)
                { _advertisingClaimBusy = false; _nextAdvertisingPoll = 0; DailyAdRewardChanged?.Invoke(); }
            }
        });

        private static void ValidateAdvertisingReward(AdsRewardResponse response)
        {
            if (response?.Ticket == null || string.IsNullOrEmpty(response.Ticket.Ticket) || response.Ticket.Ticket.Length > 256
                || response.State != AdsRules.PENDING && response.State != AdsRules.COMPLETED && response.State != AdsRules.EXPIRED)
                throw new InvalidOperationException("광고를 이용할 수 없습니다.");
        }

        private async Task ApplyAdvertisingRewardAsync(AdsRewardResponse response, string account, CancellationToken token)
        {
            ApplyAdvertisingStatus(response.Status);
            ApplyCommerceProfile(response.Profile, account);
            _advertisingPendingTicket = null; _advertisingWatchedTicket = "";
            SetStatus("보석 {0}개를 받았습니다.", response.Status.RewardPaidGems);
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            token.ThrowIfCancellationRequested();
        }

        private void EnsureAdvertisingIdentity(string session, string account, int revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_loggingOut || revision != _advertisingRevision || session != _gameSession || account != Profile?.AccountId)
                throw new OperationCanceledException(token);
        }

        private void DisposeAdvertisingProvider()
        {
            _advertisingProviderLifetime?.Cancel(); _advertisingProviderLifetime?.Dispose(); _advertisingProviderLifetime = null;
            var provider = _advertisingProvider;
            _advertisingProvider = null; _advertisingProviderKey = "";
            if (provider == null) return;
            provider.Changed -= OnAdvertisingProviderChanged;
            provider.Dispose(); DailyAdRewardChanged?.Invoke();
        }

        private void ResetAdvertising()
        {
            ++_advertisingRevision;
            ++_advertisingStateRevision;
            _advertisingLifetime?.Cancel(); _advertisingLifetime?.Dispose(); _advertisingLifetime = null;
            DisposeAdvertisingProvider();
            _advertisingSession = _advertisingAccount = _advertisingOperation = _advertisingWatchedTicket = "";
            _advertisingPendingTicket = null; _advertisingRefresh = null;
            _advertisingLoaded = _advertisingClaimBusy = _advertisingSubscriber = _advertisingProfileRefreshPending = false;
            _nextAdvertisingPoll = 0; Advertisements = new AdsStateResponse();
            DailyAdRewardChanged?.Invoke();
        }
    }
}
