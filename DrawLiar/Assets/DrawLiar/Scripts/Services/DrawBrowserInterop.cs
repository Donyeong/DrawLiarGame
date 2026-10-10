#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace DrawLiar
{
    internal static class DrawBrowserInterop
    {
        [DllImport("__Internal")] private static extern string DrawBrowserServerUrl(string name, int websocket, int development);
        [DllImport("__Internal")] private static extern int DrawBrowserRandom(byte[] buffer, int length);
        [DllImport("__Internal")] private static extern string DrawBrowserGuestLoad();
        [DllImport("__Internal")] private static extern string DrawBrowserGuestSave(string payload, int onlyIfMissing);
        [DllImport("__Internal")] private static extern int DrawBrowserGuestDelete();
        [DllImport("__Internal")] private static extern string DrawBrowserTopicsLoad();
        [DllImport("__Internal")] private static extern int DrawBrowserTopicsSave(string payload);
        [DllImport("__Internal")] private static extern int DrawBrowserIsMobile();
        [DllImport("__Internal")] private static extern string DrawBrowserRoomPageUrl();
        [DllImport("__Internal")] private static extern string DrawBrowserRoomInvite();
        [DllImport("__Internal")] private static extern void DrawBrowserRoomInviteClear();
        [DllImport("__Internal")] internal static extern int DrawBrowserClipboardStart(string value, int read);
        [DllImport("__Internal")] internal static extern int DrawBrowserClipboardState(int id);
        [DllImport("__Internal")] internal static extern string DrawBrowserClipboardValue(int id);
        [DllImport("__Internal")] internal static extern void DrawBrowserClipboardRelease(int id);
        [DllImport("__Internal")] internal static extern int DrawBrowserGoogleStart(string clientId, string nonce, string closeLabel);
        [DllImport("__Internal")] internal static extern int DrawBrowserGoogleState(int id);
        [DllImport("__Internal")] internal static extern string DrawBrowserGoogleCredential(int id);
        [DllImport("__Internal")] internal static extern void DrawBrowserGoogleCancel(int id);
        [DllImport("__Internal")] internal static extern void DrawBrowserInputConfigure(string configuration);
        [DllImport("__Internal")] internal static extern int DrawBrowserInputActive();
        [DllImport("__Internal")] internal static extern int DrawBrowserKeyboardMetrics(float[] metrics);
        [DllImport("__Internal")] internal static extern int DrawBrowserInputTakeChatShortcut();
        [DllImport("__Internal")] internal static extern int DrawBrowserInputOpen(int id);
        [DllImport("__Internal")] internal static extern int DrawBrowserInputFocused(int id);
        [DllImport("__Internal")] internal static extern int DrawBrowserInputState(int id);
        [DllImport("__Internal")] internal static extern string DrawBrowserInputValue(int id);
        [DllImport("__Internal")] internal static extern void DrawBrowserInputSetValue(int id, string value);
        [DllImport("__Internal")] internal static extern void DrawBrowserInputClose(int id, int focusCanvas);
        [DllImport("__Internal")] internal static extern void DrawBrowserInputShutdown();
        [DllImport("__Internal")] internal static extern void DrawBrowserAudioInit();
        [DllImport("__Internal")] internal static extern int DrawBrowserPenInitialize();
        [DllImport("__Internal")] internal static extern int DrawBrowserPenRead(float[] samples, int maximumSamples);
        [DllImport("__Internal")] internal static extern int DrawBrowserPenSuppress(float x, float y);
        [DllImport("__Internal")] internal static extern void DrawBrowserPenDiscard();

        public static string ServerUrl(string name, bool websocket)
        {
#if DEVELOPMENT_BUILD
            const int DEVELOPMENT = 1;
#else
            const int DEVELOPMENT = 0;
#endif
            string value = DrawBrowserServerUrl(name, websocket ? 1 : 0, DEVELOPMENT);
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("서버 주소가 올바르지 않습니다.");
            return value;
        }

        public static void FillRandom(byte[] buffer)
        {
            if (DrawBrowserRandom(buffer, buffer.Length) != 1) throw new InvalidOperationException();
        }

        public static string LoadGuestCredentials()
        {
            string value = DrawBrowserGuestLoad();
            if (value == null) throw new InvalidOperationException();
            return value;
        }

        public static string SaveGuestCredentials(string payload, bool onlyIfMissing)
        {
            string value = DrawBrowserGuestSave(payload, onlyIfMissing ? 1 : 0);
            if (string.IsNullOrEmpty(value)) throw new InvalidOperationException();
            return value;
        }

        public static void DeleteGuestCredentials()
        {
            if (DrawBrowserGuestDelete() != 1) throw new InvalidOperationException();
        }

        public static bool IsMobile => DrawBrowserIsMobile() == 1;

        public static string RoomPageUrl() => DrawBrowserRoomPageUrl() ?? "";
        public static string RoomInvite() => DrawBrowserRoomInvite() ?? "";
        public static void ClearRoomInvite() => DrawBrowserRoomInviteClear();

        public static string LoadCustomTopics()
        {
            string value = DrawBrowserTopicsLoad();
            if (value == null) throw new System.IO.IOException("커스텀 주제를 불러올 수 없어요.");
            return value;
        }

        public static void SaveCustomTopics(string value)
        {
            if (DrawBrowserTopicsSave(value) != 1) throw new System.IO.IOException("요청을 처리하지 못했습니다. 다시 시도하세요.");
        }
    }
}
#endif
