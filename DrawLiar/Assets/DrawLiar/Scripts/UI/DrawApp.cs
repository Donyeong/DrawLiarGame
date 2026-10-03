using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawApp : MonoBehaviour
    {
        private DrawNetworkManager network;
        private LobbyServiceBridge lobby;
        private VisualElement root, content, avatarStage, playerStrip, phaseActions, overlay, phaseBanner, drawingTools, contextInfo, chatbar;
        private Label word, topic, role, phaseTitle, phaseDetail, timer, roomBadge, roundCaption, serviceNotice, voteProgress;
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
        private enum LobbyScreen { Login, Main, Join, JoinCode, Browse, CreateMode, CreateRules, CreateDetails, Time, TopicSelection, Topics, Options, Customize, Account, Friends, Shop }
        private LobbyScreen lobbyScreen = LobbyScreen.Login;
        private bool inRoom;
        private bool runningAction;
        private bool reducedMotion;
        private GamePhase previousPhase=(GamePhase)(-1);
        private string playerKey="", actionKey="";
        private readonly Dictionary<int,VisualElement> playerCards=new Dictionary<int,VisualElement>();
        private readonly Dictionary<int,Label> playerStatuses=new Dictionary<int,Label>();
        private readonly List<DrawStroke> pendingStrokes=new List<DrawStroke>();
        private RoomSettings draft=new RoomSettings();
        private ScrollView friendList, shopList;
        private string _shopPreviewProductId="";
        private Label _shopPreviewStatus;
        private Button _shopPreviewReset;
        private MobileUILayout _mobileLayout;
        private VisualElement _mobileRoomStack, _mobileWorkspace, _mobileControls, _mobileSecret, _mobileCanvas, _mobileRoster, _mobileActions, _mobileChatSheet;
        private Label _mobileRoundInfo, _mobileRoundHint;
        private Button _mobileRoomCode;
        private ScrollView _mobileRoomScroll;
        private ScrollView _mobileRosterScroll;
        private Label _mobileRosterTitle;
        private int _chatTransitionVersion;
        private bool _backgroundRoomRefresh = true, _roomRefreshRunning;
        private string _roomSearch = "";
        private float _nextRoomRefresh;
        private bool IsMobile => _mobileLayout != null && _mobileLayout.IsMobile;

        private void Start()
        {
            network=GetComponent<DrawNetworkManager>();
            lobby=GetComponent<LobbyServiceBridge>();
            lobby.Initialize(network);
            draft.Topics=GameDataStore.Load().Topics.Select(t=>t.Name).ToArray();
            nickname=PlayerPrefs.GetString("DrawLiar.Name","동글이"+UnityEngine.Random.Range(10,99));
            avatarColor=Mathf.Clamp(PlayerPrefs.GetInt("DrawLiar.Color",0),0,AvatarElement.Colors.Length-1);
            accessory=Mathf.Clamp(PlayerPrefs.GetInt("DrawLiar.Equipment",(int)AvatarAccessory.Painter),0,(int)AvatarAccessory.Painter);
            var panel=panelSettings=ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet=Resources.Load<ThemeStyleSheet>("DrawLiar/DrawLiarTheme");
            panel.scaleMode=PanelScaleMode.ScaleWithScreenSize;panel.referenceResolution=new Vector2Int(1600,1000);
            panel.screenMatchMode=PanelScreenMatchMode.MatchWidthOrHeight;panel.match=.5f;
            var document=gameObject.AddComponent<UIDocument>();document.panelSettings=panel;
            root=document.rootVisualElement;root.AddToClassList("app");
            root.styleSheets.Add(Resources.Load<StyleSheet>("DrawLiar/DrawLiar"));
            root.styleSheets.Add(Resources.Load<StyleSheet>("DrawLiar/DrawLiarMobile"));
            _mobileLayout=new MobileUILayout(root,panelSettings);
            _mobileLayout.LayoutChanged+=OnMobileLayoutChanged;
            reducedMotion=PlayerPrefs.GetInt("DrawLiar.ReduceMotion",0)==1;
            root.EnableInClassList("reduce-motion",reducedMotion);
            root.RegisterCallback<GeometryChangedEvent>(_=>root.EnableInClassList("compact",root.contentRect.width<1400||root.contentRect.height<880));
            root.RegisterCallback<KeyDownEvent>(RoomShortcut);
            network.StateChanged+=RefreshState;network.Notice+=message=>
            {
                if(voteSubmitted&&network.State?.Phase==GamePhase.Voting&&!network.State.Players.Any(p=>p.Id==network.State.LocalPlayerId&&p.HasVoted))
                {voteSubmitted=false;actionKey="";RefreshState(network.State);}
                DrawAudio.Instance?.Play(DrawSound.UiError);Toast(message);
            };
            network.StrokeReceived+=s=> { if(surface!=null)surface.Apply(s);else pendingStrokes.Add(s); };
            network.CanvasCleared+=()=>{surface?.ClearCanvas();pendingStrokes.Clear();};
            network.ChatReceived+=OnChat;
            lobby.Changed+=RefreshServiceStatus;
            lobby.ProfileChanged+=OnProfileChanged;
            content=Box(root,"grow app-content");Home();
            root.schedule.Execute(Tick).Every(100);
            root.schedule.Execute(RefreshRoomsInBackground).Every(1000);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var args=Environment.GetCommandLineArgs();
            string Arg(string key) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:""; }
            if(!string.IsNullOrEmpty(Arg("-drawName"))){nickname=Arg("-drawName");Home();}
            if(args.Contains("-drawDevelopmentLogin"))Run(async()=>
            {
                await lobby.DevelopmentLoginAsync(nickname);Navigate(LobbyScreen.Main);
                if(args.Contains("-drawOnlineHost")){draft.IsPrivate=true;draft.RoomName="DrawLiar 연결 검사";await lobby.HostAsync(draft.Copy());}
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
            inRoom=false;surface=null;publicRoomList=null;friendList=null;shopList=null;avatarStage=null;serviceNotice=null;_shopPreviewStatus=null;_shopPreviewReset=null;chatHistory=null;chatInput=null;content.Clear();playerCards.Clear();playerStatuses.Clear();previousPhase=(GamePhase)(-1);
            if(!lobby.IsAuthenticated)lobbyScreen=LobbyScreen.Login;
            if(lobbyScreen!=LobbyScreen.Shop)_shopPreviewProductId="";
            root.RemoveFromClassList("in-game");content.RemoveFromClassList("room-layout");root.AddToClassList("at-home");
            if(!IsMobile)panelSettings.referenceResolution=new Vector2Int(1600,1000);
            _mobileLayout.Refresh();
            if(IsMobile){MobileHome();HideMobileScrollers();return;}
            var home=Box(content,"home");
            var layout=Box(home,"home-layout");var character=Box(layout,"character-side");
            BrandLogo(character,"game-logo");
            avatarStage=Box(character,"character-stage");UpdateLobbyAvatar(avatarColor,accessory);
            Text(character,nickname,"character-name");
            var customize=Button(character,"커스터마이징",()=>Navigate(LobbyScreen.Customize),"customize-button");
            customize.SetEnabled(lobby.IsAuthenticated);
            if(lobbyScreen==LobbyScreen.Customize)customize.style.visibility=UnityEngine.UIElements.Visibility.Hidden;
            var panel=Box(layout,"home-panel");
            serviceNotice=null;
            if(lobbyScreen==LobbyScreen.Main)
            {
                panel.AddToClassList("main-menu");
                Button(panel,"방 만들기",()=>Navigate(LobbyScreen.CreateMode),"menu-button menu-orange");
                Button(panel,"참가하기",()=>Navigate(LobbyScreen.Join),"menu-button menu-mint");
                Button(panel,"옵션",()=>Navigate(LobbyScreen.Options),"menu-button menu-purple");
                var services=Box(panel,"row");
                Button(services,"친구",()=>{Navigate(LobbyScreen.Friends);Run(lobby.RefreshFriendsAsync);},"secondary grow");
                Button(services,"상점",()=>{Navigate(LobbyScreen.Shop);Run(lobby.RefreshShopAsync);},"secondary grow");
                Button(panel,"내 계정",()=>Navigate(LobbyScreen.Account),"secondary");
                Text(panel,"친구 3–8명과 함께하는 그림 추리","menu-caption");
            }
            else
            {
                var top=Box(panel,"screen-heading");
                if(lobbyScreen!=LobbyScreen.Login)Button(top,"← 뒤로",Back,"back-button",DrawSound.UiCancel);
                var body=Box(panel,"screen-body");
                BuildLobbyScreen(body);
                serviceNotice=Text(body,lobby.Status,"notice");
            }
            Enter(panel,260,14);
        }

        private void BuildLobbyScreen(VisualElement body)
        {
                switch(lobbyScreen)
                {
                    case LobbyScreen.Login:Text(body,"시작하기","screen-title");LoginForm(body);break;
                    case LobbyScreen.Join:
                        Text(body,"참가하기","screen-title");
                        Button(body,"코드로 참가",()=>Navigate(LobbyScreen.JoinCode),"menu-button menu-orange");
                        Button(body,"공개방 둘러보기",EnterBrowse,"menu-button menu-mint");break;
                    case LobbyScreen.JoinCode:Text(body,"코드로 참가","screen-title");JoinForm(body);break;
                    case LobbyScreen.Browse:Text(body,"공개방 둘러보기","screen-title");BrowseForm(body);break;
                    case LobbyScreen.CreateMode:CreateMode(body);break;
                    case LobbyScreen.CreateRules:CreateRules(body);break;
                    case LobbyScreen.CreateDetails:CreateDetails(body);break;
                    case LobbyScreen.Time:Text(body,"시간 설정","screen-title");TimeForm(body);break;
                    case LobbyScreen.TopicSelection:Text(body,"주제 선택","screen-title");TopicSelectionForm(body);break;
                    case LobbyScreen.Topics:Text(body,"나만의 주제","screen-title");TopicsForm(body);break;
                    case LobbyScreen.Options:Text(body,"옵션","screen-title");OptionsForm(body);break;
                    case LobbyScreen.Customize:Text(body,"커스터마이징","screen-title");ProfileForm(body);break;
                    case LobbyScreen.Account:Text(body,"내 계정","screen-title");AccountForm(body);break;
                    case LobbyScreen.Friends:Text(body,"친구","screen-title");FriendsForm(body);break;
                    case LobbyScreen.Shop:Text(body,"상점","screen-title");ShopForm(body);break;
                }
        }

        private void MobileHome()
        {
            bool signingIn=lobbyScreen==LobbyScreen.Login;
            if(!signingIn)MobileTopbar();
            if(lobbyScreen==LobbyScreen.Shop||lobbyScreen==LobbyScreen.Customize)MobileProfilePreview();
            var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.AddToClassList("mobile-home-scroll");content.Add(scroll);
            var home=Box(scroll,"mobile-home");
            if(signingIn)
            {
                BrandLogo(home,"mobile-logo mobile-login-logo");
                var welcome=Box(home,"mobile-welcome row");
                var mascot=new AvatarElement(avatarColor,accessory);mascot.AddToClassList("mobile-welcome-mascot");welcome.Add(mascot);
                var copy=Box(welcome,"grow");Text(copy,"그림으로 속여볼까요?","mobile-welcome-title");Text(copy,"가입 없이 바로 한 판 시작해요","muted mobile-small");
                var card=Box(home,"home-panel mobile-card");BuildLobbyScreen(card);serviceNotice=Text(card,lobby.Status,"notice");
            }
            else if(lobbyScreen==LobbyScreen.Main)
            {
                var profile=Box(home,"mobile-card mobile-hero");var identity=Box(profile,"mobile-profile-row row");
                var avatar=new AvatarElement(avatarColor,accessory);avatar.AddToClassList("mobile-profile-mascot");identity.Add(avatar);
                var detail=Box(identity,"grow");Text(detail,nickname,"mobile-welcome-title");
                Text(detail,lobby.Profile?.HasGoogleAccount==true?"Google 연동 계정":"게스트 · 이 기기에서 플레이","muted mobile-small");
                Button(detail,"캐릭터 꾸미기",()=>Navigate(LobbyScreen.Customize),"secondary");
                var actions=Box(home,"mobile-section mobile-actions");Text(actions,"어떻게 함께할까요?","mobile-section-header");
                Button(actions,"공개 방 찾기",EnterBrowse,"primary");
                Button(actions,"친구의 방 코드로 입장",()=>Navigate(LobbyScreen.JoinCode),"secondary");
                Button(actions,"새 방 만들기",()=>Navigate(LobbyScreen.CreateMode),"secondary");
                var hint=Box(home,"mobile-card mobile-section");Text(hint,"3명부터 함께 그려요","mobile-card-title");
                Text(hint,"최대 8명 · PC와 모바일 함께 참여\n진행 중인 방은 관전으로 들어갈 수 있어요.","muted mobile-small");
            }
            else
            {
                var body=Box(home,lobbyScreen==LobbyScreen.JoinCode||lobbyScreen==LobbyScreen.Browse||lobbyScreen==LobbyScreen.Shop?"home-panel mobile-page-body":"home-panel mobile-card mobile-page-body");
                BuildLobbyScreen(body);serviceNotice=Text(body,lobby.Status,"notice");
            }
            Text(home,"Liar’s Canvas · RascalLab","mobile-footer muted");
            if(!signingIn)MobileNavigation();
            DrawUIMotion.Stagger(home,28,220,8);
        }

        private void MobileTopbar()
        {
            var header=Box(content,"mobile-topbar row");
            if(lobbyScreen==LobbyScreen.Main)BrandLogo(header,"mobile-topbar-logo");
            else
            {
                Button(header,"‹",Back,"secondary mobile-back",DrawSound.UiCancel).tooltip="뒤로";
                Text(header,LobbyTitle(lobbyScreen),"mobile-workspace-title grow");
            }
            if(lobbyScreen==LobbyScreen.Main||lobbyScreen==LobbyScreen.Shop)
                Text(header,$"{lobby.Profile?.Coins??0} 코인","mobile-status-pill");
            if(lobbyScreen==LobbyScreen.Main)Button(header,"설정",()=>Navigate(LobbyScreen.Options),"secondary mobile-back");
        }

        private void MobileProfilePreview()
        {
            var hero=Box(content,"mobile-profile-hero row");
            avatarStage=Box(hero,"mobile-profile-preview");UpdateLobbyAvatar(avatarColor,accessory);
            var detail=Box(hero,"grow");Text(detail,nickname,"mobile-card-title");
            if(lobbyScreen==LobbyScreen.Shop)
            {
                _shopPreviewStatus=Text(detail,"현재 장착한 모습","shop-preview-status mobile-small");
                _shopPreviewStatus.name="shop-preview-status";
                Text(detail,"미리보기는 장착 상태를 바꾸지 않아요","muted mobile-small");
            }
            else Text(detail,"나만의 캐릭터를 꾸며보세요","muted mobile-small");
        }

        private void MobileNavigation()
        {
            var navigation=Box(content,"mobile-bottom-nav row");
            MobileTab(navigation,"플레이",LobbyScreen.Main,lobbyScreen!=LobbyScreen.Friends&&lobbyScreen!=LobbyScreen.Shop&&lobbyScreen!=LobbyScreen.Account);
            MobileTab(navigation,"친구",LobbyScreen.Friends,lobbyScreen==LobbyScreen.Friends);
            MobileTab(navigation,"상점",LobbyScreen.Shop,lobbyScreen==LobbyScreen.Shop);
            MobileTab(navigation,"계정",LobbyScreen.Account,lobbyScreen==LobbyScreen.Account);
        }

        private void MobileTab(VisualElement parent,string title,LobbyScreen destination,bool selected)
        {
            var button=Button(parent,title,()=>
            {
                if(lobbyScreen==destination)return;
                Navigate(destination);
                if(destination==LobbyScreen.Friends)Run(lobby.RefreshFriendsAsync);
                else if(destination==LobbyScreen.Shop)Run(lobby.RefreshShopAsync);
            },"mobile-nav-item grow");
            button.EnableInClassList("mobile-nav-selected",selected);
        }

        private static string LobbyTitle(LobbyScreen screen)
        {
            switch(screen){case LobbyScreen.JoinCode:return "방 코드로 입장";case LobbyScreen.Browse:return "공개 방";case LobbyScreen.CreateMode:case LobbyScreen.CreateRules:case LobbyScreen.CreateDetails:return "방 만들기";case LobbyScreen.Friends:return "친구";case LobbyScreen.Shop:return "상점";case LobbyScreen.Customize:return "캐릭터 꾸미기";case LobbyScreen.Account:return "내 계정";case LobbyScreen.Options:return "옵션";case LobbyScreen.Time:return "시간 설정";case LobbyScreen.TopicSelection:return "주제 선택";case LobbyScreen.Topics:return "나만의 주제";default:return "함께 플레이";}
        }

        private void UpdateLobbyAvatar(int color,int decoration)
        {
            avatarStage.Clear();var avatar=new AvatarElement(color,decoration);avatar.AddToClassList("lobby-avatar");avatarStage.Add(avatar);
        }

        private void RevealAvatarPreview()
        {
            if(IsMobile&&avatarStage?.panel!=null)avatarStage.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(avatarStage);
        }

        private void OnProfileChanged()
        {
            var profile=lobby.Profile;
            if(profile!=null)
            {
                nickname=profile.DisplayName;avatarColor=profile.AvatarColor;accessory=profile.Accessory;
                PlayerPrefs.SetString("DrawLiar.Name",nickname);PlayerPrefs.SetInt("DrawLiar.Color",avatarColor);PlayerPrefs.SetInt("DrawLiar.Equipment",accessory);
                PlayerPrefs.Save();
            }
            if(!inRoom&&content!=null)Home();
        }

        private void LoginForm(VisualElement panel)
        {
            if(!IsMobile)Text(panel,"가입 없이 바로 플레이할 수 있어요.","subtitle");
            var guest=Button(panel,"게스트로 시작",()=>Run(async()=>{await lobby.GuestLoginAsync();Navigate(LobbyScreen.Main);}),"primary next-button guest-login");
            guest.SetEnabled(DrawGuestCredentialStore.IsSupported&&!lobby.IsBusy);
            var google=Button(panel,"Google로 계속하기",()=>Run(async()=>{await lobby.GoogleLoginAsync();Navigate(LobbyScreen.Main);}),"secondary google-login");
            google.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy);
            Text(panel,"게스트는 이 기기에서 이어서 플레이해요. Google을 연동하면 PC와 모바일에서 같은 계정으로 즐길 수 있어요.","rules");
            var cancel=Button(panel,"Google 인증 취소",lobby.CancelGoogleLogin,"secondary google-cancel");cancel.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None;
        }

        private void AccountForm(VisualElement panel)
        {
            if(lobby.Profile==null)return;
            Text(panel,lobby.Profile.DisplayName,"section-title");
            Text(panel,lobby.Profile.HasGoogleAccount?"Google 연동 계정":lobby.Profile.IsGuest?"게스트 계정":"기존 계정","muted");
            Text(panel,"계정 ID: "+lobby.Profile.AccountId,"rules");
            Button(panel,"계정 ID 복사",()=>{GUIUtility.systemCopyBuffer=lobby.Profile.AccountId;Toast("계정 ID를 복사했습니다.");},"secondary");
            if(!lobby.Profile.HasGoogleAccount)
            {
                Text(panel,"Google을 연동하면 현재 캐릭터와 재화를 다른 기기에서도 이어서 사용할 수 있어요.","rules");
                var link=Button(panel,"Google 계정 연동",()=>Run(()=>lobby.GoogleLoginAsync(true)),"primary google-link");link.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy);
            }
            var cancel=Button(panel,"Google 인증 취소",lobby.CancelGoogleLogin,"secondary google-cancel");cancel.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None;
            Button(panel,"로그아웃",()=>Run(async()=>{await lobby.LogoutAsync();Navigate(LobbyScreen.Login);}),"danger");
        }

        private void FriendsForm(VisualElement panel)
        {
            var account=Field(panel,"친구 계정 ID","");account.maxLength=64;
            var actions=Box(panel,"row");
            Button(actions,"친구 요청",()=>Run(()=>lobby.RequestFriendAsync(account.value)),"primary");
            Button(actions,"새로고침",()=>Run(lobby.RefreshFriendsAsync),"secondary");
            friendList=new ScrollView();friendList.AddToClassList("room-list");panel.Add(friendList);RefreshFriends();
        }

        private void RefreshFriends()
        {
            if(friendList?.panel==null)return;
            friendList.Clear();
            if(lobby.Friends.Incoming.Length>0)Text(friendList,"받은 요청","section-title");
            foreach(var friend in lobby.Friends.Incoming)
            {
                var row=Box(friendList,"room-entry");Text(row,friend.DisplayName,"player-name");
                Button(row,"수락",()=>Run(()=>lobby.RespondFriendAsync(friend.AccountId,true)),"primary");
                Button(row,"거절",()=>Run(()=>lobby.RespondFriendAsync(friend.AccountId,false)),"secondary");
            }
            Text(friendList,"친구 목록","section-title");
            if(lobby.Friends.Friends.Length==0)Text(friendList,"계정 ID로 친구를 추가해 보세요.","muted");
            foreach(var friend in lobby.Friends.Friends)
            {
                var row=Box(friendList,"room-entry");row.Add(new AvatarElement(friend.AvatarColor,friend.Accessory));Text(row,friend.DisplayName,"player-name");
                Button(row,"삭제",()=>Run(()=>lobby.RemoveFriendAsync(friend.AccountId)),"secondary");
            }
            if(lobby.Friends.Outgoing.Length>0)Text(friendList,"보낸 요청","section-title");
            foreach(var friend in lobby.Friends.Outgoing)Text(friendList,friend.DisplayName+" · 수락 대기","muted");
        }

        private void ShopForm(VisualElement panel)
        {
            var preview=Box(panel,"shop-preview-heading");
            if(!IsMobile){_shopPreviewStatus=Text(preview,"현재 장착한 모습","shop-preview-status");_shopPreviewStatus.name="shop-preview-status";}
            Text(preview,"구매 전에도 입혀볼 수 있어요. 구매한 아이템은 꾸미기에서 장착하세요.","rules");
            var controls=Box(panel,"row shop-controls");
            _shopPreviewReset=Button(controls,"원래 모습",()=>{_shopPreviewProductId="";UpdateShopPreview();RevealAvatarPreview();},"secondary grow",DrawSound.UiCancel);_shopPreviewReset.name="shop-preview-reset";
            Button(controls,"새로고침",()=>Run(lobby.RefreshShopAsync),"secondary grow mobile-last");
            shopList=new ScrollView(){name="shop-list"};shopList.AddToClassList("shop-list");
            if(IsMobile)shopList.contentContainer.AddToClassList("mobile-shop-grid");
            panel.Add(shopList);RefreshShop();
        }

        private void RefreshShop()
        {
            if(shopList?.panel==null)return;
            shopList.Clear();if(!IsMobile)Text(shopList,$"보유 코인: {lobby.Profile?.Coins??0}","section-title");
            int ownedMask=lobby.Profile?.OwnedAccessories?.Aggregate(0,(mask,item)=>mask|item)??0;
            foreach(var product in lobby.Shop.Products)
            {
                bool owned=(ownedMask&product.Accessory)==product.Accessory;
                var row=Box(shopList,"shop-entry");row.userData=product.Id;
                if(IsMobile)row.AddToClassList("mobile-shop-entry");
                var summary=Box(row,"row shop-product");
                if(IsMobile)summary.AddToClassList("mobile-shop-product");
                var image=new AvatarElement(avatarColor,product.Accessory){tooltip=product.Name};image.AddToClassList("shop-item-image");summary.Add(image);
                if(IsMobile)image.AddToClassList("mobile-shop-image");
                var info=Box(summary,"grow shop-item-info");Text(info,product.Name,"shop-item-name");Text(info,product.Price+" 코인","muted");
                if(IsMobile)info.AddToClassList("mobile-shop-info");
                var actions=Box(row,"row shop-actions");
                if(IsMobile)actions.AddToClassList("mobile-shop-actions");
                var tryOn=Button(actions,"입혀보기",()=>{_shopPreviewProductId=product.Id;UpdateShopPreview();RevealAvatarPreview();},"secondary grow shop-try-on");tryOn.name="shop-preview-"+product.Id;
                var buy=Button(actions,owned?"보유 중":"구매",()=>Run(()=>lobby.PurchaseAsync(product.Id)),"primary grow mobile-last",DrawSound.UiConfirm);buy.name="shop-buy-"+product.Id;
                buy.SetEnabled(!owned&&(lobby.Profile?.Coins??0)>=product.Price);
            }
            if(lobby.Shop.Products.Length==0)Text(shopList,"판매 중인 상품이 없습니다.","muted");
            UpdateShopPreview();
        }

        private void UpdateShopPreview()
        {
            var product=lobby.Shop.Products.FirstOrDefault(item=>item.Id==_shopPreviewProductId);
            if(product==null)_shopPreviewProductId="";
            if(avatarStage!=null)UpdateLobbyAvatar(avatarColor,accessory|(product?.Accessory??0));
            if(_shopPreviewStatus!=null)_shopPreviewStatus.text=product==null?"현재 장착한 모습":product.Name+" · 입혀보기 중";
            _shopPreviewReset?.SetEnabled(product!=null);
            shopList?.Query<VisualElement>(className:"shop-entry").ForEach(row=>
            {
                bool selected=product!=null&&(string)row.userData==product.Id;
                row.EnableInClassList("shop-selected",selected);
                row.Q<Button>(className:"shop-try-on").text=selected?"입혀보기 중":"입혀보기";
            });
        }

        private static void BrandLogo(VisualElement parent,string styleClass)
        {
            var logo=new Image{image=Resources.Load<Texture2D>("DrawLiar/Brand/LiarsCanvasLogo"),scaleMode=ScaleMode.ScaleToFit,tooltip="Liar’s Canvas",pickingMode=PickingMode.Ignore};
            Classes(logo,styleClass);parent.Add(logo);
        }

        private void Navigate(LobbyScreen screen)
        {
            if(screen!=LobbyScreen.Shop||lobbyScreen!=LobbyScreen.Shop)_shopPreviewProductId="";
            lobbyScreen=screen;Home();
        }
        private void Back()
        {
            var destination=LobbyScreen.Main;
            if(!IsMobile&&(lobbyScreen==LobbyScreen.JoinCode||lobbyScreen==LobbyScreen.Browse))destination=LobbyScreen.Join;
            if(lobbyScreen==LobbyScreen.CreateRules)destination=LobbyScreen.CreateMode;
            if(lobbyScreen==LobbyScreen.CreateDetails||lobbyScreen==LobbyScreen.Time)destination=LobbyScreen.CreateRules;
            if(lobbyScreen==LobbyScreen.TopicSelection)destination=LobbyScreen.CreateDetails;
            if(lobbyScreen==LobbyScreen.Topics)destination=LobbyScreen.TopicSelection;
            Navigate(destination);
        }
        private static void CreateHeading(VisualElement panel,string title,int step)
        {
            Text(panel,$"방 만들기   {step} / 3","step-caption");Text(panel,title,"screen-title create-title");
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
            Text(panel,$"{GameRules.MIN_START_PLAYERS}명부터 방장이 시작할 수 있어요. 최대 {GameRules.MAX_PLAYERS}명까지 참가해요.","rules");
            Int(panel,"라이어 수",draft.LiarCount,1,GameRules.MAX_PLAYERS-1,v=>draft.LiarCount=v);
            Text(panel,"참가 인원이 적으면 라이어 수를 자동으로 줄여요.","rules");
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
            var create=Button(panel,"방 만들고 입장",()=>Run(async()=> {draft.Validate();await lobby.HostAsync(draft.Copy());}),"primary next-button");
            create.SetEnabled(draft.Topics.Length>0);
            if(draft.Topics.Length==0)Text(panel,"주제를 하나 이상 선택하세요.","rules");
        }

        private void JoinForm(VisualElement panel)
        {
            if(IsMobile)
            {
                Text(panel,"친구가 알려준\n6자리 코드를 입력하세요","mobile-intro");Text(panel,"영문과 숫자 6자리 · 대소문자 구분 없음","muted mobile-small");
                var card=Box(panel,"mobile-card");var input=Field(card,"방 코드","");input.maxLength=16;input.AddToClassList("mobile-code-input");input.textEdition.placeholder="ABC 234";
                input.textEdition.keyboardType=TouchScreenKeyboardType.ASCIICapable;input.textEdition.autoCorrection=false;
                input.RegisterValueChangedCallback(e=>input.SetValueWithoutNotify(NormalizeRoomCodeInput(e.newValue)));
                Button(card,"붙여넣기",()=>{input.value=NormalizeRoomCodeInput(GUIUtility.systemCopyBuffer);},"secondary");
                Text(card,"공백이나 하이픈 없이 입력해도 돼요.","muted mobile-small");
                void Join(bool watch)=>Run(()=>lobby.JoinCodeAsync(RequireMobileRoomCode(input.value),watch));
                Button(panel,"참가하기",()=>Join(false),"primary");Button(panel,"관전하기",()=>Join(true),"secondary");
                Text(panel,"게임이 진행 중이면 관전으로 입장합니다.","muted mobile-small");return;
            }
            Text(panel,"친구에게 받은 코드를 입력하세요.","subtitle");
            var code=Field(panel,"방 코드","");code.maxLength=64;
            var spectator=new Toggle("관전으로 참가");spectator.AddToClassList("field");panel.Add(spectator);
            Button(panel,"입장하기",()=>Run(()=>lobby.JoinCodeAsync(code.value,spectator.value)),"primary next-button");
        }

        private void BrowseForm(VisualElement panel)
        {
            if(IsMobile)Text(panel,"지금 같이 그릴 사람들","mobile-page-title");
            var search=Field(panel,"방 이름 검색",_roomSearch);
            var controls=IsMobile?Box(panel,"mobile-list-actions row"):panel;
            Button(controls,"검색 / 새로고침",()=>SearchRooms(search.value),"secondary grow");
            if(IsMobile)Button(controls,"방 만들기",()=>Navigate(LobbyScreen.CreateMode),"primary grow mobile-last");
            publicRoomList=new ScrollView();publicRoomList.AddToClassList("room-list");panel.Add(publicRoomList);RefreshPublicRooms();
        }
        private void RefreshPublicRooms()
        {
            if(publicRoomList?.panel==null)return;
            var list=publicRoomList;var offset=list.scrollOffset;
            publicRoomList.Clear();
            if(lobby.PublicRooms.Count==0)Text(publicRoomList,"공개방이 없습니다. 새 방을 만들어 보세요.","muted");
            foreach(var room in lobby.PublicRooms)
            {
                var entry=Box(publicRoomList,IsMobile?"room-entry mobile-room-item":"room-entry");var info=Box(entry,"grow");
                if(IsMobile)
                {
                    var heading=Box(info,"mobile-room-top row");Text(heading,room.Name,"player-name grow");Text(heading,room.IsInProgress?"진행 중":"대기 중","mobile-status-pill");
                    Text(info,$"{room.Players}/{room.MaxPlayers}명 · 관전 {room.Spectators}명","muted mobile-small mobile-room-meta");
                }
                else{Text(info,room.Name,"player-name");Text(info,$"{room.Players} / {room.MaxPlayers} 명 · 관전 {room.Spectators}명 · "+(room.IsInProgress?"진행 중":"대기 중"),"muted mobile-small");}
                var actions=IsMobile?Box(entry,"mobile-room-entry-actions mobile-room-action row"):entry;
                if(IsMobile)Text(actions,DisplayRoomCode(room.Code),"mobile-card-title grow");
                var enter=Button(actions,room.IsInProgress&&IsMobile?"입장":"참가",()=>Run(()=>lobby.JoinLobbyAsync(room.Id)),"primary");enter.SetEnabled(room.Players<room.MaxPlayers);
                Button(actions,"관전",()=>Run(()=>lobby.JoinLobbyAsync(room.Id,true)),"secondary");
            }
            list.schedule.Execute(()=>{if(list==publicRoomList&&list.panel!=null)list.scrollOffset=offset;}).StartingIn(20);
        }
        private void EnterBrowse(){Navigate(LobbyScreen.Browse);SearchRooms(_roomSearch);}
        private void SearchRooms(string query)
        {
            if(runningAction||lobby.IsBusy)return;
            bool changed=_roomSearch!=(query??"");_roomSearch=query??"";_nextRoomRefresh=Time.unscaledTime+5;
            if(changed&&publicRoomList!=null)publicRoomList.scrollOffset=Vector2.zero;
            Run(()=>lobby.SearchRoomsAsync(_roomSearch));
        }
        private void RefreshRoomsInBackground()
        {
            if(!_backgroundRoomRefresh||_roomRefreshRunning||inRoom||lobbyScreen!=LobbyScreen.Browse||!lobby.IsAuthenticated
                ||lobby.IsBusy||runningAction||Time.unscaledTime<_nextRoomRefresh)return;
            _=RefreshRoomsAsync();
        }
        private async Task RefreshRoomsAsync()
        {
            _nextRoomRefresh=Time.unscaledTime+5;_roomRefreshRunning=true;
            try{await lobby.SearchRoomsAsync(_roomSearch);}
            catch(Exception){}
            finally{_roomRefreshRunning=false;_nextRoomRefresh=Time.unscaledTime+5;}
        }
        private async Task Leave()
        {
            CloseModal();await lobby.LeaveAsync();
        }

        private void Room()
        {
            inRoom=true;content.Clear();publicRoomList=null;serviceNotice=null;playerKey="";actionKey="";contextKey="";
            playerCards.Clear();playerStatuses.Clear();selectedPlayerId=-1;secretHidden=false;voteSubmitted=false;
            root.RemoveFromClassList("at-home");root.AddToClassList("in-game");content.AddToClassList("room-layout");lobbyScreen=LobbyScreen.Main;
            if(IsMobile){MobileRoom();HideMobileScrollers();return;}
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
                if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){if(!string.IsNullOrWhiteSpace(chatInput.value))DrawAudio.Instance?.Play(DrawSound.UiConfirm);SendChat();e.StopPropagation();}
                else if(e.keyCode==KeyCode.Escape){CloseChat();e.StopPropagation();}
            });
            Button(chatbar,"보내기",SendChat,"secondary",DrawSound.UiConfirm);
            var center=Box(workspace,"center");var frame=Box(center,"canvas-frame");surface=new DrawingSurface(network);frame.Add(surface);
            frame.RegisterCallback<GeometryChangedEvent>(_=>
            {
                var width=Mathf.Max(0,Mathf.Min(frame.contentRect.width,frame.contentRect.height*1.5f));
                surface.style.width=width;surface.style.height=width/1.5f;
            });
            var context=Box(workspace,"context-panel");CreateDrawingTools(context);
            contextInfo=Box(context,"context-info");Box(context,"grow");phaseActions=Box(context,"phase-actions");
            playerStrip=Box(content,"players players-strip");
            network.ReplayCanvas();foreach(var stroke in pendingStrokes)surface.Apply(stroke);pendingStrokes.Clear();
        }

        private void CreateDrawingTools(VisualElement context)
        {
            drawingTools=Box(context,"tools");
            var toolRow=Box(drawingTools,"tool-row");var brush=Button(toolRow,"펜",()=>surface.Eraser=false,"secondary");var eraser=Button(toolRow,"지우개",()=>surface.Eraser=true,"secondary");
            if(IsMobile)Button(toolRow,"굵기",BrushOptions,"secondary mobile-last");
            VisualElement paletteParent=drawingTools;
            if(IsMobile){var scroll=new ScrollView(ScrollViewMode.Horizontal);scroll.AddToClassList("mobile-palette-scroll");drawingTools.Add(scroll);paletteParent=scroll;}
            var palette=Box(paletteParent,"palette");
            var colors=new[]{new Color32(40,43,39,255),new Color32(225,127,103,255),new Color32(228,182,107,255),DrawingSurface.PaperColor,
                new Color32(83,110,130,255),new Color32(166,171,159,255),new Color32(35,124,98,255),new Color32(157,147,219,255)};
            var swatches=new List<Button>();
            foreach(var color in colors)
            {
                Button swatch=null;swatch=Button(palette,"",()=>{surface.BrushColor=color;surface.Eraser=false;foreach(var item in swatches)item.RemoveFromClassList("selected");swatch.AddToClassList("selected");},"swatch");
                if(IsMobile){swatch.style.backgroundColor=Color.clear;var dot=Box(swatch,"mobile-swatch-dot");dot.pickingMode=PickingMode.Ignore;dot.style.backgroundColor=(Color)color;}
                else swatch.style.backgroundColor=(Color)color;
                swatch.tooltip="색상 "+(swatches.Count+1);swatch.EnableInClassList("palette-row-end",swatches.Count%4==3);swatches.Add(swatch);
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
        }

        private void BrushOptions()
        {
            var modal=Modal("붓 굵기");
            foreach(var width in new[]{5,11,24})
            {
                int size=width;var button=Button(modal,(size==5?"가는 선":size==11?"보통 선":"굵은 선"),()=>{surface.BrushSize=size/1200f;CloseModal();},"secondary");
                button.EnableInClassList("tool-selected",Mathf.Approximately(surface.BrushSize,size/1200f));
            }
            Button(modal,"닫기",CloseModal,"secondary");
        }

        private void MobileRoom()
        {
            _mobileLayout.Refresh();
            _mobileRoomStack=Box(content,"mobile-room");
            var hud=Box(_mobileRoomStack,"room-hud");phaseBanner=Box(hud,"phase-bar");phaseTitle=Text(phaseBanner,"대기실","phase-title");phaseDetail=Text(phaseBanner,"","phase-detail");
            timer=Text(hud,"","timer");Button(hud,"메뉴",RoomMenu,"secondary mobile-back");
            var metadata=Box(_mobileRoomStack,"mobile-round-card row");var round=Box(metadata,"grow");
            _mobileRoundInfo=Text(round,"","mobile-card-title");_mobileRoundHint=Text(round,"","muted mobile-small");
            roomBadge=Text(round,"","mobile-hidden");roundCaption=Text(round,"","mobile-hidden");
            _mobileRoomCode=Button(metadata,DisplayRoomCode(lobby.RoomCode),()=>{GUIUtility.systemCopyBuffer=lobby.RoomCode;Toast("방 코드를 복사했습니다.");},"secondary mobile-code-copy");
            _mobileSecret=Box(_mobileRoomStack,"secret-bar mobile-secret");topic=Text(_mobileSecret,"","topic");
            var identity=Box(_mobileSecret,"mobile-role-line row");role=Text(identity,"","role");word=Text(identity,"","word grow");secretToggle=Button(identity,"숨기기",()=>{secretHidden=!secretHidden;RefreshSecret(network.State);},"secret-toggle");
            _mobileWorkspace=Box(_mobileRoomStack,"mobile-workspace");_mobileCanvas=Box(_mobileWorkspace,"canvas-frame");
            surface=new DrawingSurface(network);_mobileCanvas.Add(surface);_mobileCanvas.RegisterCallback<GeometryChangedEvent>(_=>SizeMobileCanvas());
            _mobileRoomScroll=new ScrollView(ScrollViewMode.Vertical);_mobileRoomScroll.AddToClassList("mobile-controls-scroll");_mobileWorkspace.Add(_mobileRoomScroll);
            _mobileControls=Box(_mobileRoomScroll,"mobile-controls");CreateDrawingTools(_mobileControls);contextInfo=Box(_mobileControls,"context-info");
            _mobileRoster=Box(_mobileControls,"mobile-roster");_mobileRosterTitle=Text(_mobileRoster,"참가자 · 좌우로 넘겨 보기","muted mobile-small");
            _mobileRosterScroll=new ScrollView(ScrollViewMode.Horizontal);_mobileRosterScroll.AddToClassList("mobile-roster-scroll");_mobileRoster.Add(_mobileRosterScroll);playerStrip=Box(_mobileRosterScroll,"players players-strip");
            _mobileActions=Box(_mobileWorkspace,"mobile-bottom-actions");phaseActions=Box(_mobileActions,"phase-actions");chatOpen=Button(_mobileActions,"채팅 열기",OpenChat,"secondary chat-open");
            _mobileChatSheet=Box(content,"overlay mobile-chat-layer");_mobileChatSheet.style.display=DisplayStyle.None;
            var sheet=Box(_mobileChatSheet,"modal mobile-chat-sheet");var chatHeading=Box(sheet,"row mobile-page-heading");Text(chatHeading,"채팅","title grow");Button(chatHeading,"닫기",CloseChat,"secondary mobile-back",DrawSound.UiCancel);
            chatHistory=new ScrollView(ScrollViewMode.Vertical);chatHistory.AddToClassList("chat-history");sheet.Add(chatHistory);
            chatbar=Box(sheet,"chatbar");chatbar.style.display=DisplayStyle.None;chatInput=new TextField{maxLength=160};chatInput.textEdition.placeholder="채팅 입력";chatInput.AddToClassList("chat-input");chatbar.Add(chatInput);
            chatInput.RegisterCallback<KeyDownEvent>(e=>{if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){if(!string.IsNullOrWhiteSpace(chatInput.value))DrawAudio.Instance?.Play(DrawSound.UiConfirm);SendChat();e.StopPropagation();}});
            Button(chatbar,"보내기",SendChat,"primary",DrawSound.UiConfirm);
            OnMobileLayoutChanged();
            network.ReplayCanvas();foreach(var stroke in pendingStrokes)surface.Apply(stroke);pendingStrokes.Clear();
        }

        private void OnMobileLayoutChanged()
        {
            if(!inRoom||_mobileWorkspace==null||!IsMobile)return;
            if(_mobileLayout.IsPortrait||network.State?.Phase==GamePhase.Lobby)
            {
                if(_mobileSecret.parent!=_mobileRoomStack)_mobileRoomStack.Insert(2,_mobileSecret);
                if(_mobileActions.parent!=_mobileWorkspace)_mobileWorkspace.Add(_mobileActions);
            }
            else
            {
                if(_mobileSecret.parent!=_mobileControls)_mobileControls.Insert(0,_mobileSecret);
                if(_mobileActions.parent!=_mobileControls)_mobileControls.Add(_mobileActions);
            }
            SizeMobileCanvas();
        }

        private void SizeMobileCanvas()
        {
            if(_mobileCanvas==null||surface==null)return;
            if(_mobileLayout.IsPortrait&&_mobileCanvas.contentRect.width>0)
            {
                float height=_mobileCanvas.contentRect.width/1.5f;
                if(root.ClassListContains("keyboard-open"))height=Mathf.Min(height,_mobileLayout.AvailableSize.y*.2f);
                _mobileCanvas.style.height=height;
            }
            else if(!_mobileLayout.IsPortrait)_mobileCanvas.style.height=StyleKeyword.Null;
            float width=Mathf.Max(0,Mathf.Min(_mobileCanvas.contentRect.width,_mobileCanvas.contentRect.height*1.5f));
            if(width>0){surface.style.width=width;surface.style.height=width/1.5f;}
        }

        private static string NormalizeRoomCodeInput(string value)
        {
            return string.Concat((value??"").Where(c=>!char.IsWhiteSpace(c)&&c!='-')).ToUpperInvariant();
        }
        private static string RequireMobileRoomCode(string value)
        {
            string code=NormalizeRoomCodeInput(value);
            if(code.Length!=6||code.Any(c=>!(c>='A'&&c<='Z')&&!(c>='0'&&c<='9')))throw new ArgumentException("영문과 숫자로 된 6자리 방 코드를 입력하세요.");
            return LobbyServiceBridge.NormalizeCode(code);
        }
        private static string DisplayRoomCode(string value)=>value!=null&&value.Length==6?value.Substring(0,3)+" "+value.Substring(3):value??"";

        private void OpenChat()
        {
            if(!inRoom||chatInput==null||overlay!=null)return;
            _chatTransitionVersion++;
            if(IsMobile&&_mobileChatSheet!=null)
            {
                _mobileChatSheet.style.display=DisplayStyle.Flex;_mobileChatSheet.pickingMode=PickingMode.Position;
                DrawUIMotion.Enter(_mobileChatSheet,160,0);
                DrawUIMotion.ShowSheet(_mobileChatSheet.Q<VisualElement>(className:"mobile-chat-sheet"));
            }
            chatbar.style.display=DisplayStyle.Flex;chatOpen.style.display=DisplayStyle.None;chatInput.Focus();
        }
        private void CloseChat()
        {
            if(chatInput==null)return;
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
            Button(modal,"방 코드 복사",()=>{GUIUtility.systemCopyBuffer=lobby.RoomCode;Toast("방 코드를 복사했습니다.");},"secondary");
            if(IsMobile&&state.Phase!=GamePhase.Lobby&&!string.IsNullOrEmpty(state.Word))
                Button(modal,secretHidden?"제시어 보기":"제시어 숨기기",()=>{secretHidden=!secretHidden;RefreshSecret(network.State);CloseModal();},"secondary");
            Button(modal,"옵션",Options,"secondary");
            Button(modal,"나가기",()=>Run(Leave),"danger",DrawSound.UiCancel);Button(modal,"닫기",CloseModal,"secondary",DrawSound.UiCancel);
        }
        private void RefreshSecret(RoomSnapshot state)
        {
            if(state==null)return;
            topic.text=state.Phase==GamePhase.Lobby?"게임 대기":state.Topic;
            role.text=state.Phase==GamePhase.Lobby?"":state.LocalIsSpectator?"관전":state.LocalIsLiar?"라이어":"시민";
            word.text=state.Phase==GamePhase.Lobby?"—":string.IsNullOrEmpty(state.Word)?"비공개":secretHidden?"•••":state.Word;
            word.EnableInClassList("word-hidden",string.IsNullOrEmpty(state.Word)||secretHidden);
            secretToggle.style.display=state.Phase==GamePhase.Lobby||string.IsNullOrEmpty(state.Word)?DisplayStyle.None:DisplayStyle.Flex;
            secretToggle.text=secretHidden?"제시어 보기":"숨기기";
            if(IsMobile)
            {
                topic.text="주제 · "+(state.Phase==GamePhase.Lobby?"게임 대기":state.Topic);
                word.text=state.Phase==GamePhase.Lobby?"게임이 시작되면 단어가 공개돼요":"제시어 "+(string.IsNullOrEmpty(state.Word)?"비공개":secretHidden?"•••":state.Word);
            }
        }
        private void RefreshState(RoomSnapshot state)
        {
            if(state==null){if(inRoom){CloseModal();Home();}return;}
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
            if(IsMobile&&state.Phase==GamePhase.Drawing)phaseTitle.text="그림 차례";
            if(IsMobile&&_mobileRoundInfo!=null)
            {
                bool waiting=state.Phase==GamePhase.Lobby;
                _mobileRoomStack.EnableInClassList("mobile-waiting",waiting);
                _mobileCanvas.style.display=waiting?DisplayStyle.None:DisplayStyle.Flex;
                _mobileSecret.style.display=waiting?DisplayStyle.None:DisplayStyle.Flex;
                var rosterMode=waiting?ScrollViewMode.Vertical:ScrollViewMode.Horizontal;
                if(_mobileRosterScroll.mode!=rosterMode)_mobileRosterScroll.mode=rosterMode;
                _mobileRosterTitle.text=waiting?"참가자":"참가자 · 좌우로 넘겨 보기";
                _mobileRoundInfo.text=state.Phase==GamePhase.Lobby?$"{state.Players.Count(p=>!p.IsSpectator&&p.IsConnected)} / {state.Settings.MaxPlayers}명 · 게임 대기":$"{state.Round}라운드 · "+(state.Phase==GamePhase.Drawing?(network.CanDraw?"내 차례":"그림 관전"):PhaseName(state.Phase));
                _mobileRoundHint.text=state.Phase==GamePhase.Lobby?$"라이어 {state.Settings.LiarCount}명 · "+(state.Settings.Victory==VictoryMode.RoundCount?$"{state.Settings.RoundCount}라운드":$"목표 {state.Settings.TargetScore}점"):MobilePhaseHint(state);
                _mobileRoomCode.text=DisplayRoomCode(lobby.RoomCode);
            }
            var artist=state.Players.FirstOrDefault(p=>p.Id==state.ArtistId);
            phaseDetail.text=state.Phase==GamePhase.Drawing?(network.CanDraw?"—  내 차례":"—  "+artist?.Name):"";
            phaseDetail.style.display=IsMobile||string.IsNullOrEmpty(phaseDetail.text)?DisplayStyle.None:DisplayStyle.Flex;
            bool toolsWereVisible=drawingTools.resolvedStyle.display!=DisplayStyle.None;
            drawingTools.style.display=network.CanDraw?DisplayStyle.Flex:DisplayStyle.None;drawingTools.SetEnabled(network.CanDraw);
            if(network.CanDraw&&!toolsWereVisible)Enter(drawingTools,140,4);
            var key=string.Join("|",state.Players.Select(p=>$"{p.Id},{p.Name},{p.Score},{p.IsLiar},{p.IsSpectator},{p.IsConnected},{p.AvatarColor},{p.Accessory},{p.HasVoted}"))+state.ArtistId+state.Phase;
            if(key!=playerKey){playerKey=key;Players(state);}
            var local=state.Players.FirstOrDefault(p=>p.Id==state.LocalPlayerId);
            var actions=$"{state.Phase}/{state.ArtistId}/{state.CanStart}/{state.IsHost}/{local?.HasVoted}/{local?.HasGuessed}/{selectedPlayerId}/{voteSubmitted}";
            if(actions!=actionKey){actionKey=actions;PhaseActions(state,local);if(IsMobile)Enter(phaseActions,160,4);}
            RefreshContext(state);RefreshPlayerStates(state);
            if(phaseChanged)
            {
                previousPhase=state.Phase;Enter(phaseBanner,180,0);
                if(IsMobile)OnMobileLayoutChanged();
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
                        DrawAudio.Instance?.Play(DrawSound.UiClick);selectedPlayerId=player.Id;RefreshState(current);
                    }
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
                card.EnableInClassList("active-player",state.Phase==GamePhase.Drawing&&player.Id==state.ArtistId);
                card.EnableInClassList("selected-player",state.Phase==GamePhase.Voting&&player.Id==selectedPlayerId);
                card.EnableInClassList("vote-selectable",CanSelectVote(state,player));
                var label=playerStatuses[player.Id];
                label.text=!player.IsConnected?"연결 끊김":state.Phase==GamePhase.Voting?(player.HasVoted?"투표 완료":player.Id==selectedPlayerId?"선택됨":""):
                    player.IsLiar?"라이어":state.Phase==GamePhase.Drawing?(player.Id==state.ArtistId?"그리는 중":""):
                    state.Phase==GamePhase.Lobby||state.Phase==GamePhase.RoundResults||state.Phase==GamePhase.MatchResults?player.Score+"점":"";
            }
        }
        private void RefreshContext(RoomSnapshot state)
        {
            var target=state.Players.FirstOrDefault(p=>p.Id==selectedPlayerId);
            string key=state.Phase+"/"+selectedPlayerId;
            if(state.Phase==GamePhase.Lobby)key+="/"+state.Players.Length+"/"+state.CanStart;
            if(key!=contextKey)
            {
                contextKey=key;contextInfo.Clear();voteProgress=null;
                if(state.Phase==GamePhase.Voting)
                {
                    if(!IsMobile)
                    {
                        Text(contextInfo,target==null?"투표할 플레이어를 선택하세요":"선택한 플레이어","muted");
                        if(target!=null){var avatar=new AvatarElement(target.AvatarColor,target.Accessory);avatar.AddToClassList("vote-avatar");contextInfo.Add(avatar);Text(contextInfo,target.Name,"context-name");}
                    }
                    voteProgress=Text(contextInfo,"","muted mobile-small");
                }
                else if(state.Phase==GamePhase.Discussion||state.Phase==GamePhase.Rebuttal)
                {
                    Text(contextInfo,"채팅으로 의견을 나눠 보세요.","muted");
                }
                else if(state.Phase==GamePhase.Lobby)
                {
                    string startHint=state.CanStart?"준비됐어요. 방장이 게임을 시작할 수 있어요.":$"참가자 {GameRules.MIN_START_PLAYERS}명부터 방장이 시작할 수 있어요.";
                    if(IsMobile)Text(contextInfo,startHint,"muted mobile-small");
                    else
                    {
                        Text(contextInfo,$"{state.Players.Count(p=>!p.IsSpectator&&p.IsConnected)} / {state.Settings.MaxPlayers}명","context-name");
                        Text(contextInfo,startHint,"muted");
                        Text(contextInfo,$"라이어 {state.Settings.LiarCount}명","muted");
                        Text(contextInfo,state.Settings.Victory==VictoryMode.RoundCount?$"{state.Settings.RoundCount}라운드":$"목표 {state.Settings.TargetScore}점","muted");
                    }
                }
            }
            if(voteProgress!=null)
            {
                int voted=state.Players.Count(p=>p.HasVoted&&!p.IsSpectator&&p.IsConnected), total=state.Players.Count(p=>!p.IsSpectator&&p.IsConnected);
                voteProgress.text=IsMobile?(target==null?"플레이어를 선택하세요":target.Name+" 선택")+$" · {voted}/{total}명 투표 완료":$"{voted} / {total}명 투표 완료";
            }
            if(IsMobile)contextInfo.style.display=contextInfo.childCount==0?DisplayStyle.None:DisplayStyle.Flex;
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
                    if(state.IsHost){var b=Button(phaseActions,"게임 시작",network.StartMatch,"primary",DrawSound.UiConfirm);b.SetEnabled(state.CanStart);}else Text(phaseActions,"방장 시작 대기","muted");
                    if(!IsMobile)Button(phaseActions,"방 코드 복사",()=>{GUIUtility.systemCopyBuffer=lobby.RoomCode;Toast("방 코드를 복사했습니다.");},"secondary");break;
                case GamePhase.Drawing:if(network.CanDraw)Button(phaseActions,"그리기 완료",network.EndTurn,"primary",DrawSound.UiConfirm);break;
                case GamePhase.Voting:
                    if(state.LocalIsSpectator){Text(phaseActions,"관전 중","muted");break;}
                    if(local!=null&&local.HasVoted){Text(phaseActions,"투표 완료","muted");break;}
                    var selected=state.Players.FirstOrDefault(p=>p.Id==selectedPlayerId);
                    var vote=Button(phaseActions,voteSubmitted?"제출 중":IsMobile&&selected!=null?selected.Name+"에게 투표":"투표하기",SubmitVote,"primary vote-submit",DrawSound.UiConfirm);vote.SetEnabled(selectedPlayerId>=0&&!voteSubmitted);
                    Text(phaseActions,"제출 후에는 바꿀 수 없습니다.","rules");break;
                case GamePhase.Guessing:
                    if(state.LocalIsLiar&&local!=null&&!local.HasGuessed)
                    {
                        var guess=new TextField(){maxLength=40};guess.textEdition.placeholder="제시어 입력";guess.AddToClassList("guess-input");phaseActions.Add(guess);
                        var submit=Button(phaseActions,"정답 제출",()=>{if(!string.IsNullOrWhiteSpace(guess.value))network.Guess(guess.value);},"primary",DrawSound.UiConfirm);
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

        private string MobilePhaseHint(RoomSnapshot state)
        {
            switch(state.Phase)
            {
                case GamePhase.Drawing:return network.CanDraw?"힌트를 그림으로 표현하세요":state.Players.FirstOrDefault(p=>p.Id==state.ArtistId)?.Name+" 님이 그리는 중";
                case GamePhase.Voting:return "의심되는 참가자를 선택하고 투표하세요";
                case GamePhase.Discussion:case GamePhase.Rebuttal:return "그림을 보고 채팅으로 의견을 나눠요";
                case GamePhase.Guessing:return "라이어가 제시어를 추측하는 중이에요";
                case GamePhase.RoundResults:case GamePhase.MatchResults:return "결과와 획득한 점수를 확인하세요";
                default:return "역할과 제시어를 확인하세요";
            }
        }

        private void RoleReveal(RoomSnapshot state)
        {
            var modal=Modal(state.LocalIsSpectator?"관전":state.LocalIsLiar?"라이어":"시민");
            var avatar=new AvatarElement(avatarColor,accessory);avatar.AddToClassList("avatar-preview");modal.Add(avatar);
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
            Text(modal,state.Summary,"rules");Button(modal,"닫기",CloseModal,"primary",DrawSound.UiCancel);
            HideMobileScrollers();
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
            Button(modal,"확인",CloseModal,"primary",DrawSound.UiConfirm);
        }
        private void Options()
        {
            var modal=Modal("옵션");OptionsForm(modal);Button(modal,"완료",CloseModal,"primary",DrawSound.UiConfirm);
        }
        private void OptionsForm(VisualElement modal)
        {
            var audio=DrawAudio.Instance;
            if(audio!=null)
            {
                var sound=Box(modal,"audio-options");Text(sound,"사운드","section-title");
                var mute=new Toggle("모든 소리 끄기"){value=audio.Muted};mute.name="audio-mute";mute.AddToClassList("field");
                mute.RegisterValueChangedCallback(e=>audio.SetMuted(e.newValue));sound.Add(mute);
                AudioVolume(sound,"전체 음량","audio-master",audio.MasterVolume,v=>audio.SetVolumes(v,audio.MusicVolume,audio.EffectsVolume));
                AudioVolume(sound,"배경 음악","audio-music",audio.MusicVolume,v=>audio.SetVolumes(audio.MasterVolume,v,audio.EffectsVolume));
                AudioVolume(sound,"효과음","audio-effects",audio.EffectsVolume,v=>audio.SetVolumes(audio.MasterVolume,audio.MusicVolume,v));
            }
            var motion=new Toggle("화면 움직임 줄이기"){value=reducedMotion};motion.AddToClassList("field");motion.RegisterValueChangedCallback(e=>{reducedMotion=e.newValue;root.EnableInClassList("reduce-motion",reducedMotion);DrawButtonScale.ResetAll(root);PlayerPrefs.SetInt("DrawLiar.ReduceMotion",reducedMotion?1:0);PlayerPrefs.Save();});modal.Add(motion);
        }
        private static void AudioVolume(VisualElement parent,string label,string name,float value,Action<float> changed)
        {
            var row=Box(parent,"audio-volume");var heading=Box(row,"row spread");Text(heading,label,"audio-label");
            var percentage=Text(heading,Mathf.RoundToInt(value*100)+"%","audio-percentage");
            var slider=new Slider(0,1){value=value,name=name};slider.AddToClassList("audio-slider");row.Add(slider);
            slider.RegisterValueChangedCallback(e=>{percentage.text=Mathf.RoundToInt(e.newValue*100)+"%";changed(e.newValue);});
            slider.RegisterCallback<PointerUpEvent>(_=>DrawAudio.Instance?.Play(DrawSound.UiClick));
        }
        private void ProfileForm(VisualElement panel)
        {
            var selectedColor=avatarColor;var selectedAccessory=accessory;
            int ownedMask=lobby.Profile?.OwnedAccessories?.Aggregate(0,(mask,item)=>mask|item)??0;
            var preview=avatarStage;
            var equipmentButtons=new Dictionary<AvatarAccessory,Button>();
            Button save=null, purchase=null;
            Label previewNotice=null;
            void UpdateAvatar()
            {
                preview.Clear();var a=new AvatarElement(selectedColor,selectedAccessory);a.AddToClassList("lobby-avatar");preview.Add(a);
                foreach(var item in equipmentButtons)
                {
                    bool equipped=(selectedAccessory & (int)item.Key)!=0;
                    item.Value.EnableInClassList("equipped",equipped);
                    bool owned=(ownedMask&(int)item.Key)==(int)item.Key;
                    item.Value.Q<Label>(className:"equipment-state").text=equipped?(owned?"장착 중":"입혀보기 중"):owned?"장착하기":"입혀보기";
                    item.Value.tooltip=(item.Key==AvatarAccessory.Beret?"화가 모자":"붓")+(equipped?" · 클릭하면 해제":owned?" · 클릭하면 장착":" · 구매 전 입혀보기");
                }
                bool canSave=(selectedAccessory&~ownedMask)==0;
                save?.SetEnabled(canSave);
                if(previewNotice!=null)previewNotice.style.display=canSave?DisplayStyle.None:DisplayStyle.Flex;
                if(purchase!=null)purchase.style.display=canSave?DisplayStyle.None:DisplayStyle.Flex;
            }
            UpdateAvatar();var nameField=Field(panel,"닉네임",nickname);nameField.maxLength=16;
            Text(panel,"몸 색상","section-title");var colors=Box(panel,"row avatar-colors");var swatches=new List<Button>();
            void SelectColor(int color){selectedColor=color;for(var i=0;i<swatches.Count;i++)swatches[i].EnableInClassList("selected",i==color);UpdateAvatar();}
            foreach(var color in Enumerable.Range(0,AvatarElement.Colors.Length)){var b=Button(colors,"",()=>SelectColor(color),"swatch");b.style.backgroundColor=AvatarElement.Colors[color];b.tooltip="몸 색상 "+(color+1);swatches.Add(b);}
            SelectColor(selectedColor);
            var heading=Box(panel,"row spread");Text(heading,"아이템","section-title");Text(heading,"구매 전 입혀보기 가능","muted");
            var equipment=Box(panel,"row equipment-options");
            void AddEquipment(AvatarAccessory item,string name)
            {
                var button=Button(equipment,"",()=>{selectedAccessory^=(int)item;UpdateAvatar();RevealAvatarPreview();},"equipment-option");
                button.name=item==AvatarAccessory.Beret?"beret-option":"brush-option";
                var icon=new AvatarElement(0,(int)item);icon.AddToClassList("equipment-preview");button.Add(icon);
                Text(button,name,"equipment-name");Text(button,"","equipment-state");equipmentButtons[item]=button;
            }
            AddEquipment(AvatarAccessory.Beret,"화가 모자");AddEquipment(AvatarAccessory.Brush,"붓");
            previewNotice=Text(panel,"미구매 아이템을 입혀보고 있어요. 구매하거나 벗긴 뒤 저장할 수 있어요.","rules");previewNotice.name="customize-preview-status";
            purchase=Button(panel,"상점에서 구매",()=>{Navigate(LobbyScreen.Shop);Run(lobby.RefreshShopAsync);},"secondary");
            var reset=Button(panel,"원래 모습",()=>{selectedAccessory=accessory;SelectColor(avatarColor);RevealAvatarPreview();},"secondary",DrawSound.UiCancel);reset.name="customize-reset";
            save=Button(panel,"이 모습으로 저장",()=>Run(async()=>
            {
                if((selectedAccessory&~ownedMask)!=0){Toast("미구매 아이템은 구매하거나 벗긴 뒤 저장해 주세요.");return;}
                await lobby.SaveProfileAsync(nameField.value,selectedColor,selectedAccessory);
                Navigate(LobbyScreen.Main);
            }),"primary next-button",DrawSound.UiConfirm);save.name="customize-save";UpdateAvatar();
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
            Button(panel,"주제 저장",()=>
            {
                try
                {
                    var builtin=JsonUtility.FromJson<GameData>(Resources.Load<TextAsset>("DrawLiar/GameData").text);
                    if(builtin.Topics.Any(topic=>topic.Name==GameRules.CleanText(nameField.value,40)))
                        throw new ArgumentException("기본 주제와 다른 이름을 입력하세요.");
                    GameDataStore.UpsertCustomTopic(nameField.value,words.value);
                    draft.Topics=(draft.Topics??Array.Empty<string>()).Append(GameRules.CleanText(nameField.value,40)).Distinct().ToArray();
                    Navigate(LobbyScreen.TopicSelection);Toast("새 주제를 저장했습니다.");
                }
                catch(Exception exception){DrawAudio.Instance?.Play(DrawSound.UiError);Toast(exception.Message);}
            },"primary");
            var list=new ScrollView();list.style.maxHeight=150;panel.Add(list);
            foreach(var customTopic in GameDataStore.LoadCustomTopics())
            {
                var row=Box(list,"room-entry");
                Button(row,customTopic.Name,()=>{nameField.value=customTopic.Name;words.value=string.Join("\n",customTopic.Words);},"secondary");
                Button(row,"삭제",()=>{GameDataStore.DeleteCustomTopic(customTopic.Name);draft.Topics=draft.Topics?.Where(name=>name!=customTopic.Name).ToArray();Home();},"danger");
            }
        }
        private void OnChat(ChatLine line)
        {
            if(chatHistory==null)return;
            var entry=Box(chatHistory,"chat-entry");Text(entry,line.Name,"chat-name");var message=Text(entry,line.Text,"chat-message");message.tooltip=line.Text;
            while(chatHistory.contentContainer.childCount>6)chatHistory.contentContainer.ElementAt(0).RemoveFromHierarchy();
            Enter(entry,140,2);chatHistory.schedule.Execute(()=>{if(entry.panel!=null)chatHistory?.ScrollTo(entry);}).StartingIn(20);
        }
        private void Tick()
        {
            var state=network.State;if(state==null||!inRoom)return;
            RefreshPlayerStates(state);RefreshContext(state);
        }
        private void RefreshServiceStatus()
        {
            root.Query<Button>(className:"guest-login").ForEach(button=>button.SetEnabled(DrawGuestCredentialStore.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-login").ForEach(button=>button.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-link").ForEach(button=>button.SetEnabled(GoogleAccountProvider.IsSupported&&!lobby.IsBusy));
            root.Query<Button>(className:"google-cancel").ForEach(button=>button.style.display=lobby.IsGoogleSigningIn?DisplayStyle.Flex:DisplayStyle.None);
            if(serviceNotice!=null)serviceNotice.text=lobby.Status;
            else if(!_roomRefreshRunning&&lastServiceStatus!=lobby.Status)Toast(lobby.Status);
            lastServiceStatus=lobby.Status;
            if(!lobby.IsBusy){RefreshPublicRooms();RefreshFriends();RefreshShop();}
        }
        private async void Run(Func<Task> action)
        {
            if(runningAction||lobby.IsBusy){Toast("연결을 처리하고 있어요. 잠시 기다려 주세요.");return;}
            runningAction=true;
            try{await action();}catch(Exception ex){DrawAudio.Instance?.Play(DrawSound.UiError);Toast(ex.Message);Debug.LogWarning(ex.Message);}
            finally{runningAction=false;}
        }
        private VisualElement Modal(string title)
        {
            if(IsMobile)CloseChat();
            CloseModal();overlay=Box(IsMobile?content:root,"overlay enter");var modal=Box(overlay,"modal");
            VisualElement body=modal;
            if(IsMobile){var scroll=new ScrollView(ScrollViewMode.Vertical);scroll.AddToClassList("mobile-modal-scroll");modal.Add(scroll);body=Box(scroll,"mobile-modal-body");}
            Text(body,title,"title");HideMobileScrollers();DrawUIMotion.ShowModal(overlay,modal);return body;
        }
        private void HideMobileScrollers()
        {
            if(!IsMobile)return;
            root.Query<ScrollView>().ForEach(scroll=>{scroll.horizontalScrollerVisibility=ScrollerVisibility.Hidden;scroll.verticalScrollerVisibility=ScrollerVisibility.Hidden;});
        }
        private void CloseModal()
        {
            DrawAudio.Instance?.FlushSettings();
            if(overlay!=null){var previous=overlay;overlay=null;DrawUIMotion.HideModal(previous,previous.Q<VisualElement>(className:"modal"));}
        }
        private void Enter(VisualElement element,int duration,float offset,int delay=0)
        {
            DrawUIMotion.Enter(element,duration,offset,delay);
        }
        private void Toast(string message)
        {
            if(string.IsNullOrWhiteSpace(message))return;root.Q<Label>("toast")?.RemoveFromHierarchy();var label=Text(root,message,"toast");label.name="toast";DrawUIMotion.ShowToast(label,4200);
        }
        private void OnDestroy()
        {
            if(network!=null){network.StateChanged-=RefreshState;network.ChatReceived-=OnChat;}
            if(lobby!=null){lobby.Changed-=RefreshServiceStatus;lobby.ProfileChanged-=OnProfileChanged;}
            if(panelSettings!=null)Destroy(panelSettings);
            if(_mobileLayout!=null){_mobileLayout.LayoutChanged-=OnMobileLayoutChanged;_mobileLayout.Dispose();}
        }
        private static VisualElement Box(VisualElement parent,string classes){var e=new VisualElement();Classes(e,classes);parent.Add(e);return e;}
        private static Label Text(VisualElement parent,string text,string classes){var e=new Label(text);e.enableRichText=false;Classes(e,classes);parent.Add(e);return e;}
        private static Button Button(VisualElement parent,string text,Action clicked,string classes,DrawSound sound=DrawSound.UiClick){var cue=sound==DrawSound.UiClick&&classes.Split(' ').Contains("danger")?DrawSound.UiCancel:sound;var e=new Button(()=>{DrawAudio.Instance?.Play(cue);clicked?.Invoke();}){text=text};e.AddToClassList("button");Classes(e,classes);parent.Add(e);DrawButtonScale.Bind(e);return e;}
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
