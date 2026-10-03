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
        public int Target;
        public RoomSettings Settings;
        public RoomSnapshot State;
        public DrawStroke Stroke;
        public int Version;
        public bool Reset;
        public DrawStroke[] Strokes;
        public ChatLine Line;
        public long Sequence;
    }
}
