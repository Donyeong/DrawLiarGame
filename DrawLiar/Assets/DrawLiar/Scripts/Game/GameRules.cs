using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DrawLiar
{
    public static class GameRules
    {
        public const string TieRule = "최다 득표 동률은 모두 지목 · 투표가 없으면 아무도 지목되지 않아요";

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

        public static HashSet<int> CaughtPlayers(IEnumerable<int> votes)
        {
            var counts = votes.GroupBy(id => id).ToArray();
            if (counts.Length == 0) return new HashSet<int>();
            var maximum = counts.Max(group => group.Count());
            return new HashSet<int>(counts.Where(group => group.Count() == maximum).Select(group => group.Key));
        }

        public static int RoundScore(bool liar, bool caught, bool correctGuess, bool correctVote, ScoreRules rules)
        {
            return liar
                ? (caught ? 0 : Math.Max(0, rules.LiarUncaught)) + (correctGuess ? Math.Max(0, rules.LiarCorrectGuess) : 0)
                : (correctVote ? Math.Max(0, rules.CitizenCorrectVote) : 0);
        }

        public static bool ValidStroke(DrawStroke stroke, int version)
        {
            return stroke.CanvasVersion == version && InRange(stroke.X1, 0, 1) && InRange(stroke.Y1, 0, 1)
                && InRange(stroke.X2, 0, 1) && InRange(stroke.Y2, 0, 1) && InRange(stroke.Size, .001f, .08f);
        }

        private static bool InRange(float value, float min, float max) => value >= min && value <= max;
    }
}
