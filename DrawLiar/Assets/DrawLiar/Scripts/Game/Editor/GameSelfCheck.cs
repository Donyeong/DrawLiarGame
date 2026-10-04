#nullable disable
using System;
using System.Linq;

namespace DrawLiar.Editor
{
    public static class GameSelfCheck
    {
#if UNITY_EDITOR
        [UnityEditor.MenuItem("DrawLiar/Verify game rules")]
#endif
        public static void Run()
        {
            Check(GameRules.RoundScore(true, false, true, false, new ScoreRules()) == 5, "Liar bonuses must stack.");
            Check(GameRules.CaughtPlayers(new[] { 1, 2, 1, 2, 3 }).SetEquals(new[] { 1, 2 }), "All top-vote ties are caught.");
            Check(GameRules.CaughtPlayers(Array.Empty<int>()).Count == 0, "No votes catches nobody.");
            Check(GameRules.NormalizeGuess(" 사 과 ") == "사과", "Guess normalization.");
            Check(GameRules.NormalizeGuess("사과\ud83d") == "사과", "Malformed Unicode cannot crash scoring.");
            Check(GameRules.CleanText("a\ud83d\ude00", 2) == "a", "Length limit preserves surrogate pairs.");
            Check(!GameRules.ValidStroke(new DrawStroke { X1 = float.NaN, Size = .01f }, 0), "Reject NaN strokes.");
            Check(!GameRules.ValidStroke(new DrawStroke { Size = .01f, CanvasVersion = 2 }, 1), "Reject stale strokes.");
            Check(!GameRules.ValidStroke(new DrawStroke { Size = float.PositiveInfinity }, 0), "Reject infinite brushes.");
            foreach (DrawingMode mode in Enum.GetValues(typeof(DrawingMode)))
                foreach (VictoryMode victory in Enum.GetValues(typeof(VictoryMode)))
                    VerifyMatch(mode, victory);
            VerifyDisconnectsAndNextRound();
            VerifySignedConnectionIds();
            VerifyDistinctVictoryConditions();
            VerifyCustomScoresAndFinalResults();
            VerifyTopicPoolAndTimers();
            VerifyReconnectsAndSpectatorCapacity();
            VerifyExplicitSpectatorsAndSeatPriority();
            VerifyFixedCapacityAndStartThreshold();
#if UNITY_EDITOR
            UnityEngine.Debug.Log("DrawLiar game self-check PASS: modes, victory rules, multiple liars, scores, secrecy, spectators, disconnects, topic selection, all phase timers and optional rebuttal.");
#else
            Console.WriteLine("DrawLiar game self-check PASS");
#endif
        }

#if DRAWLIAR_SELF_CHECK
        public static void Main() => Run();
#endif

        private static GameData Data() => new GameData
        {
            Scoring = new ScoreRules(),
            Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과" } } }
        };

        private static void VerifyReconnectsAndSpectatorCapacity()
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0 }, Data(), 18);
            for (int id = 1; id <= GameRules.MAX_PLAYERS; id++) Check(game.Join(id, "P" + id, 0, 0), "Active seat accepts player.");
            Check(!game.Join(9, "Full", 0, 0), "Full lobby rejects ninth active player.");
            Check(game.Start(0), "Full room starts.");
            for (int id = 9; id < 41; id++) Check(game.Join(id, "S" + id, 0, 0), "Full match accepts spectator.");
            Check(!game.Join(41, "Overflow", 0, 0), "Spectator capacity is bounded.");
            var before = game.Snapshot(1, 1, 0);
            game.Disconnect(1, 0);
            Check(game.Join(1, "변경요청", 7, 3), "Disconnected account rejoins stable player ID.");
            var after = game.Snapshot(1, 2, 0);
            Check(after.LocalIsLiar == before.LocalIsLiar && after.Word == before.Word && !after.LocalIsSpectator,
                "Reconnect preserves role and private word.");
            Check(after.Players.Single(player => player.Id == 1).Name == "P1", "Match identity is preserved on reconnect.");
            Check(after.Players.Length == 40 && game.ActiveCount == GameRules.MAX_PLAYERS && game.SpectatorCount == 32,
                "Reconnect adds no duplicate participant.");
            double now = 100;
            game.Tick(now);
            while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, now);
            game.Tick(now += 100);
            Check(game.Vote(1, 2, now), "Connected player submits vote.");
            game.Disconnect(1, now);
            Check(game.Join(1, "P1", 0, 0), "Voting player reconnects.");
            Check(game.Snapshot(1, 2, now).Players.Single(player => player.Id == 1).HasVoted && !game.Vote(1, 3, now),
                "Reconnect cannot overwrite submitted vote.");
            game.ReturnToLobby();
            Check(game.ActiveCount == GameRules.MAX_PLAYERS && game.SpectatorCount == 32, "Returning lobby respects active seat capacity.");
        }

        private static void VerifyExplicitSpectatorsAndSeatPriority()
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0 }, Data(), 18);
            Check(game.Join(1, "A", 0, 0), "First participant joins.");
            Check(game.Join(2, "B", 0, 0, spectatorOnly: true), "Explicit spectator joins before other participants.");
            Check(game.Join(3, "C", 0, 0) && game.Join(4, "D", 0, 0), "Remaining active seats are filled.");
            Check(game.Start(0), "Three participants start with an explicit spectator present.");
            Check(game.Snapshot(2, 1, 0).Players.Where(player => !player.IsSpectator).Select(player => player.Id)
                .OrderBy(id => id).SequenceEqual(new[] { 1, 3, 4 }), "Spectator ID order cannot displace an active participant.");
            Check(game.Snapshot(2, 1, 0).LocalIsSpectator && game.Snapshot(2, 1, 0).Word == "",
                "Explicit spectator remains spectator and receives no word.");
            game.Tick(100);
            int turns = 0;
            while (game.Phase == GamePhase.Drawing)
            {
                Check(game.ArtistId != 2 && !game.EndTurn(2, 100), "Explicit spectator cannot draw or finish another player's turn.");
                Check(game.EndTurn(game.ArtistId, 100), "Active participant completes drawing.");
                turns++;
            }
            Check(turns == 3, "All original active participants draw.");
            game.Tick(200);
            Check(game.Phase == GamePhase.Voting && !game.Vote(2, 1, 200), "Explicit spectator cannot vote.");
            game.Disconnect(2, 200);
            Check(game.Join(2, "B", 0, 0), "Explicit spectator reconnects without requesting spectator mode again.");
            game.ReturnToLobby();
            Check(game.Snapshot(2, 1, 200).LocalIsSpectator && game.Snapshot(2, 1, 200).Players
                .Where(player => !player.IsSpectator).Select(player => player.Id).OrderBy(id => id)
                .SequenceEqual(new[] { 1, 3, 4 }), "Lobby reset and reconnect preserve explicit intent and existing active seats.");
            Check(game.Start(200) && game.Snapshot(2, 1, 200).LocalIsSpectator && game.Snapshot(2, 1, 200).Word == "",
                "Explicit spectator stays spectator in the next match.");

            var vacancy = new GameSession(new RoomSettings(), Data(), 22);
            for (int id = 1; id <= GameRules.MAX_PLAYERS; id++) Check(vacancy.Join(id, "P" + id, 0, 0), "Vacancy scenario fills active seats.");
            Check(vacancy.Start(0) && vacancy.Join(9, "Late", 0, 0), "Ordinary late join begins as temporary spectator.");
            Check(vacancy.Snapshot(9, 1, 0).LocalIsSpectator, "Temporary spectator does not join the current round.");
            vacancy.Disconnect(8, 0);
            vacancy.ReturnToLobby();
            Check(!vacancy.Snapshot(9, 1, 0).LocalIsSpectator && vacancy.ActiveCount == GameRules.MAX_PLAYERS,
                "Ordinary late join fills a vacated seat in the next lobby.");
            Check(vacancy.Start(0) && !vacancy.Snapshot(9, 1, 0).LocalIsSpectator,
                "Promoted temporary spectator participates in the next match.");
        }

        private static void VerifyFixedCapacityAndStartThreshold()
        {
            foreach (int capacity in new[] { -1, 3, 8, 12 })
            {
                var settings = new RoomSettings { MaxPlayers = capacity };
                settings.Validate();
                Check(settings.MaxPlayers == GameRules.MAX_PLAYERS, "Room capacity is fixed at eight.");
            }

            var game = new GameSession(new RoomSettings { LiarCount = 7 }, Data(), 23);
            Check(game.Join(1, "A", 0, 0) && game.Join(2, "B", 0, 0), "Two participants join.");
            Check(game.Join(10, "Observer", 0, 0, spectatorOnly: true), "Explicit spectator joins.");
            Check(game.ActiveCount == 2 && !game.CanStart && !game.Start(0),
                "Two participants cannot start even with a connected spectator.");
            Check(game.Join(3, "C", 0, 0) && game.CanStart, "Three participants can start before all eight seats are full.");
            game.Disconnect(3, 0);
            Check(!game.CanStart && !game.Start(0), "Disconnected third participant cannot satisfy the start threshold.");
            Check(game.Join(3, "C", 0, 0) && game.Start(0), "Three connected participants start with seven configured liars.");
            var roles = new[] { 1, 2, 3 }.Select(id => game.Snapshot(id, 1, 0)).ToArray();
            Check(game.Settings.LiarCount == 2 && roles.All(snapshot => snapshot.Settings.LiarCount == 2)
                && roles.Count(snapshot => snapshot.LocalIsLiar) == 2,
                "Starting fewer participants reduces and publishes the actual liar count, preserving one citizen.");
            Check(game.Snapshot(10, 1, 0).LocalIsSpectator && game.Snapshot(10, 1, 0).Word == "",
                "Spectator remains excluded from roles and the private word.");
            int canvasVersion = game.CanvasVersion;
            Check(!game.CanStart && !game.Start(1) && game.Phase == GamePhase.RoleReveal && game.Round == 1
                && game.CanvasVersion == canvasVersion, "An ongoing match cannot restart.");
        }

        private static void VerifyTopicPoolAndTimers()
        {
            var data = Data();
            data.Topics = data.Topics.Concat(new[]
            {
                new TopicData { Name = "동물", Words = new[] { "고양이" } },
                new TopicData { Name = "음식", Words = new[] { "만두" } }
            }).ToArray();
            var settings = new RoomSettings
            {
                Topics = new[] { "동물", "음식", "동물", " " }, RoundCount = 20,
                RoleSeconds = 4, DrawSeconds = 7, DiscussionSeconds = 9, RebuttalSeconds = 6,
                VoteSeconds = 8, RevealSeconds = 3, GuessSeconds = 11, ResultSeconds = 5
            };
            var game = new GameSession(settings, data, 12);
            for (int id = 0; id < 3; id++) game.Join(id, "P" + id, 0, 0);
            Check(game.Settings.Topics.SequenceEqual(new[] { "동물", "음식" }), "Topic names are unique and nonempty.");
            settings.Topics[0] = "과일";
            var snapshot = game.Snapshot(0, 0, 0);
            snapshot.Settings.Topics[0] = "과일";
            Check(game.Settings.Topics[0] == "동물", "Draft and snapshot arrays cannot change server topics.");
            Check(game.Start(0), "Selected topic pool starts.");
            double now = 0;
            var seen = new System.Collections.Generic.HashSet<string>();
            void Advance(GamePhase phase, int seconds)
            {
                Check(game.Phase == phase && game.Snapshot(0, 0, now).RemainingSeconds == seconds, "Configured duration for " + phase);
                game.Tick(now + seconds - .01);
                Check(game.Phase == phase, "No early timeout for " + phase);
                now += seconds;
                game.Tick(now);
            }
            for (int round = 1; round <= 20; round++)
            {
                var state = game.Snapshot(0, 0, now);
                Check(state.Topic == "동물" || state.Topic == "음식", "Excluded topic never appears.");
                seen.Add(state.Topic);
                Advance(GamePhase.RoleReveal, 4);
                for (int turn = 0; turn < 3; turn++) Advance(GamePhase.Drawing, 7);
                Advance(GamePhase.Discussion, 9);
                Check(!game.Vote(0, 1, now), "Voting is locked during rebuttal.");
                Check(game.Snapshot(0, 0, now).Players.All(player => !player.IsLiar), "Rebuttal keeps identities private.");
                Advance(GamePhase.Rebuttal, 6);
                Advance(GamePhase.Voting, 8);
                Advance(GamePhase.LiarReveal, 3);
                Advance(GamePhase.Guessing, 11);
                Advance(GamePhase.RoundResults, 5);
            }
            Check(seen.Count == 2 && game.Phase == GamePhase.MatchResults, "Both selected topics can be drawn over the match.");
            game.ReturnToLobby();
            foreach (var selection in new[] { Array.Empty<string>(), new[] { "삭제된 주제" } })
            {
                var previousSettings = game.Settings;
                var previousTopics = previousSettings.Topics.ToArray();
                var invalid = game.Settings.Copy(); invalid.Topics = selection;
                Check(!game.Configure(invalid) && ReferenceEquals(game.Settings, previousSettings)
                    && game.Settings.Topics.SequenceEqual(previousTopics), "Invalid topics are rejected without replacing selected settings.");
            }
        }

        private static void VerifyMatch(DrawingMode mode, VictoryMode victory)
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, Mode = mode, Victory = victory, LiarCount = 2, RoundCount = 1, TargetScore = 5 }, Data(), 42);
            Check(!game.Start(0), "Too few players cannot start.");
            for (int id = 0; id < 5; id++) Check(game.Join(id, "P" + id, id, 0), "Player joins.");
            Check(game.Start(0), "Valid match starts.");
            int[] liars = Enumerable.Range(0, 5).Where(id => game.Snapshot(id, 0, 0).LocalIsLiar).ToArray();
            int[] citizens = Enumerable.Range(0, 5).Except(liars).ToArray();
            Check(liars.Length == 2, "Configured liar count.");
            Check(game.Join(5, "Spectator", 0, 0), "Late spectator joins.");
            Check(game.Snapshot(5, 0, 0).LocalIsSpectator && game.Snapshot(5, 0, 0).Word == "", "Spectator gets no word.");
            Check(game.Snapshot(liars[0], 0, 0).Word == "", "Liar gets no word.");
            Check(game.Snapshot(citizens[0], 0, 0).Word == "사과", "Citizen gets word.");
            Check(game.Snapshot(citizens[0], 0, 0).Players.All(player => !player.IsLiar), "Roles stay private before reveal.");
            Check(!game.Guess(liars[0], "사과", 0) && !game.Vote(citizens[0], liars[0], 0), "Wrong-phase inputs rejected.");
            int initialCanvas = game.CanvasVersion;
            double now = 100;
            game.Tick(now);
            Check(game.Phase == GamePhase.Drawing, "Role reveal advances to drawing.");
            int turns = 0;
            while (game.Phase == GamePhase.Drawing)
            {
                Check(game.ArtistId != 5, "Spectator never draws.");
                Check(!game.EndTurn(5, now), "Spectator cannot skip turn.");
                Check(game.EndTurn(game.ArtistId, now), "Artist can finish.");
                turns++;
            }
            Check(turns == 5, "Every participant draws once.");
            Check(game.CanvasVersion == initialCanvas + (mode == DrawingMode.Individual ? 5 : 0), "Canvas resets match mode.");
            now += 100;
            game.Tick(now);
            Check(game.Phase == GamePhase.Voting, "Discussion advances to vote.");
            Check(!game.Vote(5, liars[0], now), "Spectator cannot vote.");
            Check(!game.Vote(citizens[0], citizens[0], now), "Self vote rejected.");
            Check(game.Vote(citizens[0], liars[0], now), "First correct citizen vote.");
            Check(!game.Vote(citizens[0], liars[1], now), "Cannot vote twice.");
            game.Vote(citizens[1], liars[0], now);
            game.Vote(citizens[2], liars[1], now);
            game.Vote(liars[0], citizens[0], now);
            game.Vote(liars[1], citizens[0], now);
            Check(game.Phase == GamePhase.LiarReveal, "Complete voting reveals liars.");
            var reveal = game.Snapshot(5, 0, now);
            Check(reveal.Players.Count(player => player.IsLiar) == 2, "Reveal includes all liars.");
            Check(reveal.Players.Single(player => player.Id == liars[0]).IsCaught, "Top-voted liar caught.");
            Check(!reveal.Players.Single(player => player.Id == liars[1]).IsCaught, "Other liar survives.");
            Check(reveal.Word == "" && game.Snapshot(liars[0], 0, now).Word == "", "Word stays hidden through reveal.");
            now += 100;
            game.Tick(now);
            Check(!game.Guess(citizens[0], "사과", now), "Citizen cannot guess.");
            Check(game.Guess(liars[0], " 사 과 ", now), "Caught liar can guess.");
            Check(!game.Guess(liars[0], "바나나", now), "Cannot guess twice.");
            Check(game.Guess(liars[1], "사과", now), "Uncaught liar can guess.");
            var result = game.Snapshot(5, 0, now);
            Check(result.Phase == GamePhase.RoundResults && result.Word == "사과", "Word becomes public at result.");
            Check(result.Players.Single(player => player.Id == liars[0]).Score == 3, "Caught liar gets guess points.");
            Check(result.Players.Single(player => player.Id == liars[1]).Score == 5, "Surviving liar gets both awards.");
            Check(result.Players.Where(player => citizens.Contains(player.Id)).All(player => player.Score == 2), "Citizens get correct vote points regardless of majority.");
            Check(result.Players.Single(player => player.Id == 5).Score == 0, "Spectators do not score.");
            now += 100;
            game.Tick(now);
            Check(game.Phase == GamePhase.MatchResults && game.Snapshot(0, 0, now).Winners.SequenceEqual(new[] { liars[1] }), "Victory condition crowns highest scorer.");
            game.ReturnToLobby();
            Check(!game.Snapshot(5, 0, now).LocalIsSpectator, "Spectator can play the next match.");
            Check(game.Snapshot(0, 0, now).Players.All(player => player.Score == 0), "New match clears scores.");
            Check(game.Start(now), "Next match can start with former spectator.");
            Check(!game.Snapshot(5, 0, now).LocalIsSpectator, "Former spectator receives an active role.");
            now += 100;
            game.Tick(now);
            bool formerSpectatorDrew = false;
            while (game.Phase == GamePhase.Drawing)
            {
                formerSpectatorDrew |= game.ArtistId == 5;
                game.EndTurn(game.ArtistId, now);
            }
            Check(formerSpectatorDrew, "Former spectator receives a drawing turn in the next match.");
            now += 100;
            game.Tick(now);
            Check(game.Vote(5, 0, now), "Former spectator can vote in the next match.");
        }

        private static void VerifyDisconnectsAndNextRound()
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, RoundCount = 2 }, Data(), 7);
            for (int id = 0; id < 4; id++) game.Join(id, "P" + id, 0, 0);
            game.Start(0);
            game.Join(4, "Late", 0, 0);
            double now = 100;
            game.Tick(now);
            int oldArtist = game.ArtistId;
            game.Disconnect(oldArtist, now);
            Check(game.ArtistId != oldArtist, "Disconnected artist skipped.");
            for (int step = 0; step < 20 && game.Round == 1; step++) { now += 500; game.Tick(now); }
            Check(game.Round == 2 && game.Phase == GamePhase.RoleReveal, "Round-count victory continues before final round.");
            Check(game.Snapshot(4, 0, now).LocalIsSpectator, "Late join remains spectator through match.");
            var active = game.Snapshot(0, 0, now).Players.Where(player => player.IsConnected && !player.IsSpectator).ToArray();
            foreach (var player in active.Take(active.Length - 1)) game.Disconnect(player.Id, now);
            Check(game.Phase == GamePhase.MatchResults, "Too few active players ends match safely.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("DrawLiar self-check failed: " + message);
        }

        private static void VerifySignedConnectionIds()
        {
            GameSession game = null;
            for (int seed = 0; seed < 100; seed++)
            {
                game = new GameSession(new RoomSettings { RebuttalSeconds = 0, RoundCount = 1 }, Data(), seed);
                foreach (int id in new[] { -1, 0, int.MinValue }) game.Join(id, "P" + id, 0, 0);
                Check(!game.EndTurn(-1, 0) && game.Phase == GamePhase.Lobby, "ID -1 cannot skip a non-drawing phase.");
                game.Start(0);
                if (game.Snapshot(-1, 0, 0).LocalIsLiar) break;
            }
            Check(game.Snapshot(-1, 0, 0).LocalIsLiar, "Signed ID regression has a -1 liar.");
            Check(!game.EndTurn(-1, 0), "ID -1 cannot skip role reveal.");
            game.Tick(100);
            int turnCount = 0;
            while (game.Phase == GamePhase.Drawing)
            {
                Check(game.EndTurn(game.ArtistId, 100), "Negative-ID artist can finish own turn.");
                turnCount++;
            }
            Check(turnCount == 3, "Every signed-ID player gets a drawing turn.");
            game.Tick(200);
            Check(game.Vote(0, -1, 200), "Vote for ID -1 accepted.");
            Check(game.Snapshot(0, 0, 200).Players.Single(player => player.Id == 0).HasVoted, "ID -1 vote is not an unsubmitted sentinel.");
            Check(!game.Vote(0, int.MinValue, 200), "ID -1 vote cannot be overwritten.");
            Check(game.Vote(int.MinValue, -1, 200), "Minimum signed ID can vote.");
            Check(game.Vote(-1, int.MinValue, 200), "Negative liar can vote for a negative citizen.");
            Check(game.Phase == GamePhase.LiarReveal, "All signed-ID votes finish voting.");
            var revealed = game.Snapshot(0, 0, 200);
            Check(revealed.Players.Single(player => player.Id == -1).IsCaught, "Negative-ID votes count toward the majority.");
            Check(revealed.Players.Single(player => player.Id == int.MinValue).VoteCount == 1, "Votes for minimum signed ID counted.");
            game.Tick(300);
            game.Guess(-1, "틀린 답", 300);
            Check(game.Snapshot(0, 0, 300).Players.Where(player => player.Id != -1).All(player => player.Score == 2), "Citizen votes for negative-ID liar score correctly.");
        }

        private static void PlayUnvotedRound(GameSession game, ref double now, bool correctGuess = false)
        {
            Check(game.Phase == GamePhase.RoleReveal, "Round helper begins at role reveal.");
            game.Tick(now += 500);
            while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, now);
            game.Tick(now += 500);
            game.Tick(now += 500);
            game.Tick(now += 500);
            Check(game.Phase == GamePhase.Guessing, "Unvoted round still gives all liars a guess.");
            foreach (var liar in game.Snapshot(0, 0, now).Players.Where(player => player.IsLiar))
                game.Guess(liar.Id, correctGuess ? "사과" : "틀린 답", now);
            Check(game.Phase == GamePhase.RoundResults, "Round helper completes scoring.");
        }

        private static void VerifyDistinctVictoryConditions()
        {
            var rounds = new GameSession(new RoomSettings { RebuttalSeconds = 0, RoundCount = 3, TargetScore = 1 }, Data(), 10);
            for (int id = 0; id < 3; id++) rounds.Join(id, "P" + id, 0, 0);
            rounds.Start(0);
            double now = 0;
            for (int round = 1; round <= 3; round++)
            {
                PlayUnvotedRound(rounds, ref now);
                Check(rounds.Snapshot(0, 0, now).Players.Any(player => player.Score >= 1), "Target threshold is already crossed in round-count mode.");
                rounds.Tick(now += 500);
                Check(rounds.Phase == (round == 3 ? GamePhase.MatchResults : GamePhase.RoleReveal), "Round-count mode ignores target and ends exactly after N rounds.");
            }

            var pointsData = Data();
            pointsData.Scoring = new ScoreRules { LiarUncaught = 7, LiarCorrectGuess = 11, CitizenCorrectVote = 13 };
            var points = new GameSession(new RoomSettings { RebuttalSeconds = 0, Victory = VictoryMode.TargetScore, RoundCount = 1, TargetScore = 9 }, pointsData, 11);
            for (int id = 0; id < 3; id++) points.Join(id, "P" + id, 0, 0);
            points.Start(0);
            now = 0;
            PlayUnvotedRound(points, ref now);
            Check(points.Snapshot(0, 0, now).Players.Max(player => player.Score) == 7, "Nonstandard uncaught score comes from data.");
            points.Tick(now += 500);
            Check(points.Phase == GamePhase.RoleReveal && points.Round == 2, "Target mode continues past RoundCount while nobody reaches target.");
            PlayUnvotedRound(points, ref now, true);
            var result = points.Snapshot(0, 0, now);
            Check(result.Players.Single(player => player.IsLiar).RoundPoints == 18, "Nonstandard liar bonuses stack from data.");
            int winner = result.Winners.Single();
            points.Disconnect(winner, now);
            Check(points.Phase == GamePhase.RoundResults && points.Snapshot(0, 0, now).Winners.SequenceEqual(new[] { winner }), "Target winner is fixed before result-screen disconnect.");
            points.Tick(now += 500);
            var final = points.Snapshot(0, 0, now);
            Check(final.Phase == GamePhase.MatchResults && final.Winners.SequenceEqual(new[] { winner }), "Target winner stays fixed after leaving.");
            Check(final.Players.Any(player => player.Id == winner && !player.IsConnected), "Departed winner remains in final rankings.");
        }

        private static void VerifyCustomScoresAndFinalResults()
        {
            var data = Data();
            data.Scoring = new ScoreRules { LiarUncaught = 7, LiarCorrectGuess = 11, CitizenCorrectVote = 13 };
            data.Topics = data.Topics.Concat(new[] { new TopicData { Name = "우리 주제", Words = new[] { "우리 단어" } } }).ToArray();
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, RoundCount = 1, Topics = new[] { "우리 주제" } }, data, 5);
            for (int id = 0; id < 3; id++) game.Join(id, "P" + id, 0, 0);
            game.Start(0);
            var liar = Enumerable.Range(0, 3).Single(id => game.Snapshot(id, 0, 0).LocalIsLiar);
            var citizens = Enumerable.Range(0, 3).Where(id => id != liar).ToArray();
            foreach (int citizen in citizens)
                Check(game.Snapshot(citizen, 0, 0).Topic == "우리 주제" && game.Snapshot(citizen, 0, 0).Word == "우리 단어", "Host-selected custom word reaches each citizen snapshot.");
            Check(game.Snapshot(liar, 0, 0).Word == "", "Custom word stays hidden from liar.");
            double now = 500;
            game.Tick(now);
            while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, now);
            game.Tick(now += 500);
            foreach (int citizen in citizens) game.Vote(citizen, liar, now);
            game.Vote(liar, citizens[0], now);
            game.Tick(now += 500);
            game.Guess(liar, "우리 단어", now);
            var scored = game.Snapshot(0, 0, now);
            Check(scored.Players.Where(player => !player.IsLiar).All(player => player.Score == 13), "Custom citizen vote award is used.");
            Check(scored.Players.Single(player => player.IsLiar).Score == 11, "Caught liar earns custom guess award only.");
            Check(scored.Winners.OrderBy(id => id).SequenceEqual(citizens.OrderBy(id => id)), "Top-score tie locks both winners at final scoring.");
            game.Disconnect(citizens[0], now);
            game.Tick(now += 500);
            game.Disconnect(citizens[1], now);
            var final = game.Snapshot(liar, 0, now);
            Check(final.Phase == GamePhase.MatchResults && final.Winners.OrderBy(id => id).SequenceEqual(citizens.OrderBy(id => id)), "Final-round winners survive departures during and after results.");
            Check(final.Players.Length == 3, "Final rankings retain every participant after match end.");
        }
    }
}
