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
        public CosmeticLoadout Cosmetics = new CosmeticLoadout();
        public string[] OwnedCosmetics = Array.Empty<string>();
    }
    [Serializable] public sealed class CosmeticLoadout
    {
        public string NicknameEffect = "";
        public string AvatarBackground = "";
        public string AvatarFrame = "";
        public string ChatBubble = "";
    }
    [Serializable] public sealed class CosmeticEquipRequest { public string Category = ""; public string ProductId = ""; }
    [Serializable] public sealed class UpdateProfileRequest { public string DisplayName = ""; public int AvatarColor; public long Accessory; }
    [Serializable] public sealed class PublicProfileData
    {
        public string AccountId = "";
        public string DisplayName = "";
        public int AvatarColor;
        public long Accessory;
        public int Level = 1;
        public CosmeticLoadout Cosmetics = new CosmeticLoadout();
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
        public bool CanViewDrawings;
        public string DrawingExpiresAt = "";
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
        public string HostAccountId = "";
        public MatchPlayerResult[] Players = Array.Empty<MatchPlayerResult>();
        public RoundDrawingArchiveData[] DrawingArchives = Array.Empty<RoundDrawingArchiveData>();
        public bool DrawingsIncomplete;
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
    [Serializable] public sealed class FriendData { public string AccountId = ""; public string DisplayName = ""; public int AvatarColor; public long Accessory; public int Level = 1; public CosmeticLoadout Cosmetics = new CosmeticLoadout(); }
    [Serializable] public sealed class FriendListResponse { public FriendData[] Friends = Array.Empty<FriendData>(); public FriendData[] Incoming = Array.Empty<FriendData>(); public FriendData[] Outgoing = Array.Empty<FriendData>(); }
    [Serializable] public sealed class FriendRequest { public string AccountId = ""; }
    [Serializable] public sealed class FriendRespondRequest { public string AccountId = ""; public bool Accept; }
    [Serializable] public sealed class SocialInboxResponse
    {
        public FriendListResponse Friends = new FriendListResponse();
        public RoomInvitationData[] RoomInvitations = Array.Empty<RoomInvitationData>();
        public int MailCount;
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
    [Serializable] public sealed class MailData { public string MailId = "", Title = "", Message = "", CreatedAt = "", ExpiresAt = ""; public int Coins, PaidGems; }
    [Serializable] public sealed class MailListResponse { public MailData[] Mails = Array.Empty<MailData>(); }
    [Serializable] public sealed class MailClaimRequest { public const int MAX_MAILS = 100; public string[] MailIds = Array.Empty<string>(); }
    [Serializable] public sealed class MailClaimResponse { public int ClaimedCount, Coins, PaidGems; public ProfileData Profile = new ProfileData(); }
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
        public const string DEFAULT_TOPIC_LANGUAGE = "ko";
        public static readonly string[] TOPIC_LANGUAGES = { "ko", "en", "ja", "zh-CN", "de", "fr", "es", "es-419", "pt", "it", "nl", "da", "no", "sv", "fi", "pl", "cs", "sk", "hu", "ro", "ru", "uk", "tr", "ar", "iw", "hi", "id", "th", "vi" };
        public static string NormalizeTopicLanguage(string code) => TryNormalizeTopicLanguage(code, out string language) ? language : DEFAULT_TOPIC_LANGUAGE;

        public static bool TryNormalizeTopicLanguage(string code, out string language)
        {
            string value = (code ?? "").Trim().Replace('_', '-');
            foreach (string supported in TOPIC_LANGUAGES)
                if (string.Equals(supported, value, StringComparison.OrdinalIgnoreCase)) { language = supported; return true; }
            switch (value.ToLowerInvariant())
            {
                case "ko-kr": language = "ko"; return true;
                case "en-us": case "en-gb": language = "en"; return true;
                case "ja-jp": language = "ja"; return true;
                case "zh": case "zh-hans": case "zh-sg": language = "zh-CN"; return true;
                case "de-de": language = "de"; return true;
                case "fr-fr": language = "fr"; return true;
                case "es-es": language = "es"; return true;
                case "es-mx": language = "es-419"; return true;
                case "pt-pt": language = "pt"; return true;
                case "nb": case "nn": case "nb-no": case "nn-no": case "no-no": language = "no"; return true;
                case "he": case "he-il": case "iw-il": language = "iw"; return true;
                case "hi-in": language = "hi"; return true;
                case "in": case "in-id": case "id-id": language = "id"; return true;
                // 기존 지역별 팩과 게시물은 다른 언어의 제시어로 바꾸지 않는다.
                case "zh-tw": case "zh-hant": language = "zh-TW"; return true;
                case "pt-br": language = "pt-BR"; return true;
                default: language = ""; return false;
            }
        }

        public static string[] TopicLanguageAliases(string code)
        {
            if (!TryNormalizeTopicLanguage(code, out string language)) return Array.Empty<string>();
            switch (language)
            {
                case "ko": return new[] { "ko", "ko-kr" };
                case "en": return new[] { "en", "en-us", "en-gb" };
                case "ja": return new[] { "ja", "ja-jp" };
                case "zh-CN": return new[] { "zh-cn", "zh", "zh-hans", "zh-sg" };
                case "de": return new[] { "de", "de-de" };
                case "fr": return new[] { "fr", "fr-fr" };
                case "es": return new[] { "es", "es-es" };
                case "es-419": return new[] { "es-419", "es-mx" };
                case "pt": return new[] { "pt", "pt-pt" };
                case "no": return new[] { "no", "nb", "nn", "nb-no", "nn-no", "no-no" };
                case "iw": return new[] { "iw", "he", "he-il", "iw-il" };
                case "hi": return new[] { "hi", "hi-in" };
                case "id": return new[] { "id", "in", "in-id", "id-id" };
                case "zh-TW": return new[] { "zh-tw", "zh-hant" };
                default: return new[] { language.ToLowerInvariant() };
            }
        }

        // 언어 코드가 없는 기존 기본 주제는 한국어로 본다.
        public static string TopicLanguageOf(string languageCode) => string.IsNullOrEmpty(languageCode) ? DEFAULT_TOPIC_LANGUAGE
            : TryNormalizeTopicLanguage(languageCode, out string language) ? language : languageCode;
        public string TopicLanguage = DEFAULT_TOPIC_LANGUAGE;
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
        public int RevealSeconds = 12;
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
        public bool HostHasPainterPass;
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
    [Serializable] public sealed class BlockListResponse { public string[] AccountIds = Array.Empty<string>(); }
    [Serializable] public sealed class PlayerReportRequest
    {
        public const string REASON_ABUSE = "Abuse", REASON_DRAWING = "Drawing", REASON_NICKNAME = "Nickname", REASON_SPAM = "Spam", REASON_TOPIC = "Topic", REASON_OTHER = "Other";
        public static readonly string[] REASONS = { REASON_ABUSE, REASON_DRAWING, REASON_NICKNAME, REASON_SPAM, REASON_TOPIC, REASON_OTHER };
        public const int MAX_CONTEXT_LENGTH = 500;
        public string TargetAccountId = "";
        public string Reason = "";
        public string Context = "";
        public string RoomId = "";
        public string WorkshopTopicId = "";
    }
    [Serializable] public sealed class AdminReportData
    {
        public string ReportId = "", ReporterAccountId = "", ReporterName = "", TargetAccountId = "", TargetName = "", Reason = "", Context = "";
        public string RoomId = "", WorkshopTopicId = "", Status = "", CreatedAt = "";
    }
    [Serializable] public sealed class AdminReportAction { public string Status = ""; }
    [Serializable] public sealed class AdminMailRequest
    {
        public const int MAX_TITLE_LENGTH = 40, MAX_MESSAGE_LENGTH = 500, MAX_COINS = 1000000, MAX_PAID_GEMS = 10000, MAX_EXPIRES_IN_DAYS = 365, MAX_RECIPIENTS = 1000;
        public string[] AccountIds = Array.Empty<string>();
        public bool SendToAll;
        public string Title = "", Message = "";
        public int Coins, PaidGems, ExpiresInDays = 30;
    }
    [Serializable] public sealed class AdminMailSendResponse { public int Created; }
    [Serializable] public sealed class AdminMailData { public string MailId = "", AccountId = "", AccountName = "", Title = "", Message = "", CreatedAt = "", ExpiresAt = ""; public int Coins, PaidGems, ClaimCount; }
    [Serializable] public sealed class LobbyChatMessage
    {
        public long Id;
        public string AccountId = "";
        public string DisplayName = "";
        public int Level = 1;
        public CosmeticLoadout Cosmetics = new CosmeticLoadout();
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
    [Serializable] public sealed class ClientVersionResponse { public bool UpdateRequired; public bool UpdateAvailable; public string LatestVersion = ""; public string StoreUrl = ""; }
    [Serializable] public sealed class ClientVersionPolicy { public string Platform = ""; public string MinimumVersion = ""; public string LatestVersion = ""; public string StoreUrl = ""; }
    // 버전 정책은 스토어 배포 플랫폼에만 적용한다. 버전은 0.1.0 형식의 세 자리 숫자다.
    public static class ClientVersionRules
    {
        public const string PLATFORM_HEADER = "X-DrawLiar-Platform", VERSION_HEADER = "X-DrawLiar-Version";
        public const string PLATFORM_ANDROID = "Android", PLATFORM_WINDOWS = "Windows";
        public static readonly string[] PLATFORMS = { PLATFORM_ANDROID, PLATFORM_WINDOWS };

        public static bool TryParse(string value, out Version version) =>
            Version.TryParse(value ?? "", out version) && version.Build >= 0 && version.Revision < 0;

        // 기준 버전이 비어 있으면 적용하지 않고, 현재 버전을 해석할 수 없으면 오래된 버전으로 본다.
        public static bool IsOlder(string current, string target) =>
            TryParse(target, out var required) && (!TryParse(current, out var installed) || installed < required);
    }
}
