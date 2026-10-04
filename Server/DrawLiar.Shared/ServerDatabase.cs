using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DrawLiar.Server;

public sealed record ServerSession(Guid AccountId, string Hash, string Scope, DateTimeOffset ExpiresAt);
public sealed record IssuedSession(string Token, DateTimeOffset ExpiresAt);
public sealed record StoredChallenge(string Nonce, string ClientId, string Platform, Guid? AccountId);

public sealed partial class ServerDatabase : IDisposable
{
    private readonly NpgsqlDataSource _source;
    private static readonly string _dummyPassword = ServerRuntime.PasswordHash("invalid-account-password");

    public ServerDatabase(IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("DrawLiarDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:DrawLiarDatabase가 필요합니다.");
        _source = NpgsqlDataSource.Create(connectionString);
    }

    public async Task InitializeAsync()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(647291930);");
        await Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS \"SchemaVersion\" (\"Version\" integer PRIMARY KEY, \"AppliedAt\" timestamptz NOT NULL DEFAULT now());");
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order())
        {
            int migration = int.Parse(resource.Split('.')[^2].Split('_')[0]);
            object? version = await Scalar(connection, transaction, "SELECT \"Version\" FROM \"SchemaVersion\" WHERE \"Version\"=$1", migration);
            if (version != null) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            await Execute(connection, transaction, await reader.ReadToEndAsync());
            await Execute(connection, transaction, "INSERT INTO \"SchemaVersion\" (\"Version\") VALUES ($1)", migration);
        }
        await transaction.CommitAsync();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (object? value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static async Task<int> Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteScalarAsync();
    }

    public async Task<Guid> DevelopmentAccountAsync(string name)
    {
        Guid id = Guid.NewGuid();
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "INSERT INTO \"Account\" (\"Id\",\"DisplayName\") VALUES ($1,$2)", id, ServerRuntime.DisplayName(name));
        return id;
    }

    public async Task<Guid> LoginAsync(LoginRequest request)
    {
        string email = ServerRuntime.Email(request.Email);
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null, "SELECT \"Id\",\"PasswordHash\",\"IsBanned\" FROM \"Account\" WHERE \"Email\"=$1", email);
        await using var reader = await command.ExecuteReaderAsync();
        bool found = await reader.ReadAsync();
        string stored = found && !reader.IsDBNull(1) ? reader.GetString(1) : _dummyPassword;
        bool valid = ServerRuntime.VerifyPassword(request.Password, stored);
        if (!found || !valid || reader.GetBoolean(2)) throw new ApiException("InvalidCredentials", 401);
        return reader.GetGuid(0);
    }

    public async Task<IssuedSession> IssueSessionAsync(Guid accountId, string scope, string? parentHash = null, DateTimeOffset? expiresAt = null)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var session = await IssueSession(connection, transaction, accountId, scope, parentHash, expiresAt ?? DateTimeOffset.UtcNow.AddHours(12));
        await transaction.CommitAsync();
        return session;
    }

    private static async Task<IssuedSession> IssueSession(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, string scope, string? parentHash, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await using (var account = Command(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", accountId))
            if (await account.ExecuteScalarAsync(cancellationToken) == null) throw new ApiException("AccountUnavailable", 403);
        await using (var expired = Command(connection, transaction, "DELETE FROM \"Session\" WHERE \"AccountId\"=$1 AND \"ExpiresAt\" < now()", accountId))
            await expired.ExecuteNonQueryAsync(cancellationToken);
        await using var countCommand = Command(connection, transaction, "SELECT count(*) FROM \"Session\" WHERE \"AccountId\"=$1", accountId);
        var count = await countCommand.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt64(count) >= 32) throw new ApiException("SessionLimit", 429);
        string token = ServerRuntime.NewToken();
        await using var insert = Command(connection, transaction, "INSERT INTO \"Session\" (\"Hash\",\"AccountId\",\"Scope\",\"ExpiresAt\",\"ParentHash\") VALUES ($1,$2,$3,$4,$5)",
            ServerRuntime.Hash(token), accountId, scope, expiresAt, parentHash);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new IssuedSession(token, expiresAt);
    }

    public async Task<ServerSession> AuthenticateAsync(string token, string scope)
    {
        await using var connection = await _source.OpenConnectionAsync();
        return await AuthenticateHash(connection, null, ServerRuntime.Hash(token), scope);
    }

    private static async Task<ServerSession> AuthenticateHash(NpgsqlConnection connection, NpgsqlTransaction? transaction, string hash, string scope,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s."AccountId",s."Scope",s."ExpiresAt" FROM "Session" s JOIN "Account" a ON a."Id"=s."AccountId"
            WHERE s."Hash"=$1 AND s."Scope"=$2 AND s."ExpiresAt">now() AND NOT a."IsBanned"
            AND NOT EXISTS (WITH RECURSIVE parents AS (SELECT p.* FROM "Session" p WHERE p."Hash"=s."ParentHash"
              UNION ALL SELECT p.* FROM "Session" p JOIN parents c ON c."ParentHash"=p."Hash")
              SELECT 1 FROM parents WHERE "ExpiresAt"<=now())
            """;
        await using var command = Command(connection, transaction, sql, hash, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ApiException("Unauthorized", 401);
        return new ServerSession(reader.GetGuid(0), hash, reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<(ServerSession Session, string DisplayName)> AuthenticateLobbyAsync(string token, string scope, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var session = await AuthenticateHash(connection, null, ServerRuntime.Hash(token), scope, cancellationToken);
        await using var command = Command(connection, null, "SELECT \"DisplayName\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\"", session.AccountId);
        string name = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new ApiException("Unauthorized", 401);
        return (session, name);
    }

    public async Task LogoutAsync(ServerSession session)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "DELETE FROM \"Session\" WHERE \"Hash\"=$1", session.Hash);
    }

    public async Task<ProfileData> ProfileAsync(Guid id)
    {
        await using var connection = await _source.OpenConnectionAsync();
        return await ReadProfile(connection, null, id);
    }

    private static async Task<ProfileData> ReadProfile(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id)
    {
        ProfileData profile;
        const string sql = """
            SELECT a."DisplayName",a."AvatarColor",a."Accessory",a."Coins",
                a."GuestId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "ExternalIdentity" e WHERE e."AccountId"=a."Id"),
                EXISTS (SELECT 1 FROM "ExternalIdentity" e WHERE e."AccountId"=a."Id" AND e."Provider"='google')
            FROM "Account" a WHERE a."Id"=$1 AND NOT a."IsBanned"
            """;
        await using (var command = Command(connection, transaction, sql, id))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("AccountUnavailable", 403);
            profile = new ProfileData
            {
                AccountId = id.ToString(), DisplayName = reader.GetString(0), AvatarColor = reader.GetInt32(1),
                Accessory = reader.GetInt32(2), Coins = reader.GetInt32(3), IsGuest = reader.GetBoolean(4), HasGoogleAccount = reader.GetBoolean(5)
            };
        }
        var accessories = new List<int> { 0 };
        await using var owned = Command(connection, transaction, "SELECT \"Accessory\" FROM \"OwnedAccessory\" WHERE \"AccountId\"=$1 ORDER BY \"Accessory\"", id);
        await using var ownedReader = await owned.ExecuteReaderAsync();
        while (await ownedReader.ReadAsync()) accessories.Add(ownedReader.GetInt32(0));
        profile.OwnedAccessories = accessories.ToArray();
        return profile;
    }

    public async Task<ProfileData> UpdateProfileAsync(Guid id, UpdateProfileRequest request)
    {
        string name = ServerRuntime.DisplayName(request.DisplayName);
        if (request.AvatarColor < 0 || request.AvatarColor > 5 || !AvatarParts.IsValid(request.Accessory)) throw new ApiException("InvalidAvatar");
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (request.Accessory != 0)
        {
            int ownedMask = Convert.ToInt32(await Scalar(connection, transaction, "SELECT COALESCE(bit_or(\"Accessory\"),0) FROM \"OwnedAccessory\" WHERE \"AccountId\"=$1", id));
            if ((ownedMask & request.Accessory) != request.Accessory) throw new ApiException("AccessoryNotOwned", 403);
        }
        await Execute(connection, transaction, "UPDATE \"Account\" SET \"DisplayName\"=$2,\"AvatarColor\"=$3,\"Accessory\"=$4 WHERE \"Id\"=$1", id, name, request.AvatarColor, request.Accessory);
        var profile = await ReadProfile(connection, transaction, id);
        await transaction.CommitAsync();
        return profile;
    }

    public async Task<GoogleChallengeResponse> CreateChallengeAsync(string clientId, string platform, Guid? accountId)
    {
        if (string.IsNullOrEmpty(clientId)) throw new ApiException("GoogleUnavailable", 503);
        var response = new GoogleChallengeResponse { ChallengeId = ServerRuntime.NewToken(), Nonce = ServerRuntime.NewToken(), ClientId = clientId, ExpiresAt = ServerRuntime.Timestamp(DateTimeOffset.UtcNow.AddMinutes(5)) };
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "DELETE FROM \"AuthChallenge\" WHERE \"ExpiresAt\" < now()");
        await Execute(connection, null, "INSERT INTO \"AuthChallenge\" (\"Id\",\"Nonce\",\"ClientId\",\"Platform\",\"AccountId\",\"ExpiresAt\") VALUES ($1,$2,$3,$4,$5,$6)",
            response.ChallengeId, response.Nonce, clientId, platform, accountId, DateTimeOffset.Parse(response.ExpiresAt));
        return response;
    }

    public async Task<StoredChallenge> ConsumeChallengeAsync(string id, Guid? accountId)
    {
        if (id == null || id.Length != 64) throw new ApiException("InvalidGoogleCredential", 401);
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null, "DELETE FROM \"AuthChallenge\" WHERE \"Id\"=$1 AND \"ExpiresAt\">now() RETURNING \"Nonce\",\"ClientId\",\"Platform\",\"AccountId\"", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new ApiException("InvalidGoogleCredential", 401);
        Guid? storedId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        if (storedId != accountId) throw new ApiException("InvalidGoogleCredential", 401);
        return new StoredChallenge(reader.GetString(0), reader.GetString(1), reader.GetString(2), storedId);
    }

    public async Task<Guid> GoogleAccountAsync(string subject, string displayName, Guid? linkAccountId)
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1,0))", "google:" + subject);
        object? existing = await Scalar(connection, transaction, "SELECT \"AccountId\" FROM \"ExternalIdentity\" WHERE \"Provider\"='google' AND \"Subject\"=$1", subject);
        Guid id;
        if (existing is Guid found)
        {
            if (linkAccountId.HasValue && found != linkAccountId.Value) throw new ApiException("GoogleAlreadyLinked", 409);
            id = found;
        }
        else
        {
            id = linkAccountId ?? Guid.NewGuid();
            if (!linkAccountId.HasValue)
                await Execute(connection, transaction, "INSERT INTO \"Account\" (\"Id\",\"DisplayName\") VALUES ($1,$2)", id, displayName);
            else
            {
                if (await Scalar(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 AND NOT \"IsBanned\" FOR UPDATE", id) == null)
                    throw new ApiException("AccountUnavailable", 403);
                if (await Scalar(connection, transaction, "SELECT 1 FROM \"ExternalIdentity\" WHERE \"Provider\"='google' AND \"AccountId\"=$1", id) != null)
                    throw new ApiException("GoogleAlreadyLinked", 409);
            }
            await Execute(connection, transaction, "INSERT INTO \"ExternalIdentity\" (\"Provider\",\"Subject\",\"AccountId\") VALUES ('google',$1,$2)", subject, id);
        }
        await ReadProfile(connection, transaction, id);
        await transaction.CommitAsync();
        return id;
    }

    public void Dispose() => _source.Dispose();
}
