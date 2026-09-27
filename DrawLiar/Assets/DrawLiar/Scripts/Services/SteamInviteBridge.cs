using System;
using UnityEngine;
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
using Steamworks;
#endif

namespace DrawLiar
{
    [DisallowMultipleComponent]
    public sealed class SteamInviteBridge : MonoBehaviour
    {
        public bool Available { get; private set; }
        public string Status { get; private set; } = "Steam App ID가 설정되지 않았습니다.";
        public string PendingInvitation { get; private set; } = "";
        public event Action<string> InvitationReceived;
        public event Action Changed;
        bool initialized;
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
        Callback<GameRichPresenceJoinRequested_t> invitation;
#endif

        public void Initialize(OnlineServicesConfig config = null)
        {
            if (initialized) return;
            initialized = true;
            if (config == null) config = Resources.Load<OnlineServicesConfig>("OnlineServicesConfig");
            PendingInvitation = ParseInvitation(string.Join(" ", Environment.GetCommandLineArgs()));
            if (config == null || config.SteamAppId == 0) { Changed?.Invoke(); return; }
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
            try
            {
                var result = SteamAPI.InitEx(out string error);
                if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    Status = "Steam 연결 실패. Steam에서 게임을 실행하세요. " + error;
                    Changed?.Invoke();
                    return;
                }
                if (SteamUtils.GetAppID().m_AppId != config.SteamAppId)
                {
                    SteamAPI.Shutdown();
                    Status = "실행 중인 Steam App ID와 게임 설정이 다릅니다.";
                    Changed?.Invoke();
                    return;
                }
                Available = true;
                invitation = Callback<GameRichPresenceJoinRequested_t>.Create(value => ReceiveInvitation(value.m_rgchConnect));
                SteamApps.GetLaunchCommandLine(out string command, 1024);
                string code = ParseInvitation(command);
                if (code.Length > 0) PendingInvitation = code;
                Status = "Steam 친구 초대를 사용할 수 있습니다.";
            }
            catch (Exception exception)
            {
                Available = false;
                Status = "Steam 초기화 실패: " + exception.Message;
            }
#else
            Status = "이 플랫폼에서는 Steam 친구 초대를 지원하지 않습니다.";
#endif
            Changed?.Invoke();
        }

        public bool InviteFriends(string roomCode)
        {
            if (!Available) { Changed?.Invoke(); return false; }
            string code;
            try { code = LobbyServiceBridge.NormalizeCode(roomCode); }
            catch (Exception exception) { Status = exception.Message; Changed?.Invoke(); return false; }
            if (code.Length == 0) { Status = "온라인 방을 만든 뒤 친구를 초대하세요."; Changed?.Invoke(); return false; }
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
            if (!SteamUtils.IsOverlayEnabled())
            {
                Status = "Steam 오버레이를 켜고 Steam에서 게임을 실행하세요.";
                Changed?.Invoke();
                return false;
            }
            string connection = "--join-code " + code;
            SteamFriends.SetRichPresence("connect", connection);
            SteamFriends.ActivateGameOverlayInviteDialogConnectString(connection);
            Status = "Steam 오버레이에서 초대할 친구를 선택하세요.";
            Changed?.Invoke();
            return true;
#else
            return false;
#endif
        }

        public void ClearRoom()
        {
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
            if (Available) SteamFriends.ClearRichPresence();
#endif
        }

        public string ConsumePendingInvitation()
        {
            string code = PendingInvitation;
            PendingInvitation = "";
            return code;
        }

        void ReceiveInvitation(string connection)
        {
            string code = ParseInvitation(connection);
            if (code.Length == 0) return;
            PendingInvitation = code;
            InvitationReceived?.Invoke(code);
            Changed?.Invoke();
        }

        public static string ParseInvitation(string connection)
        {
            if (string.IsNullOrWhiteSpace(connection)) return "";
            var args = connection.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--join-code") continue;
                try { return LobbyServiceBridge.NormalizeCode(args[i + 1]); }
                catch (InvalidOperationException) { return ""; }
            }
            return "";
        }

        void Update()
        {
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
            if (Available) SteamAPI.RunCallbacks();
#endif
        }

        void OnDestroy()
        {
#if !DISABLESTEAMWORKS && (UNITY_STANDALONE || UNITY_EDITOR)
            if (Available)
            {
                SteamFriends.ClearRichPresence();
                invitation?.Dispose();
                SteamAPI.Shutdown();
            }
#endif
            Available = false;
        }
    }
}
