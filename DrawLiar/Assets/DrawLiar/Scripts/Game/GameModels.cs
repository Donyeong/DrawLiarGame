#nullable disable
using System;
using System.Linq;

namespace DrawLiar
{
    public enum GamePhase { Lobby, RoleReveal, Drawing, Discussion, Rebuttal, Voting, LiarReveal, Guessing, RoundResults, MatchResults }
    public enum DrawingMode { Relay, Individual }
    public enum LiarMode { Classic = 0, Mismatch = 1, Optional = 2 }
    public enum VictoryMode { RoundCount, TargetScore }
    public enum GuessOutcome { Hidden, Correct, Incorrect, Unanswered }

    [Serializable]
    public sealed class RoomSettings
    {
        public int MaxPlayers = GameRules.MAX_PLAYERS;
        public int LiarCount = 1;
        public LiarMode LiarMode;
        public int RoundCount = 5;
        public int TargetScore = 10;
        public DrawingMode Mode;
        public VictoryMode Victory;
        public int RoleSeconds = 6;
        public int DrawSeconds = 25;
        public int DiscussionSeconds = 45;
        public int RebuttalSeconds = 20;
        public int VoteSeconds = 20;
        public int RevealSeconds = 5;
        public int GuessSeconds = 20;
        public int ResultSeconds = 10;
        public string[] Topics;
        public string RoomName = "우리들의 그림방";
        public bool IsPrivate;
        public bool AllowMidRoundJoin = true;

        public RoomSettings Copy()
        {
            var copy = (RoomSettings)MemberwiseClone();
            copy.Topics = Topics?.ToArray();
            return copy;
        }

        public void Validate()
        {
            MaxPlayers = GameRules.MAX_PLAYERS;
            if (!Enum.IsDefined(typeof(LiarMode), LiarMode)) LiarMode = DrawLiar.LiarMode.Classic;
            LiarCount = LiarMode == DrawLiar.LiarMode.Classic ? Clamp(LiarCount, 1, MaxPlayers - 1) : 1;
            RoundCount = Clamp(RoundCount, 1, 30);
            TargetScore = Clamp(TargetScore, 1, 1000);
            RoleSeconds = Clamp(RoleSeconds, 3, 30);
            DrawSeconds = Clamp(DrawSeconds, 5, 180);
            DiscussionSeconds = Clamp(DiscussionSeconds, 5, 300);
            RebuttalSeconds = Clamp(RebuttalSeconds, 0, 180);
            VoteSeconds = Clamp(VoteSeconds, 5, 120);
            RevealSeconds = Clamp(RevealSeconds, 3, 30);
            GuessSeconds = Clamp(GuessSeconds, 5, 120);
            ResultSeconds = Clamp(ResultSeconds, 5, 60);
            if (!Enum.IsDefined(typeof(DrawingMode), Mode)) Mode = DrawingMode.Relay;
            if (!Enum.IsDefined(typeof(VictoryMode), Victory)) Victory = VictoryMode.RoundCount;
            RoomName = GameRules.CleanText(RoomName, 30, "우리들의 그림방");
            Topics = Topics?.Select(value => GameRules.CleanText(value, 40, ""))
                .Where(value => value.Length > 0).Distinct().Take(128).ToArray();
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    [Serializable]
    public sealed class PlayerView
    {
        public int Id;
        public string AccountId = "";
        public string Name;
        public int Level = 1;
        public int Score;
        public int RoundPoints;
        public bool IsSpectator;
        public bool IsConnected;
        public bool IsLiar;
        public bool IsCaught;
        public int AvatarColor;
        public long Accessory;
        public bool HasVoted;
        public bool HasJudged;
        public bool HasGuessed;
        public int VoteCount;
        public string Guess;
        public GuessOutcome GuessOutcome;
    }

    public sealed class CompletedMatchData
    {
        public string MatchId = "";
        public DrawingMode Mode;
        public int RoundCount;
        public CompletedMatchPlayerData[] Players = Array.Empty<CompletedMatchPlayerData>();
    }

    public sealed class CompletedMatchPlayerData
    {
        public int PlayerId;
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

    [Serializable]
    public sealed class RoomSnapshot
    {
        public string MatchId = "";
        public bool IsMatchComplete;
        public bool LocalRewardEligible;
        public GamePhase Phase;
        public int Round;
        public int ArtistId = -1;
        public string Topic = "";
        public string Word = "";
        public string MismatchWord = "";
        public bool LocalIsLiar;
        public int RevealedLiarCount = -1;
        public bool LocalIsSpectator;
        public int LocalPlayerId = -1;
        public int HostPlayerId;
        public bool IsHost;
        public bool CanStart;
        public int CanvasVersion;
        public int DrawingEpoch;
        public int[] DrawingOrder = Array.Empty<int>();
        public bool HasAccused;
        public int AccusedPlayerId = -1;
        public int BallotVersion;
        public int LocalVoteTargetId = -1;
        public int ApprovalCount;
        public int RejectionCount;
        public int JudgmentVoterCount;
        public int JudgmentVotesCast;
        public bool LocalJudgmentApprove;
        public bool IsJudgmentCoinToss;
        public bool JudgmentCoinApproved;
        public float RemainingSeconds;
        public RoomSettings Settings = new RoomSettings();
        public string[] AvailableTopics = Array.Empty<string>();
        public PlayerView[] Players = Array.Empty<PlayerView>();
        public int[] Winners = Array.Empty<int>();
        public string Summary = "";
    }

    [Serializable]
    public struct DrawStroke
    {
        public float X1, Y1, X2, Y2, Size;
        public byte R, G, B;
        public bool Eraser;
        public int CanvasVersion;
        public int AuthorPlayerId;
    }

    [Serializable]
    public struct ChatLine
    {
        public int PlayerId;
        public string Name;
        public int Level;
        public string Text;
    }

    [Serializable]
    public sealed class ScoreRules
    {
        public int LiarUncaught = 2;
        public int LiarCorrectGuess = 3;
        public int CitizenCorrectVote = 2;
    }

    [Serializable]
    public sealed class TopicData
    {
        public string Name;
        public string[] Words;
        public string WorkshopId = "";
        public string LanguageCode = "";
    }

    [Serializable]
    public sealed class GameData
    {
        public ScoreRules Scoring = new ScoreRules();
        public TopicData[] Topics = Array.Empty<TopicData>();
    }
}
