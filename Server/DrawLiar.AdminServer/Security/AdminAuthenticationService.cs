using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace DrawLiar.Admin.Security;

public sealed class AdminAuthenticationService
{
    private readonly IReadOnlyDictionary<string, AdminUser> _users;
    private readonly PasswordHasher<AdminUser> _passwordHasher = new();
    private readonly AdminUser _dummyUser = new();
    private readonly string _dummyHash;

    public AdminAuthenticationService(AdminOptions options)
    {
        if (!File.Exists(options.UsersFile)) throw new InvalidOperationException("Admin:UsersFile에 관리자 계정 해시 파일을 설정하세요.");
        AdminUsersDocument document;
        try
        {
            document = JsonSerializer.Deserialize<AdminUsersDocument>(File.ReadAllText(options.UsersFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("관리자 계정 설정을 확인하세요.");
        }
        catch (JsonException) { throw new InvalidOperationException("관리자 계정 설정 형식을 확인하세요."); }
        var users = new Dictionary<string, AdminUser>(StringComparer.OrdinalIgnoreCase);
        if (document.Users == null) throw new InvalidOperationException("관리자 계정 설정을 확인하세요.");
        foreach (var user in document.Users)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Username) || user.Username.Length > 100 || user.Username != user.Username.Trim()
                || !IsValidPasswordHash(user.PasswordHash) || !users.TryAdd(user.Username, user))
                throw new InvalidOperationException("관리자 계정명과 비밀번호 해시 설정을 확인하세요.");
            try
            {
                _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, Guid.NewGuid().ToString("N"));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException)
            {
                throw new InvalidOperationException("관리자 비밀번호 해시 형식을 확인하세요.");
            }
        }
        if (users.Count == 0) throw new InvalidOperationException("관리자 계정을 한 개 이상 설정하세요.");
        _users = users;
        _dummyHash = _passwordHasher.HashPassword(_dummyUser, Guid.NewGuid().ToString("N"));
    }

    public string? ValidateCredentials(string username, string password)
    {
        if (password.Length > 256 || username.Length > 100) return null;
        if (!_users.TryGetValue(username, out var user))
        {
            _passwordHasher.VerifyHashedPassword(_dummyUser, _dummyHash, password);
            return null;
        }
        try
        {
            return _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed ? null : user.Username;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException) { return null; }
    }

    private static bool IsValidPasswordHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length > 2048) return false;
        Span<byte> decoded = stackalloc byte[1536];
        if (!Convert.TryFromBase64String(hash, decoded, out int length) || length < 45 || decoded[0] != 1) return false;
        uint algorithm = BinaryPrimitives.ReadUInt32BigEndian(decoded[1..5]);
        uint iterations = BinaryPrimitives.ReadUInt32BigEndian(decoded[5..9]);
        uint saltLength = BinaryPrimitives.ReadUInt32BigEndian(decoded[9..13]);
        return algorithm is 1 or 2 && iterations is >= 100_000 and <= 1_000_000 && saltLength >= 16 && saltLength <= length - 29;
    }
}
