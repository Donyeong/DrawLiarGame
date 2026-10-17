namespace DrawLiar.Admin.Security;

public sealed class AdminOptions
{
    public string UsersFile { get; set; } = "../../Private/admin-users.json";
    public string DataProtectionKeysPath { get; set; } = "../../Private/admin-data-protection";

    public void ResolvePaths(string contentRoot)
    {
        UsersFile = Path.GetFullPath(UsersFile, contentRoot);
        DataProtectionKeysPath = Path.GetFullPath(DataProtectionKeysPath, contentRoot);
        string publicRoot = Path.Combine(Path.GetFullPath(contentRoot), "wwwroot");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (UsersFile.Equals(publicRoot, comparison) || UsersFile.StartsWith(publicRoot + Path.DirectorySeparatorChar, comparison)
            || DataProtectionKeysPath.Equals(publicRoot, comparison) || DataProtectionKeysPath.StartsWith(publicRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("관리자 인증 파일과 데이터 보호 키는 wwwroot 밖에 보관합니다.");
    }
}

public sealed class AdminUser
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class AdminUsersDocument
{
    public List<AdminUser> Users { get; set; } = [];
}
