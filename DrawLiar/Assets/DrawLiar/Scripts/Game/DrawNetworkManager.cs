using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

namespace DrawLiar
{
    public struct DrawProfileMessage : NetworkMessage
    {
        public string Name;
        public int Color, Accessory;
        public string VoiceId;
    }
    public enum DrawRequestKind : byte { Configure, Start, EndTurn, Vote, Guess, Chat, Lobby }
    public struct DrawRequestMessage : NetworkMessage
    {
        public DrawRequestKind Kind;
        public string Text;
        public int Target;
    }
    public struct DrawStateMessage : NetworkMessage { public string Json; }
    public struct DrawStrokeMessage : NetworkMessage { public DrawStroke Stroke; }
    public struct DrawCanvasMessage : NetworkMessage
    {
        public int Version;
        public bool Reset;
        public DrawStroke[] Strokes;
    }
    public struct DrawChatMessage : NetworkMessage { public ChatLine Line; }
    public struct DrawNoticeMessage : NetworkMessage { public string Text; }

    public sealed class DrawNetworkManager : NetworkManager
    {
        private sealed class ClientLimits
        {
            public double StrokeWindow, RequestWindow, LastChat = -10;
            public int Strokes, Requests;
            public bool CanvasFullNotice;
        }

        // ponytail: replay is capped at 24k segments; add image checkpoints if longer canvases are needed.
        private const int MaximumStrokes = 24000;
        private const int CanvasBatchSize = 64;
        private readonly List<DrawStroke> serverCanvas = new List<DrawStroke>();
        private readonly List<DrawStroke> localCanvas = new List<DrawStroke>();
        private readonly Dictionary<int, ClientLimits> limits = new Dictionary<int, ClientLimits>();
        private RoomSettings pendingSettings = new RoomSettings();
        private GameSession game;
        private DrawProfileMessage profile = new DrawProfileMessage { Name = "그림친구", VoiceId = "" };
        private double nextSnapshot;
        private int localCanvasVersion;

        public RoomSnapshot State { get; private set; }
        public int LocalPlayerId => State?.LocalPlayerId ?? -1;
        public int CanvasVersion => localCanvasVersion;
        public bool CanDraw => NetworkClient.isConnected && State != null && State.Phase == GamePhase.Drawing
            && State.ArtistId == LocalPlayerId && !State.LocalIsSpectator;
        public event Action<RoomSnapshot> StateChanged;
        public event Action<DrawStroke> StrokeReceived;
        public event Action CanvasCleared;
        public event Action<ChatLine> ChatReceived;
        public event Action<string> Notice;

        public void ConfigureRoom(RoomSettings settings)
        {
            if (settings == null) return;
            pendingSettings = settings.Copy();
            pendingSettings.Topics ??= GameDataStore.Load().Topics.Select(topic => topic.Name).ToArray();
            pendingSettings.Validate();
            if (!NetworkServer.active) maxConnections = pendingSettings.MaxPlayers;
            if (NetworkClient.isConnected) Request(DrawRequestKind.Configure, JsonUtility.ToJson(pendingSettings));
        }

        public void SetProfile(string name, int color, int accessory, string voiceId)
        {
            profile = new DrawProfileMessage
            {
                Name = GameRules.CleanText(name, 16, "그림친구"), Color = Mathf.Clamp(color, 0, 7),
                Accessory = Mathf.Clamp(accessory, 0, 3), VoiceId = GameRules.CleanText(voiceId, 80)
            };
            if (NetworkClient.isConnected) NetworkClient.Send(profile);
        }

        public void StartMatch() => Request(DrawRequestKind.Start);
        public void EndTurn() => Request(DrawRequestKind.EndTurn);
        public void Vote(int id) => Request(DrawRequestKind.Vote, target: id);
        public void Guess(string answer) => Request(DrawRequestKind.Guess, GameRules.CleanText(answer, 40));
        public void Chat(string text) => Request(DrawRequestKind.Chat, GameRules.CleanText(text, 160));
        public void ReturnToLobby() => Request(DrawRequestKind.Lobby);

        public void SendStroke(DrawStroke stroke)
        {
            if (!CanDraw) return;
            stroke.CanvasVersion = CanvasVersion;
            if (GameRules.ValidStroke(stroke, CanvasVersion)) NetworkClient.Send(new DrawStrokeMessage { Stroke = stroke });
        }

        public void ReplayCanvas()
        {
            CanvasCleared?.Invoke();
            foreach (var stroke in localCanvas) StrokeReceived?.Invoke(stroke);
        }

        public void Leave()
        {
            if (NetworkServer.active && NetworkClient.active) StopHost();
            else if (NetworkClient.active) StopClient();
            else if (NetworkServer.active) StopServer();
        }

        public override void OnStartServer()
        {
            autoCreatePlayer = false;
            game = new GameSession(pendingSettings, GameDataStore.Load());
            game.Changed += BroadcastState;
            game.CanvasCleared += ClearServerCanvas;
            serverCanvas.Clear();
            limits.Clear();
            NetworkServer.RegisterHandler<DrawProfileMessage>(ReceiveProfile);
            NetworkServer.RegisterHandler<DrawRequestMessage>(ReceiveRequest);
            NetworkServer.RegisterHandler<DrawStrokeMessage>(ReceiveStroke);
        }

        public override void OnStartClient()
        {
            autoCreatePlayer = false;
            NetworkClient.RegisterHandler<DrawStateMessage>(message =>
            {
                State = JsonUtility.FromJson<RoomSnapshot>(message.Json);
                StateChanged?.Invoke(State);
            });
            NetworkClient.RegisterHandler<DrawStrokeMessage>(message => ReceiveLocalStroke(message.Stroke));
            NetworkClient.RegisterHandler<DrawCanvasMessage>(ReceiveCanvas);
            NetworkClient.RegisterHandler<DrawChatMessage>(message => ChatReceived?.Invoke(message.Line));
            NetworkClient.RegisterHandler<DrawNoticeMessage>(message => Notice?.Invoke(message.Text));
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            NetworkClient.Send(profile);
        }

        public override void OnServerConnect(NetworkConnectionToClient connection)
        {
            limits[connection.connectionId] = new ClientLimits();
        }

        public override void OnServerDisconnect(NetworkConnectionToClient connection)
        {
            game?.Disconnect(connection.connectionId, Time.unscaledTimeAsDouble);
            limits.Remove(connection.connectionId);
            base.OnServerDisconnect(connection);
        }

        public override void OnClientDisconnect()
        {
            State = null;
            localCanvas.Clear();
            localCanvasVersion = 0;
            CanvasCleared?.Invoke();
            StateChanged?.Invoke(null);
            Notice?.Invoke("방 연결이 종료되었어요. 방장이 나가면 방이 닫힙니다.");
        }

        public override void OnClientError(TransportError error, string reason)
        {
            Notice?.Invoke("연결 오류: " + reason);
        }

        public override void OnStopServer()
        {
            if (game != null)
            {
                game.Changed -= BroadcastState;
                game.CanvasCleared -= ClearServerCanvas;
            }
            game = null;
            serverCanvas.Clear();
            limits.Clear();
        }

        public override void Update()
        {
            base.Update();
            if (!NetworkServer.active || game == null) return;
            double now = Time.unscaledTimeAsDouble;
            game.Tick(now);
            if (now >= nextSnapshot)
            {
                nextSnapshot = now + .5;
                BroadcastState();
            }
        }

        private void Request(DrawRequestKind kind, string text = "", int target = -1)
        {
            if (NetworkClient.isConnected) NetworkClient.Send(new DrawRequestMessage { Kind = kind, Text = text, Target = target });
        }

        private bool AllowRequest(NetworkConnectionToClient connection)
        {
            if (!limits.TryGetValue(connection.connectionId, out var rate)) return false;
            double now = Time.unscaledTimeAsDouble;
            if (now - rate.RequestWindow >= 1) { rate.RequestWindow = now; rate.Requests = 0; }
            return ++rate.Requests <= 15;
        }

        private void ReceiveProfile(NetworkConnectionToClient connection, DrawProfileMessage message)
        {
            if (game == null || !AllowRequest(connection)) return;
            if ((message.Name?.Length ?? 0) > 100 || (message.VoiceId?.Length ?? 0) > 100) return;
            int id = connection.connectionId;
            if (game.Contains(id)) game.UpdateProfile(id, message.Name, message.Color, message.Accessory, message.VoiceId);
            else if (game.Join(id, message.Name, message.Color, message.Accessory, message.VoiceId)) SendCanvas(connection);
            else
            {
                connection.Send(new DrawNoticeMessage { Text = "방이 가득 찼어요." });
                connection.Disconnect();
            }
        }

        private void ReceiveRequest(NetworkConnectionToClient connection, DrawRequestMessage request)
        {
            if (game == null || !game.Contains(connection.connectionId) || !AllowRequest(connection)) return;
            double now = Time.unscaledTimeAsDouble;
            bool host = connection == NetworkServer.localConnection;
            int textLimit = host && request.Kind == DrawRequestKind.Configure ? 32768 : 2048;
            if ((request.Text?.Length ?? 0) > textLimit) return;
            switch (request.Kind)
            {
                case DrawRequestKind.Configure:
                    if (host)
                    {
                        try
                        {
                            var settings = JsonUtility.FromJson<RoomSettings>(request.Text);
                            if (settings != null && game.Configure(settings))
                            {
                                maxConnections = game.Settings.MaxPlayers;
                                NetworkServer.maxConnections = maxConnections;
                            }
                        }
                        catch (ArgumentException) { SendNotice(connection, "방 설정을 읽을 수 없어요."); }
                    }
                    break;
                case DrawRequestKind.Start:
                    if (host && !game.Start(now, GameDataStore.Load())) SendNotice(connection, "참가자는 최소 3명이며 라이어보다 많아야 합니다. 사용할 주제도 하나 이상 선택해 주세요.");
                    break;
                case DrawRequestKind.EndTurn: game.EndTurn(connection.connectionId, now); break;
                case DrawRequestKind.Vote:
                    if (!game.Vote(connection.connectionId, request.Target, now)) SendNotice(connection, "투표할 수 없어요. 다른 참가자에게 한 번만 투표해 주세요.");
                    break;
                case DrawRequestKind.Guess:
                    if (!game.Guess(connection.connectionId, request.Text, now)) SendNotice(connection, "정답 추측은 라이어가 추측 시간에 한 번만 제출할 수 있어요.");
                    break;
                case DrawRequestKind.Chat:
                    ReceiveChat(connection, request.Text, now);
                    break;
                case DrawRequestKind.Lobby:
                    if (host && game.Phase == GamePhase.MatchResults) game.ReturnToLobby();
                    break;
            }
        }

        private void ReceiveChat(NetworkConnectionToClient connection, string text, double now)
        {
            var rate = limits[connection.connectionId];
            text = GameRules.CleanText(text, 160);
            if (text.Length == 0 || now - rate.LastChat < .7) return;
            rate.LastChat = now;
            var line = new ChatLine { PlayerId = connection.connectionId, Name = game.PlayerName(connection.connectionId), Text = text };
            NetworkServer.SendToAll(new DrawChatMessage { Line = line }, sendToReadyOnly: true);
        }

        private void ReceiveStroke(NetworkConnectionToClient connection, DrawStrokeMessage message)
        {
            if (game == null || game.Phase != GamePhase.Drawing || game.ArtistId != connection.connectionId || !GameRules.ValidStroke(message.Stroke, game.CanvasVersion)
                || !limits.TryGetValue(connection.connectionId, out var rate)) return;
            double now = Time.unscaledTimeAsDouble;
            if (now - rate.StrokeWindow >= 1) { rate.StrokeWindow = now; rate.Strokes = 0; }
            if (++rate.Strokes > 180) return;
            if (serverCanvas.Count >= MaximumStrokes)
            {
                if (!rate.CanvasFullNotice) SendNotice(connection, "이 도화지가 가득 찼어요. 턴을 마치고 다음 그림을 기다려 주세요.");
                rate.CanvasFullNotice = true;
                return;
            }
            serverCanvas.Add(message.Stroke);
            NetworkServer.SendToAll(message, sendToReadyOnly: true);
        }

        private void BroadcastState()
        {
            if (game == null || !NetworkServer.active) return;
            foreach (var connection in NetworkServer.connections.Values)
            {
                if (connection == null || !connection.isAuthenticated || !game.Contains(connection.connectionId)) continue;
                var snapshot = game.Snapshot(connection.connectionId, NetworkServer.localConnection?.connectionId ?? -1, Time.unscaledTimeAsDouble);
                connection.Send(new DrawStateMessage { Json = JsonUtility.ToJson(snapshot) });
            }
        }

        private void ClearServerCanvas()
        {
            serverCanvas.Clear();
            foreach (var rate in limits.Values) rate.CanvasFullNotice = false;
            if (NetworkServer.active) NetworkServer.SendToAll(new DrawCanvasMessage
                { Version = game.CanvasVersion, Reset = true, Strokes = Array.Empty<DrawStroke>() }, sendToReadyOnly: true);
        }

        private void SendCanvas(NetworkConnectionToClient connection)
        {
            connection.Send(new DrawCanvasMessage { Version = game.CanvasVersion, Reset = true, Strokes = Array.Empty<DrawStroke>() });
            for (int offset = 0; offset < serverCanvas.Count; offset += CanvasBatchSize)
            {
                int count = Math.Min(CanvasBatchSize, serverCanvas.Count - offset);
                connection.Send(new DrawCanvasMessage { Version = game.CanvasVersion, Strokes = serverCanvas.GetRange(offset, count).ToArray() });
            }
        }

        private void ReceiveCanvas(DrawCanvasMessage message)
        {
            if (message.Reset)
            {
                localCanvas.Clear();
                localCanvasVersion = message.Version;
                CanvasCleared?.Invoke();
            }
            if (message.Version != localCanvasVersion || message.Strokes == null) return;
            foreach (var stroke in message.Strokes) ReceiveLocalStroke(stroke);
        }

        private void ReceiveLocalStroke(DrawStroke stroke)
        {
            if (stroke.CanvasVersion != localCanvasVersion || localCanvas.Count >= MaximumStrokes) return;
            localCanvas.Add(stroke);
            StrokeReceived?.Invoke(stroke);
        }

        private static void SendNotice(NetworkConnectionToClient connection, string text) => connection.Send(new DrawNoticeMessage { Text = text });
    }
}
