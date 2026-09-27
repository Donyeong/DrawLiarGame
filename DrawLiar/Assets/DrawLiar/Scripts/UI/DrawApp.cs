using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mirror;
using kcp2k;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawApp : MonoBehaviour
    {
        private DrawNetworkManager network;
        private VoiceController voice;
        private LobbyServiceBridge lobby;
        private SteamInviteBridge steam;
        private VisualElement root, content, avatarStage, playerStrip, phaseActions, overlay, phaseBanner, drawingTools, contextInfo, chatbar;
        private Label word, topic, role, phaseTitle, phaseDetail, timer, roomBadge, roundCaption, voiceBadge, serviceNotice, voteProgress;
        private Button secretToggle, chatOpen;
        private TextField chatInput;
        private ScrollView chatHistory;
        private PanelSettings panelSettings;
        private bool secretHidden, voteSubmitted;
        private int selectedPlayerId=-1;
        private string contextKey="";
        private DrawingSurface surface;
        private ScrollView publicRoomList;
        private string lastServiceStatus="";
        private string nickname;
        private int avatarColor, accessory;
        private enum LobbyScreen { Main, Join, JoinCode, Browse, CreateMode, CreateRules, CreateDetails, Time, TopicSelection, Topics, Options, Customize }
        private LobbyScreen lobbyScreen;
        private bool inRoom;
        private bool runningAction;
        private bool reducedMotion;
        private string pendingInvitation = "";
        private GamePhase previousPhase=(GamePhase)(-1);
        private string playerKey="", actionKey="";
        private readonly Dictionary<int,VisualElement> playerCards=new Dictionary<int,VisualElement>();
        private readonly Dictionary<int,Label> playerStatuses=new Dictionary<int,Label>();
        private readonly List<DrawStroke> pendingStrokes=new List<DrawStroke>();
        private RoomSettings draft=new RoomSettings();
        private bool lan;
        private string address="localhost";

        private void Start()
        {
            network=GetComponent<DrawNetworkManager>();voice=GetComponent<VoiceController>();
            lobby=GetComponent<LobbyServiceBridge>();steam=GetComponent<SteamInviteBridge>();
            lobby.Initialize(network);steam.Initialize();
            draft.Topics=GameDataStore.Load().Topics.Select(t=>t.Name).ToArray();
            nickname=PlayerPrefs.GetString("DrawLiar.Name","그림콩"+UnityEngine.Random.Range(10,99));
            avatarColor=PlayerPrefs.GetInt("DrawLiar.Color",0);accessory=PlayerPrefs.GetInt("DrawLiar.Accessory",0);
            var panel=panelSettings=ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet=Resources.Load<ThemeStyleSheet>("DrawLiar/DrawLiarTheme");
            panel.scaleMode=PanelScaleMode.ScaleWithScreenSize;panel.referenceResolution=new Vector2Int(1600,1000);
            panel.screenMatchMode=PanelScreenMatchMode.MatchWidthOrHeight;panel.match=.5f;
            var document=gameObject.AddComponent<UIDocument>();document.panelSettings=panel;
            root=document.rootVisualElement;root.AddToClassList("app");
            root.styleSheets.Add(Resources.Load<StyleSheet>("DrawLiar/DrawLiar"));
            reducedMotion=PlayerPrefs.GetInt("DrawLiar.ReduceMotion",0)==1;
            root.EnableInClassList("reduce-motion",reducedMotion);
            root.RegisterCallback<GeometryChangedEvent>(_=>root.EnableInClassList("compact",root.contentRect.width<1400||root.contentRect.height<880));
            root.RegisterCallback<FocusInEvent>(e=>
            {
                var element=e.target as VisualElement;
                voice.SetTyping(element is TextField || element is IntegerField || element?.GetFirstAncestorOfType<TextField>()!=null || element?.GetFirstAncestorOfType<IntegerField>()!=null);
            });
            root.RegisterCallback<FocusOutEvent>(_=>voice.SetTyping(false));
            root.RegisterCallback<KeyDownEvent>(RoomShortcut);
            network.StateChanged+=RefreshState;network.Notice+=message=>
            {
                if(voteSubmitted&&network.State?.Phase==GamePhase.Voting&&!network.State.Players.Any(p=>p.Id==network.State.LocalPlayerId&&p.HasVoted))
                {voteSubmitted=false;actionKey="";RefreshState(network.State);}
                Toast(message);
            };
            network.StrokeReceived+=s=> { if(surface!=null)surface.Apply(s);else pendingStrokes.Add(s); };
            network.CanvasCleared+=()=>{surface?.ClearCanvas();pendingStrokes.Clear();};
            network.ChatReceived+=OnChat;
            lobby.Changed+=RefreshServiceStatus;
            steam.InvitationReceived+=QueueInvite;
            content=Box(root,"grow");Home();
            root.schedule.Execute(Tick).Every(100);
            ApplyProfile();
            var invitation=steam.ConsumePendingInvitation();
            if(!string.IsNullOrEmpty(invitation))QueueInvite(invitation);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args=Environment.GetCommandLineArgs();
            string Arg(string key) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:""; }
            if(args.Contains("-drawSmoke"))draft.MaxPlayers=12;
            if(!string.IsNullOrEmpty(Arg("-drawName"))){nickname=Arg("-drawName");ApplyProfile();Home();}
            if(args.Contains("-drawOnlineHost"))Run(async()=>{draft.IsPrivate=true;draft.RoomName="DrawLiar 연결 검사";await lobby.HostAsync(draft.Copy());});
            else if(!string.IsNullOrEmpty(Arg("-drawOnlineJoin")))Run(()=>lobby.JoinCodeAsync(Arg("-drawOnlineJoin")));
            else if(args.Contains("-drawHost")) { lan=true;LocalHost(); }
            else if(!string.IsNullOrEmpty(Arg("-drawJoin"))) { lan=true;address=Arg("-drawJoin");LocalJoin(); }
            if(!string.IsNullOrEmpty(Arg("-drawCapture")))StartCoroutine(CaptureAfterLayout(Arg("-drawCapture")));
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private System.Collections.IEnumerator CaptureAfterLayout(string path)
        {
            yield return new WaitForSecondsRealtime(5);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log("DRAWLIAR_CAPTURE "+path+" "+root.worldBound);
        }
#endif

        private void Home()
        {
            inRoom=false;surface=null;publicRoomList=null;chatHistory=null;chatInput=null;voiceBadge=null;content.Clear();playerCards.Clear();playerStatuses.Clear();previousPhase=(GamePhase)(-1);
            root.RemoveFromClassList("in-game");content.RemoveFromClassList("room-layout");root.AddToClassList("at-home");
            panelSettings.referenceResolution=new Vector2Int(1600,1000);
            var home=Box(content,"home");
            var layout=Box(home,"home-layout");var character=Box(layout,"character-side");
            BrandLogo(character,"game-logo");
            avatarStage=Box(character,"character-stage");UpdateLobbyAvatar(avatarColor,accessory);
            Text(character,nickname,"character-name");
            var customize=Button(character,"커스터마이징",()=>Navigate(LobbyScreen.Customize),"customize-button");
            if(lobbyScreen==LobbyScreen.Customize)customize.style.visibility=UnityEngine.UIElements.Visibility.Hidden;
            var panel=Box(layout,"home-panel");
            serviceNotice=null;
            if(lobbyScreen==LobbyScreen.Main)
            {
                panel.AddToClassList("main-menu");
                Button(panel,"방 만들기",()=>Navigate(LobbyScreen.CreateMode),"menu-button menu-orange");
                Button(panel,"참가하기",()=>Navigate(LobbyScreen.Join),"menu-button menu-mint");
                Button(panel,"옵션",()=>Navigate(LobbyScreen.Options),"menu-button menu-purple");
                Text(panel,"친구 3–12명과 함께하는 그림 추리","menu-caption");
            }
            else
            {
                var top=Box(panel,"screen-heading");Button(top,"← 뒤로",Back,"back-button");
                var body=Box(panel,"screen-body");
                switch(lobbyScreen)
                {
                    case LobbyScreen.Join:
                        Text(body,"참가하기","screen-title");
                        Button(body,"코드로 참가",()=>Navigate(LobbyScreen.JoinCode),"menu-button menu-orange");
                        Button(body,"공개방 둘러보기",()=>{Navigate(LobbyScreen.Browse);Run(lobby.RefreshAsync);},"menu-button menu-mint");break;
                    case LobbyScreen.JoinCode:Text(body,"코드로 참가","screen-title");JoinForm(body);break;
                    case LobbyScreen.Browse:Text(body,"공개방 둘러보기","screen-title");BrowseForm(body);break;
                    case LobbyScreen.CreateMode:CreateMode(body);break;
                    case LobbyScreen.CreateRules:CreateRules(body);break;
                    case LobbyScreen.CreateDetails:CreateDetails(body);break;
                    case LobbyScreen.Time:Text(body,"시간 설정","screen-title");TimeForm(body);break;
                    case LobbyScreen.TopicSelection:Text(body,"주제 선택","screen-title");TopicSelectionForm(body);break;
                    case LobbyScreen.Topics:Text(body,"나만의 주제","screen-title");TopicsForm(body);break;
                    case LobbyScreen.Options:Text(body,"옵션","screen-title");VoiceForm(body);break;
                    case LobbyScreen.Customize:Text(body,"커스터마이징","screen-title");ProfileForm(body,false);break;
                }
                if(lobbyScreen==LobbyScreen.CreateDetails||lobbyScreen==LobbyScreen.JoinCode||lobbyScreen==LobbyScreen.Browse)
                    serviceNotice=Text(body,lobbyScreen==LobbyScreen.Browse||lobby.IsBusy?lobby.Status:"","notice");
            }
            Enter(panel,260,14);
        }

        private void UpdateLobbyAvatar(int color,int decoration)
        {
            avatarStage.Clear();var avatar=new AvatarElement(color,decoration);avatar.AddToClassList("lobby-avatar");avatarStage.Add(avatar);
        }

        private static void BrandLogo(VisualElement parent,string styleClass)
        {
            var logo=new Image{image=Resources.Load<Texture2D>("DrawLiar/Brand/LiarsCanvasLogo"),scaleMode=ScaleMode.ScaleToFit,tooltip="Liar’s Canvas",pickingMode=PickingMode.Ignore};
            logo.AddToClassList(styleClass);parent.Add(logo);
        }

        private void Navigate(LobbyScreen screen)
        {
            voice.SetTyping(false);lobbyScreen=screen;Home();
        }
        private void Back()
        {
            var destination=LobbyScreen.Main;
            if(lobbyScreen==LobbyScreen.JoinCode||lobbyScreen==LobbyScreen.Browse)destination=LobbyScreen.Join;
            if(lobbyScreen==LobbyScreen.CreateRules)destination=LobbyScreen.CreateMode;
            if(lobbyScreen==LobbyScreen.CreateDetails||lobbyScreen==LobbyScreen.Time)destination=LobbyScreen.CreateRules;
            if(lobbyScreen==LobbyScreen.TopicSelection)destination=LobbyScreen.CreateDetails;
            if(lobbyScreen==LobbyScreen.Topics)destination=LobbyScreen.TopicSelection;
            Navigate(destination);
        }
        private static void CreateHeading(VisualElement panel,string title,int step)
        {
            Text(panel,$"방 만들기   {step} / 3","step-caption");Text(panel,title,"screen-title");
        }
        private void CreateMode(VisualElement panel)
        {
            CreateHeading(panel,"어떻게 그릴까요?",1);
            var modes=Box(panel,"mode-options");var modeButtons=new List<Button>();
            void SelectMode(int index){draft.Mode=(DrawingMode)index;for(var i=0;i<modeButtons.Count;i++)modeButtons[i].EnableInClassList("mode-selected",i==index);}
            modeButtons.Add(Button(modes,"릴레이 그리기\n한 도화지에 이어 그려요",()=>SelectMode(0),"mode-option"));
            modeButtons.Add(Button(modes,"한 명씩 그리기\n차례마다 새 도화지",()=>SelectMode(1),"mode-option"));SelectMode((int)draft.Mode);
            Button(panel,"다음",()=>Navigate(LobbyScreen.CreateRules),"primary next-button");
        }
        private void CreateRules(VisualElement panel)
        {
            CreateHeading(panel,"우리 방의 규칙",2);
            var columns=Box(panel,"row");var a=Box(columns,"form-column");var b=Box(columns,"form-column");
            Int(a,"최대 인원",draft.MaxPlayers,3,12,v=>{draft.MaxPlayers=v;draft.LiarCount=Mathf.Min(draft.LiarCount,v-1);Home();});
            Int(b,"라이어 수",draft.LiarCount,1,draft.MaxPlayers-1,v=>draft.LiarCount=v);
            Choice(panel,"승리 조건",new[]{"정해진 판수 후 최고점","목표 점수 먼저 달성"},(int)draft.Victory,v=>{draft.Victory=(VictoryMode)v;Home();});
            if(draft.Victory==VictoryMode.RoundCount)Int(panel,"진행 판수",draft.RoundCount,1,30,v=>draft.RoundCount=v);else Int(panel,"목표 점수",draft.TargetScore,1,1000,v=>draft.TargetScore=v);
            Button(panel,"시간 설정",()=>Navigate(LobbyScreen.Time),"secondary");
            Button(panel,"다음",()=>Navigate(LobbyScreen.CreateDetails),"primary next-button");
        }
        private void CreateDetails(VisualElement panel)
        {
            CreateHeading(panel,"방 이름을 정해요",3);
            var roomName=Field(panel,"방 이름",draft.RoomName);roomName.RegisterValueChangedCallback(e=>draft.RoomName=e.newValue);
            var topics=GameDataStore.Load().Topics.Select(t=>t.Name).ToArray();
            draft.Topics=(draft.Topics??topics).Intersect(topics).ToArray();
            Button(panel,$"주제 선택   ·   {draft.Topics.Length}개 선택   →",()=>Navigate(LobbyScreen.TopicSelection),"secondary topic-selection-button");
            Choice(panel,"공개 설정",new[]{"누구나 · 공개방","친구끼리 · 사설방"},draft.IsPrivate?1:0,i=>draft.IsPrivate=i==1);
            LocalToggle(panel);
            var create=Button(panel,"방 만들고 입장",()=>Run(async()=> { ApplyProfile();draft.Validate();if(lan)LocalHost();else await lobby.HostAsync(draft.Copy()); }),"primary next-button");
            create.SetEnabled(draft.Topics.Length>0);
            if(draft.Topics.Length==0)Text(panel,"주제를 하나 이상 선택하세요.","rules");
        }

        private void JoinForm(VisualElement panel)
        {
            Text(panel,"친구에게 받은 코드를 입력하세요.","subtitle");
            var code=Field(panel,"초대코드","");code.maxLength=12;
            Button(panel,"입장하기",()=>Run(async()=> {ApplyProfile();if(lan)LocalJoin();else await lobby.JoinCodeAsync(code.value.Trim().ToUpperInvariant());}),"primary next-button");
            LocalToggle(panel);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var ip=Field(panel,"LAN 주소",address);ip.RegisterValueChangedCallback(e=>address=e.newValue);
#endif
        }

        private void BrowseForm(VisualElement panel)
        {
            Button(panel,"새로고침",()=>Run(lobby.RefreshAsync),"secondary");
            publicRoomList=new ScrollView();publicRoomList.AddToClassList("room-list");panel.Add(publicRoomList);RefreshPublicRooms();
        }
        private void RefreshPublicRooms()
        {
            if(publicRoomList?.panel==null)return;
            publicRoomList.Clear();
            if(lobby.PublicRooms.Count==0)Text(publicRoomList,"공개방이 없습니다. 새 방을 만들어 보세요.","muted");
            foreach(var room in lobby.PublicRooms)
            {
                var entry=Box(publicRoomList,"room-entry");var info=Box(entry,"");Text(info,room.Name,"player-name");Text(info,$"{room.Players} / {room.MaxPlayers} 명","muted");
                Button(entry,"입장",()=>Run(async()=> {ApplyProfile();await lobby.JoinLobbyAsync(room.Id);}),"primary");
            }
        }

        private void LocalToggle(VisualElement panel)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var toggle=new Toggle("개발용 LAN 연결") { value=lan };toggle.AddToClassList("field small");toggle.RegisterValueChangedCallback(e=>lan=e.newValue);panel.Add(toggle);
#endif
        }
        private void LocalHost()
        {
            if(lobby.IsBusy||lobby.IsOnlineRoom||NetworkClient.active||NetworkServer.active)return;
            network.transport=GetComponent<KcpTransport>();network.ConfigureRoom(draft.Copy());ApplyProfile();network.StartHost();
        }
        private void LocalJoin()
        {
            if(lobby.IsBusy||lobby.IsOnlineRoom||NetworkClient.active||NetworkServer.active)return;
            network.transport=GetComponent<KcpTransport>();network.networkAddress=address.Trim();ApplyProfile();network.StartClient();
        }
        private async Task JoinInvite(string code)
        {
            if(NetworkClient.active||NetworkServer.active||lobby.IsOnlineRoom)await Leave();
            ApplyProfile();await lobby.JoinCodeAsync(code);
        }
        private void QueueInvite(string code)
        {
            pendingInvitation=code;
            steam.ConsumePendingInvitation();
        }
        private async Task Leave()
        {
            CloseModal();steam.ClearRoom();await lobby.LeaveAsync();network.Leave();
        }

        private void Room()
        {
            inRoom=true;content.Clear();publicRoomList=null;serviceNotice=null;playerKey="";actionKey="";contextKey="";
            playerCards.Clear();playerStatuses.Clear();selectedPlayerId=-1;secretHidden=false;voteSubmitted=false;
            root.RemoveFromClassList("at-home");root.AddToClassList("in-game");content.AddToClassList("room-layout");lobbyScreen=LobbyScreen.Main;
            panelSettings.referenceResolution=new Vector2Int(1600,900);
            var hud=Box(content,"room-hud");var round=Box(hud,"round-label");
            roomBadge=Text(round,"—","round-number");roundCaption=Text(round,"대기실","muted round-caption");
            phaseBanner=Box(hud,"phase-bar");phaseTitle=Text(phaseBanner,"대기실","phase-title");phaseDetail=Text(phaseBanner,"","phase-detail");
            var menu=Box(hud,"room-menu");timer=Text(menu,"","timer");Button(menu,"메뉴",RoomMenu,"secondary");
            var numeralFont=Resources.Load<Font>("DrawLiar/Fonts/BarlowCondensed-SemiBold");
            if(numeralFont!=null){timer.style.unityFontDefinition=FontDefinition.FromFont(numeralFont);roomBadge.style.unityFontDefinition=FontDefinition.FromFont(numeralFont);}
            var workspace=Box(content,"workspace");var sidebar=Box(workspace,"game-sidebar");
            var secret=Box(sidebar,"secret-bar");Text(secret,"주제","secret-label");topic=Text(secret,"게임 대기","topic");Box(secret,"secret-divider");
            role=Text(secret,"","role");Text(secret,"제시어","secret-label");word=Text(secret,"—","word");
            secretToggle=Button(secret,"숨기기",()=>{secretHidden=!secretHidden;RefreshSecret(network.State);},"secret-toggle");
            Box(sidebar,"grow");
            chatHistory=new ScrollView(ScrollViewMode.Vertical);chatHistory.AddToClassList("chat-history");sidebar.Add(chatHistory);
            chatOpen=Button(sidebar,"Enter  채팅",OpenChat,"chat-open");
            chatbar=Box(sidebar,"chatbar");chatbar.style.display=DisplayStyle.None;
            chatInput=new TextField(){maxLength=160};chatInput.textEdition.placeholder="채팅 입력";chatInput.AddToClassList("chat-input");chatbar.Add(chatInput);
            chatInput.RegisterCallback<KeyDownEvent>(e=>
            {
                if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){SendChat();e.StopPropagation();}
                else if(e.keyCode==KeyCode.Escape){CloseChat();e.StopPropagation();}
            });
            Button(chatbar,"보내기",SendChat,"secondary");
            var center=Box(workspace,"center");var frame=Box(center,"canvas-frame");surface=new DrawingSurface(network);frame.Add(surface);
            frame.RegisterCallback<GeometryChangedEvent>(_=>
            {
                var width=Mathf.Max(0,Mathf.Min(frame.contentRect.width,frame.contentRect.height*1.5f));
                surface.style.width=width;surface.style.height=width/1.5f;
            });
            var context=Box(workspace,"context-panel");drawingTools=Box(context,"tools");
            var toolRow=Box(drawingTools,"tool-row");var brush=Button(toolRow,"펜",()=>surface.Eraser=false,"secondary");var eraser=Button(toolRow,"지우개",()=>surface.Eraser=true,"secondary");
            var palette=Box(drawingTools,"palette");
            var colors=new[]{new Color32(40,43,39,255),new Color32(225,127,103,255),new Color32(228,182,107,255),DrawingSurface.PaperColor,
                new Color32(83,110,130,255),new Color32(166,171,159,255),new Color32(35,124,98,255),new Color32(157,147,219,255)};
            var swatches=new List<Button>();
            foreach(var color in colors)
            {
                Button swatch=null;swatch=Button(palette,"",()=>{surface.BrushColor=color;surface.Eraser=false;foreach(var item in swatches)item.RemoveFromClassList("selected");swatch.AddToClassList("selected");},"swatch");
                swatch.style.backgroundColor=(Color)color;swatch.tooltip="색상 "+(swatches.Count+1);swatch.EnableInClassList("palette-row-end",swatches.Count%4==3);swatches.Add(swatch);
            }
            swatches[0].AddToClassList("selected");
            Text(drawingTools,"굵기","secret-label");var sizes=Box(drawingTools,"brush-sizes");var sizeButtons=new List<Button>();
            foreach(var width in new[]{5,11,24})
            {
                Button choice=null;choice=Button(sizes,"●",()=>{surface.BrushSize=width/1200f;foreach(var item in sizeButtons)item.RemoveFromClassList("selected");choice.AddToClassList("selected");},"brush-size-option secondary");
                choice.userData=width/1200f;choice.tooltip="굵기 "+width;choice.style.fontSize=width==5?8:width==11?12:18;
                choice.EnableInClassList("selected",width==11);sizeButtons.Add(choice);
            }
            surface.BrushSize=11/1200f;
            drawingTools.schedule.Execute(()=>{brush.EnableInClassList("tool-selected",!surface.Eraser);eraser.EnableInClassList("tool-selected",surface.Eraser);}).Every(100);
            contextInfo=Box(context,"context-info");Box(context,"grow");phaseActions=Box(context,"phase-actions");
            voiceBadge=Text(context,"T  누르고 말하기","voice-hint");Button(context,"음성 설정",VoiceSettings,"voice-settings");
            playerStrip=Box(content,"players players-strip");
            network.ReplayCanvas();foreach(var stroke in pendingStrokes)surface.Apply(stroke);pendingStrokes.Clear();
        }

        private void OpenChat()
        {
            if(!inRoom||chatInput==null||overlay!=null)return;
            chatbar.style.display=DisplayStyle.Flex;chatOpen.style.display=DisplayStyle.None;chatInput.Focus();
        }
        private void CloseChat()
        {
            if(chatInput==null)return;
            chatInput.Blur();chatbar.style.display=DisplayStyle.None;chatOpen.style.display=DisplayStyle.Flex;voice.SetTyping(false);
        }
        private void SendChat()
        {
            if(!string.IsNullOrWhiteSpace(chatInput.value)){network.Chat(chatInput.value);chatInput.value="";}
            CloseChat();
        }
        private void RoomShortcut(KeyDownEvent e)
        {
            if(!inRoom)return;
            if(e.keyCode==KeyCode.Escape){if(overlay!=null)CloseModal();else if(chatbar.resolvedStyle.display!=DisplayStyle.None)CloseChat();else RoomMenu();e.StopPropagation();}
            else if((e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter)&&overlay==null&&chatbar.resolvedStyle.display==DisplayStyle.None
                &&!(e.target is TextField)&&!((e.target as VisualElement)?.GetFirstAncestorOfType<TextField>()!=null))
            {OpenChat();e.StopPropagation();}
        }
        private void RoomMenu()
        {
            var state=network.State;if(state==null)return;
            CloseChat();var modal=Modal("메뉴");Text(modal,state.Settings.RoomName,"subtitle");
            if(state.Phase==GamePhase.Lobby)
            {
                Button(modal,"초대코드 복사",()=>{GUIUtility.systemCopyBuffer=lobby.RoomCode;Toast(string.IsNullOrEmpty(lobby.RoomCode)?"LAN 방은 IP 주소로 참가할 수 있습니다.":"초대코드를 복사했습니다.");},"secondary");
                Button(modal,"Steam 초대",()=>{if(!steam.InviteFriends(lobby.RoomCode))Toast(steam.Status);},"secondary");
                Button(modal,"내 캐릭터",Profile,"secondary");
            }
            Button(modal,"음성 설정",VoiceSettings,"secondary");Button(modal,"플레이어 음량",PlayerAudioMenu,"secondary");
            Button(modal,"나가기",()=>Run(Leave),"danger");Button(modal,"닫기",CloseModal,"secondary");
        }
        private void PlayerAudioMenu()
        {
            var state=network.State;if(state==null)return;
            var modal=Modal("플레이어 음량");var list=new ScrollView();list.style.maxHeight=400;modal.Add(list);
            foreach(var player in state.Players.Where(p=>p.IsConnected))
                Button(list,player.Name+(player.Id==state.LocalPlayerId?" · 나":""),()=>{if(player.Id==state.LocalPlayerId)VoiceSettings();else PeerSettings(player);},"secondary");
            Button(modal,"닫기",CloseModal,"secondary");
        }
        private void RefreshSecret(RoomSnapshot state)
        {
            if(state==null)return;
            topic.text=state.Phase==GamePhase.Lobby?"게임 대기":state.Topic;
            role.text=state.Phase==GamePhase.Lobby?"":state.LocalIsSpectator?"관전":state.LocalIsLiar?"라이어":"시민";
            word.text=state.Phase==GamePhase.Lobby?"—":string.IsNullOrEmpty(state.Word)?"비공개":secretHidden?"•••":state.Word;
            word.EnableInClassList("word-hidden",string.IsNullOrEmpty(state.Word)||secretHidden);
            secretToggle.style.display=string.IsNullOrEmpty(state.Word)?DisplayStyle.None:DisplayStyle.Flex;
            secretToggle.text=secretHidden?"제시어 보기":"숨기기";
        }
        private void RefreshState(RoomSnapshot state)
        {
            if(state==null){steam.ClearRoom();if(inRoom){CloseModal();Home();}return;}
            if(!inRoom)Room();
            bool phaseChanged=previousPhase!=state.Phase;
            if(phaseChanged){selectedPlayerId=-1;voteSubmitted=false;contextKey="";}
            if(selectedPlayerId>=0&&!state.Players.Any(p=>p.Id==selectedPlayerId&&p.IsConnected&&!p.IsSpectator))
            {selectedPlayerId=-1;voteSubmitted=false;}
            roomBadge.text=state.Phase==GamePhase.Lobby?"—":state.Settings.Victory==VictoryMode.RoundCount?$"{state.Round:00} / {state.Settings.RoundCount:00}":$"{state.Round:00}";
            roundCaption.text=state.Phase==GamePhase.Lobby?"대기실":"라운드";
            RefreshSecret(state);
            int remaining=Mathf.Max(0,Mathf.CeilToInt(state.RemainingSeconds));
            timer.text=state.Phase==GamePhase.Lobby||state.Phase==GamePhase.MatchResults?"":$"{remaining/60:00}:{remaining%60:00}";
            timer.EnableInClassList("timer-urgent",remaining<=5&&remaining>0);
            phaseTitle.text=PhaseName(state.Phase);
            var artist=state.Players.FirstOrDefault(p=>p.Id==state.ArtistId);
            phaseDetail.text=state.Phase==GamePhase.Drawing?(network.CanDraw?"—  내 차례":"—  "+artist?.Name):"";
            phaseDetail.style.display=string.IsNullOrEmpty(phaseDetail.text)?DisplayStyle.None:DisplayStyle.Flex;
            bool toolsWereVisible=drawingTools.resolvedStyle.display!=DisplayStyle.None;
            drawingTools.style.display=network.CanDraw?DisplayStyle.Flex:DisplayStyle.None;drawingTools.SetEnabled(network.CanDraw);
            if(network.CanDraw&&!toolsWereVisible)Enter(drawingTools,140,4);
            var key=string.Join("|",state.Players.Select(p=>$"{p.Id},{p.Name},{p.Score},{p.IsLiar},{p.IsSpectator},{p.IsConnected},{p.AvatarColor},{p.Accessory},{p.HasVoted}"))+state.ArtistId+state.Phase;
            if(key!=playerKey){playerKey=key;Players(state);}
            var local=state.Players.FirstOrDefault(p=>p.Id==state.LocalPlayerId);
            var actions=$"{state.Phase}/{state.ArtistId}/{state.CanStart}/{local?.HasVoted}/{local?.HasGuessed}/{selectedPlayerId}/{voteSubmitted}";
            if(actions!=actionKey){actionKey=actions;PhaseActions(state,local);}
            RefreshContext(state);RefreshPlayerStates(state);
            if(phaseChanged)
            {
                previousPhase=state.Phase;Enter(phaseBanner,180,0);
                if(state.Phase==GamePhase.RoleReveal)RoleReveal(state);
                else if(state.Phase==GamePhase.LiarReveal)RevealLiars(state);
                else if(state.Phase==GamePhase.RoundResults||state.Phase==GamePhase.MatchResults)Results(state);
                else CloseModal();
            }
        }
        private bool CanSelectVote(RoomSnapshot state,PlayerView player)
        {
            return state.Phase==GamePhase.Voting&&!state.LocalIsSpectator&&!voteSubmitted
                &&!state.Players.Any(p=>p.Id==state.LocalPlayerId&&p.HasVoted)
                &&player.Id!=state.LocalPlayerId&&player.IsConnected&&!player.IsSpectator;
        }
        private void Players(RoomSnapshot state)
        {
            playerStrip.Clear();playerCards.Clear();playerStatuses.Clear();
            var players=state.Players.Where(p=>!p.IsSpectator).ToArray();
            playerStrip.EnableInClassList("crowded",players.Length>8);
            for(var i=0;i<players.Length;i++)
            {
                var p=players[i];var card=Box(playerStrip,"player");card.userData=p.Id;card.focusable=true;card.tabIndex=0;
                card.EnableInClassList("last-player",i==players.Length-1);playerCards[p.Id]=card;
                var row=Box(card,"row");row.Add(new AvatarElement(p.AvatarColor,p.Accessory));var info=Box(row,"grow");
                Text(info,p.Name+(p.Id==state.LocalPlayerId?" · 나":""),"player-name");card.tooltip=p.Name;
                playerStatuses[p.Id]=Text(info,"","player-status");
                void Activate()
                {
                    var current=network.State;var player=current?.Players.FirstOrDefault(value=>value.Id==p.Id);if(player==null)return;
                    if(current.Phase==GamePhase.Voting)
                    {
                        if(!CanSelectVote(current,player))return;
                        selectedPlayerId=player.Id;RefreshState(current);
                    }
                    else if(player.Id==current.LocalPlayerId)VoiceSettings();else PeerSettings(player);
                }
                card.RegisterCallback<ClickEvent>(_=>Activate());
                card.RegisterCallback<KeyDownEvent>(e=>{if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.Space){Activate();e.StopPropagation();}});
            }
        }
        private void RefreshPlayerStates(RoomSnapshot state)
        {
            foreach(var player in state.Players)
            {
                if(!playerCards.TryGetValue(player.Id,out var card))continue;
                bool speaking=player.IsConnected&&(player.Id==state.LocalPlayerId?voice.IsLocalSpeaking:voice.IsSpeaking(player.VoiceId));
                card.EnableInClassList("active-player",state.Phase==GamePhase.Drawing&&player.Id==state.ArtistId);
                card.EnableInClassList("selected-player",state.Phase==GamePhase.Voting&&player.Id==selectedPlayerId);
                card.EnableInClassList("vote-selectable",CanSelectVote(state,player));card.EnableInClassList("speaking",speaking);
                var label=playerStatuses[player.Id];
                label.text=!player.IsConnected?"연결 끊김":state.Phase==GamePhase.Voting?(player.HasVoted?"투표 완료":player.Id==selectedPlayerId?"선택됨":""):
                    player.IsLiar?"라이어":speaking?"말하는 중":state.Phase==GamePhase.Drawing?(player.Id==state.ArtistId?"그리는 중":""):
                    state.Phase==GamePhase.Lobby||state.Phase==GamePhase.RoundResults||state.Phase==GamePhase.MatchResults?player.Score+"점":"";
            }
        }
        private void RefreshContext(RoomSnapshot state)
        {
            var target=state.Players.FirstOrDefault(p=>p.Id==selectedPlayerId);
            var speakers=state.Players.Where(p=>p.IsConnected&&(p.Id==state.LocalPlayerId?voice.IsLocalSpeaking:voice.IsSpeaking(p.VoiceId))).ToArray();
            string key=state.Phase+"/"+selectedPlayerId+"/"+string.Join(",",speakers.Select(p=>p.Id));
            if(state.Phase==GamePhase.Lobby)key+="/"+state.Players.Length+"/"+state.CanStart;
            if(key!=contextKey)
            {
                contextKey=key;contextInfo.Clear();voteProgress=null;
                if(state.Phase==GamePhase.Voting)
                {
                    Text(contextInfo,target==null?"투표할 플레이어를 선택하세요":"선택한 플레이어","muted");
                    if(target!=null){var avatar=new AvatarElement(target.AvatarColor,target.Accessory);avatar.AddToClassList("vote-avatar");contextInfo.Add(avatar);Text(contextInfo,target.Name,"context-name");}
                    voteProgress=Text(contextInfo,"","muted");
                }
                else if(state.Phase==GamePhase.Discussion||state.Phase==GamePhase.Rebuttal)
                {
                    if(speakers.Length>0)Text(contextInfo,"말하는 중","muted");
                    foreach(var speaker in speakers.Take(3)){var row=Box(contextInfo,"speaker-row row");row.Add(new AvatarElement(speaker.AvatarColor,speaker.Accessory));Text(row,speaker.Name,"speaker-name");}
                }
                else if(state.Phase==GamePhase.Lobby)
                {
                    Text(contextInfo,$"{state.Players.Count(p=>!p.IsSpectator&&p.IsConnected)} / {state.Settings.MaxPlayers}명","context-name");
                    Text(contextInfo,$"라이어 {state.Settings.LiarCount}명","muted");
                    Text(contextInfo,state.Settings.Victory==VictoryMode.RoundCount?$"{state.Settings.RoundCount}라운드":$"목표 {state.Settings.TargetScore}점","muted");
                }
            }
            if(voteProgress!=null)voteProgress.text=$"{state.Players.Count(p=>p.HasVoted&&!p.IsSpectator&&p.IsConnected)} / {state.Players.Count(p=>!p.IsSpectator&&p.IsConnected)}명 투표 완료";
        }
        private void SubmitVote()
        {
            var state=network.State;var target=state?.Players.FirstOrDefault(p=>p.Id==selectedPlayerId);
            if(target==null||!CanSelectVote(state,target))return;
            voteSubmitted=true;network.Vote(target.Id);RefreshState(network.State);
        }
        private void PhaseActions(RoomSnapshot state,PlayerView local)
        {
            phaseActions.Clear();
            switch(state.Phase)
            {
                case GamePhase.Lobby:
                    if(state.IsHost){var b=Button(phaseActions,"게임 시작",network.StartMatch,"primary");b.SetEnabled(state.CanStart);}else Text(phaseActions,"방장 시작 대기","muted");
                    Button(phaseActions,"Steam 초대",()=>{if(!steam.InviteFriends(lobby.RoomCode))Toast(steam.Status);},"secondary");break;
                case GamePhase.Drawing:if(network.CanDraw)Button(phaseActions,"그리기 완료",network.EndTurn,"primary");break;
                case GamePhase.Voting:
                    if(state.LocalIsSpectator){Text(phaseActions,"관전 중","muted");break;}
                    if(local!=null&&local.HasVoted){Text(phaseActions,"투표 완료","muted");break;}
                    var vote=Button(phaseActions,voteSubmitted?"제출 중":"투표하기",SubmitVote,"primary vote-submit");vote.SetEnabled(selectedPlayerId>=0&&!voteSubmitted);
                    Text(phaseActions,"제출 후에는 바꿀 수 없습니다.","rules");break;
                case GamePhase.Guessing:
                    if(state.LocalIsLiar&&local!=null&&!local.HasGuessed)
                    {
                        var guess=new TextField(){maxLength=40};guess.textEdition.placeholder="제시어 입력";guess.AddToClassList("guess-input");phaseActions.Add(guess);
                        var submit=Button(phaseActions,"정답 제출",()=>{if(!string.IsNullOrWhiteSpace(guess.value))network.Guess(guess.value);},"primary");
                        submit.SetEnabled(false);guess.RegisterValueChangedCallback(e=>submit.SetEnabled(!string.IsNullOrWhiteSpace(e.newValue)));
                    }
                    else Text(phaseActions,local!=null&&local.HasGuessed?"제출 완료":"라이어 답변 대기","muted");break;
                case GamePhase.RoundResults:Button(phaseActions,"결과 보기",()=>Results(state),"secondary");break;
                case GamePhase.MatchResults:Button(phaseActions,"최종 결과",()=>Results(state),"secondary");if(state.IsHost)Button(phaseActions,"대기실로",network.ReturnToLobby,"primary");break;
            }
        }
        private static string PhaseName(GamePhase phase)
        {
            switch(phase){case GamePhase.Lobby:return "대기실";case GamePhase.RoleReveal:return "역할 확인";case GamePhase.Drawing:return "그리기";case GamePhase.Discussion:return "토론";case GamePhase.Rebuttal:return "반론";case GamePhase.Voting:return "투표";case GamePhase.LiarReveal:return "라이어 공개";case GamePhase.Guessing:return "정답 추측";case GamePhase.RoundResults:return "라운드 결과";default:return "최종 결과";}
        }

        private void RoleReveal(RoomSnapshot state)
        {
            var modal=Modal(state.LocalIsSpectator?"관전":state.LocalIsLiar?"라이어":"시민");
            var avatar=new AvatarElement(avatarColor,state.LocalIsLiar?3:accessory);avatar.AddToClassList("avatar-preview");modal.Add(avatar);
            Text(modal,"주제  "+state.Topic,"subtitle");Text(modal,string.IsNullOrEmpty(state.Word)?"다른 사람의 그림에서 제시어를 추리하세요.":"제시어  "+state.Word,"section-title");
            if(!state.LocalIsSpectator)Text(modal,state.LocalIsLiar?"다른 사람의 그림을 보고 제시어를 추측하세요.":"제시어를 그림으로 표현하세요.","rules");Button(modal,"확인",CloseModal,"primary");
        }
        private void Results(RoomSnapshot state)
        {
            var modal=Modal(state.Phase==GamePhase.MatchResults?"최종 결과":"라운드 결과");
            Text(modal,"정답은  "+state.Word,"subtitle");
            var list=new ScrollView();list.style.maxHeight=380;modal.Add(list);
            foreach(var p in state.Players.Where(p=>!p.IsSpectator).OrderByDescending(p=>p.Score))
            {
                var row=Box(list,"score-row");var identity=Box(row,"row");identity.Add(new AvatarElement(p.AvatarColor,p.Accessory));
                Text(identity,(state.Winners.Contains(p.Id)?"★ ":"")+p.Name+(p.IsLiar?" · 라이어":" · 시민"),"");
                Text(row,$"{p.Score}점  (+{p.RoundPoints})","player-score");
            }
            Text(modal,state.Summary,"rules");Button(modal,"닫기",CloseModal,"primary");
        }
        private void RevealLiars(RoomSnapshot state)
        {
            var modal=Modal("라이어 공개");
            var identities=Box(modal,"row reveal-liars");
            foreach(var player in state.Players.Where(p=>p.IsLiar))
            {
                var identity=Box(identities,"reveal-liar");var avatar=new AvatarElement(player.AvatarColor,player.Accessory);avatar.AddToClassList("avatar-preview");identity.Add(avatar);
                Text(identity,player.Name,"player-name");Text(identity,player.IsCaught?"지목됨":"지목 안 됨","rules");
            }
            Enter(identities,120,0,reducedMotion?0:400);
            Text(modal,"다음은 라이어의 정답 추측입니다.","rules");
            Button(modal,"확인",CloseModal,"primary");
        }
        private void PeerSettings(PlayerView player)
        {
            var modal=Modal(player.Name+" · 음량");var id=player.VoiceId;
            var volume=new Slider("개별 볼륨",0,1){value=voice.GetPeerVolume(id)};volume.AddToClassList("field");volume.RegisterValueChangedCallback(e=>voice.SetPeerVolume(id,e.newValue));modal.Add(volume);
            var mute=new Toggle("이 사람 음소거"){value=voice.GetPeerMuted(id)};mute.AddToClassList("field");mute.RegisterValueChangedCallback(e=>voice.SetPeerMuted(id,e.newValue));modal.Add(mute);Button(modal,"완료",CloseModal,"primary");
        }
        private void VoiceSettings()
        {
            var modal=Modal("옵션");VoiceForm(modal);Button(modal,"완료",CloseModal,"primary");
        }
        private void VoiceForm(VisualElement modal)
        {
            Choice(modal,"말하기 방식",new[]{"T를 누르는 동안 말하기","목소리 자동 감지"},voice.PushToTalk?0:1,i=>voice.PushToTalk=i==0);
            voice.RefreshMicrophones();var devices=new List<string>{"시스템 기본 마이크"};devices.AddRange(voice.MicrophoneDevices);
            Choice(modal,"입력 장치",devices.ToArray(),Mathf.Max(0,devices.IndexOf(voice.MicrophoneName)),i=>voice.MicrophoneName=i==0?"":devices[i]);
            var mute=new Toggle("내 마이크 끄기"){value=voice.Muted};mute.AddToClassList("field");mute.RegisterValueChangedCallback(e=>voice.Muted=e.newValue);modal.Add(mute);
            var motion=new Toggle("화면 움직임 줄이기"){value=reducedMotion};motion.AddToClassList("field");motion.RegisterValueChangedCallback(e=>{reducedMotion=e.newValue;root.EnableInClassList("reduce-motion",reducedMotion);PlayerPrefs.SetInt("DrawLiar.ReduceMotion",reducedMotion?1:0);PlayerPrefs.Save();});modal.Add(motion);
            Text(modal,"텍스트 입력 중에는 마이크가 꺼집니다.","rules");
        }
        private void Profile()
        {
            if(network.State!=null&&network.State.Phase!=GamePhase.Lobby){Toast("캐릭터는 게임이 끝난 뒤 대기실에서 바꿀 수 있어요.");return;}
            var modal=Modal("커스터마이징");ProfileForm(modal,true);Button(modal,"취소",CloseModal,"secondary");
        }
        private void ProfileForm(VisualElement panel,bool modal)
        {
            var selectedColor=avatarColor;var selectedAccessory=accessory;
            var preview=modal?Box(panel,""):avatarStage;
            void UpdateAvatar(){preview.Clear();var a=new AvatarElement(selectedColor,selectedAccessory);a.AddToClassList(modal?"avatar-preview":"lobby-avatar");preview.Add(a);}
            UpdateAvatar();var nameField=Field(panel,"닉네임",nickname);nameField.maxLength=16;
            Text(panel,"몸 색상","section-title");var colors=Box(panel,"row avatar-colors");var swatches=new List<Button>();
            void SelectColor(int color){selectedColor=color;for(var i=0;i<swatches.Count;i++)swatches[i].EnableInClassList("selected",i==color);UpdateAvatar();}
            foreach(var color in Enumerable.Range(0,AvatarElement.Colors.Length)){var b=Button(colors,"",()=>SelectColor(color),"swatch");b.style.backgroundColor=AvatarElement.Colors[color];b.tooltip="몸 색상 "+(color+1);swatches.Add(b);}
            SelectColor(selectedColor);
            Choice(panel,"작은 장식",new[]{"있는 그대로","작은 왕관","새싹","동그란 안경"},selectedAccessory,i=>{selectedAccessory=i;UpdateAvatar();});
            Button(panel,"이 모습으로 저장",()=>{nickname=string.IsNullOrWhiteSpace(nameField.value)?"그림콩":nameField.value.Trim();avatarColor=selectedColor;accessory=selectedAccessory;PlayerPrefs.SetString("DrawLiar.Name",nickname);PlayerPrefs.SetInt("DrawLiar.Color",avatarColor);PlayerPrefs.SetInt("DrawLiar.Accessory",accessory);PlayerPrefs.Save();ApplyProfile();if(modal)CloseModal();else Navigate(LobbyScreen.Main);},"primary next-button");
        }
        private void TimeForm(VisualElement panel)
        {
            panel.AddToClassList("time-form");
            Int(panel,"역할 확인",draft.RoleSeconds,3,30,v=>draft.RoleSeconds=v);Int(panel,"한 사람 그림",draft.DrawSeconds,5,180,v=>draft.DrawSeconds=v);
            Int(panel,"자유 토론",draft.DiscussionSeconds,5,300,v=>draft.DiscussionSeconds=v);
            Int(panel,"반론",draft.RebuttalSeconds,0,180,v=>draft.RebuttalSeconds=v);
            Int(panel,"투표",draft.VoteSeconds,5,120,v=>draft.VoteSeconds=v);
            Int(panel,"라이어 공개",draft.RevealSeconds,3,30,v=>draft.RevealSeconds=v);
            Int(panel,"정답 추측",draft.GuessSeconds,5,120,v=>draft.GuessSeconds=v);
            Int(panel,"라운드 결과",draft.ResultSeconds,5,60,v=>draft.ResultSeconds=v);
            Text(panel,"단위: 초 · 반론 0초는 건너뛰기\n그림은 완료 버튼으로 일찍 넘길 수 있습니다.","rules");
            Button(panel,"완료",()=>Navigate(LobbyScreen.CreateRules),"primary next-button");
        }
        private void TopicSelectionForm(VisualElement panel)
        {
            Text(panel,"선택한 주제 중에서 매 라운드 무작위로 출제됩니다.","subtitle");
            var names=GameDataStore.Load().Topics.Select(t=>t.Name).ToArray();
            var selected=new HashSet<string>((draft.Topics??names).Intersect(names));
            var controls=Box(panel,"row spread");var count=Text(controls,"","topic-count");var bulk=Box(controls,"row");
            var list=new ScrollView(ScrollViewMode.Vertical);list.AddToClassList("topic-list");panel.Add(list);
            var grid=Box(list,"topic-grid");var buttons=new Dictionary<string,Button>();
            Button(panel,"나만의 주제 만들기",()=>Navigate(LobbyScreen.Topics),"secondary");
            var done=Button(panel,"선택 완료",()=>Navigate(LobbyScreen.CreateDetails),"primary next-button");
            void Refresh()
            {
                draft.Topics=names.Where(selected.Contains).ToArray();
                count.text=$"{selected.Count} / {names.Length}개 선택";done.SetEnabled(selected.Count>0);
                foreach(var entry in buttons)
                {
                    bool enabled=selected.Contains(entry.Key);
                    entry.Value.text=(enabled?"✓  ":"")+entry.Key;
                    entry.Value.EnableInClassList("topic-selected",enabled);
                    entry.Value.tooltip=entry.Key+(enabled?" · 선택됨 · 클릭하면 제외":" · 제외됨 · 클릭하면 선택");
                }
            }
            Button(bulk,"전체 선택",()=>{selected.UnionWith(names);Refresh();},"secondary");
            Button(bulk,"초기화",()=>{selected.Clear();Refresh();},"secondary");
            foreach(var name in names)
            {
                var key=name;
                buttons[key]=Button(grid,key,()=>{if(!selected.Add(key))selected.Remove(key);Refresh();},"topic-toggle");
            }
            Refresh();
        }
        private void TopicsForm(VisualElement panel)
        {
            var nameField=Field(panel,"주제 이름","");nameField.maxLength=40;
            var words=new TextField("제시어"){multiline=true};words.AddToClassList("field");words.style.height=130;panel.Add(words);
            Text(panel,"쉼표나 줄바꿈으로 구분해요. 예: 사과, 바나나, 수박","rules");
            Button(panel,"주제 저장",()=> {try{GameDataStore.UpsertCustomTopic(nameField.value,words.value);draft.Topics=(draft.Topics??Array.Empty<string>()).Append(GameRules.CleanText(nameField.value,40)).Distinct().ToArray();Navigate(LobbyScreen.TopicSelection);Toast("새 주제를 저장했습니다.");}catch(Exception ex){Toast(ex.Message);}},"primary");
            var list=new ScrollView();list.style.maxHeight=150;panel.Add(list);
            foreach(var t in GameDataStore.LoadCustomTopics())
            {
                var row=Box(list,"room-entry");Button(row,t.Name,()=>{nameField.value=t.Name;words.value=string.Join("\n",t.Words);},"secondary");Button(row,"삭제",()=> {GameDataStore.DeleteCustomTopic(t.Name);draft.Topics=draft.Topics?.Where(name=>name!=t.Name).ToArray();Home();},"danger");
            }
        }

        private void ApplyProfile()=>network.SetProfile(nickname,avatarColor,accessory,voice.LocalVoiceId);
        private void OnChat(ChatLine line)
        {
            if(chatHistory==null)return;
            var entry=Box(chatHistory,"chat-entry");Text(entry,line.Name,"chat-name");var message=Text(entry,line.Text,"chat-message");message.tooltip=line.Text;
            while(chatHistory.contentContainer.childCount>6)chatHistory.contentContainer.ElementAt(0).RemoveFromHierarchy();
            Enter(entry,140,2);chatHistory.schedule.Execute(()=>{if(entry.panel!=null)chatHistory?.ScrollTo(entry);}).StartingIn(20);
        }
        private void Tick()
        {
            if(!string.IsNullOrEmpty(pendingInvitation)&&!runningAction&&!lobby.IsBusy)
            {
                string code=pendingInvitation;pendingInvitation="";
                Run(()=>JoinInvite(code));
            }
            if(voiceBadge!=null){voiceBadge.text=voice.Muted?"마이크 꺼짐":voice.IsLocalSpeaking?"● 말하는 중":voice.PushToTalk?"T  누르고 말하기":"음성 자동 감지";voiceBadge.EnableInClassList("voice-on",voice.IsLocalSpeaking);}
            var state=network.State;if(state==null||!inRoom)return;
            RefreshPlayerStates(state);RefreshContext(state);
        }
        private void RefreshServiceStatus()
        {
            if(serviceNotice!=null)serviceNotice.text=lobby.Status;
            else if(lastServiceStatus!=lobby.Status)Toast(lobby.Status);
            lastServiceStatus=lobby.Status;
            if(!lobby.IsBusy)RefreshPublicRooms();
        }
        private async void Run(Func<Task> action)
        {
            if(runningAction||lobby.IsBusy){Toast("연결을 처리하고 있어요. 잠시 기다려 주세요.");return;}
            runningAction=true;
            try{await action();}catch(Exception ex){Toast(ex.Message);Debug.LogWarning(ex.Message);}
            finally{runningAction=false;}
        }
        private VisualElement Modal(string title)
        {
            CloseModal();overlay=Box(root,"overlay enter");var modal=Box(overlay,"modal");Text(modal,title,"title");var current=overlay;current.schedule.Execute(()=>current.RemoveFromClassList("enter")).StartingIn(20);return modal;
        }
        private void CloseModal()
        {
            if(overlay!=null){var previous=overlay;previous.pickingMode=PickingMode.Ignore;previous.SetEnabled(false);previous.AddToClassList("enter");previous.schedule.Execute(()=>previous.RemoveFromHierarchy()).StartingIn(reducedMotion?80:180);overlay=null;}
            var focused=root.focusController.focusedElement as VisualElement;
            voice?.SetTyping(focused!=null&&focused.enabledInHierarchy&&(focused is TextField||focused is IntegerField
                ||focused.GetFirstAncestorOfType<TextField>()!=null||focused.GetFirstAncestorOfType<IntegerField>()!=null));
        }
        private void Enter(VisualElement element,int duration,float offset,int delay=0)
        {
            element.style.transitionDuration=new List<TimeValue>{new TimeValue(0)};
            element.style.opacity=0;element.style.translate=new Translate(0,reducedMotion?0:offset);
            element.AddToClassList("motion-enter");
            element.schedule.Execute(()=>
            {
                element.style.transitionDuration=new List<TimeValue>{new TimeValue(reducedMotion?80:duration,TimeUnit.Millisecond)};
                element.style.opacity=1;element.style.translate=new Translate(0,0);
            }).StartingIn(reducedMotion?20:20+delay);
        }
        private void Toast(string message)
        {
            if(string.IsNullOrWhiteSpace(message))return;root.Q<Label>("toast")?.RemoveFromHierarchy();var label=Text(root,message,"toast");label.name="toast";label.schedule.Execute(()=>label.RemoveFromHierarchy()).StartingIn(6000);
        }
        private static VisualElement Box(VisualElement parent,string classes){var e=new VisualElement();Classes(e,classes);parent.Add(e);return e;}
        private static Label Text(VisualElement parent,string text,string classes){var e=new Label(text);e.enableRichText=false;Classes(e,classes);parent.Add(e);return e;}
        private static Button Button(VisualElement parent,string text,Action clicked,string classes){var e=new Button(clicked){text=text};e.AddToClassList("button");Classes(e,classes);parent.Add(e);return e;}
        private static void Classes(VisualElement e,string classes){foreach(var c in classes.Split(' '))if(c.Length>0)e.AddToClassList(c);}
        private static TextField Field(VisualElement parent,string label,string value){var e=new TextField(label){value=value};e.AddToClassList("field");parent.Add(e);return e;}
        private static void Int(VisualElement parent,string label,int value,int min,int max,Action<int> changed)
        {
            var field=new IntegerField(label){value=value,isDelayed=true};field.AddToClassList("field");field.RegisterValueChangedCallback(e=>{var v=Mathf.Clamp(e.newValue,min,max);field.SetValueWithoutNotify(v);changed(v);});parent.Add(field);
        }
        private static void Choice(VisualElement parent,string label,string[] options,int index,Action<int> changed)
        {
            var field=new DropdownField(label,options.ToList(),Mathf.Clamp(index,0,options.Length-1));field.AddToClassList("field");field.RegisterValueChangedCallback(_=>changed(field.index));parent.Add(field);
        }
    }
}
