using System;
using System.Globalization;
using System.Linq;

namespace DrawLiar
{
    internal sealed class DrawLiarRevealPlan
    {
        private const float ENTRANCE_SECONDS = .24f;
        private const float FIRST_PAUSE_SECONDS = 1.1f;
        private const float SECOND_PAUSE_SECONDS = 1.4f;
        private const float VERDICT_HOLD_SECONDS = .8f;
        private const float TRANSITION_SECONDS = .25f;
        private const float READING_SECONDS = 1.6f;

        public string[] Verdict { get; private set; } = Array.Empty<string>();
        public string Identity { get; private set; }
        public float[] VerdictStarts { get; private set; } = Array.Empty<float>();
        public float VerdictCharacterSeconds { get; private set; }
        public float IdentityCharacterSeconds { get; private set; }
        public float IdentityStarts { get; private set; }
        public float IdentityFinishes { get; private set; }
        public bool IsCorrectAccusation { get; private set; }
        public bool HasVerdict => Verdict.Length == 3;

        public static DrawLiarRevealPlan Create(RoomSnapshot state)
        {
            var plan = new DrawLiarRevealPlan();
            if (state.HasAccused && state.Settings.LiarMode == LiarMode.Optional
                && state.AccusedPlayerId == GameRules.NO_LIAR_TARGET)
            {
                plan.IsCorrectAccusation = state.RevealedLiarCount == 0;
                plan.Verdict = SplitVerdict(plan.IsCorrectAccusation
                    ? "라이어는|이번 라운드에|없었습니다." : "라이어는|이번 라운드에|있었습니다.", "");
            }
            else if (state.HasAccused)
            {
                var accused = state.Players.FirstOrDefault(player => player.Id == state.AccusedPlayerId);
                if (accused != null)
                {
                    plan.IsCorrectAccusation = accused.IsLiar;
                    string key = accused.IsLiar
                        ? (HasFinalConsonant(accused.Name) ? "{0}은|라이어가|맞았습니다." : "{0}는|라이어가|맞았습니다.")
                        : (HasFinalConsonant(accused.Name) ? "{0}은|라이어가|아니었습니다." : "{0}는|라이어가|아니었습니다.");
                    plan.Verdict = SplitVerdict(key, accused.Name);
                }
            }

            var names = state.Players.Where(player => player.IsLiar).Select(player => player.Name).ToArray();
            plan.Identity = state.RevealedLiarCount == 0
                ? DrawLocalization.Text("이번 라운드에는 라이어가 없습니다.")
                : names.Length > 0 ? DrawLocalization.Format("라이어는 {0}입니다.", string.Join(", ", names))
                : DrawLocalization.Format("라이어 {0}명", state.RevealedLiarCount);

            float verdictTyping = plan.Verdict.Sum(text => DrawAnimatedText.MeasureDuration(text, .1f));
            float identityTyping = DrawAnimatedText.MeasureDuration(plan.Identity, .08f);
            float fixedTime = ENTRANCE_SECONDS + READING_SECONDS + (plan.HasVerdict
                ? FIRST_PAUSE_SECONDS + SECOND_PAUSE_SECONDS + VERDICT_HOLD_SECONDS + TRANSITION_SECONDS : 0);
            float budget = GameRules.RevealDuration(state.Settings.RevealSeconds, state.RevealedLiarCount) - fixedTime;
            float scale = Math.Min(1, Math.Max(.01f, budget) / Math.Max(.01f, verdictTyping + identityTyping));
            plan.VerdictCharacterSeconds = .1f * scale;
            plan.IdentityCharacterSeconds = .08f * scale;
            float at = ENTRANCE_SECONDS;
            if (plan.HasVerdict)
            {
                plan.VerdictStarts = new float[3];
                for (int index = 0; index < 3; index++)
                {
                    plan.VerdictStarts[index] = at;
                    at += DrawAnimatedText.MeasureDuration(plan.Verdict[index], plan.VerdictCharacterSeconds);
                    at += index == 0 ? FIRST_PAUSE_SECONDS : index == 1 ? SECOND_PAUSE_SECONDS
                        : VERDICT_HOLD_SECONDS + TRANSITION_SECONDS;
                }
            }
            plan.IdentityStarts = at;
            plan.IdentityFinishes = at + DrawAnimatedText.MeasureDuration(plan.Identity, plan.IdentityCharacterSeconds);
            return plan;
        }

        private static string[] SplitVerdict(string key, string name)
        {
            return DrawLocalization.Text(key).Split('|')
                .Select(part => string.Format(CultureInfo.InvariantCulture, part, name ?? "")).ToArray();
        }

        private static bool HasFinalConsonant(string value)
        {
            value = value?.TrimEnd();
            if (string.IsNullOrEmpty(value)) return true;
            char last = value[value.Length - 1];
            return last < '\uAC00' || last > '\uD7A3' || (last - '\uAC00') % 28 != 0;
        }
    }
}
