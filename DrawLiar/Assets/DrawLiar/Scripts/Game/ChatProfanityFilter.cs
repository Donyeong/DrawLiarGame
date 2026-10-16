#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DrawLiar
{
    public static class ChatProfanityFilter
    {
        private const string INITIALS = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        private const string VOWELS = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        private const string FINALS = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

        private struct Character
        {
            public char Value;
            public int Start, End;
            public Character(char value, int start, int end) { Value = value; Start = start; End = end; }
        }

        private sealed class Rule
        {
            public string Value = "";
            public bool WholeWord, HasExceptions;
        }

        private static readonly Dictionary<char, List<Rule>> KOREAN_RULES = CreateRules(false, new[]
        {
            "시발", "씨발", "시팔", "씨팔", "씨빨", "시벌", "씨벌", "씨바", "쉬발", "쉬벌", "쉬팔", "쒸발", "쒸벌", "쒸팔",
            "병신", "븅신", "개새끼", "개새키", "개새기", "개색끼", "개색기", "개세끼", "개같은", "개년", "썅년", "쌍년",
            "좆", "좃", "존나", "좇같", "지랄", "염병", "엿먹어", "ㅅㅂ", "ㅆㅂ", "ㅂㅅ", "ㅈㄹ"
        });
        private static readonly Dictionary<char, List<Rule>> ENGLISH_RULES = CreateRules(true, new[]
        {
            "fuck", "fucks", "fucked", "fucker", "fuckers", "fucking", "motherfucker", "motherfuckers", "motherfucking",
            "shit", "shits", "shitty", "bullshit", "bitch", "bitches", "bitching", "asshole", "assholes", "bastard", "bastards",
            "cunt", "cunts", "dickhead", "dickheads"
        });
        private static readonly string[] KOREAN_EXCEPTIONS = { Canonical("시발점", false), Canonical("시발역", false), Canonical("시발열차", false) };

        public static string Mask(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            while (true)
            {
                char[]? masked = null;
                MaskMatches(text, Normalize(text, false), KOREAN_RULES, false, ref masked);
                MaskMatches(text, Normalize(text, true), ENGLISH_RULES, true, ref masked);
                if (masked == null) return text;
                text = new string(masked);
            }
        }

        private static void MaskMatches(string text, List<Character> characters, Dictionary<char, List<Rule>> rules, bool english, ref char[]? masked)
        {
            for (int index = 0; index < characters.Count; index++)
            {
                if (!rules.TryGetValue(characters[index].Value, out var candidates)) continue;
                foreach (var rule in candidates)
                {
                    if (!Matches(characters, index, rule.Value)) continue;
                    int last = index + rule.Value.Length - 1;
                    if (index > 0 && characters[index - 1].Start == characters[index].Start
                        || last + 1 < characters.Count && characters[last + 1].Start == characters[last].Start) continue;
                    char lastSource = text[characters[last].Start];
                    if (!english && !(lastSource >= '가' && lastSource <= '힣') && !(lastSource >= '\u11A8' && lastSource <= '\u11C2')
                        && last + 1 < characters.Count && characters[last].Value >= 'ㄱ' && characters[last].Value <= 'ㅎ'
                        && characters[last + 1].Value >= 'ㅏ' && characters[last + 1].Value <= 'ㅣ') continue;
                    if (rule.WholeWord && (HasWordNeighbour(text, characters, index, -1, english)
                        || HasWordNeighbour(text, characters, last, 1, english))) continue;
                    if (rule.HasExceptions && IsException(text, characters, index)) continue;
                    if (masked == null) masked = text.ToCharArray();
                    for (int position = characters[index].Start; position < characters[last].End; position++) masked[position] = '*';
                }
            }
        }

        private static bool Matches(List<Character> characters, int start, string word)
        {
            if (start + word.Length > characters.Count) return false;
            for (int offset = 0; offset < word.Length; offset++)
                if (characters[start + offset].Value != word[offset]) return false;
            return true;
        }

        private static bool IsException(string text, List<Character> characters, int start)
        {
            foreach (string word in KOREAN_EXCEPTIONS)
            {
                if (!Matches(characters, start, word)) continue;
                bool joined = true;
                for (int index = start + 1; index < start + word.Length; index++)
                    joined &= AreJoined(text, characters[index - 1].End, characters[index].Start);
                if (joined) return true;
            }
            return false;
        }

        private static bool HasWordNeighbour(string text, List<Character> characters, int index, int direction, bool english)
        {
            int neighbour = index + direction;
            if (neighbour < 0 || neighbour >= characters.Count) return false;
            var adjacent = characters[neighbour];
            if (!char.IsLetterOrDigit(text, adjacent.Start)
                || english && !(adjacent.Value >= 'a' && adjacent.Value <= 'z' || char.IsDigit(adjacent.Value))) return false;
            int start = direction < 0 ? adjacent.End : characters[index].End;
            int end = direction < 0 ? characters[index].Start : adjacent.Start;
            return AreJoined(text, start, end);
        }

        private static bool AreJoined(string text, int start, int end)
        {
            for (int position = start; position < end; position++)
            {
                var category = char.GetUnicodeCategory(text, position);
                if (category != UnicodeCategory.Format && category != UnicodeCategory.NonSpacingMark
                    && category != UnicodeCategory.EnclosingMark) return false;
            }
            return true;
        }

        private static Dictionary<char, List<Rule>> CreateRules(bool english, string[] words)
        {
            var result = new Dictionary<char, List<Rule>>();
            foreach (string word in words)
            {
                string value = Canonical(word, english);
                if (!result.TryGetValue(value[0], out var rules)) result[value[0]] = rules = new List<Rule>();
                bool initials = true;
                foreach (char character in word) initials &= character >= 'ㄱ' && character <= 'ㅎ';
                rules.Add(new Rule { Value = value, WholeWord = english || initials, HasExceptions = word == "시발" });
            }
            return result;
        }

        private static string Canonical(string text, bool english)
        {
            var characters = Normalize(text, english);
            var result = new StringBuilder(characters.Count);
            foreach (var character in characters) result.Append(character.Value);
            return result.ToString();
        }

        private static List<Character> Normalize(string text, bool english)
        {
            var result = new List<Character>(text.Length * 3);
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (char.IsSurrogate(character))
                {
                    if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                    {
                        int codePoint = char.ConvertToUtf32(text, index);
                        // Mono의 보조 평면 수학 라틴 문자 정규화를 보완한다.
                        if (codePoint >= 0x1D400 && codePoint <= 0x1D6A3)
                            Append(result, (char)('a' + (codePoint - 0x1D400) % 26), index, index + 2, english);
                        else
                            foreach (char normalized in text.Substring(index, 2).Normalize(NormalizationForm.FormKD))
                                Append(result, normalized, index, index + 2, english);
                        index++;
                    }
                    continue;
                }
                // 기존 마스크를 넘어 새 단어를 조합하지 않는다.
                if (character == '*') { result.Add(new Character('\0', index, index + 1)); continue; }
                if (character <= 127 || character >= '가' && character <= '힣' || character >= 'ㄱ' && character <= 'ㅣ'
                    || character >= '\u1100' && character <= '\u11FF')
                    Append(result, character, index, index + 1, english);
                else
                    foreach (char normalized in character.ToString().Normalize(NormalizationForm.FormKD)) Append(result, normalized, index, index + 1, english);
            }
            return result;
        }

        private static void Append(List<Character> result, char value, int index, int end, bool english)
        {
            if (value >= '가' && value <= '힣')
            {
                int syllable = value - '가';
                result.Add(new Character(INITIALS[syllable / 588], index, end));
                result.Add(new Character(VOWELS[syllable / 28 % 21], index, end));
                if (syllable % 28 != 0) result.Add(new Character(FINALS[syllable % 28], index, end));
                return;
            }
            if (value >= '\u1100' && value <= '\u1112') value = INITIALS[value - '\u1100'];
            else if (value >= '\u1161' && value <= '\u1175') value = VOWELS[value - '\u1161'];
            else if (value >= '\u11A8' && value <= '\u11C2') value = FINALS[value - '\u11A7'];
            else value = char.ToLowerInvariant(value);
            if (english)
            {
                switch (value)
                {
                    case '0': value = 'o'; break;
                    case '1': case '!': value = 'i'; break;
                    case '3': value = 'e'; break;
                    case '4': case '@': value = 'a'; break;
                    case '5': case '$': value = 's'; break;
                    case '7': value = 't'; break;
                }
            }
            if (char.IsLetterOrDigit(value)) result.Add(new Character(value, index, end));
        }
    }
}
