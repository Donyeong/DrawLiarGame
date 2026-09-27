using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class AvatarElement : VisualElement
    {
        public static readonly Color[] Colors = { new Color32(171,156,237,255), new Color32(248,177,152,255), new Color32(145,203,182,255), new Color32(245,210,118,255), new Color32(153,190,229,255), new Color32(231,163,193,255) };
        private readonly int colorIndex;
        private readonly int accessory;

        public AvatarElement(int color = 0, int decoration = 0)
        {
            colorIndex = Mathf.Abs(color) % Colors.Length;
            accessory = decoration;
            AddToClassList("avatar");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext context)
        {
            var p = context.painter2D;
            var size = Mathf.Min(contentRect.width, contentRect.height);
            var offset = new Vector2((contentRect.width-size)/2, (contentRect.height-size)/2);
            Vector2 V(float x, float y) => offset + new Vector2(x,y)*size;
            void Ellipse(float x, float y, float rx, float ry, Color color, bool outline = false)
            {
                const float k = .5522848f;
                p.fillColor = color; p.BeginPath(); p.MoveTo(V(x+rx,y));
                p.BezierCurveTo(V(x+rx,y+ry*k),V(x+rx*k,y+ry),V(x,y+ry));
                p.BezierCurveTo(V(x-rx*k,y+ry),V(x-rx,y+ry*k),V(x-rx,y));
                p.BezierCurveTo(V(x-rx,y-ry*k),V(x-rx*k,y-ry),V(x,y-ry));
                p.BezierCurveTo(V(x+rx*k,y-ry),V(x+rx,y-ry*k),V(x+rx,y));
                p.ClosePath(); p.Fill(); if(outline) p.Stroke();
            }
            var ink = new Color32(64,53,83,255);
            var body = Colors[colorIndex];
            p.strokeColor=ink; p.lineWidth=Mathf.Max(.9f,size*.011f); p.lineCap=LineCap.Round; p.lineJoin=LineJoin.Round;
            Ellipse(.5f,.92f,.26f,.028f,new Color32(64,53,83,20));
            Ellipse(.36f,.855f,.075f,.045f,body,true); Ellipse(.64f,.855f,.075f,.045f,body,true);
            p.fillColor=body; p.BeginPath(); p.MoveTo(V(.5f,.16f));
            p.BezierCurveTo(V(.71f,.16f),V(.8f,.33f),V(.8f,.55f));
            p.BezierCurveTo(V(.8f,.76f),V(.72f,.85f),V(.5f,.85f));
            p.BezierCurveTo(V(.28f,.85f),V(.2f,.76f),V(.2f,.55f));
            p.BezierCurveTo(V(.2f,.33f),V(.29f,.16f),V(.5f,.16f)); p.ClosePath(); p.Fill(); p.Stroke();
            Ellipse(.5f,.58f,.23f,.2f,new Color32(255,249,236,255));
            Ellipse(.4f,.53f,.018f,.027f,ink); Ellipse(.6f,.53f,.018f,.027f,ink);
            Ellipse(.34f,.61f,.035f,.018f,new Color32(239,158,159,150)); Ellipse(.66f,.61f,.035f,.018f,new Color32(239,158,159,150));
            p.BeginPath(); p.MoveTo(V(.46f,.63f)); p.QuadraticCurveTo(V(.5f,.67f),V(.54f,.63f)); p.Stroke();
            if(accessory==1)
            {
                p.fillColor=new Color32(254,214,101,255); p.BeginPath(); p.MoveTo(V(.36f,.205f));
                p.LineTo(V(.33f,.085f)); p.LineTo(V(.42f,.13f)); p.LineTo(V(.5f,.045f));
                p.LineTo(V(.58f,.13f)); p.LineTo(V(.67f,.085f)); p.LineTo(V(.64f,.205f)); p.ClosePath(); p.Fill(); p.Stroke();
            }
            else if(accessory==2)
            {
                p.strokeColor=new Color32(75,133,93,255); p.BeginPath(); p.MoveTo(V(.5f,.16f)); p.LineTo(V(.5f,.075f)); p.Stroke();
                Ellipse(.435f,.075f,.065f,.03f,new Color32(145,203,135,255),true);
                Ellipse(.565f,.075f,.065f,.03f,new Color32(114,184,143,255),true);
            }
            else if(accessory==3)
            {
                p.BeginPath(); p.Arc(V(.4f,.53f),size*.07f,0,360); p.Stroke();
                p.BeginPath(); p.Arc(V(.6f,.53f),size*.07f,0,360); p.Stroke();
                p.BeginPath(); p.MoveTo(V(.47f,.53f)); p.LineTo(V(.53f,.53f)); p.Stroke();
            }
        }
    }
}
