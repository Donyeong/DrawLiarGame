#nullable disable
using System;

namespace DrawLiar
{
    [Serializable]
    public sealed class GameplayEnvelope
    {
        public string Type;
        public string Ticket;
        public string RoomId;
        public string Kind;
        public string Text;
        public string Password;
        public string RequestId;
        public string Code;
        public bool Accepted;
        public int Target;
        public bool Approve;
        public int BallotVersion;
        public RoomSettings Settings;
        public RoomSnapshot State;
        public DrawStroke Stroke;
        public int Version;
        public int Round;
        public int DrawingEpoch;
        public bool Reset;
        public bool Complete;
        public DrawStroke[] Strokes;
        public ChatLine Line;
        public long Sequence;
    }
}
