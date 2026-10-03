using System.Security.Cryptography;

namespace DrawLiar.Server;

public static class GuestCredential
{
    public const int BYTE_LENGTH = 32;
    public const int ENCODED_LENGTH = 43;
    public const int HASH_LENGTH = 64;

    public static bool IsValid(string secret)
    {
        Span<byte> bytes = stackalloc byte[BYTE_LENGTH];
        try { return TryDecode(secret, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Hash(string secret)
    {
        Span<byte> bytes = stackalloc byte[BYTE_LENGTH];
        Span<byte> hash = stackalloc byte[BYTE_LENGTH];
        try
        {
            if (!TryDecode(secret, bytes)) throw new ApiException("InvalidGuestCredential", 401);
            SHA256.HashData(bytes, hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool Verify(string expectedHash, string secret)
    {
        if (expectedHash == null || expectedHash.Length != HASH_LENGTH) return false;
        Span<byte> expected = stackalloc byte[BYTE_LENGTH];
        Span<byte> bytes = stackalloc byte[BYTE_LENGTH];
        Span<byte> actual = stackalloc byte[BYTE_LENGTH];
        try
        {
            if (!TryDecode(secret, bytes)) return false;
            for (int index = 0; index < BYTE_LENGTH; index++)
            {
                int high = HexValue(expectedHash[index * 2]);
                int low = HexValue(expectedHash[index * 2 + 1]);
                if (high < 0 || low < 0) return false;
                expected[index] = (byte)((high << 4) | low);
            }
            SHA256.HashData(bytes, actual);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static bool TryDecode(string secret, Span<byte> bytes)
    {
        if (secret == null || secret.Length != ENCODED_LENGTH) return false;
        Span<char> encoded = stackalloc char[ENCODED_LENGTH + 1];
        try
        {
            for (int index = 0; index < ENCODED_LENGTH; index++)
            {
                char value = secret[index];
                if (value == '-') encoded[index] = '+';
                else if (value == '_') encoded[index] = '/';
                else if (char.IsAsciiLetterOrDigit(value)) encoded[index] = value;
                else return false;
            }
            encoded[ENCODED_LENGTH] = '=';
            if (!Convert.TryFromBase64Chars(encoded, bytes, out int written) || written != BYTE_LENGTH) return false;
            return secret[^1] is 'A' or 'E' or 'I' or 'M' or 'Q' or 'U' or 'Y' or 'c' or 'g' or 'k' or 'o' or 's' or 'w' or '0' or '4' or '8';
        }
        finally { encoded.Clear(); }
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };
}
