using System.Text.Json;
using DrawLiar.Server;

namespace DrawLiar.DedicatedServer;

internal sealed partial class DedicatedRoom
{
    public async Task ConfigureAsync(GameConnection connection, GameplayEnvelope envelope, double now,
        Func<ConfigureRoomRequest, CancellationToken, Task<RoomConfigurationData>> persist)
    {
        ConfigureRoomRequest request;
        string requestId = envelope.RequestId ?? "";
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection.AccountId, out var current) || current != connection || !connection.IsAlive) return;
            if (!connection.AcceptRate(envelope.Type ?? "", now)) { if (connection.FrameRateExceeded) connection.Abort(); return; }
            if (_configurationPending || _configurationSyncRequired || _ownerAccountId != connection.AccountId
                || envelope.Type != "request" || envelope.Kind != "configure" || envelope.Settings == null
                || _session.Phase != GamePhase.Lobby && _session.Phase != GamePhase.MatchResults)
            {
                ConfigureReplyLocked(connection, requestId, false, "RoomConfigurationDenied");
                return;
            }
            if (requestId.Length > 80 || requestId.Any(char.IsControl))
            {
                ConfigureReplyLocked(connection, "", false, "InvalidOperation");
                return;
            }
            var settings = envelope.Settings.Copy();
            if (!Enum.IsDefined(typeof(LiarMode), settings.LiarMode))
            {
                ConfigureReplyLocked(connection, requestId, false, "InvalidSettings");
                return;
            }
            settings.Validate();
            ServerTopicData[] additions;
            GameData gameData;
            try
            {
                additions = ServerDatabase.MergeCustomTopics(null, envelope.CustomTopics);
                gameData = RoomGameData(ServerDatabase.MergeCustomTopics(_customTopics, additions));
            }
            catch (ApiException)
            {
                ConfigureReplyLocked(connection, requestId, false, "InvalidTopics");
                return;
            }
            settings.Topics ??= gameData.Topics.Where(topic => !string.IsNullOrWhiteSpace(topic.Name)
                && topic.Words != null && topic.Words.Any(word => !string.IsNullOrWhiteSpace(word)))
                .Select(topic => topic.Name).Distinct().Take(128).ToArray();
            if (!_session.CanConfigure(settings, gameData))
            {
                ConfigureReplyLocked(connection, requestId, false, "InvalidTopics");
                return;
            }
            request = new ConfigureRoomRequest
            {
                RoomId = RoomId, OwnerAccountId = _ownerAccountId, OperationId = Guid.NewGuid().ToString(),
                ExpectedVersion = _configurationVersion, Password = envelope.Password ?? "",
                Settings = JsonSerializer.Deserialize<ServerRoomSettings>(JsonSerializer.Serialize(settings, GameplayWire.Json), GameplayWire.Json)!,
                CustomTopics = additions
            };
            _configurationPending = true;
            BroadcastSnapshotsLocked(now);
        }
        bool accepted = false;
        string code = "";
        try
        {
            var configuration = await persist(request, connection.CancellationToken);
            lock (_gate)
            {
                accepted = ApplyConfigurationLocked(configuration, true);
                if (!accepted) { code = "RoomConfigurationChanged"; _configurationSyncRequired = true; }
            }
        }
        catch (ApiException exception)
        {
            code = exception.Code;
            if (exception.Status >= 500 || code == "RoomConfigurationChanged")
                lock (_gate) _configurationSyncRequired = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            code = "ServerUnavailable";
            lock (_gate) _configurationSyncRequired = true;
        }
        finally
        {
            request.Password = "";
            envelope.Password = "";
            lock (_gate)
            {
                _configurationPending = false;
                if (_deferredConfiguration != null)
                {
                    ApplyConfigurationLocked(_deferredConfiguration, false);
                    _deferredConfiguration = null;
                }
                EnsureHostLocked();
                ConfigureReplyLocked(connection, requestId, accepted, code);
                BroadcastSnapshotsLocked(RoomRegistry.Now);
                _statusChanged();
            }
        }
    }

    public void ApplyConfiguration(RoomConfigurationData configuration)
    {
        lock (_gate)
        {
            if (configuration.RoomId != RoomId || configuration.Version < _configurationVersion) return;
            if (_configurationPending)
            {
                if (_deferredConfiguration == null || configuration.Version >= _deferredConfiguration.Version)
                    _deferredConfiguration = configuration;
                return;
            }
            if (ApplyConfigurationLocked(configuration, false)) BroadcastSnapshotsLocked(RoomRegistry.Now);
        }
    }

    private bool ApplyConfigurationLocked(RoomConfigurationData configuration, bool pending)
    {
        if (configuration.RoomId != RoomId || configuration.Version < _configurationVersion || configuration.Settings == null) return false;
        if (!pending && !_configurationSyncRequired && configuration.Version == _configurationVersion) return true;
        var settings = JsonSerializer.Deserialize<RoomSettings>(JsonSerializer.Serialize(configuration.Settings, GameplayWire.Json), GameplayWire.Json)!;
        ServerTopicData[] customTopics;
        try { customTopics = configuration.CustomTopics == null ? _customTopics : ServerDatabase.MergeCustomTopics(null, configuration.CustomTopics); }
        catch (ApiException) { return false; }
        if (!_session.Configure(settings, RoomGameData(customTopics))) return false;
        _customTopics = customTopics;
        _configurationVersion = configuration.Version;
        _accessVersion = configuration.AccessVersion;
        _configurationSyncRequired = false;
        return true;
    }

    private void ConfigureReplyLocked(GameConnection connection, string requestId, bool accepted, string code) => connection.Queue(new GameplayEnvelope
    {
        Type = "configured", RequestId = requestId, Accepted = accepted, Code = accepted ? "" : code,
        Text = accepted ? "" : code switch
        {
            "RoomPasswordRequired" => "비밀번호가 필요한 방입니다.",
            "InvalidRoomPasswordFormat" => "비밀번호는 4~32자로 입력하세요.",
            "InvalidRoomPassword" => "비밀번호가 올바르지 않습니다.",
            "RoomConfigurationChanged" => "방 설정이 변경되었습니다. 다시 시도하세요.",
            "RoomConfigurationDenied" => "방 옵션 변경 권한이 없습니다.",
            "InvalidTopics" => "주제를 하나 이상 선택하세요.",
            _ => "방 옵션을 저장하지 못했습니다. 다시 시도하세요."
        },
        Sequence = ++_sequence
    });
}
