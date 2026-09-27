using System;
using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using Connection = Unity.Networking.Transport.NetworkConnection;
using Error = Unity.Networking.Transport.Error;

namespace DrawLiar
{
    [DisallowMultipleComponent]
    public sealed class RelayMirrorTransport : Transport
    {
        const int ReliableLimit = 16384;
        const int UnreliableLimit = 1200;
        const int QueueLimit = 1024 * 1024;
        sealed class Peer
        {
            public Connection Connection;
            public readonly Queue<byte[]> Pending = new Queue<byte[]>();
            public int PendingBytes;
        }

        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        readonly Dictionary<Connection, int> ids = new Dictionary<Connection, int>();
        readonly byte[] receiveBuffer = new byte[ReliableLimit];
        NetworkDriver driver;
        NetworkPipeline reliable;
        RelayServerData relayData;
        bool configured;
        bool server;
        bool connected;
        int nextId;
        Peer client;
        internal bool AllocationInvalid => driver.IsCreated && driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid;
#if UNITY_EDITOR
        NetworkEndpoint? testEndpoint;
        ushort testPort;

        public void ConfigureLoopbackTest(ushort port, bool asServer)
        {
            if (driver.IsCreated) throw new InvalidOperationException("Transport is running.");
            testEndpoint = NetworkEndpoint.LoopbackIpv4.WithPort(port);
            testPort = asServer ? port : (ushort)0;
            configured = true;
        }
#endif

        public void Configure(RelayServerData data)
        {
            if (driver.IsCreated) throw new InvalidOperationException("연결 중에는 Relay 설정을 바꿀 수 없습니다.");
            relayData = data;
            configured = true;
#if UNITY_EDITOR
            testEndpoint = null;
            testPort = 0;
#endif
        }

        public override bool Available() => Application.platform != RuntimePlatform.WebGLPlayer;
        public override bool IsEncrypted => true;
        public override string EncryptionCipher => "DTLS";
        public override bool ClientConnected() => connected;
        public override bool ServerActive() => server && driver.IsCreated;
        public override Uri ServerUri() => new Uri("relay://drawliar");
        public override string ServerGetClientAddress(int connectionId) => "Unity Relay";
        public override int GetMaxPacketSize(int channelId = Channels.Reliable) => channelId == Channels.Reliable ? ReliableLimit : UnreliableLimit;
        public override int GetBatchThreshold(int channelId = Channels.Reliable) => UnreliableLimit;

        void CreateDriver()
        {
            if (!configured) throw new InvalidOperationException("먼저 Unity Relay 할당을 생성하거나 참가해야 합니다.");
            if (driver.IsCreated) throw new InvalidOperationException("Transport가 이미 실행 중입니다.");
            var settings = new NetworkSettings(Allocator.Temp);
            try
            {
                settings.WithNetworkConfigParameters(connectTimeoutMS: 1000, maxConnectAttempts: 15,
                    disconnectTimeoutMS: 10000, heartbeatTimeoutMS: 500, sendQueueCapacity: 1024, receiveQueueCapacity: 1024);
#if UNITY_EDITOR
                if (!testEndpoint.HasValue)
#endif
                    settings.WithRelayParameters(ref relayData);
                settings.WithReliableStageParameters(windowSize: 64);
                settings.WithFragmentationStageParameters(payloadCapacity: ReliableLimit);
                driver = NetworkDriver.Create(settings);
            }
            finally { settings.Dispose(); }
            reliable = driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
            var endpoint = NetworkEndpoint.AnyIpv4;
#if UNITY_EDITOR
            if (testEndpoint.HasValue) endpoint.Port = testPort;
#endif
            if (driver.Bind(endpoint) != 0)
            {
                DisposeDriver();
                throw new InvalidOperationException("Relay 연결용 UDP 소켓을 열지 못했습니다.");
            }
        }

        public override void ServerStart()
        {
            CreateDriver();
            if (driver.Listen() != 0)
            {
                DisposeDriver();
                throw new InvalidOperationException("Relay 호스트를 시작하지 못했습니다.");
            }
            server = true;
        }

        public override void ClientConnect(string address)
        {
            try
            {
                CreateDriver();
                var endpoint = relayData.Endpoint;
#if UNITY_EDITOR
                if (testEndpoint.HasValue) endpoint = testEndpoint.Value;
#endif
                client = new Peer { Connection = driver.Connect(endpoint) };
                if (!client.Connection.IsCreated) throw new InvalidOperationException("Relay 연결을 시작하지 못했습니다.");
            }
            catch (Exception exception)
            {
                DisposeDriver();
                OnClientError?.Invoke(TransportError.Refused, exception.Message);
                OnClientDisconnected?.Invoke();
            }
        }

        public override void ClientEarlyUpdate() { if (!server) Receive(); }
        public override void ServerEarlyUpdate() { if (server) Receive(); }
        public override void ClientLateUpdate() { if (!server) Flush(); }
        public override void ServerLateUpdate() { if (server) Flush(); }

        void Receive()
        {
            if (!enabled || !driver.IsCreated) return;
            driver.ScheduleUpdate().Complete();
            if (server)
            {
                Connection connection;
                while (driver.IsCreated && (connection = driver.Accept()).IsCreated)
                {
                    int id = ++nextId;
                    peers.Add(id, new Peer { Connection = connection });
                    ids.Add(connection, id);
                    OnServerConnectedWithAddress?.Invoke(id, "Unity Relay");
                }
            }

            while (driver.IsCreated)
            {
                var type = driver.PopEvent(out var connection, out var reader, out var pipeline);
                if (type == NetworkEvent.Type.Empty) break;
                int id = 0;
                if (server && !ids.TryGetValue(connection, out id)) continue;
                if (type == NetworkEvent.Type.Connect && !server)
                {
                    connected = true;
                    OnClientConnected?.Invoke();
                }
                else if (type == NetworkEvent.Type.Data)
                {
                    int channel = pipeline.Equals(reliable) ? Channels.Reliable : Channels.Unreliable;
                    if (reader.Length > GetMaxPacketSize(channel))
                    {
                        Fail(id, TransportError.InvalidReceive, "Relay 패킷이 허용 크기를 초과했습니다.");
                        continue;
                    }
                    using var bytes = new NativeArray<byte>(reader.Length, Allocator.Temp);
                    reader.ReadBytes(bytes);
                    NativeArray<byte>.Copy(bytes, 0, receiveBuffer, 0, bytes.Length);
                    var segment = new ArraySegment<byte>(receiveBuffer, 0, bytes.Length);
                    if (server) OnServerDataReceived?.Invoke(id, segment, channel);
                    else OnClientDataReceived?.Invoke(segment, channel);
                }
                else if (type == NetworkEvent.Type.Disconnect)
                {
                    if (server)
                    {
                        peers.Remove(id);
                        ids.Remove(connection);
                        OnServerDisconnected?.Invoke(id);
                    }
                    else
                    {
                        DisposeDriver();
                        OnClientDisconnected?.Invoke();
                    }
                }
            }
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable) => Send(0, client, segment, channelId);
        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            if (peers.TryGetValue(connectionId, out var peer)) Send(connectionId, peer, segment, channelId);
        }

        void Send(int id, Peer peer, ArraySegment<byte> segment, int channel)
        {
            if (!driver.IsCreated || peer == null) return;
            if (segment.Count > GetMaxPacketSize(channel) || segment.Array == null)
            {
                Fail(id, TransportError.InvalidSend, "Relay 패킷이 허용 크기를 초과했습니다.");
                return;
            }
            if (channel != Channels.Reliable)
            {
                int result = SendNow(peer.Connection, segment, channel);
                if (result >= 0) Sent(id, segment, channel);
                return;
            }
            if (peer.Pending.Count == 0)
            {
                int result = SendNow(peer.Connection, segment, channel);
                if (result >= 0) { Sent(id, segment, channel); return; }
                if (result != (int)Error.StatusCode.NetworkSendQueueFull)
                {
                    Fail(id, TransportError.InvalidSend, $"Relay 전송 오류 ({result})");
                    return;
                }
            }
            if (peer.PendingBytes + segment.Count > QueueLimit)
            {
                Fail(id, TransportError.Congestion, "네트워크가 너무 느려 연결을 종료했습니다.");
                return;
            }
            byte[] copy = new byte[segment.Count];
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, copy.Length);
            peer.Pending.Enqueue(copy);
            peer.PendingBytes += copy.Length;
        }

        int SendNow(Connection connection, ArraySegment<byte> segment, int channel)
        {
            int result = driver.BeginSend(channel == Channels.Reliable ? reliable : NetworkPipeline.Null, connection, out var writer, segment.Count);
            if (result < 0) return result;
            using var bytes = new NativeArray<byte>(segment.Count, Allocator.Temp);
            NativeArray<byte>.Copy(segment.Array, segment.Offset, bytes, 0, segment.Count);
            writer.WriteBytes(bytes);
            return driver.EndSend(writer);
        }

        void Flush()
        {
            if (!enabled || !driver.IsCreated) return;
            if (server)
            {
                foreach (int id in new List<int>(peers.Keys))
                    if (peers.TryGetValue(id, out var peer)) FlushPeer(id, peer);
            }
            else if (client != null) FlushPeer(0, client);
            if (driver.IsCreated) driver.ScheduleFlushSend().Complete();
        }

        void FlushPeer(int id, Peer peer)
        {
            while (driver.IsCreated && peer.Pending.Count > 0)
            {
                byte[] bytes = peer.Pending.Peek();
                var segment = new ArraySegment<byte>(bytes);
                int result = SendNow(peer.Connection, segment, Channels.Reliable);
                if (result == (int)Error.StatusCode.NetworkSendQueueFull) break;
                if (result < 0) { Fail(id, TransportError.InvalidSend, $"Relay 전송 오류 ({result})"); break; }
                peer.Pending.Dequeue();
                peer.PendingBytes -= bytes.Length;
                Sent(id, segment, Channels.Reliable);
            }
        }

        void Sent(int id, ArraySegment<byte> segment, int channel)
        {
            if (server) OnServerDataSent?.Invoke(id, segment, channel);
            else OnClientDataSent?.Invoke(segment, channel);
        }

        void Fail(int id, TransportError error, string reason)
        {
            if (server) { OnServerError?.Invoke(id, error, reason); ServerDisconnect(id); }
            else { OnClientError?.Invoke(error, reason); ClientDisconnect(); }
        }

        public override void ServerDisconnect(int connectionId)
        {
            if (!peers.TryGetValue(connectionId, out var peer)) return;
            if (driver.IsCreated) driver.Disconnect(peer.Connection);
            peers.Remove(connectionId);
            ids.Remove(peer.Connection);
            OnServerDisconnected?.Invoke(connectionId);
        }

        public override void ClientDisconnect()
        {
            bool notify = client != null;
            if (driver.IsCreated && client != null) driver.Disconnect(client.Connection);
            DisposeDriver();
            if (notify) OnClientDisconnected?.Invoke();
        }

        public override void ServerStop() => DisposeDriver();
        public override void Shutdown() => DisposeDriver();
        void OnDestroy() => DisposeDriver();

        void DisposeDriver()
        {
            if (driver.IsCreated)
            {
                foreach (var peer in peers.Values) driver.Disconnect(peer.Connection);
                driver.ScheduleFlushSend().Complete();
                driver.Dispose();
            }
            peers.Clear();
            ids.Clear();
            client = null;
            connected = server = false;
            configured = false;
        }
    }
}
