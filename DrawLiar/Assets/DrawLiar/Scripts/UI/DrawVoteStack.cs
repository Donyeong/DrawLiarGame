using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawVoteStack : VisualElement
    {
        public int Count => childCount;

        public DrawVoteStack()
        {
            AddToClassList("player-vote-stack");
            pickingMode = PickingMode.Ignore;
        }

        public void SetCount(int count)
        {
            count = Mathf.Clamp(count, 0, GameRules.MAX_PLAYERS);
            if (count == Count) return;
            while (childCount > count) RemoveAt(childCount - 1);
            while (childCount < count)
            {
                var token = new DrawUIIcon(DrawUIIcon.Kind.Ballot);
                token.AddToClassList("vote-token");
                Add(token);
                DrawUIMotion.Enter(token, 180, 3);
            }
            EnableInClassList("has-votes", count > 0);
        }
    }
}
