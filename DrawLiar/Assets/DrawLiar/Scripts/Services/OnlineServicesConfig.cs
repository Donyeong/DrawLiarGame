using System;
using UnityEngine;
using UnityEngine.Networking;

namespace DrawLiar
{
    [CreateAssetMenu(menuName = "DrawLiar/Server Connection")]
    public sealed class OnlineServicesConfig : ScriptableObject
    {
        private static OnlineServicesConfig _loaded;
        [SerializeField] private string _mainServerUrl = "https://34.158.195.192:19050";
        [SerializeField] private string _certificateSha256 = "";
        [SerializeField] private bool _allowLocalDevelopmentServer;
        public string MainServerUrl => _mainServerUrl;
        public string CertificateSha256 => _certificateSha256;

        public static OnlineServicesConfig Load()
        {
            if (_loaded != null) return _loaded;
            var resource = Resources.Load<OnlineServicesConfig>("OnlineServicesConfig");
            _loaded = resource != null ? Instantiate(resource) : CreateInstance<OnlineServicesConfig>();
            return _loaded;
        }
        public void ValidateServerUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("서버 주소가 올바르지 않습니다.");
            if (uri.Scheme == "https" || uri.Scheme == "wss") return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_allowLocalDevelopmentServer && uri.IsLoopback && (uri.Scheme == "http" || uri.Scheme == "ws")) return;
#endif
            throw new InvalidOperationException("서버 연결에는 HTTPS가 필요합니다.");
        }
        public CertificateHandler CreateCertificateHandler() => DrawServerTrust.CreateCertificateHandler(_certificateSha256);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public void SetDevelopmentServer(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
                throw new InvalidOperationException("개발 서버는 로컬 HTTP 주소를 사용하세요.");
            _mainServerUrl = address.TrimEnd('/'); _allowLocalDevelopmentServer = true;
        }
#endif
    }
}
