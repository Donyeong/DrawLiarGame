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
    public static class DrawWindowsBuild
    {
        private const string UNITY_VERSION = "6000.3.20f1";
        private const string SCENE_PATH = "Assets/DrawLiar/Scenes/DrawLiar.unity";

        [Serializable]
        public sealed class ResultData
        {
            public string RunId;
            public string Result;
            public string OutputPath;
            public string BundleVersion;
            public int BuildNumber;
            public bool Development;
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
                throw new InvalidOperationException("Windows 자동 빌드는 별도 작업 폴더의 batchmode에서 실행해야 합니다.");

            ResultData result = new ResultData
            {
                RunId = GetArgument("-buildRunId"),
                Result = "InProgress",
                StartedUtc = DateTime.UtcNow.ToString("O")
            };
            string resultPath = null;
            int exitCode = 1;
            try
            {
                resultPath = Path.GetFullPath(RequiredArgument("-resultLogPath"));
                result.OutputPath = Path.GetFullPath(RequiredArgument("-buildPath"));
                result.BundleVersion = RequiredArgument("-bundleVersion");
                if (!Guid.TryParse(result.RunId, out _))
                    throw new InvalidOperationException("유효한 buildRunId가 필요합니다.");
                if (!int.TryParse(GetArgument("-buildNumber"), out result.BuildNumber) || result.BuildNumber < 1)
                    throw new InvalidOperationException("buildNumber는 양의 정수여야 합니다.");
                if (!bool.TryParse(GetArgument("-development") ?? "false", out result.Development))
                    throw new InvalidOperationException("development는 true 또는 false여야 합니다.");
                if (!string.Equals(Path.GetExtension(result.OutputPath), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows 빌드 경로는 .exe여야 합니다.");
                if (string.Equals(resultPath, result.OutputPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("빌드 파일과 결과 파일의 경로는 달라야 합니다.");
                WriteResult(resultPath, result);

                BuildReport report = BuildPlayer(result);
                result.Warnings = report.summary.totalWarnings;
                result.Errors = report.summary.totalErrors;
                result.TotalBytes = (long)report.summary.totalSize;
                result.DurationSeconds = report.summary.totalTime.TotalSeconds;
                if (report.summary.result != BuildResult.Succeeded || result.Errors != 0)
                    throw new InvalidOperationException("Windows 빌드에 실패했습니다: " + report.summary.result);
                string directory = Path.GetDirectoryName(result.OutputPath);
                foreach (string path in new[] { result.OutputPath, Path.Combine(directory, "UnityPlayer.dll"),
                    Path.Combine(directory, Path.GetFileNameWithoutExtension(result.OutputPath) + "_Data", "globalgamemanagers") })
                {
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        throw new IOException("Windows 빌드 파일이 생성되지 않았습니다: " + path);
                }
                File.Copy("Assets/DrawLiar/Resources/DrawLiar/Fonts/OFL.txt", Path.Combine(directory, "Barlow-OFL.txt"), true);
                File.Copy("Assets/DrawLiar/Resources/DrawLiar/Pretendard-LICENSE.txt", Path.Combine(directory, "Pretendard-LICENSE.txt"), true);
                result.Result = "Succeeded";
                result.CompletedUtc = DateTime.UtcNow.ToString("O");
                WriteResult(resultPath, result);
                Debug.Log("DRAWLIAR_WINDOWS_BUILD_RESULT Succeeded version=" + result.BundleVersion
                    + " build=" + result.BuildNumber + " bytes=" + result.TotalBytes
                    + " warnings=" + result.Warnings + " errors=" + result.Errors);
                exitCode = 0;
            }
            catch (Exception exception)
            {
                result.Result = "Failed";
                result.CompletedUtc = DateTime.UtcNow.ToString("O");
                result.Message = exception.Message;
                if (resultPath != null) WriteResult(resultPath, result);
                Debug.LogError("DRAWLIAR_WINDOWS_BUILD_RESULT Failed: " + result.Message);
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
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                throw new InvalidOperationException("Unity 실행 인수에 -buildTarget Win64를 지정하세요.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                throw new InvalidOperationException("Windows Build Support 모듈이 필요합니다.");
            if (!File.Exists(SCENE_PATH))
                throw new FileNotFoundException("게임 씬을 찾을 수 없습니다.", SCENE_PATH);

            BuildProfile profile = BuildProfile.GetActiveBuildProfile();
            string version = PlayerSettings.bundleVersion;
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            StandaloneBuildSubtarget subtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            try
            {
                BuildProfile.SetActiveBuildProfile(null);
                PlayerSettings.bundleVersion = result.BundleVersion;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
                Directory.CreateDirectory(Path.GetDirectoryName(result.OutputPath));
                return BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { SCENE_PATH },
                    locationPathName = result.OutputPath,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = result.Development ? BuildOptions.Development : BuildOptions.None
                });
            }
            finally
            {
                PlayerSettings.bundleVersion = version;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
                EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
                BuildProfile.SetActiveBuildProfile(profile);
                AssetDatabase.SaveAssets();
            }
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

        private static void WriteResult(string path, ResultData result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(result, true));
        }
    }
}
#endif
