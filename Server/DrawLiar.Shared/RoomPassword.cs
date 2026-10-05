using System.Security.Cryptography;

namespace DrawLiar.Server;

public static class RoomPassword
{
    private const int ITERATIONS = 210_000;

    public static void Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ApiException("RoomPasswordRequired", 403);
        if (password.Length is < 4 or > 32 || password.Any(char.IsControl)) throw new ApiException("InvalidRoomPasswordFormat");
    }

    public static string Hash(string password)
    {
        Validate(password);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, ITERATIONS, HashAlgorithmName.SHA256, 32);
        return $"{ITERATIONS}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? password, string? stored)
    {
        if (password == null || password.Length is < 4 or > 32 || password.Any(char.IsControl) || stored == null || stored.Length > 128) return false;
        string[] parts = stored.Split(':');
        if (parts.Length != 3 || parts[0] != ITERATIONS.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, ITERATIONS, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
