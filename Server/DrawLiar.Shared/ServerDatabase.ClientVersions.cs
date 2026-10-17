namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    public async Task<ClientVersionPolicy[]> AdminClientVersionsAsync()
    {
        var policies = ClientVersionRules.PLATFORMS.Select(platform => new ClientVersionPolicy { Platform = platform }).ToArray();
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null,
            "SELECT \"Platform\",\"MinimumVersion\",\"LatestVersion\",\"StoreUrl\" FROM \"ClientVersionPolicy\"");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var policy = Array.Find(policies, item => item.Platform == reader.GetString(0));
            if (policy == null) continue;
            policy.MinimumVersion = reader.GetString(1);
            policy.LatestVersion = reader.GetString(2);
            policy.StoreUrl = reader.GetString(3);
        }
        return policies;
    }

    public async Task AdminSetClientVersionAsync(string platform, ClientVersionPolicy policy)
    {
        if (!ClientVersionRules.PLATFORMS.Contains(platform, StringComparer.Ordinal) || policy == null)
            throw new ApiException("InvalidClientVersion");
        string minimum = policy.MinimumVersion ?? "";
        string latest = policy.LatestVersion ?? "";
        string storeUrl = policy.StoreUrl ?? "";
        if (minimum.Length > 0 && !ClientVersionRules.TryParse(minimum, out _)
            || latest.Length > 0 && !ClientVersionRules.TryParse(latest, out _)
            || minimum.Length > 0 && latest.Length > 0 && ClientVersionRules.IsOlder(latest, minimum)
            || storeUrl.Length > 512
            || storeUrl.Length > 0 && (!Uri.TryCreate(storeUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new ApiException("InvalidClientVersion");

        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null, """
            INSERT INTO "ClientVersionPolicy" ("Platform","MinimumVersion","LatestVersion","StoreUrl") VALUES ($1,$2,$3,$4)
            ON CONFLICT ("Platform") DO UPDATE SET "MinimumVersion"=EXCLUDED."MinimumVersion",
                "LatestVersion"=EXCLUDED."LatestVersion","StoreUrl"=EXCLUDED."StoreUrl","UpdatedAt"=now()
            """, platform, minimum, latest, storeUrl);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<ClientVersionResponse> ClientVersionAsync(string platform, string version)
    {
        var response = new ClientVersionResponse();
        if (!ClientVersionRules.PLATFORMS.Contains(platform, StringComparer.Ordinal)) return response;
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null,
            "SELECT \"MinimumVersion\",\"LatestVersion\",\"StoreUrl\" FROM \"ClientVersionPolicy\" WHERE \"Platform\"=$1", platform);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return response;
        response.UpdateRequired = ClientVersionRules.IsOlder(version, reader.GetString(0));
        response.LatestVersion = reader.GetString(1);
        response.UpdateAvailable = response.UpdateRequired || ClientVersionRules.IsOlder(version, response.LatestVersion);
        response.StoreUrl = reader.GetString(2);
        return response;
    }
}
