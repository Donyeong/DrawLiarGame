using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private const string MUTED_ACCOUNTS_KEY = "DrawLiar.MutedAccounts";
        private readonly HashSet<string> _blockedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _mutedAccounts;

        private HashSet<string> MutedAccounts => _mutedAccounts ??= new HashSet<string>(
            PlayerPrefs.GetString(MUTED_ACCOUNTS_KEY, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        public bool IsBlocked(string accountId) => !string.IsNullOrEmpty(accountId) && _blockedAccounts.Contains(accountId);
        public bool IsChatMuted(string accountId) => !string.IsNullOrEmpty(accountId) && MutedAccounts.Contains(accountId);
        public bool IsChatHidden(string accountId) => IsBlocked(accountId) || IsChatMuted(accountId);

        // 채팅 숨기기는 이 기기에만 저장하며 서버와 상대에게 알리지 않는다.
        public void SetChatMuted(string accountId, bool muted)
        {
            if (!Guid.TryParse(accountId, out _) || !(muted ? MutedAccounts.Add(accountId) : MutedAccounts.Remove(accountId))) return;
            PlayerPrefs.SetString(MUTED_ACCOUNTS_KEY, string.Join(",", MutedAccounts));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        // 차단 목록을 받지 못해도 로그인과 다른 기능은 계속 쓸 수 있게 한다.
        private async Task RefreshBlocksAsync()
        {
            _blockedAccounts.Clear();
            try
            {
                var response = await SendGameAsync<BlockListResponse>("/api/blocks", "GET");
                foreach (string id in response.AccountIds ?? Array.Empty<string>()) _blockedAccounts.Add(id);
            }
            catch (InvalidOperationException) { }
        }

        public Task SetBlockedAsync(string accountId, bool blocked) => RunAsync(async () =>
        {
            RequireLogin();
            if (!Guid.TryParse(accountId, out var id)) throw new InvalidOperationException("프로필을 불러오지 못했습니다.");
            ++_socialRevision;
            await SendGameAsync<ApiError>("/api/blocks/" + id, blocked ? "PUT" : "DELETE");
            if (blocked) _blockedAccounts.Add(id.ToString()); else _blockedAccounts.Remove(id.ToString());
            await RefreshSocialAsync();
            SetStatus(blocked ? "차단했습니다. 이 플레이어의 채팅이 보이지 않습니다." : "차단을 해제했습니다.");
        });

        public Task ReportAsync(PlayerReportRequest request) => RunAsync(async () =>
        {
            RequireLogin();
            await SendGameAsync<ApiError>("/api/reports", "POST", request);
            SetStatus("신고를 접수했습니다. 검토 후 조치합니다.");
        });
    }
}
