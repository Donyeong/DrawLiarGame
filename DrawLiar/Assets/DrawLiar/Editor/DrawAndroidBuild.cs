#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class DrawAndroidBuild
    {
        private const string UNITY_VERSION = "6000.3.20f1";
        private const string SCENE_PATH = "Assets/DrawLiar/Scenes/DrawLiar.unity";
        private const string APPLICATION_ID = "com.rascallab.liargame";
        private const long MIN_FREE_DISK_BYTES = 10L * 1024 * 1024 * 1024;

        [Serializable]
        public sealed class ResultData
        {
            public string RunId;
            public string Result;
            public string OutputPath;
            public string BundleVersion;
            public int VersionCode;
            public bool Development;
            public string Format;
            public long TotalBytes;
            public int Warnings;
            public int Errors;
            public double DurationSeconds;
            public string StartedUtc;
            public string CompletedUtc;
            public string Message;
        }

        public static void Build()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Android 자동 빌드는 batchmode에서 실행해야 합니다.");

            ResultData result = new ResultData
            {
                RunId = GetArgument("-buildRunId"),
                Result = "InProgress",
                StartedUtc = DateTime.UtcNow.ToString("O"),
                BundleVersion = GetArgument("-bundleVersion") ?? "0.1.0",
                Format = (GetArgument("-buildFormat") ?? "APK").ToUpperInvariant()
            };
            string resultPath = null;
            int exitCode = 1;
            try
            {
                resultPath = Path.GetFullPath(RequiredArgument("-resultLogPath"));
                result.OutputPath = Path.GetFullPath(RequiredArgument("-buildPath"));
                if (!Guid.TryParse(result.RunId, out _))
                    throw new InvalidOperationException("유효한 buildRunId가 필요합니다.");
                if (!int.TryParse(GetArgument("-buildNumber"), out result.VersionCode) || result.VersionCode < 1)
                    throw new InvalidOperationException("buildNumber는 양의 정수여야 합니다.");
                if (!bool.TryParse(GetArgument("-development") ?? "false", out result.Development))
                    throw new InvalidOperationException("development는 true 또는 false여야 합니다.");
                if (string.IsNullOrWhiteSpace(result.BundleVersion))
                    throw new InvalidOperationException("bundleVersion이 비어 있습니다.");
                if (result.Format != "APK" && result.Format != "AAB")
                    throw new InvalidOperationException("buildFormat은 APK 또는 AAB여야 합니다.");
                if (!string.Equals(Path.GetExtension(result.OutputPath), "." + result.Format, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("빌드 파일 확장자가 buildFormat과 일치하지 않습니다.");
                if (string.Equals(resultPath, result.OutputPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("빌드 파일과 결과 파일의 경로는 달라야 합니다.");
                WriteResult(resultPath, result);

                BuildReport report = BuildPlayer(result);
                result.Warnings = report.summary.totalWarnings;
                result.Errors = report.summary.totalErrors;
                result.DurationSeconds = report.summary.totalTime.TotalSeconds;
                result.Result = report.summary.result.ToString();
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Android 빌드에 실패했습니다: " + report.summary.result);
                FileInfo artifact = new FileInfo(result.OutputPath);
                if (!artifact.Exists || artifact.Length == 0)
                    throw new IOException("Android 빌드 파일이 생성되지 않았습니다.");
                result.TotalBytes = artifact.Length;
                result.CompletedUtc = DateTime.UtcNow.ToString("O");
                WriteResult(resultPath, result);
                Debug.Log("DRAWLIAR_ANDROID_BUILD_RESULT Succeeded format=" + result.Format
                    + " version=" + result.BundleVersion + " code=" + result.VersionCode
                    + " bytes=" + result.TotalBytes + " warnings=" + result.Warnings + " errors=" + result.Errors);
                exitCode = 0;
            }
            catch (Exception exception)
            {
                result.Result = "Failed";
                result.CompletedUtc = DateTime.UtcNow.ToString("O");
                result.Message = RedactSecrets(exception.Message);
                if (resultPath != null) WriteResult(resultPath, result);
                Debug.LogError("DRAWLIAR_ANDROID_BUILD_RESULT Failed: " + result.Message);
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static BuildReport BuildPlayer(ResultData result)
        {
            if (Application.unityVersion != UNITY_VERSION)
                throw new InvalidOperationException("Unity " + UNITY_VERSION + "로 빌드해야 합니다.");
            if (!File.Exists(SCENE_PATH))
                throw new FileNotFoundException("게임 씬을 찾을 수 없습니다.", SCENE_PATH);
            EnsureFreeDiskSpace(Application.dataPath);
            EnsureFreeDiskSpace(result.OutputPath);

            SettingsSnapshot settings = new SettingsSnapshot();
            try
            {
                BuildProfile.SetActiveBuildProfile(null);
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                    !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                    throw new InvalidOperationException("Android 빌드 타깃으로 전환할 수 없습니다.");
                ConfigureAndroidSourcePlugin("Assets/Plugins/Android/DrawGuestCredentials.java");
                ConfigureAndroidSourcePlugin("Assets/Plugins/Android/DrawGoogleAccount.java");

                PlayerSettings.companyName = "RascalLab";
                PlayerSettings.productName = "DrawLiar";
                PlayerSettings.bundleVersion = result.BundleVersion;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APPLICATION_ID);
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
                PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
                PlayerSettings.Android.bundleVersionCode = result.VersionCode;
                EditorUserBuildSettings.buildAppBundle = result.Format == "AAB";
                ApplySigning();

                Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath));
                // 이전 산출물을 지워 이번 실행의 파일만 성공 결과로 인정합니다.
                if (File.Exists(result.OutputPath)) File.Delete(result.OutputPath);
                return BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { SCENE_PATH },
                    locationPathName = result.OutputPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = result.Development ? BuildOptions.Development : BuildOptions.None
                });
            }
            finally
            {
                settings.Restore();
                AssetDatabase.SaveAssets();
            }
        }

        private static void ConfigureAndroidSourcePlugin(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Android 로그인 플러그인이 없습니다.", path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            if (!(AssetImporter.GetAtPath(path) is PluginImporter importer))
                throw new InvalidOperationException("Android 로그인 플러그인을 가져올 수 없습니다: " + path);
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
            importer.SaveAndReimport();
            importer = AssetImporter.GetAtPath(path) as PluginImporter;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (importer == null || guid.Length != 32 || importer.GetCompatibleWithAnyPlatform()
                || importer.GetCompatibleWithEditor() || !importer.GetCompatibleWithPlatform(BuildTarget.Android))
                throw new InvalidOperationException("Android 로그인 플러그인이 빌드에 포함되지 않습니다: " + path);
            Debug.Log("DRAWLIAR_ANDROID_PLUGIN_READY path=" + path + " Android=true");
        }

        private static void ApplySigning()
        {
            string path = Environment.GetEnvironmentVariable("DRAWLIAR_KEYSTORE_FILE");
            if (string.IsNullOrWhiteSpace(path))
            {
                PlayerSettings.Android.useCustomKeystore = false;
                return;
            }
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Android 서명 키 파일을 찾을 수 없습니다.");
            string storePass = Environment.GetEnvironmentVariable("DRAWLIAR_KEYSTORE_PASS");
            string keyAlias = Environment.GetEnvironmentVariable("DRAWLIAR_KEY_ALIAS");
            string aliasPass = Environment.GetEnvironmentVariable("DRAWLIAR_KEY_ALIAS_PASS");
            if (string.IsNullOrEmpty(storePass) || string.IsNullOrEmpty(keyAlias) || string.IsNullOrEmpty(aliasPass))
                throw new InvalidOperationException("Android 서명 환경 변수 설정이 불완전합니다.");
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = path;
            PlayerSettings.Android.keystorePass = storePass;
            PlayerSettings.Android.keyaliasName = keyAlias;
            PlayerSettings.Android.keyaliasPass = aliasPass;
        }

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static string RequiredArgument(string name)
        {
            string value = GetArgument(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("필수 인수가 없습니다: " + name);
            return value;
        }

        private static void EnsureFreeDiskSpace(string path)
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
            if (drive.AvailableFreeSpace < MIN_FREE_DISK_BYTES)
                throw new IOException("Android 빌드에는 디스크 여유 공간이 10GB 이상 필요합니다.");
        }

        private static void WriteResult(string path, ResultData result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(result, true));
        }

        private static string RedactSecrets(string message)
        {
            foreach (string name in new[] { "DRAWLIAR_KEYSTORE_PASS", "DRAWLIAR_KEY_ALIAS_PASS" })
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) message = message.Replace(value, "***");
            }
            return message;
        }

        private sealed class SettingsSnapshot
        {
            private readonly BuildProfile _profile = BuildProfile.GetActiveBuildProfile();
            private readonly string _company = PlayerSettings.companyName;
            private readonly string _product = PlayerSettings.productName;
            private readonly string _version = PlayerSettings.bundleVersion;
            private readonly string _applicationId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            private readonly ScriptingImplementation _backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
            private readonly AndroidArchitecture _architectures = PlayerSettings.Android.targetArchitectures;
            private readonly AndroidSdkVersions _minSdk = PlayerSettings.Android.minSdkVersion;
            private readonly AndroidSdkVersions _targetSdk = PlayerSettings.Android.targetSdkVersion;
            private readonly int _versionCode = PlayerSettings.Android.bundleVersionCode;
            private readonly bool _appBundle = EditorUserBuildSettings.buildAppBundle;
            private readonly bool _customKeystore = PlayerSettings.Android.useCustomKeystore;
            private readonly string _keystoreName = PlayerSettings.Android.keystoreName;
            private readonly string _keystorePass = PlayerSettings.Android.keystorePass;
            private readonly string _keyaliasName = PlayerSettings.Android.keyaliasName;
            private readonly string _keyaliasPass = PlayerSettings.Android.keyaliasPass;

            public void Restore()
            {
                PlayerSettings.companyName = _company;
                PlayerSettings.productName = _product;
                PlayerSettings.bundleVersion = _version;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, _applicationId);
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, _backend);
                PlayerSettings.Android.targetArchitectures = _architectures;
                PlayerSettings.Android.minSdkVersion = _minSdk;
                PlayerSettings.Android.targetSdkVersion = _targetSdk;
                PlayerSettings.Android.bundleVersionCode = _versionCode;
                EditorUserBuildSettings.buildAppBundle = _appBundle;
                PlayerSettings.Android.useCustomKeystore = _customKeystore;
                PlayerSettings.Android.keystoreName = _keystoreName;
                PlayerSettings.Android.keystorePass = _keystorePass;
                PlayerSettings.Android.keyaliasName = _keyaliasName;
                PlayerSettings.Android.keyaliasPass = _keyaliasPass;
                BuildProfile.SetActiveBuildProfile(_profile);
            }
        }
    }
}
#endif
