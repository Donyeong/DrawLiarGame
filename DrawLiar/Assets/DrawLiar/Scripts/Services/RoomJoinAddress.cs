using System;
using System.Linq;

namespace DrawLiar
{
    public static class RoomJoinAddress
    {
        public const string DEFAULT_PAGE_URL = "https://rascallab.com/games/liar-canvas";
        private const string GAME_PATH = "/games/liar-canvas";
        private const string ALPHABET = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const string ADDRESS_ERROR = "방 코드 또는 주소를 확인하세요.";
        private const int MAX_ADDRESS_LENGTH = 2048;

        public static string NormalizeCode(string value, string currentPageUrl = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string input = value.Trim();
            if (Uri.TryCreate(input, UriKind.Absolute, out var address))
            {
                if (value.Length > MAX_ADDRESS_LENGTH || !IsGamePage(address) || !IsAllowedHost(address, currentPageUrl))
                    throw InvalidAddress();
                ValidateEscapes(input);
                string code = null;
                foreach (string parameter in address.Query.TrimStart('?').Split('&'))
                {
                    int separator = parameter.IndexOf('=');
                    string key = Decode(separator < 0 ? parameter : parameter.Substring(0, separator));
                    if (!string.Equals(key, "room", StringComparison.Ordinal))
                    {
                        if (string.Equals(key, "room", StringComparison.OrdinalIgnoreCase)) throw InvalidAddress();
                        continue;
                    }
                    if (code != null || separator < 0) throw InvalidAddress();
                    string decoded = Decode(parameter.Substring(separator + 1));
                    try { code = NormalizePlainCode(decoded); }
                    catch (InvalidOperationException) { throw InvalidAddress(); }
                    if (code.Length == 0) throw InvalidAddress();
                }
                return code ?? throw InvalidAddress();
            }
            if (input.IndexOfAny(new[] { '/', '\\', '?', '#', ':' }) >= 0) throw InvalidAddress();
            return NormalizePlainCode(value);
        }

        public static string Create(string pageUrl, string roomCode)
        {
            if (string.IsNullOrEmpty(pageUrl) || pageUrl.Length > MAX_ADDRESS_LENGTH
                || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !IsGamePage(page))
                throw InvalidAddress();
            ValidateEscapes(pageUrl);
            string code;
            try { code = NormalizePlainCode(roomCode); }
            catch (InvalidOperationException) { throw InvalidAddress(); }
            if (code.Length == 0) throw InvalidAddress();
            return new UriBuilder(page)
            {
                Path = GAME_PATH, Query = "room=" + Uri.EscapeDataString(code), Fragment = ""
            }.Uri.AbsoluteUri;
        }

        private static string NormalizePlainCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (value.Length > 64) throw new InvalidOperationException("6자리 방 코드를 입력하세요.");
            if (Guid.TryParse(value.Trim(), out Guid legacyId)) return legacyId.ToString();
            string code = new string(value.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray()).ToUpperInvariant();
            if (code.Length != 6 || code.Any(character => ALPHABET.IndexOf(character) < 0))
                throw new InvalidOperationException("영문·숫자 6자리 방 코드를 확인하세요.");
            return code;
        }

        private static bool IsGamePage(Uri address)
        {
            if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
                return false;
            string original = address.OriginalString;
            int authorityStart = original.IndexOf("://", StringComparison.Ordinal);
            if (authorityStart < 0 || original.IndexOf('\\') >= 0) return false;
            authorityStart += 3;
            int authorityEnd = original.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
            if (authorityEnd < 0 || original.Substring(authorityStart, authorityEnd - authorityStart).IndexOf('@') >= 0)
                return false;
            int pathEnd = original.IndexOfAny(new[] { '?', '#' }, authorityEnd);
            string path = pathEnd < 0 ? original.Substring(authorityEnd) : original.Substring(authorityEnd, pathEnd - authorityEnd);
            return path == GAME_PATH || path == GAME_PATH + "/" || path == GAME_PATH + ".html" || path == GAME_PATH + ".html/";
        }

        private static bool IsAllowedHost(Uri address, string currentPageUrl)
        {
            string host = address.Host;
            if (string.Equals(host, "rascallab.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "www.rascallab.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1" || host == "[::1]" || host == "::1") return true;
            return Uri.TryCreate(currentPageUrl, UriKind.Absolute, out var current) && IsGamePage(current)
                && address.Scheme == current.Scheme && string.Equals(host, current.Host, StringComparison.OrdinalIgnoreCase)
                && address.Port == current.Port;
        }

        private static string Decode(string value)
        {
            ValidateEscapes(value);
            try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
            catch (UriFormatException) { throw InvalidAddress(); }
        }

        private static void ValidateEscapes(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] != '%') continue;
                if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])) throw InvalidAddress();
                index += 2;
            }
        }

        private static bool IsHex(char value) => value >= '0' && value <= '9' || value >= 'A' && value <= 'F' || value >= 'a' && value <= 'f';
        private static InvalidOperationException InvalidAddress() => new InvalidOperationException(ADDRESS_ERROR);
    }
}
