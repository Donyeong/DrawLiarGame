#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawUiSmoke : MonoBehaviour
    {
        [Serializable]
        private sealed class Report
        {
            public string Outcome = "RUNNING", Stage = "Starting", Error = "";
            public bool Peer, InputObserved, ChatObserved, HostDisconnected, HomeFlowVerified;
            public bool SecretVerified, DiscussionVerified, RebuttalVerified, VotingVerified, VoteSubmittedOnce, TurnCompleted;
            public bool PointerReleased, TypingPreserved, EquipmentVerified;
            public int PlayerCount, PlayersInRow;
            public int Strokes; public string CanvasBounds = "", BubbleBounds = "", RenderedBrush = "", RenderedEraser = "";
            public int BoundsChecks;
            public string[] Captures = Array.Empty<string>();
            public bool SteamAvailable;
            public string SteamStatus = "";
            public uint SteamAppId;
        }

        private readonly Report report = new Report();
        private DrawNetworkManager network;
        private UIDocument document;
        private string reportPath, stopSignal;
        private int playerCount = 3;
        private bool peerAutoTurn;
        private RenderTexture target;
        private readonly List<string> captures = new List<string>();
        private bool reportDirty;
        private float nextReportWrite;
        private const string ChatText = "UI 입력으로 보낸 채팅";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("-drawUiSmoke") && !args.Contains("-drawUiSmokePeer")) return;
            var instance = new GameObject("DrawLiar UI interaction check");
            DontDestroyOnLoad(instance);
            instance.AddComponent<DrawUiSmoke>();
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
        }

        private async void Start()
        {
            reportPath = Arg("-drawUiReport");
            stopSignal = Arg("-drawUiStop");
            report.Peer = Environment.GetCommandLineArgs().Contains("-drawUiSmokePeer");
            if (int.TryParse(Arg("-drawUiPlayers"), out var count)) playerCount = Mathf.Clamp(count, 3, 12);
            report.PlayerCount = playerCount;
            try
            {
                await Until(() => (network = FindFirstObjectByType<DrawNetworkManager>()) != null
                    && (document = network.GetComponent<UIDocument>()) != null, "UI initialization");
                network.StrokeReceived += _ => { report.Strokes++; Write(); };
                network.ChatReceived += line => { if (line.Text == ChatText) report.ChatObserved = true; Write(); };
                if (report.Peer) await CheckPeer();
                else await CheckHost();
                report.Outcome = "PASS";
                report.Stage = "Finished";
            }
            catch (Exception exception)
            {
                report.Outcome = "FAIL";
                report.Error = exception.Message; Debug.LogError("UI_SMOKE_FAIL " + report.Error); if (target != null) Capture();
                network?.Leave();
            }
            Write();
        }

        private async Task CheckPeer()
        {
            await Until(() => network.State != null, "peer connection");
            RequireRoomSettings();
            report.Stage = "Connected"; Write();
            await Until(() => network.State.Phase == GamePhase.Drawing, "peer drawing phase", 120);
            await CheckSecret();
            peerAutoTurn = true;
            await Until(() => report.Strokes >= 5 && report.ChatObserved, "replicated pointer strokes and chat", 120);
            var surface = document.rootVisualElement.Q<DrawingSurface>();
            await Until(() => IsRed(Pixel(surface, .35f, .5f)) && IsPaper(Pixel(surface, .5f, .5f)), "remote canvas brush and eraser pixels");
            report.InputObserved = true; report.Stage = "InputVerified"; Write();
            await CheckLaterPhases(false);
            await Until(() => !NetworkClient.active, "host exit", 60);
            report.HostDisconnected = true;
        }

        private async Task CheckHost()
        {
            await Until(() => document.rootVisualElement.Q<VisualElement>(className: "home-panel") != null, "home layout");
            var steam=network.GetComponent<SteamInviteBridge>();
            report.SteamAvailable=steam.Available;report.SteamStatus=steam.Status;
            report.SteamAppId=Resources.Load<OnlineServicesConfig>("OnlineServicesConfig").SteamAppId;
            target = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
            target.Create();
            document.panelSettings.targetTexture = target;
            await Task.Delay(500);
            var root = document.rootVisualElement;
            RequireInWindow(root.Q<VisualElement>(className: "lobby-avatar"), "main character");
            await CaptureHome("main");
            await Click(FindButton("참가하기"));
            await Until(() => HasButton("코드로 참가") && HasButton("공개방 둘러보기"), "join choices");
            await CaptureHome("join");
            await Click(FindButton("코드로 참가"));
            await Until(() => HomePanel().Q<TextField>() != null && HasButton("← 뒤로"), "invitation code page");
            await CaptureHome("code");
            await Click(FindButton("← 뒤로"));
            await Until(() => HasButton("공개방 둘러보기"), "back to join choices");
            await Click(FindButton("공개방 둘러보기"));
            await Until(() => HasButton("← 뒤로"), "public room page");
            await CaptureHome("browse");
            await Click(FindButton("← 뒤로"));
            await Until(() => HasButton("코드로 참가"), "public room back to join choices");
            await Click(FindButton("← 뒤로"));
            await Until(() => HasButton("옵션") && HasButton("방 만들기"), "join back to main");
            await Click(FindButton("옵션"));
            await Until(() => HomePanel().Query<Toggle>().ToList().Any(toggle => toggle.label == "내 마이크 끄기"), "options page");
            await CaptureHome("options");
            await Click(FindButton("← 뒤로"));
            await Until(() => HasButton("커스터마이징"), "options back to main");

            await CheckEquipment();

            await Click(FindButton("방 만들기"));
            await Until(() => HasButton("릴레이 그리기\n한 도화지에 이어 그려요"), "creation mode page");
            Require(HasButton("한 명씩 그리기\n차례마다 새 도화지"), "Individual drawing choice is missing.");
            await Click(FindButton("릴레이 그리기\n한 도화지에 이어 그려요"));
            await CaptureHome("create-mode");
            await Click(FindButton("다음"));
            await Until(() => HomePanel().Query<IntegerField>().ToList().Any(field => field.label == "진행 판수"), "creation rules page");
            Integer("최대 인원").value = playerCount;
            Integer("진행 판수").value = 1;
            await CaptureHome("create-rules");
            await Click(FindButton("시간 설정"));
            await Until(() => HomePanel().Query<IntegerField>().ToList().Any(field => field.label == "역할 확인"), "creation time page");
            Integer("역할 확인").value = 3;
            Integer("한 사람 그림").value = 15;
            Integer("자유 토론").value = 17;
            Integer("반론").value = 9;
            Integer("투표").value = 11;
            Integer("라이어 공개").value = 4;
            Integer("정답 추측").value = 13;
            Integer("라운드 결과").value = 7;
            await CaptureHome("create-time");
            await Click(FindButton("← 뒤로"));
            await Until(() => HasButton("시간 설정") && HasButton("다음"), "time page back to rules");
            Require(Integer("진행 판수").value == 1, "Returning from time settings discarded round count.");
            await Click(FindButton("다음"));
            await Until(() => HasButton("방 만들고 입장"), "creation details page");
            Require(!HomePanel().Query<DropdownField>().ToList().Any(field => field.label == "주제"), "Topic dropdown should be replaced by toggle selection.");
            await Click(HomePanel().Q<Button>(className: "topic-selection-button"));
            await Until(() => HasButton("선택 완료"), "topic selection page");
            await Click(FindButton("초기화"));
            Require(!FindButton("선택 완료").enabledInHierarchy, "Empty topic selection should disable completion.");
            await Click(FindButton("← 뒤로"));
            Require(!FindButton("방 만들고 입장").enabledInHierarchy, "Empty topic selection should block room creation.");
            await Click(HomePanel().Q<Button>(className: "topic-selection-button"));
            await Click(FindButton("전체 선택"));
            Require(HomePanel().Query<Button>(className: "topic-toggle").ToList().All(button => button.ClassListContains("topic-selected")), "Select all missed a topic.");
            await Click(FindButton("초기화"));
            await Click(FindButton("동물"));
            await Click(FindButton("음식"));
            await CaptureHome("create-topics");
            await Click(FindButton("선택 완료"));
            await Click(HomePanel().Q<Button>(className: "topic-selection-button"));
            Require(HomePanel().Query<Button>(className: "topic-selected").ToList().Count == 2, "Topic selection was lost when revisiting.");
            await Click(FindButton("선택 완료"));
            var lan = root.Query<Toggle>().ToList().First(value => value.label == "개발용 LAN 연결");
            RequireInWindow(lan, "LAN toggle");
            lan.value = true;
            await CaptureHome("create-details");
            report.HomeFlowVerified = true;
            await Click(FindButton("방 만들고 입장"));
            await Until(() => network.State?.Phase == GamePhase.Lobby, "room creation from UI");
            RequireRoomSettings();
            report.Stage = "LobbyReady"; Write();
            await Until(() => network.State != null && network.State.Players.Length == playerCount && network.State.CanStart, "all peer connections", 120);
            await Task.Delay(400);
            CheckPlayerRow();
            Capture(false, "room-lobby.png");
            await Click(FindButton("게임 시작"));
            await Until(() => network.State.Phase == GamePhase.Drawing, "drawing phase", 60);
            await CheckSecret();
            await Until(() => network.CanDraw && IsDisplayed(root.Q<VisualElement>(className: "tools")), "host drawing tools", 120);
            Require(network.State.Topic == "동물" || network.State.Topic == "음식", "Round topic is outside the selected pool.");
            var surface = root.Q<DrawingSurface>();
            Require(surface != null, "Drawing surface is missing.");
            var palette = root.Q<VisualElement>(className: "tools").Query<Button>(className: "swatch").ToList();
            Require(palette.Count > 1, "Drawing palette is missing.");
            await Click(palette[1]);
            var sizes = root.Query<Button>(className: "brush-size-option").ToList();
            Require(sizes.Count == 3 && new[] { 5f, 11f, 24f }.All(size => sizes.Any(button => button.userData is float value && Mathf.Abs(value - size / 1200f) < .0001f)), "Brush size choices are missing.");
            await Click(sizes.First(button => button.userData is float value && Mathf.Abs(value - .02f) < .0001f));
            Require(surface.BrushColor.r == 225 && surface.BrushColor.g == 127 && surface.BrushColor.b == 103 && !surface.Eraser && Mathf.Abs(surface.BrushSize - .02f) < .0001f,
                "Palette or brush size input did not update the brush.");
            Pointer(surface, EventType.MouseDown, .25f, .5f);
            await Task.Delay(70);
            Pointer(surface, EventType.MouseDrag, .75f, .5f);
            await Task.Delay(70);
            Pointer(surface, EventType.MouseUp, .75f, .5f);
            await Until(() => IsRed(Pixel(surface, .5f, .5f)), "pointer brush stroke");
            await Click(FindButton("지우개"));
            Require(surface.Eraser, "Eraser button did not change the active tool.");
            Pointer(surface, EventType.MouseDown, .5f, .5f);
            await Task.Delay(70);
            Pointer(surface, EventType.MouseUp, .5f, .5f);
            await Until(() => IsPaper(Pixel(surface, .5f, .5f)) && IsRed(Pixel(surface, .35f, .5f)), "pointer eraser stroke");
            await Click(root.Q<Button>(className: "chat-open"));
            await Until(() => IsDisplayed(root.Q<VisualElement>(className: "chatbar")), "chat drawer");
            RequireInWindow(root.Q<VisualElement>(className: "chatbar"), "chat input");
            var chat = root.Q<TextField>(className: "chat-input");
            chat.value = ChatText;
            await Click(FindButton("보내기"));
            await Until(() => report.ChatObserved && root.Query<Label>(className: "chat-message").ToList().Any(label => label.text.Contains(ChatText)), "chat history from UI input");
            await Until(() => !IsDisplayed(root.Q<VisualElement>(className: "chatbar")), "chat closes after sending");
            foreach (var className in new[] { "room-hud", "workspace", "canvas-frame", "tools", "chat-history" })
            {
                var element = root.Q<VisualElement>(className: className);
                RequireInWindow(element, className);
            }
            await Task.Delay(350);
            Capture(true);
            report.InputObserved = true; report.Stage = "InputVerified"; Write();
            await Click(FindButton("펜"));
            Pointer(surface, EventType.MouseDown, .35f, .5f);
            await Until(() => surface.HasPointerCapture(PointerId.mousePointerId), "held drawing pointer", 2);
            network.EndTurn();
            await Until(() => !network.CanDraw, "drawing permission lost", 3);
            await Until(() => !surface.HasPointerCapture(PointerId.mousePointerId), "pointer released without mouse-up after turn", 2);
            report.PointerReleased = true;
            Pointer(surface, EventType.MouseUp, .35f, .5f);
            report.TurnCompleted = true;
            await CheckLaterPhases(true);
            await Until(() => File.Exists(stopSignal), "peer phase verification", 60);
            await Click(FindButton("메뉴"));
            await Click(FindButton("나가기"));
            await Until(() => !NetworkClient.active && !NetworkServer.active && root.Q<VisualElement>(className: "home-panel") != null, "leave button and home return");
            report.HostDisconnected = true;
        }

        private Button FindButton(string text) => document.rootVisualElement.Query<Button>().ToList().First(button => button.text == text);
        private async Task CheckSecret()
        {
            await Task.Delay(300);
            var root = document.rootVisualElement;
            var toggle = root.Q<Button>(className: "secret-toggle");
            if (network.State.LocalIsLiar || network.State.LocalIsSpectator)
            {
                Require(string.IsNullOrEmpty(network.State.Word), "A hidden role received the secret word.");
                Require(!IsDisplayed(toggle), "A hidden role can reveal the secret word.");
            }
            else
            {
                Require(!string.IsNullOrEmpty(network.State.Word) && IsDisplayed(toggle), "Citizen secret control is missing.");
                bool WordVisible() => root.Query<Label>().ToList().Any(label => IsDisplayed(label) && label.text.Contains(network.State.Word));
                if (toggle.text == "숨기기") await Click(toggle);
                await Until(() => !WordVisible() && toggle.text == "제시어 보기", "secret hidden");
                await Click(toggle);
                await Until(() => WordVisible() && toggle.text == "숨기기", "secret revealed for citizen");
                await Click(toggle);
                await Until(() => !WordVisible(), "secret hidden again");
            }
            report.SecretVerified = true; Write();
        }

        private async Task CheckLaterPhases(bool host)
        {
            var root = document.rootVisualElement;
            var voice = network.GetComponent<VoiceController>();
            var typing = typeof(VoiceController).GetField("typing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var chat = root.Q<TextField>(className: "chat-input");
            bool Typing() => typing != null && voice != null && (bool)typing.GetValue(voice);
            bool ChatFocused() => root.focusController.focusedElement is VisualElement focused && (focused == chat || chat.Contains(focused));
            foreach (var phase in new[] { GamePhase.Discussion, GamePhase.Rebuttal, GamePhase.Voting })
            {
                await Until(() => network.State?.Phase == phase, phase + " phase", 120);
                await Task.Delay(350);
                Require(network.State.Phase == phase && !network.CanDraw, "Unexpected phase during layout validation: " + phase);
                Require(!IsDisplayed(root.Q<VisualElement>(className: "tools")), "Drawing tools remain visible during " + phase);
                Require(IsDisplayed(root.Q<VisualElement>(className: "room-hud")), "Room HUD is missing during " + phase);
                Require(!IsDisplayed(root.Q<VisualElement>(className: "header")), "Home header remains visible in game.");
                var surface = root.Q<DrawingSurface>();
                Require(IsDisplayed(surface) && IsRed(Pixel(surface, .35f, .5f)) && IsPaper(Pixel(surface, .5f, .5f)), "Canvas evidence disappeared during " + phase);
                CheckPlayerRow();
                if (phase == GamePhase.Discussion) report.DiscussionVerified = true;
                if (phase == GamePhase.Rebuttal) report.RebuttalVerified = true;
                if (phase == GamePhase.Voting) report.VotingVerified = true;
                if (host)
                {
                    if (phase == GamePhase.Rebuttal)
                    {
                        Require(ChatFocused() && Typing(), "Phase change cleared typing protection while chat retained focus.");
                        report.TypingPreserved = true;
                        await Click(FindButton("보내기"));
                        await Until(() => !IsDisplayed(root.Q<VisualElement>(className: "chatbar")) && !Typing(), "typing protection clears after closing chat");
                    }
                    var filename = "room-" + phase.ToString().ToLowerInvariant() + ".png";
                    Capture(false, filename);
                    captures.Add(filename); report.Captures = captures.ToArray();
                    if (phase == GamePhase.Discussion)
                    {
                        await Click(root.Q<Button>(className: "chat-open"));
                        await Until(() => ChatFocused() && Typing(), "chat focus blocks voice before phase change");
                    }
                }
                report.Stage = phase + "Verified"; Write();
            }
            if (!host) return;
            var submit = root.Q<Button>(className: "vote-submit");
            Require(IsDisplayed(submit) && !submit.enabledInHierarchy, "Vote submit must require selecting a player.");
            var other = network.State.Players.First(player => player.Id != network.LocalPlayerId && !player.IsSpectator && player.IsConnected);
            var card = root.Query<VisualElement>(className: "player").ToList().First(element => element.userData is int id && id == other.Id);
            RequireInWindow(card, "vote target");
            var picked = card.panel.Pick(card.worldBound.center);
            Require(picked == card || card.Contains(picked), "Vote target is covered.");
            Pointer(card, EventType.MouseMove, .5f, .5f);
            Pointer(card, EventType.MouseDown, .5f, .5f);
            await Task.Delay(50);
            Pointer(card, EventType.MouseUp, .5f, .5f);
            await Task.Delay(650);
            Require(network.State.Phase == GamePhase.Voting && !network.State.Players.First(player => player.Id == network.LocalPlayerId).HasVoted,
                "Selecting a player submitted a vote before confirmation.");
            submit = root.Q<Button>(className: "vote-submit");
            Require(submit != null && submit.enabledInHierarchy, "Selecting a player did not enable vote submission.");
            await Click(submit);
            await Until(() => network.State.Players.First(player => player.Id == network.LocalPlayerId).HasVoted, "server-accepted UI vote");
            await Task.Delay(650);
            submit = root.Q<Button>(className: "vote-submit");
            Require(!IsDisplayed(submit) || !submit.enabledInHierarchy, "Vote submission remains enabled after voting.");
            Require(network.State.Phase == GamePhase.Voting && network.State.Players.Count(player => player.HasVoted) == 1,
                "The vote check did not remain in the expected single-vote state.");
            Capture(false, "room-voted.png");
            report.VoteSubmittedOnce = true; report.Stage = "PhasesVerified"; Write();
        }

        private void CheckPlayerRow()
        {
            var root = document.rootVisualElement;
            Require(root.ClassListContains("in-game") || root.Q<VisualElement>(className: "in-game") != null, "In-game layout class is missing.");
            var players = root.Query<VisualElement>(className: "player").ToList().Where(IsDisplayed).OrderBy(element => element.worldBound.x).ToList();
            Require(players.Count == playerCount && players.All(player => player.userData is int)
                && players.Select(player => (int)player.userData).Distinct().Count() == playerCount, "The displayed player row does not match the connected players.");
            for (int i = 0; i < players.Count; i++)
            {
                RequireInWindow(players[i], "player " + i);
                Require(Mathf.Abs(players[i].worldBound.y - players[0].worldBound.y) < 2, "Players do not fit in one row.");
                if (i > 0) Require(players[i - 1].worldBound.xMax <= players[i].worldBound.xMin + 1, "Player entries overlap.");
            }
            report.PlayersInRow = players.Count;
        }

        private static bool IsDisplayed(VisualElement element)
        {
            if (element == null || element.panel == null) return false;
            for (var current = element; current != null; current = current.parent)
                if (current.resolvedStyle.display == DisplayStyle.None || current.resolvedStyle.visibility == UnityEngine.UIElements.Visibility.Hidden) return false;
            return element.worldBound.width > 0 && element.worldBound.height > 0;
        }

        private async Task CheckEquipment()
        {
            bool hadName=PlayerPrefs.HasKey("DrawLiar.Name"),hadColor=PlayerPrefs.HasKey("DrawLiar.Color"),hadEquipment=PlayerPrefs.HasKey("DrawLiar.Equipment");
            string savedName=PlayerPrefs.GetString("DrawLiar.Name","");
            int savedColor=PlayerPrefs.GetInt("DrawLiar.Color",0),savedEquipment=PlayerPrefs.GetInt("DrawLiar.Equipment",0);
            var root=document.rootVisualElement;
            AvatarElement Preview()=>root.Q<AvatarElement>(className:"lobby-avatar");
            async Task Select(int mask)
            {
                foreach(var item in new[]{AvatarAccessory.Beret,AvatarAccessory.Brush})
                {
                    var button=HomePanel().Q<Button>(item==AvatarAccessory.Beret?"beret-option":"brush-option");
                    Require(button!=null,"A default painter item is missing.");
                    if(((int)Preview().Equipment & (int)item)!=(mask & (int)item))await Click(button);
                    Require(button.ClassListContains("equipped")==((mask & (int)item)!=0),"Equipment selection indicator is incorrect.");
                }
                Require((int)Preview().Equipment==mask,"Hat and brush cannot be equipped independently.");
            }
            try
            {
                if(!hadEquipment)Require(Preview().Equipment==AvatarAccessory.Painter,"New players must start with both default painter items.");
                await Click(FindButton("커스터마이징"));
                await Until(()=>HomePanel().Q<Button>("beret-option")!=null,"customization equipment");
                Require(HomePanel().Query<Button>(className:"equipment-option").ToList().Count==2 && HasButton("이 모습으로 저장"),"Customization controls are missing.");
                var colors=HomePanel().Query<Button>(className:"swatch").ToList();
                Require(colors.Count>1,"Customization colors are missing.");
                await Click(colors[(savedColor+1)%colors.Count]);
                for(int mask=0;mask<=3;mask++)await Select(mask);
                await CaptureHome("customize");
                await Click(FindButton("← 뒤로"));
                await Until(()=>HasButton("방 만들기"),"customization cancel");
                Require(hadName==PlayerPrefs.HasKey("DrawLiar.Name") && hadColor==PlayerPrefs.HasKey("DrawLiar.Color") && hadEquipment==PlayerPrefs.HasKey("DrawLiar.Equipment")
                    && savedName==PlayerPrefs.GetString("DrawLiar.Name","") && savedColor==PlayerPrefs.GetInt("DrawLiar.Color",0)
                    && savedEquipment==PlayerPrefs.GetInt("DrawLiar.Equipment",0),"Canceling customization changed saved user preferences.");
                await Click(FindButton("커스터마이징"));await Select((int)AvatarAccessory.Brush);
                await Click(FindButton("이 모습으로 저장"));
                Require(PlayerPrefs.GetInt("DrawLiar.Equipment",-1)==(int)AvatarAccessory.Brush,"Brush-only equipment was not saved.");
                await Click(FindButton("커스터마이징"));
                Require(Preview().Equipment==AvatarAccessory.Brush,"Saved equipment was lost on reopening customization.");
                await Select((int)AvatarAccessory.Painter);await CaptureHome("customize-painter");await Click(FindButton("이 모습으로 저장"));
                Require(PlayerPrefs.GetInt("DrawLiar.Equipment",-1)==(int)AvatarAccessory.Painter,"Both default items were not saved.");
            }
            finally
            {
                if(hadName)PlayerPrefs.SetString("DrawLiar.Name",savedName);else PlayerPrefs.DeleteKey("DrawLiar.Name");
                if(hadColor)PlayerPrefs.SetInt("DrawLiar.Color",savedColor);else PlayerPrefs.DeleteKey("DrawLiar.Color");
                if(hadEquipment)PlayerPrefs.SetInt("DrawLiar.Equipment",savedEquipment);else PlayerPrefs.DeleteKey("DrawLiar.Equipment");
                PlayerPrefs.Save();
            }
        }

        private void RequireRoomSettings()
        {
            var settings = network.State.Settings;
            Require(settings.MaxPlayers == playerCount && settings.RoundCount == 1 && settings.RoleSeconds == 3 && settings.DrawSeconds == 15
                && settings.DiscussionSeconds == 17 && settings.RebuttalSeconds == 9 && settings.VoteSeconds == 11
                && settings.RevealSeconds == 4 && settings.GuessSeconds == 13 && settings.ResultSeconds == 7,
                "Phase time inputs were not replicated to the room.");
            Require(settings.Topics != null && settings.Topics.Length == 2 && settings.Topics.Contains("동물") && settings.Topics.Contains("음식"),
                "Selected topics were not replicated to the room.");
            Require(network.State.Players.Any(player=>player.Name=="UiPeer0" && player.Accessory==(int)AvatarAccessory.Painter),"Painter equipment was not replicated to the room.");
            report.EquipmentVerified=true;
        }
        private bool HasButton(string text) => document.rootVisualElement.Query<Button>().ToList().Any(button => button.text == text);
        private VisualElement HomePanel() => document.rootVisualElement.Q<VisualElement>(className: "home-panel");
        private IntegerField Integer(string label) => document.rootVisualElement.Query<IntegerField>().ToList().First(field => field.label == label);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static bool IsRed(Color color) => color.r > .85f && color.g < .6f && color.b < .65f;
        private static bool IsPaper(Color color) => Mathf.Abs(color.r - 246 / 255f) < .025f && Mathf.Abs(color.g - 241 / 255f) < .025f && Mathf.Abs(color.b - 230 / 255f) < .025f;
        private static Color Pixel(DrawingSurface surface, float x, float y)
        {
            var texture = surface?.resolvedStyle.backgroundImage.texture;
            return texture == null ? Color.clear : texture.GetPixel(Mathf.RoundToInt(x * (texture.width - 1)), Mathf.RoundToInt((1 - y) * (texture.height - 1)));
        }

        private async Task Click(Button button)
        {
            Require(button.enabledInHierarchy, "Button is disabled: " + button.text);
            await Task.Delay(100);
            if (button.panel == null && !string.IsNullOrEmpty(button.text)) button = FindButton(button.text);
            RequireInWindow(button, "button " + button.text);
            var picked = button.panel.Pick(button.worldBound.center);
            Require(picked == button || button.Contains(picked), "Button center is covered: " + button.text);
            bool clicked = false;
            void Observed() => clicked = true;
            button.clicked += Observed;
            try
            {
                Pointer(button, EventType.MouseMove, .5f, .5f);
                await Task.Delay(40);
                Pointer(button, EventType.MouseDown, .5f, .5f);
                await Task.Delay(40);
                Pointer(button, EventType.MouseUp, .5f, .5f);
                await Task.Delay(60);
                Require(clicked, "Pointer click did not reach button: " + button.text + " bounds=" + button.worldBound);
            }
            finally { button.clicked -= Observed; }
        }

        private void RequireInWindow(VisualElement element, string name)
        {
            var window = document.rootVisualElement.worldBound;
            var bounds = element?.worldBound ?? Rect.zero;
            Require(element != null && element.panel != null && bounds.width > 0 && bounds.height > 0
                && !float.IsNaN(bounds.x) && !float.IsNaN(bounds.y)
                && bounds.xMin >= window.xMin - 1 && bounds.yMin >= window.yMin - 1
                && bounds.xMax <= window.xMax + 1 && bounds.yMax <= window.yMax + 1,
                "UI is outside the window: " + name + " bounds=" + bounds + " window=" + window);
            report.BoundsChecks++;
        }

        private async Task CaptureHome(string name)
        {
            await Task.Delay(400);
            RequireInWindow(HomePanel(), name + " panel");
            foreach (var button in HomePanel().Query<Button>(className: "button").ToList())
                if (button.enabledInHierarchy && button.visible && button.resolvedStyle.display != DisplayStyle.None
                    && button.GetFirstAncestorOfType<ScrollView>() == null)
                    RequireInWindow(button, name + " / " + button.text);
            Capture(false, name + ".png");
            captures.Add(name + ".png"); report.Captures = captures.ToArray();
            report.Stage = "Home/" + name; Write();
        }

        private static void Pointer(VisualElement targetElement, EventType type, float x, float y)
        {
            var bounds = targetElement.worldBound;
            var input = new Event { type = type, button = 0, mousePosition = new Vector2(bounds.x + bounds.width * x, bounds.y + bounds.height * y) };
            if (type == EventType.MouseDown) { using var e = PointerDownEvent.GetPooled(input); targetElement.SendEvent(e); }
            else if (type == EventType.MouseUp) { using var e = PointerUpEvent.GetPooled(input); targetElement.SendEvent(e); }
            else { using var e = PointerMoveEvent.GetPooled(input); targetElement.SendEvent(e); }
        }

        private async Task Until(Func<bool> condition, string step, float seconds = 15)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (this == null || Time.realtimeSinceStartup > end) throw new TimeoutException("Timed out: " + step);
                await Task.Delay(25);
            }
        }

        private void Capture(bool verify = false, string filename = "ui-input.png")
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(reportPath), filename), image.EncodeToPNG());
                var canvas = document.rootVisualElement.Q<DrawingSurface>();
                var bubble = document.rootVisualElement.Query<Label>(className: "chat-message").ToList().FirstOrDefault(label => label.text.Contains(ChatText));
                if (canvas != null)
                {
                    var rect = canvas.worldBound;
                    report.CanvasBounds = rect + " content=" + canvas.contentRect + " frame=" + canvas.parent.worldBound;
                    var window=document.rootVisualElement.worldBound;
                    int PixelX(float x)=>Mathf.RoundToInt((x-window.x)/window.width*target.width);
                    int PixelY(float y)=>Mathf.RoundToInt(target.height-(y-window.y)/window.height*target.height);
                    var ink = image.GetPixel(PixelX(rect.x + rect.width * .35f), PixelY(rect.center.y));
                    var erased = image.GetPixel(PixelX(rect.center.x), PixelY(rect.center.y));
                    report.RenderedBrush = ink.ToString(); report.RenderedEraser = erased.ToString();
                    if (bubble != null) report.BubbleBounds = bubble.worldBound + " content=" + bubble.contentRect + " opacity=" + bubble.resolvedStyle.opacity + " text=" + bubble.text;
                    Write();
                    if (verify) Require(IsRed(ink) && IsPaper(erased), "Rendered canvas does not show the verified brush and eraser pixels.");
                }
            }
            finally { RenderTexture.active = previous; Destroy(image); }
        }

        private void Write()
        {
            if (string.IsNullOrEmpty(reportPath)) return;
            reportDirty=true;
            try
            {
                var temporary = reportPath + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(report, true));
                if (File.Exists(reportPath)) File.Replace(temporary, reportPath, null); else File.Move(temporary, reportPath);
                reportDirty=false;
            }
            catch (IOException) { nextReportWrite=Time.unscaledTime+.1f; }
        }

        private void Update()
        {
            if (peerAutoTurn && !report.TurnCompleted && network != null && network.CanDraw)
            {
                network.EndTurn(); report.TurnCompleted = true; Write();
            }
            if(reportDirty&&Time.unscaledTime>=nextReportWrite)Write();
        }

        private void OnDestroy() { if (target != null) { target.Release(); Destroy(target); } }
    }
}
#endif
