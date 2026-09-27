using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Mirror;
using UnityEditor;
using UnityEngine;

namespace DrawLiar.Editor
{
    public static class ServicesChecks
    {
        [MenuItem("DrawLiar/Run Services Checks")]
        public static void Run()
        {
            CheckDiagnostics();
            Require(LobbyServiceBridge.NormalizeCode(" aBc123 ") == "ABC123", "Invite code normalization");
            Require(SteamInviteBridge.ParseInvitation("DrawLiar.exe --join-code abc123") == "ABC123", "Steam startup invite");
            Require(SteamInviteBridge.ParseInvitation("--join-code ../../secret") == "", "Reject invalid invite");
            Require(SteamInviteBridge.ParseInvitation("--join-code") == "", "Ignore incomplete invite");
            var hostObject = new GameObject("Transport check host");
            var clientObject = new GameObject("Transport check client");
            var host = hostObject.AddComponent<RelayMirrorTransport>();
            var client = clientObject.AddComponent<RelayMirrorTransport>();
            try
            {
                int connectionId = 0, serverReliable = 0, clientReliable = 0, serverUnreliable = 0, clientUnreliable = 0;
                string error = null;
                host.OnServerConnectedWithAddress = (id, _) => connectionId = id;
                host.OnServerError = (_, _, message) => error = message;
                client.OnClientError = (_, message) => error = message;
                host.OnServerDataReceived = (_, bytes, channel) =>
                {
                    if (channel == Channels.Reliable) CheckPayload(bytes, serverReliable++);
                    else serverUnreliable++;
                };
                client.OnClientDataReceived = (bytes, channel) =>
                {
                    if (channel == Channels.Reliable) CheckPayload(bytes, clientReliable++);
                    else clientUnreliable++;
                };
                ushort port = (ushort)UnityEngine.Random.Range(20000, 40000);
                host.ConfigureLoopbackTest(port, true);
                client.ConfigureLoopbackTest(port, false);
                host.ServerStart();
                client.ClientConnect("127.0.0.1");
                PumpUntil(() => client.ClientConnected() && connectionId > 0, host, client, () => error);
                const int count = 160;
                for (int i = 0; i < count; i++)
                {
                    var payload = MakePayload(i);
                    host.ServerSend(connectionId, payload);
                    client.ClientSend(payload);
                }
                host.ServerSend(connectionId, new ArraySegment<byte>(new byte[1200]), Channels.Unreliable);
                client.ClientSend(new ArraySegment<byte>(new byte[1200]), Channels.Unreliable);
                PumpUntil(() => clientReliable == count && serverReliable == count && clientUnreliable > 0 && serverUnreliable > 0,
                    host, client, () => error);
                bool disconnected = false;
                client.OnClientDisconnected = () => disconnected = true;
                host.ServerDisconnect(connectionId);
                PumpUntil(() => disconnected, host, client, () => error);
                Require(!client.ClientConnected(), "Remote disconnect");
                UnityEngine.Debug.Log("DRAWLIAR_SERVICES_CHECKS_OK: invite parsing, bidirectional reliable fragmentation/queue order, unreliable packets, disconnect.");
            }
            finally
            {
                host.Shutdown();
                client.Shutdown();
                UnityEngine.Object.DestroyImmediate(hostObject);
                UnityEngine.Object.DestroyImmediate(clientObject);
            }
        }

        static void CheckDiagnostics()
        {
            const string secret = "DO_NOT_LOG_TOKEN_OR_BODY";
            var sdk = typeof(Unity.Services.Relay.RelayServiceException).Assembly;
            var response = Activator.CreateInstance(sdk.GetType("Unity.Services.Relay.Http.HttpClientResponse"),
                new object[] { new Dictionary<string, string> { ["Authorization"] = secret }, 503L, true, false,
                    System.Text.Encoding.UTF8.GetBytes(secret), secret });
            var inner = (Exception)Activator.CreateInstance(sdk.GetType("Unity.Services.Relay.Http.HttpException"), new[] { response });
            var error = new Unity.Services.Relay.RelayServiceException(Unity.Services.Relay.RelayExceptionReason.Unknown, secret, inner);
            error.Data["DrawLiarApi"] = "Relay.JoinAllocation";
            var formatter = typeof(LobbyServiceBridge).GetMethod("FormatDiagnostic", BindingFlags.Static | BindingFlags.NonPublic);
            string text = (string)formatter.Invoke(null, new object[] { error });
            Require(text.Contains("api=Relay.JoinAllocation") && text.Contains("reason=Unknown") && text.Contains("code=15999")
                && text.Contains("http=503") && !text.Contains(secret), "Token-free service failure diagnostics");
        }

        static ArraySegment<byte> MakePayload(int sequence)
        {
            byte[] payload = new byte[sequence % 16 == 0 ? 16384 : 4096];
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, payload, 0, 4);
            for (int i = 4; i < payload.Length; i++) payload[i] = (byte)(sequence + i);
            return new ArraySegment<byte>(payload);
        }

        static void CheckPayload(ArraySegment<byte> payload, int sequence)
        {
            Require(payload.Count == (sequence % 16 == 0 ? 16384 : 4096)
                && BitConverter.ToInt32(payload.Array, payload.Offset) == sequence, "Reliable packet ordering");
            for (int i = 4; i < payload.Count; i++)
                Require(payload.Array[payload.Offset + i] == (byte)(sequence + i), "Fragment integrity");
        }

        static void PumpUntil(Func<bool> complete, RelayMirrorTransport host, RelayMirrorTransport client, Func<string> error)
        {
            var timer = Stopwatch.StartNew();
            while (!complete())
            {
                Require(error() == null, "Transport error: " + error());
                Require(timer.Elapsed.TotalSeconds < 10, "Transport loopback timed out");
                host.ServerEarlyUpdate();
                client.ClientEarlyUpdate();
                host.ServerLateUpdate();
                client.ClientLateUpdate();
                Thread.Sleep(1);
            }
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
