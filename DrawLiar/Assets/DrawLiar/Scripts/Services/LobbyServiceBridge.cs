using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Mirror;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace DrawLiar
{
    public sealed class PublicRoomInfo
    {
        public string Id;
        public string Name;
        public int Players;
        public int MaxPlayers;
    }

    [DisallowMultipleComponent]
    public sealed class LobbyServiceBridge : MonoBehaviour
    {
        const string RelayKey = "relay";
        const string ProtocolKey = "protocol";
        const string ProtocolVersion = "drawliar-2";
        readonly List<PublicRoomInfo> publicRooms = new List<PublicRoomInfo>();
        DrawNetworkManager manager;
        RelayMirrorTransport relayTransport;
        Lobby lobby;
        string originalHost;
        bool hosting;
        bool maintaining;
        bool quitting;
        float nextMaintenance;
        Task authenticationTask;

        public string RoomCode => lobby?.LobbyCode ?? "";
        public string Status { get; private set; } = "친구를 초대해 함께 그려보세요.";
        public bool IsBusy { get; private set; }
        public bool IsOnlineRoom => lobby != null;
        public IReadOnlyList<PublicRoomInfo> PublicRooms => publicRooms;
        public event Action Changed;

        public void Initialize(DrawNetworkManager networkManager)
        {
            manager = networkManager;
            relayTransport = GetComponent<RelayMirrorTransport>();
            if (relayTransport == null) relayTransport = gameObject.AddComponent<RelayMirrorTransport>();
        }

        async Task AuthenticateAsync()
        {
            if (authenticationTask != null)
            {
                await authenticationTask;
                return;
            }
            authenticationTask = AuthenticateCoreAsync();
            try { await authenticationTask; }
            finally { authenticationTask = null; }
        }

        async Task AuthenticateCoreAsync()
        {
            var config = Resources.Load<OnlineServicesConfig>("OnlineServicesConfig");
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetEnvironmentName(config != null ? config.EnvironmentName : "production");
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-ugsProfile") options.SetProfile(args[i + 1]);
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn || AuthenticationService.Instance.IsExpired)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        public Task HostAsync(RoomSettings settings) => RunAsync(async () =>
        {
            EnsureStopped();
            settings = settings?.Copy() ?? new RoomSettings();
            settings.Validate();
            await AuthenticateAsync();
            SetStatus("Relay 서버를 준비하고 있습니다…");
            var allocation = await RelayService.Instance.CreateAllocationAsync(settings.MaxPlayers - 1);
            string relayCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            SetStatus("온라인 방을 만들고 있습니다…");
            lobby = await LobbyService.Instance.CreateLobbyAsync(settings.RoomName, settings.MaxPlayers, new CreateLobbyOptions
            {
                IsPrivate = settings.IsPrivate,
                Player = new Player(AuthenticationService.Instance.PlayerId, allocationId: allocation.AllocationId.ToString()),
                Data = new Dictionary<string, DataObject>
                {
                    [RelayKey] = new DataObject(DataObject.VisibilityOptions.Member, relayCode),
                    [ProtocolKey] = new DataObject(DataObject.VisibilityOptions.Public, ProtocolVersion, DataObject.IndexOptions.S1)
                }
            });
            hosting = true;
            originalHost = lobby.HostId;
            relayTransport.Configure(allocation.ToRelayServerData("dtls"));
            ActivateTransport();
            manager.ConfigureRoom(settings);
            manager.StartHost();
            nextMaintenance = Time.unscaledTime + 10f;
            SetStatus("온라인 방을 만들었습니다. 초대 코드: " + RoomCode);
        }, true);

        public Task JoinCodeAsync(string code) => JoinAsync("Lobby.JoinLobbyByCode", async () =>
        {
            string normalized = NormalizeCode(code);
            if (normalized.Length == 0) throw new InvalidOperationException("초대 코드를 입력하세요.");
            return await LobbyService.Instance.JoinLobbyByCodeAsync(normalized);
        });

        public Task JoinLobbyAsync(string lobbyId) => JoinAsync("Lobby.JoinLobbyById", () => LobbyService.Instance.JoinLobbyByIdAsync(lobbyId));

        Task JoinAsync(string joinApi, Func<Task<Lobby>> join) => RunAsync(async () =>
        {
            string stage = "Join.Preflight";
            try
            {
                EnsureStopped();
                stage = "Authentication";
                await AuthenticateAsync();
                SetStatus("온라인 방에 참가하고 있습니다…");
                stage = joinApi;
                lobby = await join();
                stage = "Join.ValidateLobby";
                originalHost = lobby.HostId;
                hosting = false;
                if (lobby.Data == null || !lobby.Data.TryGetValue(ProtocolKey, out var protocol) || protocol.Value != ProtocolVersion)
                    throw new InvalidOperationException("게임 버전이 다른 방입니다.");
                if (!lobby.Data.TryGetValue(RelayKey, out var data) || string.IsNullOrEmpty(data.Value))
                    throw new InvalidOperationException("방의 Relay 연결 정보가 없습니다.");
                stage = "Relay.JoinAllocation";
                var allocation = await RelayService.Instance.JoinAllocationAsync(data.Value);
                stage = "Lobby.UpdatePlayer";
                await LobbyService.Instance.UpdatePlayerAsync(lobby.Id, AuthenticationService.Instance.PlayerId, new UpdatePlayerOptions
                {
                    AllocationId = allocation.AllocationId.ToString()
                });
                stage = "Mirror.StartClient";
                relayTransport.Configure(allocation.ToRelayServerData("dtls"));
                ActivateTransport();
                manager.networkAddress = "relay";
                manager.StartClient();
                nextMaintenance = Time.unscaledTime + 10f;
                SetStatus("방에 연결하고 있습니다…");
            }
            catch (Exception exception)
            {
                exception.Data["DrawLiarApi"] = stage;
                throw;
            }
        }, true);

        public Task RefreshAsync() => RunAsync(async () =>
        {
            await AuthenticateAsync();
            SetStatus("공개 방을 찾고 있습니다…");
            var result = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
            {
                Count = 30,
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                    new QueryFilter(QueryFilter.FieldOptions.S1, ProtocolVersion, QueryFilter.OpOptions.EQ)
                }
            });
            publicRooms.Clear();
            foreach (var room in result.Results)
                publicRooms.Add(new PublicRoomInfo { Id = room.Id, Name = room.Name, Players = room.MaxPlayers - room.AvailableSlots, MaxPlayers = room.MaxPlayers });
            SetStatus(publicRooms.Count == 0 ? "참가 가능한 공개 방이 없습니다." : $"공개 방 {publicRooms.Count}개를 찾았습니다.");
        });

        public Task LeaveAsync() => RunAsync(async () =>
        {
            manager.Leave();
            await CleanupLobbyAsync();
            SetStatus("방에서 나왔습니다.");
        });

        void EnsureStopped()
        {
            if (manager == null) throw new InvalidOperationException("네트워크 매니저가 초기화되지 않았습니다.");
            if (NetworkServer.active || NetworkClient.active || lobby != null)
                throw new InvalidOperationException("현재 방에서 나간 뒤 다른 방에 참가하세요.");
        }

        void ActivateTransport()
        {
            if (NetworkServer.active || NetworkClient.active) throw new InvalidOperationException("실행 중에는 Transport를 바꿀 수 없습니다.");
            manager.transport = relayTransport;
            Transport.active = relayTransport;
        }

        async Task RunAsync(Func<Task> operation, bool cleanupOnFailure = false)
        {
            if (IsBusy) return;
            bool alreadyConnected = lobby != null || NetworkClient.active || NetworkServer.active;
            IsBusy = true;
            Changed?.Invoke();
            try { await operation(); }
            catch (Exception exception)
            {
                Debug.LogWarning("[DrawLiar Lobby] " + FormatDiagnostic(exception));
                if (cleanupOnFailure && !alreadyConnected)
                {
                    manager?.Leave();
                    relayTransport?.Shutdown();
                    await CleanupLobbyAsync();
                }
                SetStatus(DescribeError(exception));
            }
            finally { IsBusy = false; Changed?.Invoke(); }
        }

        async void Update()
        {
            if (quitting || IsBusy || maintaining || lobby == null) return;
            if (relayTransport.AllocationInvalid)
            {
                maintaining = true;
                manager.Leave();
                await CleanupLobbyAsync();
                SetStatus("Relay 연결이 만료되거나 연결에 실패해 방을 종료했습니다. 방을 다시 만들거나 참가하세요.");
                maintaining = false;
                return;
            }
            if (!NetworkClient.active && !NetworkServer.active)
            {
                maintaining = true;
                await CleanupLobbyAsync();
                SetStatus("호스트와 연결이 종료되었습니다.");
                maintaining = false;
                return;
            }
            if (Time.unscaledTime < nextMaintenance) return;
            nextMaintenance = Time.unscaledTime + (hosting ? 15f : 10f);
            maintaining = true;
            var current = lobby;
            try
            {
                await AuthenticateAsync();
                if (lobby != current) return;
                if (hosting) await LobbyService.Instance.SendHeartbeatPingAsync(current.Id);
                else
                {
                    var updated = await LobbyService.Instance.GetLobbyAsync(current.Id);
                    if (lobby != current) return;
                    if (updated.HostId != originalHost)
                    {
                        manager.Leave();
                        await CleanupLobbyAsync();
                        SetStatus("방장이 나가서 방이 종료되었습니다.");
                    }
                    else
                    {
                        lobby = updated;
                        if (NetworkClient.isConnected) SetStatus("온라인 방에 연결되었습니다.");
                    }
                }
            }
            catch (LobbyServiceException exception)
            {
                if (lobby != current) return;
                if (exception.Reason == LobbyExceptionReason.LobbyNotFound || exception.Reason == LobbyExceptionReason.Forbidden)
                {
                    manager.Leave();
                    await CleanupLobbyAsync();
                    SetStatus("방이 종료되었거나 참가 권한이 없어 연결을 종료했습니다.");
                }
                else SetStatus("로비 연결 확인 중: " + exception.Reason);
            }
            catch (Exception exception) { SetStatus("로비 연결 확인 중: " + exception.Message); }
            finally { maintaining = false; }
        }

        async Task CleanupLobbyAsync()
        {
            var previous = lobby;
            bool wasHost = hosting;
            lobby = null;
            hosting = false;
            originalHost = null;
            Changed?.Invoke();
            if (previous == null) return;
            try
            {
                await AuthenticateAsync();
                if (wasHost) await LobbyService.Instance.DeleteLobbyAsync(previous.Id);
                else if (AuthenticationService.Instance.IsSignedIn)
                    await LobbyService.Instance.RemovePlayerAsync(previous.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception exception) { Debug.LogWarning("[DrawLiar Lobby cleanup] " + FormatDiagnostic(exception)); }
        }

        async void OnApplicationQuit()
        {
            quitting = true;
            await CleanupLobbyAsync();
        }

        void SetStatus(string value) { Status = value; Changed?.Invoke(); }

        public static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string code = value.Trim().ToUpperInvariant();
            if (code.Length > 12) throw new InvalidOperationException("올바른 초대 코드를 입력하세요.");
            foreach (char character in code)
                if (character < 'A' || character > 'Z')
                    if (character < '0' || character > '9') throw new InvalidOperationException("초대 코드에는 영문과 숫자만 사용할 수 있습니다.");
            return code;
        }

        static string FormatDiagnostic(Exception exception)
        {
            var text = new StringBuilder("api=").Append(exception.Data["DrawLiarApi"] ?? "unspecified");
            for (var error = exception; error != null; error = error.InnerException)
            {
                text.AppendLine().Append(error.GetType().FullName).Append(" hresult=").Append(error.HResult);
                if (error is RequestFailedException request) text.Append(" code=").Append(request.ErrorCode);
                if (error is LobbyServiceException lobbyError) text.Append(" reason=").Append(lobbyError.Reason);
                if (error is RelayServiceException relayError) text.Append(" reason=").Append(relayError.Reason);
                // SDK HTTP types are internal. Read only response status, never bodies, headers, or exception messages.
                if (error.GetType().Namespace == "Unity.Services.Lobbies.Http" || error.GetType().Namespace == "Unity.Services.Relay.Http")
                {
                    var response = error.GetType().GetField("Response")?.GetValue(error);
                    if (response != null)
                    {
                        text.Append(" http=").Append(response.GetType().GetProperty("StatusCode")?.GetValue(response));
                        text.Append(" networkError=").Append(response.GetType().GetProperty("IsNetworkError")?.GetValue(response));
                    }
                }
                text.AppendLine().Append(error.StackTrace);
            }
            return text.ToString();
        }

        static string DescribeError(Exception exception)
        {
            if (exception is LobbyServiceException lobbyException)
            {
                if (lobbyException.Reason == LobbyExceptionReason.LobbyNotFound) return "방을 찾지 못했습니다. 초대 코드를 확인하세요.";
                if (lobbyException.Reason == LobbyExceptionReason.LobbyFull) return "방의 인원이 가득 찼습니다.";
                if (lobbyException.Reason == LobbyExceptionReason.RateLimited) return "요청이 많습니다. 잠시 후 다시 시도하세요.";
            }
            if (exception is RequestFailedException)
                return "온라인 연결 실패: " + exception.Message + " Unity Dashboard에서 Authentication·Lobby·Relay 활성화와 인터넷 연결을 확인하세요.";
            return exception.Message;
        }
    }
}
