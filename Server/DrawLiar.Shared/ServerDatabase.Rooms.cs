using System.Text.Json;
using Npgsql;

namespace DrawLiar.Server;

public sealed partial class ServerDatabase
{
    private static readonly Lazy<HashSet<string>> _builtInTopicNames = new(() =>
    {
        using var stream = typeof(ServerDatabase).Assembly.GetManifestResourceStream("DrawLiar.BuiltInGameData.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("Topics").EnumerateArray().Select(topic => topic.GetProperty("Name").GetString()!).ToHashSet(StringComparer.Ordinal);
    });
    public async Task RegisterGameAsync(RegisterGameRequest request, bool allowHttp)
    {
        ValidateNode(request.NodeId, request.PublicUrl, false, allowHttp);
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "INSERT INTO \"GameNode\" (\"NodeId\",\"PublicUrl\",\"HeartbeatAt\") VALUES ($1,$2,now()) ON CONFLICT (\"NodeId\") DO UPDATE SET \"PublicUrl\"=$2,\"HeartbeatAt\"=now()", request.NodeId, request.PublicUrl.TrimEnd('/'));
    }

    public async Task<LoginResponse> AssignGameAsync(ServerSession mainSession, string mainToken)
    {
        await using var connection = await _source.OpenConnectionAsync();
        string nodeId;
        string publicUrl;
        await using (var command = Command(connection, null, "SELECT \"NodeId\",\"PublicUrl\" FROM \"GameNode\" WHERE \"HeartbeatAt\">now()-interval '30 seconds' ORDER BY random() LIMIT 1"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("GameServerUnavailable", 503);
            nodeId = reader.GetString(0);
            publicUrl = reader.GetString(1);
        }
        var assignment = await IssueSessionAsync(mainSession.AccountId, "assignment:" + nodeId, mainSession.Hash, DateTimeOffset.UtcNow.AddMinutes(2));
        return new LoginResponse { AccountId = mainSession.AccountId.ToString(), SessionToken = mainToken, ExpiresAt = ServerRuntime.Timestamp(mainSession.ExpiresAt), GameServerUrl = publicUrl, AssignmentToken = assignment.Token, Profile = await ProfileAsync(mainSession.AccountId) };
    }

    public async Task<GameSessionResponse> EnterGameAsync(string assignmentToken, string nodeId)
    {
        if (assignmentToken == null || assignmentToken.Length != 64) throw new ApiException("InvalidAssignment", 401);
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        string hash = ServerRuntime.Hash(assignmentToken);
        await Execute(connection, transaction, "SELECT \"Hash\" FROM \"Session\" WHERE \"Hash\"=$1 FOR UPDATE", hash);
        var assignment = await AuthenticateHash(connection, transaction, hash, "assignment:" + nodeId);
        string parent;
        DateTimeOffset expiry;
        await using (var command = Command(connection, transaction, "SELECT p.\"Hash\",p.\"ExpiresAt\" FROM \"Session\" s JOIN \"Session\" p ON p.\"Hash\"=s.\"ParentHash\" WHERE s.\"Hash\"=$1", hash))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("InvalidAssignment", 401);
            parent = reader.GetString(0);
            expiry = reader.GetFieldValue<DateTimeOffset>(1);
        }
        await Execute(connection, transaction, "DELETE FROM \"Session\" WHERE \"Hash\"=$1", hash);
        await Execute(connection, transaction, "DELETE FROM \"Session\" WHERE \"AccountId\"=$1 AND \"Scope\"=$2", assignment.AccountId, "game:" + nodeId);
        var issued = await IssueSession(connection, transaction, assignment.AccountId, "game:" + nodeId, parent, expiry);
        var profile = await ReadProfile(connection, transaction, assignment.AccountId);
        await transaction.CommitAsync();
        return new GameSessionResponse { SessionToken = issued.Token, ExpiresAt = ServerRuntime.Timestamp(issued.ExpiresAt), Profile = profile };
    }

    public async Task RegisterDedicatedAsync(RegisterDedicatedRequest request, bool allowHttp)
    {
        ValidateNode(request.NodeId, request.PublicUrl, true, allowHttp);
        if (request.Capacity < 1 || request.Capacity > 1024) throw new ApiException("InvalidCapacity");
        await using var connection = await _source.OpenConnectionAsync();
        await Execute(connection, null, "INSERT INTO \"DedicatedNode\" (\"NodeId\",\"PublicUrl\",\"Capacity\",\"HeartbeatAt\") VALUES ($1,$2,$3,now()) ON CONFLICT (\"NodeId\") DO UPDATE SET \"PublicUrl\"=$2,\"Capacity\"=$3,\"HeartbeatAt\"=now()", request.NodeId, request.PublicUrl, request.Capacity);
    }

    private static void ValidateNode(string nodeId, string url, bool websocket, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 64 || nodeId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new ApiException("InvalidNode");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || (websocket ? uri.Scheme != "wss" && !(allowHttp && uri.Scheme == "ws") : uri.Scheme != "https" && !(allowHttp && uri.Scheme == "http")))
            throw new ApiException("InvalidPublicUrl");
    }

    public async Task<DedicatedHeartbeatResponse> DedicatedHeartbeatAsync(DedicatedHeartbeatRequest request)
    {
        if (request.Rooms == null || request.Rooms.Length > 1024) throw new ApiException("InvalidHeartbeat");
        var closed = new List<string>();
        var configurations = new List<RoomConfigurationData>();
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (await Execute(connection, transaction, "UPDATE \"DedicatedNode\" SET \"HeartbeatAt\"=now() WHERE \"NodeId\"=$1", request.NodeId) != 1)
            throw new ApiException("NodeNotRegistered", 409);
        foreach (var room in request.Rooms)
        {
            Guid roomId = ServerRuntime.AccountId(room.RoomId);
            if (room.PlayerCount < 0 || room.PlayerCount > 12 || room.SpectatorCount < 0 || room.SpectatorCount > 32
                || room.PlayerAccountIds == null || room.PlayerAccountIds.Length > 12 || room.SpectatorAccountIds == null || room.SpectatorAccountIds.Length > 32
                || room.AdmissionIds == null || room.AdmissionIds.Length > 128 || room.AdmissionIds.Any(id => id == null || id.Length != 64 || !id.All(Uri.IsHexDigit))
                || room.Closed && (room.PlayerCount != 0 || room.SpectatorCount != 0))
                throw new ApiException("InvalidHeartbeat");
            await Execute(connection, transaction, "SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 AND \"NodeId\"=$2 FOR UPDATE", roomId, request.NodeId);
            Guid owner = ServerRuntime.AccountId(room.OwnerAccountId);
            ValidateSettings(room.Settings);
            foreach (string id in room.PlayerAccountIds.Concat(room.SpectatorAccountIds)) ServerRuntime.AccountId(id);
            await Execute(connection, transaction, "DELETE FROM \"RoomAdmission\" WHERE \"RoomId\"=$1 AND \"Id\"=ANY($2)", roomId, room.AdmissionIds);
            await Execute(connection, transaction, """
                UPDATE "Room" SET "PlayerCount"=$3,"SpectatorCount"=$4,"IsInProgress"=$5,"OwnerAccountId"=$6,"UpdatedAt"=now(),
                "PlayerAccountIds"=$7::jsonb,"SpectatorAccountIds"=$8::jsonb,
                "ConfirmedPlayerAccountIds"=$7::jsonb,"ConfirmedSpectatorAccountIds"=$8::jsonb,
                "Settings"=CASE WHEN "ConfigurationVersion"=$11 THEN $9::jsonb || jsonb_build_object('IsPrivate',"Settings"->'IsPrivate') ELSE "Settings" END,
                "Established"="Established" OR $10 WHERE "RoomId"=$1 AND "NodeId"=$2
                """, roomId, request.NodeId, room.PlayerCount, room.SpectatorCount, room.IsInProgress, owner,
                JsonSerializer.Serialize(room.PlayerAccountIds), JsonSerializer.Serialize(room.SpectatorAccountIds), JsonSerializer.Serialize(room.Settings, ServerRuntime.Json), room.AdmissionIds.Length > 0 || room.PlayerCount + room.SpectatorCount > 0, room.ConfigurationVersion);
            await using (var configuration = Command(connection, transaction, "SELECT \"Settings\"::text,\"ConfigurationVersion\",\"AccessVersion\" FROM \"Room\" WHERE \"RoomId\"=$1 AND \"NodeId\"=$2 AND \"ConfigurationVersion\"<>$3", roomId, request.NodeId, room.ConfigurationVersion))
            await using (var reader = await configuration.ExecuteReaderAsync())
                if (await reader.ReadAsync()) configurations.Add(new RoomConfigurationData { RoomId = room.RoomId,
                    Settings = JsonSerializer.Deserialize<ServerRoomSettings>(reader.GetString(0), ServerRuntime.Json)!, Version = reader.GetInt64(1), AccessVersion = reader.GetInt64(2) });
            if (room.Closed)
            {
                // 발급·교환 중인 입장권이 끝난 뒤 GS가 방 종료를 승인한다.
                await Execute(connection, transaction, """
                    DELETE FROM "Room" r WHERE r."RoomId"=$1 AND r."NodeId"=$2
                    AND NOT EXISTS (SELECT 1 FROM "RoomAdmission" a WHERE a."RoomId"=r."RoomId" AND a."ExpiresAt">now())
                    AND NOT EXISTS (SELECT 1 FROM "JoinTicket" t WHERE t."RoomId"=r."RoomId" AND t."ExpiresAt">now())
                    """, roomId, request.NodeId);
                if (await Scalar(connection, transaction, "SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 AND \"NodeId\"=$2", roomId, request.NodeId) == null)
                    closed.Add(room.RoomId);
            }
        }
        await Execute(connection, transaction, "DELETE FROM \"JoinTicket\" WHERE \"ExpiresAt\"<=now()");
        await Execute(connection, transaction, "DELETE FROM \"RoomAdmission\" WHERE \"ExpiresAt\"<=now()");
        await RemoveUnoccupiedRooms(connection, transaction);
        await transaction.CommitAsync();
        return new DedicatedHeartbeatResponse { ClosedRoomIds = closed.ToArray(), Configurations = configurations.ToArray() };
    }

    private const string ROOM_SELECT = """
        SELECT r."RoomId",r."OwnerAccountId",r."NodeId",r."Settings"::text,r."PlayerCount",r."SpectatorCount",r."IsInProgress",d."PublicUrl",
        r."PlayerAccountIds"::text,r."SpectatorAccountIds"::text,r."RoomCode",r."ConfigurationVersion",r."AccessVersion"
        FROM "Room" r JOIN "DedicatedNode" d ON d."NodeId"=r."NodeId"
        """;

    private static ServerRoomData ReadRoom(NpgsqlDataReader reader)
    {
        var settings = JsonSerializer.Deserialize<ServerRoomSettings>(reader.GetString(3), ServerRuntime.Json)!;
        return new ServerRoomData { RoomId = reader.GetGuid(0).ToString(), RoomCode = reader.GetString(10), OwnerAccountId = reader.GetGuid(1).ToString(), NodeId = reader.GetString(2), Settings = settings, Name = settings.RoomName,
            IsPrivate = settings.IsPrivate, PlayerCount = reader.GetInt32(4), SpectatorCount = reader.GetInt32(5), IsInProgress = reader.GetBoolean(6),
            ConfigurationVersion = reader.GetInt64(11), AccessVersion = reader.GetInt64(12) };
    }

    public async Task<RoomListResponse> RoomsAsync(string? search, bool includePrivate = false)
    {
        string filter = (search ?? "").Trim();
        if (filter.Length > 30) throw new ApiException("InvalidSearch");
        var rooms = new List<ServerRoomData>();
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = Command(connection, null, ROOM_SELECT + " WHERE d.\"HeartbeatAt\">now()-interval '30 seconds' AND r.\"UpdatedAt\">now()-interval '90 seconds' AND ($2 OR r.\"PlayerCount\"+r.\"SpectatorCount\">0) AND ($2 OR NOT (r.\"Settings\"->>'IsPrivate')::boolean) AND r.\"Settings\"->>'RoomName' ILIKE $1 ORDER BY r.\"UpdatedAt\" DESC LIMIT 100", "%" + filter + "%", includePrivate);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rooms.Add(ReadRoom(reader));
        return new RoomListResponse { Rooms = rooms.ToArray() };
    }

    public async Task<DedicatedAssignment> CreateRoomAsync(ServerSession session, CreateRoomRequest request)
    {
        var settings = request.Settings ?? throw new ApiException("InvalidSettings");
        ValidateSettings(settings);
        ValidateCustomTopics(request.CustomTopics);
        string? passwordHash = settings.IsPrivate ? RoomPassword.Hash(request.Password) : null;
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await RemoveUnoccupiedRooms(connection, transaction);
        await Execute(connection, transaction, "SELECT \"Id\" FROM \"Account\" WHERE \"Id\"=$1 FOR UPDATE", session.AccountId);
        if (Convert.ToInt64(await Scalar(connection, transaction, "SELECT count(*) FROM \"Room\" WHERE \"OwnerAccountId\"=$1", session.AccountId)) >= 1) throw new ApiException("AlreadyOwnsRoom", 409);
        string nodeId;
        string publicUrl;
        await using (var command = Command(connection, transaction, """
            SELECT d."NodeId",d."PublicUrl" FROM "DedicatedNode" d WHERE d."HeartbeatAt">now()-interval '30 seconds'
            AND (SELECT count(*) FROM "Room" r WHERE r."NodeId"=d."NodeId")<d."Capacity"
            ORDER BY (SELECT count(*) FROM "Room" r WHERE r."NodeId"=d."NodeId"),d."NodeId" FOR UPDATE OF d SKIP LOCKED LIMIT 1
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("DedicatedUnavailable", 503);
            nodeId = reader.GetString(0);
            publicUrl = reader.GetString(1);
        }
        Guid roomId = Guid.NewGuid();
        string roomCode;
        do
        {
            roomCode = RoomCodes.Create();
        } while (await Execute(connection, transaction, "INSERT INTO \"Room\" (\"RoomId\",\"RoomCode\",\"OwnerAccountId\",\"NodeId\",\"Settings\",\"CustomTopics\",\"PasswordHash\") VALUES ($1,$2,$3,$4,$5::jsonb,$6::jsonb,$7) ON CONFLICT (\"RoomCode\") DO NOTHING",
            roomId, roomCode, session.AccountId, nodeId, JsonSerializer.Serialize(settings, ServerRuntime.Json), JsonSerializer.Serialize(request.CustomTopics, ServerRuntime.Json), passwordHash) == 0);
        await Execute(connection, transaction, "INSERT INTO \"RoomGameAuthority\" (\"RoomId\",\"NodeId\") VALUES ($1,$2)", roomId, nodeId);
        var assignment = await CreateTicket(connection, transaction, session, roomId, roomCode, publicUrl, false);
        await transaction.CommitAsync();
        return assignment;
    }

    private static Task<int> RemoveUnoccupiedRooms(NpgsqlConnection connection, NpgsqlTransaction transaction) => Execute(connection, transaction, """
        WITH expired AS (
            SELECT r."RoomId" FROM "Room" r WHERE r."UpdatedAt"<now()-interval '90 seconds'
            OR (NOT r."Established" AND r."PlayerCount"=0 AND r."SpectatorCount"=0
                AND NOT EXISTS (SELECT 1 FROM "RoomAdmission" a WHERE a."RoomId"=r."RoomId" AND a."ExpiresAt">now())
                AND NOT EXISTS (SELECT 1 FROM "JoinTicket" t WHERE t."RoomId"=r."RoomId" AND t."ExpiresAt">now()))
            FOR UPDATE SKIP LOCKED
        ) DELETE FROM "Room" WHERE "RoomId" IN (SELECT "RoomId" FROM expired)
        """);

    public async Task<DedicatedAssignment> JoinRoomAsync(ServerSession session, string identifier, bool asSpectator, string password = "")
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var ticket = await JoinRoom(connection, transaction, session, identifier, asSpectator, password);
        await transaction.CommitAsync();
        return ticket;
    }

    private static async Task<DedicatedAssignment> JoinRoom(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ServerSession session, string identifier, bool asSpectator, string password)
    {
        bool legacyId = Guid.TryParse(identifier, out Guid parsedId);
        string lookup = legacyId ? parsedId.ToString() : RoomCodes.Normalize(identifier);
        ServerRoomData room;
        string url;
        string[] players;
        string[] spectators;
        await using (var command = Command(connection, transaction, ROOM_SELECT + " WHERE " + (legacyId ? "r.\"RoomId\"::text" : "r.\"RoomCode\"") + "=$1 AND d.\"HeartbeatAt\">now()-interval '30 seconds' AND r.\"UpdatedAt\">now()-interval '90 seconds' FOR UPDATE OF r", lookup))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("RoomUnavailable", 404);
            room = ReadRoom(reader);
            url = reader.GetString(7);
            players = JsonSerializer.Deserialize<string[]>(reader.GetString(8))!;
            spectators = JsonSerializer.Deserialize<string[]>(reader.GetString(9))!;
        }
        Guid roomId = Guid.Parse(room.RoomId);
        bool reconnectPlayer, reconnectSpectator;
        await using (var membership = Command(connection, transaction, "SELECT \"ConfirmedPlayerAccountIds\" ? $2::text,\"ConfirmedSpectatorAccountIds\" ? $2::text,\"PasswordHash\" FROM \"Room\" WHERE \"RoomId\"=$1", roomId, session.AccountId))
        await using (var reader = await membership.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            reconnectPlayer = reader.GetBoolean(0);
            reconnectSpectator = reader.GetBoolean(1);
            if (room.IsPrivate && !reconnectPlayer && !reconnectSpectator)
            {
                if (reader.IsDBNull(2)) throw new ApiException("RoomPasswordNotConfigured", 409);
                if (string.IsNullOrEmpty(password)) throw new ApiException("RoomPasswordRequired", 403);
                if (!RoomPassword.Verify(password, reader.GetString(2))) throw new ApiException("InvalidRoomPassword", 403);
            }
        }
        bool spectator = reconnectSpectator || !reconnectPlayer && (asSpectator || room.IsInProgress);
        await Execute(connection, transaction, "DELETE FROM \"JoinTicket\" WHERE \"RoomId\"=$1 AND (\"ExpiresAt\"<=now() OR \"AccountId\"=$2)", roomId, session.AccountId);
        long reservations = await PendingReservations(connection, transaction, roomId, spectator, session.AccountId, players.Concat(spectators).ToArray());
        if (!reconnectPlayer && !reconnectSpectator && (spectator ? Math.Max(room.SpectatorCount, spectators.Length) + reservations >= 32 : Math.Max(room.PlayerCount, players.Length) + reservations >= room.Settings.MaxPlayers))
            throw new ApiException("RoomFull", 409);
        return await CreateTicket(connection, transaction, session, roomId, room.RoomCode, url, spectator, asSpectator);
    }

    private static async Task<long> PendingReservations(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid roomId, bool spectator, Guid accountId, string[] admittedAccounts) => Convert.ToInt64(await Scalar(connection, transaction, """
        SELECT count(*) FROM (
            SELECT "AccountId" FROM "JoinTicket" WHERE "RoomId"=$1 AND "IsSpectator"=$2 AND "ExpiresAt">now()
            UNION SELECT "AccountId" FROM "RoomAdmission" WHERE "RoomId"=$1 AND "IsSpectator"=$2 AND "ExpiresAt">now()
        ) pending WHERE "AccountId"<>$3 AND NOT ("AccountId"::text=ANY($4))
        """, roomId, spectator, accountId, admittedAccounts));

    private static async Task<DedicatedAssignment> CreateTicket(NpgsqlConnection connection, NpgsqlTransaction transaction, ServerSession session, Guid roomId, string roomCode, string url, bool spectator, bool spectatorOnly = false)
    {
        string token = ServerRuntime.NewToken();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
        long accessVersion = Convert.ToInt64(await Scalar(connection, transaction, "SELECT \"AccessVersion\" FROM \"Room\" WHERE \"RoomId\"=$1", roomId));
        await Execute(connection, transaction, "INSERT INTO \"JoinTicket\" (\"Hash\",\"AccountId\",\"RoomId\",\"SessionHash\",\"IsSpectator\",\"ExpiresAt\",\"SpectatorOnly\",\"AccessVersion\") VALUES ($1,$2,$3,$4,$5,$6,$7,$8)", ServerRuntime.Hash(token), session.AccountId, roomId, session.Hash, spectator, expiresAt, spectatorOnly, accessVersion);
        return new DedicatedAssignment { RoomId = roomId.ToString(), RoomCode = roomCode, DedicatedUrl = url, JoinTicket = token, ExpiresAt = ServerRuntime.Timestamp(expiresAt) };
    }

    public async Task<RedeemTicketResponse> RedeemTicketAsync(RedeemTicketRequest request)
    {
        Guid roomId = ServerRuntime.AccountId(request.RoomId);
        if (request.JoinTicket == null || request.JoinTicket.Length != 64) throw new ApiException("InvalidTicket", 401);
        await using var connection = await _source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, transaction, "SELECT \"RoomId\" FROM \"Room\" WHERE \"RoomId\"=$1 AND \"NodeId\"=$2 FOR UPDATE", roomId, request.NodeId);
        Guid accountId;
        string sessionHash;
        bool spectator;
        bool spectatorOnly;
        long accessVersion;
        DateTimeOffset admissionUntil;
        await using (var command = Command(connection, transaction, """
            DELETE FROM "JoinTicket" t USING "Room" r WHERE t."Hash"=$1 AND t."ExpiresAt">now() AND t."RoomId"=$2
            AND r."RoomId"=t."RoomId" AND r."NodeId"=$3 AND t."AccessVersion"=r."AccessVersion"
            RETURNING t."AccountId",t."SessionHash",t."IsSpectator",t."SpectatorOnly",t."ExpiresAt",t."AccessVersion"
            """, ServerRuntime.Hash(request.JoinTicket), roomId, request.NodeId))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("InvalidTicket", 401);
            accountId = reader.GetGuid(0);
            sessionHash = reader.GetString(1);
            spectator = reader.GetBoolean(2);
            spectatorOnly = reader.GetBoolean(3);
            admissionUntil = reader.GetFieldValue<DateTimeOffset>(4);
            accessVersion = reader.GetInt64(5);
        }
        string admissionId = ServerRuntime.Hash(request.JoinTicket);
        await Execute(connection, transaction, "INSERT INTO \"RoomAdmission\" (\"Id\",\"RoomId\",\"AccountId\",\"IsSpectator\",\"ExpiresAt\",\"AccessVersion\") VALUES ($1,$2,$3,$4,$5,$6)", admissionId, roomId, accountId, spectator, admissionUntil, accessVersion);
        string scope = (string)(await Scalar(connection, transaction, "SELECT \"Scope\" FROM \"Session\" WHERE \"Hash\"=$1", sessionHash) ?? "");
        if (!scope.StartsWith("game:", StringComparison.Ordinal)) throw new ApiException("InvalidTicket", 401);
        var session = await AuthenticateHash(connection, transaction, sessionHash, scope);
        ServerRoomData room;
        string[] players;
        string[] spectators;
        await using (var command = Command(connection, transaction, ROOM_SELECT + " WHERE r.\"RoomId\"=$1 AND r.\"NodeId\"=$2 FOR UPDATE OF r", roomId, request.NodeId))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new ApiException("RoomUnavailable", 404);
            room = ReadRoom(reader);
            players = JsonSerializer.Deserialize<string[]>(reader.GetString(8))!;
            spectators = JsonSerializer.Deserialize<string[]>(reader.GetString(9))!;
        }
        string accountKey = accountId.ToString();
        if (players.Contains(accountKey)) spectator = false;
        else if (spectators.Contains(accountKey)) spectator = true;
        else
        {
            spectator = spectator || room.IsInProgress;
            string rosterColumn = spectator ? "SpectatorAccountIds" : "PlayerAccountIds";
            string countColumn = spectator ? "SpectatorCount" : "PlayerCount";
            // 참가 승인과 heartbeat 사이에도 새 입장을 예약하여 정원 초과 배정을 막는다.
            long count = (spectator ? Math.Max(room.SpectatorCount, spectators.Length) : Math.Max(room.PlayerCount, players.Length))
                + await PendingReservations(connection, transaction, roomId, spectator, accountId, players.Concat(spectators).ToArray());
            if (count >= (spectator ? 32 : room.Settings.MaxPlayers)) throw new ApiException("RoomFull", 409);
            await Execute(connection, transaction, $"UPDATE \"Room\" SET \"{rosterColumn}\"=\"{rosterColumn}\"||$2::jsonb,\"{countColumn}\"=\"{countColumn}\"+1,\"UpdatedAt\"=now() WHERE \"RoomId\"=$1", roomId, JsonSerializer.Serialize(new[] { accountKey }));
        }
        await Execute(connection, transaction, "UPDATE \"RoomAdmission\" SET \"IsSpectator\"=$2 WHERE \"Id\"=$1", admissionId, spectator);
        await Execute(connection, transaction, "DELETE FROM \"Session\" WHERE \"AccountId\"=$1 AND \"Scope\"=$2", accountId, "dedicated:" + request.NodeId);
        var issued = await IssueSession(connection, transaction, accountId, "dedicated:" + request.NodeId, sessionHash, session.ExpiresAt);
        var profile = await ReadProfile(connection, transaction, accountId);
        var customTopics = JsonSerializer.Deserialize<ServerTopicData[]>((string)(await Scalar(connection, transaction, "SELECT \"CustomTopics\"::text FROM \"Room\" WHERE \"RoomId\"=$1", roomId))!, ServerRuntime.Json)!;
        await Execute(connection, transaction, "INSERT INTO \"RoomGameAdmission\" (\"RoomId\",\"AccountId\") VALUES ($1,$2) ON CONFLICT DO NOTHING", roomId, accountId);
        await transaction.CommitAsync();
        return new RedeemTicketResponse { AccountId = accountId.ToString(), AdmissionId = admissionId, Profile = profile, Room = room, SessionToken = issued.Token, IsSpectator = spectator, SpectatorOnly = spectatorOnly, CustomTopics = customTopics };
    }

    public async Task<bool> CheckDedicatedSessionAsync(SessionCheckRequest request)
    {
        if (!Guid.TryParse(request.AccountId, out Guid accountId) || request.SessionToken == null || request.SessionToken.Length != 64) return false;
        await using var connection = await _source.OpenConnectionAsync();
        string hash = ServerRuntime.Hash(request.SessionToken);
        var scope = await Scalar(connection, null, "SELECT \"Scope\" FROM \"Session\" WHERE \"Hash\"=$1", hash) as string;
        if (scope == null || !scope.StartsWith("dedicated:", StringComparison.Ordinal)) return false;
        try { return (await AuthenticateHash(connection, null, hash, scope)).AccountId == accountId; }
        catch (ApiException) { return false; }
    }

    public static void ValidateSettings(ServerRoomSettings settings)
    {
        settings.MaxPlayers = ServerRoomSettings.MAX_PLAYERS;
        if (settings.LiarCount < 1 || settings.LiarCount >= settings.MaxPlayers || settings.RoundCount is < 1 or > 30 || settings.TargetScore is < 1 or > 1000
            || settings.Mode is < 0 or > 1 || settings.Victory is < 0 or > 1 || settings.RoleSeconds is < 3 or > 30 || settings.DrawSeconds is < 5 or > 180
            || settings.DiscussionSeconds is < 5 or > 300 || settings.RebuttalSeconds is < 0 or > 180 || settings.VoteSeconds is < 5 or > 120
            || settings.RevealSeconds is < 3 or > 30 || settings.GuessSeconds is < 5 or > 120 || settings.ResultSeconds is < 5 or > 60) throw new ApiException("InvalidSettings");
        settings.RoomName = (settings.RoomName ?? "").Trim();
        if (settings.RoomName.Length is < 1 or > 30 || settings.RoomName.Any(c => char.IsControl(c) || c is '<' or '>')) throw new ApiException("InvalidRoomName");
        settings.Topics ??= [];
        if (settings.Topics.Length > 128 || settings.Topics.Any(topic => string.IsNullOrWhiteSpace(topic) || topic.Length > 40 || topic.Any(char.IsControl))) throw new ApiException("InvalidTopics");
        settings.Topics = settings.Topics.Select(topic => topic.Trim()).Distinct().ToArray();
    }

    public static void ValidateCustomTopics(ServerTopicData[] topics)
    {
        if (topics == null || topics.Length > 100) throw new ApiException("InvalidTopics");
        if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(topics, ServerRuntime.Json)) > 32 * 1024) throw new ApiException("InvalidTopics");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in topics)
        {
            if (topic == null || string.IsNullOrWhiteSpace(topic.Name) || topic.Name.Length > 40 || topic.Name.Any(c => char.IsControl(c) || c is '<' or '>')
                || !names.Add(topic.Name.Trim()) || _builtInTopicNames.Value.Contains(topic.Name.Trim()) || topic.Words == null || topic.Words.Length is < 1 or > 200
                || topic.Words.Any(word => string.IsNullOrWhiteSpace(word) || word.Length > 40 || word.Any(c => char.IsControl(c) || c is '<' or '>')))
                throw new ApiException("InvalidTopics");
            topic.Name = topic.Name.Trim();
            topic.Words = topic.Words.Select(word => word.Trim()).Distinct().ToArray();
        }
    }
}
