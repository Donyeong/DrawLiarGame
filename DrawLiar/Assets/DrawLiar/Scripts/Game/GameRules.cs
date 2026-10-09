#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DrawLiar
{
    public static class GameRules
    {
        public const int MIN_START_PLAYERS = 3;
        public const int NO_LIAR_TARGET = -1;
        public const int MAX_PLAYERS = ServerRoomSettings.MAX_PLAYERS;
        public const int MAX_CANVAS_STROKES = 12000;
        public const int MAX_ROUND_STROKES = MAX_CANVAS_STROKES * MAX_PLAYERS;
        public const float JUDGMENT_COIN_TOSS_SECONDS = 3f;
        public const string TieRule = "최다 득표 동률은 무작위로 결정 · 첫 찬반 동수는 부결, 이후 동전";

        public static int MinimumPlayers(LiarMode mode) => MIN_START_PLAYERS;

        public static string CleanText(string value, int maxLength, string fallback = "")
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var result = new StringBuilder();
            value = value.Trim();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    {
                        if (result.Length + 2 > maxLength) break;
                        result.Append(character).Append(value[++index]);
                    }
                    continue;
                }
                if (char.IsLowSurrogate(character)) continue;
                if (!char.IsControl(character) && character != '<' && character != '>') result.Append(character);
                if (result.Length >= maxLength) break;
            }
            return result.Length == 0 ? fallback : result.ToString();
        }

        public static string NormalizeGuess(string value)
        {
            return new string(CleanText(value, 80).Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character)).ToArray()).ToLowerInvariant();
        }

        public static int? SelectAccused(IEnumerable<int> votes, Random random)
        {
            var counts = votes.GroupBy(id => id).ToArray();
            if (counts.Length == 0) return null;
            var maximum = counts.Max(group => group.Count());
            var tied = counts.Where(group => group.Count() == maximum).Select(group => group.Key).OrderBy(id => id).ToArray();
            return tied[random.Next(tied.Length)];
        }

        public static int RoundScore(bool liar, bool caught, bool correctGuess, bool correctVote, ScoreRules rules)
        {
            int judgmentBonus = correctVote ? Math.Max(0, rules.CitizenCorrectVote) : 0;
            return judgmentBonus + (liar
                ? (caught ? 0 : Math.Max(0, rules.LiarUncaught)) + (correctGuess ? Math.Max(0, rules.LiarCorrectGuess) : 0)
                : 0);
        }

        public static bool ValidStroke(DrawStroke stroke, int version)
        {
            return stroke.CanvasVersion == version && InRange(stroke.X1, 0, 1) && InRange(stroke.Y1, 0, 1)
                && InRange(stroke.X2, 0, 1) && InRange(stroke.Y2, 0, 1) && InRange(stroke.Size, .001f, .08f)
                && (stroke.StartSize == 0 || InRange(stroke.StartSize, .001f, .08f));
        }

        private static bool InRange(float value, float min, float max) => value >= min && value <= max;
    }
}
