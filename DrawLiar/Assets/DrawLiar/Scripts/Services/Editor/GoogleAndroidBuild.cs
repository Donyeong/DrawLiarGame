#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor.Android;

namespace DrawLiar.Editor
{
    public sealed class GoogleAndroidBuild : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string gradlePath = Path.Combine(path, "build.gradle");
            string gradle = File.ReadAllText(gradlePath);
            const string marker = "implementation 'com.google.android.libraries.identity.googleid:googleid:1.2.1'";
            if (!gradle.Contains(marker))
            {
                int dependencies = gradle.IndexOf("dependencies {", StringComparison.Ordinal);
                if (dependencies < 0) throw new InvalidOperationException("Android 의존성 설정을 찾지 못했습니다.");
                int insertion = dependencies + "dependencies {".Length;
                gradle = gradle.Insert(insertion, "\n    implementation 'androidx.credentials:credentials:1.6.0'"
                    + "\n    implementation 'androidx.credentials:credentials-play-services-auth:1.6.0'\n    " + marker + "\n");
                File.WriteAllText(gradlePath, gradle);
            }
            string propertiesPath = Path.Combine(Path.GetDirectoryName(path), "gradle.properties");
            string properties = File.Exists(propertiesPath) ? File.ReadAllText(propertiesPath) : "";
            if (!properties.Contains("android.useAndroidX=true")) File.AppendAllText(propertiesPath, "\nandroid.useAndroidX=true\n");
        }
    }
}
#endif
