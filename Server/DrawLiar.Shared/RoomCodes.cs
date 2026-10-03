using System.Security.Cryptography;

namespace DrawLiar.Server;

public static class RoomCodes
{
    public const string ALPHABET = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int LENGTH = 6;

    public static string Create()
    {
        Span<char> code = stackalloc char[LENGTH];
        for (int index = 0; index < code.Length; index++) code[index] = ALPHABET[RandomNumberGenerator.GetInt32(ALPHABET.Length)];
        return new string(code);
    }

    public static string Normalize(string? value)
    {
        if (value == null || value.Length > 64) throw new ApiException("InvalidRoomCode");
        string code = string.Concat(value.Where(character => !char.IsWhiteSpace(character) && character != '-')).ToUpperInvariant();
        if (code.Length != LENGTH || code.Any(character => !ALPHABET.Contains(character))) throw new ApiException("InvalidRoomCode");
        return code;
    }
}
