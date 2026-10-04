using System.Reflection;
using UnityEditor;

namespace DrawLiar.Editor
{
    [InitializeOnLoad]
    internal static class DrawTypographySetup
    {
        static DrawTypographySetup()
        {
            EditorApplication.delayCall += EnableAdvancedText;
        }

        private static void EnableAdvancedText()
        {
            // Unity 6.3의 프로젝트 설정 API는 internal이므로 에디터에서만 접근한다.
            var settingsType = typeof(EditorWindow).Assembly.GetType("UnityEditor.UIElements.UIToolkitProjectSettings");
            if (settingsType == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    settingsType = assembly.GetType("UnityEditor.UIElements.UIToolkitProjectSettings");
                    if (settingsType != null) break;
                }
            }
            var enabled = settingsType?.GetProperty("enableAdvancedText", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (enabled != null && !(bool)enabled.GetValue(null)) enabled.SetValue(null, true);
        }
    }
}
