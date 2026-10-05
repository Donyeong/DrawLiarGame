using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace DrawLiar
{
    public sealed class PublicRoomInfo
    {
        public string Id;
        public string Code;
        public string Name;
        public int Players;
        public int MaxPlayers;
        public int Spectators;
        public bool IsInProgress;
    }

    public sealed class RoomPasswordPrompt
    {
        public string RoomCode { get; }
        public string RoomName { get; }
        public string Error { get; }

        public RoomPasswordPrompt(string roomCode, string roomName, string error)
        {
            RoomCode = roomCode ?? "";
            RoomName = roomName ?? "";
            Error = error ?? "";
        }
    }

    [DisallowMultipleComponent]
    public sealed class LobbyServiceBridge : MonoBehaviour
    {
        private readonly List<PublicRoomInfo> _publicRooms = new List<PublicRoomInfo>();
        private readonly DrawLobbyChatClient _lobbyChat = new DrawLobbyChatClient();
        private DrawNetworkManager _manager;
        private OnlineServicesConfig _config;
        private string _mainSession = "", _gameSession = "", _gameServerUrl = "", _roomCode = "";
        private string _pendingRoomInvite = "";
        private CancellationToken _lifetime;
        private CancellationTokenSource _googleCancellation;
        private CancellationTokenSource _socialCancellation = new CancellationTokenSource();
        private string _statusSource = "";
        private object[] _statusArguments = Array.Empty<object>();
        private bool _lobbyChatPaused;
        private bool _loggingOut;
        private bool _socialPolling;
        private double _nextSocialPoll;
        private int _socialRevision;
        private string _inboxFingerprint = "";
        private readonly HashSet<string> _knownFriendRequests = new HashSet<string>();
        private readonly HashSet<string> _knownRoomInvitations = new HashSet<string>();

        public string RoomCode => _roomCode;
        public bool IsGoogleSigningIn => _googleCancellation != null;
        public string Status => DrawLocalization.Format(_statusSource, _statusArguments);
        public bool IsBusy { get; private set; }
        public bool IsOnlineRoom => _manager != null && _manager.IsConnected;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_gameSession);
        public bool HasPendingRoomInvite => !string.IsNullOrEmpty(_pendingRoomInvite);
        public ProfileData Profile { get; private set; }
        public FriendListResponse Friends { get; private set; } = new FriendListResponse();
        public SocialInboxResponse Inbox { get; private set; } = new SocialInboxResponse();
        public int NotificationCount => Inbox.Friends.Incoming.Length + Inbox.RoomInvitations.Length;
        public ShopResponse Shop { get; private set; } = new ShopResponse();
        public IReadOnlyList<PublicRoomInfo> PublicRooms => _publicRooms;
        public IReadOnlyList<LobbyChatMessage> LobbyMessages => _lobbyChat.Messages;
        public bool IsLobbyChatConnected => _lobbyChat.IsConnected;
        public bool IsLobbyChatSending => _lobbyChat.IsSending;
        public int LobbyMemberCount => _lobbyChat.MemberCount;
        public string LobbyChatStatus => _lobbyChat.Status;
        public event Action Changed;
        public event Action ProfileChanged;
        public event Action LobbyChatChanged;
        public event Action<string> LobbyChatNotice;
        public event Action SocialChanged;
        public event Action<string> SocialNotice;
        public event Func<RoomPasswordPrompt, CancellationToken, Task<string>> RoomPasswordRequested;

        private sealed class RoomPasswordException : InvalidOperationException
        {
            public RoomPasswordException(string message) : base(message) { }
        }

        public void Initialize(DrawNetworkManager manager)
        {
            _manager = manager;
            _lifetime = destroyCancellationToken;
            _config = OnlineServicesConfig.Load();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-drawMainServer");
            if (index >= 0 && index + 1 < arguments.Length) _config.SetDevelopmentServer(arguments[index + 1]);
#endif
            _manager.StateChanged += OnRoomState;
            _lobbyChat.Changed += OnLobbyChatChanged;
            _lobbyChat.Notice += OnLobbyChatNotice;
#if UNITY_WEBGL && !UNITY_EDITOR
            _pendingRoomInvite = "";
            string invite = DrawBrowserInterop.RoomInvite();
            if (!string.IsNullOrEmpty(invite))
            {
                try { _pendingRoomInvite = NormalizeCode(invite); }
                catch (InvalidOperationException) { SetStatus("방 코드 또는 주소를 확인하세요."); }
            }
#endif
        }

        private void OnRoomState(RoomSnapshot state)
        {
            if (state == null) _roomCode = "";
            if (IsOnlineRoom) StopLobbyChat(); else StartLobbyChat();
            Changed?.Invoke();
        }

        private void StartLobbyChat()
        {
            if (!_lobbyChatPaused && !_loggingOut && IsAuthenticated && !IsOnlineRoom)
                _lobbyChat.Start(_gameServerUrl, _gameSession, _config, _lifetime);
        }

        private void StopLobbyChat(bool clearHistory = false) => _lobbyChat.Stop(clearHistory);
        private void OnLobbyChatChanged() => LobbyChatChanged?.Invoke();
        private void OnLobbyChatNotice(string message) => LobbyChatNotice?.Invoke(message);
        private void Update()
        {
            _lobbyChat.Drain();
            if (IsAuthenticated && !_loggingOut && !_lobbyChatPaused && !IsBusy && !_socialPolling
                && Time.realtimeSinceStartupAsDouble >= _nextSocialPoll)
                _ = PollSocialAsync();
        }

        private async Task PollSocialAsync()
        {
            _socialPolling = true;
            string session = _gameSession;
            int revision = 0;
            double delay = 3;
            try
            {
                var refresh = RefreshSocialAsync();
                revision = _socialRevision;
                await refresh;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { delay = 30; }
            finally
            {
                _socialPolling = false;
                if (session == _gameSession && revision == _socialRevision)
                    _nextSocialPoll = Time.realtimeSinceStartupAsDouble + delay;
            }
        }

        public async Task RefreshSocialAsync()
        {
            RequireLogin();
            int revision = ++_socialRevision;
            string session = _gameSession;
            var inbox = await SendAsync<SocialInboxResponse>(_gameServerUrl, "/api/social/inbox", "GET",
                bearer: session, cancellationToken: _socialCancellation.Token);
            if (revision != _socialRevision || session != _gameSession || _loggingOut || _lobbyChatPaused) return;
            inbox.Friends ??= new FriendListResponse();
            inbox.Friends.Friends ??= Array.Empty<FriendData>();
            inbox.Friends.Incoming ??= Array.Empty<FriendData>();
            inbox.Friends.Outgoing ??= Array.Empty<FriendData>();
            inbox.RoomInvitations ??= Array.Empty<RoomInvitationData>();
            string fingerprint = JsonUtility.ToJson(inbox);
            var newRequests = inbox.Friends.Incoming.Where(friend => !_knownFriendRequests.Contains(friend.AccountId)).ToArray();
            var newInvitations = inbox.RoomInvitations.Where(invite => !_knownRoomInvitations.Contains(invite.InvitationId)).ToArray();
            _knownFriendRequests.Clear();
            _knownRoomInvitations.Clear();
            foreach (var friend in inbox.Friends.Incoming) _knownFriendRequests.Add(friend.AccountId);
            foreach (var invite in inbox.RoomInvitations) _knownRoomInvitations.Add(invite.InvitationId);
            Friends = inbox.Friends;
            Inbox = inbox;
            if (_inboxFingerprint != fingerprint)
            {
                _inboxFingerprint = fingerprint;
                SocialChanged?.Invoke();
            }
            if (newRequests.Length == 1) SocialNotice?.Invoke(DrawLocalization.Format("{0}님이 친구 요청을 보냈습니다.", newRequests[0].DisplayName));
            else if (newRequests.Length > 1) SocialNotice?.Invoke(DrawLocalization.Format("친구 요청 {0}개가 도착했습니다.", newRequests.Length));
            if (newInvitations.Length == 1) SocialNotice?.Invoke(DrawLocalization.Format("{0}님이 방에 초대했습니다.", newInvitations[0].Sender.DisplayName));
            else if (newInvitations.Length > 1) SocialNotice?.Invoke(DrawLocalization.Format("방 초대 {0}개가 도착했습니다.", newInvitations.Length));
        }

        private void ResetSocial()
        {
            ++_socialRevision;
            RenewSocialCancellation();
            _nextSocialPoll = 0;
            _inboxFingerprint = "";
            _knownFriendRequests.Clear();
            _knownRoomInvitations.Clear();
            Inbox = new SocialInboxResponse();
            Friends = Inbox.Friends;
            SocialChanged?.Invoke();
        }

        private void RenewSocialCancellation()
        {
            _socialCancellation.Cancel();
            _socialCancellation.Dispose();
            _socialCancellation = new CancellationTokenSource();
        }

        private void OnApplicationPause(bool paused)
        {
            _lobbyChatPaused = paused;
            ++_socialRevision;
            if (paused) RenewSocialCancellation();
            if (!paused) _nextSocialPoll = 0;
            if (paused) StopLobbyChat(); else StartLobbyChat();
        }

        public Task SendLobbyChatAsync(string text)
        {
            RequireLogin();
            if (IsOnlineRoom) throw new InvalidOperationException(DrawLocalization.Text("채팅에 연결된 뒤 다시 시도하세요."));
            return _lobbyChat.SendAsync(text);
        }

        public Task GuestLoginAsync() => RunAsync(async () =>
        {
            var credentials = DrawGuestCredentialStore.GetOrCreate();
            await EnterOutgameAsync(await SendAsync<LoginResponse>(_config.MainServerUrl, "/api/auth/guest", "POST",
                new GuestLoginRequest { GuestId = credentials.GuestId, GuestSecret = credentials.GuestSecret }));
        });

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public Task DevelopmentLoginAsync(string displayName) => RunAsync(async () =>
        {
            await EnterOutgameAsync(await SendAsync<LoginResponse>(_config.MainServerUrl, "/api/auth/development", "POST",
                new DevelopmentLoginRequest { DisplayName = displayName }));
        });
#endif

        private async Task EnterOutgameAsync(LoginResponse login)
        {
            if (login == null || string.IsNullOrEmpty(login.SessionToken) || string.IsNullOrEmpty(login.AssignmentToken))
                throw new InvalidOperationException("서버 로그인 응답을 확인할 수 없습니다.");
            string gameServerUrl = _config.ResolveGameServerUrl(login.GameServerUrl);
            _config.ValidateServerUrl(gameServerUrl);
            var session = await SendAsync<GameSessionResponse>(gameServerUrl, "/api/session/enter", "POST",
                new EnterGameRequest { AssignmentToken = login.AssignmentToken });
            if (string.IsNullOrEmpty(session.SessionToken) || session.Profile == null || session.Profile.AccountId != login.AccountId)
                throw new InvalidOperationException("서버 로그인 응답을 확인할 수 없습니다.");
            StopLobbyChat(true);
            _mainSession = login.SessionToken;
            _gameServerUrl = gameServerUrl.TrimEnd('/');
            _gameSession = session.SessionToken;
            ResetSocial();
            SetProfile(session.Profile);
            StartLobbyChat();
            SetStatus("로그인했습니다. 방을 만들거나 참가하세요.");
        }

        public Task GoogleLoginAsync(bool link = false) => RunAsync(async () =>
        {
            if (link && string.IsNullOrEmpty(_mainSession)) throw new InvalidOperationException("먼저 로그인해 주세요.");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            DrawAsync.CancelAfter(cancellation, TimeSpan.FromMinutes(4));
            _googleCancellation = cancellation;
            try
            {
                var challenge = await SendAsync<GoogleChallengeResponse>(_config.MainServerUrl, "/api/auth/google/challenge", "POST",
                    new GoogleChallengeRequest { Platform = GoogleAccountProvider.Platform }, link ? _mainSession : "");
                SetStatus("Google 계정 인증을 완료해 주세요.");
                var request = await GoogleAccountProvider.AuthenticateAsync(challenge, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _googleCancellation = null;
                SetStatus(link ? "Google 계정을 연동하고 있습니다…" : "Google 로그인 정보를 확인하고 있습니다…");
                if (link)
                {
                    SetProfile(await SendAsync<ProfileData>(_config.MainServerUrl, "/api/auth/google/link", "POST", request, _mainSession));
                    SetStatus("Google 계정을 연동했습니다.");
                }
                else await EnterOutgameAsync(await SendAsync<LoginResponse>(_config.MainServerUrl, "/api/auth/google", "POST", request));
            }
            finally { _googleCancellation = null; }
        });

        public void CancelGoogleLogin() => _googleCancellation?.Cancel();

        public Task SaveProfileAsync(string displayName, int avatarColor, int accessory) => RunAsync(async () =>
        {
            RequireLogin();
            string session = _gameSession, accountId = Profile?.AccountId;
            void CheckSession()
            {
                _lifetime.ThrowIfCancellationRequested();
                if (_loggingOut || session != _gameSession || accountId != Profile?.AccountId) throw new OperationCanceledException();
            }
            var profile = await SendGameAsync<ProfileData>("/api/profile", "PATCH",
                new UpdateProfileRequest { DisplayName = displayName.Trim(), AvatarColor = avatarColor, Accessory = accessory });
            CheckSession();
            SetProfile(profile);
            if (_manager != null && _manager.IsConnected) await _manager.RefreshProfileAsync();
            CheckSession();
            SetStatus("캐릭터를 저장했습니다.");
        });

        public async Task<PublicProfileData> GetPublicProfileAsync(string accountId, CancellationToken cancellationToken)
        {
            RequireLogin();
            if (!Guid.TryParse(accountId, out var id)) throw new InvalidOperationException("프로필을 불러오지 못했습니다.");
            string session = _gameSession;
            string viewer = Profile?.AccountId;
            var profile = await SendAsync<PublicProfileData>(_gameServerUrl, "/api/profiles/" + id, "GET",
                bearer: session, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != _gameSession || viewer != Profile?.AccountId) throw new OperationCanceledException();
            if (profile == null || !string.Equals(profile.AccountId, id.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("프로필을 불러오지 못했습니다.");
            return profile;
        }

        public Task RefreshFriendsAsync() => RunAsync(async () =>
        {
            await RefreshSocialAsync();
            SetStatus("친구 목록을 갱신했습니다.");
        });

        public Task RequestFriendAsync(string accountId) => RunAsync(async () =>
        {
            RequireLogin();
            ++_socialRevision;
            await SendGameAsync<ApiError>("/api/friends/request", "POST", new FriendRequest { AccountId = accountId.Trim() });
            await RefreshSocialAsync();
            SetStatus("친구 요청을 보냈습니다.");
        });

        public Task RespondFriendAsync(string accountId, bool accept) => RunAsync(async () =>
        {
            RequireLogin();
            ++_socialRevision;
            await SendGameAsync<ApiError>("/api/friends/respond", "POST", new FriendRespondRequest { AccountId = accountId, Accept = accept });
            await RefreshSocialAsync();
            SetStatus(accept ? "친구 요청을 수락했습니다." : "친구 요청을 거절했습니다.");
        });

        public Task RemoveFriendAsync(string accountId) => RunAsync(async () =>
        {
            RequireLogin(); ++_socialRevision;
            await SendGameAsync<ApiError>("/api/friends/" + Uri.EscapeDataString(accountId), "DELETE");
            await RefreshSocialAsync(); SetStatus("친구를 삭제했습니다.");
        });

        public async Task<RoomInvitationData> InviteFriendAsync(string accountId)
        {
            RoomInvitationData invitation = null;
            await RunAsync(async () =>
            {
                RequireLogin();
                if (!IsOnlineRoom || string.IsNullOrEmpty(_roomCode)) throw new InvalidOperationException("방에 입장한 뒤 친구를 초대하세요.");
                invitation = await SendGameAsync<RoomInvitationData>("/api/rooms/" + Uri.EscapeDataString(_roomCode) + "/invite", "POST",
                    new FriendRequest { AccountId = accountId });
                SetStatus("친구를 초대했습니다.");
            });
            return invitation;
        }

        public Task RespondRoomInvitationAsync(string invitationId, bool accept, bool leaveCurrentRoom = false) => RunAsync(async () =>
        {
            RequireLogin();
            if (!Guid.TryParse(invitationId, out var id)) throw new InvalidOperationException("초대가 만료되었거나 취소되었습니다.");
            if (accept && IsOnlineRoom && !leaveCurrentRoom) throw new InvalidOperationException("현재 방에서 나온 뒤 참가하세요.");
            if (accept && _manager == null) throw new InvalidOperationException("네트워크가 초기화되지 않았습니다.");
            ++_socialRevision;
            try
            {
                string path = "/api/room-invitations/" + id + "/respond";
                var invitation = Inbox.RoomInvitations.FirstOrDefault(entry => entry.InvitationId == id.ToString());
                var assignment = accept
                    ? await RequestRoomAssignmentAsync(password => SendGameAsync<DedicatedAssignment>(path, "POST",
                        new RoomInvitationRespondRequest { Accept = true, Password = password }), invitation?.RoomCode, invitation?.RoomName)
                    : await SendGameAsync<DedicatedAssignment>(path, "POST", new RoomInvitationRespondRequest { Accept = false });
                if (accept)
                {
                    if (IsOnlineRoom)
                    {
                        await _manager.LeaveAsync();
                        _roomCode = "";
                    }
                    await ConnectRoomAsync(assignment);
                }
                else SetStatus("방 초대를 거절했습니다.");
            }
            finally
            {
                _nextSocialPoll = 0;
                try { await RefreshSocialAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        });

        public Task RefreshShopAsync() => RunAsync(async () =>
        {
            RequireLogin(); Shop = await SendGameAsync<ShopResponse>("/api/shop", "GET"); SetStatus("상점을 갱신했습니다.");
        });

        public Task PurchaseAsync(string productId) => RunAsync(async () =>
        {
            RequireLogin();
            SetProfile(await SendGameAsync<ProfileData>("/api/shop/purchase", "POST",
                new PurchaseRequest { ProductId = productId, OperationId = Guid.NewGuid().ToString("N") }));
            SetStatus("아이템을 구매했습니다.");
        });

        public Task HostAsync(RoomSettings settings, string password = "") => RunAsync(async () =>
        {
            RequireAvailable();
            settings = settings?.Copy() ?? new RoomSettings(); settings.Validate();
            var serverSettings = JsonUtility.FromJson<ServerRoomSettings>(JsonUtility.ToJson(settings));
            var customTopics = GameDataStore.LoadCustomTopics().Where(topic => settings.Topics == null || settings.Topics.Contains(topic.Name))
                .Select(topic => new ServerTopicData { Name = topic.Name, Words = topic.Words }).ToArray();
            var assignment = await SendGameAsync<DedicatedAssignment>("/api/rooms", "POST",
                new CreateRoomRequest { Settings = serverSettings, CustomTopics = customTopics, Password = settings.IsPrivate ? password : "" });
            await ConnectRoomAsync(assignment);
        });

        public Task JoinCodeAsync(string code, bool asSpectator = false) => JoinLobbyAsync(NormalizeCode(code), asSpectator);

        public async Task TryJoinInviteAsync()
        {
            if (!HasPendingRoomInvite || !IsAuthenticated || IsBusy || IsOnlineRoom || _manager == null) return;
            string code = _pendingRoomInvite;
            _pendingRoomInvite = "";
#if UNITY_WEBGL && !UNITY_EDITOR
            DrawBrowserInterop.ClearRoomInvite();
#endif
            await JoinCodeAsync(code);
        }

        public Task JoinLobbyAsync(string roomId, bool asSpectator = false) => RunAsync(async () =>
        {
            RequireAvailable();
            if (string.IsNullOrWhiteSpace(roomId)) throw new InvalidOperationException("방 코드를 입력하세요.");
            var assignment = await RequestRoomAssignmentAsync(password => SendGameAsync<DedicatedAssignment>(
                "/api/rooms/" + Uri.EscapeDataString(roomId) + "/join", "POST",
                new JoinRoomRequest { AsSpectator = asSpectator, Password = password }), roomId, "");
            await ConnectRoomAsync(assignment);
        });

        private async Task<DedicatedAssignment> RequestRoomAssignmentAsync(Func<string, Task<DedicatedAssignment>> request,
            string roomCode, string roomName)
        {
            string session = _gameSession;
            string password = "";
            try
            {
                while (true)
                {
                    _lifetime.ThrowIfCancellationRequested();
                    if (session != _gameSession || _loggingOut) throw new OperationCanceledException();
                    try
                    {
                        var assignment = await request(password);
                        if (session != _gameSession || _loggingOut) throw new OperationCanceledException();
                        return assignment;
                    }
                    catch (RoomPasswordException exception)
                    {
                        var prompt = RoomPasswordRequested;
                        if (prompt == null) throw;
                        password = await prompt(new RoomPasswordPrompt(roomCode, roomName, exception.Message), _lifetime);
                    }
                }
            }
            finally { password = ""; }
        }

        private async Task ConnectRoomAsync(DedicatedAssignment assignment)
        {
            SetStatus("게임 서버에 연결하고 있습니다…");
            await _manager.ConnectAsync(assignment);
            _roomCode = string.IsNullOrEmpty(assignment.RoomCode) ? assignment.RoomId : assignment.RoomCode;
            SetStatus("방에 연결했습니다. 방 코드: {0}", _roomCode);
        }

        public Task RefreshAsync() => SearchRoomsAsync("");
        public Task SearchRoomsAsync(string search) => RunAsync(async () =>
        {
            RequireLogin();
            var response = await SendGameAsync<RoomListResponse>("/api/rooms?search=" + Uri.EscapeDataString(search.Trim()), "GET");
            _publicRooms.Clear();
            foreach (var room in response.Rooms)
                _publicRooms.Add(new PublicRoomInfo { Id = room.RoomId, Code = room.RoomCode, Name = room.Name, Players = room.PlayerCount,
                    MaxPlayers = room.Settings.MaxPlayers, Spectators = room.SpectatorCount, IsInProgress = room.IsInProgress });
            if (_publicRooms.Count == 0) SetStatus("공개 방이 없습니다.");
            else SetStatus("공개 방 {0}개를 찾았습니다.", _publicRooms.Count);
        });

        public Task LeaveAsync() => RunAsync(async () =>
        {
            await _manager.LeaveAsync(); _roomCode = ""; SetStatus("방에서 나왔습니다.");
        });

        public Task LogoutAsync() => RunAsync(async () =>
        {
            _loggingOut = true;
            ++_socialRevision;
            StopLobbyChat(true);
            try
            {
                await _manager.LeaveAsync();
                if (!string.IsNullOrEmpty(_gameSession)) await SendGameAsync<ApiError>("/api/session/logout", "POST");
                if (!string.IsNullOrEmpty(_mainSession)) await SendAsync<ApiError>(_config.MainServerUrl, "/api/session/logout", "POST", null, _mainSession);
            }
            finally
            {
                _mainSession = _gameSession = _gameServerUrl = _roomCode = "";
                StopLobbyChat(true);
                _loggingOut = false;
                Profile = null; Friends = new FriendListResponse(); Shop = new ShopResponse(); _publicRooms.Clear();
                ResetSocial();
                ProfileChanged?.Invoke(); SetStatus("로그아웃했습니다.");
            }
        });

        private void RequireLogin()
        {
            if (!IsAuthenticated) throw new InvalidOperationException("먼저 로그인해 주세요.");
        }
        private void RequireAvailable()
        {
            RequireLogin();
            if (_manager == null) throw new InvalidOperationException("네트워크가 초기화되지 않았습니다.");
            if (_manager.IsConnected) throw new InvalidOperationException("현재 방에서 나온 뒤 참가하세요.");
        }
        private void SetProfile(ProfileData profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.AccountId) || string.IsNullOrEmpty(profile.DisplayName))
                throw new InvalidOperationException("서버 프로필 응답을 확인할 수 없습니다.");
            Profile = profile;
            ProfileChanged?.Invoke();
        }
        private void SetStatus(string source, params object[] args)
        {
            _statusSource = source;
            _statusArguments = args;
            Changed?.Invoke();
        }

        private async Task RunAsync(Func<Task> operation)
        {
            if (IsBusy) throw new InvalidOperationException("요청을 처리하고 있습니다. 잠시 기다려 주세요.");
            IsBusy = true; Changed?.Invoke();
            try { await operation(); }
            catch (OperationCanceledException) { SetStatus("요청을 취소했습니다."); throw; }
            catch (Exception exception) { SetStatus(exception.Message); throw; }
            finally { IsBusy = false; Changed?.Invoke(); }
        }

        private Task<T> SendGameAsync<T>(string path, string method, object body = null) where T : class, new() =>
            SendAsync<T>(_gameServerUrl, path, method, body, _gameSession);

        private async Task<T> SendAsync<T>(string baseUrl, string path, string method, object body = null, string bearer = "", CancellationToken cancellationToken = default) where T : class, new()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime, cancellationToken);
            cancellation.Token.ThrowIfCancellationRequested();
            _config.ValidateServerUrl(baseUrl);
            using var request = new UnityWebRequest(baseUrl.TrimEnd('/') + path, method);
            request.redirectLimit = 0;
            request.downloadHandler = new DownloadHandlerBuffer();
            request.certificateHandler = _config.CreateCertificateHandler();
            request.timeout = 20;
            request.SetRequestHeader("Accept", "application/json");
            if (body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(bearer)) request.SetRequestHeader("Authorization", "Bearer " + bearer);
            var operation = request.SendWebRequest();
            try
            {
                while (!operation.isDone) { cancellation.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                cancellation.Token.ThrowIfCancellationRequested();
            }
            catch { request.Abort(); throw; }
            if (request.result != UnityWebRequest.Result.Success)
            {
                string code = "ConnectionFailed";
                try { code = JsonUtility.FromJson<ApiError>(request.downloadHandler.text)?.Code ?? code; } catch (ArgumentException) { }
                string message = DescribeError(code, request.responseCode);
                if (code == "RoomPasswordRequired" || code == "InvalidRoomPassword" || code == "InvalidRoomPasswordFormat")
                    throw new RoomPasswordException(message);
                throw new InvalidOperationException(message);
            }
            if (string.IsNullOrEmpty(request.downloadHandler.text))
            {
                if (typeof(T) == typeof(ApiError)) return new T();
                throw new InvalidOperationException("서버 응답을 확인할 수 없습니다.");
            }
            return JsonUtility.FromJson<T>(request.downloadHandler.text) ?? throw new InvalidOperationException("서버 응답을 확인할 수 없습니다.");
        }

        public static string NormalizeCode(string value)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return RoomJoinAddress.NormalizeCode(value, DrawBrowserInterop.RoomPageUrl());
#else
            return RoomJoinAddress.NormalizeCode(value);
#endif
        }

        private static string DescribeError(string code, long status)
        {
            switch (code)
            {
                case "InvalidGuestCredential": return "게스트 인증 정보를 확인할 수 없습니다. Google 연동 계정은 Google로 로그인하세요.";
                case "ExternalLoginRequired": return "Google에 연동된 계정입니다. Google로 로그인해 주세요.";
                case "InvalidCredentials": return "인증 정보를 확인하세요.";
                case "AlreadyExists": return "이미 추가한 항목입니다.";
                case "RoomUnavailable": return "방을 찾지 못했습니다. 방 코드를 확인하세요.";
                case "InvalidRoomCode": return "영문·숫자 6자리 방 코드를 확인하세요.";
                case "RoomFull": return "참가 인원이 가득 찼습니다. 관전으로 참가할 수 있습니다.";
                case "RoomPasswordRequired": return "비밀번호가 필요한 방입니다.";
                case "RoomPasswordNotConfigured": return "방장이 비밀번호를 설정한 뒤 입장할 수 있습니다.";
                case "InvalidRoomPassword": return "비밀번호가 올바르지 않습니다.";
                case "InvalidRoomPasswordFormat": return "비밀번호는 4~32자로 입력하세요.";
                case "RoomConfigurationChanged": return "방 설정이 변경되었습니다. 다시 시도하세요.";
                case "InsufficientCoins": return "코인이 부족합니다.";
                case "GoogleUnavailable": return "서버의 Google 로그인 설정을 확인해야 합니다.";
                case "InvalidGoogleCredential": return "Google 인증에 실패했습니다. 다시 로그인해 주세요.";
                case "GoogleAlreadyLinked": return "이미 연동한 Google 계정입니다.";
                case "InvalidDisplayName": return "닉네임은 2~16자의 글자, 숫자, 공백, 밑줄로 입력하세요.";
                case "GameServerUnavailable": return "로비 서버가 준비 중입니다. 잠시 후 다시 시도하세요.";
                case "DedicatedUnavailable": return "게임 서버가 준비 중입니다. 잠시 후 다시 시도하세요.";
                case "InvalidTopics": return "주제 이름과 제시어를 확인하세요.";
                case "InvalidSettings": return "방 설정을 확인하세요.";
                case "AlreadyOwnsRoom": return "이미 만든 방에 다시 참가하거나 방을 닫은 뒤 새 방을 만드세요.";
                case "AccessoryNotOwned": return "보유한 아이템만 장착할 수 있습니다.";
                case "AlreadyOwned": return "이미 보유한 아이템입니다.";
                case "FriendLimit": return "친구 목록의 최대 인원에 도달했습니다.";
                case "AccountNotFound": return "계정 ID를 찾지 못했습니다.";
                case "FriendRequestNotFound": return "친구 요청을 찾지 못했습니다. 목록을 갱신하세요.";
                case "NotFriends": return "친구로 추가된 상대만 초대할 수 있습니다.";
                case "NotRoomMember": return "방에 입장한 뒤 친구를 초대하세요.";
                case "AlreadyInRoom": return "이미 같은 방에 있습니다.";
                case "RoomInvitationCooldown": return "잠시 후 다시 초대하세요.";
                case "RoomInvitationLimit": return "보낸 초대가 많습니다. 잠시 후 다시 시도하세요.";
                case "RoomInvitationUnavailable":
                case "InvalidInvitation": return "초대가 만료되었거나 취소되었습니다.";
            }
            if (status == 401) return "로그인이 만료되었습니다. 다시 로그인해 주세요.";
            if (status == 429) return "요청이 많습니다. 잠시 후 다시 시도하세요.";
            return status == 0 ? "서버에 연결하지 못했습니다. 인터넷 연결을 확인하세요." : "요청을 처리하지 못했습니다. 다시 시도하세요.";
        }

        private void OnDestroy()
        {
            _googleCancellation?.Cancel();
            _socialCancellation.Cancel();
            _socialCancellation.Dispose();
            if (_manager != null) _manager.StateChanged -= OnRoomState;
            _lobbyChat.Changed -= OnLobbyChatChanged;
            _lobbyChat.Notice -= OnLobbyChatNotice;
            _lobbyChat.Dispose();
        }
    }
}
