#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace DrawLiar
{
    public sealed class DrawPublicRoomSmoke : MonoBehaviour
    {
        [Serializable]
        sealed class Report
        {
            public string Outcome = "RUNNING", Stage = "Starting", Role = "", RunId = "", PlayerId = "", Error = "";
            public string PrivateLobbyId = "", PublicLobbyId = "";
            public bool PrivateVerified, PrivateExcluded, PrivateDeleted, PublicVerified, PublicListed;
            public bool JoinedById, HostObservedJoin, GuestLeft, GuestRemoved, PublicDeleted, ServicesCleaned;
            public int ConnectedPlayers;
        }

        readonly Report report = new Report();
        DrawNetworkManager manager;
        LobbyServiceBridge bridge;
        string folder, reportPath, otherPath;
        DateTime deadline;
        DateTime nextBrowse;
        bool reportDirty;
        float nextReportWrite;
        string PrivateName => "smoke-private-" + report.RunId;
        string PublicName => "smoke-public-" + report.RunId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Attach()
        {
            if (!Environment.GetCommandLineArgs().Contains("-drawPublicRoomSmoke")) return;
            var instance = new GameObject("DrawLiar Public Room Smoke");
            DontDestroyOnLoad(instance);
            instance.AddComponent<DrawPublicRoomSmoke>();
        }

        async void Start()
        {
            report.Role = Argument("-publicRoomRole");
            report.RunId = Argument("-publicRoomRun");
            folder = Path.GetFullPath(Argument("-publicRoomReports"));
            reportPath = Path.Combine(folder, report.Role + ".json");
            otherPath = Path.Combine(folder, report.Role == "host" ? "guest.json" : "host.json");
            deadline = DateTime.UtcNow.AddSeconds(180);
            try
            {
                Require(report.Role == "host" || report.Role == "guest", "Invalid smoke role.");
                Require(report.RunId.Length == 12 && report.RunId.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "Invalid smoke run id.");
                Save();
                await Task.Delay(250);
                await WaitAsync(() => (manager = FindFirstObjectByType<DrawNetworkManager>()) != null, "Waiting for game startup");
                bridge = manager.GetComponent<LobbyServiceBridge>();
                Require(bridge != null && !bridge.IsBusy && !NetworkClient.active && !NetworkServer.active, "Run this check without other host/join flags.");
                if (report.Role == "host") await HostAsync();
                else await GuestAsync();
                report.Outcome = "PASS";
            }
            catch (Exception exception)
            {
                report.Outcome = "FAIL";
                report.Error = exception.Message;
            }
            finally
            {
                try { await CleanupAsync(); }
                catch (Exception exception) { report.Outcome = "FAIL"; report.Error += " Cleanup: " + exception.Message; }
                report.Stage = "Finished";
                Save();
                Debug.Log("DRAWLIAR_PUBLIC_ROOM_SMOKE_" + report.Outcome + " " + report.Role);
            }
        }

        async Task HostAsync()
        {
            Stage("Creating private room");
            var privateRoom = await CreateAsync(true);
            report.PrivateLobbyId = privateRoom.Id;
            report.PrivateVerified = privateRoom.IsPrivate;
            Require(report.PrivateVerified, "Private HostAsync created a public lobby.");
            Stage("PrivateReady");
            await WaitAsync(() => Other()?.PrivateExcluded == true, "Guest is checking private-room exclusion");
            await bridge.LeaveAsync();
            await RequireDeletedAsync(privateRoom.Id);
            report.PrivateDeleted = true;

            Stage("Creating public room");
            var publicRoom = await CreateAsync(false);
            report.PublicLobbyId = publicRoom.Id;
            report.PublicVerified = !publicRoom.IsPrivate;
            Require(report.PublicVerified, "Public HostAsync created a private lobby.");
            Stage("PublicReady");
            await WaitAsync(() => Other()?.JoinedById == true && manager.State != null
                && manager.State.Players.Count(player => player.IsConnected) == 2, "Waiting for public-room guest");
            report.ConnectedPlayers = manager.State.Players.Count(player => player.IsConnected);
            report.HostObservedJoin = true;
            Stage("HostObservedJoin");
            await WaitAsync(() => Other()?.GuestLeft == true, "Waiting for guest LeaveAsync");
            var afterLeave = await LobbyService.Instance.GetLobbyAsync(publicRoom.Id);
            report.GuestRemoved = afterLeave.Players.Count == 1 && afterLeave.Players[0].Id == report.PlayerId;
            Require(report.GuestRemoved, "Guest LeaveAsync left stale Lobby membership.");
            await bridge.LeaveAsync();
            await RequireDeletedAsync(publicRoom.Id);
            report.PublicDeleted = true;
        }

        async Task<Lobby> CreateAsync(bool isPrivate)
        {
            await bridge.HostAsync(new RoomSettings { MaxPlayers = 3, RoomName = isPrivate ? PrivateName : PublicName, IsPrivate = isPrivate });
            Require(bridge.IsOnlineRoom && NetworkServer.active, "HostAsync failed: " + bridge.Status);
            report.PlayerId = AuthenticationService.Instance.PlayerId;
            var joined = await LobbyService.Instance.GetJoinedLobbiesAsync();
            Require(joined.Count == 1, "Isolated host profile must belong to exactly one lobby.");
            var lobby = await LobbyService.Instance.GetLobbyAsync(joined[0]);
            Require(lobby.HostId == report.PlayerId && lobby.Name == (isPrivate ? PrivateName : PublicName), "Unexpected lobby ownership/name.");
            return lobby;
        }

        async Task GuestAsync()
        {
            Report host = null;
            await WaitAsync(() => (host = Other())?.PrivateVerified == true, "Waiting for private-room host");
            report.PrivateLobbyId = host.PrivateLobbyId;
            await BrowseAsync();
            report.PrivateExcluded = bridge.PublicRooms.All(room => room.Id != report.PrivateLobbyId && room.Name != PrivateName);
            Require(report.PrivateExcluded, "A private room appeared in RefreshAsync results.");
            Stage("PrivateExcluded");
            await WaitAsync(() => (host = Other())?.PublicVerified == true, "Waiting for public-room host");
            report.PublicLobbyId = host.PublicLobbyId;
            for (int attempt = 0; attempt < 12 && !report.PublicListed; attempt++)
            {
                CheckStop();
                await BrowseAsync();
                report.PublicListed = bridge.PublicRooms.Any(room => room.Id == report.PublicLobbyId && room.Name == PublicName && room.MaxPlayers == 3);
                if (!report.PublicListed) await Task.Delay(1500);
            }
            Require(report.PublicListed, "Public HostAsync lobby never appeared in RefreshAsync results.");
            Stage("Joining by public lobby id");
            await bridge.JoinLobbyAsync(report.PublicLobbyId);
            Require(NetworkClient.active && bridge.IsOnlineRoom, "JoinLobbyAsync failed: " + bridge.Status);
            await WaitAsync(() => NetworkClient.isConnected && manager.State != null
                && manager.State.Players.Count(player => player.IsConnected) == 2, "Waiting for Relay game state");
            report.ConnectedPlayers = manager.State.Players.Count(player => player.IsConnected);
            report.JoinedById = true;
            Stage("JoinedById");
            await WaitAsync(() => Other()?.HostObservedJoin == true, "Waiting for host to observe guest");
            await bridge.LeaveAsync();
            report.GuestLeft = !bridge.IsOnlineRoom && !NetworkClient.active;
            Require(report.GuestLeft, "Guest network did not stop after LeaveAsync.");
            Stage("GuestLeft");
            await WaitAsync(() => Other()?.PublicDeleted == true, "Waiting for public lobby deletion");
        }

        async Task BrowseAsync()
        {
            var remaining = nextBrowse - DateTime.UtcNow;
            if (remaining.TotalMilliseconds > 0) await Task.Delay(remaining);
            nextBrowse = DateTime.UtcNow.AddMilliseconds(1200);
            await bridge.RefreshAsync();
            Require(bridge.Status == "참가 가능한 공개 방이 없습니다." || bridge.Status.StartsWith("공개 방 ", StringComparison.Ordinal),
                "RefreshAsync failed: " + bridge.Status);
            report.PlayerId = AuthenticationService.Instance.PlayerId;
        }

        async Task CleanupAsync()
        {
            if (bridge == null) return;
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(20);
            while (bridge.IsBusy && DateTime.UtcNow < cleanupDeadline) await Task.Delay(100);
            Require(!bridge.IsBusy, "Service operation did not finish before cleanup.");
            await bridge.LeaveAsync();
            if (!AuthenticationService.Instance.IsSignedIn) return;
            await Task.Delay(1200);
            var joined = await LobbyService.Instance.GetJoinedLobbiesAsync();
            foreach (string id in joined)
            {
                await Task.Delay(1200);
                var lobby = await LobbyService.Instance.GetLobbyAsync(id);
                if (lobby.Name != PrivateName && lobby.Name != PublicName) continue;
                if (lobby.HostId == AuthenticationService.Instance.PlayerId)
                    await LobbyService.Instance.DeleteLobbyAsync(id);
                else await LobbyService.Instance.RemovePlayerAsync(id, AuthenticationService.Instance.PlayerId);
            }
            if (report.Role == "host")
            {
                if (!report.PrivateDeleted && report.PrivateLobbyId.Length > 0) { await RequireDeletedAsync(report.PrivateLobbyId); report.PrivateDeleted = true; }
                if (!report.PublicDeleted && report.PublicLobbyId.Length > 0) { await RequireDeletedAsync(report.PublicLobbyId); report.PublicDeleted = true; }
            }
            if (joined.Count > 0) { await Task.Delay(1200); joined = await LobbyService.Instance.GetJoinedLobbiesAsync(); }
            report.ServicesCleaned = !bridge.IsOnlineRoom && !NetworkClient.active && !NetworkServer.active
                && joined.Count == 0;
            Require(report.ServicesCleaned, "Test profile still has network or Lobby membership.");
        }

        static async Task RequireDeletedAsync(string id)
        {
            await Task.Delay(1200);
            try { await LobbyService.Instance.GetLobbyAsync(id); }
            catch (LobbyServiceException exception) when (exception.Reason == LobbyExceptionReason.LobbyNotFound) { return; }
            throw new InvalidOperationException("Hosted lobby was not deleted by LeaveAsync.");
        }

        async Task WaitAsync(Func<bool> predicate, string stage)
        {
            Stage(stage);
            while (!predicate()) { CheckStop(); await Task.Delay(200); }
        }

        void CheckStop()
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Public-room check timed out at " + report.Stage);
            if (File.Exists(Path.Combine(folder, "stop.signal"))) throw new OperationCanceledException("Runner requested cleanup.");
            var other = Other();
            if (other?.Outcome == "FAIL") throw new InvalidOperationException("Other process failed: " + other.Error);
        }

        Report Other()
        {
            if (!File.Exists(otherPath)) return null;
            try
            {
                using var stream = new FileStream(otherPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return JsonUtility.FromJson<Report>(reader.ReadToEnd());
            }
            catch (IOException) { return null; }
        }

        void Stage(string value) { report.Stage = value; Save(); }
        void Save()
        {
            reportDirty = true;
            TryWriteReport();
        }

        void Update()
        {
            if (reportDirty && Time.unscaledTime >= nextReportWrite) TryWriteReport();
        }

        void TryWriteReport()
        {
            nextReportWrite = Time.unscaledTime + .1f;
            try
            {
                Directory.CreateDirectory(folder);
                string temporary = reportPath + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
                if (File.Exists(reportPath)) File.Replace(temporary, reportPath, null);
                else File.Move(temporary, reportPath);
                reportDirty = false;
            }
            catch (IOException)
            {
                // A report reader can briefly deny replacement on Windows; retry without terminating the test lobby.
            }
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static string Argument(string key)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, key);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : "";
        }
    }
}
#endif
