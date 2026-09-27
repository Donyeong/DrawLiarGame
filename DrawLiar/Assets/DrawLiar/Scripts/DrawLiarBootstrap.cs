using Mirror;
using kcp2k;
using UnityEngine;

namespace DrawLiar
{
    public static class DrawLiarBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Start()
        {
            if(Object.FindFirstObjectByType<DrawApp>()!=null)return;
            if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!="DrawLiar")return;
            Application.runInBackground=true;
            Application.targetFrameRate=60;
            var camera=Camera.main;
            if(camera==null)camera=new GameObject("UI Camera",typeof(Camera),typeof(AudioListener)).GetComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color32(246,243,251,255);
            var root=new GameObject("DrawLiar");root.SetActive(false);
            var transport=root.AddComponent<KcpTransport>();
            var manager=root.AddComponent<DrawNetworkManager>();
            manager.transport=transport;manager.autoCreatePlayer=false;manager.maxConnections=12;
            root.AddComponent<VoiceController>();
            root.AddComponent<LobbyServiceBridge>();
            root.AddComponent<SteamInviteBridge>();
            root.AddComponent<DrawApp>();
            root.SetActive(true);
        }
    }
}
