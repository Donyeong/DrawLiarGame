using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class DrawLiarEditor
    {
        [MenuItem("DrawLiar/Prepare Game Scene")]
        public static void Prepare()
        {
            const string path = "Assets/DrawLiar/Scenes/DrawLiar.unity";
            Directory.CreateDirectory("Assets/DrawLiar/Scenes");
            if (!File.Exists(path))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.SaveScene(scene, path);
                EditorSceneManager.CloseScene(scene, true);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
            PlayerSettings.productName = "DrawLiar";
            PlayerSettings.companyName = "DrawLiar";
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("DrawLiar/Build Windows")]
        public static void Build() => BuildPlayer("Builds/Windows", BuildOptions.Development);

        [MenuItem("DrawLiar/Build Windows Release")]
        public static void BuildRelease() => BuildPlayer("Builds/WindowsRelease", BuildOptions.None);

        private static void BuildPlayer(string directory, BuildOptions options)
        {
            Prepare();
            Directory.CreateDirectory(directory);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/DrawLiar/Scenes/DrawLiar.unity" },
                locationPathName = Path.Combine(directory, "DrawLiar.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = options
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Build failed: " + report.summary.result);
            File.Copy("Assets/DrawLiar/Resources/DrawLiar/Fonts/OFL.txt", Path.Combine(directory, "Barlow-OFL.txt"), true);
            var services = Resources.Load<OnlineServicesConfig>("OnlineServicesConfig");
            string steamAppIdPath = Path.Combine(directory, "steam_appid.txt");
            bool steamTestBuild = (options & BuildOptions.Development) != 0 || services != null && services.SteamAppId == 480;
            if (services != null && services.SteamAppId != 0 && steamTestBuild)
                File.WriteAllText(steamAppIdPath, services.SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (File.Exists(steamAppIdPath)) File.Delete(steamAppIdPath);
            Debug.Log("DRAWLIAR_BUILD_OK " + directory + " " + report.summary.totalSize);
        }

        [MenuItem("DrawLiar/Run Game Checks")]
        public static void RunChecks()
        {
            GameSelfCheck.Run();
            GameDataSelfCheck.Run();
            ServicesChecks.Run();
            VoicePacketChecks.Run();
        }

        [MenuItem("DrawLiar/Capture Game Screen")]
        public static void Capture()
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode to capture the game.");
            Directory.CreateDirectory("../.codex-temp");
            ScreenCapture.CaptureScreenshot(Path.GetFullPath("../.codex-temp/game-screen.png"));
        }
    }
}
