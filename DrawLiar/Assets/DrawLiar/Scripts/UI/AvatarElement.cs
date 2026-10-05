using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class AvatarElement : VisualElement
    {
        public static readonly Color[] Colors = { new Color32(171,156,237,255), new Color32(248,177,152,255), new Color32(145,203,182,255), new Color32(245,210,118,255), new Color32(153,190,229,255), new Color32(231,163,193,255) };
        private readonly int _colorIndex;
        public AvatarAccessory Equipment { get; }

        public AvatarElement(int color = 0, int decoration = (int)AvatarAccessory.Painter)
        {
            _colorIndex = Mathf.Clamp(color, 0, Colors.Length - 1);
            Equipment = (AvatarAccessory)AvatarParts.Sanitize(decoration);
            AddToClassList("avatar");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext context)
        {
            var p = context.painter2D;
            var size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0) return;
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
            void RoundedBox(float x, float y, float width, float height, float radius, Color fill)
            {
                p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x+radius,y));
                p.LineTo(V(x+width-radius,y)); p.BezierCurveTo(V(x+width-radius/3,y),V(x+width,y+radius/3),V(x+width,y+radius));
                p.LineTo(V(x+width,y+height-radius)); p.QuadraticCurveTo(V(x+width,y+height),V(x+width-radius,y+height));
                p.LineTo(V(x+radius,y+height)); p.QuadraticCurveTo(V(x,y+height),V(x,y+height-radius));
                p.LineTo(V(x,y+radius)); p.QuadraticCurveTo(V(x,y),V(x+radius,y));
                p.ClosePath(); p.Fill(); p.Stroke();
            }
            void Star(float x, float y, float radius, Color fill, bool outline = false)
            {
                p.fillColor=fill; p.BeginPath();
                for(int point=0;point<10;point++)
                {
                    float angle=-Mathf.PI/2+point*Mathf.PI/5;
                    float distance=point%2==0?radius:radius*.45f;
                    var position=V(x+Mathf.Cos(angle)*distance,y+Mathf.Sin(angle)*distance);
                    if(point==0)p.MoveTo(position);else p.LineTo(position);
                }
                p.ClosePath(); p.Fill(); if(outline)p.Stroke();
            }
            void Clothing(Color fill)
            {
                p.fillColor=fill; p.BeginPath(); p.MoveTo(V(.272f,.675f));
                p.BezierCurveTo(V(.31f,.663f),V(.365f,.651f),V(.40f,.66f));
                p.QuadraticCurveTo(V(.53f,.708f),V(.66f,.66f));
                p.QuadraticCurveTo(V(.739f,.655f),V(.788f,.675f));
                p.BezierCurveTo(V(.798f,.699f),V(.806f,.72f),V(.815f,.73f));
                p.BezierCurveTo(V(.84f,.805f),V(.745f,.865f),V(.53f,.865f));
                p.BezierCurveTo(V(.315f,.865f),V(.22f,.805f),V(.245f,.73f));
                p.QuadraticCurveTo(V(.255f,.70f),V(.272f,.675f));
                p.ClosePath(); p.Fill(); p.Stroke();
            }
            void Stripe(float x, float y, float width, float height, Color fill)
            {
                p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x,y));
                p.LineTo(V(x+width,y)); p.LineTo(V(x+width,y+height)); p.LineTo(V(x,y+height));
                p.ClosePath(); p.Fill();
            }
            var ink = new Color32(64,53,83,255);
            var cream = new Color32(255,249,236,255);
            var gold = new Color32(251,211,123,255);
            var mint = new Color32(132,195,174,255);
            var lavender = new Color32(157,147,219,255);
            var body = Colors[_colorIndex];
            p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f); p.lineCap=LineCap.Round; p.lineJoin=LineJoin.Round;
            Ellipse(.53f,.918f,.27f,.022f,new Color32(64,53,83,20));
            if((Equipment & AvatarAccessory.Cape)!=0)
            {
                p.fillColor=lavender; p.BeginPath(); p.MoveTo(V(.36f,.34f));
                p.BezierCurveTo(V(.4733333f,.30f),V(.5833333f,.30f),V(.69f,.34f));
                p.BezierCurveTo(V(.78f,.43f),V(.86f,.65f),V(.93f,.83f));
                p.QuadraticCurveTo(V(.95f,.90f),V(.84f,.90f));
                p.QuadraticCurveTo(V(.80f,.85f),V(.75f,.85f));
                p.QuadraticCurveTo(V(.69f,.92f),V(.62f,.89f));
                p.QuadraticCurveTo(V(.53f,.85f),V(.44f,.89f));
                p.QuadraticCurveTo(V(.36f,.92f),V(.30f,.85f));
                p.QuadraticCurveTo(V(.25f,.85f),V(.21f,.90f));
                p.QuadraticCurveTo(V(.10f,.90f),V(.12f,.83f));
                p.BezierCurveTo(V(.19f,.65f),V(.27f,.43f),V(.36f,.34f));
                p.ClosePath(); p.Fill(); p.Stroke();
                Star(.18f,.76f,.035f,gold); Star(.87f,.76f,.035f,gold);
            }
            Ellipse(.395f,.872f,.079f,.039f,ink); Ellipse(.665f,.872f,.079f,.039f,ink);
            p.fillColor=body; p.BeginPath(); p.MoveTo(V(.53f,.19f));
            p.BezierCurveTo(V(.72f,.19f),V(.845f,.34f),V(.845f,.535f));
            p.BezierCurveTo(V(.845f,.635f),V(.823f,.69f),V(.815f,.73f));
            p.BezierCurveTo(V(.84f,.805f),V(.745f,.865f),V(.53f,.865f));
            p.BezierCurveTo(V(.315f,.865f),V(.22f,.805f),V(.245f,.73f));
            p.BezierCurveTo(V(.237f,.69f),V(.215f,.635f),V(.215f,.535f));
            p.BezierCurveTo(V(.215f,.34f),V(.34f,.19f),V(.53f,.19f)); p.ClosePath(); p.Fill(); p.Stroke();
            var clothing=Equipment&(AvatarAccessory.PainterApron|AvatarAccessory.StripedShirt|AvatarAccessory.PolkaDotShirt|AvatarAccessory.Overalls|AvatarAccessory.StarSweater);
            if(clothing==AvatarAccessory.PainterApron)
            {
                Clothing(new Color32(241,216,175,255));
                p.fillColor=new Color32(110,173,164,255); p.BeginPath(); p.MoveTo(V(.397f,.638f));
                p.BezierCurveTo(V(.437f,.65f),V(.475f,.655f),V(.53f,.655f));
                p.BezierCurveTo(V(.585f,.655f),V(.623f,.65f),V(.663f,.638f));
                p.LineTo(V(.717f,.808f)); p.QuadraticCurveTo(V(.735f,.812f),V(.74f,.816f));
                p.BezierCurveTo(V(.685f,.846f),V(.625f,.852f),V(.53f,.852f));
                p.BezierCurveTo(V(.435f,.852f),V(.375f,.846f),V(.32f,.816f));
                p.QuadraticCurveTo(V(.325f,.812f),V(.343f,.808f)); p.LineTo(V(.397f,.638f));
                p.ClosePath(); p.Fill(); p.Stroke();
                RoundedBox(.425f,.741f,.21f,.092f,.016f,cream);
                Ellipse(.480f,.794f,.019f,.018f,new Color32(239,155,142,255));
                Ellipse(.558f,.770f,.023f,.018f,gold);
            }
            else if(clothing==AvatarAccessory.StripedShirt)
            {
                Clothing(new Color32(105,148,179,255));
                Stripe(.284f,.704f,.492f,.026f,cream);
                Stripe(.274f,.758f,.512f,.026f,cream);
                Stripe(.352f,.810f,.356f,.022f,cream);
            }
            else if(clothing==AvatarAccessory.PolkaDotShirt)
            {
                Clothing(new Color32(226,140,158,255));
                Ellipse(.352f,.718f,.024f,.023f,cream); Ellipse(.530f,.718f,.024f,.023f,cream); Ellipse(.708f,.718f,.024f,.023f,cream);
                Ellipse(.432f,.765f,.024f,.023f,cream); Ellipse(.628f,.765f,.024f,.023f,cream);
                Ellipse(.365f,.809f,.023f,.022f,cream); Ellipse(.530f,.811f,.024f,.023f,cream); Ellipse(.695f,.809f,.023f,.022f,cream);
            }
            else if(clothing==AvatarAccessory.Overalls)
            {
                var denim=new Color32(107,139,191,255);
                Clothing(new Color32(241,201,125,255));
                p.fillColor=denim; p.BeginPath(); p.MoveTo(V(.37f,.686f));
                p.BezierCurveTo(V(.42f,.697f),V(.48f,.70f),V(.53f,.70f));
                p.BezierCurveTo(V(.58f,.70f),V(.64f,.697f),V(.69f,.686f));
                p.LineTo(V(.69f,.746f)); p.LineTo(V(.776f,.792f));
                p.BezierCurveTo(V(.737f,.838f),V(.645f,.862f),V(.53f,.862f));
                p.BezierCurveTo(V(.415f,.862f),V(.323f,.838f),V(.284f,.792f));
                p.LineTo(V(.37f,.746f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.strokeColor=denim; p.lineWidth=size*.035f;
                p.BeginPath(); p.MoveTo(V(.354f,.653f)); p.LineTo(V(.415f,.755f));
                p.MoveTo(V(.706f,.653f)); p.LineTo(V(.645f,.755f)); p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
                Ellipse(.412f,.744f,.019f,.019f,gold,true); Ellipse(.648f,.744f,.019f,.019f,gold,true);
                p.BeginPath(); p.MoveTo(V(.46f,.775f)); p.LineTo(V(.46f,.809f));
                p.QuadraticCurveTo(V(.53f,.833f),V(.60f,.809f)); p.LineTo(V(.60f,.775f));
                p.MoveTo(V(.53f,.833f)); p.LineTo(V(.53f,.858f)); p.Stroke();
            }
            else if(clothing==AvatarAccessory.StarSweater)
            {
                Clothing(new Color32(137,121,183,255));
                p.strokeColor=new Color32(191,181,224,255); p.lineWidth=size*.023f;
                p.BeginPath(); p.MoveTo(V(.36f,.828f)); p.QuadraticCurveTo(V(.53f,.854f),V(.70f,.828f)); p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
                Star(.53f,.756f,.077f,gold,true);
            }
            if(clothing==AvatarAccessory.None)Ellipse(.53f,.625f,.235f,.2f,cream);
            else Ellipse(.53f,.555f,.235f,.13f,cream);
            Ellipse(.35f,.545f,.044f,.02f,new Color32(247,169,184,255)); Ellipse(.71f,.545f,.044f,.02f,new Color32(247,169,184,255));
            if((Equipment&AvatarAccessory.Wink)!=0)
            {
                Ellipse(.43f,.48f,.021f,.03f,ink);
                p.BeginPath(); p.MoveTo(V(.604f,.486f)); p.QuadraticCurveTo(V(.63f,.462f),V(.656f,.486f));
                p.MoveTo(V(.492f,.555f)); p.QuadraticCurveTo(V(.537f,.605f),V(.582f,.547f)); p.Stroke();
            }
            else if((Equipment&AvatarAccessory.Happy)!=0)
            {
                p.BeginPath(); p.MoveTo(V(.402f,.486f)); p.QuadraticCurveTo(V(.43f,.452f),V(.458f,.486f));
                p.MoveTo(V(.602f,.486f)); p.QuadraticCurveTo(V(.63f,.452f),V(.658f,.486f)); p.Stroke();
                p.fillColor=ink; p.BeginPath(); p.MoveTo(V(.486f,.553f));
                p.BezierCurveTo(V(.513f,.56f),V(.547f,.56f),V(.574f,.553f));
                p.BezierCurveTo(V(.572f,.618f),V(.488f,.618f),V(.486f,.553f)); p.ClosePath(); p.Fill();
                Ellipse(.53f,.592f,.024f,.011f,new Color32(247,169,184,255));
            }
            else if((Equipment&AvatarAccessory.Sleepy)!=0)
            {
                p.BeginPath(); p.MoveTo(V(.402f,.480f)); p.QuadraticCurveTo(V(.43f,.496f),V(.458f,.483f));
                p.MoveTo(V(.602f,.483f)); p.QuadraticCurveTo(V(.63f,.496f),V(.658f,.480f));
                p.MoveTo(V(.510f,.576f)); p.QuadraticCurveTo(V(.53f,.582f),V(.55f,.576f)); p.Stroke();
            }
            else if((Equipment&AvatarAccessory.Surprised)!=0)
            {
                Ellipse(.43f,.48f,.027f,.039f,ink); Ellipse(.63f,.48f,.027f,.039f,ink);
                Ellipse(.53f,.576f,.030f,.040f,ink);
                Ellipse(.53f,.576f,.015f,.023f,cream);
            }
            else if((Equipment&AvatarAccessory.Determined)!=0)
            {
                Ellipse(.43f,.483f,.019f,.026f,ink); Ellipse(.63f,.483f,.019f,.026f,ink);
                p.BeginPath(); p.MoveTo(V(.397f,.431f)); p.LineTo(V(.461f,.458f));
                p.MoveTo(V(.599f,.458f)); p.LineTo(V(.663f,.431f));
                p.MoveTo(V(.494f,.582f)); p.QuadraticCurveTo(V(.53f,.564f),V(.566f,.582f)); p.Stroke();
            }
            else
            {
                Ellipse(.43f,.48f,.021f,.03f,ink); Ellipse(.63f,.48f,.021f,.03f,ink);
                p.BeginPath(); p.MoveTo(V(.49f,.555f)); p.QuadraticCurveTo(V(.53f,.605f),V(.57f,.555f)); p.Stroke();
            }
            if((Equipment & AvatarAccessory.Beret)!=0)
            {
                p.fillColor=new Color32(112,150,156,255); p.BeginPath(); p.MoveTo(V(.27f,.23f));
                p.BezierCurveTo(V(.20f,.205f),V(.29f,.125f),V(.40f,.10f));
                p.BezierCurveTo(V(.39f,.085f),V(.365f,.053f),V(.39f,.047f));
                p.BezierCurveTo(V(.42f,.039f),V(.44f,.054f),V(.44f,.087f));
                p.BezierCurveTo(V(.55f,.039f),V(.71f,.06f),V(.76f,.11f));
                p.BezierCurveTo(V(.81f,.19f),V(.65f,.22f),V(.49f,.25f));
                p.BezierCurveTo(V(.38f,.27f),V(.29f,.265f),V(.27f,.23f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.43f,.177f,.023f,.015f,gold); Ellipse(.453f,.16f,.024f,.019f,gold);
            }
            if((Equipment & AvatarAccessory.Crown)!=0)
            {
                p.fillColor=gold; p.BeginPath(); p.MoveTo(V(.30f,.24f));
                p.LineTo(V(.27f,.105f)); p.LineTo(V(.39f,.165f));
                p.LineTo(V(.43f,.035f)); p.LineTo(V(.535f,.135f));
                p.LineTo(V(.64f,.035f)); p.LineTo(V(.68f,.165f));
                p.LineTo(V(.80f,.105f)); p.LineTo(V(.77f,.24f));
                p.QuadraticCurveTo(V(.53f,.27f),V(.30f,.24f)); p.ClosePath(); p.Fill(); p.Stroke();
                RoundedBox(.315f,.215f,.44f,.045f,.018f,new Color32(243,186,99,255));
                Star(.535f,.174f,.040f,cream,true);
                Ellipse(.36f,.188f,.017f,.020f,mint); Ellipse(.71f,.188f,.017f,.020f,mint);
            }
            if((Equipment & AvatarAccessory.WizardHat)!=0)
            {
                var violet=new Color32(120,103,164,255);
                p.fillColor=violet; p.BeginPath(); p.MoveTo(V(.30f,.24f));
                p.BezierCurveTo(V(.3733333f,.1566667f),V(.4366667f,.095f),V(.49f,.055f));
                p.QuadraticCurveTo(V(.52f,.015f),V(.58f,.024f));
                p.QuadraticCurveTo(V(.59f,.087f),V(.65f,.16f));
                p.LineTo(V(.76f,.24f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.fillColor=gold; p.BeginPath(); p.MoveTo(V(.335f,.203f));
                p.BezierCurveTo(V(.465f,.215f),V(.5866667f,.2146667f),V(.70f,.202f));
                p.LineTo(V(.745f,.24f)); p.QuadraticCurveTo(V(.53f,.261f),V(.30f,.24f));
                p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.53f,.256f,.325f,.045f,lavender,true);
                Star(.53f,.127f,.034f,gold);
            }
            if((Equipment & AvatarAccessory.Headphones)!=0)
            {
                p.BeginPath(); p.MoveTo(V(.235f,.405f));
                p.BezierCurveTo(V(.235f,.12f),V(.825f,.12f),V(.825f,.405f));
                p.lineWidth=size*.069f; p.Stroke();
                p.strokeColor=mint; p.lineWidth=size*.039f; p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
                Ellipse(.235f,.418f,.044f,.096f,mint,true);
                Ellipse(.825f,.418f,.044f,.096f,mint,true);
                Ellipse(.235f,.418f,.018f,.062f,cream);
                Ellipse(.825f,.418f,.018f,.062f,cream);
            }
            if((Equipment & AvatarAccessory.RoundGlasses)!=0)
            {
                var glass=new Color32(168,216,221,62);
                Ellipse(.43f,.48f,.076f,.066f,glass,true);
                Ellipse(.63f,.48f,.076f,.066f,glass,true);
                p.BeginPath(); p.MoveTo(V(.506f,.477f)); p.QuadraticCurveTo(V(.53f,.463f),V(.554f,.477f));
                p.MoveTo(V(.354f,.464f)); p.LineTo(V(.316f,.445f));
                p.MoveTo(V(.706f,.464f)); p.LineTo(V(.744f,.445f)); p.Stroke();
            }
            if((Equipment & AvatarAccessory.Sunglasses)!=0)
            {
                var lens=new Color32(100,87,151,188);
                RoundedBox(.337f,.433f,.168f,.099f,.027f,lens);
                RoundedBox(.555f,.433f,.168f,.099f,.027f,lens);
                p.BeginPath(); p.MoveTo(V(.505f,.459f)); p.LineTo(V(.555f,.459f));
                p.MoveTo(V(.337f,.451f)); p.LineTo(V(.306f,.438f));
                p.MoveTo(V(.723f,.451f)); p.LineTo(V(.754f,.438f)); p.Stroke();
                p.strokeColor=cream; p.lineWidth=size*.010f;
                p.BeginPath(); p.MoveTo(V(.36f,.455f)); p.LineTo(V(.394f,.455f));
                p.MoveTo(V(.58f,.455f)); p.LineTo(V(.614f,.455f)); p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
            }
            if((Equipment & AvatarAccessory.Scarf)!=0)
            {
                var coral=new Color32(239,155,142,255);
                p.fillColor=coral; p.BeginPath(); p.MoveTo(V(.665f,.753f));
                p.LineTo(V(.751f,.768f)); p.LineTo(V(.737f,.893f));
                p.QuadraticCurveTo(V(.697f,.91f),V(.658f,.883f));
                p.LineTo(V(.665f,.753f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.fillColor=coral; p.BeginPath(); p.MoveTo(V(.326f,.729f));
                p.BezierCurveTo(V(.462f,.7616667f),V(.6013333f,.7616667f),V(.744f,.729f));
                p.LineTo(V(.768f,.790f)); p.QuadraticCurveTo(V(.53f,.85f),V(.304f,.790f));
                p.QuadraticCurveTo(V(.299f,.752f),V(.326f,.729f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.706f,.781f,.037f,.029f,gold,true);
                p.BeginPath(); p.MoveTo(V(.675f,.876f)); p.LineTo(V(.674f,.9f));
                p.MoveTo(V(.704f,.882f)); p.LineTo(V(.704f,.906f));
                p.MoveTo(V(.73f,.879f)); p.LineTo(V(.732f,.901f)); p.Stroke();
            }
            if((Equipment & AvatarAccessory.BowTie)!=0)
            {
                p.fillColor=mint; p.BeginPath(); p.MoveTo(V(.505f,.779f));
                p.LineTo(V(.389f,.736f)); p.QuadraticCurveTo(V(.367f,.772f),V(.389f,.824f));
                p.LineTo(V(.505f,.796f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.BeginPath(); p.MoveTo(V(.553f,.779f));
                p.LineTo(V(.669f,.736f)); p.QuadraticCurveTo(V(.691f,.772f),V(.669f,.824f));
                p.LineTo(V(.553f,.796f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.53f,.785f,.030f,.029f,gold,true);
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
            if((Equipment & AvatarAccessory.StarWand)!=0)
            {
                p.fillColor=lavender; p.BeginPath(); p.MoveTo(V(.106f,.486f));
                p.LineTo(V(.133f,.477f)); p.LineTo(V(.285f,.802f));
                p.QuadraticCurveTo(V(.278f,.824f),V(.259f,.813f)); p.ClosePath(); p.Fill(); p.Stroke();
                Star(.105f,.435f,.084f,gold,true);
                Ellipse(.106f,.441f,.018f,.018f,cream);
                Ellipse(.209f,.685f,.051f,.058f,body,true);
            }
            if((Equipment & AvatarAccessory.Palette)!=0)
            {
                p.fillColor=new Color32(231,190,132,255); p.BeginPath(); p.MoveTo(V(.352f,.686f));
                p.BezierCurveTo(V(.352f,.618f),V(.229f,.594f),V(.15f,.63f));
                p.BezierCurveTo(V(.06f,.663f),V(.069f,.787f),V(.158f,.818f));
                p.BezierCurveTo(V(.225f,.845f),V(.297f,.804f),V(.303f,.765f));
                p.QuadraticCurveTo(V(.245f,.727f),V(.29f,.718f));
                p.QuadraticCurveTo(V(.354f,.739f),V(.352f,.686f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.141f,.697f,.026f,.027f,new Color32(236,134,137,255));
                Ellipse(.185f,.649f,.026f,.025f,gold);
                Ellipse(.257f,.649f,.026f,.025f,mint);
                Ellipse(.305f,.691f,.025f,.026f,new Color32(141,181,220,255));
                Ellipse(.152f,.767f,.025f,.026f,lavender);
                Ellipse(.245f,.780f,.020f,.027f,ink);
                Ellipse(.29f,.72f,.035f,.037f,body,true);
            }
        }
    }
}
