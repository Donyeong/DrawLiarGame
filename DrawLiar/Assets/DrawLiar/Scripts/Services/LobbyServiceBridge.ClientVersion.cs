using System;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        private const string UPDATE_REQUIRED_MESSAGE = "새 버전으로 업데이트해야 이용할 수 있습니다.";
        public ClientVersionResponse ClientVersion { get; private set; } = new ClientVersionResponse();
        public event Action ClientVersionChanged;

        private static string ClientPlatform =>
            Application.platform == RuntimePlatform.Android ? ClientVersionRules.PLATFORM_ANDROID
            : Application.platform == RuntimePlatform.WindowsPlayer ? ClientVersionRules.PLATFORM_WINDOWS
            : Application.platform.ToString();

        // 조회 실패는 접속을 막지 않는다. 강제 업데이트는 메인서버가 로그인에서 다시 검증한다.
        public async Task CheckClientVersionAsync()
        {
            try
            {
                var version = await SendAsync<ClientVersionResponse>(_config.MainServerUrl, "/api/client/version", "GET");
                version.UpdateRequired |= ClientVersion.UpdateRequired;
                ClientVersion = version;
                ClientVersionChanged?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogWarning("클라이언트 버전을 확인하지 못했습니다: " + exception.Message); }
        }

        private void OnUpdateRequired()
        {
            ClientVersion.UpdateRequired = true;
            ClientVersionChanged?.Invoke();
            _ = CheckClientVersionAsync();
        }

        private void RequireCurrentClient()
        {
            if (ClientVersion.UpdateRequired) throw new InvalidOperationException(UPDATE_REQUIRED_MESSAGE);
        }

        public void OpenStore()
        {
            string url = ClientVersion.StoreUrl;
            if (string.IsNullOrEmpty(url) && Application.platform == RuntimePlatform.Android) url = "market://details?id=" + Application.identifier;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "market")) Application.OpenURL(uri.AbsoluteUri);
        }
    }
}
