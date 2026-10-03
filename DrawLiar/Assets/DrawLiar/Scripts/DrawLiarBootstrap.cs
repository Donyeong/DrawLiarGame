using UnityEngine;

namespace DrawLiar
{
    public static class DrawLiarBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Start()
        {
#if UNITY_SERVER
            return;
#else
            if(Object.FindFirstObjectByType<DrawApp>()!=null)return;
            if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!="DrawLiar")return;
            Application.runInBackground=true;
            Application.targetFrameRate=60;
            if(Application.isMobilePlatform)
            {
                Screen.autorotateToPortrait=true;Screen.autorotateToPortraitUpsideDown=false;
                Screen.autorotateToLandscapeLeft=true;Screen.autorotateToLandscapeRight=true;
                Screen.orientation=ScreenOrientation.AutoRotation;
            }
            var camera=Camera.main;
            if(camera==null)camera=new GameObject("UI Camera",typeof(Camera),typeof(AudioListener)).GetComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color32(246,243,251,255);
            var root=new GameObject("DrawLiar");root.SetActive(false);
            root.AddComponent<DrawNetworkManager>();
            root.AddComponent<LobbyServiceBridge>();
            root.AddComponent<DrawAudio>();
            root.AddComponent<DrawApp>();
            root.SetActive(true);
#endif
        }
    }
}
