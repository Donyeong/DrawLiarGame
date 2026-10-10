using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using L = DrawLiar.DrawLocalization;

namespace DrawLiar
{
    public sealed partial class DrawApp : MonoBehaviour
    {
        private DrawNetworkManager network;
        private LobbyServiceBridge lobby;
        private VisualElement root, content, avatarStage, playerStrip, phaseActions, overlay, phaseBanner, drawingTools, contextInfo, chatbar;
        private Label word, topic, role, phaseTitle, phaseDetail, timer, roomBadge, roundCaption, serviceNotice, voteProgress;
        private Button secretToggle, chatOpen;
        private TextField chatInput;
        private readonly DrawGameClock _timerClock=new DrawGameClock();
        private DrawGameTimer _hudTimer;
        private ScrollView chatHistory;
        private VisualElement _pcRoomChat;
        private PanelSettings panelSettings;
        private bool secretHidden, voteSubmitted;
        private int selectedPlayerId=-1;
        private bool _hasSelectedPlayer, _judgmentSubmitted;
        private int _ballotVersion=-1;
        private int _voteSubmissionVersion;
        private VisualElement _pcLeftPlayers, _pcRightPlayers, _pcCenter, _accusedSpotlight, _speechLayer;
        private Label _spectatorCount, _judgmentCounts, _judgmentProgress;
        private string _accusedKey="";
        private const float SPEECH_MESSAGE_SECONDS = 6f;
        private sealed class SpeechMessage { public string Text; public float Expires; public Label Label; }
        private sealed class SpeechState { public readonly Queue<SpeechMessage> Messages=new Queue<SpeechMessage>(); }
        private sealed class SpeechPlacement { public VisualElement Element; public Rect Bounds; public bool Left; }
        private readonly Dictionary<int,SpeechState> _speeches=new Dictionary<int,SpeechState>();
        private readonly Dictionary<int,VisualElement> _speechVisuals=new Dictionary<int,VisualElement>();
        private string contextKey="";
        private DrawingSurface surface;
        private ScrollView publicRoomList;
        private string lastServiceStatus="";
        private string nickname;
        private int avatarColor;
        private long accessory;
        private enum LobbyScreen { Login, Main, JoinCode, CreateRoom, Topics, Options, Customize, Account, Friends, Shop, Notifications, InviteFriends }
        private LobbyScreen lobbyScreen = LobbyScreen.Login;
        private LobbyScreen _workshopReturnScreen = LobbyScreen.Main;
        private bool inRoom;
        private bool runningAction;
        private GamePhase previousPhase=(GamePhase)(-1);
        private string playerKey="", actionKey="";
        private readonly Dictionary<int,VisualElement> playerCards=new Dictionary<int,VisualElement>();
        private readonly Dictionary<int,Label> playerStatuses=new Dictionary<int,Label>();
        private readonly Dictionary<int,Label> _playerScores=new Dictionary<int,Label>();
        private readonly Dictionary<int,DrawVoteStack> _playerVoteStacks=new Dictionary<int,DrawVoteStack>();
        private readonly List<DrawStroke> pendingStrokes=new List<DrawStroke>();
        private RoomSettings draft=new RoomSettings();
        private ScrollView _createRoomScroll;
        private Vector2 _createRoomScrollOffset;
        private bool _createRulesExpanded, _createTimeExpanded, _createTopicsExpanded;
        private VisualElement friendList;
        private VisualElement shopList;
        private readonly Dictionary<AvatarPartSlot,long> _shopPreviewParts=new Dictionary<AvatarPartSlot,long>();
        private const int SHOP_SET_FILTER=-2;
        private int _shopPartFilter=-1;
        private AvatarPartSlot _customizePartSlot=AvatarPartSlot.Head;
        private Label _shopPreviewStatus;
        private Button _shopPreviewReset;
        private MobileUILayout _mobileLayout;
#if UNITY_WEBGL && !UNITY_EDITOR
        private DrawBrowserTextInput _browserTextInput;
        private bool _webRenderedMode, _webLayoutRebuildPending;
#endif
        private VisualElement _mobileRoomStack, _mobileWorkspace, _mobileControls, _mobileSecret, _mobileCanvas, _mobileRoster, _mobileActions, _mobileChatSheet;
        private Label _mobileRoundInfo, _mobileRoundHint, _mobileLiveRound;
        private Button _mobileRoomCode;
        private ScrollView _mobileRoomScroll;
        private ScrollView _mobileRosterScroll;
        private Label _mobileRosterTitle;
        private int _chatTransitionVersion;
        private bool _backgroundRoomRefresh = true, _roomRefreshRunning;
        [SerializeField,Min(1f)] private float _roomRefreshIntervalSeconds=5f;
        private string _roomSearch = "";
        private string _roomSearchDraft = "";
        private TextField _roomSearchField;
        private bool _roomSearchPending;
        private float _nextRoomRefresh;
        private VisualElement _roomOptionsEditor;
        private RoomSettings _roomOptionsDraft;
        private bool _roomOptionsSaving;
        private VisualElement _lobbyChatPanel;
        private ScrollView _lobbyChatHistory;
        private TextField _lobbyChatInput;
        private Button _lobbyChatSend, _mobileLobbyChatButton;
        private Label _lobbyChatStatus, _lobbyChatCount;
        private LobbyScreen? _utilityPopupScreen;
        private VisualElement _popupReturnFocus;
        private string _lobbyChatDraft="";
        private int _lobbyChatDraftVersion;
#if ENABLE_INPUT_SYSTEM
        private UnityEngine.InputSystem.Keyboard _lobbyChatKeyboard;
        private bool _lobbyChatComposing;
        private int _lobbyChatCompositionFrame=-1;
#endif
        private readonly Dictionary<long,VisualElement> _lobbyChatLines=new Dictionary<long,VisualElement>();
        private bool IsMobile => _mobileLayout != null && _mobileLayout.IsMobile;
        private float RoomRefreshInterval => Mathf.Max(1f,_roomRefreshIntervalSeconds);
        private sealed class LocalizedValue
        {
            public string Source;
            public object[] Arguments;
            public string Resolve()=>Arguments.Length==0?L.Text(Source):L.Format(Source,Arguments);
            public bool Matches(string source,object[] args)=>Source==source&&Arguments.SequenceEqual(args);
        }
        private static readonly ConditionalWeakTable<TextElement,LocalizedValue> LOCALIZED_TEXT = new ConditionalWeakTable<TextElement,LocalizedValue>();
        private static readonly ConditionalWeakTable<VisualElement,LocalizedValue> LOCALIZED_TOOLTIPS = new ConditionalWeakTable<VisualElement,LocalizedValue>();
        private static readonly ConditionalWeakTable<TextField,LocalizedValue> LOCALIZED_PLACEHOLDERS = new ConditionalWeakTable<TextField,LocalizedValue>();
        private static readonly ConditionalWeakTable<DropdownField,string[]> LOCALIZED_CHOICES = new ConditionalWeakTable<DropdownField,string[]>();

        private void OnEnable()
        {
            L.LanguageChanged+=OnLanguageChanged;
            AttachSocialEvents();
            AttachRoomPasswordEvents();
            AttachTopicWorkshopEvents();
            AttachMatchRewardEvents();
            AttachCommerceEvents();
#if UNITY_WEBGL && !UNITY_EDITOR
            if(root!=null)_browserTextInput=new DrawBrowserTextInput(root,ChatShortcutTarget,OpenChatShortcut);
#endif
#if ENABLE_INPUT_SYSTEM
            if(_lobbyChatPanel!=null)AttachLobbyChatKeyboard();
#endif
        }
        private void OnDisable()
        {
            ResetTextPresentation();
            ClearJudgmentCoinToss();
            HideModeTooltip();
            CloseRoomTopicWorkshop(false);
            ClearDrawingPreview();
            ResetGuessingInput(true);
            CloseRoomCustomization(false,true);
            ClosePublicProfile(false);
            L.LanguageChanged-=OnLanguageChanged;
            DetachSocialEvents();
            DetachRoomPasswordEvents();
            DetachTopicWorkshopEvents();SuspendTopicWorkshopPage();DetachMatchRewardEvents();
            ClearRoomPasswordSecrets();
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.Dispose();_browserTextInput=null;
#endif
#if ENABLE_INPUT_SYSTEM
            DetachLobbyChatKeyboard();
#endif
        }

        private void Start()
        {
            network=GetComponent<DrawNetworkManager>();
            lobby=GetComponent<LobbyServiceBridge>();
            lobby.Initialize(network);
            draft.Topics=GameDataStore.Load().Topics.Select(t=>t.Name).ToArray();
            nickname=PlayerPrefs.GetString("DrawLiar.Name","동글이"+UnityEngine.Random.Range(10,99));
            avatarColor=Mathf.Clamp(PlayerPrefs.GetInt("DrawLiar.Color",0),0,AvatarElement.Colors.Length-1);
            accessory=DrawAvatarEquipmentStore.Load();
#if UNITY_WEBGL && !UNITY_EDITOR
            var panel=panelSettings=DrawLocalizedTypography.CreateBrowserPanel();
#else
            var panel=panelSettings=ScriptableObject.CreateInstance<PanelSettings>();
#endif
            panel.themeStyleSheet=Resources.Load<ThemeStyleSheet>("DrawLiar/DrawLiarTheme");
            panel.scaleMode=PanelScaleMode.ScaleWithScreenSize;panel.referenceResolution=new Vector2Int(1600,1000);
            panel.screenMatchMode=PanelScreenMatchMode.MatchWidthOrHeight;panel.match=.5f;
            var document=gameObject.AddComponent<UIDocument>();document.panelSettings=panel;
            root=document.rootVisualElement;root.AddToClassList("app");
            root.styleSheets.Add(Resources.Load<StyleSheet>("DrawLiar/DrawLiar"));
            root.styleSheets.Add(Resources.Load<StyleSheet>("DrawLiar/DrawLiarMobile"));
            DrawLocalizedTypography.Apply(root);root.EnableInClassList("rtl",L.IsRightToLeft);
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput=new DrawBrowserTextInput(root,ChatShortcutTarget,OpenChatShortcut);
#endif
            _mobileLayout=new MobileUILayout(root,panelSettings);
#if UNITY_WEBGL && !UNITY_EDITOR
            _webRenderedMode=IsMobile;
#endif
            _mobileLayout.LayoutChanged+=OnMobileLayoutChanged;
            root.RegisterCallback<GeometryChangedEvent>(_=>
            {
                root.EnableInClassList("compact",root.contentRect.width<1400||root.contentRect.height<880);
                if(inRoom&&!IsMobile)RefreshPcRoomGeometry(_pcCenter?.Q<VisualElement>("pc-game-canvas"));
            });
            root.RegisterCallback<KeyDownEvent>(ChatFocusShortcut,TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(ChatNavigationSubmit,TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(RoomChatOutsidePointer,TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(_=>HideModeTooltip(),TrickleDown.TrickleDown);
            root.RegisterCallback<FocusInEvent>(_=>{if(IsRoomModeTooltipBlocked(_modeTooltipTarget))HideModeTooltip();},TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(RoomShortcut,TrickleDown.TrickleDown);
            network.StateChanged+=RefreshState;network.Kicked+=OnRoomKicked;network.Notice+=message=>
            {
                if(_clearOwnPending){_clearOwnPending=false;if(inRoom&&network.State!=null)RefreshDrawingInteractions(network.State);}
                if(voteSubmitted&&IsNominationPhase(network.State))
                {voteSubmitted=false;actionKey="";RefreshState(network.State);}
                if(_judgmentSubmitted&&network.State?.Phase==GamePhase.Rebuttal&&!network.State.Players.Any(p=>p.Id==network.State.LocalPlayerId&&p.HasJudged))
                {_judgmentSubmitted=false;actionKey="";RefreshState(network.State);}
                DrawAudio.Instance?.Play(DrawSound.UiError);Toast(message);
            };
            network.StrokeReceived+=s=> { if(surface!=null)surface.Apply(s);else pendingStrokes.Add(s); };
            network.CanvasCleared+=()=>{surface?.ClearCanvas();pendingStrokes.Clear();};
            network.AuthorDrawingChanged+=OnAuthorDrawingChanged;
            network.ChatReceived+=OnChat;
            lobby.Changed+=RefreshServiceStatus;
            lobby.ProfileChanged+=OnProfileChanged;
            AttachMatchRewardEvents();
            lobby.LobbyChatChanged+=RefreshLobbyChat;
            lobby.LobbyChatNotice+=Toast;
            AttachSocialEvents();
            AttachRoomPasswordEvents();
            AttachTopicWorkshopEvents();
            content=Box(root,"grow app-content");Home();
            root.schedule.Execute(Tick).Every(100);
            root.schedule.Execute(RefreshRoomsInBackground).Every(1000);
            root.schedule.Execute(RefreshSocialControls).Every(1000);
            root.schedule.Execute(RefreshCommerceControls).Every(1000);
#if UNITY_WEBGL && !UNITY_EDITOR
            if(lobby.HasPendingRoomInvite)Run(async()=>
            {
                if(!lobby.IsAuthenticated)await lobby.GuestLoginAsync();
                Navigate(LobbyScreen.Main);await lobby.TryJoinInviteAsync();
            });
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args=Environment.GetCommandLineArgs();
            string Arg(string key) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:""; }
            if(!string.IsNullOrEmpty(Arg("-drawName"))){nickname=Arg("-drawName");Home();}
            if(args.Contains("-drawDevelopmentLogin"))Run(async()=>
            {
                await lobby.DevelopmentLoginAsync(nickname);Navigate(LobbyScreen.Main);
                if(args.Contains("-drawOnlineHost"))
                {
                    draft.IsPrivate=true;draft.RoomName="DrawLiar 연결 검사";
                    string password=await RequestRoomPasswordAsync(new RoomPasswordPrompt("",draft.RoomName,"비밀번호는 4~32자로 입력하세요."),destroyCancellationToken);
                    try{await lobby.HostAsync(draft.Copy(),password);}finally{password="";}
                }
                else if(!string.IsNullOrEmpty(Arg("-drawOnlineJoin")))await lobby.JoinCodeAsync(Arg("-drawOnlineJoin"));
            });
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
            ResetTextPresentation();
            ClearJudgmentCoinToss();
            _pcRoomChat=null;_pcChatExpanded=false;_chatFocusVersion++;
            if(lobbyScreen!=LobbyScreen.Topics||!lobby.IsAuthenticated||_workshopAccountId!=lobby.Profile?.AccountId)EndTopicWorkshopPage();else ClearTopicWorkshopView();
            ClearDrawingPreview();_clearOwnButton=null;_clearOwnPending=false;
            _timerClock.Reset();_hudTimer=null;
            ResetGuessingInput(true);
            CloseRoomCustomization(false,true);
            CancelRoomPasswordPrompt();
            if(lobbyScreen!=LobbyScreen.CreateRoom&&lobbyScreen!=LobbyScreen.Topics)ClearCreateRoomPassword();
            else ClearRoomPasswordField(root?.Q<TextField>("create-room-password"));
            if(_createRoomScroll!=null)
            {
                root.focusController?.focusedElement?.Blur();_createRoomScrollOffset=_createRoomScroll.scrollOffset;_createRoomScroll=null;
            }
            CloseModal();
            SyncProfile();
            ResetLobbyChatView();_mobileLobbyChatButton=null;
            inRoom=false;ResetMobileRoomView();surface=null;publicRoomList=null;_roomSearchField=null;friendList=null;shopList=null;avatarStage=null;serviceNotice=null;_shopPreviewStatus=null;_shopPreviewReset=null;chatHistory=null;chatInput=null;content.Clear();playerCards.Clear();playerStatuses.Clear();_playerScores.Clear();_playerVoteStacks.Clear();previousPhase=(GamePhase)(-1);
            ClearShopCartView();
            _speeches.Clear();_speechVisuals.Clear();_speechLayer=null;_pcLeftPlayers=_pcRightPlayers=_pcCenter=_accusedSpotlight=null;
            if(!lobby.IsAuthenticated)lobbyScreen=LobbyScreen.Login;
            if(!lobby.IsAuthenticated)_lobbyChatDraft="";
            if(lobbyScreen!=LobbyScreen.Shop)ClearShopPreview();
            if(lobbyScreen==LobbyScreen.Main)_nextRoomRefresh=0;
            root.RemoveFromClassList("in-game");root.RemoveFromClassList("pc-game");content.RemoveFromClassList("room-layout");root.AddToClassList("at-home");
            if(!IsMobile){panelSettings.referenceResolution=new Vector2Int(1600,1000);panelSettings.match=.5f;}
            _mobileLayout.Refresh();
            if(lobbyScreen==LobbyScreen.CreateRoom){CreateRoomPage();HideMobileScrollers();return;}
            if(lobbyScreen==LobbyScreen.Shop){ShopPage();HideMobileScrollers();return;}
            if(IsMobile){MobileHome();HideMobileScrollers();return;}
            if(lobbyScreen==LobbyScreen.Main){MainLobby(content);return;}
            var home=Box(content,"home");
            var layout=Box(home,"home-layout");var character=Box(layout,"character-side");
            if(lobbyScreen==LobbyScreen.Login)
            {
                BrandLogo(character,"game-logo");
            }
            else
            {
                avatarStage=Box(character,"character-stage");UpdateLobbyAvatar(avatarColor,accessory);
                LeveledName(character,nickname,OwnLevel,"character-name","character-name",lobby.Profile?.AccountId,OwnSubscriberBadge);
                var customize=Button(character,"꾸미기",()=>Navigate(LobbyScreen.Customize),"customize-button");
                if(lobbyScreen==LobbyScreen.Customize)customize.style.visibility=UnityEngine.UIElements.Visibility.Hidden;
            }
            var panel=Box(layout,"home-panel");
            serviceNotice=null;
            var top=Box(panel,"screen-heading");
            if(lobbyScreen!=LobbyScreen.Login)Button(top,"← 뒤로",Back,"back-button",DrawSound.UiCancel);
            VisualElement body;
            if(lobbyScreen==LobbyScreen.Topics)
            {
                panel.AddToClassList("topic-workshop-panel");
                body=Box(panel,"screen-body");TopicWorkshopHeading(body,"screen-title");
                var scroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);scroll.name="workshop-page-scroll";scroll.AddToClassList("workshop-page-scroll");body.Add(scroll);
                TopicsForm(Box(scroll,"workshop-page-form"));
                float previousHeight=-1;
                void SizeWorkshopPanel()
                {
                    if(panel.panel==null||home.parent!=content)return;
                    float height=content.contentRect.height;
                    if(height<=0||Mathf.Approximately(height,previousHeight))return;
                    previousHeight=height;panel.style.height=height;panel.style.maxHeight=height;
                }
                home.RegisterCallback<GeometryChangedEvent>(_=>SizeWorkshopPanel());SizeWorkshopPanel();
            }
            else body=Box(panel,"screen-body");
            if(lobbyScreen!=LobbyScreen.Topics)BuildLobbyScreen(body);
            if(lobbyScreen==LobbyScreen.Login)CreateServiceNotice(body);
            Enter(panel,260,14);
        }

        private void BuildLobbyScreen(VisualElement body)
        {
                switch(lobbyScreen)
                {
                    case LobbyScreen.Login:Text(body,"시작하기","screen-title");LoginForm(body);break;
                    case LobbyScreen.JoinCode:Text(body,"코드로 참가","screen-title");JoinForm(body);break;
                    case LobbyScreen.Topics:TopicWorkshopHeading(body,"screen-title");TopicsForm(body);break;
                    case LobbyScreen.Customize:
                        var customizeHeading=Box(body,"row spread customize-heading");
                        Text(customizeHeading,"커스터마이징","screen-title");
                        if(!IsMobile||Application.platform==RuntimePlatform.WebGLPlayer)
                            Button(customizeHeading,"상점",()=>{Navigate(LobbyScreen.Shop);Run(lobby.RefreshShopAsync);},"secondary").name="customize-shop";
                        ProfileForm(body);break;
                }
        }

        private void MobileHome()
        {
            bool signingIn=lobbyScreen==LobbyScreen.Login;
            if(signingIn)
            {
                var screen=Box(content,"mobile-login-screen");var visual=Box(screen,"mobile-login-visual");
                BrandLogo(visual,"mobile-login-logo");
                var actions=Box(screen,"mobile-login-actions");LoginForm(actions);LanguageSelector(actions);CreateServiceNotice(actions);
                DrawUIMotion.Stagger(screen,28,220,8);return;
            }
            if(!signingIn)MobileTopbar();
            if(lobbyScreen==LobbyScreen.Main){MobileLobby();MobileNavigation();return;}
            if(lobbyScreen==LobbyScreen.Customize)MobileProfilePreview();
            var scroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);scroll.AddToClassList("mobile-home-scroll");content.Add(scroll);
            var home=Box(scroll,"mobile-home");
            var body=Box(home,"home-panel mobile-page-body");BuildLobbyScreen(body);
            MobileNavigation();
            DrawUIMotion.Stagger(home,28,220,8);
        }

        private void MobileLobby()
        {
            var page=Box(content,"mobile-lobby");var profile=Box(page,"mobile-lobby-profile row");
            avatarStage=Box(profile,"mobile-lobby-avatar");UpdateLobbyAvatar(avatarColor,accessory);
            BindProfileTarget(avatarStage,lobby.Profile?.AccountId);
            var identity=Box(profile,"mobile-lobby-identity grow");LeveledName(identity,nickname,OwnLevel,"mobile-lobby-name","mobile-lobby-name",lobby.Profile?.AccountId,OwnSubscriberBadge);
            Text(identity,lobby.Profile?.HasGoogleAccount==true?"Google":"게스트","mobile-account-state");
            Button(identity,"꾸미기",()=>Navigate(LobbyScreen.Customize),"secondary mobile-customize");
            LobbyRoomActions(page,"mobile-lobby-actions row");
            var heading=Box(page,"mobile-room-list-heading row");Text(heading,"공개 방","mobile-card-title grow");
            IconButton(heading,"새로고침",DrawUIIcon.Kind.Refresh,()=>SearchRooms(_roomSearchDraft),"room-refresh");
            RoomSearch(page);
            publicRoomList=DrawSmoothScroll.Create(ScrollViewMode.Vertical);Classes(publicRoomList,"room-list lobby-room-list mobile-lobby-room-list");page.Add(publicRoomList);RefreshPublicRooms();
            _mobileLobbyChatButton=Button(page,"로비 채팅",OpenLobbyChat,"secondary mobile-lobby-chat-open");_mobileLobbyChatButton.name="mobile-lobby-chat-open";
            DrawUIMotion.Stagger(page,28,220,8);
        }

        private void MainLobby(VisualElement panel)
        {
            panelSettings.referenceResolution=new Vector2Int(1600,900);
            var screen=Box(panel,"pc-lobby");screen.name="pc-lobby";
            var header=Box(screen,"row pc-lobby-header");Text(header,"로비","lobby-title grow");
            Text(header,"{0} 코인","mobile-status-pill lobby-coins",lobby.Profile?.Coins??0);
            SocialBell(header);
            IconButton(header,"설정",DrawUIIcon.Kind.Settings,()=>Navigate(LobbyScreen.Options),"lobby-settings");
            var workspace=Box(screen,"row pc-lobby-workspace grow");
            var sidebar=Box(workspace,"pc-lobby-sidebar");sidebar.name="pc-lobby-sidebar";
            var profile=Box(sidebar,"pc-lobby-card pc-lobby-profile");
            avatarStage=Box(profile,"pc-lobby-avatar");UpdateLobbyAvatar(avatarColor,accessory);
            BindProfileTarget(avatarStage,lobby.Profile?.AccountId);
            LeveledName(profile,nickname,OwnLevel,"pc-lobby-name","pc-lobby-name",lobby.Profile?.AccountId,OwnSubscriberBadge);Text(profile,lobby.Profile?.HasGoogleAccount==true?"Google":"게스트","pc-lobby-account");
            Button(profile,"꾸미기",()=>Navigate(LobbyScreen.Customize),"secondary pc-lobby-customize");
            var services=Box(sidebar,"pc-lobby-card pc-lobby-services");
            Button(services,"상점",()=>{Navigate(LobbyScreen.Shop);Run(lobby.RefreshShopAsync);},"secondary");
            Button(services,"친구",()=>Navigate(LobbyScreen.Friends),"secondary");
            Button(services,"내 계정",()=>Navigate(LobbyScreen.Account),"secondary pc-lobby-service-last");
            var main=Box(workspace,"pc-lobby-main grow");main.name="pc-lobby-main";
            var rooms=Box(main,"pc-lobby-card pc-lobby-rooms grow");rooms.name="pc-lobby-rooms";
            var heading=Box(rooms,"row pc-lobby-room-heading");Text(heading,"공개 방","section-title grow");
            LobbyRoomActions(heading,"row pc-lobby-room-actions");
            RoomSearch(rooms);
            IconButton(rooms.Q<VisualElement>(className:"room-search"),"새로고침",DrawUIIcon.Kind.Refresh,()=>SearchRooms(_roomSearchDraft),"room-refresh");
            publicRoomList=DrawSmoothScroll.Create(ScrollViewMode.Vertical);Classes(publicRoomList,"room-list lobby-room-list pc-lobby-room-list grow");rooms.Add(publicRoomList);RefreshPublicRooms();
            BuildLobbyChat(main,false);
            DrawUIMotion.Stagger(screen,28,220,8);
        }

        private void LobbyRoomActions(VisualElement parent,string classes)
        {
            var actions=Box(parent,classes);
            Button(actions,"주제 관리",()=>Navigate(LobbyScreen.Topics),"secondary grow").name="lobby-manage-topics";
            Button(actions,"방 만들기",()=>Navigate(LobbyScreen.CreateRoom),"primary menu-orange grow").name="lobby-create";
            Button(actions,"코드로 입장",()=>Navigate(LobbyScreen.JoinCode),"secondary grow mobile-last").name="lobby-join-code";
        }

        private void OpenLobbyChat()
        {
            if(inRoom||!lobby.IsAuthenticated)return;
            var modal=Modal("",false);modal.AddToClassList("lobby-chat-modal");
            var heading=Box(modal,"row lobby-chat-modal-heading");Text(heading,"로비 채팅","title grow");
            Button(heading,"닫기",CloseModal,"secondary lobby-chat-close",DrawSound.UiCancel);
            BuildLobbyChat(modal,true);
            HideMobileScrollers();
        }

        private void BuildLobbyChat(VisualElement parent,bool modal)
        {
            ResetLobbyChatView();
            var panel=_lobbyChatPanel=Box(parent,modal?"lobby-chat-panel lobby-chat-in-modal grow":"pc-lobby-card lobby-chat-panel");
            var heading=Box(panel,"row lobby-chat-heading");
            if(!modal)Text(heading,"로비 채팅","section-title grow");
            _lobbyChatCount=Text(heading,"접속 {0}명","muted lobby-chat-count",lobby.LobbyMemberCount);_lobbyChatCount.name="lobby-chat-count";
            _lobbyChatStatus=RawText(panel,lobby.LobbyChatStatus,"muted lobby-chat-status");_lobbyChatStatus.name="lobby-chat-status";
            _lobbyChatHistory=DrawSmoothScroll.Create(ScrollViewMode.Vertical);_lobbyChatHistory.name="lobby-chat-history";_lobbyChatHistory.AddToClassList("lobby-chat-history");panel.Add(_lobbyChatHistory);
            var composer=Box(panel,"row lobby-chat-composer");
            var input=_lobbyChatInput=new TextField{maxLength=DrawLobbyChatClient.MAXIMUM_TEXT_LENGTH,value=_lobbyChatDraft};
            input.name="lobby-chat-input";Classes(input,"field lobby-chat-input grow");Placeholder(input,"메시지를 입력하세요");SetTooltip(input,"메시지를 입력하세요");composer.Add(input);
            input.textEdition.autoCorrection=false;
            input.RegisterValueChangedCallback(e=>{if(!ReferenceEquals(e.target,input))return;_lobbyChatDraft=e.newValue;_lobbyChatDraftVersion++;RefreshLobbyChatComposer();});
#if ENABLE_INPUT_SYSTEM
            AttachLobbyChatKeyboard();
#endif
            input.RegisterCallback<KeyDownEvent>(e=>
            {
                if(e.keyCode==KeyCode.Escape)
                {
                    CloseLobbyChatInput(input,modal);e.StopImmediatePropagation();return;
                }
                if(e.keyCode!=KeyCode.Return&&e.keyCode!=KeyCode.KeypadEnter)return;
#if UNITY_WEBGL && !UNITY_EDITOR
                SubmitLobbyChatInput(input,modal);
#elif ENABLE_INPUT_SYSTEM
                if(!_lobbyChatComposing&&Time.frameCount!=_lobbyChatCompositionFrame)SubmitLobbyChatInput(input,modal);
#else
                if(string.IsNullOrEmpty(Input.compositionString))SubmitLobbyChatInput(input,modal);
#endif
                e.StopImmediatePropagation();
            },TrickleDown.TrickleDown);
            _lobbyChatSend=Button(composer,"보내기",()=>_=SendLobbyChatAsync(),"primary lobby-chat-send",DrawSound.UiConfirm);_lobbyChatSend.name="lobby-chat-send";
            RefreshLobbyChat();
        }

        private void SubmitLobbyChatInput(TextField input,bool modal)
        {
            _chatShortcutFrame=Time.frameCount;
            if(string.IsNullOrWhiteSpace(input.value)){CloseLobbyChatInput(input,modal);return;}
            _=SendLobbyChatAsync();
        }

        private void CloseLobbyChatInput(TextField input,bool modal)
        {
            _chatFocusVersion++;
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.Blur(input,true);
#endif
            input.Blur();if(modal)CloseModal();
        }

        private void ResetLobbyChatView()
        {
            _chatFocusVersion++;
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.Blur(_lobbyChatInput,true);
#endif
#if ENABLE_INPUT_SYSTEM
            DetachLobbyChatKeyboard();
#endif
            _lobbyChatPanel=null;_lobbyChatHistory=null;_lobbyChatInput=null;_lobbyChatSend=null;_lobbyChatCount=null;_lobbyChatStatus=null;_lobbyChatLines.Clear();
        }

#if ENABLE_INPUT_SYSTEM
        private void AttachLobbyChatKeyboard()
        {
            if(_lobbyChatKeyboard!=null)return;
            _lobbyChatKeyboard=UnityEngine.InputSystem.Keyboard.current;
            if(_lobbyChatKeyboard==null)return;
            _lobbyChatKeyboard.onIMECompositionChange+=OnLobbyChatComposition;
            _lobbyChatKeyboard.onTextInput+=OnLobbyChatTextInput;
        }
        private void DetachLobbyChatKeyboard()
        {
            if(_lobbyChatKeyboard!=null)
            {
                _lobbyChatKeyboard.onIMECompositionChange-=OnLobbyChatComposition;
                _lobbyChatKeyboard.onTextInput-=OnLobbyChatTextInput;
            }
            _lobbyChatKeyboard=null;_lobbyChatComposing=false;_lobbyChatCompositionFrame=-1;
        }
        private void OnLobbyChatComposition(UnityEngine.InputSystem.LowLevel.IMECompositionString composition)
        {
            _lobbyChatComposing=composition.Count>0;_lobbyChatCompositionFrame=Time.frameCount;
        }
        private void OnLobbyChatTextInput(char character)
        {
            if(!_lobbyChatComposing)return;
            _lobbyChatComposing=false;_lobbyChatCompositionFrame=Time.frameCount;
        }
#endif

        private void RefreshLobbyChatComposer()
        {
            _lobbyChatSend?.SetEnabled(lobby.IsLobbyChatConnected&&!lobby.IsLobbyChatSending&&!string.IsNullOrWhiteSpace(_lobbyChatInput?.value));
        }

        private void RefreshLobbyChat()
        {
            if(_lobbyChatPanel?.panel==null)return;
            SetText(_lobbyChatCount,"접속 {0}명",lobby.LobbyMemberCount);
            SetRawText(_lobbyChatStatus,lobby.LobbyChatStatus);
            _lobbyChatStatus.style.display=string.IsNullOrEmpty(lobby.LobbyChatStatus)?DisplayStyle.None:DisplayStyle.Flex;
            RefreshLobbyChatComposer();
            var history=_lobbyChatHistory;var messages=lobby.LobbyMessages;
            bool atBottom=_lobbyChatLines.Count==0||history.scrollOffset.y>=history.verticalScroller.highValue-24;
            var ids=new HashSet<long>(messages.Select(message=>message.Id));
            if(messages.Count>0&&_lobbyChatLines.Count>0&&messages[0].Id<_lobbyChatLines.Keys.Min())
            {
                history.Clear();_lobbyChatLines.Clear();atBottom=true;
            }
            foreach(var id in _lobbyChatLines.Keys.Where(id=>!ids.Contains(id)).ToArray()){_lobbyChatLines[id].RemoveFromHierarchy();_lobbyChatLines.Remove(id);}
            bool added=false;
            foreach(var message in messages)
            {
                if(_lobbyChatLines.TryGetValue(message.Id,out var existing))
                {
                    var previous=existing.userData as LobbyChatMessage;
                    if(previous?.DisplayName!=message.DisplayName||previous?.Text!=message.Text||previous?.Level!=message.Level)
                    {
                        var name=existing.Q<Label>(className:"lobby-chat-name");var body=existing.Q<Label>(className:"lobby-chat-message");
                        SetRawText(name,message.DisplayName);name.tooltip=message.DisplayName;existing.Q<DrawLevelBadge>()?.SetLevel(message.Level);SetRawText(body,message.Text);body.tooltip=message.Text;existing.userData=message;
                    }
                    continue;
                }
                var entry=Box(history,"lobby-chat-line");entry.name="lobby-chat-message-"+message.Id;
                var nickname=LeveledName(entry,message.DisplayName,message.Level,"lobby-chat-name","lobby-chat-"+message.Id,message.AccountId);BindProfileTarget(nickname,message.AccountId);
                var text=RawText(entry,message.Text,"lobby-chat-message");text.tooltip=message.Text;
                entry.userData=message;_lobbyChatLines[message.Id]=entry;added=true;
            }
            var empty=history.contentViewport.Q<VisualElement>("lobby-chat-empty");
            if(messages.Count==0&&empty==null){empty=Box(history.contentViewport,"room-list-empty-state");empty.name="lobby-chat-empty";empty.pickingMode=PickingMode.Ignore;Text(empty,"첫 메시지를 보내보세요.","muted").pickingMode=PickingMode.Ignore;}
            else if(messages.Count>0)empty?.RemoveFromHierarchy();
            if(added&&atBottom)history.schedule.Execute(()=>{if(history==_lobbyChatHistory&&history.panel!=null)history.scrollOffset=new Vector2(0,Mathf.Max(0,history.verticalScroller.highValue));}).StartingIn(20);
        }

        private async Task SendLobbyChatAsync()
        {
            var input=_lobbyChatInput;
            if(input?.panel==null||lobby.IsLobbyChatSending||string.IsNullOrWhiteSpace(input.value))return;
            string text=input.value;
            int version=_lobbyChatDraftVersion;
            try
            {
                await lobby.SendLobbyChatAsync(text);
                if(_lobbyChatDraftVersion==version&&_lobbyChatDraft==text)
                {
                    _lobbyChatDraft="";
                    if(_lobbyChatInput?.value==text)_lobbyChatInput.SetValueWithoutNotify("");
                }
            }
            catch(Exception ex){DrawAudio.Instance?.Play(DrawSound.UiError);Toast(ex.Message);}
            finally{RefreshLobbyChatComposer();}
        }

        private void RoomSearch(VisualElement parent)
        {
            var row=Box(parent,"room-search row");
            var search=_roomSearchField=Field(row,"",_roomSearchDraft);search.name="room-search-input";search.maxLength=30;Classes(search,"room-search-input grow");
            search.labelElement.style.display=DisplayStyle.None;Placeholder(search,"방 검색");SetTooltip(search,"방 검색");
            search.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,search))_roomSearchDraft=e.newValue;});
            void Submit(){SearchRooms(search.value);search.Blur();}
            search.RegisterCallback<KeyDownEvent>(e=>{if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){Submit();e.StopPropagation();}},TrickleDown.TrickleDown);
            Button(row,"검색",Submit,"secondary room-search-submit").name="room-search-submit";
        }

        private void MobileTopbar()
        {
            var header=Box(content,"mobile-topbar row");
            if(lobbyScreen==LobbyScreen.Main)Text(header,"로비","mobile-workspace-title mobile-lobby-title grow");
            else
            {
                SetTooltip(Button(header,"‹",Back,"secondary mobile-back",DrawSound.UiCancel),"뒤로");
                Text(header,LobbyTitle(lobbyScreen),"mobile-workspace-title grow");
            }
            if(lobbyScreen==LobbyScreen.Main||lobbyScreen==LobbyScreen.Shop)
                Text(header,"{0} 코인","mobile-status-pill lobby-coins",lobby.Profile?.Coins??0);
            SocialBell(header);
            if(lobbyScreen==LobbyScreen.Main)IconButton(header,"설정",DrawUIIcon.Kind.Settings,()=>Navigate(LobbyScreen.Options),"lobby-settings");
        }

        private void MobileProfilePreview()
        {
            var hero=Box(content,"mobile-profile-hero row");
            avatarStage=Box(hero,"mobile-profile-preview");UpdateLobbyAvatar(avatarColor,accessory);
            var detail=Box(hero,"grow");LeveledName(detail,nickname,OwnLevel,"mobile-card-title","mobile-profile-name",lobby.Profile?.AccountId,OwnSubscriberBadge);
        }

        private void MobileNavigation()
        {
            var navigation=Box(content,"mobile-bottom-nav row");
            MobileTab(navigation,"로비",LobbyScreen.Main,lobbyScreen!=LobbyScreen.Friends&&lobbyScreen!=LobbyScreen.Shop);
            MobileTab(navigation,"상점",LobbyScreen.Shop,lobbyScreen==LobbyScreen.Shop);
            MobileTab(navigation,"친구",LobbyScreen.Friends,lobbyScreen==LobbyScreen.Friends);
        }

        private void MobileTab(VisualElement parent,string title,LobbyScreen destination,bool selected)
        {
            var button=Button(parent,title,()=>
            {
                if(lobbyScreen==destination)return;
                Navigate(destination);
                if(destination==LobbyScreen.Shop)Run(lobby.RefreshShopAsync);
            },"mobile-nav-item grow");
            button.name="lobby-nav-"+destination.ToString().ToLowerInvariant();
            button.EnableInClassList("mobile-nav-selected",selected);
        }

        private static string LobbyTitle(LobbyScreen screen)
        {
            switch(screen){case LobbyScreen.JoinCode:return "방 코드로 입장";case LobbyScreen.CreateRoom:return "방 만들기";case LobbyScreen.Friends:return "친구";case LobbyScreen.Shop:return "상점";case LobbyScreen.Customize:return "캐릭터 꾸미기";case LobbyScreen.Account:return "내 계정";case LobbyScreen.Options:return "옵션";case LobbyScreen.Topics:return "나만의 주제";default:return "함께 플레이";}
        }

        private void UpdateLobbyAvatar(int color,long decoration)
        {
            avatarStage.Clear();var avatar=new AvatarElement(color,decoration);avatar.AddToClassList("lobby-avatar");avatarStage.Add(avatar);
        }

        private void RevealAvatarPreview()
        {
            if(IsMobile&&avatarStage?.panel!=null)avatarStage.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(avatarStage);
        }

        private void SyncProfile()
        {
            var profile=lobby.Profile;
            if(profile==null)return;
            nickname=profile.DisplayName;avatarColor=profile.AvatarColor;accessory=profile.Accessory;
        }

        private void OnProfileChanged()
        {
            if(_guessAccountId!=null&&_guessAccountId!=lobby.Profile?.AccountId)ResetGuessingInput(true);
            if(_roomCustomizeAccountId!=null&&_roomCustomizeAccountId!=lobby.Profile?.AccountId)CloseRoomCustomization(false,true);
            ClearRoomPasswordSecrets();
            var popup=_utilityPopupScreen;
            string friendDraft=popup==LobbyScreen.Friends?overlay?.Q<TextField>("friend-request-id")?.value:null;
            SyncProfile();
            if(lobby.Profile!=null)
            {
                PlayerPrefs.SetString("DrawLiar.Name",nickname);PlayerPrefs.SetInt("DrawLiar.Color",avatarColor);DrawAvatarEquipmentStore.Save(accessory);
                PlayerPrefs.Save();
            }
            if(!inRoom&&content!=null)
            {
                Home();
                if(popup.HasValue&&lobby.IsAuthenticated)OpenUtilityPopup(popup.Value);
                if(friendDraft!=null)overlay?.Q<TextField>("friend-request-id")?.SetValueWithoutNotify(friendDraft);
            }
        }

        private void LoginForm(VisualElement panel)
        {
            var guest=Button(panel,"게스트로 시작",()=>Run(async()=>{await lobby.GuestLoginAsync();Navigate(LobbyScreen.Main);await lobby.TryJoinInviteAsync();}),"primary guest-login");
            guest.SetEnabled(DrawGuestCredentialStore.IsSupported&&!lobby.IsBusy);
            var google=Button(panel,"Google로 계속하기",()=>Run(async()=>{await lobby.GoogleLoginAsync();Navigate(LobbyScreen.Main);await lobby.TryJoinInviteAsync();}),"secondary google-login");
            google.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy);
            var cancel=Button(panel,"Google 인증 취소",lobby.CancelGoogleLogin,"secondary google-cancel");cancel.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None;
            if(!IsMobile)LanguageSelector(panel);
        }

        private void AccountForm(VisualElement panel)
        {
            if(lobby.Profile==null)return;
            LeveledName(panel,lobby.Profile.DisplayName,OwnLevel,"section-title","account-name",lobby.Profile.AccountId,OwnSubscriberBadge);
            AccountExperience(panel);
            SubscriberSettings(panel);
            Text(panel,lobby.Profile.HasGoogleAccount?"Google 연동 계정":lobby.Profile.IsGuest?"게스트 계정":"기존 계정","muted");
            Text(panel,"계정 ID: {0}","rules",lobby.Profile.AccountId);
            Button(panel,"계정 ID 복사",()=>Run(async()=>{await DrawClipboard.CopyAsync(lobby.Profile.AccountId);Toast("계정 ID를 복사했습니다.");}),"secondary");
            if(!lobby.Profile.HasGoogleAccount)
            {
                var link=Button(panel,"Google 계정 연동",()=>Run(()=>lobby.GoogleLoginAsync(true)),"primary google-link");link.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy);
            }
            var cancel=Button(panel,"Google 인증 취소",lobby.CancelGoogleLogin,"secondary google-cancel");cancel.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None;
            Button(panel,"로그아웃",()=>Run(async()=>{await lobby.LogoutAsync();Navigate(LobbyScreen.Login);}),"danger");
        }

        private void FriendsForm(VisualElement panel)
        {
            var account=Field(panel,"친구 계정 ID","");account.maxLength=64;account.name="friend-request-id";
            var actions=Box(panel,"row friend-actions");
            Button(actions,"친구 요청",()=>Run(()=>lobby.RequestFriendAsync(account.value)),"primary").name="friend-request-send";
            IconButton(actions,"새로고침",DrawUIIcon.Kind.Refresh,()=>Run(lobby.RefreshFriendsAsync),"friends-refresh");
            friendList=Box(panel,"utility-friend-list");friendList.name="friends-list";RefreshFriends();
        }

        private void RefreshFriends()
        {
            if(friendList?.panel==null)return;
            friendList.Clear();
            if(lobby.Friends.Incoming.Length>0)Text(friendList,"받은 요청","section-title");
            foreach(var friend in lobby.Friends.Incoming)
            {
                var row=Box(friendList,"room-entry");FriendProfileIdentity(row,friend);
                Button(row,"수락",()=>Run(()=>lobby.RespondFriendAsync(friend.AccountId,true)),"primary").name="friend-accept-"+friend.AccountId;
                Button(row,"거절",()=>Run(()=>lobby.RespondFriendAsync(friend.AccountId,false)),"secondary").name="friend-reject-"+friend.AccountId;
            }
            Text(friendList,"친구 목록","section-title");
            if(lobby.Friends.Friends.Length==0)Text(friendList,"친구가 없습니다.","muted");
            foreach(var friend in lobby.Friends.Friends)
            {
                var row=Box(friendList,"room-entry");FriendProfileIdentity(row,friend);
                Button(row,"삭제",()=>Run(()=>lobby.RemoveFriendAsync(friend.AccountId)),"secondary").name="friend-remove-"+friend.AccountId;
            }
            if(lobby.Friends.Outgoing.Length>0)Text(friendList,"보낸 요청","section-title");
            foreach(var friend in lobby.Friends.Outgoing)
            {
                var row=Box(friendList,"room-entry");FriendProfileIdentity(row,friend);
                var pending=Box(row,"friend-outgoing-actions");pending.style.width=160;pending.style.maxWidth=Length.Percent(55);pending.style.minWidth=0;pending.style.flexShrink=0;pending.style.marginLeft=8;
                var state=Text(pending,"수락 대기","muted friend-pending");state.style.marginLeft=0;state.style.marginBottom=4;
                var cancel=Button(pending,"신청 취소",()=>RunSocialAction(async()=>
                {
                    await lobby.CancelFriendRequestAsync(friend.AccountId);RefreshFriends();
                }),"secondary social-action");cancel.name="friend-request-cancel-"+friend.AccountId;
                cancel.style.marginLeft=0;cancel.style.height=StyleKeyword.Auto;cancel.style.whiteSpace=WhiteSpace.Normal;
            }
        }

        private void ShopPage()
        {
            _commerceCatalogue=null;
            if(_shopPreviewAccountId!=lobby.Profile?.AccountId){ClearShopPreview();_shopPreviewAccountId=lobby.Profile?.AccountId;}
            if(_shopPartFilter>=0&&!AvatarParts.Slots.Contains((AvatarPartSlot)_shopPartFilter))_shopPartFilter=-1;
            foreach(var slot in _shopPreviewParts.Keys.Where(slot=>!AvatarParts.Slots.Contains(slot)).ToArray())RemoveShopPreviewPart(slot);
            if(!IsMobile)panelSettings.referenceResolution=new Vector2Int(1600,900);
            var page=Box(content,"shop-page grow");page.name="shop-page";
            var header=Box(page,"row shop-header");
            Button(header,"로비",()=>Navigate(LobbyScreen.Main),"secondary shop-back",DrawSound.UiCancel).name="shop-back";
            Text(header,"상점","shop-title grow");
            Button(header,"친구",()=>Navigate(LobbyScreen.Friends),"secondary shop-friends").name="shop-friends";
            if(IsMobile)IconButton(header,"설정",DrawUIIcon.Kind.Settings,()=>Navigate(LobbyScreen.Options),"shop-settings");
            else Button(header,"설정",()=>Navigate(LobbyScreen.Options),"secondary shop-settings").name="shop-settings";
            ShopCommerceNavigation(page);
            if(_shopSection!=ShopSection.Cosmetics)
            {
                CommerceCataloguePage(page);
                if(IsMobile)MobileNavigation();
                DrawUIMotion.Stagger(page,28,220,8);return;
            }
            var workspace=Box(page,"row shop-workspace grow");
            var preview=Box(workspace,"shop-panel shop-preview-panel");preview.name="shop-preview-panel";
            var hero=Box(preview,"shop-preview-hero");
            var profileName=LeveledName(hero,nickname,OwnLevel,"shop-profile-name","shop-profile-name",lobby.Profile?.AccountId,OwnSubscriberBadge);
            avatarStage=Box(hero,"shop-preview-stage");avatarStage.name="shop-preview-stage";UpdateLobbyAvatar(avatarColor,CurrentShopPreview());
            var previewControls=Box(hero,"shop-preview-controls");
            if(IsMobile)previewControls.Add(profileName.parent);
            _shopPreviewStatus=Text(previewControls,"착용 중","shop-preview-status");_shopPreviewStatus.name="shop-preview-status";
            var previewActions=Box(previewControls,"shop-preview-actions");
            _shopPreviewReset=Button(previewActions,"초기화",ResetShopPreview,"secondary shop-preview-reset",DrawSound.UiCancel);_shopPreviewReset.name="shop-preview-reset";
            Button(previewActions,"꾸미기",()=>Navigate(LobbyScreen.Customize),"secondary shop-customize").name="shop-customize";
            BuildShopCart(preview);
            var catalog=Box(workspace,"shop-panel shop-catalog grow");catalog.name="shop-catalog";
            var filters=Box(catalog,"row shop-filters");
            PartTabs(filters,_shopPartFilter,SelectShopPart,true);
            IconButton(filters,"새로고침",DrawUIIcon.Kind.Refresh,()=>Run(lobby.RefreshShopAsync),"shop-refresh");
            var scroll=DrawSmoothScroll.Create();scroll.name="shop-scroll";scroll.AddToClassList("shop-scroll");catalog.Add(scroll);
            shopList=Box(scroll,"shop-grid");shopList.name="shop-list";
            var grid=shopList;
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_=>SizeShopGrid(grid,scroll.contentViewport.contentRect.width));
            RefreshShop();
            if(IsMobile)MobileNavigation();
            DrawUIMotion.Stagger(page,28,220,8);
        }

        private void SelectShopPart(int slot)
        {
            _shopPartFilter=slot;
            var scroll=shopList?.GetFirstAncestorOfType<ScrollView>();
            if(scroll!=null)
            {
                DrawSmoothScroll.Bind(scroll);
                scroll.scrollOffset=Vector2.zero;
            }
            RefreshShop();
        }

        private void SizeShopGrid(VisualElement grid,float width)
        {
            if(grid?.panel==null||width<=0)return;
            float gap=IsMobile?12:16;
            int columns=IsMobile?Mathf.Clamp(Mathf.FloorToInt((width+gap)/180),2,4):Mathf.Clamp(Mathf.FloorToInt((width+gap)/226),3,4);
            // 픽셀 반올림 후에도 마지막 카드가 같은 줄에 들어가도록 여유를 둔다.
            float cardWidth=Mathf.Max(0,Mathf.Floor((width-gap*(columns-1))/columns)-1);
            int index=0;
            foreach(var card in grid.Children())
            {
                if(!card.ClassListContains("shop-entry"))continue;
                card.style.width=cardWidth;card.style.marginRight=index++%columns==columns-1?0:gap;
            }
        }

        private static void SizeShopActions(VisualElement card,VisualElement actions)
        {
            if(actions?.panel==null||actions.contentRect.width<=0)return;
            float buttonWidth=(actions.contentRect.width-8)/2;
            bool stacked=actions.Query<Button>().ToList().Any(button=>
                button.MeasureTextSize(button.text,0,VisualElement.MeasureMode.Undefined,0,VisualElement.MeasureMode.Undefined).x>
                buttonWidth-button.resolvedStyle.paddingLeft-button.resolvedStyle.paddingRight-button.resolvedStyle.borderLeftWidth-button.resolvedStyle.borderRightWidth);
            card.EnableInClassList("shop-actions-stacked",stacked);actions.EnableInClassList("shop-actions-stacked",stacked);
        }

        private void RefreshShop()
        {
            if(shopList==null)return;
            shopList.Clear();
            var ownedAccessories=lobby.Profile?.OwnedAccessories??Array.Empty<long>();
            var serverProducts=(lobby.Shop.Products??Array.Empty<ShopProduct>()).Where(item=>item!=null).ToArray();
            int displayed=0;
            var products=AvatarParts.CreateShopProducts().Select(product=>serverProducts.FirstOrDefault(item=>item.Id==product.Id&&item.Accessory==product.Accessory)??product)
                .Where(MatchesShopFilter)
                .OrderBy(item=>AvatarParts.IsOwned(ownedAccessories,item.Accessory));
            foreach(var product in products)
            {
                displayed++;
                bool owned=AvatarParts.IsOwned(ownedAccessories,product.Accessory);
                var row=Box(shopList,"shop-entry");row.userData=product;
                row.EnableInClassList("shop-owned",owned);
                var summary=Box(row,"shop-product");
                var image=new AvatarElement(avatarColor,product.Accessory);SetTooltip(image,product.Name);image.AddToClassList("shop-item-image");summary.Add(image);
                var info=Box(summary,"shop-item-info");Text(info,product.Name,"shop-item-name");
                if(owned)Text(info,"보유 중","shop-item-price muted");else Text(info,"{0} 코인","shop-item-price muted",product.Price);
                var actions=Box(row,"row shop-actions");
                actions.RegisterCallback<GeometryChangedEvent>(_=>SizeShopActions(row,actions));
                var tryOn=Button(actions,"입혀보기",()=>PreviewShopProduct(product),"secondary grow shop-try-on");tryOn.name="shop-preview-"+product.Id;
                bool equipped=owned&&AvatarParts.IsEquipped(lobby.Profile?.Accessory??0,product.Accessory);
                var apply=Button(actions,owned?(equipped?"장착 중":"장착하기"):"구매",()=>Run(async()=>
                {
                    if(!owned){await PurchaseShopProductAsync(product);return;}
                    var profile=lobby.Profile;
                    await lobby.SaveProfileAsync(profile.DisplayName,profile.AvatarColor,AvatarParts.Equip(profile.Accessory,product.Accessory));
                    foreach(var slot in AvatarParts.Slots)if(AvatarParts.Get(product.Accessory,slot)!=0)RemoveShopPreviewPart(slot);
                    UpdateShopPreview();
                }),"primary grow mobile-last",DrawSound.UiConfirm);apply.name="shop-buy-"+product.Id;
                bool available=serverProducts.Any(item=>item.Id==product.Id&&item.Accessory==product.Accessory);
                apply.SetEnabled(!lobby.IsBusy&&(owned?!equipped:available&&(lobby.Profile?.Coins??0)>=product.Price));
            }
            if(displayed==0)Text(shopList,"판매 중인 상품이 없습니다.","muted");
            var viewport=shopList.GetFirstAncestorOfType<ScrollView>()?.contentViewport;
            if(viewport!=null)SizeShopGrid(shopList,viewport.contentRect.width);
            UpdateShopPreview();
        }

        private bool MatchesShopFilter(ShopProduct product)
        {
            if(_shopPartFilter==-1)return true;
            bool set=AvatarParts.Slots.Count(slot=>AvatarParts.Get(product.Accessory,slot)!=0)>1;
            return _shopPartFilter==SHOP_SET_FILTER?set:!set&&AvatarParts.Get(product.Accessory,(AvatarPartSlot)_shopPartFilter)!=0;
        }

        private void PreviewShopProduct(ShopProduct product)
        {
            if(_shopBatchPurchasing||lobby.IsBusy)return;
            foreach(var slot in AvatarParts.Slots)
            {
                long part=AvatarParts.Get(product.Accessory,slot);
                if(part!=0){_shopPreviewParts[slot]=part;_shopPreviewProductIds[slot]=product.Id;}
            }
            UpdateShopPreview();RevealAvatarPreview();
        }

        private void ResetShopPreview()
        {
            if(_shopBatchPurchasing||lobby.IsBusy)return;
            ClearShopPreview();UpdateShopPreview();RevealAvatarPreview();
        }

        private long CurrentShopPreview()
        {
            long result=accessory;
            foreach(var part in _shopPreviewParts.Values)result=AvatarParts.Equip(result,part);
            return result;
        }

        private void UpdateShopPreview()
        {
            long preview=CurrentShopPreview();
            if(avatarStage!=null)UpdateLobbyAvatar(avatarColor,preview);
            SetText(_shopPreviewStatus,_shopPreviewParts.Count==0?"착용 중":"입혀보기 중");
            RefreshShopCart();
            shopList?.Query<VisualElement>(className:"shop-entry").ForEach(row=>
            {
                var product=(ShopProduct)row.userData;
                bool selected=AvatarParts.IsEquipped(preview,product.Accessory)&&AvatarParts.Slots.Any(slot=>AvatarParts.Get(product.Accessory,slot)!=0&&_shopPreviewParts.ContainsKey(slot));
                row.EnableInClassList("shop-selected",selected);
                var tryOn=row.Q<Button>(className:"shop-try-on");
                SetText(tryOn,selected&&!IsMobile?"미리보기 중":"입혀보기");SetTooltip(tryOn,selected?"미리보기 중":"입혀보기");
                SizeShopActions(row,row.Q<VisualElement>(className:"shop-actions"));
            });
        }

        private static void BrandLogo(VisualElement parent,string styleClass)
        {
            var logo=new Image{image=L.LoadLogo(),scaleMode=ScaleMode.ScaleToFit,pickingMode=PickingMode.Ignore};
            logo.AddToClassList("brand-logo");Classes(logo,styleClass);parent.Add(logo);
        }

        private void Navigate(LobbyScreen screen)
        {
            if(screen==LobbyScreen.Options||screen==LobbyScreen.Friends||screen==LobbyScreen.Account)
            {
                OpenUtilityPopup(screen);return;
            }
            if(screen==LobbyScreen.CreateRoom&&lobbyScreen!=LobbyScreen.CreateRoom&&lobbyScreen!=LobbyScreen.Topics)
            {_createRulesExpanded=false;_createTimeExpanded=false;_createTopicsExpanded=false;_createRoomScrollOffset=Vector2.zero;_createRoomPassword="";}
            if(screen==LobbyScreen.Topics&&lobbyScreen!=LobbyScreen.Topics)
            {_workshopReturnScreen=lobbyScreen==LobbyScreen.CreateRoom?LobbyScreen.CreateRoom:LobbyScreen.Main;BeginTopicWorkshopPage();}
            else if(screen!=LobbyScreen.Topics&&lobbyScreen==LobbyScreen.Topics)EndTopicWorkshopPage();
            if(screen!=LobbyScreen.Shop||lobbyScreen!=LobbyScreen.Shop)ClearShopPreview();
            lobbyScreen=screen;Home();
        }
        private void Back()
        {
            var destination=lobbyScreen==LobbyScreen.Topics?_workshopReturnScreen:LobbyScreen.Main;
            Navigate(destination);
        }
        private void CreateRoomPage()
        {
            var page=Box(content,"create-room-page");page.name="create-room-page";
            var panel=Box(page,"create-room-panel");
            var heading=Box(panel,"row create-room-heading");
            Button(heading,"← 뒤로",Back,"secondary create-back",DrawSound.UiCancel).name="create-back";
            Text(heading,"방 만들기","lobby-title grow");
            var scroll=_createRoomScroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);scroll.name="create-scroll";scroll.AddToClassList("create-scroll");panel.Add(scroll);
            var form=Box(scroll,"create-room-form");form.name="create-room-form";
            var footer=Box(panel,"create-footer");footer.name="create-footer";
            var topicError=Text(footer,"주제를 하나 이상 선택하세요.","rules create-topic-error");topicError.name="create-topic-error";
            var create=Button(footer,"방 만들고 입장",()=>Run(async()=>
            {
                root.focusController?.focusedElement?.Blur();
                if(draft.Topics==null||draft.Topics.Length==0)return;
                draft.Validate();ValidateRoomPassword(draft.IsPrivate,_createRoomPassword,false);await lobby.HostAsync(draft.Copy(),draft.IsPrivate?_createRoomPassword:"");
            }),"primary create-submit",DrawSound.UiConfirm);create.name="create-submit";

            var basic=Box(form,"row create-basic-fields");
            var roomName=Field(basic,"방 이름",draft.RoomName);roomName.name="create-room-name";roomName.maxLength=30;roomName.AddToClassList("grow");
            roomName.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,roomName))draft.RoomName=e.newValue;});
            Action refreshCreate=null;
            var passwordBlock=Box(form,"room-password-fields");
            var password=RoomPasswordField(passwordBlock,"방 비밀번호",_createRoomPassword,"create-room-password");
            Text(passwordBlock,"비밀번호는 4~32자로 입력하세요.","rules");
            void RefreshPasswordField()
            {
                passwordBlock.style.display=draft.IsPrivate?DisplayStyle.Flex:DisplayStyle.None;
                if(!draft.IsPrivate){_createRoomPassword="";password.SetValueWithoutNotify("");}
                refreshCreate?.Invoke();
            }
            password.RegisterValueChangedCallback(e=>{if(!ReferenceEquals(e.target,password))return;_createRoomPassword=e.newValue;refreshCreate?.Invoke();});
            Choice(basic,"공개 설정",new[]{"누구나 · 공개방","친구끼리 · 사설방"},draft.IsPrivate?1:0,i=>{draft.IsPrivate=i==1;RefreshPasswordField();}).name="create-privacy";
            RefreshPasswordField();
            VisualElement liarCount=null;
            RoomModeChoices(form,draft,"create",()=>
            {
                RefreshRoomLiarCount(draft,liarCount);refreshCreate?.Invoke();
            });

            var rules=CreateRoomSection(form,"게임 규칙","create-rules",_createRulesExpanded,value=>_createRulesExpanded=value,out var rulesSummary);
            var time=CreateRoomSection(form,"시간 설정","create-time",_createTimeExpanded,value=>_createTimeExpanded=value,out var timeSummary);
            var topics=CreateRoomSection(form,"주제 선택","create-topics",_createTopicsExpanded,value=>_createTopicsExpanded=value,out var topicSummary);
            var topicData=GameDataStore.Load().Topics;
            var names=topicData.Select(entry=>entry.Name).Distinct().ToArray();
            var mismatchTopics=new HashSet<string>(topicData.Where(entry=>(entry.Words??Array.Empty<string>()).Select(value=>GameRules.CleanText(value,40))
                .Where(value=>value.Length>0).Select(GameRules.NormalizeGuess).Distinct().Take(2).Count()>=2).Select(entry=>entry.Name));
            var selected=new HashSet<string>((draft.Topics??names).Intersect(names));var topicButtons=new Dictionary<string,Button>();
            void RefreshSummaries()
            {
                int limit=draft.Victory==VictoryMode.RoundCount?draft.RoundCount:draft.TargetScore;
                if(draft.LiarMode==LiarMode.Mismatch)
                {
                    string source=draft.Victory==VictoryMode.RoundCount?
                        draft.AllowMidRoundJoin?"미스매치 · {0}라운드 · 난입 허용":"미스매치 · {0}라운드 · 난입 제한":
                        draft.AllowMidRoundJoin?"미스매치 · 목표 {0}점 · 난입 허용":"미스매치 · 목표 {0}점 · 난입 제한";
                    SetText(rulesSummary,source,limit);
                }
                else
                {
                    string source=draft.Victory==VictoryMode.RoundCount?
                        draft.AllowMidRoundJoin?"라이어 {0}명 · {1}라운드 · 난입 허용":"라이어 {0}명 · {1}라운드 · 난입 제한":
                        draft.AllowMidRoundJoin?"라이어 {0}명 · 목표 {1}점 · 난입 허용":"라이어 {0}명 · 목표 {1}점 · 난입 제한";
                    SetText(rulesSummary,source,draft.LiarMode==LiarMode.Optional?(object)"0~1":draft.LiarCount,limit);
                }
                SetText(timeSummary,"그림 {0}초 · 토론 {1}초",draft.DrawSeconds,draft.DiscussionSeconds);
                SetText(topicSummary,"{0} / {1}개 선택",selected.Count,names.Length);
                bool validTopics=selected.Count>0&&(draft.LiarMode!=LiarMode.Mismatch||selected.Any(mismatchTopics.Contains));
                SetText(topicError,selected.Count==0?"주제를 하나 이상 선택하세요.":"미스매치는 서로 다른 단어가 2개 이상인 주제가 필요합니다.");
                create.SetEnabled(validTopics&&(!draft.IsPrivate||IsValidRoomPassword(_createRoomPassword)));topicError.style.display=validTopics?DisplayStyle.None:DisplayStyle.Flex;
            }
            refreshCreate=RefreshSummaries;
            liarCount=Int(rules.contentContainer,"라이어 수",draft.LiarCount,1,GameRules.MAX_PLAYERS-1,value=>{draft.LiarCount=draft.LiarMode==LiarMode.Classic?value:1;RefreshSummaries();});liarCount.name="create-liars";
            RefreshRoomLiarCount(draft,liarCount);
            VisualElement victoryLimit=null;
            void BuildVictoryLimit()
            {
                victoryLimit.Clear();
                if(draft.Victory==VictoryMode.RoundCount)Int(victoryLimit,"진행 판수",draft.RoundCount,1,30,value=>{draft.RoundCount=value;RefreshSummaries();}).name="create-rounds";
                else Int(victoryLimit,"목표 점수",draft.TargetScore,1,1000,value=>{draft.TargetScore=value;RefreshSummaries();}).name="create-target-score";
            }
            Choice(rules.contentContainer,"승리 조건",new[]{"정해진 판수 후 최고점","목표 점수 먼저 달성"},(int)draft.Victory,index=>{draft.Victory=(VictoryMode)index;BuildVictoryLimit();RefreshSummaries();}).name="create-victory";
            victoryLimit=Box(rules.contentContainer,"create-victory-limit");BuildVictoryLimit();
            MidRoundJoinToggle(rules.contentContainer,draft,"create-mid-round-join",RefreshSummaries);
            var timeGrid=Box(time.contentContainer,"create-time-grid");TimeFields(timeGrid,draft);
            timeGrid.RegisterCallback<ChangeEvent<int>>(_=>RefreshSummaries());
            var bulk=Box(topics.contentContainer,"row create-topic-actions");
            void RefreshTopics()
            {
                draft.Topics=names.Where(selected.Contains).ToArray();
                foreach(var entry in topicButtons)
                {
                    bool chosen=selected.Contains(entry.Key);SetRawText(entry.Value,(chosen?"✓  ":"")+entry.Key);entry.Value.EnableInClassList("topic-selected",chosen);
                }
                RefreshSummaries();
            }
            Button(bulk,"전체 선택",()=>{selected.UnionWith(names);RefreshTopics();},"secondary grow").name="create-topics-all";
            Button(bulk,"초기화",()=>{selected.Clear();RefreshTopics();},"secondary grow").name="create-topics-clear";
            TopicEntryButtons(topics.contentContainer,"create");
            var grid=Box(topics.contentContainer,"create-topic-grid");int topicIndex=0;
            foreach(string name in names)
            {
                string key=name;var button=Button(grid,"",()=>{if(!selected.Add(key))selected.Remove(key);RefreshTopics();},"topic-toggle");
                button.name="create-topic-"+topicIndex++;button.tooltip=key;button.userData=key;topicButtons[key]=button;
            }
            RefreshTopics();
            scroll.schedule.Execute(()=>{if(scroll==_createRoomScroll&&scroll.panel!=null)scroll.scrollOffset=_createRoomScrollOffset;}).StartingIn(20);
            Enter(panel,220,8);
        }
        private Foldout CreateRoomSection(VisualElement parent,string title,string name,bool expanded,Action<bool> changed,out Label summary)
        {
            var foldout=new Foldout{value=expanded};foldout.name=name;foldout.AddToClassList("create-foldout");parent.Add(foldout);
            var toggle=foldout.Q<Toggle>();toggle.name=name+"-toggle";toggle.AddToClassList("create-foldout-toggle");
            var heading=Box(toggle,"create-section-heading grow");heading.pickingMode=PickingMode.Ignore;
            Text(heading,title,"create-section-title").pickingMode=PickingMode.Ignore;
            summary=Text(heading,"","create-section-summary");summary.name=name+"-summary";summary.pickingMode=PickingMode.Ignore;
            foldout.contentContainer.AddToClassList("create-section-fields");
            foldout.RegisterValueChangedCallback(e=>
            {
                if(!ReferenceEquals(e.target,foldout))return;
                changed(e.newValue);if(e.newValue)Enter(foldout.contentContainer,160,4);
            });
            return foldout;
        }
        private static Toggle MidRoundJoinToggle(VisualElement parent,RoomSettings settings,string name,Action changed=null)
        {
            var toggle=new Toggle(L.Text("라운드 도중 난입 허용")){name=name,value=settings.AllowMidRoundJoin};
            SetText(toggle.labelElement,"라운드 도중 난입 허용");Classes(toggle,"field mid-round-join");parent.Add(toggle);
            toggle.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,toggle)){settings.AllowMidRoundJoin=e.newValue;changed?.Invoke();}});
            return toggle;
        }
        private static void RefreshRoomLiarCount(RoomSettings settings,VisualElement field)
        {
            bool fixedCount=settings.LiarMode!=LiarMode.Classic;
            if(fixedCount)settings.LiarCount=1;
            field?.SetEnabled(!fixedCount);
            if(field!=null)field.style.display=fixedCount?DisplayStyle.None:DisplayStyle.Flex;
#if UNITY_WEBGL && !UNITY_EDITOR
            if(field is TextField text)text.SetValueWithoutNotify(settings.LiarCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
#else
            if(field is IntegerField number)number.SetValueWithoutNotify(settings.LiarCount);
#endif
        }

        private void JoinForm(VisualElement panel)
        {
            if(IsMobile)
            {
                var codeRow=Box(panel,"mobile-join-code-row row");var input=Field(codeRow,"방 코드 또는 주소","");input.maxLength=2048;Classes(input,"mobile-code-input grow");input.textEdition.placeholder="ABC 234";
                LeftToRightInput(input);
                input.textEdition.keyboardType=TouchScreenKeyboardType.ASCIICapable;input.textEdition.autoCorrection=false;
                input.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,input))input.SetValueWithoutNotify(NormalizeRoomCodeInput(e.newValue));});
                Button(codeRow,"붙여넣기",()=>Run(async()=>{input.value=NormalizeRoomCodeInput(await DrawClipboard.ReadAsync());}),"secondary mobile-code-paste");
                void Join(bool watch)=>Run(()=>lobby.JoinCodeAsync(input.value,watch));
                var actions=Box(panel,"mobile-join-actions row");Button(actions,"참가하기",()=>Join(false),"primary grow");Button(actions,"관전",()=>Join(true),"secondary grow mobile-last");return;
            }
            var code=Field(panel,"방 코드 또는 주소","");code.maxLength=2048;LeftToRightInput(code);
            var spectator=new Toggle(L.Text("관전으로 참가"));SetText(spectator.labelElement,"관전으로 참가");spectator.AddToClassList("field");panel.Add(spectator);
            Button(panel,"입장하기",()=>Run(()=>lobby.JoinCodeAsync(code.value,spectator.value)),"primary next-button");
        }

        private void RefreshPublicRooms()
        {
            if(publicRoomList?.panel==null)return;
            var list=publicRoomList;bool empty=lobby.PublicRooms.Count==0;var offset=empty?Vector2.zero:list.scrollOffset;
            publicRoomList.Clear();
            var emptyState=list.contentViewport.Q<VisualElement>("rooms-empty-state");
            if(empty)
            {
                if(emptyState==null)
                {
                    emptyState=Box(list.contentViewport,"room-list-empty-state");emptyState.name="rooms-empty-state";emptyState.pickingMode=PickingMode.Ignore;
                    Text(emptyState,"열린 방이 없습니다.","muted room-list-empty-message").pickingMode=PickingMode.Ignore;
                }
                list.scrollOffset=Vector2.zero;
            }
            else emptyState?.RemoveFromHierarchy();
            foreach(var room in lobby.PublicRooms)
            {
                var entry=Box(publicRoomList,IsMobile?"room-entry mobile-room-item":"room-entry");var info=Box(entry,"grow room-entry-info");
                if(IsMobile)
                {
                    var heading=Box(info,"mobile-room-top row");RawText(heading,room.Name,"player-name grow");Text(heading,room.IsInProgress?"진행 중":"대기 중","mobile-room-state");
                    var metadata=Box(info,"mobile-room-metadata row");Text(metadata,"{0} / {1}명","muted mobile-small mobile-room-meta",room.Players,room.MaxPlayers);
                    if(room.Spectators>0)Text(metadata,"관전 {0}명","muted mobile-small mobile-room-meta",room.Spectators);
                    if(room.LiarMode!=LiarMode.Classic)Text(metadata,room.LiarMode==LiarMode.Optional?"불확정":"미스매치","muted mobile-small mobile-room-meta").name="room-liar-mode-"+room.Id;
                }
                else
                {
                    RawText(info,room.Name,"player-name");Text(info,room.IsInProgress?"{0} / {1}명 · 관전 {2}명 · 진행 중":"{0} / {1}명 · 관전 {2}명 · 대기 중","muted mobile-small",room.Players,room.MaxPlayers,room.Spectators);
                    if(room.LiarMode!=LiarMode.Classic)Text(info,room.LiarMode==LiarMode.Optional?"불확정":"미스매치","muted mobile-small").name="room-liar-mode-"+room.Id;
                }
                CreatePublicRoomTopics(info,room);
                var actions=IsMobile?Box(entry,"mobile-room-entry-actions mobile-room-action row"):entry;
                if(!room.IsInProgress||room.AllowMidRoundJoin)
                {
                    var enter=Button(actions,"참가",()=>Run(()=>lobby.JoinLobbyAsync(room.Id)),"primary");enter.name="room-join-"+room.Id;enter.SetEnabled(room.Players<room.MaxPlayers);
                }
                Button(actions,"관전",()=>Run(()=>lobby.JoinLobbyAsync(room.Id,true)),room.IsInProgress&&!room.AllowMidRoundJoin?"primary":"secondary").name="room-spectate-"+room.Id;
                if(IsMobile)actions.ElementAt(0).AddToClassList("mobile-room-entry-first");
            }
            RefreshRoomTopicsPopup();
            list.schedule.Execute(()=>
            {
                if(list!=publicRoomList||list.panel==null)return;
                list.scrollOffset=new Vector2(
                    Mathf.Clamp(offset.x,list.horizontalScroller.lowValue,Mathf.Max(list.horizontalScroller.lowValue,list.horizontalScroller.highValue)),
                    Mathf.Clamp(offset.y,list.verticalScroller.lowValue,Mathf.Max(list.verticalScroller.lowValue,list.verticalScroller.highValue)));
            }).StartingIn(20);
        }
        private void SearchRooms(string query)
        {
            string value=(query??"").Trim();if(value.Length>30)value=value.Substring(0,30);
            bool changed=_roomSearch!=value;_roomSearch=_roomSearchDraft=value;_roomSearchField?.SetValueWithoutNotify(value);
            _roomSearchPending=true;_nextRoomRefresh=Time.unscaledTime+RoomRefreshInterval;
            if(changed&&publicRoomList!=null)publicRoomList.scrollOffset=Vector2.zero;
            RefreshRoomsInBackground();
        }
        private void RefreshRoomsInBackground()
        {
            if(!isActiveAndEnabled||(!_backgroundRoomRefresh&&!_roomSearchPending)||_roomRefreshRunning||inRoom||lobbyScreen!=LobbyScreen.Main||!lobby.IsAuthenticated
                ||lobby.IsBusy||runningAction||(!_roomSearchPending&&Time.unscaledTime<_nextRoomRefresh))return;
            if(_roomSearchPending){_roomSearchPending=false;string query=_roomSearch;Run(()=>lobby.SearchRoomsAsync(query));return;}
            _=RefreshRoomsAsync();
        }
        private async Task RefreshRoomsAsync()
        {
            _nextRoomRefresh=Time.unscaledTime+RoomRefreshInterval;_roomRefreshRunning=true;
            try{await lobby.SearchRoomsAsync(_roomSearch);}
            catch(Exception){}
            finally{_roomRefreshRunning=false;_nextRoomRefresh=Time.unscaledTime+RoomRefreshInterval;}
        }
        private async Task Leave()
        {
            CloseModal();await lobby.LeaveAsync();
        }

        private void OnRoomKicked(string message)
        {
            lobbyScreen=LobbyScreen.Main;
            if(!inRoom){CloseModal();Home();}
        }

        private void Room()
        {
            ClearTextPresentationView();
            ClearJudgmentCoinToss();
            ClearShopCartView();
            _pcRoomChat=null;_pcChatExpanded=false;_chatFocusVersion++;
            ClearDrawingPreview();_clearOwnButton=null;_clearOwnPending=false;
            ClearRoomPasswordSecrets();
            ClosePublicProfile(false);
            ResetLobbyChatView();_mobileLobbyChatButton=null;
            inRoom=true;ResetMobileRoomView();_mobileWaitingRoomTitle=null;_waitingRoomTopics=null;content.Clear();publicRoomList=null;serviceNotice=null;playerKey="";actionKey="";contextKey="";
            playerCards.Clear();playerStatuses.Clear();_playerScores.Clear();_playerVoteStacks.Clear();selectedPlayerId=-1;secretHidden=false;voteSubmitted=false;
            _hasSelectedPlayer=_judgmentSubmitted=false;_ballotVersion=-1;_accusedKey="";_speechVisuals.Clear();
            _pcLeftPlayers=_pcRightPlayers=_pcCenter=_accusedSpotlight=null;_spectatorCount=null;
            root.EnableInClassList("pc-game",!IsMobile);
            root.RemoveFromClassList("at-home");root.AddToClassList("in-game");content.AddToClassList("room-layout");lobbyScreen=LobbyScreen.Main;
            if(IsMobile){MobileRoom();HideMobileScrollers();return;}
            panelSettings.referenceResolution=new Vector2Int(1600,900);panelSettings.match=1f;
            var shell=_pcRoomShell=Box(content,"pc-room-shell");shell.name="pc-room-shell";
            var hud=Box(shell,"room-hud");var round=Box(hud,"round-label");
            roomBadge=Text(round,"","round-number");roundCaption=Text(round,"대기실","muted round-caption");
            phaseBanner=Box(hud,"phase-bar");phaseTitle=Text(phaseBanner,"대기실","phase-title");phaseDetail=Text(phaseBanner,"","phase-detail");
            AddLevelBadge(phaseDetail,1,"artist");
            _hudTimer=new DrawGameTimer{name="hud-timer"};_hudTimer.AddToClassList("hud-timer");phaseBanner.Add(_hudTimer);timer=_hudTimer.Value;
            var menu=Box(hud,"room-menu");SocialBell(menu);Button(menu,"메뉴",RoomMenu,"secondary");
            timer.languageDirection=LanguageDirection.LTR;roomBadge.languageDirection=LanguageDirection.LTR;
            var numeralFont=Resources.Load<Font>("DrawLiar/Fonts/BarlowCondensed-SemiBold");
            if(numeralFont!=null){timer.style.unityFontDefinition=FontDefinition.FromFont(numeralFont);roomBadge.style.unityFontDefinition=FontDefinition.FromFont(numeralFont);}
            var workspace=Box(shell,"workspace pc-game-workspace");workspace.name="pc-game-workspace";
            var secret=_pcRoomSecret=Box(workspace,"secret-bar pc-secret-bar");secret.name="pc-secret-bar";CreateRoomSecretContext(secret,"게임 대기");
            CreateRoomSecretIdentity(secret,"room-secret-identity row");
            _waitingRoomTopics=Button(secret,"",OpenWaitingRoomTopics,"secondary waiting-room-topics");_waitingRoomTopics.name="waiting-room-topics";
            _spectatorCount=Text(secret,"","spectator-count");
            var stage=Box(workspace,"pc-canvas-stage");stage.name="pc-canvas-stage";
            _pcLeftPlayers=Box(stage,"pc-player-rail pc-player-left");_pcLeftPlayers.name="pc-player-left";
            var center=_pcCenter=Box(stage,"center pc-game-center");center.name="pc-game-center";
            var frame=Box(center,"canvas-frame pc-canvas-frame");frame.name="pc-game-canvas";surface=new DrawingSurface(network);frame.Add(surface);
            frame.RegisterCallback<GeometryChangedEvent>(_=>{surface.FitTo(frame.contentRect.size);RefreshPcRoomGeometry(frame);});
            workspace.RegisterCallback<GeometryChangedEvent>(_=>RefreshPcRoomGeometry(frame));
            stage.RegisterCallback<GeometryChangedEvent>(_=>RefreshPcRoomGeometry(frame));
            _pcLeftPlayers.RegisterCallback<GeometryChangedEvent>(_=>RefreshPcRoomGeometry(frame));
            CreateAccusedSpotlight(frame);CreateDrawingPreviewName(frame);
            var controls=Box(workspace,"pc-game-controls");controls.name="pc-game-controls";CreateDrawingTools(controls);CreateJudgmentPanel(controls);
            var footer=Box(controls,"pc-room-footer row");footer.name="pc-room-footer";
            var chatDock=Box(footer,"pc-chat-dock");chatDock.name="pc-chat-dock";CreatePcRoomChat(chatDock);
            _pcNominationHost=Box(footer,"pc-nomination-host grow");_pcNominationHost.name="pc-nomination-host";_pcNominationHost.style.display=DisplayStyle.None;
            var judgment=Box(footer,"pc-phase-controls row grow");contextInfo=Box(judgment,"context-info");phaseActions=Box(judgment,"phase-actions grow");
            _pcRightPlayers=Box(stage,"pc-player-rail pc-player-right");_pcRightPlayers.name="pc-player-right";
            _pcRightPlayers.RegisterCallback<GeometryChangedEvent>(_=>RefreshPcRoomGeometry(frame));
            playerStrip=null;CreateSpeechLayer(workspace);
            network.ReplayCanvas();foreach(var stroke in pendingStrokes)surface.Apply(stroke);pendingStrokes.Clear();
        }

        private void MobileRoom()
        {
            _mobileLayout.Refresh();
            _mobileRoomStack=Box(content,"mobile-room");
            var hud=_mobileHud=Box(_mobileRoomStack,"room-hud");phaseBanner=Box(hud,"phase-bar");phaseTitle=Text(phaseBanner,"대기실","phase-title");_mobileLiveRound=Text(phaseBanner,"","mobile-live-round");phaseDetail=Text(phaseBanner,"","phase-detail");
            AddLevelBadge(phaseDetail,1,"artist");
            _hudTimer=new DrawGameTimer{name="hud-timer"};_hudTimer.AddToClassList("hud-timer");hud.Add(_hudTimer);timer=_hudTimer.Value;SocialBell(hud);Button(hud,"메뉴",RoomMenu,"secondary mobile-back");
            timer.languageDirection=LanguageDirection.LTR;
            _mobileRoundCard=Box(_mobileRoomStack,"mobile-round-card row");var round=Box(_mobileRoundCard,"grow");
            _mobileWaitingRoomTitle=RawText(round,"","mobile-waiting-room-title");_mobileWaitingRoomTitle.name="mobile-waiting-room-title";
            _mobileRoundInfo=Text(round,"","mobile-card-title");_mobileRoundHint=Text(round,"","muted mobile-small");
            AddLevelBadge(_mobileRoundHint,1,"mobile-artist");
            roomBadge=Text(round,"","mobile-hidden");roundCaption=Text(round,"","mobile-hidden");
            roomBadge.languageDirection=LanguageDirection.LTR;
            var roomActions=Box(_mobileRoundCard,"mobile-room-info-actions");
            _mobileRoomCode=Button(roomActions,"",CopyRoomInvite,"secondary mobile-code-copy");SetRawText(_mobileRoomCode,DisplayRoomCode(lobby.RoomCode));SetTooltip(_mobileRoomCode,RoomCopyLabel);
            _waitingRoomTopics=Button(roomActions,"",OpenWaitingRoomTopics,"secondary waiting-room-topics");_waitingRoomTopics.name="waiting-room-topics";
            _mobileRoomCode.languageDirection=LanguageDirection.LTR;
            _mobileSecret=Box(_mobileRoomStack,"secret-bar mobile-secret");CreateRoomSecretContext(_mobileSecret,"");
            CreateRoomSecretIdentity(_mobileSecret,"room-secret-identity mobile-role-line row");
            BindMobileSecretDetails();
            _mobileWorkspace=Box(_mobileRoomStack,"mobile-workspace");_mobilePlayStage=Box(_mobileWorkspace,"mobile-play-stage");
            _mobileCanvas=Box(_mobilePlayStage,"canvas-frame");_mobilePlaySide=Box(_mobileWorkspace,"mobile-play-side");
            surface=new DrawingSurface(network);_mobileCanvas.Add(surface);CreateAccusedSpotlight(_mobileCanvas);CreateDrawingPreviewName(_mobileCanvas);_mobileCanvas.RegisterCallback<GeometryChangedEvent>(_=>SizeMobileCanvas());
            _mobileRoomScroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);_mobileRoomScroll.AddToClassList("mobile-controls-scroll");_mobileWorkspace.Add(_mobileRoomScroll);
            _mobileControls=Box(_mobileRoomScroll,"mobile-controls");CreateDrawingTools(_mobileControls);contextInfo=Box(_mobileControls,"context-info");
            _mobileRoster=Box(_mobileControls,"mobile-roster");_mobileRosterTitle=Text(_mobileRoster,"참가자","muted mobile-small");
            _mobileRosterScroll=DrawSmoothScroll.Create(ScrollViewMode.Horizontal);_mobileRosterScroll.AddToClassList("mobile-roster-scroll");_mobileRoster.Add(_mobileRosterScroll);playerStrip=Box(_mobileRosterScroll,"players players-strip");
            _mobileRosterScroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_=>SizeMobileWaitingPlayers());
            _mobileActions=Box(_mobileWorkspace,"mobile-bottom-actions");CreateJudgmentPanel(_mobileActions);var actionRow=Box(_mobileActions,"mobile-action-row");phaseActions=Box(actionRow,"phase-actions");chatOpen=Button(actionRow,"채팅 열기",OpenChat,"secondary chat-open");
            _mobileWorkspace.RegisterCallback<GeometryChangedEvent>(_=>RefreshMobileRoomGeometry());
            _mobileRoster.RegisterCallback<GeometryChangedEvent>(_=>RefreshMobileRoomGeometry());
            _mobileChatSheet=Box(content,"overlay mobile-chat-layer");_mobileChatSheet.style.display=DisplayStyle.None;
            var sheet=Box(_mobileChatSheet,"modal mobile-chat-sheet");var chatHeading=Box(sheet,"row mobile-page-heading");Text(chatHeading,"채팅","title grow");Button(chatHeading,"닫기",CloseChat,"secondary mobile-back",DrawSound.UiCancel);
            chatHistory=DrawSmoothScroll.Create(ScrollViewMode.Vertical);chatHistory.AddToClassList("chat-history");sheet.Add(chatHistory);
            chatbar=Box(sheet,"chatbar");chatbar.style.display=DisplayStyle.None;chatInput=new TextField{maxLength=160,name="room-chat-input"};Placeholder(chatInput,"채팅 입력");chatInput.AddToClassList("chat-input");chatbar.Add(chatInput);
            chatInput.RegisterCallback<KeyDownEvent>(e=>
            {
                if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){SubmitChatInput();e.StopImmediatePropagation();}
            },TrickleDown.TrickleDown);
            Button(chatbar,"보내기",SendChat,"primary",DrawSound.UiConfirm).name="room-chat-send";
            CreateSpeechLayer(content);
            OnMobileLayoutChanged();
            network.ReplayCanvas();foreach(var stroke in pendingStrokes)surface.Apply(stroke);pendingStrokes.Clear();
        }

        private void OnMobileLayoutChanged()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if(_webRenderedMode!=IsMobile)
            {
                if(!_webLayoutRebuildPending)
                {
                    _webLayoutRebuildPending=true;
                    root.schedule.Execute(RebuildBrowserLayout);
                }
                return;
            }
#endif
            if(!inRoom||_mobileWorkspace==null||!IsMobile)return;
            ArrangeMobileRoom(network.State);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private void RebuildBrowserLayout()
        {
            try
            {
                if(content==null||_webRenderedMode==IsMobile)return;
                bool chatVisible=_webRenderedMode?chatbar?.style.display==DisplayStyle.Flex:
                    _pcChatExpanded;
                root.focusController?.focusedElement?.Blur();
                var popup=_utilityPopupScreen;
                ClearTextPresentationView();
                var oldOverlay=overlay;CloseModal();oldOverlay?.RemoveFromHierarchy();
                _webRenderedMode=IsMobile;
                if(inRoom&&network.State!=null)
                {
                    string chatDraft=chatInput?.value??"";
                    var chatLines=chatHistory?.contentContainer.Children().ToArray();
                    var brushColor=surface?.BrushColor;var brushSize=surface?.BrushSize;var eraser=surface?.Eraser;
                    int selected=selectedPlayerId,ballot=_ballotVersion;bool hasSelected=_hasSelectedPlayer,hidden=secretHidden,submitted=voteSubmitted,judged=_judgmentSubmitted;
                    Room();
                    selectedPlayerId=selected;_hasSelectedPlayer=hasSelected;_ballotVersion=ballot;secretHidden=hidden;voteSubmitted=submitted;_judgmentSubmitted=judged;
                    if(surface!=null&&brushColor.HasValue){surface.BrushColor=brushColor.Value;surface.BrushSize=brushSize.Value;surface.Eraser=eraser.Value;RefreshBrushControls();}
                    if(chatLines!=null)foreach(var line in chatLines)chatHistory.Add(line);
                    if(chatInput!=null)chatInput.SetValueWithoutNotify(chatDraft);
                    RefreshState(network.State);
                    if(chatVisible)OpenChat();else CloseChat();
                }
                else Home();
                if(popup.HasValue)OpenUtilityPopup(popup.Value);
            }
            finally
            {
                _webLayoutRebuildPending=false;
                if(_webRenderedMode!=IsMobile)OnMobileLayoutChanged();
            }
        }
#endif

        private void SizeMobileWaitingPlayers()
        {
            if(!IsMobile||_mobileRoomStack==null||!_mobileRoomStack.ClassListContains("mobile-waiting")||playerStrip?.panel==null)return;
            float width=_mobileRosterScroll.contentViewport.contentRect.width;
            if(width<=0)return;
            const int COLUMNS=4;
            const float GAP=6,MAX_CARD_WIDTH=96,CARD_HEIGHT=144;
            float gridWidth=Mathf.Min(width,COLUMNS*MAX_CARD_WIDTH+GAP*(COLUMNS-1));
            float cardWidth=Mathf.Max(0,Mathf.Floor((gridWidth-GAP*(COLUMNS-1))/COLUMNS)-1);
            float avatarSize=Mathf.Min(64,Mathf.Max(0,cardWidth-10));
            int index=0;
            foreach(var card in playerStrip.Children())
            {
                card.style.width=cardWidth;card.style.height=CARD_HEIGHT;
                card.style.marginRight=++index%COLUMNS==0?0:GAP;
                card.style.marginBottom=index<=playerStrip.childCount-(playerStrip.childCount%COLUMNS==0?COLUMNS:playerStrip.childCount%COLUMNS)?GAP:0;
                var avatar=card.Q<AvatarElement>();
                if(avatar!=null){avatar.style.width=avatarSize;avatar.style.height=avatarSize;}
            }
        }

        private void SizeMobileCanvas()
        {
            if(_mobileCanvas==null||surface==null)return;
            surface.FitTo(_mobileCanvas.contentRect.size);
        }

        private static string NormalizeRoomCodeInput(string value)
        {
            if(!string.IsNullOrEmpty(value)&&value.IndexOfAny(new[]{':','/','?','#','.'})>=0)return value;
            return string.Concat((value??"").Where(c=>!char.IsWhiteSpace(c)&&c!='-')).ToUpperInvariant();
        }
        private static string RoomCopyLabel
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return "방 주소 복사";
#else
                return "방 코드 복사";
#endif
            }
        }
        private void CopyRoomInvite()=>Run(async()=>
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string value=RoomJoinAddress.Create(DrawBrowserInterop.RoomPageUrl(),lobby.RoomCode);
            const string MESSAGE="방 주소를 복사했습니다.";
#else
            string value=lobby.RoomCode;
            const string MESSAGE="방 코드를 복사했습니다.";
#endif
            await DrawClipboard.CopyAsync(value);Toast(MESSAGE);
        });
        private static string DisplayRoomCode(string value)=>value!=null&&value.Length==6?value.Substring(0,3)+" "+value.Substring(3):value??"";

        private void OpenChat()
        {
            if(!inRoom||chatInput==null||overlay!=null)return;
            _chatTransitionVersion++;
            if(!IsMobile&&_pcRoomChat!=null)SetPcRoomChatExpanded(true);
            if(IsMobile&&_mobileChatSheet!=null)
            {
                _mobileChatSheet.style.display=DisplayStyle.Flex;_mobileChatSheet.pickingMode=PickingMode.Position;
                DrawUIMotion.Enter(_mobileChatSheet,160,0);
                DrawUIMotion.ShowSheet(_mobileChatSheet.Q<VisualElement>(className:"mobile-chat-sheet"));
            }
            chatbar.style.display=DisplayStyle.Flex;chatOpen.style.display=DisplayStyle.None;FocusChatInput(chatInput);
        }
        private void FocusChatInput(TextField input)
        {
            if(input==null)return;
            int version=++_chatFocusVersion;
            var expectedOverlay=overlay;
            var expectedProfile=_profileOverlay;
            bool roomAtRequest=inRoom;
#if UNITY_WEBGL && !UNITY_EDITOR
            if(_browserTextInput?.Focus(input)==true)return;
#else
            input.Focus();
#endif
            int attempts=0;
            IVisualElementScheduledItem pending=null;
            pending=input.schedule.Execute(()=>
            {
                attempts++;
                if(this==null||!isActiveAndEnabled||version!=_chatFocusVersion||roomAtRequest!=inRoom||overlay!=expectedOverlay
                    ||_profileOverlay!=expectedProfile||input!=(inRoom?chatInput:_lobbyChatInput)||input.panel==null
                    ||FocusedTextField()!=null&&FocusedTextField()!=input){pending?.Pause();return;}
                if(!IsVisible(input)||input.worldBound.width<=0||input.worldBound.height<=0)
                {if(attempts>=8)pending?.Pause();return;}
#if UNITY_WEBGL && !UNITY_EDITOR
                bool focused=_browserTextInput?.Focus(input)==true;
#else
                input.Focus();
                bool focused=FocusedTextField()==input;
#endif
                if(focused||attempts>=8)pending?.Pause();
            }).StartingIn(1).Every(16);
        }
        private TextField FocusedTextField()
        {
            var focused=root?.focusController?.focusedElement as VisualElement;
            return focused as TextField??focused?.GetFirstAncestorOfType<TextField>();
        }
        private void ChatFocusShortcut(KeyDownEvent e)
        {
            if((e.keyCode!=KeyCode.Return&&e.keyCode!=KeyCode.KeypadEnter)||e.altKey||e.ctrlKey||e.commandKey)return;
            var target=e.target as VisualElement;
            if(!inRoom&&target?.ClassListContains("profile-trigger")==true)return;
            if(target is TextField||target?.GetFirstAncestorOfType<TextField>()!=null||FocusedTextField()!=null)return;
            if(ChatShortcutTarget()==null)return;
            OpenChatShortcut();
            _chatShortcutFrame=Time.frameCount;
            root.focusController?.IgnoreEvent(e);
            e.StopImmediatePropagation();
        }
        private void OpenChatShortcut()
        {
            if(ChatShortcutTarget()==null)return;
            if(inRoom)
            {
                OpenChat();
            }
            else
            {
                if(_lobbyChatInput?.panel==null||!IsVisible(_lobbyChatInput))
                {
                    if(!IsMobile||overlay!=null)return;
                    OpenLobbyChat();
                }
                FocusChatInput(_lobbyChatInput);
            }
        }
        private void ChatNavigationSubmit(NavigationSubmitEvent e)
        {
            if(_chatShortcutFrame!=Time.frameCount)return;
            root.focusController?.IgnoreEvent(e);
            e.StopImmediatePropagation();
        }
        private void CloseChat()
        {
            if(chatInput==null)return;
            if(!IsMobile)
            {
                CollapsePcRoomChat(true);
                return;
            }
            _chatFocusVersion++;
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.Blur(chatInput,true);
#endif
            chatInput.Blur();chatbar.style.display=DisplayStyle.None;chatOpen.style.display=DisplayStyle.Flex;
            if(_mobileChatSheet!=null&&_mobileChatSheet.style.display!=DisplayStyle.None)
            {
                var layer=_mobileChatSheet;int version=++_chatTransitionVersion;
                DrawUIMotion.HideSheet(layer,()=>{if(version==_chatTransitionVersion)layer.style.display=DisplayStyle.None;});
            }
        }
        private void SendChat()
        {
            if(!string.IsNullOrWhiteSpace(chatInput.value)){network.Chat(chatInput.value);chatInput.value="";}
            if(IsMobile)CloseChat();else chatInput.Focus();
        }
        private void RoomShortcut(KeyDownEvent e)
        {
            if(e.keyCode!=KeyCode.Escape)return;
            if(_profileKickOverlay!=null){ClosePublicProfileKick();e.StopImmediatePropagation();return;}
            if(_workshopPreviewOverlay!=null){CloseWorkshopPreview();e.StopImmediatePropagation();return;}
            if(_roomPasswordOverlay!=null)return;
            if(_roomTopicWorkshopOverlay!=null){CloseRoomTopicWorkshop();e.StopImmediatePropagation();return;}
            if(_roomCustomizeOverlay!=null&&overlay==null)
            {CloseRoomCustomization();e.StopImmediatePropagation();return;}
            if(_profileOverlay!=null){ClosePublicProfile();e.StopImmediatePropagation();return;}
            if(!inRoom&&overlay!=null){CloseModal();e.StopImmediatePropagation();return;}
            if(!inRoom)return;
            if(_roomOptionsSaving){e.StopImmediatePropagation();return;}
            bool chatVisible=IsMobile?chatbar?.style.display==DisplayStyle.Flex:_pcChatExpanded;
            if(overlay!=null)CloseModal();else if(chatVisible)CloseChat();else RoomMenu();
            root.focusController?.IgnoreEvent(e);e.StopImmediatePropagation();
        }
        private void RoomMenu()
        {
            var state=network.State;if(state==null)return;
            CloseChat();var modal=Modal("메뉴");RawText(modal,state.Settings.RoomName,"subtitle");
            if(CanEditRoomOptions(state))Button(modal,"방 옵션",RoomOptions,"secondary").name="room-options-menu";
            if(state.IsHost)Button(modal,"참가자 관리",OpenRoomModeration,"secondary").name="room-moderation-open";
            RoomFriendInviteButton(modal,"secondary");
            Button(modal,RoomCopyLabel,CopyRoomInvite,"secondary");
            if(IsMobile&&state.Phase!=GamePhase.Lobby&&!string.IsNullOrEmpty(state.Word))
                Button(modal,secretHidden?"제시어 보기":"제시어 숨기기",()=>{secretHidden=!secretHidden;RefreshSecret(network.State);CloseModal();},"secondary");
            Button(modal,"옵션",Options,"secondary");
            Button(modal,"나가기",()=>Run(Leave),"danger",DrawSound.UiCancel);Button(modal,"닫기",CloseModal,"secondary",DrawSound.UiCancel);
        }
        private static bool CanEditRoomOptions(RoomSnapshot state) => state!=null&&state.IsHost
            &&(state.Phase==GamePhase.Lobby||state.Phase==GamePhase.MatchResults);

        private void RoomOptions()
        {
            var state=network.State;if(!CanEditRoomOptions(state))return;
            var settings=state.Settings.Copy();
            var names=state.AvailableTopics??Array.Empty<string>();
            if(names.Length==0)
            {
                var asset=Resources.Load<TextAsset>("DrawLiar/GameData");
                var data=asset!=null?JsonUtility.FromJson<GameData>(asset.text):new GameData();
                names=(data.Topics??Array.Empty<TopicData>()).Select(entry=>entry.Name).Concat(settings.Topics??Array.Empty<string>()).Distinct().ToArray();
            }
            names=names.Where(name=>!string.IsNullOrWhiteSpace(name)).Distinct().Take(128).ToArray();
            var editor=Modal("방 옵션",false);editor.name="room-options-editor";editor.AddToClassList("room-options-editor");
            _roomOptionsEditor=editor;_roomOptionsDraft=settings;
            foreach(var topic in GameDataStore.LoadCustomTopics().Where(topic=>!names.Contains(topic.Name)))
                _roomOptionsCustomTopics[topic.Name]=new ServerTopicData{Name=topic.Name,Words=(topic.Words??Array.Empty<string>()).ToArray()};
            var tabs=Box(editor,"row room-options-tabs");
            var scroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);scroll.name="room-options-scroll";scroll.AddToClassList("room-options-scroll");editor.Add(scroll);
            var body=Box(scroll,"room-options-body");
            var footer=Box(editor,"row room-options-footer");
            var cancel=Button(footer,"닫기",CloseModal,"secondary grow",DrawSound.UiCancel);cancel.name="room-options-cancel";
            Button save=null;save=Button(footer,"저장",()=>Run(async()=>
            {
                root.focusController?.focusedElement?.Blur();
                if(!CanEditRoomOptions(network.State)){CloseModal();Toast("방 옵션 변경 권한이 없습니다.");return;}
                _roomOptionsSaving=true;editor.SetEnabled(false);
                try
                {
                    ValidateRoomPassword(settings.IsPrivate,_roomOptionsPassword,state.Settings.IsPrivate);
                    await network.ConfigureRoomAsync(_roomOptionsDraft,settings.IsPrivate?_roomOptionsPassword:"",
                        _roomOptionsCustomTopics.Values.Where(topic=>settings.Topics.Contains(topic.Name)).ToArray());
                    if(_roomOptionsEditor==editor){CloseModal();Toast("방 옵션을 변경했습니다.");}
                }
                finally
                {
                    if(_roomOptionsEditor==editor){_roomOptionsSaving=false;editor.SetEnabled(true);save.SetEnabled(settings.Topics?.Length>0);}
                }
            }),"primary grow");save.name="room-options-save";
            var buttons=new List<Button>();
            void SelectTab(int index)
            {
                ClearRoomPasswordField(body.Q<TextField>("room-options-password"));
                body.Clear();for(int i=0;i<buttons.Count;i++)buttons[i].EnableInClassList("room-options-selected",i==index);
                if(index==0)
                {
                    var name=Field(body,"방 이름",settings.RoomName);name.name="room-options-name";name.maxLength=30;
                    name.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,name))settings.RoomName=e.newValue;});
                    var passwordBlock=Box(body,"room-password-fields");
                    var password=RoomPasswordField(passwordBlock,state.Settings.IsPrivate?"새 비밀번호":"방 비밀번호",_roomOptionsPassword,"room-options-password");
                    Text(passwordBlock,state.Settings.IsPrivate?"비워 두면 기존 비밀번호를 유지합니다.":"비밀번호는 4~32자로 입력하세요.","rules");
                    void RefreshPassword()
                    {
                        passwordBlock.style.display=settings.IsPrivate?DisplayStyle.Flex:DisplayStyle.None;
                        if(!settings.IsPrivate){_roomOptionsPassword="";password.SetValueWithoutNotify("");}
                    }
                    password.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,password))_roomOptionsPassword=e.newValue;});
                    var privacy=Choice(body,"공개 설정",new[]{"누구나 · 공개방","친구끼리 · 사설방"},settings.IsPrivate?1:0,i=>{settings.IsPrivate=i==1;RefreshPassword();});
                    body.Insert(1,privacy);
                    RefreshPassword();
                }
                else if(index==1)
                {
                    VisualElement liarCount=null;
                    RoomModeChoices(body,settings,"room-options",()=>RefreshRoomLiarCount(settings,liarCount));
                    liarCount=Int(body,"라이어 수",settings.LiarCount,1,GameRules.MAX_PLAYERS-1,v=>settings.LiarCount=settings.LiarMode==LiarMode.Classic?v:1);liarCount.name="room-options-liars";
                    RefreshRoomLiarCount(settings,liarCount);
                    Choice(body,"승리 조건",new[]{"정해진 판수 후 최고점","목표 점수 먼저 달성"},(int)settings.Victory,i=>{settings.Victory=(VictoryMode)i;SelectTab(1);});
                    if(settings.Victory==VictoryMode.RoundCount)Int(body,"진행 판수",settings.RoundCount,1,30,v=>settings.RoundCount=v);
                    else Int(body,"목표 점수",settings.TargetScore,1,1000,v=>settings.TargetScore=v);
                    MidRoundJoinToggle(body,settings,"room-options-mid-round-join");
                }
                else if(index==2)TimeFields(body,settings);
                else
                {
                    var available=names.Concat(_roomOptionsCustomTopics.Keys).Distinct().Take(128).ToArray();
                    var selected=new HashSet<string>((settings.Topics??available).Intersect(available));var controls=Box(body,"row spread");
                    var count=Text(controls,"","topic-count");var bulk=Box(controls,"row");
                    TopicEntryButtons(body,"room-options");
                    var grid=Box(body,"topic-grid");var topics=new Dictionary<string,Button>();
                    void Refresh()
                    {
                        settings.Topics=available.Where(selected.Contains).ToArray();save.SetEnabled(settings.Topics.Length>0);
                        SetText(count,"{0} / {1}개 선택",selected.Count,available.Length);
                        foreach(var entry in topics){bool chosen=selected.Contains(entry.Key);SetRawText(entry.Value,(chosen?"✓  ":"")+entry.Key);entry.Value.EnableInClassList("topic-selected",chosen);}
                    }
                    Button(bulk,"전체 선택",()=>{selected.UnionWith(available);Refresh();},"secondary");
                    Button(bulk,"초기화",()=>{selected.Clear();Refresh();},"secondary");
                    foreach(string name in available){string key=name;var button=topics[key]=Button(grid,"",()=>{if(!selected.Add(key))selected.Remove(key);Refresh();},"topic-toggle");button.tooltip=key;button.userData=key;}
                    Refresh();
                }
                scroll.scrollOffset=Vector2.zero;HideMobileScrollers();
            }
            var titles=new[]{"방 정보","게임 규칙","시간 설정","주제"};var ids=new[]{"info","rules","time","topics"};
            for(int i=0;i<titles.Length;i++){int index=i;var button=Button(tabs,titles[i],()=>SelectTab(index),"secondary grow");button.name="room-options-"+ids[i];buttons.Add(button);}
            _roomOptionsRefreshTopics=()=>SelectTab(3);
            save.SetEnabled(settings.Topics?.Length>0);SelectTab(0);
        }
        private void RefreshSecret(RoomSnapshot state)
        {
            if(state==null)return;
            RefreshRoomModeLabels(state);
            bool waiting=state.Phase==GamePhase.Lobby;
            topic.parent.parent.EnableInClassList("waiting-room-info",waiting);
            if(waiting)SetRawText(topic,state.Settings.RoomName);else if(IsMobile)SetText(topic,"주제 · {0}",state.Topic);else SetRawText(topic,state.Topic);
            topic.tooltip=waiting?state.Settings.RoomName:state.Topic;
            word.style.display=role.style.display=waiting?DisplayStyle.None:DisplayStyle.Flex;
            _roomSecretIdentity.style.display=waiting?DisplayStyle.None:DisplayStyle.Flex;
            SetText(role,waiting?"":!state.LocalIsSpectator&&(HasHiddenMismatchRole(state)
                ||state.Settings.LiarMode==LiarMode.Mismatch&&HideLiarIdentity(state))?"비공개":LocalRoleName(state));
            if(waiting)SetRawText(word,"");
            else if(string.IsNullOrEmpty(state.Word))SetText(word,"비공개");
            else SetRawText(word,secretHidden?"•••":state.Word);
            word.EnableInClassList("word-hidden",string.IsNullOrEmpty(state.Word)||secretHidden);
            secretToggle.style.display=state.Phase==GamePhase.Lobby||string.IsNullOrEmpty(state.Word)?DisplayStyle.None:DisplayStyle.Flex;
            SetText(secretToggle,secretHidden?"제시어 보기":"숨기기");
        }
        private void RefreshState(RoomSnapshot state)
        {
            RefreshPublicProfileActions();
            RefreshRoomModeration(state);
            if(_roomOptionsEditor!=null&&!CanEditRoomOptions(state))CloseModal();
            if(state==null){if(inRoom){CloseModal();Home();}return;}
            if(!inRoom)Room();
            RefreshDrawingInteractions(state);
            bool phaseChanged=previousPhase!=state.Phase;
            UpdateGameTimers(state);
            PrepareTextPresentation(state);
            if(phaseChanged||_ballotVersion!=state.BallotVersion)
            {selectedPlayerId=-1;_hasSelectedPlayer=false;voteSubmitted=_judgmentSubmitted=false;contextKey="";_ballotVersion=state.BallotVersion;}
            if(_hasSelectedPlayer&&!IsNoLiarSelected(state)&&!state.Players.Any(p=>p.Id==selectedPlayerId&&p.IsConnected&&!p.IsSpectator))
            {selectedPlayerId=-1;_hasSelectedPlayer=false;voteSubmitted=false;}
            var local=state.Players.FirstOrDefault(p=>p.Id==state.LocalPlayerId);
            if(IsNominationPhase(state)&&local?.HasVoted==true&&!_hasSelectedPlayer
                &&(state.Settings.LiarMode==LiarMode.Optional&&state.LocalVoteTargetId==GameRules.NO_LIAR_TARGET
                    ||state.Players.Any(p=>p.Id==state.LocalVoteTargetId&&p.IsConnected&&!p.IsSpectator)))
            {selectedPlayerId=state.LocalVoteTargetId;_hasSelectedPlayer=true;}
            SetRawText(roomBadge,state.Phase==GamePhase.Lobby?"":state.Settings.Victory==VictoryMode.RoundCount?$"{state.Round:00} / {state.Settings.RoundCount:00}":$"{state.Round:00}");
            SetText(roundCaption,state.Phase==GamePhase.Lobby?"대기실":"라운드");
            RefreshSecret(state);
            RefreshWaitingRoomTopics(state);
            SetText(phaseTitle,IsMobile&&state.Phase==GamePhase.Drawing?"그림 차례":PhaseName(state));
            if(IsMobile&&_mobileRoundInfo!=null)
            {
                bool waiting=state.Phase==GamePhase.Lobby;
                _mobileRoomStack.EnableInClassList("mobile-waiting",waiting);
                _mobileRoomStack.EnableInClassList("mobile-playing",!waiting);
                _mobileCanvas.style.display=DisplayStyle.Flex;
                _mobileSecret.style.display=waiting?DisplayStyle.None:DisplayStyle.Flex;
                var rosterMode=ScrollViewMode.Vertical;
                if(_mobileRosterScroll.mode!=rosterMode)
                {
                    _mobileRosterScroll.mode=rosterMode;
                    _mobileRosterScroll.scrollOffset=Vector2.zero;_mobileRoomScroll.scrollOffset=Vector2.zero;
                }
                SetText(_mobileRosterTitle,"참가자");
                if(waiting)
                {
                    SetText(_mobileRoundInfo,"{0} / {1}명",state.Players.Count(p=>!p.IsSpectator&&p.IsConnected),state.Settings.MaxPlayers);
                    if(state.Settings.LiarMode==LiarMode.Mismatch)
                        SetText(_mobileRoundHint,state.Settings.Victory==VictoryMode.RoundCount?"미스매치 · {0}라운드":"미스매치 · 목표 {0}점",state.Settings.Victory==VictoryMode.RoundCount?state.Settings.RoundCount:state.Settings.TargetScore);
                    else if(state.Settings.Victory==VictoryMode.RoundCount)SetText(_mobileRoundHint,"라이어 {0}명 · {1}라운드",state.Settings.LiarMode==LiarMode.Optional?(object)"0~1":state.Settings.LiarCount,state.Settings.RoundCount);
                    else SetText(_mobileRoundHint,"라이어 {0}명 · 목표 {1}점",state.Settings.LiarMode==LiarMode.Optional?(object)"0~1":state.Settings.LiarCount,state.Settings.TargetScore);
                }
                else
                {
                    SetText(_mobileRoundInfo,"라운드 {0}",state.Round);
                    SetText(_mobileLiveRound,"라운드 {0}",state.Round);
                    if(state.Phase==GamePhase.Drawing&&network.CanDraw)SetText(_mobileRoundHint,"내 차례");
                    else if(state.Phase==GamePhase.Drawing)SetRawText(_mobileRoundHint,state.Players.FirstOrDefault(p=>p.Id==state.ArtistId)?.Name);
                    else SetText(_mobileRoundHint,PhaseName(state));
                }
                SetRawText(_mobileRoomCode,DisplayRoomCode(lobby.RoomCode));
            }
            var artist=state.Players.FirstOrDefault(p=>p.Id==state.ArtistId);
            RefreshNameLevel(_mobileRoundHint,state.Phase==GamePhase.Drawing&&!network.CanDraw?artist:null);
            RefreshNameLevel(phaseDetail,state.Phase==GamePhase.Drawing&&!network.CanDraw?artist:null);
            if(state.Phase==GamePhase.Drawing&&network.CanDraw)SetText(phaseDetail,"내 차례");
            else SetRawText(phaseDetail,state.Phase==GamePhase.Drawing?artist?.Name:"");
            phaseDetail.style.display=IsMobile||string.IsNullOrEmpty(phaseDetail.text)?DisplayStyle.None:DisplayStyle.Flex;
            phaseDetail.parent.style.display=phaseDetail.style.display;
            if(_spectatorCount!=null)
            {
                int spectators=state.Players.Count(p=>p.IsConnected&&p.IsSpectator);
                SetText(_spectatorCount,"관전 {0}명",spectators);_spectatorCount.style.display=spectators>0?DisplayStyle.Flex:DisplayStyle.None;
            }
            bool toolsWereVisible=drawingTools.resolvedStyle.display!=DisplayStyle.None;
            drawingTools.style.display=network.CanDraw?DisplayStyle.Flex:DisplayStyle.None;drawingTools.SetEnabled(network.CanDraw);
            if(network.CanDraw&&!toolsWereVisible)Enter(drawingTools,140,4);
            var key=string.Join("|",state.Players.Select(p=>$"{p.Id},{p.AccountId},{p.Name},{p.IsSpectator},{p.IsConnected},{p.AvatarColor},{p.Accessory}"))+state.HostPlayerId+state.Phase+state.LocalIsSpectator;
            if(key!=playerKey){playerKey=key;Players(state);}
            PrepareGuessingInput(state,local);
            if(voteSubmitted&&local?.HasVoted==true&&state.LocalVoteTargetId==selectedPlayerId)voteSubmitted=false;
            if(local?.HasJudged==true)_judgmentSubmitted=false;
            var actions=$"{state.Phase}/{state.Settings.LiarMode}/{state.Players.Count(player=>player.IsConnected&&!player.IsSpectator)}/{state.BallotVersion}/{state.HasAccused}/{state.AccusedPlayerId}/{state.ArtistId}/{state.CanStart}/{state.IsHost}/{state.LocalPlayerId}/{state.LocalIsLiar}/{state.LocalIsSpectator}/{local?.IsConnected}/{local?.HasVoted}/{local?.HasJudged}/{local?.HasGuessed}/{selectedPlayerId}/{_hasSelectedPlayer}/{voteSubmitted}/{_judgmentSubmitted}/{_guessSubmitted}/{CanGuess(state,local)}";
            if(actions!=actionKey){actionKey=actions;PhaseActions(state,local);if(IsMobile)Enter(phaseActions,160,4);}
            RefreshContext(state);RefreshPlayerStates(state);RefreshAccusedSpotlight(state);RefreshSpeeches();
            if(phaseChanged)
            {
                bool resumedDiscussion=previousPhase==GamePhase.Rebuttal&&state.Phase==GamePhase.Discussion;
                previousPhase=state.Phase;Enter(phaseBanner,180,0);
                if(IsMobile)OnMobileLayoutChanged();
                if(state.Phase==GamePhase.RoleReveal)RoleReveal(state);
                else if(state.Phase==GamePhase.LiarReveal)RevealLiars(state);
                else if(state.Phase==GamePhase.RoundResults||state.Phase==GamePhase.MatchResults)Results(state);
                else CloseModal();
                if(resumedDiscussion)Toast(state.Summary);
            }
            RefreshJudgmentPanel(state,local);
            RestoreTextPresentation(state);
            RefreshGuessingInput(state,local);
            if(IsMobile)ArrangeMobileRoom(state);
            RefreshJudgmentCoinToss(state);
        }
        private bool CanSelectVote(RoomSnapshot state,PlayerView player)
        {
            return CanNominate(state)&&player!=null&&player.Id!=state.LocalPlayerId&&player.IsConnected&&!player.IsSpectator;
        }
        private static bool IsNominationPhase(RoomSnapshot state)=>state!=null&&(state.Phase==GamePhase.Discussion||state.Phase==GamePhase.Voting);
        private bool CanJudge(RoomSnapshot state,PlayerView local)=>state!=null&&state.Phase==GamePhase.Rebuttal
            &&state.HasAccused&&!state.IsJudgmentCoinToss&&!state.LocalIsSpectator&&local!=null&&local.IsConnected&&!local.IsSpectator
            &&local.Id!=state.AccusedPlayerId&&!local.HasJudged&&!_judgmentSubmitted;
        private void Players(RoomSnapshot state)
        {
            ClearDrawingPreview();
            int previousCount=playerCards.Count;
            playerStrip?.Clear();_pcLeftPlayers?.Clear();_pcRightPlayers?.Clear();playerCards.Clear();playerStatuses.Clear();_playerScores.Clear();_playerVoteStacks.Clear();
            bool currentPlayersOnly=state.Phase==GamePhase.RoundResults||state.Phase==GamePhase.MatchResults;
            var players=state.Players.Where(p=>!p.IsSpectator&&(!currentPlayersOnly||p.IsConnected)).ToArray();
            playerStrip?.EnableInClassList("crowded",players.Length>8);
            for(var i=0;i<Mathf.Max(players.Length,GameRules.MAX_PLAYERS);i++)
            {
                if(i>=players.Length)
                {
                    CreateEmptyPlayerSlot(IsMobile?playerStrip:i%2==0?_pcLeftPlayers:_pcRightPlayers,i);
                    continue;
                }
                var p=players[i];var parent=IsMobile?playerStrip:i%2==0?_pcLeftPlayers:_pcRightPlayers;
                var card=Box(parent,"player");card.name="player-slot-"+p.Id;card.userData=p.Id;card.focusable=true;card.tabIndex=0;
                card.EnableInClassList("last-player",i==players.Length-1);playerCards[p.Id]=card;
                var row=Box(card,"row player-identity");var avatar=new AvatarElement(p.AvatarColor,p.Accessory);avatar.AddToClassList("player-avatar");row.Add(avatar);
                if(!IsMobile)card.RegisterCallback<GeometryChangedEvent>(_=>SizePcPlayerAvatar(card,avatar));
                if(!IsMobile)BindProfileTarget(avatar,p.AccountId,()=>!IsNominationPhase(network.State));
                var info=Box(row,"grow");
                var name=p.Id==state.LocalPlayerId?Text(info,"{0} · 나","player-name",p.Name):RawText(info,p.Name,"player-name");
                AddLevelBadge(name,p.Level,"player-"+p.Id,p.AccountId);
                name.name="player-name-"+p.Id;if(!IsMobile)BindProfileTarget(name,p.AccountId,()=>!IsNominationPhase(network.State));card.tooltip=p.Name;
                if(IsMobile)CreatePlayerProfileButton(card,p);
                if(CanSelectVote(state,p))SetTooltip(card,"지목하기");
                var summary=Box(info,"player-summary");
                var score=Text(summary,"{0}점","player-score",p.Score);score.name="player-score-"+p.Id;score.pickingMode=PickingMode.Ignore;_playerScores[p.Id]=score;
                playerStatuses[p.Id]=Text(summary,"","player-status");
                var host=Text(card,"방장","player-host-badge");host.style.display=p.Id==state.HostPlayerId?DisplayStyle.Flex:DisplayStyle.None;
                card.EnableInClassList("host-player",p.Id==state.HostPlayerId);
                var order=RawText(card,"","player-drawing-order");order.name="drawing-order-"+p.Id;order.pickingMode=PickingMode.Ignore;
                var votes=new DrawVoteStack { name="player-votes-"+p.Id };card.Add(votes);_playerVoteStacks[p.Id]=votes;
                card.RegisterCallback<PointerEnterEvent>(e=>{if(ReferenceEquals(e.target,card)&&!IsMobile)PreviewAuthorDrawing(p.Id);});
                card.RegisterCallback<PointerLeaveEvent>(e=>{if(!IsMobile&&ReferenceEquals(e.target,card)&&_previewAuthorId==p.Id)ClearDrawingPreview();});
                void Activate()
                {
                    var current=network.State;var player=current?.Players.FirstOrDefault(value=>value.Id==p.Id);if(player==null)return;
                    if(IsMobile)
                    {
                        if(_previewAuthorId==player.Id)ClearDrawingPreview();else PreviewAuthorDrawing(player.Id);
                        RefreshDrawingInteractions(current);
                    }
                    if(IsNominationPhase(current))
                    {
                        if(!CanSelectVote(current,player))return;
                        DrawAudio.Instance?.Play(DrawSound.UiClick);selectedPlayerId=player.Id;_hasSelectedPlayer=true;RefreshState(current);
                    }
                }
                card.RegisterCallback<ClickEvent>(e=>{if(e.button==0&&!IsPlayerProfileTarget(e))Activate();});
                card.RegisterCallback<KeyDownEvent>(e=>{if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter||e.keyCode==KeyCode.Space){if(!IsPlayerProfileTarget(e))Activate();e.StopPropagation();}});
                card.RegisterCallback<NavigationSubmitEvent>(e=>{if(!IsPlayerProfileTarget(e))Activate();e.StopPropagation();});
            }
            if(!IsMobile&&_pcCenter!=null)
                RefreshPcRoomGeometry(_pcCenter.Q<VisualElement>("pc-game-canvas"));
            if(IsMobile)
            {
                RefreshMobileRoomGeometry();
                if(state.Phase==GamePhase.Lobby&&players.Length!=previousCount)
                {_mobileRosterScroll.scrollOffset=Vector2.zero;_mobileRoomScroll.scrollOffset=Vector2.zero;}
            }
        }
        private void RefreshPlayerStates(RoomSnapshot state)
        {
            foreach(var player in state.Players)
            {
                if(!playerCards.TryGetValue(player.Id,out var card))continue;
                card.Q<DrawLevelBadge>("level-badge-player-"+player.Id)?.SetLevel(player.Level);
                card.EnableInClassList("active-player",state.Phase==GamePhase.Drawing&&player.Id==state.ArtistId);
                card.EnableInClassList("selected-player",IsNominationPhase(state)&&_hasSelectedPlayer&&player.Id==selectedPlayerId);
                card.EnableInClassList("accused-player",state.Phase==GamePhase.Rebuttal&&state.HasAccused&&player.Id==state.AccusedPlayerId);
                card.EnableInClassList("disconnected-player",!player.IsConnected);
                card.EnableInClassList("vote-selectable",CanSelectVote(state,player));
                card.EnableInClassList("nomination-player",IsNominationPhase(state));
                if(_playerVoteStacks.TryGetValue(player.Id,out var votes))votes.SetCount(IsNominationPhase(state)?player.VoteCount:0);
                if(IsNominationPhase(state))SetTooltip(card,CanSelectVote(state,player)?"지목하기 · 받은 표 {0}개":"받은 표 {0}개",player.VoteCount);
                else {LOCALIZED_TOOLTIPS.Remove(card);card.tooltip=player.Name;}
                var order=card.Q<Label>("drawing-order-"+player.Id);
                int drawingIndex=Array.IndexOf(state.DrawingOrder??Array.Empty<int>(),player.Id);
                if(order!=null)
                {
                    order.style.display=drawingIndex>=0&&state.Phase!=GamePhase.Lobby?DisplayStyle.Flex:DisplayStyle.None;
                    SetRawText(order,(drawingIndex+1).ToString());SetTooltip(order,"그리기 순서 {0}",drawingIndex+1);
                    order.EnableInClassList("drawing-order-current",state.Phase==GamePhase.Drawing&&player.Id==state.ArtistId);
                }
                foreach(var target in card.Query<VisualElement>(className:"profile-trigger").ToList())
                    SetTooltip(target,target.ClassListContains("player-profile-button")?"프로필 보기":IsNominationPhase(state)?CanSelectVote(state,player)?"지목하기":"":"프로필 보기");
                var label=playerStatuses[player.Id];
                SetText(_playerScores[player.Id],"{0}점",player.Score);
                SetText(label,!player.IsConnected?"연결 끊김":IsNominationPhase(state)?(player.HasVoted?"지목 완료":_hasSelectedPlayer&&player.Id==selectedPlayerId?"선택됨":""):
                    state.Phase==GamePhase.Rebuttal?(state.IsJudgmentCoinToss?"동전 던지기":state.HasAccused&&player.Id==state.AccusedPlayerId?"반론 중":player.HasJudged?"투표 완료":""):
                    player.IsLiar&&!HasHiddenMismatchRole(state)&&!HideLiarIdentity(state)?"라이어":state.Phase==GamePhase.Drawing?(player.Id==state.ArtistId?"그리는 중":""):"");
                label.style.display=string.IsNullOrEmpty(label.text)?DisplayStyle.None:DisplayStyle.Flex;
            }
        }
        private void RefreshContext(RoomSnapshot state)
        {
            string key=state.Phase+"/"+state.BallotVersion+"/"+selectedPlayerId+"/"+_hasSelectedPlayer;
            if(state.Phase==GamePhase.Lobby)key+="/"+state.Players.Length+"/"+state.Players.Count(p=>!p.IsSpectator&&p.IsConnected)+"/"+state.CanStart+"/"+state.Settings.LiarMode+"/"+state.Settings.LiarCount+"/"+state.Settings.Victory+"/"+state.Settings.RoundCount+"/"+state.Settings.TargetScore;
            if(key!=contextKey)
            {
                contextKey=key;contextInfo.Clear();
                if(state.Phase==GamePhase.Lobby)
                {
                    if(!IsMobile)
                    {
                        var summary=Box(contextInfo,"room-settings-summary");summary.name="room-settings-summary";
                        Text(summary,"{0} / {1}명","context-name",state.Players.Count(p=>!p.IsSpectator&&p.IsConnected),state.Settings.MaxPlayers);
                        if(state.Settings.LiarMode!=LiarMode.Classic)Text(summary,state.Settings.LiarMode==LiarMode.Optional?"불확정":"미스매치","muted");
                        Text(summary,"라이어 {0}명","muted",state.Settings.LiarMode==LiarMode.Optional?(object)"0~1":state.Settings.LiarCount);
                        if(state.Settings.Victory==VictoryMode.RoundCount)Text(summary,"{0}라운드","muted",state.Settings.RoundCount);
                        else Text(summary,"목표 {0}점","muted",state.Settings.TargetScore);
                    }
                }
            }
            if(IsMobile)contextInfo.style.display=contextInfo.childCount==0?DisplayStyle.None:DisplayStyle.Flex;
        }
        private void SubmitVote()
        {
            var state=network.State;
            if(!CanSubmitVote(state)){if(state!=null)RefreshState(state);return;}
            int version=++_voteSubmissionVersion;
            string scope=VoteSubmissionScope(state);
            voteSubmitted=true;network.Vote(selectedPlayerId);RefreshState(network.State);
            root.schedule.Execute(()=>
            {
                var current=network.State;
                if(version!=_voteSubmissionVersion||!voteSubmitted||!IsNominationPhase(current)||VoteSubmissionScope(current)!=scope)return;
                voteSubmitted=false;actionKey="";RefreshState(current);
                if(current.RemainingSeconds>0)Toast("투표가 반영되지 않았어요. 다시 시도하세요.");
            }).StartingIn(5000);
        }
        private string VoteSubmissionScope(RoomSnapshot state)=>$"{lobby.Profile?.AccountId}/{lobby.RoomCode}/{state.MatchId}/{state.Round}/{state.BallotVersion}/{state.LocalPlayerId}";
        private bool CanSubmitVote(RoomSnapshot state)
        {
            if(!CanNominate(state)||!_hasSelectedPlayer)return false;
            var local=state.Players.FirstOrDefault(p=>p.Id==state.LocalPlayerId);
            if(local?.HasVoted==true&&state.LocalVoteTargetId==selectedPlayerId)return false;
            return IsNoLiarSelected(state)||CanSelectVote(state,state.Players.FirstOrDefault(p=>p.Id==selectedPlayerId));
        }
        private void SubmitJudgment(bool approve)
        {
            var state=network.State;var local=state?.Players.FirstOrDefault(p=>p.Id==state.LocalPlayerId);
            if(!CanJudge(state,local))return;
            _judgmentSubmitted=true;network.Judge(approve);RefreshState(state);
        }
        private void PhaseActions(RoomSnapshot state,PlayerView local)
        {
            phaseActions.Clear();
            bool centeredVote=IsNominationPhase(state)||state.Phase==GamePhase.Rebuttal;
            phaseActions.style.display=centeredVote?DisplayStyle.None:DisplayStyle.Flex;
            if(!IsMobile)phaseActions.parent.EnableInClassList("phase-controls-empty",centeredVote);
            switch(state.Phase)
            {
                case GamePhase.Lobby:
                    if(state.IsHost){var b=Button(phaseActions,"게임 시작",network.StartMatch,"primary",DrawSound.UiConfirm);b.SetEnabled(state.CanStart);}else Text(phaseActions,"방장 시작 대기","muted room-status-label");
                    if(state.IsHost)Button(phaseActions,"방 옵션",RoomOptions,"secondary").name="room-options-open";
                    if(state.IsHost&&!state.CanStart)
                    {
                        if(state.Settings.LiarMode==LiarMode.Mismatch&&state.Players.Count(player=>player.IsConnected&&!player.IsSpectator)>=GameRules.MinimumPlayers(state.Settings.LiarMode))
                            Text(phaseActions,"미스매치는 서로 다른 단어가 2개 이상인 주제가 필요합니다.","rules room-status-label");
                        else Text(phaseActions,"참가자 {0}명 필요","rules room-status-label",GameRules.MinimumPlayers(state.Settings.LiarMode));
                    }
                    if(!IsMobile){Button(phaseActions,RoomCopyLabel,CopyRoomInvite,"secondary");RoomFriendInviteButton(phaseActions,"secondary");}break;
                case GamePhase.Drawing:if(network.CanDraw)Button(phaseActions,"그리기 완료",network.EndTurn,"primary",DrawSound.UiConfirm);break;
                case GamePhase.Discussion:
                case GamePhase.Voting:
                case GamePhase.Rebuttal:
                    break;
                case GamePhase.Guessing:
                    if(CanGuess(state,local)&&!_guessSubmitted)Button(phaseActions,"정답 입력",OpenGuessingPopup,"primary",DrawSound.UiConfirm).name="guess-open";
                    else if(_guessSubmitted)Text(phaseActions,"제출 중","muted room-status-label");
                    else Text(phaseActions,local!=null&&local.HasGuessed?"제출 완료":"라이어 답변 대기","muted room-status-label");break;
                case GamePhase.RoundResults:Button(phaseActions,"결과 보기",()=>Results(state),"secondary");break;
                case GamePhase.MatchResults:Button(phaseActions,"최종 결과",()=>Results(state),"secondary");if(state.IsHost)Button(phaseActions,"한 판 더",network.ReturnToLobby,"primary");break;
            }
        }
        private static string PhaseName(GamePhase phase)
        {
            switch(phase){case GamePhase.Lobby:return "대기실";case GamePhase.RoleReveal:return "역할 확인";case GamePhase.Drawing:return "그리기";case GamePhase.Discussion:return "토론";case GamePhase.Rebuttal:return "반론";case GamePhase.Voting:return "투표";case GamePhase.LiarReveal:return "라이어 공개";case GamePhase.Guessing:return "정답 추측";case GamePhase.RoundResults:return "라운드 결과";default:return "최종 결과";}
        }
        private static bool HasHiddenMismatchRole(RoomSnapshot state)=>state.Settings.LiarMode==LiarMode.Mismatch
            &&state.Phase>=GamePhase.RoleReveal&&state.Phase<=GamePhase.Voting;
        private string LocalRoleName(RoomSnapshot state)=>state.LocalIsSpectator?"관전":HasHiddenMismatchRole(state)
            ||state.Settings.LiarMode==LiarMode.Mismatch&&HideLiarIdentity(state)?"제시어":state.LocalIsLiar?"라이어":"시민";
        private static string RoleInstructions(RoomSnapshot state)
        {
            if(state.LocalIsSpectator)return "그림과 토론을 지켜보며 누가 라이어인지 추측해 보세요.";
            if(HasHiddenMismatchRole(state))return "내 제시어에 맞춰 그리고, 다른 단어를 받은 사람을 찾아보세요. 나일 수도 있어요.";
            if(state.LocalIsLiar)return "시민인 척 들키지 않게 그림을 그리고, 시민들의 그림을 보고 제시어를 맞혀 보세요.";
            if(state.Settings.LiarMode==LiarMode.Optional)return "제시어에 맞춰 그리고, 라이어를 찾아 투표하세요. 없다고 생각하면 ‘라이어 없음’을 선택하세요.";
            return "제시어에 맞춰 그림을 그리고, 단어를 모르는 라이어를 찾아 투표하세요.";
        }
        private static string PhaseName(RoomSnapshot state)=>state.Phase==GamePhase.Rebuttal&&state.IsJudgmentCoinToss?"동전 던지기":state.Phase==GamePhase.Rebuttal&&IsNoLiarAccused(state)?"확인 투표":state.Phase==GamePhase.RoleReveal&&HasHiddenMismatchRole(state)?"제시어 확인":PhaseName(state.Phase);

        private void RoleReveal(RoomSnapshot state)
        {
            RoleInformation(state,true);
        }
        private void RoleInformation(RoomSnapshot state,bool showWord)
        {
            var modal=Modal("");
            BeginTextPresentation(state,state.Phase==GamePhase.RoleReveal);
            PresentationText(modal,L.Text(LocalRoleName(state)),"title role-reveal-title","role-reveal-title",.15f,.075f);
            modal.AddToClassList("room-role-details");
            var identity=Box(modal,"role-information-identity");
            var avatar=new AvatarElement(avatarColor,accessory);avatar.AddToClassList("avatar-preview");identity.Add(avatar);
            var details=Box(identity,"role-information-details");
            Text(details,"주제 · {0}","subtitle",state.Topic);
            if(showWord&&!string.IsNullOrEmpty(state.Word))PresentationText(details,L.Format("제시어 · {0}",state.Word),"section-title","role-reveal-word",.3f,.035f);
            if(state.Phase>=GamePhase.RoleReveal&&state.Phase<=GamePhase.Voting)
                Text(modal,RoleInstructions(state),"room-role-instructions").name="room-role-instructions";
            Button(modal,"확인",CloseModal,"primary").name="room-role-confirm";
            Enter(identity,300,10,120);
            RefreshTextPresentation();
        }
        private void Options()
        {
            OpenUtilityPopup(LobbyScreen.Options);
        }

        private void OpenUtilityPopup(LobbyScreen screen)
        {
            if(screen!=LobbyScreen.Options&&!lobby.IsAuthenticated)return;
            var returnFocus=_popupReturnFocus??root.focusController?.focusedElement as VisualElement;
            var modal=Modal("",false);modal.name=screen==LobbyScreen.Options?"settings-popup":screen==LobbyScreen.Friends?"friends-popup":screen==LobbyScreen.Notifications?"notifications-popup":screen==LobbyScreen.InviteFriends?"room-friends-popup":"account-popup";
            modal.AddToClassList("utility-popup");_utilityPopupScreen=screen;_popupReturnFocus=returnFocus;
            var popupOverlay=overlay;popupOverlay.AddToClassList("utility-overlay");
            var header=Box(modal,"row utility-popup-header");Text(header,screen==LobbyScreen.Options?"설정":screen==LobbyScreen.Friends?"친구":screen==LobbyScreen.Notifications?"알림":screen==LobbyScreen.InviteFriends?"친구 초대":"내 계정","utility-popup-title grow");
            if(screen==LobbyScreen.Notifications||screen==LobbyScreen.InviteFriends)
                IconButton(header,"새로고침",DrawUIIcon.Kind.Refresh,()=>RunSocialAction(lobby.RefreshSocialAsync),"social-refresh");
            IconButton(header,"닫기",DrawUIIcon.Kind.Close,CloseModal,"utility-popup-x");
            var body=Box(modal,"utility-popup-body grow");
            var scroll=DrawSmoothScroll.Create();scroll.name="utility-popup-scroll";scroll.AddToClassList("utility-popup-scroll");body.Add(scroll);
            var form=Box(scroll,"utility-popup-form");
            if(screen==LobbyScreen.Friends)FriendsForm(form);
            else if(screen==LobbyScreen.Options)OptionsForm(form);
            else if(screen==LobbyScreen.Notifications||screen==LobbyScreen.InviteFriends)SocialForm(form,screen);
            else AccountForm(form);
            Button(modal,"닫기",CloseModal,"secondary utility-popup-close",DrawSound.UiCancel).name="utility-popup-close";
            popupOverlay.focusable=true;popupOverlay.Focus();
            popupOverlay.RegisterCallback<PointerDownEvent>(e=>{if(overlay==popupOverlay&&ReferenceEquals(e.target,popupOverlay))CloseModal();});
            popupOverlay.RegisterCallback<KeyDownEvent>(e=>
            {
                if(e.keyCode!=KeyCode.Tab)return;
                var controls=modal.Query<VisualElement>().ToList().Where(control=>control.canGrabFocus&&control.enabledInHierarchy&&control.tabIndex>=0&&control.resolvedStyle.visibility==Visibility.Visible&&IsVisible(control)).ToList();
                if(controls.Count==0)return;
                int index=controls.IndexOf(root.focusController?.focusedElement as VisualElement);
                int next=index<0?(e.shiftKey?controls.Count-1:0):(index+(e.shiftKey?-1:1)+controls.Count)%controls.Count;
                root.focusController?.IgnoreEvent(e);controls[next].Focus();e.StopPropagation();
            },TrickleDown.TrickleDown);
            HideMobileScrollers();
            if(screen==LobbyScreen.Friends&&!lobby.IsBusy)Run(lobby.RefreshFriendsAsync);
            if(screen==LobbyScreen.Notifications||screen==LobbyScreen.InviteFriends)RunSocialAction(lobby.RefreshSocialAsync);
        }

        private static bool IsVisible(VisualElement element)
        {
            for(var current=element;current!=null;current=current.parent)
                if(current.resolvedStyle.display==DisplayStyle.None)return false;
            return true;
        }
        private void OptionsForm(VisualElement modal)
        {
            LanguageSelector(modal);
            var audio=DrawAudio.Instance;
            if(audio!=null)
            {
                var sound=Box(modal,"audio-options");Text(sound,"사운드","section-title");
                var mute=new Toggle(L.Text("모든 소리 끄기")){value=audio.Muted};SetText(mute.labelElement,"모든 소리 끄기");mute.name="audio-mute";mute.AddToClassList("field");
                mute.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,mute))audio.SetMuted(e.newValue);});sound.Add(mute);
                AudioVolume(sound,"전체 음량","audio-master",audio.MasterVolume,v=>audio.SetVolumes(v,audio.MusicVolume,audio.EffectsVolume));
                AudioVolume(sound,"배경 음악","audio-music",audio.MusicVolume,v=>audio.SetVolumes(audio.MasterVolume,v,audio.EffectsVolume));
                AudioVolume(sound,"효과음","audio-effects",audio.EffectsVolume,v=>audio.SetVolumes(audio.MasterVolume,audio.MusicVolume,v));
            }
            if(!inRoom&&lobby.IsAuthenticated)Button(modal,"내 계정",()=>Navigate(LobbyScreen.Account),"secondary");
        }
        private static void AudioVolume(VisualElement parent,string label,string name,float value,Action<float> changed)
        {
            var row=Box(parent,"audio-volume");var heading=Box(row,"row spread");Text(heading,label,"audio-label");
            var percentage=RawText(heading,Mathf.RoundToInt(value*100)+"%","audio-percentage");
            var slider=new Slider(0,1){value=value,name=name};slider.AddToClassList("audio-slider");row.Add(slider);
            var fill=new VisualElement{pickingMode=PickingMode.Ignore};fill.AddToClassList("audio-slider-fill");fill.style.width=Length.Percent(value*100);
            slider.Q<VisualElement>(className:"unity-base-slider__tracker")?.Add(fill);
            slider.RegisterValueChangedCallback(e=>{if(!ReferenceEquals(e.target,slider))return;fill.style.width=Length.Percent(e.newValue*100);SetRawText(percentage,Mathf.RoundToInt(e.newValue*100)+"%");changed(e.newValue);});
            slider.RegisterCallback<PointerUpEvent>(_=>DrawAudio.Instance?.Play(DrawSound.UiClick));
        }
        private void ProfileForm(VisualElement panel,VisualElement preview=null,Action onSaved=null,bool scrollInModal=false,Action onCancel=null)
        {
            if(!AvatarParts.Slots.Contains(_customizePartSlot))_customizePartSlot=AvatarPartSlot.Head;
            string accountId=lobby.Profile?.AccountId;
            var selectedColor=avatarColor;
            var ownedAccessories=lobby.Profile?.OwnedAccessories??Array.Empty<long>();
            var selectedAccessory=AvatarParts.KeepOwned(accessory,ownedAccessories);
            preview??=avatarStage;
            var equipmentButtons=new Dictionary<AvatarAccessory,Button>();
            var form=panel;
            if(!IsMobile||scrollInModal)
            {
                var scroll=DrawSmoothScroll.Create();scroll.AddToClassList("customize-scroll");panel.Add(scroll);form=scroll;
                if(scrollInModal){scroll.AddToClassList("utility-popup-scroll");scroll.AddToClassList("room-customize-scroll");scroll.name="room-customize-scroll";}
            }
            Button save=null;
            void UpdateAvatar()
            {
                preview.Clear();var a=new AvatarElement(selectedColor,selectedAccessory);a.AddToClassList("lobby-avatar");preview.Add(a);
                foreach(var item in equipmentButtons)
                {
                    bool equipped=item.Key==AvatarAccessory.None?AvatarParts.Get(selectedAccessory,_customizePartSlot)==0:AvatarParts.IsEquipped(selectedAccessory,(long)item.Key);
                    item.Value.EnableInClassList("equipped",equipped);
                    SetText(item.Value.Q<Label>(className:"equipment-state"),equipped?"장착 중":item.Key==AvatarAccessory.None?"":"장착하기");
                }
                bool canSave=AvatarParts.IsOwned(ownedAccessories,selectedAccessory);
                if(save!=null){save.userData=canSave;save.SetEnabled(canSave&&!lobby.IsBusy);}
            }
            UpdateAvatar();AccountExperience(form);var nameField=Field(form,"닉네임",nickname);nameField.maxLength=16;nameField.name="customize-nickname";
            Text(form,"몸 색상","section-title");var colors=Box(form,"row avatar-colors");var swatches=new List<Button>();
            void SelectColor(int color){selectedColor=color;for(var i=0;i<swatches.Count;i++)swatches[i].EnableInClassList("selected",i==color);UpdateAvatar();}
            foreach(var color in Enumerable.Range(0,AvatarElement.Colors.Length)){var b=Button(colors,"",()=>SelectColor(color),"swatch");b.style.backgroundColor=AvatarElement.Colors[color];SetTooltip(b,"몸 색상 {0}",color+1);swatches.Add(b);}
            SelectColor(selectedColor);
            Text(form,"아이템","section-title");
            VisualElement equipment=null;
            void RenderEquipment()
            {
                equipment.Clear();equipmentButtons.Clear();
                void AddEquipment(AvatarAccessory item,string name,string id)
                {
                    var button=Button(equipment,"",()=>
                    {
                        selectedAccessory=item==AvatarAccessory.None?AvatarParts.Remove(selectedAccessory,_customizePartSlot):AvatarParts.Equip(selectedAccessory,(long)item);
                        UpdateAvatar();RevealAvatarPreview();
                    },"equipment-option");
                    button.name=id+"-option";SetTooltip(button,name);
                    var icon=new AvatarElement(selectedColor,(long)item);icon.AddToClassList("equipment-preview");button.Add(icon);
                    Text(button,name,"equipment-name");Text(button,"","equipment-state");equipmentButtons[item]=button;
                }
                string defaultName=_customizePartSlot==AvatarPartSlot.Expression||_customizePartSlot==AvatarPartSlot.Body?"기본":"없음";
                AddEquipment(AvatarAccessory.None,defaultName,"none");
                foreach(var item in AvatarParts.Items.Where(part=>part.Slot==_customizePartSlot&&AvatarParts.IsOwned(ownedAccessories,(long)part.Accessory)))AddEquipment(item.Accessory,item.Name,item.Id);
                UpdateAvatar();
            }
            PartTabs(form,(int)_customizePartSlot,slot=>{_customizePartSlot=(AvatarPartSlot)slot;RenderEquipment();},false);
            equipment=Box(form,"row equipment-options");RenderEquipment();
            var reset=Button(form,"원래 모습",()=>{selectedAccessory=AvatarParts.KeepOwned(accessory,ownedAccessories);SelectColor(avatarColor);RevealAvatarPreview();},"secondary",DrawSound.UiCancel);reset.name="customize-reset";
            var footer=scrollInModal?Box(panel,"row room-customize-actions"):panel;
            if(scrollInModal)Button(footer,"취소",onCancel??CloseModal,"secondary grow",DrawSound.UiCancel).name="room-customize-cancel";
            save=Button(footer,"저장",()=>Run(async()=>
            {
                if(accountId!=lobby.Profile?.AccountId||panel.panel==null)return;
                if(!AvatarParts.IsOwned(ownedAccessories,selectedAccessory)){Toast("미구매 아이템은 구매하거나 벗긴 뒤 저장해 주세요.");return;}
                if(scrollInModal)panel.SetEnabled(false);
                try
                {
                    await lobby.SaveProfileAsync(nameField.value,selectedColor,selectedAccessory);
                    if(accountId!=lobby.Profile?.AccountId)return;
                    if(onSaved!=null)onSaved();else Navigate(LobbyScreen.Main);
                }
                finally{if(scrollInModal&&panel.panel!=null){panel.SetEnabled(true);UpdateAvatar();}}
            }),scrollInModal?"primary grow":"primary next-button",DrawSound.UiConfirm);save.name="customize-save";UpdateAvatar();
        }

        private static void PartTabs(VisualElement parent,int selected,Action<int> changed,bool all)
        {
            var tabs=Box(parent,"part-tabs");
            var buttons=new Dictionary<int,Button>();
            void AddTab(int index,string title,string name)
            {
                var button=Button(tabs,title,()=>
                {
                    if(selected==index)return;
                    selected=index;foreach(var entry in buttons)entry.Value.EnableInClassList("part-selected",entry.Key==index);
                    changed(index);
                },"secondary part-tab");
                button.name="part-filter-"+name;button.EnableInClassList("part-selected",selected==index);buttons[index]=button;
            }
            if(all){AddTab(-1,"전체","all");AddTab(SHOP_SET_FILTER,"세트","sets");}
            foreach(var slot in AvatarParts.Slots)AddTab((int)slot,AvatarParts.Name(slot),slot.ToString().ToLowerInvariant());
        }
        private static void TimeFields(VisualElement panel,RoomSettings settings)
        {
            panel.AddToClassList("time-form");
            Int(panel,"역할 확인",settings.RoleSeconds,3,30,v=>settings.RoleSeconds=v);Int(panel,"한 사람 그림",settings.DrawSeconds,5,180,v=>settings.DrawSeconds=v);
            Int(panel,"자유 토론",settings.DiscussionSeconds,5,300,v=>settings.DiscussionSeconds=v);
            settings.RebuttalSeconds=Mathf.Clamp(settings.RebuttalSeconds>0?settings.RebuttalSeconds:settings.VoteSeconds,5,180);
            Int(panel,"반론 · 찬반",settings.RebuttalSeconds,5,180,v=>settings.RebuttalSeconds=v);
            Int(panel,"라이어 공개",settings.RevealSeconds,GameRules.MIN_REVEAL_SECONDS,GameRules.MAX_REVEAL_SECONDS,v=>settings.RevealSeconds=v);
            Int(panel,"정답 추측",settings.GuessSeconds,5,120,v=>settings.GuessSeconds=v);
            Int(panel,"라운드 결과",settings.ResultSeconds,5,60,v=>settings.ResultSeconds=v);
            Text(panel,"단위: 초","rules");
        }
        private void TopicsForm(VisualElement panel)
        {
            BuildTopicWorkshopForm(panel);
        }
        private void OnChat(ChatLine line)
        {
            if(inRoom&&network.State?.Players.Any(player=>player.Id==line.PlayerId&&!player.IsSpectator)==true)
            {
                if(!_speeches.TryGetValue(line.PlayerId,out var speech)){speech=new SpeechState();_speeches.Add(line.PlayerId,speech);}
                speech.Messages.Enqueue(new SpeechMessage{Text=line.Text??"",Expires=Time.unscaledTime+SPEECH_MESSAGE_SECONDS});
                RefreshSpeeches();
            }
            AppendRoomChatLine(line);
        }
        private void CreateAccusedSpotlight(VisualElement frame)
        {
            _accusedSpotlight=Box(frame,"accused-spotlight");_accusedSpotlight.name="accused-spotlight";
            _accusedSpotlight.pickingMode=PickingMode.Ignore;_accusedSpotlight.style.display=DisplayStyle.None;
        }
        private void RefreshAccusedSpotlight(RoomSnapshot state)
        {
            if(_accusedSpotlight==null)return;
            var accused=state.HasAccused?state.Players.FirstOrDefault(player=>player.Id==state.AccusedPlayerId):null;
            bool visible=state.Phase==GamePhase.Rebuttal&&accused!=null&&surface?.HasAuthorPreview!=true;
            _accusedSpotlight.style.display=visible?DisplayStyle.Flex:DisplayStyle.None;
            if(!visible){_accusedKey="";return;}
            string key=$"{accused.Id}/{accused.Name}/{accused.Level}/{accused.AvatarColor}/{accused.Accessory}/{accused.AccountId}";
            if(key==_accusedKey)return;
            _accusedKey=key;_accusedSpotlight.Clear();
            var heading=Text(_accusedSpotlight,"지목된 플레이어","accused-caption");heading.pickingMode=PickingMode.Ignore;
            var avatar=new AvatarElement(accused.AvatarColor,accused.Accessory);avatar.AddToClassList("accused-avatar");
            _accusedSpotlight.Add(avatar);BindProfileTarget(avatar,accused.AccountId);
            var name=LeveledName(_accusedSpotlight,accused.Name,accused.Level,"accused-name","accused-name",accused.AccountId);BindProfileTarget(name,accused.AccountId);
            Enter(_accusedSpotlight,160,4);
        }
        private void CreateSpeechLayer(VisualElement parent)
        {
            _speechLayer=Box(parent,"game-speech-layer");_speechLayer.name="game-speech-layer";
            _speechLayer.pickingMode=PickingMode.Ignore;
        }
        private string SpeechPreview(string value)
        {
            value=value??"";
            int limit=IsMobile?18:40;
            if(value.Length<=limit)return value;
            int length=char.IsHighSurrogate(value[limit-1])?limit-1:limit;
            return value.Substring(0,length)+"…";
        }
        private void RefreshSpeeches()
        {
            if(_speechLayer?.panel==null)return;
            var placements=new List<SpeechPlacement>();
            foreach(int id in _speeches.Keys.ToArray())
            {
                var messages=_speeches[id].Messages;
                while(messages.Count>0&&messages.Peek().Expires<=Time.unscaledTime)messages.Dequeue().Label?.RemoveFromHierarchy();
                if(messages.Count>0)continue;
                _speeches.Remove(id);
                if(_speechVisuals.TryGetValue(id,out var expired)){expired.RemoveFromHierarchy();_speechVisuals.Remove(id);}
            }
            foreach(var speech in _speeches)
            {
                if(!playerCards.TryGetValue(speech.Key,out var card)||card.panel==null)
                {if(_speechVisuals.TryGetValue(speech.Key,out var hidden))hidden.style.display=DisplayStyle.None;continue;}
                if(!_speechVisuals.TryGetValue(speech.Key,out var bubble))
                {
                    bubble=Box(_speechLayer,"player-speech");bubble.name="speech-"+speech.Key;bubble.pickingMode=PickingMode.Ignore;
                    Box(bubble,"player-speech-messages").pickingMode=PickingMode.Ignore;
                    Box(bubble,"player-speech-tail").pickingMode=PickingMode.Ignore;_speechVisuals.Add(speech.Key,bubble);Enter(bubble,140,4);
                }
                var messageContainer=bubble.Q<VisualElement>(className:"player-speech-messages");int messageIndex=0;
                foreach(var message in speech.Value.Messages)
                {
                    if(message.Label?.parent!=messageContainer)
                    {message.Label=RawText(messageContainer,SpeechPreview(message.Text),"player-speech-text");message.Label.pickingMode=PickingMode.Ignore;}
                    message.Label.EnableInClassList("player-speech-following",messageIndex++>0);
                }
                bool visible=!IsMobile||_mobileChatSheet?.style.display==DisplayStyle.None;
                Rect cardBounds=card.worldBound;
                if(IsMobile)
                {
                    var viewport=_mobileRoomStack.ClassListContains("mobile-waiting")?_mobileRosterScroll?.contentViewport:_mobileRoster;
                    visible&=viewport!=null&&viewport.worldBound.Overlaps(cardBounds);
                }
                bubble.style.display=visible?DisplayStyle.Flex:DisplayStyle.None;
                if(!visible)continue;
                bool left=!IsMobile&&card.parent==_pcLeftPlayers;
                bubble.EnableInClassList("speech-left",left);bubble.EnableInClassList("speech-right",!IsMobile&&!left);
                bubble.EnableInClassList("speech-mobile",IsMobile);
                float width=IsMobile?Mathf.Max(64,cardBounds.width-16):Mathf.Clamp(_pcCenter.contentRect.width*.24f,120,224);
                bubble.style.width=width;
                float height=bubble.resolvedStyle.height;
                if(float.IsNaN(height)||height<=0)height=IsMobile?64:76;
                var position=IsMobile?new Vector2(cardBounds.x+8,cardBounds.yMax-72):
                    new Vector2(left?cardBounds.xMax+6:_pcRightPlayers.worldBound.x-width-6,cardBounds.y+32);
                if(IsMobile)position.y=cardBounds.yMax-height-8;
                Vector2 point=_speechLayer.WorldToLocal(position);
                if(IsMobile)
                {
                    float minimumY=_speechLayer.contentRect.yMin+8;
                    if(_mobileSecret?.panel!=null)minimumY=Mathf.Max(minimumY,_speechLayer.WorldToLocal(_mobileSecret.worldBound.max).y+8);
                    point.y=Mathf.Max(minimumY,Mathf.Min(point.y,_speechLayer.contentRect.yMax-height-8));
                }
                var bounds=new Rect(point,new Vector2(width,height));
                if(!IsMobile)
                {
                    foreach(var control in new[]{phaseActions,drawingTools})
                    {
                        if(control?.panel==null||control.resolvedStyle.display==DisplayStyle.None)continue;
                        var minimum=_speechLayer.WorldToLocal(control.worldBound.min);var maximum=_speechLayer.WorldToLocal(control.worldBound.max);
                        var controlBounds=new Rect(minimum,maximum-minimum);
                        if(bounds.Overlaps(controlBounds))bounds.y=controlBounds.yMin-height-8;
                    }
                }
                placements.Add(new SpeechPlacement{Element=bubble,Bounds=bounds,Left=left});
            }
            if(!IsMobile)
            {
                float minimumY=_speechLayer.contentRect.yMin+8;
                var secretBar=_pcRoomSecret;
                if(secretBar?.panel!=null)minimumY=Mathf.Max(minimumY,_speechLayer.WorldToLocal(secretBar.worldBound.max).y+8);
                foreach(bool left in new[]{true,false})
                {
                    float next=float.PositiveInfinity;
                    var side=placements.Where(value=>value.Left==left).OrderByDescending(value=>value.Bounds.y).ToArray();
                    foreach(var placement in side)
                    {
                        placement.Bounds.y=Mathf.Min(placement.Bounds.y,next-placement.Bounds.height-6);
                        next=placement.Bounds.y;
                    }
                    if(side.Length>0)
                    {
                        float shift=Mathf.Max(0,minimumY-side.Min(value=>value.Bounds.y));
                        foreach(var placement in side)placement.Bounds.y+=shift;
                    }
                }
            }
            foreach(var placement in placements)
            {
                if(!IsMobile&&_pcRoomChat?.panel!=null)
                {
                    var minimum=_speechLayer.WorldToLocal(_pcRoomChat.worldBound.min);
                    var maximum=_speechLayer.WorldToLocal(_pcRoomChat.worldBound.max);
                    var chatBounds=new Rect(minimum,maximum-minimum);
                    if(placement.Bounds.Overlaps(chatBounds))placement.Bounds.x=chatBounds.xMax+8;
                }
                placement.Element.style.left=placement.Bounds.x;placement.Element.style.top=placement.Bounds.y;
                placement.Element.style.width=placement.Bounds.width;
            }
        }
        private void Tick()
        {
            var state=network.State;if(state==null||!inRoom)return;
            UpdateGameTimers(state);
            RefreshTextPresentation();
            RefreshJudgmentCoinToss(state);
            RefreshDrawingInteractions(state);
            RefreshPlayerStates(state);RefreshContext(state);RefreshSpeeches();
        }
        private void UpdateGameTimers(RoomSnapshot state)
        {
            double now=Time.realtimeSinceStartupAsDouble;
            _timerClock.Observe(state,now);
            float remaining=_timerClock.Remaining(now);
            _hudTimer?.Refresh(remaining,_timerClock.Duration,_timerClock.Period,_timerClock.IsVisible);
            _guessCountdown?.Refresh(remaining,_timerClock.Duration,_timerClock.Period,_timerClock.IsVisible);
        }
        private void RefreshServiceStatus()
        {
            RefreshPublicProfileActions();
            RefreshCommerceControls();
            var save=root.Q<Button>("customize-save");if(save!=null)save.SetEnabled(save.userData is bool canSave&&canSave&&!lobby.IsBusy);
            shopList?.Query<Button>().ForEach(button=>{if(lobby.IsBusy)button.SetEnabled(false);});
            RefreshShopCart();
            root.Query<Button>(className:"guest-login").ForEach(button=>button.SetEnabled(DrawGuestCredentialStore.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-login").ForEach(button=>button.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-link").ForEach(button=>button.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-cancel").ForEach(button=>button.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None);
            if(serviceNotice!=null){SetRawText(serviceNotice,lobby.Status);serviceNotice.style.display=string.IsNullOrWhiteSpace(lobby.Status)?DisplayStyle.None:DisplayStyle.Flex;}
            else if(!_roomRefreshRunning&&_workshopPendingCalls==0&&lastServiceStatus!=lobby.Status)Toast(lobby.Status);
            lastServiceStatus=lobby.Status;
            if(!lobby.IsBusy){RefreshPublicRooms();RefreshFriends();RefreshShop();}
            RefreshTopicWorkshopControls();
        }
        private async void Run(Func<Task> action)
        {
            if(runningAction||lobby.IsBusy){Toast("연결을 처리하고 있어요. 잠시 기다려 주세요.");return;}
            runningAction=true;
            try{await action();}catch(OperationCanceledException){}catch(Exception ex){DrawAudio.Instance?.Play(DrawSound.UiError);Toast(ex.Message);Debug.LogWarning(ex.Message);}
            finally{runningAction=false;RefreshSocialControls();RefreshCommerceControls();}
        }
        private VisualElement Modal(string title,bool mobileScroll=true)
        {
            HideModeTooltip();
            if(IsMobile)CloseChat();
            CloseModal();overlay=Box(IsMobile&&_roomCustomizeOverlay==null?content:root,"overlay enter");var modal=Box(overlay,"modal");
#if UNITY_WEBGL && !UNITY_EDITOR
            _browserTextInput?.RefreshChatShortcut();
#endif
            VisualElement body=modal;
            if(IsMobile&&mobileScroll){var scroll=DrawSmoothScroll.Create(ScrollViewMode.Vertical);scroll.AddToClassList("mobile-modal-scroll");modal.Add(scroll);body=Box(scroll,"mobile-modal-body");}
            if(!string.IsNullOrEmpty(title))Text(body,title,"title");HideMobileScrollers();DrawUIMotion.ShowModal(overlay,modal);
            if(_roomCustomizeOverlay!=null){overlay.focusable=true;overlay.Focus();}
            _roomPasswordOverlay?.BringToFront();return body;
        }
        private void HideMobileScrollers()
        {
            if(!IsMobile)return;
            root.Query<ScrollView>().ForEach(scroll=>{scroll.horizontalScrollerVisibility=ScrollerVisibility.Hidden;scroll.verticalScrollerVisibility=ScrollerVisibility.Hidden;});
        }
        private void CloseModal()
        {
            CloseTextPresentation();
            ClearRoomTopicsPopup();
            CloseRoomTopicWorkshop(false);
            ClearGuessingPopup();
            ClosePublicProfile(false);
            var returnFocus=_popupReturnFocus;_popupReturnFocus=null;
            if(_utilityPopupScreen.HasValue)friendList=null;
            _utilityPopupScreen=null;
            _socialList=null;
            if(_lobbyChatPanel?.ClassListContains("lobby-chat-in-modal")==true)ResetLobbyChatView();
            _roomOptionsEditor=null;_roomOptionsDraft=null;_roomOptionsSaving=false;
            _roomOptionsRefreshTopics=null;_roomOptionsCustomTopics.Clear();
            ClearRoomOptionsPassword();
            DrawAudio.Instance?.FlushSettings();
            if(overlay!=null){var previous=overlay;overlay=null;DrawUIMotion.HideModal(previous,previous.Q<VisualElement>(className:"modal"));}
            if(returnFocus!=null)root.schedule.Execute(()=>{if(overlay==null&&returnFocus.panel!=null&&!ChatInputHasFocus())returnFocus.Focus();}).StartingIn(180);
            RestoreRoomCustomizationFocus();
        }
        private void Enter(VisualElement element,int duration,float offset,int delay=0)
        {
            DrawUIMotion.Enter(element,duration,offset,delay);
        }
        private void Toast(string message)
        {
            if(string.IsNullOrWhiteSpace(message))return;root.Q<Label>("toast")?.RemoveFromHierarchy();var label=Text(root,message,"toast");label.name="toast";label.pickingMode=PickingMode.Ignore;DrawUIMotion.ShowToast(label,4200);
        }

        private void CreateServiceNotice(VisualElement parent)
        {
            serviceNotice=Text(parent,"","notice");serviceNotice.style.display=DisplayStyle.None;
        }

        private static void LanguageSelector(VisualElement parent)
        {
            var languages=L.AvailableLanguages;int index=languages.ToList().FindIndex(language=>language.Code==L.CurrentLanguageCode);
            var field=new DropdownField(L.Text("언어"),languages.Select(language=>language.DisplayName).ToList(),Mathf.Max(0,index));
            field.AddToClassList("field");field.AddToClassList("language-selection");SetText(field.labelElement,"언어");parent.Add(field);
            field.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,field)&&field.index>=0&&field.index<languages.Count)L.SetLanguage(languages[field.index].Code);});
        }

        private void OnLanguageChanged()
        {
            if(root==null)return;
            root.EnableInClassList("rtl",L.IsRightToLeft);
            root.Query<TextElement>().ForEach(element=>{if(LOCALIZED_TEXT.TryGetValue(element,out var value))element.text=value.Resolve();});
            root.Query<VisualElement>().ForEach(element=>{if(LOCALIZED_TOOLTIPS.TryGetValue(element,out var value))element.tooltip=value.Resolve();});
            PositionModeTooltip();
            root.Query<TextField>().ForEach(field=>{if(LOCALIZED_PLACEHOLDERS.TryGetValue(field,out var value))field.textEdition.placeholder=value.Resolve();});
            root.Query<DropdownField>().ForEach(field=>
            {
                if(LOCALIZED_CHOICES.TryGetValue(field,out var choices))
                {
                    int index=Mathf.Clamp(field.index,0,choices.Length-1);field.choices=choices.Select(L.Text).ToList();
                    if(choices.Length>0)field.SetValueWithoutNotify(field.choices[index]);
                }
                else if(field.ClassListContains("language-selection"))
                {
                    var selected=L.AvailableLanguages.First(language=>language.Code==L.CurrentLanguageCode);field.SetValueWithoutNotify(selected.DisplayName);
                }
            });
            root.Query<Image>(className:"brand-logo").ForEach(logo=>logo.image=L.LoadLogo());
            RebuildTextPresentationLanguage();
            if(inRoom&&network.State!=null)RefreshState(network.State);
            lastServiceStatus=lobby.Status;if(serviceNotice!=null)SetRawText(serviceNotice,lastServiceStatus);
            RefreshLobbyChat();
            RefreshShopCart();
            OnTopicWorkshopLanguageChanged();
            RefreshCommerceView();
            DrawLocalizedTypography.Apply(root);
            if(_profileOverlay!=null)BeginPublicProfileLoad();
            shopList?.schedule.Execute(()=>shopList?.Query<VisualElement>(className:"shop-entry").ForEach(card=>SizeShopActions(card,card.Q<VisualElement>(className:"shop-actions")))).StartingIn(1);
        }
        private void OnDestroy()
        {
            ResetTextPresentation();
            ClearJudgmentCoinToss();
            HideModeTooltip();
            ClearDrawingPreview();
            ResetGuessingInput(true);
            CloseRoomCustomization(false,true);
            ClosePublicProfile(false);
            ResetLobbyChatView();
            DetachSocialEvents();
            DetachRoomPasswordEvents();
            DetachTopicWorkshopEvents();EndTopicWorkshopPage();DetachMatchRewardEvents();
            DetachCommerceEvents();
            ClearRoomPasswordSecrets();
            if(network!=null){network.StateChanged-=RefreshState;network.Kicked-=OnRoomKicked;network.ChatReceived-=OnChat;network.AuthorDrawingChanged-=OnAuthorDrawingChanged;}
            if(lobby!=null){lobby.Changed-=RefreshServiceStatus;lobby.ProfileChanged-=OnProfileChanged;lobby.LobbyChatChanged-=RefreshLobbyChat;lobby.LobbyChatNotice-=Toast;}
            if(panelSettings!=null)Destroy(panelSettings);
            if(_mobileLayout!=null){_mobileLayout.LayoutChanged-=OnMobileLayoutChanged;_mobileLayout.Dispose();}
        }
        private static VisualElement Box(VisualElement parent,string classes){var e=new VisualElement();Classes(e,classes);parent.Add(e);return e;}
        private static Label Text(VisualElement parent,string source,string classes,params object[] args){var e=new Label();e.enableRichText=false;SetText(e,source,args);Classes(e,classes);parent.Add(e);return e;}
        private static Label RawText(VisualElement parent,string value,string classes){var e=new Label(L.Raw(value));e.enableRichText=false;Classes(e,classes);parent.Add(e);return e;}
        private static void SetText(TextElement element,string source,params object[] args)
        {
            if(element==null)return;
            if(LOCALIZED_TEXT.TryGetValue(element,out var previous)&&previous.Matches(source,args))return;
            var binding=new LocalizedValue{Source=source,Arguments=args};
            LOCALIZED_TEXT.Remove(element);LOCALIZED_TEXT.Add(element,binding);element.text=binding.Resolve();
        }
        private static void SetRawText(TextElement element,string value){if(element==null)return;LOCALIZED_TEXT.Remove(element);element.text=L.Raw(value);}
        private static void SetTooltip(VisualElement element,string source,params object[] args)
        {
            if(LOCALIZED_TOOLTIPS.TryGetValue(element,out var previous)&&previous.Matches(source,args))return;
            var binding=new LocalizedValue{Source=source,Arguments=args};
            LOCALIZED_TOOLTIPS.Remove(element);LOCALIZED_TOOLTIPS.Add(element,binding);element.tooltip=binding.Resolve();
        }
        private static void Placeholder(TextField field,string source)
        {
            var binding=new LocalizedValue{Source=source,Arguments=Array.Empty<object>()};LOCALIZED_PLACEHOLDERS.Remove(field);LOCALIZED_PLACEHOLDERS.Add(field,binding);field.textEdition.placeholder=binding.Resolve();
        }
        private static void LeftToRightInput(TextField field)
        {
            var input=field.Q<VisualElement>(className:"unity-base-text-field__input");if(input!=null)input.languageDirection=LanguageDirection.LTR;
        }
        private static Button Button(VisualElement parent,string source,Action clicked,string classes,DrawSound sound=DrawSound.UiClick){var cue=sound==DrawSound.UiClick&&classes.Split(' ').Contains("danger")?DrawSound.UiCancel:sound;var e=new Button(()=>{DrawAudio.Instance?.Play(cue);clicked?.Invoke();});e.enableRichText=false;SetText(e,source);e.AddToClassList("button");Classes(e,classes);parent.Add(e);DrawButtonScale.Bind(e);return e;}
        private static Button IconButton(VisualElement parent,string title,DrawUIIcon.Kind icon,Action clicked,string name)
        {
            var button=Button(parent,"",clicked,"secondary icon-button");button.name=name;button.AddToClassList(name);SetTooltip(button,title);button.Add(new DrawUIIcon(icon));return button;
        }
        private static void Classes(VisualElement e,string classes){foreach(var c in classes.Split(' '))if(c.Length>0)e.AddToClassList(c);}
        private static TextField Field(VisualElement parent,string label,string value){var e=new TextField(L.Text(label)){value=L.Raw(value)};SetText(e.labelElement,label);e.AddToClassList("field");parent.Add(e);return e;}
#if UNITY_WEBGL && !UNITY_EDITOR
        private static TextField Int(VisualElement parent,string label,int value,int min,int max,Action<int> changed)
        {
            int current=Mathf.Clamp(value,min,max);
            var field=new TextField(L.Text(label)){value=current.ToString(System.Globalization.CultureInfo.InvariantCulture),isDelayed=true,maxLength=10};
            field.textEdition.keyboardType=TouchScreenKeyboardType.NumberPad;
            SetText(field.labelElement,label);field.AddToClassList("field");LeftToRightInput(field);
            field.RegisterValueChangedCallback(e=>
            {
                if(!ReferenceEquals(e.target,field))return;
                if(int.TryParse(e.newValue,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out int parsed))current=Mathf.Clamp(parsed,min,max);
                field.SetValueWithoutNotify(current.ToString(System.Globalization.CultureInfo.InvariantCulture));changed(current);
            });
            parent.Add(field);return field;
        }
#else
        private static IntegerField Int(VisualElement parent,string label,int value,int min,int max,Action<int> changed)
        {
            var field=new IntegerField(L.Text(label)){value=value,isDelayed=true};SetText(field.labelElement,label);field.AddToClassList("field");field.RegisterValueChangedCallback(e=>{if(!ReferenceEquals(e.target,field))return;var v=Mathf.Clamp(e.newValue,min,max);field.SetValueWithoutNotify(v);changed(v);});parent.Add(field);return field;
        }
#endif
        private static DropdownField Choice(VisualElement parent,string label,string[] options,int index,Action<int> changed)
        {
            var field=new DropdownField(L.Text(label),options.Select(L.Text).ToList(),Mathf.Clamp(index,0,options.Length-1));SetText(field.labelElement,label);LOCALIZED_CHOICES.Add(field,options);field.AddToClassList("field");field.RegisterValueChangedCallback(e=>{if(ReferenceEquals(e.target,field))changed(field.index);});parent.Add(field);return field;
        }
    }
}
