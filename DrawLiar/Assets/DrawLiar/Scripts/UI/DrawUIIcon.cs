using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawUIIcon : VisualElement
    {
        public enum Kind { Refresh, Settings, Close }
        private readonly Kind _kind;

        public DrawUIIcon(Kind kind)
        {
            _kind = kind;
            AddToClassList("draw-ui-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
            Vector2 origin = contentRect.center;
            Vector2 Point(float x, float y) => origin + new Vector2(x - .5f, y - .5f) * size;
            painter.strokeColor = resolvedStyle.color;
            painter.lineWidth = size / 12;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;

            if (_kind == Kind.Refresh)
            {
                painter.BeginPath();
                painter.MoveTo(Point(.84f, .52f));
                painter.BezierCurveTo(Point(.84f, .72f), Point(.70f, .86f), Point(.50f, .86f));
                painter.BezierCurveTo(Point(.30f, .86f), Point(.14f, .70f), Point(.14f, .50f));
                painter.BezierCurveTo(Point(.14f, .30f), Point(.30f, .14f), Point(.50f, .14f));
                painter.BezierCurveTo(Point(.62f, .14f), Point(.74f, .21f), Point(.82f, .32f));
                painter.Stroke();
                painter.BeginPath();
                painter.MoveTo(Point(.82f, .12f));
                painter.LineTo(Point(.82f, .32f));
                painter.LineTo(Point(.62f, .32f));
                painter.Stroke();
                return;
            }

            if(_kind==Kind.Close)
            {
                painter.BeginPath();painter.MoveTo(Point(.25f,.25f));painter.LineTo(Point(.75f,.75f));painter.Stroke();
                painter.BeginPath();painter.MoveTo(Point(.75f,.25f));painter.LineTo(Point(.25f,.75f));painter.Stroke();return;
            }

            painter.BeginPath();
            for (int index = 0; index < 32; index++)
            {
                float angle = index * Mathf.PI / 16;
                float radius = (index % 4 == 1 || index % 4 == 2) ? .38f : .29f;
                Vector2 point = origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (radius * size);
                if (index == 0) painter.MoveTo(point);
                else painter.LineTo(point);
            }
            painter.ClosePath();
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(Point(.63f, .50f));
            painter.BezierCurveTo(Point(.63f, .572f), Point(.572f, .63f), Point(.50f, .63f));
            painter.BezierCurveTo(Point(.428f, .63f), Point(.37f, .572f), Point(.37f, .50f));
            painter.BezierCurveTo(Point(.37f, .428f), Point(.428f, .37f), Point(.50f, .37f));
            painter.BezierCurveTo(Point(.572f, .37f), Point(.63f, .428f), Point(.63f, .50f));
            painter.ClosePath();
            painter.Stroke();
        }
    }
}
