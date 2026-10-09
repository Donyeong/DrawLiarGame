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
        public long Accessory;
        public int Coins;
        public int PaidGems;
        public string SubscriptionExpiresAt = "";
        public bool HasPainterSubscription;
        public bool ShowSubscriberBadge;
        public long ServerTimeUnixSeconds;
        public long Experience;
        public int Level = 1;
        public bool IsGuest;
        public bool HasGoogleAccount;
        public long[] OwnedAccessories = Array.Empty<long>();
    }
    [Serializable] public sealed class UpdateProfileRequest { public string DisplayName = ""; public int AvatarColor; public long Accessory; }
    [Serializable] public sealed class PublicProfileData
    {
        public string AccountId = "";
        public string DisplayName = "";
        public int AvatarColor;
        public long Accessory;
        public int Level = 1;
        public bool ShowSubscriberBadge;
        public string JoinedAt = "";
        public string Friendship = "None";
        public ProfileStatsData Stats = new ProfileStatsData();
        public ProfileMatchData[] RecentMatches = Array.Empty<ProfileMatchData>();
    }
    [Serializable] public sealed class ProfileStatsData
    {
        public int MatchesPlayed;
        public int MatchesWon;
        public int TotalScore;
        public int BestScore;
        public int RoundsPlayed;
        public int CitizenRounds;
        public int LiarRounds;
        public int CorrectVotes;
        public int CorrectGuesses;
    }
    [Serializable] public sealed class ProfileMatchData
    {
        public string MatchId = "";
        public string PlayedAt = "";
        public int Score;
        public int Rank;
        public int PlayerCount;
        public int Mode;
        public bool Won;
        public int RoundCount;
    }
    [Serializable] public sealed class MatchRewardPolicy
    {
        public int BaseCoinsPerParticipantRound;
        public int CoinsPerParticipantPoint;
    }
    [Serializable] public sealed class MatchRewardResponse
    {
        public string MatchId = "";
        public bool Recorded;
        public int CoinReward;
        public int ExperienceReward;
        public ProfileData Profile = new ProfileData();
    }
    [Serializable] public sealed class MatchResultRequest
    {
        public string NodeId = "";
        public string RoomId = "";
        public string MatchId = "";
        public string PlayedAt = "";
        public int Mode;
        public int RoundCount;
        public MatchPlayerResult[] Players = Array.Empty<MatchPlayerResult>();
    }
    [Serializable] public sealed class MatchPlayerResult
    {
        public string AccountId = "";
        public int Score;
        public int Rank;
        public bool Won;
        public int RoundsPlayed;
        public int CitizenRounds;
        public int LiarRounds;
        public int CorrectVotes;
        public int CorrectGuesses;
        public long WeightedRoundParticipants;
        public long WeightedRoundScore;
    }
    [Serializable] public sealed class GoogleChallengeRequest { public string Platform = "desktop"; }
    [Serializable] public sealed class GoogleChallengeResponse { public string ChallengeId = ""; public string Nonce = ""; public string ClientId = ""; public string ExpiresAt = ""; }
    [Serializable] public sealed class GoogleAuthRequest { public string ChallengeId = ""; public string IdToken = ""; public string Code = ""; public string CodeVerifier = ""; public string RedirectUri = ""; }
    [Serializable] public sealed class FriendData { public string AccountId = ""; public string DisplayName = ""; public int AvatarColor; public long Accessory; public int Level = 1; }
    [Serializable] public sealed class FriendListResponse { public FriendData[] Friends = Array.Empty<FriendData>(); public FriendData[] Incoming = Array.Empty<FriendData>(); public FriendData[] Outgoing = Array.Empty<FriendData>(); }
    [Serializable] public sealed class FriendRequest { public string AccountId = ""; }
    [Serializable] public sealed class FriendRespondRequest { public string AccountId = ""; public bool Accept; }
    [Serializable] public sealed class SocialInboxResponse
    {
        public FriendListResponse Friends = new FriendListResponse();
        public RoomInvitationData[] RoomInvitations = Array.Empty<RoomInvitationData>();
    }
    [Serializable] public sealed class RoomInvitationData
    {
        public string InvitationId = "";
        public string RoomId = "";
        public string RoomCode = "";
        public string RoomName = "";
        public FriendData Sender = new FriendData();
        public string ExpiresAt = "";
    }
    [Serializable] public sealed class RoomInvitationRespondRequest { public bool Accept; public string Password = ""; }
    [Serializable] public sealed class ShopProduct { public string Id = ""; public string Name = ""; public int Price; public long Accessory; }
    [Serializable] public sealed class ShopResponse { public ShopProduct[] Products = Array.Empty<ShopProduct>(); }
    [Serializable] public sealed class PurchaseRequest { public string ProductId = ""; public string OperationId = ""; }
    [Serializable] public sealed class PurchaseBatchRequest
    {
        public const int MAX_PRODUCTS = 7;
        public string[] ProductIds = Array.Empty<string>();
        public string OperationId = "";
    }
    [Serializable] public sealed class ServerRoomSettings
    {
        public const int MAX_PLAYERS = 8;
        public int MaxPlayers = MAX_PLAYERS;
        public int LiarCount = 1;
        public int LiarMode;
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
        public bool AllowMidRoundJoin = true;
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
        public long ConfigurationVersion;
        public long AccessVersion;
    }
    [Serializable] public sealed class RoomListResponse { public ServerRoomData[] Rooms = Array.Empty<ServerRoomData>(); }
    [Serializable] public sealed class ServerTopicData { public string Name = ""; public string[] Words = Array.Empty<string>(); }
    [Serializable] public sealed class CreateRoomRequest { public ServerRoomSettings Settings = new ServerRoomSettings(); public ServerTopicData[] CustomTopics = Array.Empty<ServerTopicData>(); public string Password = ""; }
    [Serializable] public sealed class JoinRoomRequest { public bool AsSpectator; public string Password = ""; }
    [Serializable] public sealed class DedicatedAssignment { public string RoomId = ""; public string RoomCode = ""; public string DedicatedUrl = ""; public string JoinTicket = ""; public string ExpiresAt = ""; }
    [Serializable] public sealed class RegisterGameRequest { public string NodeId = ""; public string PublicUrl = ""; }
    [Serializable] public sealed class RegisterDedicatedRequest { public string NodeId = ""; public string PublicUrl = ""; public int Capacity = 32; }
    [Serializable] public sealed class RoomStatusData { public string RoomId = ""; public int PlayerCount; public int SpectatorCount; public bool IsInProgress; public string OwnerAccountId = ""; public bool Closed; public string[] PlayerAccountIds = Array.Empty<string>(); public string[] SpectatorAccountIds = Array.Empty<string>(); public string[] AdmissionIds = Array.Empty<string>(); public ServerRoomSettings Settings = new ServerRoomSettings(); public long ConfigurationVersion; }
    [Serializable] public sealed class DedicatedHeartbeatRequest { public string NodeId = ""; public RoomStatusData[] Rooms = Array.Empty<RoomStatusData>(); }
    [Serializable] public sealed class DedicatedHeartbeatResponse { public string[] ClosedRoomIds = Array.Empty<string>(); public RoomConfigurationData[] Configurations = Array.Empty<RoomConfigurationData>(); public RoomKickData[] Kicks = Array.Empty<RoomKickData>(); }
    [Serializable] public sealed class RoomKickData { public string RoomId = ""; public string[] AccountIds = Array.Empty<string>(); }
    [Serializable] public sealed class KickRoomRequest
    {
        public string NodeId = "";
        public string RoomId = "";
        public string OwnerAccountId = "";
        public string OwnerSessionToken = "";
        public string TargetAccountId = "";
        public string TargetSessionToken = "";
        public string OperationId = "";
    }
    [Serializable] public sealed class KickRoomResponse { public string RoomId = ""; public string AccountId = ""; public string OperationId = ""; }
    [Serializable] public sealed class ConfigureRoomRequest
    {
        public string NodeId = "";
        public string RoomId = "";
        public string OwnerAccountId = "";
        public string OperationId = "";
        public long ExpectedVersion;
        public ServerRoomSettings Settings = new ServerRoomSettings();
        public ServerTopicData[] CustomTopics = Array.Empty<ServerTopicData>();
        public string Password = "";
    }
    [Serializable] public sealed class RoomConfigurationData { public string RoomId = ""; public ServerRoomSettings Settings = new ServerRoomSettings(); public ServerTopicData[] CustomTopics = null!; public long Version; public long AccessVersion; }
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
        public int Level = 1;
        public string Text = "";
        public string SentAt = "";
    }
    [Serializable] public sealed class TopicWorkshopLimits
    {
        public int NameMaxLength;
        public int WordMaxLength;
        public int MinWordsPerTopic;
        public int MaxWordsPerTopic;
        public int MaxUploadsPerAccount;
    }
    [Serializable] public sealed class TopicWorkshopPolicy
    {
        public TopicWorkshopLimits Limits = new TopicWorkshopLimits();
        public string[] LanguageCodes = Array.Empty<string>();
    }
    [Serializable] public sealed class TopicWorkshopPublishRequest
    {
        public string Name = "";
        public string LanguageCode = "";
        public string[] Words = Array.Empty<string>();
    }
    [Serializable] public sealed class TopicWorkshopEntry
    {
        public string Id = "";
        public string CreatorAccountId = "";
        public string CreatorName = "";
        public int CreatorLevel = 1;
        public string Name = "";
        public string LanguageCode = "";
        public int WordCount;
        public int DownloadCount;
        public int RecommendationCount;
        public bool IsRecommended;
        public string CreatedAt = "";
        public bool IsMine;
    }
    [Serializable] public sealed class TopicWorkshopRecommendationRequest
    {
        public bool IsRecommended;
    }
    [Serializable] public sealed class TopicWorkshopDetailResponse
    {
        public TopicWorkshopEntry Topic = new TopicWorkshopEntry();
        public string[] Words = Array.Empty<string>();
    }
    [Serializable] public sealed class TopicWorkshopListResponse
    {
        public TopicWorkshopEntry[] Items = Array.Empty<TopicWorkshopEntry>();
        public int Total;
        public int Offset;
        public int Limit;
        public int OwnCount;
        public TopicWorkshopPolicy Policy = new TopicWorkshopPolicy();
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
