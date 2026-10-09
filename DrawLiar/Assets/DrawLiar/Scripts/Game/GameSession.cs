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
            public int Score, RoundPoints, Color;
            public int Level = 1;
            public long Accessory;
            public bool Connected = true, SeatReserved = true, Spectator, SpectatorOnly, Liar, Caught, Guessed;
            public int? Vote;
            public bool? Judgment;
            public bool CorrectJudgment;
            public int RoundsPlayed, CitizenRounds, LiarRounds, CorrectVotes, CorrectGuesses;
            public long WeightedRoundParticipants, WeightedRoundScore;
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
        private bool _hasAccused;
        private int _accusedPlayerId = -1;
        private int _judgmentTieCount;
        private bool _isJudgmentCoinToss, _judgmentCoinApproved;
        private int[] _judgmentCoinCorrectVoters = Array.Empty<int>();
        private string topic = "", word = "", summary = GameRules.TieRule;
        private string _mismatchWord = "";

        public GamePhase Phase { get; private set; }
        public RoomSettings Settings { get; private set; }
        public int Round { get; private set; }
        public int CanvasVersion { get; private set; }
        public int DrawingEpoch { get; private set; }
        public int BallotVersion { get; private set; }
        public int ArtistId => Phase == GamePhase.Drawing && turnIndex < turns.Length ? turns[turnIndex] : -1;
        public int ConnectedCount => players.Values.Count(player => player.Connected);
        public int ActiveCount => players.Values.Count(player => player.Connected && !player.Spectator);
        public int SpectatorCount => players.Values.Count(player => player.Connected && player.Spectator);
        public bool CanStart => (Phase == GamePhase.Lobby || Phase == GamePhase.MatchResults)
            && ActiveCount >= GameRules.MinimumPlayers(Settings.LiarMode) && AvailableTopics().Any();
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
        public bool HoldsPlayerSeat(int id) => players.TryGetValue(id, out var player) && !player.Spectator && (player.Connected || player.SeatReserved);
        public bool HoldsSpectatorSeat(int id) => players.TryGetValue(id, out var player) && player.Spectator && (player.Connected || player.SeatReserved);

        public bool Join(int id, string name, int color, long accessory, bool spectator = false, bool spectatorOnly = false, int level = 1)
        {
            if (players.TryGetValue(id, out var existing))
            {
                if (existing.Connected) return false;
                if (!existing.Spectator && !existing.SeatReserved && ReservedPlayerCount() >= Settings.MaxPlayers) return false;
                if (existing.Spectator && !existing.SeatReserved && ReservedSpectatorCount() >= 32) return false;
                existing.Connected = true;
                existing.SeatReserved = true;
                existing.Level = Math.Max(1, Math.Min(AccountLevelRules.MAX_LEVEL, level));
                AddRoundParticipant(existing);
                Changed?.Invoke();
                return true;
            }
            spectator |= spectatorOnly || IsInProgress() && !Settings.AllowMidRoundJoin;
            if (players.Count >= 64 || (spectator
                ? ReservedSpectatorCount() >= 32
                : ReservedPlayerCount() >= Settings.MaxPlayers)) return false;
            var participant = new Participant
            {
                Id = id, Name = GameRules.CleanText(name, 16, "그림친구"),
                Color = Math.Max(0, Math.Min(7, color)), Accessory = AvatarParts.Sanitize(accessory),
                Level = Math.Max(1, Math.Min(AccountLevelRules.MAX_LEVEL, level)),
                Spectator = spectator, SpectatorOnly = spectatorOnly
            };
            players[id] = participant;
            AddRoundParticipant(participant);
            Changed?.Invoke();
            return true;
        }

        public void UpdateProfile(int id, string name, int color, long accessory, int level = 1)
        {
            if (!players.TryGetValue(id, out var player)) return;
            player.Name = GameRules.CleanText(name, 16, "그림친구");
            player.Color = Math.Max(0, Math.Min(7, color));
            player.Accessory = AvatarParts.Sanitize(accessory);
            player.Level = Math.Max(1, Math.Min(AccountLevelRules.MAX_LEVEL, level));
            Changed?.Invoke();
        }

        public string PlayerName(int id) => players.TryGetValue(id, out var player) ? player.Name : "그림친구";
        public int PlayerLevel(int id) => players.TryGetValue(id, out var player) ? player.Level : 1;

        public void Disconnect(int id, double now, bool reserveSeat = true)
        {
            if (!players.TryGetValue(id, out var player)) return;
            if (Phase == GamePhase.Lobby) players.Remove(id);
            else
            {
                player.Connected = false;
                player.SeatReserved = reserveSeat;
                if (matchDecided) { Changed?.Invoke(); return; }
                if (!player.Spectator && ActivePlayers().Count() < 2)
                {
                    summary = "참가자가 부족해 경기를 종료했어요. 현재 점수로 순위를 확인하세요.";
                    FinishMatch();
                }
                else if (ArtistId == id) NextArtist(now);
                else if (Phase == GamePhase.Discussion)
                {
                    foreach (var voter in players.Values.Where(voter => voter.Vote == id)) voter.Vote = null;
                    if (AllNominationsSubmitted()) ResolveNomination(now);
                }
                else if (Phase == GamePhase.Rebuttal)
                {
                    if (_hasAccused && _accusedPlayerId == id)
                        BeginDiscussion(now, "지목된 친구의 연결이 끊겨 토론과 지목 투표를 다시 시작해요.");
                    else if (!_isJudgmentCoinToss && JudgmentVoters().All(voter => voter.Judgment.HasValue)) ResolveJudgment(now);
                }
                else if (Phase == GamePhase.Guessing && ActivePlayers().Where(liar => liar.Liar).All(liar => liar.Guessed)) ScoreRound(now);
            }
            Changed?.Invoke();
        }

        public bool ReleaseSeat(int id)
        {
            if (!players.TryGetValue(id, out var player) || player.Connected || !player.SeatReserved) return false;
            player.SeatReserved = false;
            Changed?.Invoke();
            return true;
        }

        private bool IsInProgress() => Phase != GamePhase.Lobby && Phase != GamePhase.MatchResults;
        private int ReservedPlayerCount() => players.Values.Count(player => !player.Spectator && (player.Connected || player.SeatReserved));
        private int ReservedSpectatorCount() => players.Values.Count(player => player.Spectator && (player.Connected || player.SeatReserved));
        private void AddRoundParticipant(Participant player)
        {
            if (player.Spectator || !IsInProgress() || Phase >= GamePhase.RoundResults || _roundParticipants.Contains(player.Id)) return;
            _roundParticipants = _roundParticipants.Concat(new[] { player.Id }).ToArray();
            if (Phase == GamePhase.RoleReveal || Phase == GamePhase.Drawing) turns = turns.Concat(new[] { player.Id }).ToArray();
        }

        public bool CanConfigure(RoomSettings settings, GameData gameData = null) => TryConfiguration(settings, gameData, out _);

        public bool Configure(RoomSettings settings, GameData gameData = null)
        {
            if (!TryConfiguration(settings, gameData, out var valid)) return false;
            data = gameData ?? data;
            Settings = valid;
            Changed?.Invoke();
            return true;
        }

        private bool TryConfiguration(RoomSettings settings, GameData gameData, out RoomSettings valid)
        {
            valid = null;
            if (settings == null || Phase != GamePhase.Lobby && Phase != GamePhase.MatchResults) return false;
            var candidate = gameData ?? data;
            valid = settings.Copy();
            valid.Validate();
            var topics = (candidate?.Topics ?? Array.Empty<TopicData>()).Where(IsValidTopic).ToArray();
            var knownTopics = topics.Select(topic => topic.Name).Distinct().ToArray();
            valid.Topics ??= knownTopics.Take(128).ToArray();
            if (valid.Topics.Length == 0 || valid.Topics.Any(value => !knownTopics.Contains(value))) return false;
            var selected = valid;
            return topics.Any(topic => selected.Topics.Contains(topic.Name)
                && (selected.LiarMode != LiarMode.Mismatch || MismatchWords(topic).Length >= 2));
        }

        public bool Start(double now, GameData gameData = null)
        {
            if (Phase != GamePhase.Lobby && Phase != GamePhase.MatchResults || ActiveCount < GameRules.MinimumPlayers(Settings.LiarMode)) return false;
            if (gameData != null) data = gameData;
            if (!AvailableTopics().Any()) return false;
            foreach (var id in players.Values.Where(player => !player.Connected).Select(player => player.Id).ToArray()) players.Remove(id);
            AssignSeats();
            Settings.LiarCount = Math.Min(Settings.LiarCount, ActiveCount - 1);
            foreach (var player in players.Values)
            {
                player.Score = 0;
                player.RoundsPlayed = player.CitizenRounds = player.LiarRounds = player.CorrectVotes = player.CorrectGuesses = 0;
                player.WeightedRoundParticipants = player.WeightedRoundScore = 0;
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
            word = topic = _mismatchWord = "";
            summary = GameRules.TieRule;
            winners = Array.Empty<int>();
            matchDecided = false;
            _matchId = "";
            _completionEmitted = false;
            _judgmentTieCount = 0;
            ResetBallots();
            foreach (var id in players.Values.Where(player => !player.Connected).Select(player => player.Id).ToArray()) players.Remove(id);
            AssignSeats();
            foreach (var player in players.Values)
            {
                player.Liar = player.Caught = player.Guessed = false;
                player.Score = player.RoundPoints = 0;
                player.WeightedRoundParticipants = player.WeightedRoundScore = 0;
                player.Vote = null;
                player.Judgment = null;
                player.CorrectJudgment = false;
                player.Guess = "";
            }
            DrawingEpoch = CanvasVersion + 1;
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
            if (Phase != GamePhase.Discussion || !IsActive(id) || !IsNominationTarget(target) || id == target) return false;
            if (now >= deadline) { Tick(now); return false; }
            var player = players[id];
            if (player.Vote == target) return true;
            player.Vote = target;
            if (AllNominationsSubmitted()) ResolveNomination(now);
            else Changed?.Invoke();
            return true;
        }

        public bool Judge(int id, int target, bool approve, double now)
        {
            if (Phase != GamePhase.Rebuttal || !_hasAccused || target != _accusedPlayerId
                || id == target || !IsActive(id) || !IsNominationTarget(target)) return false;
            if (now >= deadline) { Tick(now); return false; }
            if (_isJudgmentCoinToss) return false;
            var player = players[id];
            if (player.Judgment.HasValue) return false;
            player.Judgment = approve;
            if (JudgmentVoters().All(voter => voter.Judgment.HasValue)) ResolveJudgment(now);
            else Changed?.Invoke();
            return true;
        }

        public bool Guess(int id, string answer, double now)
        {
            if (Phase != GamePhase.Guessing || !IsActive(id)) return false;
            var player = players[id];
            if (!player.Liar || player.Guessed) return false;
            if (now >= deadline) { Tick(now); return false; }
            string clean = GameRules.CleanText(answer, 40);
            if (clean.Length == 0) return false;
            player.Guess = clean;
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
                case GamePhase.Discussion: ResolveNomination(now); break;
                case GamePhase.Rebuttal: ResolveJudgment(now); break;
                case GamePhase.LiarReveal:
                    if (ActivePlayers().Any(player => player.Liar)) SetPhase(GamePhase.Guessing, Settings.GuessSeconds, now);
                    else ScoreRound(now);
                    break;
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
            bool showVoteCount = reveal || Phase == GamePhase.Discussion || Phase == GamePhase.Voting;
            bool result = Phase >= GamePhase.RoundResults;
            bool mismatch = Settings.LiarMode == LiarMode.Mismatch;
            bool spectator = local == null || local.Spectator;
            var judgmentVoters = _hasAccused ? JudgmentVoters().ToArray() : Array.Empty<Participant>();
            return new RoomSnapshot
            {
                MatchId = _matchId, IsMatchComplete = matchDecided && _completedRounds > 0,
                LocalRewardEligible = local != null && local.WeightedRoundParticipants > 0,
                Phase = Phase, Round = Round, ArtistId = ArtistId, Topic = topic,
                Word = PlayerWord(local, result),
                MismatchWord = result && mismatch ? _mismatchWord : "",
                LocalIsLiar = !spectator && local.Liar && (!mismatch || reveal), LocalIsSpectator = spectator,
                RevealedLiarCount = reveal ? players.Values.Count(player => player.Liar) : -1,
                LocalPlayerId = id, HostPlayerId = hostId, IsHost = id == hostId, CanStart = CanStart, CanvasVersion = CanvasVersion,
                DrawingEpoch = DrawingEpoch, DrawingOrder = Round > 0 ? turns.ToArray() : Array.Empty<int>(),
                HasAccused = _hasAccused, AccusedPlayerId = _accusedPlayerId, BallotVersion = BallotVersion,
                LocalVoteTargetId = spectator ? -1 : local.Vote ?? -1,
                ApprovalCount = judgmentVoters.Count(player => player.Judgment == true),
                RejectionCount = judgmentVoters.Count(player => player.Judgment == false),
                JudgmentVoterCount = judgmentVoters.Length, JudgmentVotesCast = judgmentVoters.Count(player => player.Judgment.HasValue),
                LocalJudgmentApprove = local?.Judgment == true,
                IsJudgmentCoinToss = _isJudgmentCoinToss,
                JudgmentCoinApproved = _isJudgmentCoinToss && _judgmentCoinApproved,
                RemainingSeconds = Phase == GamePhase.Lobby || Phase == GamePhase.MatchResults ? 0 : (float)Math.Max(0, deadline - now),
                Settings = Settings.Copy(), AvailableTopics = KnownTopicNames(), Winners = winners.ToArray(), Summary = summary,
                Players = players.Values.Where(player => player.Connected || player.SeatReserved || result && player.RoundsPlayed > 0).OrderBy(player => player.Id).Select(player => new PlayerView
                {
                    Id = player.Id, Name = player.Name, Level = player.Level, Score = player.Score, RoundPoints = result ? player.RoundPoints : 0,
                    IsSpectator = player.Spectator, IsConnected = player.Connected, IsLiar = reveal && player.Liar,
                    IsCaught = reveal && player.Caught, AvatarColor = player.Color, Accessory = player.Accessory,
                    HasVoted = player.Vote.HasValue, HasJudged = player.Judgment.HasValue, HasGuessed = reveal && player.Guessed,
                    VoteCount = showVoteCount ? ActivePlayers().Count(voter => voter.Vote == player.Id) : 0,
                    Guess = result && player.Liar ? player.Guess : "",
                    GuessOutcome = result && player.Liar ? !player.Guessed ? GuessOutcome.Unanswered
                        : HasCorrectGuess(player) ? GuessOutcome.Correct : GuessOutcome.Incorrect : GuessOutcome.Hidden
                }).ToArray()
            };
        }

        private string PlayerWord(Participant player, bool result)
        {
            if (result) return word;
            if (player == null || player.Spectator || Phase == GamePhase.Lobby) return "";
            if (Settings.LiarMode == LiarMode.Mismatch) return player.Liar ? _mismatchWord : word;
            return player.Liar ? "" : word;
        }

        private IEnumerable<Participant> ActivePlayers() => players.Values.Where(player => player.Connected && !player.Spectator);
        private IEnumerable<Participant> JudgmentVoters() => ActivePlayers().Where(player => IsNoLiarTarget(_accusedPlayerId) || player.Id != _accusedPlayerId);
        private bool HasValidVote(Participant player) => player.Vote.HasValue && IsNominationTarget(player.Vote.Value);
        private bool AllNominationsSubmitted() => ActivePlayers().Any() && ActivePlayers().All(HasValidVote);
        private bool IsNoLiarTarget(int id) => Settings.LiarMode == LiarMode.Optional && id == GameRules.NO_LIAR_TARGET;
        private bool IsNominationTarget(int id) => IsNoLiarTarget(id) || IsActive(id);
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
            .Where(entry => IsValidTopic(entry) && (Settings.Topics == null || Settings.Topics.Contains(entry.Name))
                && (Settings.LiarMode != LiarMode.Mismatch || MismatchWords(entry).Length >= 2));

        private static string[] MismatchWords(TopicData entry) => entry.Words.Select(value => GameRules.CleanText(value, 40))
            .Where(value => value.Length > 0).GroupBy(GameRules.NormalizeGuess).Select(group => group.First()).ToArray();

        private void BeginRound(double now)
        {
            Round++;
            _judgmentTieCount = 0;
            ResetBallots();
            summary = GameRules.TieRule;
            var available = AvailableTopics().ToArray();
            var selected = available[random.Next(available.Length)];
            topic = selected.Name;
            var words = Settings.LiarMode == LiarMode.Mismatch ? MismatchWords(selected)
                : selected.Words.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            int wordIndex = random.Next(words.Length);
            word = words[wordIndex];
            _mismatchWord = Settings.LiarMode == LiarMode.Mismatch
                ? words[(wordIndex + 1 + random.Next(words.Length - 1)) % words.Length] : "";
            foreach (var player in players.Values)
            {
                player.Liar = player.Caught = player.Guessed = false;
                player.Guess = "";
                player.Vote = null;
                player.Judgment = null;
                player.CorrectJudgment = false;
                player.RoundPoints = 0;
            }
            var participants = ActivePlayers().ToArray();
            Shuffle(participants);
            int liarCount = Settings.LiarMode == LiarMode.Optional ? random.Next(2) : Settings.LiarCount;
            foreach (var player in participants.Take(Math.Min(liarCount, participants.Length - 1))) player.Liar = true;
            _roundParticipants = participants.Select(player => player.Id).ToArray();
            Shuffle(participants);
            turns = participants.Select(player => player.Id).ToArray();
            turnIndex = -1;
            DrawingEpoch = CanvasVersion + 1;
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
            if (turnIndex >= turns.Length) BeginDiscussion(now, GameRules.TieRule);
            else
            {
                if (Settings.Mode == DrawingMode.Individual) ClearCanvas();
                SetPhase(GamePhase.Drawing, Settings.DrawSeconds, now);
            }
        }

        private void ResetBallots()
        {
            ResetJudgmentCoinToss();
            BallotVersion++;
            _hasAccused = false;
            _accusedPlayerId = -1;
            foreach (var player in players.Values)
            {
                player.Vote = null;
                player.Judgment = null;
                player.Caught = false;
            }
        }

        private void BeginDiscussion(double now, string message)
        {
            ResetBallots();
            summary = message;
            SetPhase(GamePhase.Discussion, Settings.DiscussionSeconds, now);
        }

        private void ResolveNomination(double now)
        {
            var accused = GameRules.SelectAccused(ActivePlayers().Where(HasValidVote).Select(player => player.Vote.Value), random);
            if (!accused.HasValue)
            {
                summary = "투표가 없어 아무도 지목되지 않았어요. 라이어 모두에게 정답 기회가 있어요.";
                RevealLiars(now);
                return;
            }
            _hasAccused = true;
            _accusedPlayerId = accused.Value;
            BallotVersion++;
            foreach (var player in players.Values) player.Judgment = null;
            summary = IsNoLiarTarget(_accusedPlayerId)
                ? "라이어가 없다는 의견에 찬반을 투표해 주세요."
                : "지목된 친구를 제외하고 찬반을 투표해 주세요.";
            SetPhase(GamePhase.Rebuttal, Settings.RebuttalSeconds > 0 ? Settings.RebuttalSeconds : Settings.VoteSeconds, now);
        }

        private void ResolveJudgment(double now)
        {
            if (!_hasAccused || !IsNominationTarget(_accusedPlayerId))
            {
                BeginDiscussion(now, "지목된 친구의 연결이 끊겨 토론과 지목 투표를 다시 시작해요.");
                return;
            }
            if (_isJudgmentCoinToss)
            {
                if (now < deadline) return;
                foreach (int id in _judgmentCoinCorrectVoters)
                    if (players.TryGetValue(id, out var voter)) voter.CorrectJudgment = true;
                ApplyJudgment(_judgmentCoinApproved, now);
                return;
            }
            bool noLiarAccused = IsNoLiarTarget(_accusedPlayerId);
            bool accusationCorrect = noLiarAccused ? !players.Values.Any(player => player.Liar) : players[_accusedPlayerId].Liar;
            var voters = JudgmentVoters().ToArray();
            int approvals = voters.Count(voter => voter.Judgment == true), rejections = voters.Count(voter => voter.Judgment == false);
            if (approvals == rejections && ++_judgmentTieCount >= 2)
            {
                _isJudgmentCoinToss = true;
                _judgmentCoinApproved = random.Next(2) == 1;
                _judgmentCoinCorrectVoters = voters.Where(voter => voter.Judgment == accusationCorrect).Select(voter => voter.Id).ToArray();
                summary = "이번 라운드 두 번째 동률부터 동전으로 결정해요.";
                SetPhase(GamePhase.Rebuttal, GameRules.JUDGMENT_COIN_TOSS_SECONDS, now);
                return;
            }
            foreach (var voter in voters.Where(voter => voter.Judgment == accusationCorrect)) voter.CorrectJudgment = true;
            if (approvals == rejections)
            {
                BeginDiscussion(now, "첫 동률은 반대 우선으로 부결해요.");
                return;
            }
            ApplyJudgment(approvals > rejections, now);
        }

        private void ResetJudgmentCoinToss()
        {
            _isJudgmentCoinToss = false;
            _judgmentCoinApproved = false;
            _judgmentCoinCorrectVoters = Array.Empty<int>();
        }

        private void ApplyJudgment(bool approved, double now)
        {
            ResetJudgmentCoinToss();
            if (!approved)
            {
                BeginDiscussion(now, "지목이 부결되었어요. 토론과 지목 투표를 다시 진행해 주세요.");
                return;
            }
            bool noLiarAccused = IsNoLiarTarget(_accusedPlayerId);
            if (!noLiarAccused) players[_accusedPlayerId].Caught = true;
            summary = noLiarAccused ? "라이어가 없다는 의견이 가결되었어요. 실제 역할을 공개해요."
                : "지목이 가결되었어요. 라이어 모두에게 정답 기회가 있어요.";
            RevealLiars(now);
        }

        private void RevealLiars(double now)
        {
            SetPhase(GamePhase.LiarReveal, GameRules.RevealDuration(Settings.RevealSeconds, players.Values.Count(player => player.Liar)), now);
        }

        private void ScoreRound(double now)
        {
            var scoring = data.Scoring ?? new ScoreRules();
            foreach (var player in players.Values.Where(player => !player.Spectator && player.Connected))
            {
                bool correctGuess = HasCorrectGuess(player);
                player.RoundPoints = GameRules.RoundScore(player.Liar, player.Caught, correctGuess, player.CorrectJudgment, scoring);
                player.Score += player.RoundPoints;
            }
            foreach (int id in _roundParticipants)
            {
                if (!players.TryGetValue(id, out var participant)) continue;
                participant.RoundsPlayed++;
                if (participant.Liar) participant.LiarRounds++;
                else participant.CitizenRounds++;
                if (participant.CorrectJudgment) participant.CorrectVotes++;
                if (participant.Liar && HasCorrectGuess(participant)) participant.CorrectGuesses++;
                if (participant.Connected && !participant.Spectator)
                {
                    participant.WeightedRoundParticipants += _roundParticipants.Length;
                    participant.WeightedRoundScore += (long)_roundParticipants.Length * participant.RoundPoints;
                }
            }
            _completedRounds++;
            if (Settings.Victory == VictoryMode.RoundCount ? Round >= Settings.RoundCount
                : ActivePlayers().Any(player => player.Score >= Settings.TargetScore)) DecideWinners();
            summary = "올바른 찬반 판단은 라운드마다 한 번, 라이어는 생존과 정답으로 득점해요.";
            SetPhase(GamePhase.RoundResults, Settings.ResultSeconds, now);
            if (matchDecided) PublishMatchCompleted();
        }

        private bool HasCorrectGuess(Participant player) => player.Guessed
            && GameRules.NormalizeGuess(player.Guess) == GameRules.NormalizeGuess(word);

        private void FinishMatch()
        {
            ResetJudgmentCoinToss();
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
                        CorrectVotes = player.CorrectVotes, CorrectGuesses = player.CorrectGuesses,
                        WeightedRoundParticipants = player.WeightedRoundParticipants, WeightedRoundScore = player.WeightedRoundScore
                    }).ToArray()
                });
            }
        }

        private void DecideWinners()
        {
            var eligible = ActivePlayers().Where(player => _completedRounds == 0 || player.RoundsPlayed > 0).ToArray();
            int maximum = eligible.Length == 0 ? 0 : eligible.Max(player => player.Score);
            winners = eligible.Where(player => player.Score == maximum).Select(player => player.Id).ToArray();
            matchDecided = true;
        }

        private void SetPhase(GamePhase phase, float seconds, double now)
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
