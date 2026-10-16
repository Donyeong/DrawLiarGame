using System;
using System.Collections.Generic;

namespace DrawLiar
{
    public static class DrawingReplayRules
    {
        public const int DEFAULT_RETENTION_DAYS = 30;
        public const int MAX_RETENTION_DAYS = 365;
        public const int MAX_ROUNDS = 1000;
        public const int MAX_PLAYERS_PER_ROUND = 64;
        public const int MAX_STROKES_PER_ROUND = 12000 * ServerRoomSettings.MAX_PLAYERS;
        public const int MAX_COMPRESSED_ROUND_BYTES = 8 * 1024 * 1024;
        public const int MAX_COMPRESSED_MATCH_BYTES = 128 * 1024 * 1024;
        public const long MAX_REQUEST_BYTES = 192L * 1024 * 1024;
        public const int MAX_ROUND_JSON_BYTES = 32 * 1024 * 1024;

        public static bool ValidRound(RoundDrawingData drawing)
        {
            if (drawing == null || drawing.Round < 1 || drawing.Round > MAX_ROUNDS
                || !ValidText(drawing.Topic, 100) || !ValidText(drawing.Word, 100)
                || drawing.Players == null || drawing.Players.Length < 1 || drawing.Players.Length > MAX_PLAYERS_PER_ROUND
                || drawing.Strokes == null || drawing.Strokes.Length > MAX_STROKES_PER_ROUND) return false;
            var ids = new HashSet<int>();
            var accounts = new HashSet<Guid>();
            foreach (var player in drawing.Players)
                if (player == null || player.PlayerId <= 0 || !ids.Add(player.PlayerId)
                    || !Guid.TryParse(player.AccountId, out Guid account) || account == Guid.Empty || !accounts.Add(account)
                    || !ValidText(player.DisplayName, 24)) return false;
            foreach (var stroke in drawing.Strokes)
                if (!ids.Contains(stroke.AuthorPlayerId) || !ValidStroke(stroke)) return false;
            return true;
        }

        public static bool ValidStroke(ReplayStrokeData stroke) =>
            InRange(stroke.X1, 0, 1) && InRange(stroke.Y1, 0, 1) && InRange(stroke.X2, 0, 1) && InRange(stroke.Y2, 0, 1)
            && InRange(stroke.Size, .001f, .08f) && (stroke.StartSize == 0 || InRange(stroke.StartSize, .001f, .08f));

        private static bool InRange(float value, float minimum, float maximum) => value >= minimum && value <= maximum;

        private static bool ValidText(string value, int maximum)
        {
            if (value == null || value.Length > maximum) return false;
            foreach (char character in value)
                if (char.IsControl(character) || character == '<' || character == '>') return false;
            return true;
        }
    }
}
