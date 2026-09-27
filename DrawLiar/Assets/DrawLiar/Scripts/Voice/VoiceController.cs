using System;
using System.Collections.Generic;
using Dissonance;
using Dissonance.Integrations.MirrorIgnorance;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DrawLiar
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class VoiceController : MonoBehaviour
    {
        const string RoomName = "DrawLiar.Table";
        readonly List<string> microphones = new List<string>();
        DissonanceComms comms;
        VoiceBroadcastTrigger broadcast;
        VoiceReceiptTrigger receipt;
        string preferencePrefix;
        bool pushToTalk;
        bool muted;
        bool typing;
        bool initialized;
        float nextDeviceCheck;
        float connectedAt;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal bool? SmokePushToTalk { get; set; }
#endif

        public string LocalVoiceId => comms == null ? "" : comms.LocalPlayerName;
        public bool IsConnected => comms != null && comms.IsNetworkInitialized;
        public bool HasMicrophone => microphones.Count > 0 &&
            (string.IsNullOrEmpty(MicrophoneName) || microphones.Contains(MicrophoneName));
        public IReadOnlyList<string> MicrophoneDevices => microphones;
        public bool IsLocalSpeaking => IsConnected && HasMicrophone && !comms.IsMuted && !TransmissionBlocked &&
            comms.MicrophoneCapture != null && comms.MicrophoneCapture.IsRecording && broadcast.IsTransmitting;

        public bool PushToTalk
        {
            get => pushToTalk;
            set
            {
                pushToTalk = value;
                SaveBool("pushToTalk", value);
                ApplyTransmissionState();
            }
        }

        public bool Muted
        {
            get => muted;
            set
            {
                muted = value;
                SaveBool("muted", value);
                ApplyTransmissionState();
            }
        }

        public string MicrophoneName
        {
            get => comms == null ? "" : comms.MicrophoneName ?? "";
            set
            {
                var device = value ?? "";
                if (comms == null || comms.MicrophoneName == device)
                    return;
                comms.MicrophoneName = device;
                PlayerPrefs.SetString(preferencePrefix + "microphone", device);
                PlayerPrefs.Save();
                connectedAt = Time.unscaledTime;
                ApplyTransmissionState();
            }
        }

        public string Status
        {
            get
            {
                if (!HasMicrophone)
                    return microphones.Count == 0 ? "마이크가 없습니다. 음성 듣기는 가능합니다." : "선택한 마이크를 찾을 수 없습니다.";
                if (Muted)
                    return "내 마이크가 꺼져 있습니다.";
                if (!IsConnected)
                    return "방에 입장하면 음성을 사용할 수 있습니다.";
                if (Time.unscaledTime - connectedAt > 5 &&
                    (comms.MicrophoneCapture == null || !comms.MicrophoneCapture.IsRecording))
                    return "마이크 입력을 시작하지 못했습니다. 장치와 Windows 마이크 권한을 확인해 주세요.";
                if (IsLocalSpeaking)
                    return "말하는 중";
                return PushToTalk ? "T를 누르는 동안 말하기" : "목소리를 감지하면 자동으로 말하기";
            }
        }

        bool TransmissionBlocked => muted || typing || HasInputFieldFocus() || !HasMicrophone || !IsConnected;

        void Awake() => Initialize();

        public void Initialize()
        {
            if (initialized)
                return;
            Application.runInBackground = true;
            preferencePrefix = "DrawLiar.Voice." + GetVoiceProfile() + ".";
            pushToTalk = PlayerPrefs.GetInt(preferencePrefix + "pushToTalk", 1) != 0;
            muted = PlayerPrefs.GetInt(preferencePrefix + "muted", 0) != 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            DrawVoiceSmoke.TryAttach(gameObject);
#endif
            if (GetComponent<MirrorIgnoranceCommsNetwork>() == null)
                gameObject.AddComponent<MirrorIgnoranceCommsNetwork>();
            comms = GetComponent<DissonanceComms>();
            if (comms == null)
                comms = gameObject.AddComponent<DissonanceComms>();
            var identity = PlayerPrefs.GetString(preferencePrefix + "id", "");
            if (string.IsNullOrEmpty(identity))
            {
                identity = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(preferencePrefix + "id", identity);
                PlayerPrefs.Save();
            }
            comms.LocalPlayerName = identity;
            comms.MicrophoneName = PlayerPrefs.GetString(preferencePrefix + "microphone", "");
            comms.IsMuted = true;
            comms.OnPlayerJoinedSession += ApplyPeerSettings;

            receipt = gameObject.AddComponent<VoiceReceiptTrigger>();
            receipt.RoomName = RoomName;
            receipt.UseColliderTrigger = false;
            broadcast = gameObject.AddComponent<VoiceBroadcastTrigger>();
            broadcast.ChannelType = CommTriggerTarget.Room;
            broadcast.RoomName = RoomName;
            broadcast.BroadcastPosition = false;
            broadcast.UseColliderTrigger = false;
            broadcast.Mode = CommActivationMode.None;
            RefreshMicrophones();
            initialized = true;
        }

        void Update()
        {
            if (Time.unscaledTime >= nextDeviceCheck)
                RefreshMicrophones();
            if (!IsConnected)
                connectedAt = Time.unscaledTime;
            ApplyTransmissionState();
        }

        void OnEnable()
        {
            if (receipt != null)
                receipt.enabled = true;
            if (broadcast != null)
                broadcast.enabled = true;
        }

        void OnDisable()
        {
            if (comms != null)
                comms.IsMuted = true;
            if (broadcast != null)
                broadcast.enabled = false;
            if (receipt != null)
                receipt.enabled = false;
        }

        void OnDestroy()
        {
            if (comms != null)
                comms.OnPlayerJoinedSession -= ApplyPeerSettings;
        }

        public void SetTyping(bool isTyping)
        {
            typing = isTyping;
            ApplyTransmissionState();
        }

        public void RefreshMicrophones()
        {
            microphones.Clear();
            if (comms != null)
                comms.GetMicrophoneDevices(microphones);
            nextDeviceCheck = Time.unscaledTime + 3;
        }

        public bool IsSpeaking(string voiceId)
        {
            if (string.IsNullOrEmpty(voiceId) || comms == null)
                return false;
            if (voiceId == LocalVoiceId)
                return IsLocalSpeaking;
            var player = comms.FindPlayer(voiceId);
            return player != null && player.IsConnected && player.IsSpeaking;
        }

        public float GetPeerVolume(string voiceId)
        {
            return string.IsNullOrEmpty(voiceId) ? 1 : PlayerPrefs.GetFloat(PeerKey(voiceId, "volume"), 1);
        }

        public void SetPeerVolume(string voiceId, float volume)
        {
            if (string.IsNullOrEmpty(voiceId) || float.IsNaN(volume) || float.IsInfinity(volume))
                return;
            PlayerPrefs.SetFloat(PeerKey(voiceId, "volume"), Mathf.Clamp01(volume));
            ApplyPeerSettings(comms == null ? null : comms.FindPlayer(voiceId));
            PlayerPrefs.Save();
        }

        public bool GetPeerMuted(string voiceId)
        {
            return !string.IsNullOrEmpty(voiceId) && PlayerPrefs.GetInt(PeerKey(voiceId, "muted"), 0) != 0;
        }

        public void SetPeerMuted(string voiceId, bool isMuted)
        {
            if (string.IsNullOrEmpty(voiceId))
                return;
            PlayerPrefs.SetInt(PeerKey(voiceId, "muted"), isMuted ? 1 : 0);
            ApplyPeerSettings(comms == null ? null : comms.FindPlayer(voiceId));
            PlayerPrefs.Save();
        }

        void ApplyPeerSettings(VoicePlayerState player)
        {
            if (player == null || player.IsLocalPlayer || !player.IsConnected)
                return;
            player.Volume = Mathf.Clamp01(GetPeerVolume(player.Name));
            player.IsLocallyMuted = GetPeerMuted(player.Name);
        }

        void ApplyTransmissionState()
        {
            if (comms == null || broadcast == null)
                return;
            var blocked = !isActiveAndEnabled || TransmissionBlocked;
            var pressed = Application.isFocused && Keyboard.current != null && Keyboard.current.tKey.isPressed;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            pressed = SmokePushToTalk ?? pressed;
#endif
            comms.IsMuted = blocked || (pushToTalk && !pressed);
            // Dissonance's PushToTalk mode reads the legacy Input Manager.
            broadcast.Mode = blocked ? CommActivationMode.None :
                pushToTalk ? (pressed ? CommActivationMode.Open : CommActivationMode.None) : CommActivationMode.VoiceActivation;
        }

        static bool HasInputFieldFocus()
        {
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            if (selected == null)
                return false;
            var input = selected.GetComponent<InputField>();
            if (input != null && input.isFocused)
                return true;
            var tmp = selected.GetComponent<TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

        string PeerKey(string voiceId, string setting) => preferencePrefix + "peer." + voiceId + "." + setting;

        void SaveBool(string setting, bool value)
        {
            PlayerPrefs.SetInt(preferencePrefix + setting, value ? 1 : 0);
            PlayerPrefs.Save();
        }

        static string GetVoiceProfile()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-voiceProfile")
                    return args[i + 1];
            return "default";
        }
    }
}
