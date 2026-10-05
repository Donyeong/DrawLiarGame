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
            CheckRoomJoinAddresses(LEGACY_ROOM_ID);
            CheckGoogleCallback();
            Debug.Log("DRAWLIAR_SERVICES_CHECKS_OK: room-code/address validation and Google PKCE callback validation.");
        }

        private static void CheckRoomJoinAddresses(string legacyId)
        {
            const string PAGE = RoomJoinAddress.DEFAULT_PAGE_URL;
            foreach (string page in new[] { PAGE, PAGE + "/", PAGE + ".html", PAGE + ".html/",
                "http://rascallab.com/games/liar-canvas", "https://www.rascallab.com/games/liar-canvas",
                "http://localhost:8090/games/liar-canvas", "http://127.0.0.1:8090/games/liar-canvas",
                "http://[::1]:8090/games/liar-canvas" })
                Require(RoomJoinAddress.NormalizeCode(page + "?room=k7m-%209rX") == "K7M9RX", "방 주소 정규화 실패: " + page);
            Require(RoomJoinAddress.NormalizeCode(PAGE + "?lang=ko&%72oom=K7M9RX#chat") == "K7M9RX", "방 파라미터 1회 디코딩 실패");
            Require(RoomJoinAddress.NormalizeCode(PAGE + "?room=%7B" + legacyId + "%7D") == legacyId, "주소의 기존 방 UUID 호환 실패");
            Require(RoomJoinAddress.Create(PAGE + ".html/?token=secret&room=OLD#fragment", "k7m-9rx")
                == PAGE + "?room=K7M9RX", "공유 주소에 토큰·다른 쿼리·fragment가 남았습니다.");
            Require(RoomJoinAddress.Create(PAGE, legacyId) == PAGE + "?room=" + legacyId, "기존 UUID 방의 공유 주소 생성 실패");
            const string PREVIEW = "https://preview.example:8443/games/liar-canvas";
            Require(RoomJoinAddress.NormalizeCode(PREVIEW + "?room=K7M9RX", PREVIEW) == "K7M9RX", "현재 웹 원점 허용 실패");
            foreach (string invalid in new[] {
                PAGE, PAGE + "?room=", PAGE + "?room", PAGE + "?room=K7M9RX&room=K7M9RX",
                PAGE + "?room=K7M9RX&%72oom=K7M9RX", PAGE + "?ROOM=K7M9RX", PAGE + "?room=K7M9R%",
                PAGE + "?room=K7M9R%XX", PAGE + "?room=%254B7M9RX", PAGE + "?room=K7M9RX&other=%",
                PAGE + "?room=K7M0RX", "ftp://rascallab.com/games/liar-canvas?room=K7M9RX",
                "https://user:secret@rascallab.com/games/liar-canvas?room=K7M9RX",
                "https://@rascallab.com/games/liar-canvas?room=K7M9RX",
                "https://rascallab.com.attacker.example/games/liar-canvas?room=K7M9RX",
                "https://attacker.example/games/liar-canvas?room=K7M9RX",
                "https://rascallab.com/games/other?room=K7M9RX",
                "https://rascallab.com/games/other/../liar-canvas?room=K7M9RX",
                "https://rascallab.com/games/liar-canvas%2F?room=K7M9RX",
                "https://preview.example:8444/games/liar-canvas?room=K7M9RX",
                "http://preview.example:8443/games/liar-canvas?room=K7M9RX",
                PAGE + "?room=K7M9RX&other=" + new string('a', 2048)
            })
            {
                bool rejected = false;
                try { RoomJoinAddress.NormalizeCode(invalid, PREVIEW); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "잘못된 방 주소 허용: " + invalid);
            }
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
