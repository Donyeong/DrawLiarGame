#if UNITY_ANDROID
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEditor.Android;

namespace DrawLiar.Editor
{
    public sealed class DrawAdvertisingAndroidBuild : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 120;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var config = DrawAdvertisingConfig.Load();
            bool enabled = config != null && config.IsAndroidConfigured;
            if (config != null && config.Enabled && !enabled)
                throw new InvalidOperationException("광고를 활성화하려면 유효한 Android AdMob App ID가 필요합니다.");
            string propertiesPath = Path.Combine(Path.GetDirectoryName(path), "gradle.properties");
            string properties = File.Exists(propertiesPath) ? File.ReadAllText(propertiesPath) : "";
            properties = Regex.Replace(properties, @"(?m)^drawliarAdvertisingEnabled=.*\r?\n?", "");
            File.WriteAllText(propertiesPath, properties.TrimEnd() + "\ndrawliarAdvertisingEnabled=" + (enabled ? "true" : "false") + "\n");
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var manifest = new XmlDocument();
            manifest.Load(manifestPath);
            var application = manifest.DocumentElement?.SelectSingleNode("application") as XmlElement;
            if (application == null) throw new InvalidOperationException("Android application 설정을 찾지 못했습니다.");
            const string ANDROID = "http://schemas.android.com/apk/res/android";
            const string TOOLS = "http://schemas.android.com/tools";
            foreach (XmlNode child in application.SelectNodes("meta-data"))
                if (child is XmlElement element && element.GetAttribute("name", ANDROID) == "com.google.android.gms.ads.APPLICATION_ID")
                    application.RemoveChild(child);
            foreach (XmlNode child in application.SelectNodes("provider"))
                if (child is XmlElement element && element.GetAttribute("name", ANDROID) == "com.google.android.gms.ads.MobileAdsInitProvider")
                    application.RemoveChild(child);
            if (enabled)
            {
                var element = manifest.CreateElement("meta-data");
                element.SetAttribute("name", ANDROID, "com.google.android.gms.ads.APPLICATION_ID");
                element.SetAttribute("value", ANDROID, config.AndroidAppId);
                application.AppendChild(element);
                var provider = manifest.CreateElement("provider");
                provider.SetAttribute("name", ANDROID, "com.google.android.gms.ads.MobileAdsInitProvider");
                provider.SetAttribute("node", TOOLS, "remove");
                application.AppendChild(provider);
            }
            manifest.Save(manifestPath);
        }
    }
}
#endif
