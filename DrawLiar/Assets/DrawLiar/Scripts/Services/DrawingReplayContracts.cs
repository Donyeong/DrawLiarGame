using System;

namespace DrawLiar
{
    [Serializable] public sealed class ReplayPlayerData
    {
        public int PlayerId;
        public string AccountId = "";
        public string DisplayName = "";
    }

    [Serializable] public struct ReplayStrokeData
    {
        public float X1, Y1, X2, Y2, Size, StartSize;
        public byte R, G, B;
        public bool Eraser;
        public int AuthorPlayerId;
    }

    [Serializable] public sealed class RoundDrawingData
    {
        public int Round;
        public string Topic = "";
        public string Word = "";
        public ReplayPlayerData[] Players = Array.Empty<ReplayPlayerData>();
        public ReplayStrokeData[] Strokes = Array.Empty<ReplayStrokeData>();
    }

    [Serializable] public sealed class RoundDrawingSummary
    {
        public int Round;
        public string Topic = "";
        public string Word = "";
        public int PlayerCount;
        public int StrokeCount;
    }

    [Serializable] public sealed class RoundDrawingArchiveData
    {
        public int Round;
        public byte[] Payload = Array.Empty<byte>();
    }

    [Serializable] public sealed class MatchDrawingListResponse
    {
        public string MatchId = "";
        public string ExpiresAt = "";
        public int RetentionDays;
        public bool IsPartial;
        public RoundDrawingSummary[] Rounds = Array.Empty<RoundDrawingSummary>();
    }

    [Serializable] public sealed class MatchDrawingResponse
    {
        public string MatchId = "";
        public string ExpiresAt = "";
        public RoundDrawingData Drawing = new RoundDrawingData();
    }
}
