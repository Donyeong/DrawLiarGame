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
            VerifyConsensusVoting();
            VerifyLiveNominationCounts();
            VerifyMutableNomination();
            VerifyJudgmentCoinToss();
            VerifyGuessResults();
#if UNITY_EDITOR
            UnityEngine.Debug.Log("DrawLiar game self-check PASS: 게임 모드, 승리 조건, 지목·찬반 합의, 점수, 비밀 상태, 재접속, 관전자, 타이머 검증");
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

        private static void VerifyGuessResults()
        {
            GameSession Prepare(int liarCount)
            {
                var game = new GameSession(new RoomSettings { LiarCount = liarCount, RoundCount = 1, ResultSeconds = 5 }, Data(), 73);
                for (int id = 1; id <= 4; id++) Check(game.Join(id, "P" + id, 0, 0), "추측 검증 참가자가 입장해야 합니다.");
                Check(game.Join(9, "관전자", 0, 0, spectatorOnly: true) && game.Start(0), "관전자를 제외하고 경기를 시작해야 합니다.");
                game.Tick(6);
                while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, 6);
                game.Tick(51);
                game.Tick(56);
                Check(game.Phase == GamePhase.Guessing, "지목 없는 라운드는 라이어 추측으로 진행해야 합니다.");
                return game;
            }
            void Hidden(GameSession game)
            {
                foreach (int viewer in new[] { 1, 2, 3, 4, 9 })
                    Check(game.Snapshot(viewer, 1, 56).Players.All(player => player.Guess == "" && player.GuessOutcome == GuessOutcome.Hidden),
                        "모든 라이어의 추측이 끝나기 전에는 누구에게도 답과 판정을 공개하면 안 됩니다.");
            }
            var game = Prepare(2);
            int[] liars = game.Snapshot(1, 1, 56).Players.Where(player => player.IsLiar).Select(player => player.Id).ToArray();
            int citizen = game.Snapshot(1, 1, 56).Players.First(player => !player.IsLiar && !player.IsSpectator).Id;
            Hidden(game);
            Check(!game.Guess(citizen, "사과", 56) && !game.Guess(9, "사과", 56), "시민과 관전자는 추측을 제출할 수 없습니다.");
            Check(!game.Guess(liars[1], "<>\0", 56), "정제 후 빈 입력은 제출 처리하지 않아야 합니다.");
            Check(game.Guess(liars[0], " 사 과 ", 56.1) && !game.Guess(liars[0], "바나나", 56.2), "라이어는 한 번만 제출해야 합니다.");
            Hidden(game);
            string incorrect = new string('나', 60);
            Check(game.Guess(liars[1], "<" + incorrect + ">", 57) && game.Phase == GamePhase.RoundResults, "모두 제출하면 즉시 결과로 넘어가야 합니다.");
            foreach (int viewer in new[] { 1, 2, 3, 4, 9 })
            {
                var result = game.Snapshot(viewer, 1, 57);
                var correct = result.Players.Single(player => player.Id == liars[0]);
                var wrong = result.Players.Single(player => player.Id == liars[1]);
                Check(correct.Guess == "사 과" && correct.GuessOutcome == GuessOutcome.Correct && correct.Score == 5,
                    "공백 정규화 정답 판정과 기존 정답 점수가 일치해야 합니다.");
                Check(wrong.Guess == incorrect.Substring(0, 40) && wrong.GuessOutcome == GuessOutcome.Incorrect && wrong.Score == 2,
                    "오답은 정제한 40자 답변과 기존 점수로 모두에게 공개해야 합니다.");
                Check(result.Players.Where(player => !player.IsLiar).All(player => player.GuessOutcome == GuessOutcome.Hidden), "시민·관전자의 판정은 숨겨야 합니다.");
            }
            game.Tick(62);
            Check(game.Phase == GamePhase.MatchResults && game.Snapshot(9, 1, 62).Players.Single(player => player.Id == liars[0]).GuessOutcome == GuessOutcome.Correct,
                "최종 결과에서도 마지막 라운드의 판정을 유지해야 합니다.");
            game.ReturnToLobby();
            Hidden(game);
            var timeout = Prepare(2);
            liars = timeout.Snapshot(1, 1, 56).Players.Where(player => player.IsLiar).Select(player => player.Id).ToArray();
            Check(timeout.Guess(liars[0], "사과", 56.1), "시간 초과 검증의 첫 라이어가 제출해야 합니다.");
            Hidden(timeout);
            Check(!timeout.Guess(liars[1], "사과", 76) && timeout.Phase == GamePhase.RoundResults, "마감 시각의 제출은 거부하고 결과로 넘어가야 합니다.");
            Check(!timeout.Guess(liars[1], "사과", 77), "늦은 제출은 이미 공개한 결과를 바꾸면 안 됩니다.");
            foreach (int viewer in new[] { 1, 2, 3, 4, 9 })
                Check(timeout.Snapshot(viewer, 1, 76).Players.Single(player => player.Id == liars[1]).GuessOutcome == GuessOutcome.Unanswered,
                    "미제출 판정도 모든 참가자와 관전자에게 동일하게 공개해야 합니다.");
            var single = Prepare(1);
            int liar = single.Snapshot(1, 1, 56).Players.Single(player => player.IsLiar).Id;
            Check(single.Guess(liar, "바나나", 56) && single.Snapshot(9, 1, 56).Players.Single(player => player.Id == liar).GuessOutcome == GuessOutcome.Incorrect,
                "라이어 한 명의 제출도 즉시 전체 결과로 공개해야 합니다.");
        }

        private static void VerifyReconnectsAndSpectatorCapacity()
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, AllowMidRoundJoin = false }, Data(), 18);
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
            Check(game.Phase == GamePhase.Discussion, "그림 뒤 토론 중 지목을 제출할 수 있어야 합니다.");
            Check(game.Vote(1, 2, now), "Connected player submits vote.");
            game.Disconnect(1, now);
            Check(game.Join(1, "P1", 0, 0), "Voting player reconnects.");
            Check(game.Snapshot(1, 2, now).Players.Single(player => player.Id == 1).HasVoted
                && game.Snapshot(1, 2, now).LocalVoteTargetId == 2 && game.Vote(1, 3, now)
                && game.Snapshot(1, 2, now).LocalVoteTargetId == 3,
                "재접속은 제출한 선택을 복원하며 남은 시간 동안 변경할 수 있어야 합니다.");
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
            Check(game.Phase == GamePhase.Discussion && !game.Vote(2, 1, 200), "관전자는 토론 지목에 참가할 수 없습니다.");
            game.Disconnect(2, 200);
            Check(game.Join(2, "B", 0, 0), "Explicit spectator reconnects without requesting spectator mode again.");
            game.ReturnToLobby();
            Check(game.Snapshot(2, 1, 200).LocalIsSpectator && game.Snapshot(2, 1, 200).Players
                .Where(player => !player.IsSpectator).Select(player => player.Id).OrderBy(id => id)
                .SequenceEqual(new[] { 1, 3, 4 }), "Lobby reset and reconnect preserve explicit intent and existing active seats.");
            Check(game.Start(200) && game.Snapshot(2, 1, 200).LocalIsSpectator && game.Snapshot(2, 1, 200).Word == "",
                "Explicit spectator stays spectator in the next match.");

            var vacancy = new GameSession(new RoomSettings { AllowMidRoundJoin = false }, Data(), 22);
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
                Check(game.Vote(0, 1, now), "토론 중 일부 지목을 제출할 수 있어야 합니다.");
                Advance(GamePhase.Discussion, 9);
                Check(!game.Vote(0, 1, now), "반론 중 지목을 새로 제출할 수 없습니다.");
                Check(game.Snapshot(0, 0, now).Players.All(player => !player.IsLiar), "Rebuttal keeps identities private.");
                Check(game.Judge(0, 1, true, now), "시간 제한 검증을 위해 찬성 우세의 일부 찬반을 제출해야 합니다.");
                Advance(GamePhase.Rebuttal, 6);
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
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, AllowMidRoundJoin = false, Mode = mode, Victory = victory, LiarCount = 2, RoundCount = 1, TargetScore = 5 }, Data(), 42);
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
            Check(game.Phase == GamePhase.Discussion, "토론 중 지목을 제출해야 합니다.");
            Check(!game.Vote(5, liars[0], now), "Spectator cannot vote.");
            Check(!game.Vote(citizens[0], citizens[0], now), "Self vote rejected.");
            Check(game.Vote(citizens[0], liars[0], now), "First correct citizen vote.");
            Check(game.Vote(citizens[0], liars[1], now) && game.Vote(citizens[0], liars[0], now), "제한 시간 내 지목을 변경하고 되돌릴 수 있어야 합니다.");
            game.Vote(citizens[1], liars[0], now);
            game.Vote(citizens[2], liars[0], now);
            game.Vote(liars[0], citizens[0], now);
            game.Vote(liars[1], citizens[0], now);
            Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(0, 0, now).AccusedPlayerId == liars[0], "전원이 지목하면 최다표 한 명의 반론으로 바로 진행해야 합니다.");
            Check(!game.Judge(5, liars[0], true, now) && !game.Judge(liars[0], liars[0], true, now), "관전자와 후보는 찬반에 참가할 수 없습니다.");
            ApproveAll(game, now);
            Check(game.Phase == GamePhase.LiarReveal, "찬반 가결 뒤 라이어를 공개해야 합니다.");
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
            Check(result.Players.Single(player => player.Id == liars[1]).Score == 7, "라이어도 올바른 찬성과 생존·정답 보상을 함께 얻어야 합니다.");
            Check(result.Players.Where(player => citizens.Contains(player.Id)).All(player => player.Score == 2), "라이어 후보에 올바르게 찬성한 참가자는 점수를 얻어야 합니다.");
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
            Check(game.Vote(5, 0, now), "Former spectator can vote in the next match.");
        }

        private static void VerifyDisconnectsAndNextRound()
        {
            var game = new GameSession(new RoomSettings { RebuttalSeconds = 0, AllowMidRoundJoin = false, RoundCount = 2 }, Data(), 7);
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
            Check(game.Vote(0, -1, 100), "Vote for ID -1 accepted.");
            Check(game.Snapshot(0, 0, 100).Players.Single(player => player.Id == 0).HasVoted, "ID -1 vote is not an unsubmitted sentinel.");
            Check(game.Vote(0, int.MinValue, 100) && game.Vote(0, -1, 100), "음수 ID를 지목한 표도 제한 시간 내 변경할 수 있어야 합니다.");
            Check(game.Vote(int.MinValue, -1, 100), "Minimum signed ID can vote.");
            Check(game.Vote(-1, int.MinValue, 100), "Negative liar can vote for a negative citizen.");
            double nominationEnd = 100;
            Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(0, 0, nominationEnd).HasAccused
                && game.Snapshot(0, 0, nominationEnd).AccusedPlayerId == -1, "-1 ID도 존재하는 반론 후보로 구분해야 합니다.");
            ApproveAll(game, nominationEnd);
            var revealed = game.Snapshot(0, 0, nominationEnd);
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
            foreach (int citizen in citizens) game.Vote(citizen, liar, now);
            game.Vote(liar, citizens[0], now);
            ApproveAll(game, now);
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

        private static void ApproveAll(GameSession game, double now)
        {
            var state = game.Snapshot(0, 0, now);
            Check(game.Phase == GamePhase.Rebuttal && state.HasAccused, "반론 후보가 있어야 찬반을 처리할 수 있습니다.");
            foreach (var player in state.Players.Where(player => player.IsConnected && !player.IsSpectator && player.Id != state.AccusedPlayerId))
                Check(game.Judge(player.Id, state.AccusedPlayerId, true, now), "후보 외의 참가자가 찬성할 수 있어야 합니다.");
        }

        private static GameSession DiscussionGame(int seed, int count = 4, int rebuttal = 6, int rounds = 1)
        {
            var game = new GameSession(new RoomSettings { RoundCount = rounds, RebuttalSeconds = rebuttal, VoteSeconds = 8 }, Data(), seed);
            for (int id = 0; id < count; id++) game.Join(id, "P" + id, 0, 0);
            Check(game.Start(0), "합의 검증 경기를 시작해야 합니다.");
            game.Tick(100);
            while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, 100);
            Check(game.Phase == GamePhase.Discussion, "그림을 마치면 토론으로 이동해야 합니다.");
            return game;
        }

        private static double TickDeadline(GameSession game, double now)
        {
            now += game.Snapshot(0, 0, now).RemainingSeconds;
            game.Tick(now);
            return now;
        }

        private static double NominateAll(GameSession game, int accused, double now = 100)
        {
            int[] ids = game.Snapshot(0, 0, now).Players.Where(player => player.IsConnected && !player.IsSpectator).Select(player => player.Id).ToArray();
            foreach (int id in ids)
                Check(game.Vote(id, id == accused ? ids.First(other => other != accused) : accused, now), "현재 토론의 지목을 제출해야 합니다.");
            Check(game.Phase == GamePhase.Rebuttal && game.Snapshot(0, 0, now).HasAccused
                && game.Snapshot(0, 0, now).AccusedPlayerId == accused, "전원 제출 시 최다표 한 명의 반론으로 바로 진행해야 합니다.");
            return now;
        }

        private static void VerifyLiveNominationCounts()
        {
            var game = DiscussionGame(118, 5, rounds: 2);
            double now = 100;
            Check(game.Join(99, "관전자", 0, 0, spectatorOnly: true), "실시간 지목 집계 검증 관전자가 입장해야 합니다.");
            void Counts(params (int Id, int Count)[] expected)
            {
                var baseline = game.Snapshot(99, 0, now);
                foreach (var player in baseline.Players)
                    Check(player.VoteCount == expected.Where(value => value.Id == player.Id).Select(value => value.Count).FirstOrDefault(),
                        "실시간 지목 집계는 연결된 유효 참가자의 표만 대상별로 반환해야 합니다.");
                foreach (int viewer in baseline.Players.Select(player => player.Id))
                {
                    var state = game.Snapshot(viewer, 0, now);
                    Check(state.Players.Select(player => (player.Id, player.VoteCount))
                        .SequenceEqual(baseline.Players.Select(player => (player.Id, player.VoteCount))),
                        "실시간 득표수는 모든 참가자와 관전자에게 동일해야 합니다.");
                    if (state.Phase < GamePhase.LiarReveal)
                        Check(state.RevealedLiarCount == -1 && state.MismatchWord == ""
                            && state.Word == (state.LocalIsSpectator || state.LocalIsLiar ? "" : "사과")
                            && state.Players.All(player => !player.IsLiar && !player.IsCaught && player.Guess == "" && player.GuessOutcome == GuessOutcome.Hidden),
                            "실시간 득표 공개로 다른 참가자의 역할·정답·추측을 추가 공개하면 안 됩니다.");
                }
            }
            Counts();
            Check(game.Vote(0, 1, now), "첫 유효 지목을 제출해야 합니다.");
            Counts((1, 1));
            Check(game.Vote(0, 1, now) && game.Vote(0, 2, now) && game.Vote(0, 1, now) && !game.Vote(1, 1, now)
                && !game.Vote(1, 99, now) && !game.Vote(99, 1, now)
                && !game.Vote(404, 1, now) && !game.Vote(1, 404, now),
                "본인·관전자·미존재 지목은 거부하고 제한 시간 내 교체는 허용해야 합니다.");
            Counts((1, 1));
            game.Disconnect(0, now);
            Counts();
            Check(game.Join(0, "복귀", 0, 0) && game.Snapshot(0, 0, now).LocalVoteTargetId == 1,
                "접속 유예 중 제외된 표는 재접속 시 현재 선택과 함께 복원해야 합니다.");
            Counts((1, 1));
            game.Disconnect(1, now);
            Counts();
            Check(game.Join(1, "복귀", 0, 0) && game.Vote(0, 2, now),
                "대상 이탈로 취소한 표는 재접속 뒤에도 되살리지 않으며 다시 제출할 수 있어야 합니다.");
            Counts((2, 1));
            Check(game.Vote(1, 2, now) && game.Vote(3, 2, now) && game.Vote(4, 2, now),
                "유효 지목마다 대상의 실시간 집계를 늘려야 합니다.");
            Counts((2, 4));
            int ballot = game.BallotVersion;
            Check(game.Vote(2, 0, now) && game.Phase == GamePhase.Rebuttal, "마지막 지목을 제출하면 후보의 반론으로 바로 진행해야 합니다.");
            Counts();
            foreach (int voter in new[] { 0, 1, 3, 4 })
                Check(game.Judge(voter, 2, false, now), "새 지목 투표를 열기 위해 반대해야 합니다.");
            Check(game.Phase == GamePhase.Discussion && game.BallotVersion > ballot
                && game.Snapshot(99, 0, now).Players.All(player => !player.HasVoted),
                "부결 뒤 새로운 지목 투표는 제출 상태와 집계를 초기화해야 합니다.");
            Counts();
            now = NominateAll(game, 2, now);
            ApproveAll(game, now);
            Counts((2, 4), (0, 1));
            game.Tick(now += 100); game.Tick(now += 100); game.Tick(now += 100); game.Tick(now += 100);
            while (game.Phase == GamePhase.Drawing) game.EndTurn(game.ArtistId, now);
            Check(game.Round == 2 && game.Phase == GamePhase.Discussion
                && Enumerable.Range(0, 5).All(id => game.Snapshot(id, 0, now).LocalVoteTargetId == -1),
                "다음 라운드의 지목 투표는 개인 선택도 초기화해야 합니다.");
            Counts();
            game.ReturnToLobby();
            Check(game.Snapshot(99, 0, now).Players.All(player => player.VoteCount == 0 && !player.HasVoted)
                && Enumerable.Range(0, 5).All(id => game.Snapshot(id, 0, now).LocalVoteTargetId == -1),
                "대기방 복귀는 이전 지목 집계와 제출 상태를 제거해야 합니다.");

            var departed = DiscussionGame(120, 5);
            Check(departed.Vote(0, 1, 100), "명시적 탈퇴 전에 지목을 제출해야 합니다.");
            departed.Disconnect(0, 100, reserveSeat: false);
            var afterDeparture = departed.Snapshot(99, 1, 100);
            Check(departed.Phase == GamePhase.Discussion && afterDeparture.Players.All(player => player.Id != 0 && player.VoteCount == 0),
                "좌석을 반환한 탈퇴자의 기존 표는 현재 지목 집계에 남으면 안 됩니다.");

            var data = Data(); data.Topics[0].Words = new[] { "사과", "배" };
            var mismatch = new GameSession(new RoomSettings { LiarMode = LiarMode.Mismatch }, data, 119);
            for (int id = 0; id < 4; id++) Check(mismatch.Join(id, "P" + id, 0, 0), "미스매치 집계 참가자가 입장해야 합니다.");
            Check(mismatch.Join(99, "관전자", 0, 0, spectatorOnly: true) && mismatch.Start(0), "미스매치 집계 검증을 시작해야 합니다.");
            mismatch.Tick(100);
            while (mismatch.Phase == GamePhase.Drawing) mismatch.EndTurn(mismatch.ArtistId, 100);
            int[] viewers = new[] { 0, 1, 2, 3, 99 };
            var words = viewers.ToDictionary(viewer => viewer, viewer => mismatch.Snapshot(viewer, 0, 100).Word);
            Check(mismatch.Vote(0, 1, 100), "미스매치에서도 실시간 지목을 제출해야 합니다.");
            foreach (int viewer in viewers)
            {
                var state = mismatch.Snapshot(viewer, 0, 100);
                Check(state.Players.Single(player => player.Id == 1).VoteCount == 1
                    && state.Players.Where(player => player.Id != 1).All(player => player.VoteCount == 0)
                    && !state.LocalIsLiar && state.RevealedLiarCount == -1 && state.MismatchWord == "" && state.Word == words[viewer]
                    && state.Players.All(player => !player.IsLiar && !player.IsCaught),
                    "미스매치 득표는 모두에게 같고 본인 역할·상대 제시어는 기존 비공개 정책을 유지해야 합니다.");
            }
        }

        private static void VerifyMutableNomination()
        {
            var game = DiscussionGame(121, rounds: 2);
            Check(game.Join(99, "관전자", 0, 0, spectatorOnly: true), "투표 변경 검증 관전자가 입장해야 합니다.");
            int ballot = game.BallotVersion;
            var initial = Enumerable.Range(0, 4).Select(id => game.Snapshot(id, 0, 100)).ToArray();
            double deadline = 100 + initial[0].RemainingSeconds;
            Check(initial.All(state => state.LocalVoteTargetId == -1 && state.Players.All(player => !player.HasVoted)),
                "새 지목 투표의 개인 선택과 제출 상태는 비어 있어야 합니다.");
            int changes = 0;
            game.Changed += () => changes++;
            Check(game.Vote(0, 1, 101) && changes == 1 && game.Snapshot(0, 0, 101).LocalVoteTargetId == 1,
                "첫 지목은 본인의 선택과 서버 집계를 갱신해야 합니다.");
            Check(game.Vote(0, 1, 102) && changes == 1, "같은 지목 재전송은 성공하되 상태 변경 이벤트를 중복 발생시키지 않아야 합니다.");
            Check(game.Vote(0, 2, 103) && changes == 2, "마감 전에는 다른 참가자로 지목을 변경할 수 있어야 합니다.");
            var replaced = game.Snapshot(0, 0, 103);
            Check(replaced.LocalVoteTargetId == 2 && replaced.Players.Single(player => player.Id == 1).VoteCount == 0
                && replaced.Players.Single(player => player.Id == 2).VoteCount == 1
                && replaced.RemainingSeconds == deadline - 103 && replaced.BallotVersion == ballot,
                "교체는 이전 득표를 빼고 새 득표를 더하며 제한 시간과 투표 번호를 유지해야 합니다.");
            Check(game.Snapshot(1, 0, 103).LocalVoteTargetId == -1 && game.Snapshot(99, 0, 103).LocalVoteTargetId == -1,
                "다른 참가자와 관전자의 개인 선택에 제출자의 지목을 노출하면 안 됩니다.");
            Check(!game.Vote(0, 0, 103) && !game.Vote(0, 99, 103) && !game.Vote(0, 1000, 103)
                && !game.Vote(99, 2, 103) && game.Snapshot(0, 0, 103).LocalVoteTargetId == 2,
                "자기 자신·관전자·없는 참가자 지목과 관전자 제출은 기존 선택을 바꾸면 안 됩니다.");
            Check(game.Vote(1, 2, 104) && game.Vote(2, 1, 104) && game.Vote(3, 2, 104)
                && game.Phase == GamePhase.Rebuttal && game.Snapshot(0, 0, 104).AccusedPlayerId == 2
                && changes == 5 && game.CanvasVersion == initial[0].CanvasVersion && game.DrawingEpoch == initial[0].DrawingEpoch
                && game.Snapshot(0, 0, 104).Players.Where(player => !player.IsSpectator).All(player => player.HasVoted),
                "마지막 유효 지목은 그림을 유지하며 한 번의 상태 변경으로 반론을 시작해야 합니다.");
            Check(!game.Vote(0, 3, 104) && !game.Vote(0, 2, 104), "반론으로 넘어간 뒤에는 변경과 같은 표 재전송을 모두 거부해야 합니다.");
            int[] targets = { 2, 2, 1, 2 };
            foreach (int viewer in new[] { 0, 1, 2, 3, 99 })
            {
                var state = game.Snapshot(viewer, 0, 104);
                Check(state.LocalVoteTargetId == (viewer == 99 ? -1 : targets[viewer])
                    && state.Players.All(player => player.VoteCount == 0)
                    && state.Players.All(player => !player.IsLiar && !player.IsCaught),
                    "반론에서는 각 참가자에게 본인 선택만 유지하고 지목 집계·비공개 역할은 숨겨야 합니다.");
                Check(viewer == 99 ? state.Word == "" : state.Word == initial[viewer].Word && state.LocalIsLiar == initial[viewer].LocalIsLiar,
                    "지목 교체는 본인에게 허용된 제시어·역할 공개 범위를 바꾸면 안 됩니다.");
            }
            Check(game.Snapshot(0, 0, 104).RemainingSeconds == 6 && 104 < deadline,
                "마지막 지목 시각을 기준으로 새 반론 제한 시간을 시작해야 합니다.");
            Check(game.Judge(0, 2, false, 104) && game.Judge(1, 2, false, 104) && game.Judge(3, 2, false, 104)
                && game.Phase == GamePhase.Discussion && game.BallotVersion > ballot
                && Enumerable.Range(0, 4).All(id => game.Snapshot(id, 0, 104).LocalVoteTargetId == -1)
                && game.Snapshot(0, 0, 104).Players.All(player => !player.HasVoted)
                && game.Vote(0, 1, 105), "부결 뒤 재투표는 선택을 초기화하고 새 제한 시간으로 제출을 받아야 합니다.");

            var optional = new GameSession(new RoomSettings { LiarMode = LiarMode.Optional }, Data(), 122);
            for (int id = 0; id < 4; id++) optional.Join(id, "P" + id, 0, 0);
            Check(optional.Join(99, "관전자", 0, 0, spectatorOnly: true) && optional.Start(0), "라이어 없음 선택의 변경 검증을 시작해야 합니다.");
            optional.Tick(100);
            while (optional.Phase == GamePhase.Drawing) optional.EndTurn(optional.ArtistId, 100);
            double optionalDeadline = 100 + optional.Snapshot(0, 0, 100).RemainingSeconds;
            Check(optional.Vote(0, GameRules.NO_LIAR_TARGET, 101)
                && optional.Snapshot(0, 0, 101).LocalVoteTargetId == -1 && optional.Snapshot(0, 0, 101).Players.Single(player => player.Id == 0).HasVoted,
                "라이어 없음 지목은 미제출과 같은 -1을 쓰되 제출 상태로 구분해야 합니다.");
            Check(optional.Vote(0, 1, 102) && optional.Snapshot(0, 0, 102).Players.Single(player => player.Id == 1).VoteCount == 1
                && optional.Vote(0, GameRules.NO_LIAR_TARGET, 103)
                && optional.Snapshot(0, 0, 103).Players.All(player => player.VoteCount == 0),
                "라이어 없음과 참가자 지목을 서로 교체하면 기존 참가자 득표를 정확히 제거해야 합니다.");
            Check(optional.Vote(1, 2, 104), "이탈할 후보를 지목해야 합니다.");
            optional.Disconnect(2, 105);
            Check(optional.Phase == GamePhase.Discussion && optional.Snapshot(1, 0, 105).LocalVoteTargetId == -1
                && !optional.Snapshot(1, 0, 105).Players.Single(player => player.Id == 1).HasVoted
                && !optional.Vote(0, 2, 105) && !optional.Vote(0, -2, 105)
                && optional.Snapshot(0, 0, 105).RemainingSeconds == optionalDeadline - 105,
                "후보 연결이 끊기면 해당 지목만 취소하고 끊긴 후보·무효 선택과 시간 연장을 막아야 합니다.");
            Check(optional.Vote(1, GameRules.NO_LIAR_TARGET, 106) && optional.Vote(3, GameRules.NO_LIAR_TARGET, 107)
                && optional.Phase == GamePhase.Rebuttal && optional.Snapshot(0, 0, 107).HasAccused
                && optional.Snapshot(0, 0, 107).AccusedPlayerId == GameRules.NO_LIAR_TARGET,
                "연결된 전원이 라이어 없음을 제출하면 미연결 참가자를 기다리지 않고 찬반을 시작해야 합니다.");

            var disconnected = DiscussionGame(123, 3);
            Check(disconnected.Vote(0, 1, 100) && disconnected.Vote(1, 0, 100), "이탈 후 전원 제출 상태가 될 지목을 준비해야 합니다.");
            disconnected.Disconnect(2, 100);
            Check(disconnected.Phase == GamePhase.Rebuttal && disconnected.Snapshot(0, 0, 100).RemainingSeconds == 6,
                "미제출자 이탈 후 남은 전원이 유효표를 제출했으면 반론을 바로 시작해야 합니다.");

            var invalidated = DiscussionGame(124, 3);
            Check(invalidated.Join(99, "관전자", 0, 0, spectatorOnly: true)
                && invalidated.Vote(0, 2, 100) && invalidated.Vote(1, 0, 100), "이탈 대상에게 제출한 지목을 준비해야 합니다.");
            invalidated.Disconnect(2, 101);
            var waiting = invalidated.Snapshot(0, 0, 101);
            Check(waiting.Phase == GamePhase.Discussion && !waiting.HasAccused && waiting.LocalVoteTargetId == -1
                && !waiting.Players.Single(player => player.Id == 0).HasVoted && waiting.RemainingSeconds == 44,
                "미제출자 이탈로 표적이 무효가 된 표는 먼저 취소하고 유효 지목을 다시 기다려야 합니다.");
            Check(!invalidated.Vote(0, 2, 102) && !invalidated.Vote(0, 99, 102) && !invalidated.Vote(0, 0, 102)
                && !invalidated.Vote(99, 1, 102) && invalidated.Phase == GamePhase.Discussion,
                "마지막 참가자의 잘못된 대상·자기 지목과 관전자 투표는 조기 확정을 일으키면 안 됩니다.");
            Check(invalidated.Vote(0, 1, 103) && invalidated.Phase == GamePhase.Rebuttal,
                "무효 표를 유효 지목으로 다시 제출하면 연결된 참가자만으로 즉시 반론을 시작해야 합니다.");
        }

        private static void VerifyConsensusVoting()
        {
            var selected = new System.Collections.Generic.HashSet<int>();
            for (int seed = 0; seed < 30; seed++)
            {
                int Candidate()
                {
                    var tie = DiscussionGame(seed);
                    Check(tie.Vote(0, 1, 100) && tie.Vote(1, 0, 100) && tie.Vote(2, 3, 100), "동률 지목을 준비해야 합니다.");
                    Check(tie.Phase == GamePhase.Discussion, "전원 제출 전에는 토론을 유지해야 합니다.");
                    Check(tie.Vote(3, 2, 100), "마지막 지목을 제출해야 합니다.");
                    double nominationEnd = 100;
                    var state = tie.Snapshot(0, 0, nominationEnd);
                    Check(tie.Phase == GamePhase.Rebuttal && state.HasAccused && state.AccusedPlayerId >= 0 && state.AccusedPlayerId < 4
                        && state.Players.All(player => !player.IsCaught && !player.IsLiar && player.RoundPoints == 0 && player.Score == 0),
                        "동률은 서버가 한 후보를 선택하고 역할·점수는 비공개로 유지해야 합니다.");
                    return state.AccusedPlayerId;
                }
                int first = Candidate();
                Check(first == Candidate(), "같은 서버 시드의 동률 후보는 재현 가능해야 합니다.");
                selected.Add(first);
            }
            Check(selected.Count > 1, "서버 동률 선정이 ID 순서 한 명에 고정되면 안 됩니다.");

            var game = DiscussionGame(41);
            double now = 100;
            int liar = Enumerable.Range(0, 4).Single(id => game.Snapshot(id, 0, now).LocalIsLiar);
            int[] citizens = Enumerable.Range(0, 4).Where(id => id != liar).ToArray();
            int candidate = citizens[0], repeatVoter = citizens[1], changingVoter = citizens[2];
            int canvas = game.CanvasVersion;
            CompletedMatchData completed = null;
            game.MatchCompleted += value => completed = value;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                now = NominateAll(game, candidate, now);
                var before = game.Snapshot(repeatVoter, 0, now);
                Check(!game.Judge(candidate, candidate, true, now), "후보는 자신의 찬반에 참가할 수 없습니다.");
                Check(game.Judge(repeatVoter, candidate, false, now), "시민 후보에 반대할 수 있어야 합니다.");
                Check(!game.Judge(repeatVoter, candidate, true, now), "같은 찬반에서 표를 다시 제출할 수 없습니다.");
                Check(game.Judge(changingVoter, candidate, attempt == 0, now), "다른 참가자가 찬반을 제출해야 합니다.");
                Check(game.Judge(liar, candidate, attempt != 0, now), "라이어도 후보가 아니면 찬반에 참가할 수 있습니다.");
                var after = game.Snapshot(repeatVoter, 0, now);
                Check(game.Phase == GamePhase.Discussion && game.Round == 1 && game.CanvasVersion == canvas
                    && !after.HasAccused && after.BallotVersion > before.BallotVersion
                    && after.Players.All(player => !player.HasVoted && !player.HasJudged && !player.IsLiar && player.Score == 0 && player.RoundPoints == 0),
                    "부결은 같은 그림·라운드의 새 토론을 열고 표를 초기화하며 보상 정보를 숨겨야 합니다.");
            }
            now = NominateAll(game, liar, now);
            ApproveAll(game, now);
            var reveal = game.Snapshot(liar, 0, now);
            Check(game.Phase == GamePhase.LiarReveal && reveal.Players.Count(player => player.IsCaught) == 1
                && reveal.Players.Single(player => player.IsCaught).Id == liar
                && reveal.Players.All(player => player.Score == 0 && player.RoundPoints == 0) && reveal.Word == "",
                "가결 후보 한 명만 잡히고 보상은 아직 노출되면 안 됩니다.");
            game.Tick(now += 100);
            Check(game.Guess(liar, "틀린 답", now), "라이어 추측으로 라운드를 마쳐야 합니다.");
            var result = game.Snapshot(0, 0, now);
            Check(result.Players.All(player => player.Score == 2 && player.RoundPoints == 2)
                && completed != null && completed.Players.All(player => player.CorrectVotes == 1 && player.RoundsPlayed == 1),
                "올바른 찬성·반대는 역할과 무관하게 보상하되 같은 라운드의 반복 부결로 점수를 늘릴 수 없습니다.");

            var fallback = DiscussionGame(42, 3, 0);
            Check(fallback.Vote(0, 1, 100), "토론 시간 제한의 일부 지목을 제출해야 합니다.");
            fallback.Tick(200);
            var rebuttal = fallback.Snapshot(0, 0, 200);
            Check(fallback.Phase == GamePhase.Rebuttal && rebuttal.RemainingSeconds == 8 && rebuttal.JudgmentVoterCount == 2,
                "반론 시간이 0이어도 기존 투표 시간을 찬반 제한으로 사용해야 합니다.");
            int[] eligible = rebuttal.Players.Where(player => player.Id != rebuttal.AccusedPlayerId).Select(player => player.Id).ToArray();
            Check(fallback.Judge(eligible[0], rebuttal.AccusedPlayerId, false, 200), "첫 찬반을 제출해야 합니다.");
            Check(fallback.Judge(eligible[1], rebuttal.AccusedPlayerId, true, 200) && fallback.Phase == GamePhase.Discussion,
                "첫 찬성·반대 동률이면 부결하고 새 토론을 열어야 합니다.");

            var noVote = DiscussionGame(43);
            noVote.Tick(200);
            Check(noVote.Phase == GamePhase.LiarReveal && !noVote.Snapshot(0, 0, 200).HasAccused
                && noVote.Snapshot(0, 0, 200).Players.All(player => !player.IsCaught), "무지목 토론 시간 초과는 아무도 잡지 않아야 합니다.");

            var disconnected = DiscussionGame(44);
            int citizen = Enumerable.Range(0, 4).First(id => !disconnected.Snapshot(id, 0, 100).LocalIsLiar);
            double disconnectedNow = NominateAll(disconnected, citizen);
            var stateBefore = disconnected.Snapshot(0, 0, disconnectedNow);
            int voter = stateBefore.Players.First(player => player.Id != citizen).Id;
            Check(disconnected.Judge(voter, citizen, false, disconnectedNow), "반론 중 표를 제출해야 합니다.");
            disconnected.Disconnect(voter, disconnectedNow);
            Check(disconnected.Join(voter, "복귀", 0, 0), "찬반 참가자가 재접속해야 합니다.");
            var reconnected = disconnected.Snapshot(voter, 0, disconnectedNow);
            Check(reconnected.BallotVersion == stateBefore.BallotVersion && reconnected.JudgmentVotesCast == 1
                && reconnected.Players.Single(player => player.Id == voter).HasJudged && !reconnected.LocalJudgmentApprove
                && !disconnected.Judge(voter, citizen, true, disconnectedNow), "같은 찬반의 재접속은 제출 상태·선택을 복원하고 중복을 막아야 합니다.");
            disconnected.Disconnect(citizen, disconnectedNow);
            var canceled = disconnected.Snapshot(voter, 0, disconnectedNow);
            Check(disconnected.Phase == GamePhase.Discussion && !canceled.HasAccused && canceled.BallotVersion > stateBefore.BallotVersion
                && canceled.Players.All(player => !player.HasJudged && !player.HasVoted), "후보가 나가면 기존 표를 무효화하고 새 토론을 열어야 합니다.");
            disconnected.Tick(disconnectedNow += 100); disconnected.Tick(disconnectedNow += 100); disconnected.Tick(disconnectedNow += 100);
            Check(disconnected.Snapshot(voter, 0, disconnectedNow).Players.All(player => player.RoundPoints == (player.IsLiar ? 2 : 0)),
                "취소된 후보의 찬반은 점수 보상을 만들면 안 됩니다.");

            var eligibility = DiscussionGame(45, 3);
            double eligibilityNow = NominateAll(eligibility, 0);
            Check(eligibility.Judge(1, 0, true, eligibilityNow), "찬성표를 제출해야 합니다.");
            eligibility.Disconnect(1, eligibilityNow);
            var remaining = eligibility.Snapshot(2, 0, eligibilityNow);
            Check(remaining.JudgmentVoterCount == 1 && remaining.JudgmentVotesCast == 0
                && remaining.ApprovalCount == 0 && eligibility.Judge(2, 0, false, eligibilityNow) && eligibility.Phase == GamePhase.Discussion,
                "끊긴 참가자는 찬반 모수와 집계에서 빠지고 현재 참가자의 제출로 판정해야 합니다.");

            var lateNomination = DiscussionGame(46, 3);
            Check(lateNomination.Vote(0, 1, 100), "시간 초과 전의 지목은 수락해야 합니다.");
            Check(!lateNomination.Vote(1, 2, 145) && lateNomination.Phase == GamePhase.Rebuttal
                && lateNomination.Snapshot(0, 0, 145).AccusedPlayerId == 1
                && !lateNomination.Snapshot(0, 0, 145).Players.Single(player => player.Id == 1).HasVoted,
                "정확한 토론 기한의 늦은 지목은 기존 제출로 후보를 판정하고 거부해야 합니다.");
            var lateJudgment = DiscussionGame(47, 3);
            double judgmentNow = NominateAll(lateJudgment, 0);
            double judgmentEnd = judgmentNow + lateJudgment.Settings.RebuttalSeconds;
            int expiredEpoch = lateJudgment.Snapshot(1, 0, judgmentNow).BallotVersion;
            Check(lateJudgment.Judge(1, 0, false, judgmentNow), "시간 초과 전의 반대는 수락해야 합니다.");
            Check(!lateJudgment.Judge(2, 0, true, judgmentEnd) && lateJudgment.Phase == GamePhase.Discussion
                && lateJudgment.Snapshot(2, 0, judgmentEnd).BallotVersion > expiredEpoch
                && lateJudgment.Snapshot(2, 0, judgmentEnd).Players.All(player => !player.HasJudged && player.Score == 0 && player.RoundPoints == 0),
                "정확한 찬반 기한의 늦은 찬성은 앞 판정을 뒤집거나 새 토론에 제출되면 안 됩니다.");
        }

        private static void SplitJudgment(GameSession game, int accused, double now, bool reverse = false)
        {
            int[] voters = game.Snapshot(0, 0, now).Players.Where(player => player.IsConnected && !player.IsSpectator && player.Id != accused)
                .Select(player => player.Id).ToArray();
            Check(voters.Length == 2 && game.Judge(voters[0], accused, reverse, now) && game.Judge(voters[1], accused, !reverse, now),
                "두 유효 참가자의 찬반을 동률로 제출해야 합니다.");
        }

        private static GameSession SecondTieGame(int seed, out double now, int rounds = 1)
        {
            var game = DiscussionGame(seed, 3, rounds: rounds);
            now = NominateAll(game, 0);
            int ballot = game.BallotVersion, canvas = game.CanvasVersion;
            SplitJudgment(game, 0, now);
            var first = game.Snapshot(0, 0, now);
            Check(game.Phase == GamePhase.Discussion && !first.IsJudgmentCoinToss && !first.JudgmentCoinApproved
                && !first.HasAccused && game.BallotVersion > ballot && game.CanvasVersion == canvas && game.Round == 1,
                "첫 동률은 동전 없이 같은 라운드·그림의 새 토론을 열어야 합니다.");
            now = NominateAll(game, 0, now);
            SplitJudgment(game, 0, now, true);
            return game;
        }

        private static void VerifyJudgmentCoinToss()
        {
            Check((int)GamePhase.Rebuttal == 4 && (int)GamePhase.LiarReveal == 6 && (int)GamePhase.MatchResults == 9,
                "동전 상태를 추가해도 기존 단계 번호와 역할 공개 순서를 유지해야 합니다.");
            var outcomes = new System.Collections.Generic.HashSet<bool>();
            int rejectedSeed = -1;
            for (int seed = 0; seed < 32; seed++)
            {
                var game = SecondTieGame(seed, out double now);
                Check(game.Join(99, "관전자", 0, 0, spectatorOnly: true), "동전 검증 관전자를 추가해야 합니다.");
                var toss = game.Snapshot(0, 0, now);
                Check(toss.Phase == GamePhase.Rebuttal && toss.IsJudgmentCoinToss && toss.RemainingSeconds == 3
                    && toss.ApprovalCount == 1 && toss.RejectionCount == 1 && toss.HasAccused,
                    "두 번째 동률은 서버 결과를 저장하고 반론 안에서 정확히 3초간 기다려야 합니다.");
                bool approved = toss.JudgmentCoinApproved;
                outcomes.Add(approved);
                if (!approved) rejectedSeed = seed;
                var reproduced = SecondTieGame(seed, out double reproducedNow);
                Check(reproduced.Snapshot(0, 0, reproducedNow).JudgmentCoinApproved == approved,
                    "같은 서버 시드는 같은 동전 결과를 재현해야 합니다.");
                foreach (int viewer in new[] { 0, 1, 2, 99 })
                {
                    var state = game.Snapshot(viewer, 0, now);
                    Check(state.IsJudgmentCoinToss && state.JudgmentCoinApproved == approved && state.RevealedLiarCount == -1
                        && state.Players.All(player => !player.IsLiar && !player.IsCaught && player.RoundPoints == 0 && player.Score == 0)
                        && (viewer != 99 || state.Word == ""), "동전 중 참가자와 관전자는 같은 결과를 받고 역할·중간 점수를 볼 수 없어야 합니다.");
                }
                Check(!game.Judge(1, 0, false, now + 1) && !game.Judge(99, 0, true, now + 1) && !game.Vote(2, 1, now + 1),
                    "동전 연출 중 늦은 찬반·관전자 표·새 지목은 모두 거부해야 합니다.");
                game.Tick(now + 2.999);
                Check(game.Snapshot(0, 0, now + 2.999).IsJudgmentCoinToss && game.Snapshot(0, 0, now + 2.999).JudgmentCoinApproved == approved,
                    "동전 결과는 마감 직전까지 적용하거나 재추첨하면 안 됩니다.");
                Check(!game.Judge(1, 0, true, now + 3), "동전 마감의 늦은 표는 타이머 결과만 처리하고 거부해야 합니다.");
                now += 3;
                var applied = game.Snapshot(0, 0, now);
                Check(game.Phase == (approved ? GamePhase.LiarReveal : GamePhase.Discussion) && !applied.IsJudgmentCoinToss
                    && !applied.JudgmentCoinApproved && (approved ? applied.Players.Single(player => player.Id == 0).IsCaught : !applied.HasAccused),
                    "저장된 동전 결과는 마감에 가결 또는 부결로 한 번만 적용해야 합니다.");
                int changes = 0; game.Changed += () => changes++;
                game.Tick(now);
                Check(changes == 0, "같은 마감 Tick을 반복해도 동전 결과를 다시 적용하면 안 됩니다.");
                if (!approved) { now = NominateAll(game, 0, now); ApproveAll(game, now); }
                CompletedMatchData completed = null; game.MatchCompleted += value => completed = value;
                game.Tick(now += 100); game.Tick(now += 100);
                Check(completed != null && completed.Players.Where(player => player.PlayerId != 0).All(player => player.CorrectVotes == 1)
                    && completed.Players.Single(player => player.PlayerId == 0).CorrectVotes == 0,
                    "첫 동률과 동전의 올바른 표는 누적하되 보상 횟수는 라운드당 한 번이어야 합니다.");
            }
            Check(outcomes.Count == 2 && rejectedSeed >= 0, "여러 서버 시드에서 가결과 부결 동전 결과가 모두 나와야 합니다.");

            var third = SecondTieGame(rejectedSeed, out double thirdNow);
            third.Tick(thirdNow += 3);
            thirdNow = NominateAll(third, 0, thirdNow); SplitJudgment(third, 0, thirdNow);
            Check(third.Snapshot(0, 0, thirdNow).IsJudgmentCoinToss && third.Snapshot(0, 0, thirdNow).RemainingSeconds == 3,
                "같은 라운드의 세 번째 동률도 동전으로 결정해야 합니다.");

            var empty = DiscussionGame(90, 3);
            double emptyNow = NominateAll(empty, 0); emptyNow = TickDeadline(empty, emptyNow);
            Check(empty.Phase == GamePhase.Discussion && !empty.Snapshot(0, 0, emptyNow).IsJudgmentCoinToss,
                "시간 만료의 첫 0대0도 첫 동률 부결로 처리해야 합니다.");
            emptyNow = NominateAll(empty, 0, emptyNow); emptyNow = TickDeadline(empty, emptyNow);
            Check(empty.Snapshot(0, 0, emptyNow).IsJudgmentCoinToss && empty.Snapshot(0, 0, emptyNow).RemainingSeconds == 3,
                "시간 만료의 두 번째 0대0도 3초 동전으로 처리해야 합니다.");

            var reconnect = SecondTieGame(91, out double reconnectNow);
            var frozen = reconnect.Snapshot(1, 0, reconnectNow);
            reconnect.Disconnect(1, reconnectNow + 1);
            Check(reconnect.Snapshot(2, 0, reconnectNow + 1).IsJudgmentCoinToss && reconnect.Join(1, "복귀", 0, 0),
                "투표자 이탈·재접속은 동전을 취소하지 않아야 합니다.");
            Check(reconnect.Join(3, "난입", 0, 0) && !reconnect.Judge(3, 0, true, reconnectNow + 1), "동전 중 난입자는 표를 추가할 수 없어야 합니다.");
            var resumed = reconnect.Snapshot(1, 0, reconnectNow + 1);
            Check(resumed.JudgmentCoinApproved == frozen.JudgmentCoinApproved && resumed.RemainingSeconds == 2
                && resumed.BallotVersion == frozen.BallotVersion && resumed.Players.Single(player => player.Id == 1).HasJudged,
                "재접속은 제출 상태·동전 결과·기한을 그대로 복원해야 합니다.");
            reconnect.Tick(reconnectNow + 3);
            Check(reconnect.Phase == (frozen.JudgmentCoinApproved ? GamePhase.LiarReveal : GamePhase.Discussion),
                "인원 변화는 이미 저장된 동전 결과를 바꾸면 안 됩니다.");

            var canceled = SecondTieGame(92, out double canceledNow);
            bool liarAccused = canceled.Snapshot(0, 0, canceledNow).LocalIsLiar;
            canceled.Disconnect(0, canceledNow + 1);
            Check(canceled.Phase == GamePhase.Discussion && !canceled.Snapshot(1, 1, canceledNow + 1).IsJudgmentCoinToss
                && canceled.Join(0, "복귀", 0, 0), "후보 이탈은 동전을 취소하고 재접속해도 부활시키지 않아야 합니다.");
            CompletedMatchData canceledReport = null; canceled.MatchCompleted += value => canceledReport = value;
            canceled.Tick(canceledNow += 100); canceled.Tick(canceledNow += 100); canceled.Tick(canceledNow += 100);
            int firstCorrect = liarAccused ? 2 : 1, canceledCorrect = liarAccused ? 1 : 2;
            Check(canceledReport != null && canceledReport.Players.Single(player => player.PlayerId == firstCorrect).CorrectVotes == 1
                && canceledReport.Players.Single(player => player.PlayerId == canceledCorrect).CorrectVotes == 0,
                "취소된 동전 표는 새 보상을 만들지 않고 앞 동률의 올바른 표만 보존해야 합니다.");

            var insufficient = SecondTieGame(93, out double insufficientNow);
            insufficient.Disconnect(1, insufficientNow + 1); insufficient.Disconnect(2, insufficientNow + 1);
            Check(insufficient.Phase == GamePhase.MatchResults && !insufficient.Snapshot(0, 0, insufficientNow + 1).IsJudgmentCoinToss,
                "참가자 부족 종료는 동전 상태를 제거해야 합니다.");

            var mismatch = new GameSession(new RoomSettings { LiarMode = LiarMode.Mismatch, RoundCount = 1 },
                new GameData { Topics = new[] { new TopicData { Name = "과일", Words = new[] { "사과", "배" } } } }, 95);
            for (int id = 0; id < 3; id++) mismatch.Join(id, "P" + id, 0, 0);
            Check(mismatch.Start(0), "미스매치 동전 검증을 시작해야 합니다.");
            mismatch.Tick(100); while (mismatch.Phase == GamePhase.Drawing) mismatch.EndTurn(mismatch.ArtistId, 100);
            double mismatchNow = 100;
            for (int attempt = 0; attempt < 2; attempt++) { mismatchNow = NominateAll(mismatch, 0, mismatchNow); SplitJudgment(mismatch, 0, mismatchNow); }
            for (int viewer = 0; viewer < 3; viewer++)
            {
                var hidden = mismatch.Snapshot(viewer, 0, mismatchNow);
                Check(hidden.IsJudgmentCoinToss && !hidden.LocalIsLiar && hidden.RevealedLiarCount == -1 && hidden.MismatchWord == ""
                    && hidden.Players.All(player => !player.IsLiar && !player.IsCaught),
                    "미스매치 동전 중에도 본인·공용 실제 역할과 상대 제시어를 공개하면 안 됩니다.");
            }

            var next = SecondTieGame(94, out double nextNow, 2);
            var nextToss = next.Snapshot(0, 0, nextNow);
            next.Tick(nextNow += 3);
            if (!nextToss.JudgmentCoinApproved) { nextNow = NominateAll(next, 0, nextNow); ApproveAll(next, nextNow); }
            next.Tick(nextNow += 100); next.Tick(nextNow += 100); next.Tick(nextNow += 100); next.Tick(nextNow += 100);
            while (next.Phase == GamePhase.Drawing) next.EndTurn(next.ArtistId, nextNow);
            Check(next.Round == 2 && next.Phase == GamePhase.Discussion, "새 라운드의 토론으로 진행해야 합니다.");
            nextNow = NominateAll(next, 0, nextNow); SplitJudgment(next, 0, nextNow);
            Check(next.Phase == GamePhase.Discussion && !next.Snapshot(0, 0, nextNow).IsJudgmentCoinToss,
                "새 라운드의 첫 동률은 앞 라운드의 동률 횟수와 무관하게 부결해야 합니다.");
            third.ReturnToLobby();
            Check(third.Phase == GamePhase.Lobby && !third.Snapshot(0, 0, thirdNow).IsJudgmentCoinToss
                && !third.Snapshot(0, 0, thirdNow).JudgmentCoinApproved, "대기방 복귀는 동전 결과와 연출 상태를 제거해야 합니다.");
        }
    }
}
