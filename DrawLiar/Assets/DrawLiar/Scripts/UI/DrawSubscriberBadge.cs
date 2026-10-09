using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawSubscriberBadge : VisualElement
    {
        public DrawSubscriberBadge()
        {
            AddToClassList("draw-subscriber-badge");
            pickingMode = PickingMode.Position;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
            var painter = context.painter2D;
            Vector2 Point(float x, float y) => contentRect.center + new Vector2((x - .5f) * size, (y - .5f) * size);
            painter.fillColor = new Color32(255, 226, 159, 255);
            painter.strokeColor = new Color32(82, 67, 90, 255); painter.lineWidth = Mathf.Max(1, size / 20);
            painter.BeginPath(); painter.MoveTo(Point(.09f, .55f));
            painter.BezierCurveTo(Point(.09f, .26f), Point(.33f, .08f), Point(.60f, .08f));
            painter.BezierCurveTo(Point(.90f, .08f), Point(1.05f, .43f), Point(.86f, .54f));
            painter.BezierCurveTo(Point(.69f, .56f), Point(.67f, .72f), Point(.74f, .80f));
            painter.BezierCurveTo(Point(.62f, 1.05f), Point(.09f, .91f), Point(.09f, .55f));
            painter.ClosePath(); painter.Fill(); painter.Stroke();
            void Dot(float x, float y, float radius, Color color)
            {
                painter.fillColor = color; painter.BeginPath(); painter.MoveTo(Point(x + radius, y));
                painter.BezierCurveTo(Point(x + radius, y + radius * .55f), Point(x + radius * .55f, y + radius), Point(x, y + radius));
                painter.BezierCurveTo(Point(x - radius * .55f, y + radius), Point(x - radius, y + radius * .55f), Point(x - radius, y));
                painter.BezierCurveTo(Point(x - radius, y - radius * .55f), Point(x - radius * .55f, y - radius), Point(x, y - radius));
                painter.BezierCurveTo(Point(x + radius * .55f, y - radius), Point(x + radius, y - radius * .55f), Point(x + radius, y));
                painter.ClosePath(); painter.Fill();
            }
            Dot(.34f, .28f, .09f, new Color32(238, 128, 156, 255));
            Dot(.63f, .25f, .09f, new Color32(137, 120, 197, 255));
            Dot(.27f, .53f, .09f, new Color32(115, 180, 178, 255));
            Dot(.39f, .77f, .09f, new Color32(245, 181, 83, 255));
            Dot(.57f, .58f, .09f, Color.white);
        }
    }
}
