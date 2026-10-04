#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace DrawLiar.Editor
{
    public static class DrawWebBuild
    {
        private const string UNITY_VERSION = "6000.3.20f1";
        private const string SCENE_PATH = "Assets/DrawLiar/Scenes/DrawLiar.unity";
        private const string TEMPLATE = "PROJECT:RascalLab";
        private const string GENERATED_ASSETS = "Assets/__DrawLiarWebBuild";

        [Serializable]
        private sealed class Manifest
        {
            public string loaderUrl;
            public string dataUrl;
            public string frameworkUrl;
            public string codeUrl;
            public string streamingAssetsUrl = "StreamingAssets";
            public string companyName = "RascalLab";
            public string productName = "LiarCanvas";
            public string productVersion;
            public string unityVersion = UNITY_VERSION;
            public string buildTimeUtc;
        }

        public static void Build()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("웹 빌드는 별도 작업 폴더의 batchmode에서 실행해야 합니다.");

            int exitCode = 1;
            try
            {
                if (Application.unityVersion != UNITY_VERSION)
                    throw new InvalidOperationException("Unity " + UNITY_VERSION + "로 빌드해야 합니다.");
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                    throw new InvalidOperationException("Unity 실행 인수에 -buildTarget WebGL을 지정하세요.");
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                    throw new InvalidOperationException("Web Build Support 모듈을 설치하세요.");
                if (!File.Exists(SCENE_PATH))
                    throw new FileNotFoundException("게임 씬을 찾을 수 없습니다.", SCENE_PATH);

                string output = Path.GetFullPath(GetArgument("-buildPath") ?? "Builds/WebGL");
                if (File.Exists(Path.Combine(output, "build.json")))
                    File.Delete(Path.Combine(output, "build.json"));
                Directory.CreateDirectory(output);
                SettingsSnapshot snapshot = new SettingsSnapshot();
                SceneSetup[] scenes = EditorSceneManager.GetSceneManagerSetup();
                bool generatedAssetsCreated = false;
                BuildReport report;
                try
                {
                    BuildProfile.SetActiveBuildProfile(null);
                    PlayerSettings.companyName = "RascalLab";
                    PlayerSettings.productName = "LiarCanvas";
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.WebGL, "com.rascallab.drawliar");
                    PlayerSettings.WebGL.template = TEMPLATE;
                    PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                    PlayerSettings.WebGL.decompressionFallback = false;
                    PlayerSettings.WebGL.threadsSupport = false;
                    if (AssetDatabase.IsValidFolder(GENERATED_ASSETS))
                        throw new InvalidOperationException("웹 빌드 임시 에셋 폴더가 이미 있습니다.");
                    AssetDatabase.CreateFolder("Assets", "__DrawLiarWebBuild");
                    generatedAssetsCreated = true;
                    string webScene = CreateWebScene();
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { webScene },
                        locationPathName = output,
                        target = BuildTarget.WebGL,
                        options = BuildOptions.None
                    });
                }
                finally
                {
                    snapshot.Restore();
                    AssetDatabase.SaveAssets();
                    try
                    {
                        if (scenes.Any(scene => scene.isLoaded && scene.isActive))
                            EditorSceneManager.RestoreSceneManagerSetup(scenes);
                        else
                            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    }
                    finally
                    {
                        if (generatedAssetsCreated && AssetDatabase.IsValidFolder(GENERATED_ASSETS))
                            AssetDatabase.DeleteAsset(GENERATED_ASSETS);
                    }
                }
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("웹 빌드에 실패했습니다: " + report.summary.result);

                Manifest manifest = new Manifest
                {
                    loaderUrl = FindArtifact(output, "*.loader.js"),
                    dataUrl = FindArtifact(output, "*.data.gz"),
                    frameworkUrl = FindArtifact(output, "*.framework.js.gz"),
                    codeUrl = FindArtifact(output, "*.wasm.gz"),
                    productVersion = PlayerSettings.bundleVersion,
                    buildTimeUtc = DateTime.UtcNow.ToString("O")
                };
                File.Copy("Assets/DrawLiar/Resources/DrawLiar/Fonts/OFL.txt", Path.Combine(output, "Barlow-OFL.txt"), true);
                File.Copy("Assets/DrawLiar/Resources/DrawLiar/Pretendard-LICENSE.txt", Path.Combine(output, "Pretendard-LICENSE.txt"), true);
                File.Copy("Assets/DrawLiar/WebFonts/NotoCJK-OFL.txt", Path.Combine(output, "NotoCJK-OFL.txt"), true);
                File.Copy("Assets/DrawLiar/WebFonts/Noto-OFL.txt", Path.Combine(output, "Noto-OFL.txt"), true);
                File.WriteAllText(Path.Combine(output, "build.json"), JsonUtility.ToJson(manifest, true));
                Debug.Log("DRAWLIAR_WEB_BUILD_RESULT Succeeded bytes=" + report.summary.totalSize
                    + " warnings=" + report.summary.totalWarnings + " errors=" + report.summary.totalErrors);
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogError("DRAWLIAR_WEB_BUILD_RESULT Failed: " + exception.Message);
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static string CreateWebScene()
        {
            FontAsset primary = CreateFontAsset("Assets/DrawLiar/Resources/DrawLiar/Pretendard-Regular.otf");
            primary.fallbackFontAssetTable = new List<FontAsset>
            {
                CreateFontAsset("Assets/DrawLiar/WebFonts/NotoSansCJKsc-Regular.otf"),
                CreateFontAsset("Assets/DrawLiar/WebFonts/NotoSansDevanagari-Regular.ttf"),
                CreateFontAsset("Assets/DrawLiar/WebFonts/NotoSansArabic-Regular.ttf")
            };
            EditorUtility.SetDirty(primary);
            PanelSettings panel = CreatePanelSettings();
            var scene = EditorSceneManager.OpenScene(SCENE_PATH, OpenSceneMode.Single);
            var provider = new GameObject("Web Typography").AddComponent<DrawWebTypography>();
            provider.Configure(primary, panel);
            string path = GENERATED_ASSETS + "/DrawLiar.unity";
            if (!EditorSceneManager.SaveScene(scene, path))
                throw new IOException("웹 빌드 씬을 저장할 수 없습니다.");
            AssetDatabase.SaveAssets();
            return path;
        }

        private static PanelSettings CreatePanelSettings()
        {
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = "DrawLiarBrowserPanel";
            var serialized = new SerializedObject(panel);
            var icu = serialized.FindProperty("m_ICUDataAsset");
            if (icu == null || icu.objectReferenceValue == null)
                throw new InvalidOperationException("웹 UI 패널에 ICU 데이터가 연결되지 않았습니다.");
            AssetDatabase.CreateAsset(panel, GENERATED_ASSETS + "/DrawLiarBrowserPanel.asset");
            Debug.Log("DRAWLIAR_WEB_ICU_ASSET " + AssetDatabase.GetAssetPath(icu.objectReferenceValue));
            return panel;
        }

        private static FontAsset CreateFontAsset(string path)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(path);
            if (font == null) throw new FileNotFoundException("웹 폰트를 찾을 수 없습니다.", path);
            var asset = FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, true);
            if (asset == null) throw new InvalidOperationException("웹 폰트 에셋을 만들 수 없습니다: " + path);
            asset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, GENERATED_ASSETS + "/" + asset.name + ".asset");
            if (asset.material != null) AssetDatabase.AddObjectToAsset(asset.material, asset);
            foreach (var texture in asset.atlasTextures)
                if (texture != null) AssetDatabase.AddObjectToAsset(texture, asset);
            return asset;
        }

        private static string FindArtifact(string output, string pattern)
        {
            string path = Directory.GetFiles(Path.Combine(output, "Build"), pattern).Single();
            if (new FileInfo(path).Length == 0)
                throw new IOException("웹 빌드 파일이 비어 있습니다: " + Path.GetFileName(path));
            return "Build/" + Path.GetFileName(path);
        }

        private static string GetArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(arguments, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }

        private sealed class SettingsSnapshot
        {
            private readonly BuildProfile _profile = BuildProfile.GetActiveBuildProfile();
            private readonly string _company = PlayerSettings.companyName;
            private readonly string _product = PlayerSettings.productName;
            private readonly string _applicationId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.WebGL);
            private readonly string _template = PlayerSettings.WebGL.template;
            private readonly WebGLCompressionFormat _compression = PlayerSettings.WebGL.compressionFormat;
            private readonly bool _fallback = PlayerSettings.WebGL.decompressionFallback;
            private readonly bool _threads = PlayerSettings.WebGL.threadsSupport;

            public void Restore()
            {
                PlayerSettings.companyName = _company;
                PlayerSettings.productName = _product;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.WebGL, _applicationId);
                PlayerSettings.WebGL.template = _template;
                PlayerSettings.WebGL.compressionFormat = _compression;
                PlayerSettings.WebGL.decompressionFallback = _fallback;
                PlayerSettings.WebGL.threadsSupport = _threads;
                BuildProfile.SetActiveBuildProfile(_profile);
            }
        }
    }
}
#endif
