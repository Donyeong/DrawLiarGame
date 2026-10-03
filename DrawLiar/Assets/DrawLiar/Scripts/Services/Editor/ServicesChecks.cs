using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class ServicesChecks
    {
        [MenuItem("DrawLiar/Run Services Checks")]
        public static void Run()
        {
            Require(LobbyServiceBridge.NormalizeCode(" k7m- 9rX ") == "K7M9RX", "방 코드 정규화 실패");
            Require(LobbyServiceBridge.NormalizeCode(" ") == "", "빈 방 코드 처리 실패");
            foreach (string invalid in new[] { "../../secret", "K7M9R", "K7M9RXX", "K7M0RX", "K7M1RX", "K7MIRX", "K7MORX", "한글코드입력" })
            {
                bool rejected = false;
                try { LobbyServiceBridge.NormalizeCode(invalid); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "잘못된 방 코드 허용: " + invalid);
            }
            const string LEGACY_ROOM_ID = "30e2c8b3-22c2-4e10-a22c-486d52b4422a";
            Require(LobbyServiceBridge.NormalizeCode(LEGACY_ROOM_ID) == LEGACY_ROOM_ID, "기존 방 코드 호환 실패");
            CheckGoogleCallback();
            Debug.Log("DRAWLIAR_SERVICES_CHECKS_OK: room-code validation and Google PKCE callback validation.");
        }

        private static void CheckGoogleCallback()
        {
            var authentication = typeof(LobbyServiceBridge).Assembly.GetType("DrawLiar.GoogleDesktopAuthentication");
            Require(authentication != null, "Google PC 인증 구현 누락");
            var hash = authentication.GetMethod("CreateCodeChallenge", BindingFlags.Static | BindingFlags.NonPublic);
            Require((string)hash.Invoke(null, new object[] { "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk" })
                == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE S256 검증 실패");
            var callback = authentication.GetNestedType("LoopbackSession", BindingFlags.NonPublic);
            var parse = callback.GetMethod("TryParseCallback", BindingFlags.Static | BindingFlags.NonPublic);
            bool Parse(string query, string host = "127.0.0.1:43210")
            {
                string request = "GET /oauth2/callback?" + query + " HTTP/1.1\r\nHost: " + host + "\r\n\r\n";
                return (bool)parse.Invoke(null, new object[] { request, "127.0.0.1:43210", "expected-state", null, null });
            }
            Require(Parse("state=expected-state&code=valid-code"), "정상 OAuth 콜백 거부");
            Require(!Parse("state=wrong-state&code=valid-code"), "잘못된 OAuth state 허용");
            Require(!Parse("state=expected-state&state=expected-state&code=valid-code"), "중복 OAuth 파라미터 허용");
            Require(!Parse("state=expected-state&code=valid-code&error=access_denied"), "혼합 OAuth 결과 허용");
            Require(!Parse("state=expected-state&code=valid-code", "attacker.example"), "잘못된 콜백 호스트 허용");
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
