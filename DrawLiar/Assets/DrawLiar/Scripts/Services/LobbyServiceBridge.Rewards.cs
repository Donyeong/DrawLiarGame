using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private const int MAX_CACHED_MATCH_REWARDS = 32;
        private readonly Dictionary<string, MatchRewardResponse> _matchRewards = new Dictionary<string, MatchRewardResponse>();
        private readonly Queue<string> _pendingMatchRewards = new Queue<string>();
        private readonly HashSet<string> _queuedMatchRewards = new HashSet<string>();
        private readonly Queue<string> _rewardCacheOrder = new Queue<string>();
        private bool _matchRewardPolling;
        private string _activeRewardMatchId = "";
        private int _matchRewardRevision, _rewardProfileRevision;
        private double _nextMatchRewardPoll;
        public event Action MatchRewardsChanged;

        public MatchRewardResponse MatchReward(string matchId) => !string.IsNullOrEmpty(matchId)
            && _matchRewards.TryGetValue(matchId, out var reward) ? reward : null;

        private void TrackMatchReward(RoomSnapshot state)
        {
            if (!IsAuthenticated || _loggingOut) return;
            string matchId = state != null && Guid.TryParse(state.MatchId, out var id) && id != Guid.Empty ? id.ToString() : "";
            if (_activeRewardMatchId.Length > 0 && matchId != _activeRewardMatchId) QueueMatchReward(_activeRewardMatchId);
            if (matchId.Length == 0) { _activeRewardMatchId = ""; return; }
            if (!state.LocalRewardEligible) return;
            _activeRewardMatchId = matchId;
            if (state.IsMatchComplete) QueueMatchReward(matchId);
        }

        private void QueueMatchReward(string matchId)
        {
            if (MatchReward(matchId)?.Recorded == true || _queuedMatchRewards.Contains(matchId)) return;
            if (_pendingMatchRewards.Count >= MAX_CACHED_MATCH_REWARDS)
                _queuedMatchRewards.Remove(_pendingMatchRewards.Dequeue());
            _queuedMatchRewards.Add(matchId); _pendingMatchRewards.Enqueue(matchId);
            _nextMatchRewardPoll = Math.Min(_nextMatchRewardPoll, Time.realtimeSinceStartupAsDouble);
        }

        private void PollPendingMatchRewards()
        {
            if (IsAuthenticated && !_loggingOut && !_matchRewardPolling && !IsBusy
                && _pendingMatchRewards.Count > 0 && Time.realtimeSinceStartupAsDouble >= _nextMatchRewardPoll)
                _ = RefreshMatchRewardAsync();
        }

        private async Task RefreshMatchRewardAsync()
        {
            _matchRewardPolling = true;
            int revision = _matchRewardRevision, profileRevision = _rewardProfileRevision;
            string session = _gameSession, account = Profile.AccountId, matchId = _pendingMatchRewards.Dequeue();
            double delay = 5;
            bool settled = false;
            bool Current() => this != null && !_loggingOut && revision == _matchRewardRevision
                && session == _gameSession && Profile?.AccountId == account && !_lifetime.IsCancellationRequested;
            try
            {
                var reward = await SendAsync<MatchRewardResponse>(_gameServerUrl,
                    "/api/matches/" + Uri.EscapeDataString(matchId) + "/reward", "GET", bearer: session);
                if (!Current()) return;
                if (reward.MatchId != matchId || reward.CoinReward < 0 || reward.Profile?.AccountId != account || reward.Profile.Coins < 0)
                    throw new InvalidOperationException();
                if (!reward.Recorded) return;
                if (!_matchRewards.ContainsKey(matchId)) _rewardCacheOrder.Enqueue(matchId);
                _matchRewards[matchId] = reward;
                while (_rewardCacheOrder.Count > MAX_CACHED_MATCH_REWARDS) _matchRewards.Remove(_rewardCacheOrder.Dequeue());
                if (profileRevision == _rewardProfileRevision && !IsBusy)
                {
                    Profile.Coins = reward.Profile.Coins;
                    ++_rewardProfileRevision;
                    settled = true;
                }
                MatchRewardsChanged?.Invoke();
            }
            catch (OperationCanceledException) { delay = 10; }
            catch (Exception) { delay = 30; }
            finally
            {
                if (Current())
                {
                    if (settled) _queuedMatchRewards.Remove(matchId);
                    else _pendingMatchRewards.Enqueue(matchId);
                    _nextMatchRewardPoll = Time.realtimeSinceStartupAsDouble + delay;
                }
                _matchRewardPolling = false;
            }
        }

        private void ResetMatchRewards()
        {
            ++_matchRewardRevision; ++_rewardProfileRevision;
            _matchRewards.Clear(); _rewardCacheOrder.Clear(); _pendingMatchRewards.Clear(); _queuedMatchRewards.Clear();
            _activeRewardMatchId = "";
            _nextMatchRewardPoll = 0;
            MatchRewardsChanged?.Invoke();
        }
    }
}
