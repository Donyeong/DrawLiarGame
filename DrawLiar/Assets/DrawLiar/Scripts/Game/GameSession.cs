#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace DrawLiar
{
    public sealed class GameSession
    {
        private sealed class Participant
        {
            public int Id;
            public string Name, Guess = "";
            public int Score, RoundPoints, Color, Accessory;
            public bool Connected = true, Spectator, SpectatorOnly, Liar, Caught, Guessed;
            public int? Vote;
            public int RoundsPlayed, CitizenRounds, LiarRounds, CorrectVotes, CorrectGuesses;
        }

        private readonly Dictionary<int, Participant> players = new Dictionary<int, Participant>();
        private readonly Random random;
        private GameData data;
        private double deadline;
        private int turnIndex;
        private int[] turns = Array.Empty<int>();
        private int[] winners = Array.Empty<int>();
        private bool matchDecided;
        private bool _completionEmitted;
        private string _matchId = "";
        private int _completedRounds;
        private int[] _roundParticipants = Array.Empty<int>();
        private string topic = "", word = "", summary = GameRules.TieRule;

        public GamePhase Phase { get; private set; }
        public RoomSettings Settings { get; private set; }
        public int Round { get; private set; }
        public int CanvasVersion { get; private set; }
        public int ArtistId => Phase == GamePhase.Drawing && turnIndex < turns.Length ? turns[turnIndex] : -1;
        public int ConnectedCount => players.Values.Count(player => player.Connected);
        public int ActiveCount => players.Values.Count(player => player.Connected && !player.Spectator);
        public int SpectatorCount => players.Values.Count(player => player.Connected && player.Spectator);
        public bool CanStart => (Phase == GamePhase.Lobby || Phase == GamePhase.MatchResults)
            && ActiveCount >= GameRules.MIN_START_PLAYERS;
        public event Action Changed;
        public event Action CanvasCleared;
        public event Action<CompletedMatchData> MatchCompleted;

        public GameSession(RoomSettings settings, GameData gameData, int? seed = null)
        {
            Settings = settings.Copy();
            Settings.Validate();
            data = gameData;
            Settings.Topics ??= AvailableTopics().Select(entry => entry.Name).ToArray();
            random = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public bool Contains(int id) => players.TryGetValue(id, out var player) && player.Connected;
        public bool HasParticipant(int id) => players.ContainsKey(id);
        public bool IsSpectator(int id) => players.TryGetValue(id, out var player) && player.Spectator;

        public bool Join(int id, string name, int color, int accessory, bool spectator = false, bool spectatorOnly = false)
        {
            if (players.TryGetValue(id, out var existing))
            {
                if (existing.Connected) return false;
                existing.Connected = true;
                Changed?.Invoke();
                return true;
            }
            spectator |= spectatorOnly || Phase != GamePhase.Lobby && Phase != GamePhase.MatchResults;
            if (players.Count >= 64 || (spectator
                ? players.Values.Count(player => player.Spectator) >= 32
                : players.Values.Count(player => !player.Spectator) >= Settings.MaxPlayers)) return false;
            players[id] = new Participant
            {
                Id = id, Name = GameRules.CleanText(name, 16, "그림친구"),
                Color = Math.Max(0, Math.Min(7, color)), Accessory = AvatarParts.Sanitize(accessory),
                Spectator = spectator, SpectatorOnly = spectatorOnly
            };
            Changed?.Invoke();
            return true;
        }

        public void UpdateProfile(int id, string name, int color, int accessory)
        {
            if (!players.TryGetValue(id, out var player)) return;
            if (Phase == GamePhase.Lobby)
            {
                player.Name = GameRules.CleanText(name, 16, "그림친구");
                player.Color = Math.Max(0, Math.Min(7, color));
                player.Accessory = AvatarParts.Sanitize(accessory);
            }
            Changed?.Invoke();
        }

        public string PlayerName(int id) => players.TryGetValue(id, out var player) ? player.Name : "그림친구";

        public void Disconnect(int id, double now)
        {
            if (!players.TryGetValue(id, out var player)) return;
            if (Phase == GamePhase.Lobby) players.Remove(id);
            else
            {
                player.Connected = false;
                if (matchDecided) { Changed?.Invoke(); return; }
                if (!player.Spectator && ActivePlayers().Count() < 2)
                {
                    summary = "참가자가 부족해 경기를 종료했어요. 현재 점수로 순위를 확인하세요.";
                    FinishMatch();
                }
                else if (ArtistId == id) NextArtist(now);
                else if (Phase == GamePhase.Voting && ActivePlayers().All(voter => voter.Vote.HasValue)) RevealLiars(now);
                else if (Phase == GamePhase.Guessing && ActivePlayers().Where(liar => liar.Liar).All(liar => liar.Guessed)) ScoreRound(now);
            }
            Changed?.Invoke();
        }

        public bool Configure(RoomSettings settings)
        {
            if (Phase != GamePhase.Lobby && Phase != GamePhase.MatchResults) return false;
            var valid = settings.Copy();
            valid.Validate();
            var knownTopics = KnownTopicNames();
            valid.Topics ??= knownTopics.Take(128).ToArray();
            if (valid.Topics.Length == 0 || valid.Topics.Any(value => !knownTopics.Contains(value))) return false;
            Settings = valid;
            Changed?.Invoke();
            return true;
        }

        public bool Start(double now, GameData gameData = null)
        {
            if (!CanStart) return false;
            if (gameData != null) data = gameData;
            if (!AvailableTopics().Any()) return false;
            foreach (var id in players.Values.Where(player => !player.Connected).Select(player => player.Id).ToArray()) players.Remove(id);
            AssignSeats();
            Settings.LiarCount = Math.Min(Settings.LiarCount, ActiveCount - 1);
            foreach (var player in players.Values)
            {
                player.Score = 0;
                player.RoundsPlayed = player.CitizenRounds = player.LiarRounds = player.CorrectVotes = player.CorrectGuesses = 0;
            }
            _matchId = Guid.NewGuid().ToString();
            _completedRounds = 0;
            _completionEmitted = false;
            Round = 0;
            winners = Array.Empty<int>();
            matchDecided = false;
            BeginRound(now);
            return true;
        }

        public void ReturnToLobby()
        {
            Phase = GamePhase.Lobby;
            Round = 0;
            word = topic = "";
            summary = GameRules.TieRule;
            winners = Array.Empty<int>();
            matchDecided = false;
            foreach (var id in players.Values.Where(player => !player.Connected).Select(player => player.Id).ToArray()) players.Remove(id);
            AssignSeats();
            foreach (var player in players.Values)
            {
                player.Liar = player.Caught = player.Guessed = false;
                player.Score = player.RoundPoints = 0;
                player.Vote = null;
                player.Guess = "";
            }
            ClearCanvas();
            Changed?.Invoke();
        }

        public bool EndTurn(int id, double now)
        {
            if (Phase != GamePhase.Drawing || ArtistId != id) return false;
            NextArtist(now);
            return true;
        }

        public bool Vote(int id, int target, double now)
        {
            if (Phase != GamePhase.Voting || !IsActive(id) || !IsActive(target) || id == target) return false;
            var player = players[id];
            if (player.Vote.HasValue) return false;
            player.Vote = target;
            if (ActivePlayers().All(voter => voter.Vote.HasValue)) RevealLiars(now);
            else Changed?.Invoke();
            return true;
        }

        public bool Guess(int id, string answer, double now)
        {
            if (Phase != GamePhase.Guessing || !IsActive(id)) return false;
            var player = players[id];
            if (!player.Liar || player.Guessed || string.IsNullOrWhiteSpace(answer)) return false;
            player.Guess = GameRules.CleanText(answer, 40);
            player.Guessed = true;
            if (ActivePlayers().Where(liar => liar.Liar).All(liar => liar.Guessed)) ScoreRound(now);
            else Changed?.Invoke();
            return true;
        }

        public void Tick(double now)
        {
            if (Phase == GamePhase.Lobby || Phase == GamePhase.MatchResults || now < deadline) return;
            switch (Phase)
            {
                case GamePhase.RoleReveal:
                    turnIndex = -1;
                    NextArtist(now);
                    break;
                case GamePhase.Drawing: NextArtist(now); break;
                case GamePhase.Discussion:
                    if (Settings.RebuttalSeconds > 0) SetPhase(GamePhase.Rebuttal, Settings.RebuttalSeconds, now);
                    else SetPhase(GamePhase.Voting, Settings.VoteSeconds, now);
                    break;
                case GamePhase.Rebuttal: SetPhase(GamePhase.Voting, Settings.VoteSeconds, now); break;
                case GamePhase.Voting: RevealLiars(now); break;
                case GamePhase.LiarReveal: SetPhase(GamePhase.Guessing, Settings.GuessSeconds, now); break;
                case GamePhase.Guessing: ScoreRound(now); break;
                case GamePhase.RoundResults:
                    if (matchDecided) FinishMatch();
                    else if (ActivePlayers().Count() < 2) FinishMatch();
                    else BeginRound(now);
                    break;
            }
        }

        public RoomSnapshot Snapshot(int id, int hostId, double now)
        {
            players.TryGetValue(id, out var local);
            bool reveal = Phase >= GamePhase.LiarReveal;
            bool result = Phase >= GamePhase.RoundResults;
            bool spectator = local == null || local.Spectator;
            return new RoomSnapshot
            {
                Phase = Phase, Round = Round, ArtistId = ArtistId, Topic = topic,
                Word = result || (!spectator && !local.Liar && Phase != GamePhase.Lobby) ? word : "",
                LocalIsLiar = !spectator && local.Liar, LocalIsSpectator = spectator,
                LocalPlayerId = id, IsHost = id == hostId, CanStart = CanStart, CanvasVersion = CanvasVersion,
                RemainingSeconds = Phase == GamePhase.Lobby || Phase == GamePhase.MatchResults ? 0 : (float)Math.Max(0, deadline - now),
                Settings = Settings.Copy(), AvailableTopics = KnownTopicNames(), Winners = winners.ToArray(), Summary = summary,
                Players = players.Values.OrderBy(player => player.Id).Select(player => new PlayerView
                {
                    Id = player.Id, Name = player.Name, Score = player.Score, RoundPoints = result ? player.RoundPoints : 0,
                    IsSpectator = player.Spectator, IsConnected = player.Connected, IsLiar = reveal && player.Liar,
                    IsCaught = reveal && player.Caught, AvatarColor = player.Color, Accessory = player.Accessory,
                    HasVoted = player.Vote.HasValue, HasGuessed = reveal && player.Guessed,
                    VoteCount = reveal ? players.Values.Count(voter => voter.Vote == player.Id) : 0,
                    Guess = result && player.Liar ? player.Guess : ""
                }).ToArray()
            };
        }

        private IEnumerable<Participant> ActivePlayers() => players.Values.Where(player => player.Connected && !player.Spectator);
        private void AssignSeats()
        {
            var eligible = players.Values.Where(player => !player.SpectatorOnly)
                .OrderBy(player => player.Spectator).ThenBy(player => player.Id).ToArray();
            foreach (var player in players.Values) player.Spectator = true;
            for (int index = 0; index < Math.Min(Settings.MaxPlayers, eligible.Length); index++) eligible[index].Spectator = false;
        }

        private bool IsActive(int id) => players.TryGetValue(id, out var player) && player.Connected && !player.Spectator;
        private static bool IsValidTopic(TopicData entry) => entry != null && !string.IsNullOrWhiteSpace(entry.Name)
            && entry.Words != null && entry.Words.Any(value => !string.IsNullOrWhiteSpace(value));

        private string[] KnownTopicNames() => (data?.Topics ?? Array.Empty<TopicData>()).Where(IsValidTopic)
            .Select(entry => entry.Name).Distinct().ToArray();

        private IEnumerable<TopicData> AvailableTopics() => (data?.Topics ?? Array.Empty<TopicData>())
            .Where(entry => IsValidTopic(entry) && (Settings.Topics == null || Settings.Topics.Contains(entry.Name)));

        private void BeginRound(double now)
        {
            Round++;
            summary = GameRules.TieRule;
            var available = AvailableTopics().ToArray();
            var selected = available[random.Next(available.Length)];
            topic = selected.Name;
            var words = selected.Words.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            word = words[random.Next(words.Length)];
            foreach (var player in players.Values)
            {
                player.Liar = player.Caught = player.Guessed = false;
                player.Guess = "";
                player.Vote = null;
                player.RoundPoints = 0;
            }
            var participants = ActivePlayers().ToArray();
            Shuffle(participants);
            foreach (var player in participants.Take(Math.Min(Settings.LiarCount, participants.Length - 1))) player.Liar = true;
            _roundParticipants = participants.Select(player => player.Id).ToArray();
            Shuffle(participants);
            turns = participants.Select(player => player.Id).ToArray();
            turnIndex = -1;
            ClearCanvas();
            SetPhase(GamePhase.RoleReveal, Settings.RoleSeconds, now);
        }

        private void Shuffle<T>(T[] values)
        {
            for (var index = values.Length - 1; index > 0; index--)
            {
                var other = random.Next(index + 1);
                var temporary = values[index];
                values[index] = values[other];
                values[other] = temporary;
            }
        }

        private void NextArtist(double now)
        {
            do { turnIndex++; } while (turnIndex < turns.Length && !IsActive(turns[turnIndex]));
            if (turnIndex >= turns.Length) SetPhase(GamePhase.Discussion, Settings.DiscussionSeconds, now);
            else
            {
                if (Settings.Mode == DrawingMode.Individual) ClearCanvas();
                SetPhase(GamePhase.Drawing, Settings.DrawSeconds, now);
            }
        }

        private void RevealLiars(double now)
        {
            var caught = GameRules.CaughtPlayers(players.Values.Where(player => !player.Spectator && player.Vote.HasValue).Select(player => player.Vote.Value));
            foreach (var player in players.Values) player.Caught = caught.Contains(player.Id);
            summary = caught.Count == 0 ? "투표가 없어 아무도 지목되지 않았어요. 라이어 모두에게 정답 기회가 있어요."
                : "최다 득표한 친구들이 지목되었어요. 들킨 라이어도 정답을 맞히면 점수를 얻어요.";
            SetPhase(GamePhase.LiarReveal, Settings.RevealSeconds, now);
        }

        private void ScoreRound(double now)
        {
            var scoring = data.Scoring ?? new ScoreRules();
            foreach (var player in players.Values.Where(player => !player.Spectator && player.Connected))
            {
                bool correctVote = player.Vote.HasValue && players.TryGetValue(player.Vote.Value, out var target) && target.Liar;
                bool correctGuess = player.Guessed && GameRules.NormalizeGuess(player.Guess) == GameRules.NormalizeGuess(word);
                player.RoundPoints = GameRules.RoundScore(player.Liar, player.Caught, correctGuess, correctVote, scoring);
                player.Score += player.RoundPoints;
            }
            foreach (int id in _roundParticipants)
            {
                if (!players.TryGetValue(id, out var participant)) continue;
                participant.RoundsPlayed++;
                if (participant.Liar) participant.LiarRounds++;
                else participant.CitizenRounds++;
                if (!participant.Liar && participant.Vote.HasValue
                    && players.TryGetValue(participant.Vote.Value, out var target) && target.Liar) participant.CorrectVotes++;
                if (participant.Liar && participant.Guessed
                    && GameRules.NormalizeGuess(participant.Guess) == GameRules.NormalizeGuess(word)) participant.CorrectGuesses++;
            }
            _completedRounds++;
            if (Settings.Victory == VictoryMode.RoundCount ? Round >= Settings.RoundCount
                : ActivePlayers().Any(player => player.Score >= Settings.TargetScore)) DecideWinners();
            summary = "정답은 “" + word + "”! 시민은 정확한 투표, 라이어는 생존과 정답으로 득점해요.";
            SetPhase(GamePhase.RoundResults, Settings.ResultSeconds, now);
            if (matchDecided) PublishMatchCompleted();
        }

        private void FinishMatch()
        {
            if (!matchDecided) DecideWinners();
            Phase = GamePhase.MatchResults;
            if (!summary.StartsWith("참가자")) summary = winners.Length > 1 ? "동점 공동 우승! 함께 축하해요." : "최고 점수의 주인공이 정해졌어요!";
            PublishMatchCompleted();
            Changed?.Invoke();
        }

        private void PublishMatchCompleted()
        {
            if (!_completionEmitted && _completedRounds > 0)
            {
                _completionEmitted = true;
                var participants = players.Values.Where(player => player.RoundsPlayed > 0).ToArray();
                MatchCompleted?.Invoke(new CompletedMatchData
                {
                    MatchId = _matchId, Mode = Settings.Mode, RoundCount = _completedRounds,
                    Players = participants.OrderBy(player => player.Id).Select(player => new CompletedMatchPlayerData
                    {
                        PlayerId = player.Id, Score = player.Score,
                        Rank = 1 + participants.Count(other => other.Score > player.Score), Won = winners.Contains(player.Id),
                        RoundsPlayed = player.RoundsPlayed, CitizenRounds = player.CitizenRounds, LiarRounds = player.LiarRounds,
                        CorrectVotes = player.CorrectVotes, CorrectGuesses = player.CorrectGuesses
                    }).ToArray()
                });
            }
        }

        private void DecideWinners()
        {
            var eligible = ActivePlayers().ToArray();
            int maximum = eligible.Length == 0 ? 0 : eligible.Max(player => player.Score);
            winners = eligible.Where(player => player.Score == maximum).Select(player => player.Id).ToArray();
            matchDecided = true;
        }

        private void SetPhase(GamePhase phase, int seconds, double now)
        {
            Phase = phase;
            deadline = now + seconds;
            Changed?.Invoke();
        }

        private void ClearCanvas()
        {
            CanvasVersion++;
            CanvasCleared?.Invoke();
        }
    }
}
