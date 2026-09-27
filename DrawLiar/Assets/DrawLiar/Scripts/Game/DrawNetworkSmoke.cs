#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using Unity.Services.Lobbies;
using UnityEngine;

namespace DrawLiar
{
    public sealed class DrawNetworkSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class SmokeReport
        {
            public string Outcome = "RUNNING";
            public string Player = "", LastPhase = "", Mode = "", RoomCode = "";
            public int PlayerId = -1, Round, CompletedRounds, CanvasResets, ReceivedStrokes, ReceivedChats, LocalScore;
            public float ElapsedSeconds;
            public bool IsSpectator, ReplayReady, ReplayVerified, MatchPassed, HostShutdownRequested, HostDisconnectObserved;
            public bool Online, ServicesCleaned, LobbyDeletionVerified;
            public int ReplayVersion, PeakPlayers, RoomCapacity;
            public string ReplayHash = "";
            public string[] Phases = Array.Empty<string>();
            public string[] Errors = Array.Empty<string>();
        }

        private readonly SmokeReport report = new SmokeReport();
        private readonly List<string> errors = new List<string>();
        private readonly HashSet<string> phases = new HashSet<string>();
        private readonly HashSet<int> completedRounds = new HashSet<int>();
        private readonly HashSet<int> drawnRounds = new HashSet<int>();
        private readonly Dictionary<int, int> votes = new Dictionary<int, int>();
        private readonly HashSet<int> guessedRounds = new HashSet<int>();
        private DrawNetworkManager manager;
        private string reportPath;
        private float began, drawAt, configurationAt = -1;
        private bool sentChat, started, sentEnd, done;
        private int expectedScore, lastRoundCanvas = -1, lastRound;
        private DrawingMode mode;
        private int expectedPlayers = 4;
        private bool expectReplay;
        private bool lifecycle, spectator, replayAcknowledged, replayChatSent, disconnected;
        private string stopSignal, expectedReplayHash;
        private int expectedReplayVersion, canvasStrokes;
        private uint canvasHash = 2166136261;
        private float joinedAt = -1;
        private LobbyServiceBridge lobbyService;
        private Task cleanupTask;
        private bool online, wasHost, finishing;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            if (!Environment.GetCommandLineArgs().Contains("-drawSmoke")) return;
            var instance = new GameObject("DrawLiar Network Smoke");
            DontDestroyOnLoad(instance);
            instance.AddComponent<DrawNetworkSmoke>();
        }

        private static string Argument(string key)
        {
            var arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, key);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : "";
        }

        private void Awake()
        {
            began = Time.unscaledTime;
            Application.targetFrameRate = 30;
            report.Player = Argument("-drawName");
            reportPath = Argument("-drawReport");
            if (string.IsNullOrWhiteSpace(reportPath)) reportPath = Path.Combine(Application.persistentDataPath, "smoke-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
            mode = string.Equals(Argument("-drawSmokeMode"), "Individual", StringComparison.OrdinalIgnoreCase) ? DrawingMode.Individual : DrawingMode.Relay;
            report.Mode = mode.ToString();
            lifecycle = Environment.GetCommandLineArgs().Contains("-drawLifecycleSmoke");
            spectator = Environment.GetCommandLineArgs().Contains("-drawSpectator");
            if (int.TryParse(Argument("-drawSmokePlayers"), out var count)) expectedPlayers = Mathf.Clamp(count, 3, 12);
            expectReplay = lifecycle && expectedPlayers < 12;
            report.IsSpectator = spectator;
            online = Environment.GetCommandLineArgs().Contains("-drawOnlineHost") || !string.IsNullOrEmpty(Argument("-drawOnlineJoin"));
            report.Online = online;
            wasHost = Environment.GetCommandLineArgs().Contains("-drawOnlineHost");
            stopSignal = Argument("-drawStopSignal");
            expectedReplayHash = Argument("-drawReplayHash");
            int.TryParse(Argument("-drawReplayVersion"), out expectedReplayVersion);
            WriteReport();
        }

        private void Update()
        {
            if (done) return;
            if (finishing) { Finish(); return; }
            if (Time.unscaledTime - began > 240) { Fail("Timed out before three complete rounds."); Finish(); return; }
            if (disconnected && !NetworkClient.active && !NetworkServer.active)
            {
                report.HostDisconnectObserved = true;
                Finish();
                return;
            }
            if (manager == null)
            {
                manager = FindFirstObjectByType<DrawNetworkManager>();
                if (manager == null) return;
                lobbyService = manager.GetComponent<LobbyServiceBridge>();
                manager.StateChanged += OnState;
                manager.CanvasCleared += OnCanvasCleared;
                manager.StrokeReceived += OnStroke;
                manager.ChatReceived += OnChat;
                if (manager.State != null) OnState(manager.State);
            }
            if (online && lobbyService != null && !string.IsNullOrEmpty(lobbyService.RoomCode) && report.RoomCode != lobbyService.RoomCode)
            {
                report.RoomCode = lobbyService.RoomCode;
                WriteReport();
            }
            if (online && wasHost && !report.HostShutdownRequested && File.Exists(stopSignal))
            {
                report.HostShutdownRequested = true;
                WriteReport();
                cleanupTask = CleanupOnlineAsync();
            }
            if (online && !report.HostShutdownRequested && report.Round == 0 && Time.unscaledTime - began > 15
                && lobbyService != null && !lobbyService.IsBusy && !lobbyService.IsOnlineRoom
                && !NetworkClient.active && !NetworkServer.active)
            {
                Fail("Online room connection failed: " + lobbyService.Status);
                Finish();
                return;
            }
            var state = manager.State;
            if (state == null) return;
            if (lifecycle && report.MatchPassed)
            {
                if (state.IsHost && !report.HostShutdownRequested && File.Exists(stopSignal))
                {
                    report.HostShutdownRequested = true;
                    WriteReport();
                    if (online) cleanupTask = CleanupOnlineAsync();
                    else manager.Leave();
                }
                return;
            }
            if (!sentChat)
            {
                sentChat = true;
                manager.Chat("smoke-chat-" + report.Player);
            }
            if (spectator && report.ReplayVerified && !replayChatSent && Time.unscaledTime - joinedAt >= 1.5f)
            {
                replayChatSent = true;
                manager.Chat("__draw_smoke_replay__");
            }
            if (state.IsHost && state.Phase == GamePhase.Lobby && state.Players.Count(player => player.IsConnected) >= expectedPlayers)
            {
                if (configurationAt < 0)
                {
                    configurationAt = Time.unscaledTime;
                    manager.ConfigureRoom(new RoomSettings
                    {
                        MaxPlayers = 12, LiarCount = 1, RoundCount = 3, Mode = mode, Victory = VictoryMode.RoundCount,
                        RoleSeconds = 3, DrawSeconds = lifecycle ? 45 : 5, DiscussionSeconds = 5, RebuttalSeconds = 5, VoteSeconds = 5,
                        RevealSeconds = 3, GuessSeconds = 5, ResultSeconds = 5, RoomName = "Network smoke", IsPrivate = online
                    });
                }
                else if (!started && Time.unscaledTime - configurationAt >= 1)
                {
                    started = true;
                    manager.StartMatch();
                }
            }
            if (manager.CanDraw)
            {
                if (drawnRounds.Add(state.Round))
                {
                    drawAt = Time.unscaledTime;
                    sentEnd = false;
                    for (int index = 0; index < 3; index++)
                    {
                        manager.SendStroke(new DrawStroke
                        {
                            X1 = .1f + index * .1f, Y1 = .1f + ((uint)state.LocalPlayerId % 7) * .08f,
                            X2 = .15f + index * .1f, Y2 = .25f, Size = .007f,
                            R = (byte)(40 + state.LocalPlayerId % 150), G = 100, B = 210,
                            CanvasVersion = manager.CanvasVersion
                        });
                    }
                }
                else if (!sentEnd && Time.unscaledTime - drawAt >= 1 && (!expectReplay || state.Round != 1 || replayAcknowledged))
                {
                    sentEnd = true;
                    manager.EndTurn();
                }
            }
            if (state.Phase == GamePhase.Voting && !state.LocalIsSpectator && !votes.ContainsKey(state.Round))
            {
                int target = state.Players.First(player => player.IsConnected && !player.IsSpectator && player.Id != state.LocalPlayerId).Id;
                votes[state.Round] = target;
                manager.Vote(target);
            }
            if (state.Phase == GamePhase.Guessing && state.LocalIsLiar && guessedRounds.Add(state.Round))
                manager.Guess("__smoke_wrong_answer__");
        }

        private void OnState(RoomSnapshot state)
        {
            if (done) return;
            if (state == null)
            {
                if (lifecycle && report.MatchPassed) disconnected = true;
                else if (report.Round > 0) { Fail("Disconnected during the match."); Finish(); }
                return;
            }
            if (joinedAt < 0)
            {
                joinedAt = Time.unscaledTime;
                if (spectator && (state.Round != 1 || state.Phase != GamePhase.Drawing || !state.LocalIsSpectator))
                    Fail("Late client did not join as a spectator during the first drawing.");
            }
            if (spectator && (!state.LocalIsSpectator || state.LocalIsLiar || state.ArtistId == state.LocalPlayerId || manager.CanDraw))
                Fail("Late spectator was promoted or permitted to draw during the match.");
            report.PlayerId = state.LocalPlayerId;
            report.PeakPlayers = Mathf.Max(report.PeakPlayers, state.Players.Count(player => player.IsConnected));
            report.RoomCapacity = state.Settings.MaxPlayers;
            wasHost |= state.IsHost;
            if (online && lobbyService != null && !string.IsNullOrEmpty(lobbyService.RoomCode)) report.RoomCode = lobbyService.RoomCode;
            report.Round = state.Round;
            report.LocalScore = state.Players.FirstOrDefault(player => player.Id == state.LocalPlayerId)?.Score ?? 0;
            bool changed = phases.Add(state.Round + ":" + state.Phase);
            report.LastPhase = state.Phase.ToString();
            if (state.Phase > GamePhase.Lobby && state.Phase < GamePhase.RoundResults)
            {
                if ((state.LocalIsLiar || state.LocalIsSpectator) && !string.IsNullOrEmpty(state.Word)) Fail("Secret word leaked to liar/spectator before results.");
                if (!state.LocalIsLiar && !state.LocalIsSpectator && string.IsNullOrEmpty(state.Word)) Fail("Citizen did not receive the secret word.");
            }
            if (state.Phase < GamePhase.LiarReveal && state.Players.Any(player => player.IsLiar)) Fail("Another player's role leaked before reveal.");
            if (state.Phase == GamePhase.Drawing)
            {
                if (lastRound == state.Round && mode == DrawingMode.Relay && lastRoundCanvas != state.CanvasVersion) Fail("Relay canvas was reset between artists.");
                lastRound = state.Round;
                lastRoundCanvas = state.CanvasVersion;
            }
            if (state.Phase == GamePhase.RoundResults && completedRounds.Add(state.Round))
            {
                var local = state.Players.Single(player => player.Id == state.LocalPlayerId);
                if (string.IsNullOrEmpty(state.Word)) Fail("Round result did not reveal the word.");
                var rules = GameDataStore.Load().Scoring;
                bool correctVote = votes.TryGetValue(state.Round, out var target) && state.Players.Any(player => player.Id == target && player.IsLiar);
                int points = local.IsSpectator ? 0 : local.IsLiar ? (local.IsCaught ? 0 : rules.LiarUncaught) : (correctVote ? rules.CitizenCorrectVote : 0);
                expectedScore += points;
                if (local.RoundPoints != points || local.Score != expectedScore) Fail("Authoritative score differs from role/vote result in round " + state.Round + ".");
                report.CompletedRounds = completedRounds.Count;
            }
            if (state.Phase == GamePhase.MatchResults)
            {
                if (report.MatchPassed) return;
                if (completedRounds.Count != 3 || drawnRounds.Count != (spectator ? 0 : 3)) Fail("Incorrect completed-round or drawing-turn count.");
                if (report.ReceivedStrokes != expectedPlayers * 9) Fail("Client did not receive every reliable drawing segment, including replay.");
                if (report.ReceivedChats < 1) Fail("No chat message received.");
                if (report.CanvasResets < (mode == DrawingMode.Individual ? (3 * (expectedPlayers + 1) - (spectator ? 1 : 0)) : 3)) Fail("Too few canvas resets for selected mode.");
                if (expectReplay && !replayAcknowledged) Fail("Late spectator never acknowledged the exact canvas replay.");
                if (spectator && (!report.ReplayVerified || report.LocalScore != 0 || votes.Count != 0 || guessedRounds.Count != 0))
                    Fail("Spectator replay or participation restriction check failed.");
                for (int round = 1; round <= 3; round++)
                    foreach (GamePhase phase in Enum.GetValues(typeof(GamePhase)))
                        if (phase > GamePhase.Lobby && phase < GamePhase.MatchResults && !(spectator && round == 1 && phase == GamePhase.RoleReveal)
                            && !phases.Contains(round + ":" + phase)) Fail("Missing phase " + round + ":" + phase);
                report.MatchPassed = errors.Count == 0;
                if (lifecycle && report.MatchPassed) WriteReport();
                else Finish();
            }
            else if (changed) WriteReport();
        }

        private void OnCanvasCleared() { report.CanvasResets++; canvasStrokes = 0; canvasHash = 2166136261; }
        private void OnStroke(DrawStroke stroke)
        {
            report.ReceivedStrokes++;
            canvasStrokes++;
            foreach (byte value in System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(stroke)))
                canvasHash = unchecked((canvasHash ^ value) * 16777619);
            var state = manager.State;
            if (!expectReplay || state == null || state.Round != 1 || state.Phase != GamePhase.Drawing || canvasStrokes != 3) return;
            string hash = canvasHash.ToString("X8");
            if (state.IsHost && !report.ReplayReady)
            {
                report.ReplayReady = true;
                report.ReplayVersion = stroke.CanvasVersion;
                report.ReplayHash = hash;
                WriteReport();
            }
            if (spectator && !report.ReplayVerified)
            {
                if (stroke.CanvasVersion != expectedReplayVersion || hash != expectedReplayHash)
                    Fail("Replayed canvas differs from the host's pre-join canvas fingerprint.");
                else report.ReplayVerified = true;
                report.ReplayVersion = stroke.CanvasVersion;
                report.ReplayHash = hash;
                WriteReport();
            }
        }
        private void OnChat(ChatLine line)
        {
            report.ReceivedChats++;
            if (line.Text == "__draw_smoke_replay__" && manager.State != null
                && manager.State.Players.Any(player => player.Id == line.PlayerId && player.IsSpectator)) replayAcknowledged = true;
        }

        private void Fail(string error)
        {
            if (errors.Contains(error)) return;
            errors.Add(error);
            Debug.LogError("DrawLiar network smoke: " + error);
            WriteReport();
        }

        private void Finish()
        {
            finishing = true;
            if (online)
            {
                if (cleanupTask == null) cleanupTask = CleanupOnlineAsync();
                if (!cleanupTask.IsCompleted) return;
            }
            report.Outcome = errors.Count == 0 ? "PASS" : "FAIL";
            if (!WriteReport()) return;
            done = true;
            Debug.Log("DrawLiar network smoke " + report.Outcome + " — " + reportPath);
        }

        private async Task CleanupOnlineAsync()
        {
            string[] joinedLobbies = Array.Empty<string>();
            if (wasHost)
            {
                try { joinedLobbies = (await LobbyService.Instance.GetJoinedLobbiesAsync()).ToArray(); }
                catch (Exception exception) { Fail("Could not record hosted lobby before cleanup: " + exception.Message); }
            }
            try
            {
                if (lobbyService != null)
                {
                    while (lobbyService.IsBusy) await Task.Delay(50);
                    await lobbyService.LeaveAsync();
                    report.ServicesCleaned = !lobbyService.IsOnlineRoom && !lobbyService.IsBusy;
                }
                else report.ServicesCleaned = string.IsNullOrEmpty(report.RoomCode);
                if (wasHost)
                {
                    bool deleted = joinedLobbies.Length == 1;
                    foreach (string id in joinedLobbies)
                    {
                        try { await LobbyService.Instance.GetLobbyAsync(id); deleted = false; }
                        catch (LobbyServiceException exception) when (exception.Reason == LobbyExceptionReason.LobbyNotFound) { }
                    }
                    report.LobbyDeletionVerified = deleted;
                    if (!deleted) Fail("Host lobby deletion was not confirmed by the Lobby service.");
                }
                if (!report.ServicesCleaned) Fail("Online lobby cleanup did not finish.");
            }
            catch (Exception exception) { Fail("Online smoke cleanup failed: " + exception.Message); }
            WriteReport();
        }

        private bool WriteReport()
        {
            report.Errors = errors.ToArray();
            report.Phases = phases.OrderBy(value => value).ToArray();
            report.ElapsedSeconds = Time.unscaledTime - began;
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                Directory.CreateDirectory(folder);
                string temporary = reportPath + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
                if (File.Exists(reportPath)) File.Replace(temporary, reportPath, null);
                else File.Move(temporary, reportPath);
                return true;
            }
            catch (IOException) { return false; }
        }

        private void OnDestroy()
        {
            if (manager == null) return;
            manager.StateChanged -= OnState;
            manager.CanvasCleared -= OnCanvasCleared;
            manager.StrokeReceived -= OnStroke;
            manager.ChatReceived -= OnChat;
        }
    }
}
#endif
