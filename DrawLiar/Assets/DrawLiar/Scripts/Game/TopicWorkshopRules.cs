#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DrawLiar
{
    public static class TopicWorkshopRules
    {
        public static int TextLength(string value) => (value ?? "").Length;

        public static bool ValidatePolicy(TopicWorkshopPolicy policy)
        {
            var limits = policy?.Limits;
            return limits != null && limits.NameMaxLength >= 1 && limits.NameMaxLength <= 40
                && limits.WordMaxLength >= 1 && limits.WordMaxLength <= 40
                && limits.MaxWordsPerTopic >= 1 && limits.MaxWordsPerTopic <= 200
                && limits.MaxUploadsPerAccount >= 1 && policy.LanguageCodes != null && policy.LanguageCodes.Length > 0
                && !policy.LanguageCodes.Any(string.IsNullOrWhiteSpace)
                && policy.LanguageCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == policy.LanguageCodes.Length;
        }

        public static string[] SplitWords(string value) => (value ?? "")
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !string.IsNullOrWhiteSpace(word)).ToArray();

        public static bool TryNormalize(TopicWorkshopPublishRequest request, TopicWorkshopPolicy policy,
            out TopicWorkshopPublishRequest normalized, out string errorCode)
        {
            normalized = null;
            errorCode = "";
            if (!ValidatePolicy(policy)) throw new ArgumentException("TopicWorkshopInvalidPolicy");
            string name = (request?.Name ?? "").Trim();
            if (!ValidText(name, policy.Limits.NameMaxLength))
            {
                errorCode = "TopicWorkshopInvalidName";
                return false;
            }
            string language = policy.LanguageCodes.FirstOrDefault(code =>
                string.Equals(code, request.LanguageCode, StringComparison.OrdinalIgnoreCase));
            if (language == null)
            {
                errorCode = "TopicWorkshopInvalidLanguage";
                return false;
            }
            if (request.Words == null || request.Words.Length == 0)
            {
                errorCode = "TopicWorkshopInvalidWords";
                return false;
            }
            if (request.Words.Length > policy.Limits.MaxWordsPerTopic)
            {
                errorCode = "TopicWorkshopTooManyWords";
                return false;
            }
            var words = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in request.Words)
            {
                string word = (value ?? "").Trim();
                if (!ValidText(word, policy.Limits.WordMaxLength))
                {
                    errorCode = "TopicWorkshopInvalidWords";
                    return false;
                }
                string key = new string(word.Normalize(NormalizationForm.FormKC)
                    .Where(character => !char.IsWhiteSpace(character)).ToArray()).ToLowerInvariant();
                if (distinct.Add(key)) words.Add(word);
            }
            normalized = new TopicWorkshopPublishRequest { Name = name, LanguageCode = language, Words = words.ToArray() };
            return true;
        }

        private static bool ValidText(string value, int limit) => value.Length > 0 && TextLength(value) <= limit
            && !value.Any(character => char.IsControl(character) || character == '<' || character == '>')
            && HasValidSurrogates(value);

        private static bool HasValidSurrogates(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]))
                {
                    if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
                }
                else if (char.IsLowSurrogate(value[index])) return false;
            }
            return true;
        }
    }
}
