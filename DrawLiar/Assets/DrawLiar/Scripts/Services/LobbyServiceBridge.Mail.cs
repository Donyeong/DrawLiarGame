using System;
using System.Linq;
using System.Threading.Tasks;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        public MailData[] Mails { get; private set; } = Array.Empty<MailData>();
        public int MailCount => Inbox.MailCount;
        public event Action MailChanged;

        public async Task RefreshMailAsync()
        {
            RequireLogin();
            string session = _gameSession;
            var response = await SendGameAsync<MailListResponse>("/api/mail", "GET");
            if (session != _gameSession) return;
            Mails = response.Mails ?? Array.Empty<MailData>();
            MailChanged?.Invoke();
        }

        // 보상 지급과 중복 수령 방지는 서버가 처리하고, 클라이언트는 확정된 프로필만 반영한다.
        public Task ClaimMailAsync(string[] mailIds) => RunAsync(async () =>
        {
            RequireLogin();
            var response = await SendGameAsync<MailClaimResponse>("/api/mail/claim", "POST", new MailClaimRequest { MailIds = mailIds });
            SetProfile(response.Profile);
            // 요청한 우편은 수령했거나 더 이상 받을 수 없으므로 목록에서 빼고, 받은편지함 수는 다음 프레임에 다시 조회한다.
            Mails = Mails.Where(mail => Array.IndexOf(mailIds, mail.MailId) < 0).ToArray();
            Inbox.MailCount = Math.Max(0, Inbox.MailCount - response.ClaimedCount);
            ++_socialRevision;
            _nextSocialPoll = 0;
            MailChanged?.Invoke();
            if (response.ClaimedCount > 0) SetStatus("우편 {0}개를 받았습니다.", response.ClaimedCount);
            else SetStatus("받을 수 있는 우편이 없습니다.");
        });

        private void ResetMail()
        {
            Mails = Array.Empty<MailData>();
            MailChanged?.Invoke();
        }
    }
}
