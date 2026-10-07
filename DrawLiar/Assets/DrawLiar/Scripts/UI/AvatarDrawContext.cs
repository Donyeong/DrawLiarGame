using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    internal readonly struct AvatarDrawContext
    {
        public Painter2D Painter { get; }
        public float Size { get; }
        public Color Body { get; }
        public Color Ink => new Color32(64,53,83,255);
        public Color Cream => new Color32(255,249,236,255);
        private readonly Vector2 _offset;
        private readonly float _headCenter, _headWidth, _headSeat;

        public AvatarDrawContext(Painter2D painter,float size,Vector2 offset,Color body,float headCenter,float headWidth,float headSeat)
        {
            Painter=painter; Size=size; _offset=offset; Body=body;
            _headCenter=headCenter; _headWidth=headWidth; _headSeat=headSeat;
        }

        public Vector2 V(float x,float y)=>_offset+new Vector2(x,y)*Size;
        public Vector2 H(float x,float y)=>V(_headCenter+(x-.53f)*_headWidth,_headSeat+(y-.24f)*.94f);

        public void ResetStroke()
        {
            Painter.strokeColor=Ink; Painter.lineWidth=Mathf.Max(.65f,Size*.0135f);
            Painter.lineCap=LineCap.Round; Painter.lineJoin=LineJoin.Round;
        }

        public void Ellipse(float x,float y,float rx,float ry,Color fill,bool outline=false)
        {
            const float k=.5522848f;
            var p=Painter;
            p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x+rx,y));
            p.BezierCurveTo(V(x+rx,y+ry*k),V(x+rx*k,y+ry),V(x,y+ry));
            p.BezierCurveTo(V(x-rx*k,y+ry),V(x-rx,y+ry*k),V(x-rx,y));
            p.BezierCurveTo(V(x-rx,y-ry*k),V(x-rx*k,y-ry),V(x,y-ry));
            p.BezierCurveTo(V(x+rx*k,y-ry),V(x+rx,y-ry*k),V(x+rx,y));
            p.ClosePath(); p.Fill(); if(outline)p.Stroke();
        }

        public void HeadEllipse(float x,float y,float rx,float ry,Color fill,bool outline=false)
        {
            Ellipse(_headCenter+(x-.53f)*_headWidth,_headSeat+(y-.24f)*.94f,rx*_headWidth,ry*.94f,fill,outline);
        }

        public void Box(float x,float y,float width,float height,float radius,Color fill,bool outline=true)
        {
            var p=Painter;
            if(radius<=0)
            {
                p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x,y)); p.LineTo(V(x+width,y));
                p.LineTo(V(x+width,y+height)); p.LineTo(V(x,y+height)); p.ClosePath(); p.Fill();
                if(outline)p.Stroke(); return;
            }
            p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x+radius,y));
            p.LineTo(V(x+width-radius,y));
            p.BezierCurveTo(V(x+width-radius/3,y),V(x+width,y+radius/3),V(x+width,y+radius));
            p.LineTo(V(x+width,y+height-radius));
            p.BezierCurveTo(V(x+width,y+height-radius/3),V(x+width-radius/3,y+height),V(x+width-radius,y+height));
            p.LineTo(V(x+radius,y+height));
            p.BezierCurveTo(V(x+radius/3,y+height),V(x,y+height-radius/3),V(x,y+height-radius));
            p.LineTo(V(x,y+radius));
            p.BezierCurveTo(V(x,y+radius/3),V(x+radius/3,y),V(x+radius,y));
            p.ClosePath(); p.Fill(); if(outline)p.Stroke();
        }

        public void Star(float x,float y,float radius,Color fill,bool outline=false)
        {
            var p=Painter; p.fillColor=fill; p.BeginPath();
            for(int point=0;point<10;point++)
            {
                float angle=-Mathf.PI/2+point*Mathf.PI/5,distance=point%2==0?radius:radius*.45f;
                var position=V(x+Mathf.Cos(angle)*distance,y+Mathf.Sin(angle)*distance);
                if(point==0)p.MoveTo(position);else p.LineTo(position);
            }
            p.ClosePath(); p.Fill(); if(outline)p.Stroke();
        }
    }
}
