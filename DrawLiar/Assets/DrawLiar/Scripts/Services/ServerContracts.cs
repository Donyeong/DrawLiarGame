using System;

namespace DrawLiar
{
    [Serializable] public sealed class ApiError { public string Code = "RequestFailed"; }
    [Serializable] public sealed class GuestLoginRequest { public string GuestId = ""; public string GuestSecret = ""; }
    [Serializable] public sealed class LoginRequest { public string Email = ""; public string Password = ""; }
    [Serializable] public sealed class DevelopmentLoginRequest { public string DisplayName = ""; }
    [Serializable] public sealed class LoginResponse
    {
        public string AccountId = "";
        public string SessionToken = "";
        public string ExpiresAt = "";
        public string GameServerUrl = "";
        public string AssignmentToken = "";
        public ProfileData Profile = new ProfileData();
    }
    [Serializable] public sealed class EnterGameRequest { public string AssignmentToken = ""; }
    [Serializable] public sealed class GameSessionResponse { public string SessionToken = ""; public string ExpiresAt = ""; public ProfileData Profile = new ProfileData(); }
    [Serializable] public sealed class ProfileData
    {
        public string AccountId = "";
        public string DisplayName = "";
        public int AvatarColor;
        public int Accessory;
        public int Coins;
        public bool IsGuest;
        public bool HasGoogleAccount;
        public int[] OwnedAccessories = Array.Empty<int>();
    }
    [Serializable] public sealed class UpdateProfileRequest { public string DisplayName = ""; public int AvatarColor; public int Accessory; }
    [Serializable] public sealed class GoogleChallengeRequest { public string Platform = "desktop"; }
    [Serializable] public sealed class GoogleChallengeResponse { public string ChallengeId = ""; public string Nonce = ""; public string ClientId = ""; public string ExpiresAt = ""; }
    [Serializable] public sealed class GoogleAuthRequest { public string ChallengeId = ""; public string IdToken = ""; public string Code = ""; public string CodeVerifier = ""; public string RedirectUri = ""; }
    [Serializable] public sealed class FriendData { public string AccountId = ""; public string DisplayName = ""; public int AvatarColor; public int Accessory; }
    [Serializable] public sealed class FriendListResponse { public FriendData[] Friends = Array.Empty<FriendData>(); public FriendData[] Incoming = Array.Empty<FriendData>(); public FriendData[] Outgoing = Array.Empty<FriendData>(); }
    [Serializable] public sealed class FriendRequest { public string AccountId = ""; }
    [Serializable] public sealed class FriendRespondRequest { public string AccountId = ""; public bool Accept; }
    [Serializable] public sealed class ShopProduct { public string Id = ""; public string Name = ""; public int Price; public int Accessory; }
    [Serializable] public sealed class ShopResponse { public ShopProduct[] Products = Array.Empty<ShopProduct>(); }
    [Serializable] public sealed class PurchaseRequest { public string ProductId = ""; public string OperationId = ""; }
    [Serializable] public sealed class ServerRoomSettings
    {
        public const int MAX_PLAYERS = 8;
        public int MaxPlayers = MAX_PLAYERS;
        public int LiarCount = 1;
        public int RoundCount = 5;
        public int TargetScore = 10;
        public int Mode;
        public int Victory;
        public int RoleSeconds = 6;
        public int DrawSeconds = 25;
        public int DiscussionSeconds = 45;
        public int RebuttalSeconds = 20;
        public int VoteSeconds = 20;
        public int RevealSeconds = 5;
        public int GuessSeconds = 20;
        public int ResultSeconds = 10;
        public string[] Topics = Array.Empty<string>();
        public string RoomName = "우리들의 그림방";
        public bool IsPrivate;
    }
    [Serializable] public sealed class ServerRoomData
    {
        public string RoomId = "";
        public string RoomCode = "";
        public string Name = "";
        public ServerRoomSettings Settings = new ServerRoomSettings();
        public string OwnerAccountId = "";
        public string NodeId = "";
        public int PlayerCount;
        public int SpectatorCount;
        public bool IsInProgress;
        public bool IsPrivate;
    }
    [Serializable] public sealed class RoomListResponse { public ServerRoomData[] Rooms = Array.Empty<ServerRoomData>(); }
    [Serializable] public sealed class ServerTopicData { public string Name = ""; public string[] Words = Array.Empty<string>(); }
    [Serializable] public sealed class CreateRoomRequest { public ServerRoomSettings Settings = new ServerRoomSettings(); public ServerTopicData[] CustomTopics = Array.Empty<ServerTopicData>(); }
    [Serializable] public sealed class JoinRoomRequest { public bool AsSpectator; }
    [Serializable] public sealed class DedicatedAssignment { public string RoomId = ""; public string RoomCode = ""; public string DedicatedUrl = ""; public string JoinTicket = ""; public string ExpiresAt = ""; }
    [Serializable] public sealed class RegisterGameRequest { public string NodeId = ""; public string PublicUrl = ""; }
    [Serializable] public sealed class RegisterDedicatedRequest { public string NodeId = ""; public string PublicUrl = ""; public int Capacity = 32; }
    [Serializable] public sealed class RoomStatusData { public string RoomId = ""; public int PlayerCount; public int SpectatorCount; public bool IsInProgress; public string OwnerAccountId = ""; public bool Closed; public string[] PlayerAccountIds = Array.Empty<string>(); public string[] SpectatorAccountIds = Array.Empty<string>(); public string[] AdmissionIds = Array.Empty<string>(); public ServerRoomSettings Settings = new ServerRoomSettings(); }
    [Serializable] public sealed class DedicatedHeartbeatRequest { public string NodeId = ""; public RoomStatusData[] Rooms = Array.Empty<RoomStatusData>(); }
    [Serializable] public sealed class DedicatedHeartbeatResponse { public string[] ClosedRoomIds = Array.Empty<string>(); }
    [Serializable] public sealed class RedeemTicketRequest { public string JoinTicket = ""; public string NodeId = ""; public string RoomId = ""; }
    [Serializable] public sealed class RedeemTicketResponse { public string AccountId = ""; public string AdmissionId = ""; public ProfileData Profile = new ProfileData(); public ServerRoomData Room = new ServerRoomData(); public string SessionToken = ""; public bool IsSpectator; public bool SpectatorOnly; public ServerTopicData[] CustomTopics = Array.Empty<ServerTopicData>(); }
    [Serializable] public sealed class SessionCheckRequest { public string SessionToken = ""; public string AccountId = ""; }
    [Serializable] public sealed class SessionCheckResponse { public bool Valid; }
    [Serializable] public sealed class AdminAccountAction { public bool IsBanned; }
    [Serializable] public sealed class LobbyChatMessage
    {
        public long Id;
        public string AccountId = "";
        public string DisplayName = "";
        public string Text = "";
        public string SentAt = "";
    }
    [Serializable] public sealed class LobbyChatEnvelope
    {
        public string Type = "";
        public string SessionToken = "";
        public string RequestId = "";
        public string Text = "";
        public LobbyChatMessage Message = new LobbyChatMessage();
        public LobbyChatMessage[] Messages = Array.Empty<LobbyChatMessage>();
        public int MemberCount;
        public string Code = "";
    }
}
