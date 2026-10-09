using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawUIIcon : VisualElement
    {
        public enum Kind { Refresh, Settings, Close, Bell, Profile, Ballot }
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

            if (_kind == Kind.Bell)
            {
                painter.BeginPath();
                painter.MoveTo(Point(.23f, .67f));
                painter.BezierCurveTo(Point(.29f, .60f), Point(.29f, .53f), Point(.29f, .40f));
                painter.BezierCurveTo(Point(.29f, .27f), Point(.37f, .19f), Point(.50f, .19f));
                painter.BezierCurveTo(Point(.63f, .19f), Point(.71f, .27f), Point(.71f, .40f));
                painter.BezierCurveTo(Point(.71f, .53f), Point(.71f, .60f), Point(.77f, .67f));
                painter.LineTo(Point(.23f, .67f));
                painter.ClosePath();
                painter.Stroke();
                painter.BeginPath();
                painter.MoveTo(Point(.40f, .79f));
                painter.BezierCurveTo(Point(.42f, .89f), Point(.58f, .89f), Point(.60f, .79f));
                painter.Stroke();
                painter.BeginPath();
                painter.MoveTo(Point(.50f, .10f));
                painter.LineTo(Point(.50f, .19f));
                painter.Stroke();
                return;
            }

            if(_kind==Kind.Profile)
            {
                painter.BeginPath();
                for(int index=0;index<16;index++)
                {
                    float angle=index*Mathf.PI/8;var point=Point(.5f+Mathf.Cos(angle)*.15f,.3f+Mathf.Sin(angle)*.15f);
                    if(index==0)painter.MoveTo(point);else painter.LineTo(point);
                }
                painter.ClosePath();painter.Stroke();
                painter.BeginPath();painter.MoveTo(Point(.2f,.82f));
                painter.BezierCurveTo(Point(.2f,.48f),Point(.8f,.48f),Point(.8f,.82f));
                painter.LineTo(Point(.2f,.82f));painter.Stroke();return;
            }

            if (_kind == Kind.Ballot)
            {
                painter.fillColor = new Color32(249, 230, 190, 255);
                painter.BeginPath();
                painter.MoveTo(Point(.90f, .50f));
                painter.BezierCurveTo(Point(.90f, .721f), Point(.721f, .90f), Point(.50f, .90f));
                painter.BezierCurveTo(Point(.279f, .90f), Point(.10f, .721f), Point(.10f, .50f));
                painter.BezierCurveTo(Point(.10f, .279f), Point(.279f, .10f), Point(.50f, .10f));
                painter.BezierCurveTo(Point(.721f, .10f), Point(.90f, .279f), Point(.90f, .50f));
                painter.ClosePath();
                painter.Fill();
                painter.Stroke();
                painter.BeginPath();
                painter.MoveTo(Point(.29f, .35f));
                painter.LineTo(Point(.50f, .68f));
                painter.LineTo(Point(.71f, .35f));
                painter.Stroke();
                return;
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
