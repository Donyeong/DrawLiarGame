using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawLevelBadge : VisualElement
    {
        private static readonly Color[] FILLS = { new Color(.92f, .90f, .84f), new Color(.76f, .89f, .79f), new Color(.73f, .85f, .96f), new Color(.86f, .78f, .96f), new Color(1f, .82f, .66f), new Color(1f, .91f, .56f) };
        private static readonly Color[] INKS = { new Color(.39f, .36f, .30f), new Color(.20f, .43f, .31f), new Color(.23f, .39f, .59f), new Color(.43f, .30f, .60f), new Color(.61f, .35f, .21f), new Color(.56f, .40f, .13f) };
        private readonly Label _number;
        public int Level { get; private set; }
        public int Tier => AccountLevelRules.GetBadgeTier(Level);
        public Color FillColor => FILLS[Tier];

        public DrawLevelBadge(int level = 1)
        {
            AddToClassList("account-level-badge");
            pickingMode = PickingMode.Ignore;
            _number = new Label { name = "level-number", pickingMode = PickingMode.Ignore, enableRichText = false, languageDirection = LanguageDirection.LTR };
            _number.AddToClassList("account-level-number");
            Add(_number);
            generateVisualContent += Paint;
            SetLevel(level);
        }

        public void SetLevel(int level)
        {
            level = Mathf.Clamp(level, 1, AccountLevelRules.MAX_LEVEL);
            if (level == Level) return;
            Level = level;
            _number.text = level.ToString(CultureInfo.InvariantCulture);
            _number.style.color = INKS[Tier];
            for (int tier = 0; tier < FILLS.Length; tier++) EnableInClassList("level-tier-" + tier, tier == Tier);
            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
            var painter = context.painter2D;
            Vector2 Point(float x, float y) => contentRect.center + new Vector2((x - .5f) * size, (y - .5f) * size);
            painter.fillColor = FILLS[Tier];
            painter.strokeColor = INKS[Tier];
            painter.lineWidth = Mathf.Max(1f, size / 20f);
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            if (Tier == 0)
            {
                painter.MoveTo(Point(.5f, .07f));
                painter.BezierCurveTo(Point(.74f, .07f), Point(.93f, .26f), Point(.93f, .5f));
                painter.BezierCurveTo(Point(.93f, .74f), Point(.74f, .93f), Point(.5f, .93f));
                painter.BezierCurveTo(Point(.26f, .93f), Point(.07f, .74f), Point(.07f, .5f));
                painter.BezierCurveTo(Point(.07f, .26f), Point(.26f, .07f), Point(.5f, .07f));
            }
            else if (Tier == 1)
            {
                painter.MoveTo(Point(.12f, .13f)); painter.LineTo(Point(.88f, .13f)); painter.LineTo(Point(.85f, .62f));
                painter.BezierCurveTo(Point(.82f, .80f), Point(.61f, .87f), Point(.5f, .95f));
                painter.BezierCurveTo(Point(.39f, .87f), Point(.18f, .80f), Point(.15f, .62f));
            }
            else if (Tier == 2)
            {
                painter.MoveTo(Point(.5f, .04f)); painter.LineTo(Point(.96f, .5f)); painter.LineTo(Point(.5f, .96f)); painter.LineTo(Point(.04f, .5f));
            }
            else if (Tier == 3)
            {
                painter.MoveTo(Point(.27f, .07f)); painter.LineTo(Point(.73f, .07f)); painter.LineTo(Point(.96f, .5f));
                painter.LineTo(Point(.73f, .93f)); painter.LineTo(Point(.27f, .93f)); painter.LineTo(Point(.04f, .5f));
            }
            else if (Tier == 4)
            {
                for (int index = 0; index < 10; index++)
                {
                    float angle = -Mathf.PI / 2 + index * Mathf.PI / 5;
                    float radius = index % 2 == 0 ? .47f : .35f;
                    var point = Point(.5f + Mathf.Cos(angle) * radius, .5f + Mathf.Sin(angle) * radius);
                    if (index == 0) painter.MoveTo(point); else painter.LineTo(point);
                }
            }
            else
            {
                painter.MoveTo(Point(.08f, .20f)); painter.LineTo(Point(.29f, .32f)); painter.LineTo(Point(.5f, .05f));
                painter.LineTo(Point(.71f, .32f)); painter.LineTo(Point(.92f, .20f)); painter.LineTo(Point(.86f, .85f));
                painter.LineTo(Point(.14f, .85f));
            }
            painter.ClosePath(); painter.Fill(); painter.Stroke();
        }
    }
}
