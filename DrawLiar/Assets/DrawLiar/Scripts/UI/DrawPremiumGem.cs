using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawPremiumGem : VisualElement
    {
        public DrawPremiumGem()
        {
            AddToClassList("commerce-gem"); pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
            Vector2 Point(float x, float y) => contentRect.center + new Vector2((x - .5f) * size, (y - .5f) * size);
            var painter = context.painter2D;
            painter.fillColor = new Color32(201, 184, 245, 255);
            painter.strokeColor = new Color32(94, 77, 132, 255); painter.lineWidth = Mathf.Max(1, size / 28);
            painter.BeginPath(); painter.MoveTo(Point(.25f, .16f)); painter.LineTo(Point(.75f, .16f));
            painter.LineTo(Point(.96f, .42f)); painter.LineTo(Point(.5f, .94f)); painter.LineTo(Point(.04f, .42f));
            painter.ClosePath(); painter.Fill(); painter.Stroke();
            painter.BeginPath(); painter.MoveTo(Point(.04f, .42f)); painter.LineTo(Point(.96f, .42f));
            painter.MoveTo(Point(.25f, .16f)); painter.LineTo(Point(.35f, .42f)); painter.LineTo(Point(.5f, .94f));
            painter.MoveTo(Point(.75f, .16f)); painter.LineTo(Point(.65f, .42f)); painter.LineTo(Point(.5f, .94f)); painter.Stroke();
        }
    }
}
