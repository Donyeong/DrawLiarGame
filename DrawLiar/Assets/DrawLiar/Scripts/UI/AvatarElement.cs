using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class AvatarElement : VisualElement
    {
        public static readonly Color[] Colors = { new Color32(171,156,237,255), new Color32(248,177,152,255), new Color32(145,203,182,255), new Color32(245,210,118,255), new Color32(153,190,229,255), new Color32(231,163,193,255) };
        private readonly int colorIndex;
        public AvatarAccessory Equipment { get; }

        public AvatarElement(int color = 0, int decoration = (int)AvatarAccessory.Painter)
        {
            colorIndex = Mathf.Clamp(color, 0, Colors.Length - 1);
            Equipment = (AvatarAccessory)Mathf.Clamp(decoration, 0, (int)AvatarAccessory.Painter);
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
            p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f); p.lineCap=LineCap.Round; p.lineJoin=LineJoin.Round;
            Ellipse(.53f,.918f,.27f,.022f,new Color32(64,53,83,20));
            Ellipse(.395f,.872f,.079f,.039f,ink); Ellipse(.665f,.872f,.079f,.039f,ink);
            p.fillColor=body; p.BeginPath(); p.MoveTo(V(.53f,.19f));
            p.BezierCurveTo(V(.72f,.19f),V(.845f,.34f),V(.845f,.535f));
            p.BezierCurveTo(V(.845f,.635f),V(.823f,.69f),V(.815f,.73f));
            p.BezierCurveTo(V(.84f,.805f),V(.745f,.865f),V(.53f,.865f));
            p.BezierCurveTo(V(.315f,.865f),V(.22f,.805f),V(.245f,.73f));
            p.BezierCurveTo(V(.237f,.69f),V(.215f,.635f),V(.215f,.535f));
            p.BezierCurveTo(V(.215f,.34f),V(.34f,.19f),V(.53f,.19f)); p.ClosePath(); p.Fill(); p.Stroke();
            Ellipse(.53f,.625f,.235f,.2f,new Color32(255,249,236,255));
            Ellipse(.43f,.48f,.021f,.03f,ink); Ellipse(.63f,.48f,.021f,.03f,ink);
            Ellipse(.35f,.545f,.044f,.02f,new Color32(247,169,184,255)); Ellipse(.71f,.545f,.044f,.02f,new Color32(247,169,184,255));
            p.BeginPath(); p.MoveTo(V(.49f,.555f)); p.QuadraticCurveTo(V(.53f,.605f),V(.57f,.555f)); p.Stroke();
            if((Equipment & AvatarAccessory.Beret)!=0)
            {
                p.fillColor=new Color32(112,150,156,255); p.BeginPath(); p.MoveTo(V(.27f,.23f));
                p.BezierCurveTo(V(.20f,.205f),V(.29f,.125f),V(.40f,.10f));
                p.BezierCurveTo(V(.39f,.085f),V(.365f,.053f),V(.39f,.047f));
                p.BezierCurveTo(V(.42f,.039f),V(.44f,.054f),V(.44f,.087f));
                p.BezierCurveTo(V(.55f,.039f),V(.71f,.06f),V(.76f,.11f));
                p.BezierCurveTo(V(.81f,.19f),V(.65f,.22f),V(.49f,.25f));
                p.BezierCurveTo(V(.38f,.27f),V(.29f,.265f),V(.27f,.23f)); p.ClosePath(); p.Fill(); p.Stroke();
                var paint=new Color32(251,211,123,255);
                Ellipse(.43f,.177f,.023f,.015f,paint); Ellipse(.453f,.16f,.024f,.019f,paint);
            }
            if((Equipment & AvatarAccessory.Brush)!=0)
            {
                Vector2 B(float x,float y)=>V(.23f+x*.925f+y*.38f,.685f-x*.38f+y*.925f);
                p.fillColor=new Color32(185,132,83,255); p.BeginPath(); p.MoveTo(B(-.018f,-.115f));
                p.LineTo(B(.018f,-.115f)); p.LineTo(B(.014f,.135f)); p.QuadraticCurveTo(B(0,.15f),B(-.014f,.135f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.fillColor=new Color32(212,210,202,255); p.BeginPath(); p.MoveTo(B(-.028f,-.155f));
                p.LineTo(B(.028f,-.155f)); p.LineTo(B(.024f,-.105f)); p.LineTo(B(-.024f,-.105f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.fillColor=new Color32(251,211,123,255); p.BeginPath(); p.MoveTo(B(-.028f,-.155f));
                p.BezierCurveTo(B(-.045f,-.185f),B(-.021f,-.221f),B(-.012f,-.257f));
                p.BezierCurveTo(B(.012f,-.222f),B(.047f,-.192f),B(.028f,-.155f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.23f,.685f,.057f,.061f,body,true);
            }
        }
    }
}
