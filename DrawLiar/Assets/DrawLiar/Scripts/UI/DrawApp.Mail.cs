using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private VisualElement _mailList;
        private bool _mailActionBusy;

        private void MailButton(VisualElement parent)
        {
            if (!lobby.IsAuthenticated) return;
            var button = IconButton(parent, "우편함", DrawUIIcon.Kind.Mail, () => OpenUtilityPopup(LobbyScreen.Mail), "lobby-mail");
            button.AddToClassList("mail-entry");
            button.Add(new DrawRedDot());
            RefreshMailControls();
        }

        private void OnMailChanged()
        {
            RefreshMailControls();
            if (!_mailActionBusy) RefreshMailList();
        }

        private void RefreshMailControls()
        {
            if (root == null || lobby == null) return;
            root.Query<Button>(className: "mail-entry").ForEach(button => button.Q<DrawRedDot>()?.SetVisible(lobby.MailCount > 0));
            bool busy = runningAction || lobby.IsBusy || _mailActionBusy;
            root.Query<Button>(className: "mail-claim").ForEach(button => button.SetEnabled(!busy));
        }

        private void MailForm(VisualElement form)
        {
            _mailList = Box(form, "mail-list");
            _mailList.name = "mail-list";
            RefreshMailList();
        }

        private void RefreshMailList()
        {
            if (_mailList?.panel == null) return;
            _mailList.Clear();
            var mails = lobby.Mails;
            if (mails.Length == 0)
            {
                Text(_mailList, "받은 우편이 없습니다.", "social-empty muted");
                return;
            }
            Button(_mailList, "모두 받기", () => ClaimMail(mails.Select(mail => mail.MailId).Take(MailClaimRequest.MAX_MAILS).ToArray()),
                "primary mail-claim mail-claim-all").name = "mail-claim-all";
            foreach (var mail in mails)
            {
                var card = Box(_mailList, "social-card mail-card");
                RawText(card, mail.Title, "mail-title");
                if (!string.IsNullOrEmpty(mail.Message)) RawText(card, mail.Message, "mail-message");
                var footer = Box(card, "row mail-card-footer");
                var rewards = Box(footer, "row mail-rewards grow");
                if (mail.Coins > 0) Text(rewards, "{0} 코인", "mail-reward", mail.Coins);
                if (mail.PaidGems > 0) Text(rewards, "{0} 보석", "mail-reward", mail.PaidGems);
                if (DateTimeOffset.TryParse(mail.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry))
                    Text(footer, "남은 기간: {0}일", "mail-expiry muted", Mathf.Max(1, (int)Math.Ceiling((expiry - lobby.CommerceUtcNow).TotalDays)));
                Button(footer, "받기", () => ClaimMail(new[] { mail.MailId }), "primary mail-claim").name = "mail-claim-" + mail.MailId;
            }
            RefreshMailControls();
        }

        private void RunMailAction(Func<Task> action)
        {
            Run(async () =>
            {
                _mailActionBusy = true;
                RefreshMailControls();
                try { await action(); }
                finally
                {
                    _mailActionBusy = false;
                    RefreshMailList();
                    RefreshMailControls();
                }
            });
        }

        private void ClaimMail(string[] mailIds) => RunMailAction(() => lobby.ClaimMailAsync(mailIds));
    }
}
