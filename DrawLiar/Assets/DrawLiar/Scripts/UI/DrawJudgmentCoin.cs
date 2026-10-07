using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawJudgmentCoin : VisualElement
    {
        public const float RESULT_HOLD_SECONDS = .8f;
        private const int FRAMES_PER_SECOND = 30;
        private int _frame = -1;
        private bool _approved;

        public DrawJudgmentCoin()
        {
            AddToClassList("judgment-coin-visual");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        public void UpdateFlip(float remainingSeconds, bool approved)
        {
            if (float.IsNaN(remainingSeconds) || float.IsInfinity(remainingSeconds)) remainingSeconds = 0;
            float duration = GameRules.JUDGMENT_COIN_TOSS_SECONDS;
            float elapsed = duration - Mathf.Clamp(remainingSeconds, 0, duration);
            int lastFrame = Mathf.RoundToInt((duration - RESULT_HOLD_SECONDS) * FRAMES_PER_SECOND);
            int frame = remainingSeconds <= RESULT_HOLD_SECONDS ? lastFrame : Mathf.FloorToInt(elapsed * FRAMES_PER_SECOND);
            if (_frame == frame && _approved == approved) return;
            _frame = frame;
            _approved = approved;
            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext context)
        {
            if (_frame < 0) return;
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
            var painter = context.painter2D;
            var origin = contentRect.center;
            float spinDuration = GameRules.JUDGMENT_COIN_TOSS_SECONDS - RESULT_HOLD_SECONDS;
            float progress = Mathf.Clamp01(_frame / (FRAMES_PER_SECOND * spinDuration));
            bool settled = progress >= 1;
            float eased = 1 - Mathf.Pow(1 - progress, 3);
            float rotation = settled ? (_approved ? 1 : -1) : Mathf.Cos(eased * Mathf.PI * (_approved ? 6 : 7));
            float projection = Mathf.Max(.06f, Mathf.Abs(rotation));
            float lift = settled ? 0 : Mathf.Sin(progress * Mathf.PI) * .24f;
            float bounce = settled ? 0 : Mathf.Abs(Mathf.Sin(progress * Mathf.PI * 3)) * (1 - progress) * .045f;
            var center = origin + new Vector2(0, (.10f - lift - bounce) * size);
            float radius = size * .285f;
            float width = radius * projection;
            float thickness = size * .028f * (1 - projection * .5f);
            var ink = new Color32(117, 76, 38, 255);
            var gold = new Color32(250, 196, 89, 255);
            var light = new Color32(255, 226, 151, 255);
            float lineWidth = Mathf.Max(.75f, size * .010f);

            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            DrawEllipse(painter, origin + new Vector2(0, size * .405f),
                new Vector2(size * (.24f - lift * .32f), size * .028f), new Color32(94, 67, 46, 30), ink, 0);
            DrawEllipse(painter, center + new Vector2(thickness, size * .006f),
                new Vector2(width, radius), new Color32(199, 137, 55, 255), ink, lineWidth);
            DrawEllipse(painter, center, new Vector2(width, radius), gold, ink, lineWidth);
            DrawEllipse(painter, center, new Vector2(width * .82f, radius * .82f),
                light, new Color32(217, 154, 58, 255), Mathf.Max(.6f, size * .007f));

            if (projection <= .22f) return;
            Vector2 Point(float x, float y) => center + new Vector2(x * width, y * radius);
            painter.strokeColor = new Color32(230, 168, 64, 255);
            painter.lineWidth = Mathf.Max(.6f, size * .006f);
            painter.BeginPath();
            for (int mark = 0; mark < 12; mark++)
            {
                float angle = mark * Mathf.PI / 6;
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                painter.MoveTo(Point(direction.x * .87f, direction.y * .87f));
                painter.LineTo(Point(direction.x * .94f, direction.y * .94f));
            }
            painter.Stroke();

            painter.fillColor = new Color32(255, 244, 200, 255);
            painter.BeginPath();
            painter.MoveTo(Point(-.65f, -.36f));
            painter.BezierCurveTo(Point(-.52f, -.62f), Point(-.27f, -.72f), Point(-.09f, -.66f));
            painter.BezierCurveTo(Point(-.32f, -.64f), Point(-.47f, -.49f), Point(-.55f, -.30f));
            painter.BezierCurveTo(Point(-.59f, -.25f), Point(-.68f, -.29f), Point(-.65f, -.36f));
            painter.ClosePath();
            painter.Fill();

            bool approvedFace = settled ? _approved : rotation >= 0;
            painter.strokeColor = approvedFace ? new Color32(51, 111, 83, 255) : new Color32(157, 73, 61, 255);
            painter.lineWidth = Mathf.Max(1.2f, size * .035f);
            painter.BeginPath();
            if (approvedFace)
            {
                painter.MoveTo(Point(-.39f, .02f));
                painter.LineTo(Point(-.10f, .31f));
                painter.LineTo(Point(.41f, -.30f));
            }
            else
            {
                painter.MoveTo(Point(-.30f, -.30f)); painter.LineTo(Point(.30f, .30f));
                painter.MoveTo(Point(.30f, -.30f)); painter.LineTo(Point(-.30f, .30f));
            }
            painter.Stroke();
        }

        private static void DrawEllipse(Painter2D painter, Vector2 center, Vector2 radius, Color fill, Color stroke, float lineWidth)
        {
            const float K = .5522848f;
            Vector2 Point(float x, float y) => center + new Vector2(x * radius.x, y * radius.y);
            painter.fillColor = fill;
            painter.BeginPath();
            painter.MoveTo(Point(1, 0));
            painter.BezierCurveTo(Point(1, K), Point(K, 1), Point(0, 1));
            painter.BezierCurveTo(Point(-K, 1), Point(-1, K), Point(-1, 0));
            painter.BezierCurveTo(Point(-1, -K), Point(-K, -1), Point(0, -1));
            painter.BezierCurveTo(Point(K, -1), Point(1, -K), Point(1, 0));
            painter.ClosePath();
            painter.Fill();
            if (lineWidth <= 0) return;
            painter.strokeColor = stroke;
            painter.lineWidth = lineWidth;
            painter.Stroke();
        }
    }
}
