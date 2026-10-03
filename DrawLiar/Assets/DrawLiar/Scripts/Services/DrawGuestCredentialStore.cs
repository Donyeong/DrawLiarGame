using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace DrawLiar
{
    [Serializable]
    public sealed class GuestCredentials
    {
        public string GuestId;
        public string GuestSecret;
    }

    public static class DrawGuestCredentialStore
    {
        private const string STORAGE_ERROR = "게스트 로그인 정보를 안전하게 저장하거나 읽을 수 없습니다.";
        private static readonly object STORAGE_LOCK = new object();

        public static bool IsSupported
        {
            get
            {
#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR) || (UNITY_ANDROID && !UNITY_EDITOR))
                return true;
#else
                return false;
#endif
            }
        }

        public static GuestCredentials GetOrCreate()
        {
            lock (STORAGE_LOCK)
            {
                RequireSupported();
                try
                {
                    string payload = Read();
                    if (!string.IsNullOrEmpty(payload)) return Decode(payload);
                    var secret = new byte[32];
                    try
                    {
                        using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                        var credentials = new GuestCredentials { GuestId = Guid.NewGuid().ToString("D"), GuestSecret = Encode(secret) };
                        return Decode(Write(JsonUtility.ToJson(credentials), true));
                    }
                    finally { Array.Clear(secret, 0, secret.Length); }
                }
                catch { throw new InvalidOperationException(STORAGE_ERROR); }
            }
        }

        public static bool TryLoad(out GuestCredentials credentials)
        {
            lock (STORAGE_LOCK)
            {
                credentials = null;
                RequireSupported();
                try
                {
                    string payload = Read();
                    if (string.IsNullOrEmpty(payload)) return false;
                    credentials = Decode(payload);
                    return true;
                }
                catch { throw new InvalidOperationException(STORAGE_ERROR); }
            }
        }

        public static void Save(GuestCredentials credentials)
        {
            lock (STORAGE_LOCK)
            {
                RequireSupported();
                try { Validate(credentials); Write(JsonUtility.ToJson(credentials), false); }
                catch { throw new InvalidOperationException(STORAGE_ERROR); }
            }
        }

        public static void Delete()
        {
            lock (STORAGE_LOCK)
            {
                RequireSupported();
                try
                {
#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
                    WindowsGuestCredentials.Delete(DirectoryPath);
#elif UNITY_ANDROID && !UNITY_EDITOR && !UNITY_SERVER
                    CallAndroid("Delete", null);
#endif
                }
                catch { throw new InvalidOperationException(STORAGE_ERROR); }
            }
        }

        private static string Read()
        {
#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
            return WindowsGuestCredentials.Load(DirectoryPath);
#elif UNITY_ANDROID && !UNITY_EDITOR && !UNITY_SERVER
            return CallAndroid("Load", null);
#else
            throw new InvalidOperationException(STORAGE_ERROR);
#endif
        }

        private static string Write(string payload, bool onlyIfMissing)
        {
#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
            return WindowsGuestCredentials.Save(DirectoryPath, payload, onlyIfMissing);
#elif UNITY_ANDROID && !UNITY_EDITOR && !UNITY_SERVER
            return CallAndroid(onlyIfMissing ? "SaveIfMissing" : "Save", payload);
#else
            throw new InvalidOperationException(STORAGE_ERROR);
#endif
        }

        private static GuestCredentials Decode(string payload)
        {
            if (payload.Length > 4096) throw new InvalidOperationException(STORAGE_ERROR);
            var credentials = JsonUtility.FromJson<GuestCredentials>(payload);
            Validate(credentials);
            return credentials;
        }

        private static void Validate(GuestCredentials credentials)
        {
            if (credentials == null || !Guid.TryParseExact(credentials.GuestId, "D", out var id) || id == Guid.Empty
                || id.ToString("D") != credentials.GuestId || credentials.GuestSecret == null || credentials.GuestSecret.Length != 43)
                throw new InvalidOperationException(STORAGE_ERROR);
            byte[] secret = null;
            try
            {
                secret = Convert.FromBase64String(credentials.GuestSecret.Replace('-', '+').Replace('_', '/') + "=");
                if (secret.Length != 32 || Encode(secret) != credentials.GuestSecret) throw new InvalidOperationException(STORAGE_ERROR);
            }
            finally { if (secret != null) Array.Clear(secret, 0, secret.Length); }
        }

        private static string Encode(byte[] secret) => Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static void RequireSupported()
        {
            if (!IsSupported) throw new InvalidOperationException("이 플랫폼에서는 게스트 로그인을 지원하지 않습니다.");
        }

#if !UNITY_SERVER && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR))
        private static string DirectoryPath => Path.Combine(Application.persistentDataPath, "GuestCredentials");
#endif

#if UNITY_ANDROID && !UNITY_EDITOR && !UNITY_SERVER
        private static string CallAndroid(string method, string payload)
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var store = new AndroidJavaClass("com.rascallab.drawliar.accounts.DrawGuestCredentials");
            if (method == "Delete") { store.CallStatic(method, activity); return null; }
            return payload == null ? store.CallStatic<string>(method, activity) : store.CallStatic<string>(method, activity, payload);
        }
#endif
    }
}
