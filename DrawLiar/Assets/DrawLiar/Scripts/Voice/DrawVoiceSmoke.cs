#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Audio.Playback;
using NAudio.Wave;
using Mirror;
using Unity.Services.Lobbies;
using UnityEngine;

namespace DrawLiar
{
    // Opt-in synthetic capture: the normal Dissonance encoder, transport and playback remain in use.
    public sealed class DrawVoiceSmoke : MonoBehaviour, IMicrophoneCapture, IMicrophoneDeviceList
    {
        [Serializable]
        private sealed class Command
        {
            public int Sequence, Sender;
            public string Stage = "Waiting";
            public bool Talk = false, Typing = false, Muted = false, PeerMuted = false, Finish = false;
            public float PeerVolume = 1;
        }

        [Serializable]
        private sealed class Report
        {
            public int Player, Sequence, Measurements, LocalSpeakingFrames, PeerSpeakingFrames, CapturedFrames;
            public string Stage = "Waiting", VoiceId = "", PeerVoiceId = "", Error = "";
            public bool HostReady, Connected, PeerConnected, ProfileMapped, CommsMuted, PeerMuted, Finished, ListenerMuted;
            public float PeerVolume, Rms, Peak, SourceRms, MeasurementSeconds, DrainSeconds;
            public int OutputSampleRate, DecodedCallbacks;
            public long DecodedSamples;
            public string RoomCode = "", Transport = "", Encryption = "", LobbyStatus = "";
            public bool Online, RelayPathVerified, ServicesCleaned, LobbyDeletionVerified;
        }

        private const int SampleRate = 48000, FrameSize = 960;
        private readonly float[] frame = new float[FrameSize], output = new float[2048];
        private readonly List<IMicrophoneSubscriber> subscribers = new List<IMicrophoneSubscriber>();
        private readonly WaveFormat format = new WaveFormat(SampleRate, 1);
        private readonly Report report = new Report();
        private VoiceController voice;
        private DissonanceComms comms;
        private DrawNetworkManager network;
        private LobbyServiceBridge lobbyService;
        private Task cleanupTask;
        private bool wasHost;
        private VoicePlayback playback;
        private DrawVoicePlaybackProbe probe;
        private Command command = new Command { Sequence = -1, Sender = -1 };
        private string commandPath, reportPath, previousCommand = "";
        private double nextFrame, energy;
        private long sampleIndex, measuredSamples;
        private double decodedStartEnergy;
        private long decodedStartSamples;
        private int decodedStartCallbacks;
        private bool decodedStarted;
        private float nextPoll, nextReport, stageBegan;
        private float measurementAt = -1;
        private bool savedPushToTalk, savedMuted, restored;
        private string savedMicrophone;
        private float savedPeerVolume;
        private bool savedPeerMuted, savedPeer;
        private string savedPeerId;

        public bool IsRecording { get; private set; }
        public string Device => IsRecording ? "DrawLiar synthetic voice check" : null;
        public TimeSpan Latency => TimeSpan.FromMilliseconds(20);

        internal static void TryAttach(GameObject target)
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-drawVoiceSmoke") < 0) return;
            if (Application.isEditor) throw new InvalidOperationException("Voice smoke runs only in standalone development players.");
            if (!Argument("-voiceProfile").StartsWith("voice-smoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Voice smoke requires an isolated -voiceProfile voice-smoke-... value.");
            if (target.GetComponent<DrawVoiceSmoke>() == null) target.AddComponent<DrawVoiceSmoke>();
        }

        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
        }

        private void Start()
        {
            voice = GetComponent<VoiceController>();
            comms = GetComponent<DissonanceComms>();
            comms.OnPlayerJoinedSession += AttachProbe;
            network = GetComponent<DrawNetworkManager>();
            lobbyService = GetComponent<LobbyServiceBridge>();
            wasHost = Array.IndexOf(Environment.GetCommandLineArgs(), "-drawOnlineHost") >= 0;
            report.Online = wasHost || !string.IsNullOrEmpty(Argument("-drawOnlineJoin"));
            commandPath = Argument("-drawVoiceCommand");
            reportPath = Argument("-drawVoiceReport");
            int.TryParse(Argument("-drawVoicePlayer"), out report.Player);
            savedPushToTalk = voice.PushToTalk;
            savedMuted = voice.Muted;
            savedMicrophone = voice.MicrophoneName;
            AudioListener.volume = 0;
            report.ListenerMuted = true;
            voice.SmokePushToTalk = false;
            voice.MicrophoneName = "";
            voice.RefreshMicrophones();
            voice.PushToTalk = true;
            voice.Muted = false;
            report.OutputSampleRate = AudioSettings.outputSampleRate;
        }

        private void Update()
        {
            if (voice == null || report.Finished) return;
            try
            {
                if (Time.unscaledTime >= nextPoll)
                {
                    nextPoll = Time.unscaledTime + .1f;
                    ReadCommand();
                }
                if (command.Finish)
                {
                    Restore();
                    if (cleanupTask == null) cleanupTask = CleanupAsync();
                    if (!cleanupTask.IsCompleted) { WriteReport(); return; }
                    report.Finished = true;
                    if (WriteReport()) Application.Quit();
                    else report.Finished = false;
                    return;
                }
                if (!string.IsNullOrEmpty(report.Error)) return;

                bool sender = command.Sender == report.Player;
                voice.SmokePushToTalk = sender && command.Talk;
                voice.SetTyping(sender && command.Typing);
                if (voice.Muted != (sender && command.Muted)) voice.Muted = sender && command.Muted;

                report.HostReady = network.State != null;
                report.Connected = voice.IsConnected;
                report.VoiceId = voice.LocalVoiceId;
                report.CommsMuted = comms.IsMuted;
                report.Transport = network.transport?.GetType().Name ?? "";
                report.Encryption = network.transport?.EncryptionCipher ?? "";
                report.LobbyStatus = lobbyService?.Status ?? "";
                if (!string.IsNullOrEmpty(lobbyService?.RoomCode)) report.RoomCode = lobbyService.RoomCode;
                report.RelayPathVerified = report.Online && report.Connected && lobbyService != null && lobbyService.IsOnlineRoom
                    && network.transport is RelayMirrorTransport && ReferenceEquals(Mirror.Transport.active, network.transport)
                    && network.transport.IsEncrypted && report.Encryption == "DTLS";
                VoicePlayerState peer = null;
                foreach (var player in comms.Players)
                    if (!player.IsLocalPlayer) { peer = player; break; }
                report.PeerConnected = peer != null && peer.IsConnected;
                report.PeerVoiceId = peer?.Name ?? "";
                report.ProfileMapped = false;
                if (network.State != null && peer != null)
                    foreach (var player in network.State.Players)
                        if (player.Id != network.State.LocalPlayerId && player.VoiceId == peer.Name)
                            report.ProfileMapped = true;

                if (peer != null && peer.IsConnected)
                {
                    if (!savedPeer)
                    {
                        savedPeer = true; savedPeerId = peer.Name;
                        savedPeerVolume = voice.GetPeerVolume(peer.Name);
                        savedPeerMuted = voice.GetPeerMuted(peer.Name);
                    }
                    float volume = sender ? 1 : command.PeerVolume;
                    bool muted = !sender && command.PeerMuted;
                    if (voice.GetPeerVolume(peer.Name) != volume) voice.SetPeerVolume(peer.Name, volume);
                    if (voice.GetPeerMuted(peer.Name) != muted) voice.SetPeerMuted(peer.Name, muted);
                    report.PeerVolume = peer.Volume;
                    report.PeerMuted = peer.IsLocallyMuted;
                    if (playback == null || playback.PlayerName != peer.Name)
                        foreach (var candidate in comms.GetComponentsInChildren<VoicePlayback>(true))
                            if (candidate.PlayerName == peer.Name) { playback = candidate; break; }
                }

                // A closed sender may leave a receive session alive for Dissonance's 1.5s timeout.
                // For gate checks, first observe both sessions end, then measure a fresh silence window.
                if (measurementAt < 0 && Time.unscaledTime - stageBegan >= 1.5f)
                {
                    bool gate = !command.Talk || command.Typing || command.Muted;
                    if (!gate || (!voice.IsLocalSpeaking && (peer == null || !peer.IsSpeaking)))
                    {
                        measurementAt = Time.unscaledTime;
                        report.DrainSeconds = measurementAt - stageBegan;
                    }
                    else if (Time.unscaledTime - stageBegan > 4)
                        throw new InvalidOperationException("Voice transmission did not finish within the bounded drain interval.");
                }
                float elapsed = measurementAt < 0 ? -1 : Time.unscaledTime - measurementAt;
                if (command.Sequence >= 0 && elapsed > 0 && playback != null)
                {
                    playback.AudioSource.GetOutputData(output, 0);
                    foreach (float sample in output)
                    {
                        energy += sample * sample;
                        report.Peak = Mathf.Max(report.Peak, Mathf.Abs(sample));
                    }
                    measuredSamples += output.Length;
                    report.SourceRms = (float)Math.Sqrt(energy / measuredSamples);
                    if (probe != null)
                    {
                        if (!decodedStarted)
                        {
                            decodedStartEnergy = probe.Energy;
                            decodedStartSamples = probe.Samples;
                            decodedStartCallbacks = probe.Callbacks;
                            decodedStarted = true;
                        }
                        report.DecodedSamples = probe.Samples - decodedStartSamples;
                        report.DecodedCallbacks = probe.Callbacks - decodedStartCallbacks;
                        report.Rms = report.DecodedSamples > 0
                            ? (float)Math.Sqrt(Math.Max(0, probe.Energy - decodedStartEnergy) / report.DecodedSamples) : 0;
                        if (report.DecodedCallbacks > 0) report.Peak = Mathf.Max(report.Peak, probe.LastPeak);
                    }
                    report.Measurements++;
                    if (voice.IsLocalSpeaking) report.LocalSpeakingFrames++;
                    if (peer != null && voice.IsSpeaking(peer.Name)) report.PeerSpeakingFrames++;
                    report.MeasurementSeconds = elapsed;
                }
                if (Time.unscaledTime >= nextReport)
                {
                    nextReport = Time.unscaledTime + .25f;
                    WriteReport();
                }
            }
            catch (Exception exception)
            {
                report.Error = exception.ToString();
                Restore();
                Debug.LogError("DRAWLIAR_VOICE_SMOKE_FAIL " + exception);
                WriteReport();
            }
        }

        private void ReadCommand()
        {
            if (!File.Exists(commandPath)) return;
            string json;
            try
            {
                using (var stream = new FileStream(commandPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
            }
            catch (IOException) { return; }
            if (json == previousCommand) return;
            var next = JsonUtility.FromJson<Command>(json);
            if (next == null || next.PeerVolume < 0 || next.PeerVolume > 1) throw new InvalidDataException("Invalid voice smoke command.");
            command = next;
            previousCommand = json;
            stageBegan = Time.unscaledTime;
            measurementAt = -1;
            energy = 0; measuredSamples = 0;
            decodedStarted = false;
            report.Sequence = next.Sequence; report.Stage = next.Stage;
            report.Rms = report.Peak = report.SourceRms = report.MeasurementSeconds = report.DrainSeconds = 0;
            report.Measurements = report.LocalSpeakingFrames = report.PeerSpeakingFrames = 0;
            report.DecodedSamples = 0; report.DecodedCallbacks = 0;
        }

        private void AttachProbe(VoicePlayerState player)
        {
            if (player.IsLocalPlayer || !(player.Playback is VoicePlayback remote)) return;
            playback = remote;
            probe = remote.GetComponent<DrawVoicePlaybackProbe>();
            if (probe == null) probe = remote.gameObject.AddComponent<DrawVoicePlaybackProbe>();
        }

        private async Task CleanupAsync()
        {
            string[] hostedLobbies = Array.Empty<string>();
            try
            {
                if (report.Online)
                {
                    while (lobbyService.IsBusy) await Task.Delay(50);
                    if (wasHost)
                    {
                        try { hostedLobbies = (await LobbyService.Instance.GetJoinedLobbiesAsync()).ToArray(); }
                        catch (Exception exception) { report.Error += "Cannot identify hosted lobby: " + exception.Message; }
                    }
                    await lobbyService.LeaveAsync();
                    report.ServicesCleaned = !lobbyService.IsOnlineRoom && !lobbyService.IsBusy
                        && !NetworkClient.active && !NetworkServer.active
                        && !network.transport.ClientConnected() && !network.transport.ServerActive();
                    if (wasHost)
                    {
                        bool deleted = hostedLobbies.Length == 1;
                        foreach (string id in hostedLobbies)
                        {
                            try { await LobbyService.Instance.GetLobbyAsync(id); deleted = false; }
                            catch (LobbyServiceException exception) when (exception.Reason == LobbyExceptionReason.LobbyNotFound) { }
                        }
                        report.LobbyDeletionVerified = deleted;
                        if (!deleted) report.Error += " Hosted lobby deletion was not confirmed.";
                    }
                }
                else
                {
                    network.Leave();
                    report.ServicesCleaned = !NetworkClient.active && !NetworkServer.active;
                }
                if (!report.ServicesCleaned) report.Error += " Voice smoke services did not finish cleanup.";
            }
            catch (Exception exception) { report.Error += " Voice smoke cleanup failed: " + exception.Message; }
        }

        private bool WriteReport()
        {
            if (string.IsNullOrEmpty(reportPath)) return false;
            try
            {
                string temporary = reportPath + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
                if (File.Exists(reportPath)) File.Replace(temporary, reportPath, null);
                else File.Move(temporary, reportPath);
                return true;
            }
            catch (IOException) { return false; } // An inspector may hold the report open; the next snapshot retries.
        }

        private void Restore()
        {
            if (restored || voice == null) return;
            restored = true;
            voice.SmokePushToTalk = false;
            voice.SetTyping(false);
            voice.Muted = savedMuted;
            voice.PushToTalk = savedPushToTalk;
            voice.MicrophoneName = savedMicrophone;
            comms.IsMuted = true;
            if (savedPeer)
            {
                voice.SetPeerVolume(savedPeerId, savedPeerVolume);
                voice.SetPeerMuted(savedPeerId, savedPeerMuted);
            }
        }

        private void OnApplicationQuit() => Restore();
        private void OnDestroy() { if (comms != null) comms.OnPlayerJoinedSession -= AttachProbe; }

        public WaveFormat StartCapture(string name)
        {
            IsRecording = true;
            nextFrame = Time.realtimeSinceStartupAsDouble;
            sampleIndex = 0;
            foreach (var subscriber in subscribers) subscriber.Reset();
            return format;
        }

        public void StopCapture() => IsRecording = false;
        public void Subscribe(IMicrophoneSubscriber subscriber) { if (!subscribers.Contains(subscriber)) subscribers.Add(subscriber); }
        public bool Unsubscribe(IMicrophoneSubscriber subscriber) => subscribers.Remove(subscriber);
        public void GetDevices(List<string> devices) => devices.Add("DrawLiar synthetic voice check");

        public bool UpdateSubscribers()
        {
            if (!IsRecording) return false;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - nextFrame > .2) nextFrame = now;
            while (nextFrame <= now)
            {
                for (int i = 0; i < frame.Length; i++, sampleIndex++)
                {
                    double t = sampleIndex / (double)SampleRate;
                    // Quiet voiced harmonics vary slowly, avoiding a stationary noise-floor-only input.
                    double phase = 2 * Math.PI * (report.Player == 0 ? 220 : 330) * t + .2 * Math.Sin(2 * Math.PI * 3 * t);
                    frame[i] = (float)(.006 * Math.Sin(phase) + .003 * Math.Sin(phase * 2) + .001 * Math.Sin(phase * 3));
                }
                var samples = new ArraySegment<float>(frame);
                foreach (var subscriber in subscribers) subscriber.ReceiveMicrophoneData(samples, format);
                report.CapturedFrames++;
                nextFrame += .02;
            }
            return false;
        }
    }
}
#endif
