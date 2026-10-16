using System.Text.RegularExpressions;
using UnityEngine;

namespace DrawLiar
{
    [CreateAssetMenu(menuName = "DrawLiar/Advertising")]
    public sealed class DrawAdvertisingConfig : ScriptableObject
    {
        [SerializeField] private bool _enabled = false;
        [SerializeField] private string _androidAppId = "";
        [SerializeField] private bool _webConsentRequired = true;

        public bool Enabled => _enabled;
        public string AndroidAppId => _androidAppId;
        public bool WebConsentRequired => _webConsentRequired;
        public bool IsAndroidConfigured => _enabled && Regex.IsMatch(_androidAppId ?? "", @"^ca-app-pub-\d{16}~\d{10}$");

        public static DrawAdvertisingConfig Load() => Resources.Load<DrawAdvertisingConfig>("DrawAdvertisingConfig");
    }
}
