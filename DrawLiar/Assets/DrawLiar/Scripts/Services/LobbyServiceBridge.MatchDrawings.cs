using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DrawLiar
{
    public sealed partial class LobbyServiceBridge
    {
        public async Task<MatchDrawingListResponse> GetMatchDrawingsAsync(string matchId, CancellationToken cancellationToken)
        {
            var response = await ReadMatchDrawingAsync<MatchDrawingListResponse>(matchId, "", cancellationToken);
            if (response == null || !string.Equals(response.MatchId, matchId, StringComparison.OrdinalIgnoreCase) || !ValidDrawingExpiry(response.ExpiresAt)
                || response.RetentionDays < 1 || response.RetentionDays > DrawingReplayRules.MAX_RETENTION_DAYS
                || response.Rounds == null || response.Rounds.Length < 1 || response.Rounds.Length > DrawingReplayRules.MAX_ROUNDS
                || response.Rounds.Any(round => round == null || round.Round < 1 || round.Round > DrawingReplayRules.MAX_ROUNDS
                    || round.PlayerCount < 1 || round.PlayerCount > DrawingReplayRules.MAX_PLAYERS_PER_ROUND
                    || round.StrokeCount < 0 || round.StrokeCount > DrawingReplayRules.MAX_STROKES_PER_ROUND)
                || response.Rounds.Select(round => round.Round).Distinct().Count() != response.Rounds.Length)
                throw new InvalidOperationException("그림을 불러오지 못했습니다.");
            response.Rounds = response.Rounds.OrderBy(round => round.Round).ToArray();
            return response;
        }

        public async Task<MatchDrawingResponse> GetMatchDrawingAsync(string matchId, int round, CancellationToken cancellationToken)
        {
            if (round < 1 || round > DrawingReplayRules.MAX_ROUNDS)
                throw new InvalidOperationException("그림을 불러오지 못했습니다.");
            var response = await ReadMatchDrawingAsync<MatchDrawingResponse>(matchId, "/" + round.ToString(CultureInfo.InvariantCulture), cancellationToken);
            if (response == null || !string.Equals(response.MatchId, matchId, StringComparison.OrdinalIgnoreCase) || !ValidDrawingExpiry(response.ExpiresAt)
                || !DrawingReplayRules.ValidRound(response.Drawing) || response.Drawing.Round != round)
                throw new InvalidOperationException("그림을 불러오지 못했습니다.");
            return response;
        }

        private async Task<T> ReadMatchDrawingAsync<T>(string matchId, string suffix, CancellationToken cancellationToken) where T : class, new()
        {
            RequireLogin();
            if (!Guid.TryParse(matchId, out var id) || id == Guid.Empty)
                throw new InvalidOperationException("그림을 불러오지 못했습니다.");
            string session = _gameSession, account = Profile.AccountId;
            var response = await SendAsync<T>(_gameServerUrl, "/api/matches/" + id + "/drawings" + suffix, "GET",
                bearer: session, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != _gameSession || account != Profile?.AccountId) throw new OperationCanceledException(cancellationToken);
            return response;
        }

        private static bool ValidDrawingExpiry(string expiry) => DateTimeOffset.TryParse(expiry,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }
}
