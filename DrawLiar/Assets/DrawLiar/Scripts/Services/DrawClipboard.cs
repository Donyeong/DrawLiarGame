using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    internal static class DrawClipboard
    {
        public static Task CopyAsync(string value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return DrawBrowserRuntime.ClipboardAsync(value ?? "", false);
#else
            GUIUtility.systemCopyBuffer = value ?? "";
            return Task.CompletedTask;
#endif
        }

        public static Task<string> ReadAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return DrawBrowserRuntime.ClipboardAsync("", true);
#else
            return Task.FromResult(GUIUtility.systemCopyBuffer);
#endif
        }
    }
}
