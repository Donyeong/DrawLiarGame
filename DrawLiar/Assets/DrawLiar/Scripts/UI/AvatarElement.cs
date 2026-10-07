using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class AvatarElement : VisualElement
    {
        public static readonly Color[] Colors = { new Color32(171,156,237,255), new Color32(248,177,152,255), new Color32(145,203,182,255), new Color32(245,210,118,255), new Color32(153,190,229,255), new Color32(231,163,193,255) };
        private readonly int _colorIndex;
        public AvatarAccessory Equipment { get; }

        public AvatarElement(int color = 0, long decoration = (long)AvatarAccessory.Painter)
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
            var bodyShape=Equipment&(AvatarAccessory.RoundBody|AvatarAccessory.SoftSquareBody|AvatarAccessory.DropletBody|AvatarAccessory.BeanBody|AvatarAccessory.CatBody|AvatarAccessory.BearBody);
            float headCenter=.53f,headWidth=1f,headSeat=.255f;
            float headphoneCenter=.53f,headphoneWidth=1f;
            if(bodyShape==AvatarAccessory.RoundBody){headWidth=1.1f;headphoneWidth=1.08f;}
            else if(bodyShape==AvatarAccessory.SoftSquareBody){headWidth=1.07f;headSeat=.245f;headphoneWidth=1.1f;}
            else if(bodyShape==AvatarAccessory.DropletBody){headWidth=.84f;headSeat=.28f;headphoneWidth=.75f;}
            else if(bodyShape==AvatarAccessory.BeanBody){headCenter=.56f;headWidth=.98f;headSeat=.245f;headphoneCenter=.52f;headphoneWidth=1.1f;}
            else if(bodyShape==AvatarAccessory.CatBody||bodyShape==AvatarAccessory.BearBody){headWidth=1.1f;headphoneWidth=1.08f;}
            Vector2 H(float x,float y)=>V(headCenter+(x-.53f)*headWidth,headSeat+(y-.24f)*.94f);
            Vector2 P(float x,float y)=>V(headphoneCenter+(x-.53f)*headphoneWidth,y);
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
            void HeadEllipse(float x,float y,float rx,float ry,Color fill,bool outline=false)
            {
                Ellipse(headCenter+(x-.53f)*headWidth,headSeat+(y-.24f)*.94f,rx*headWidth,ry*.94f,fill,outline);
            }
            void HeadStar(float x,float y,float radius,Color fill,bool outline=false)
            {
                Star(headCenter+(x-.53f)*headWidth,headSeat+(y-.24f)*.94f,radius*.94f,fill,outline);
            }
            void HeadBox(float x,float y,float width,float height,float radius,Color fill)
            {
                RoundedBox(headCenter+(x-.53f)*headWidth,headSeat+(y-.24f)*.94f,width*headWidth,height*.94f,radius*.94f,fill);
            }
            void PhoneEllipse(float x,float y,float rx,float ry,Color fill,bool outline=false)
            {
                Ellipse(headphoneCenter+(x-.53f)*headphoneWidth,y,rx*headphoneWidth,ry,fill,outline);
            }
            void Heart(float x,float y,float radius,Color fill)
            {
                p.fillColor=fill; p.BeginPath(); p.MoveTo(V(x,y+radius));
                p.BezierCurveTo(V(x-radius*1.4f,y-radius*.15f),V(x-radius*1.1f,y-radius*1.55f),V(x,y-radius*.6f));
                p.BezierCurveTo(V(x+radius*1.1f,y-radius*1.55f),V(x+radius*1.4f,y-radius*.15f),V(x,y+radius));
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
            if(bodyShape==AvatarAccessory.RoundBody)
            {
                p.BezierCurveTo(V(.745f,.19f),V(.875f,.345f),V(.875f,.535f));
                p.BezierCurveTo(V(.875f,.632f),V(.845f,.691f),V(.815f,.73f));
            }
            else if(bodyShape==AvatarAccessory.SoftSquareBody)
            {
                p.BezierCurveTo(V(.705f,.19f),V(.855f,.185f),V(.855f,.35f));
                p.BezierCurveTo(V(.855f,.50f),V(.855f,.661f),V(.815f,.73f));
            }
            else if(bodyShape==AvatarAccessory.DropletBody)
            {
                p.BezierCurveTo(V(.56f,.255f),V(.62f,.292f),V(.692f,.355f));
                p.BezierCurveTo(V(.79f,.435f),V(.856f,.55f),V(.847f,.64f));
                p.BezierCurveTo(V(.837f,.698f),V(.82f,.712f),V(.815f,.73f));
            }
            else if(bodyShape==AvatarAccessory.BeanBody)
            {
                p.BezierCurveTo(V(.71f,.12f),V(.875f,.32f),V(.80f,.445f));
                p.BezierCurveTo(V(.75f,.522f),V(.822f,.58f),V(.835f,.65f));
                p.BezierCurveTo(V(.845f,.684f),V(.828f,.714f),V(.815f,.73f));
            }
            else if(bodyShape==AvatarAccessory.CatBody)
            {
                p.BezierCurveTo(V(.59f,.19f),V(.63f,.222f),V(.67f,.23f));
                p.BezierCurveTo(V(.72f,.20f),V(.772f,.14f),V(.802f,.118f));
                p.BezierCurveTo(V(.82f,.15f),V(.805f,.276f),V(.793f,.303f));
                p.BezierCurveTo(V(.841f,.364f),V(.86f,.439f),V(.849f,.535f));
                p.BezierCurveTo(V(.846f,.63f),V(.824f,.69f),V(.815f,.73f));
            }
            else if(bodyShape==AvatarAccessory.BearBody)
            {
                p.BezierCurveTo(V(.57f,.19f),V(.61f,.21f),V(.652f,.234f));
                p.BezierCurveTo(V(.666f,.133f),V(.766f,.118f),V(.806f,.171f));
                p.BezierCurveTo(V(.865f,.229f),V(.827f,.29f),V(.802f,.312f));
                p.BezierCurveTo(V(.848f,.371f),V(.86f,.447f),V(.849f,.535f));
                p.BezierCurveTo(V(.846f,.63f),V(.824f,.69f),V(.815f,.73f));
            }
            else
            {
                p.BezierCurveTo(V(.72f,.19f),V(.845f,.34f),V(.845f,.535f));
                p.BezierCurveTo(V(.845f,.635f),V(.823f,.69f),V(.815f,.73f));
            }
            p.BezierCurveTo(V(.84f,.805f),V(.745f,.865f),V(.53f,.865f));
            p.BezierCurveTo(V(.315f,.865f),V(.22f,.805f),V(.245f,.73f));
            if(bodyShape==AvatarAccessory.RoundBody)
            {
                p.BezierCurveTo(V(.215f,.691f),V(.185f,.632f),V(.185f,.535f));
                p.BezierCurveTo(V(.185f,.345f),V(.315f,.19f),V(.53f,.19f));
            }
            else if(bodyShape==AvatarAccessory.SoftSquareBody)
            {
                p.BezierCurveTo(V(.205f,.661f),V(.205f,.50f),V(.205f,.35f));
                p.BezierCurveTo(V(.205f,.185f),V(.355f,.19f),V(.53f,.19f));
            }
            else if(bodyShape==AvatarAccessory.DropletBody)
            {
                p.BezierCurveTo(V(.24f,.712f),V(.223f,.698f),V(.213f,.64f));
                p.BezierCurveTo(V(.204f,.55f),V(.27f,.435f),V(.368f,.355f));
                p.BezierCurveTo(V(.44f,.292f),V(.50f,.255f),V(.53f,.19f));
            }
            else if(bodyShape==AvatarAccessory.BeanBody)
            {
                p.BezierCurveTo(V(.195f,.68f),V(.17f,.60f),V(.18f,.495f));
                p.BezierCurveTo(V(.20f,.28f),V(.355f,.19f),V(.53f,.19f));
            }
            else if(bodyShape==AvatarAccessory.CatBody)
            {
                p.BezierCurveTo(V(.236f,.69f),V(.214f,.63f),V(.211f,.535f));
                p.BezierCurveTo(V(.20f,.439f),V(.219f,.364f),V(.267f,.303f));
                p.BezierCurveTo(V(.255f,.276f),V(.24f,.15f),V(.258f,.118f));
                p.BezierCurveTo(V(.288f,.14f),V(.34f,.20f),V(.39f,.23f));
                p.BezierCurveTo(V(.43f,.222f),V(.47f,.19f),V(.53f,.19f));
            }
            else if(bodyShape==AvatarAccessory.BearBody)
            {
                p.BezierCurveTo(V(.236f,.69f),V(.214f,.63f),V(.211f,.535f));
                p.BezierCurveTo(V(.20f,.447f),V(.212f,.371f),V(.258f,.312f));
                p.BezierCurveTo(V(.233f,.29f),V(.195f,.229f),V(.254f,.171f));
                p.BezierCurveTo(V(.294f,.118f),V(.394f,.133f),V(.408f,.234f));
                p.BezierCurveTo(V(.45f,.21f),V(.49f,.19f),V(.53f,.19f));
            }
            else
            {
                p.BezierCurveTo(V(.237f,.69f),V(.215f,.635f),V(.215f,.535f));
                p.BezierCurveTo(V(.215f,.34f),V(.34f,.19f),V(.53f,.19f));
            }
            p.ClosePath(); p.Fill(); p.Stroke();
            if(bodyShape==AvatarAccessory.CatBody)
            {
                p.fillColor=new Color32(247,169,184,255); p.BeginPath(); p.MoveTo(V(.269f,.17f));
                p.BezierCurveTo(V(.284f,.186f),V(.312f,.224f),V(.324f,.246f));
                p.BezierCurveTo(V(.307f,.267f),V(.285f,.282f),V(.276f,.271f));
                p.BezierCurveTo(V(.268f,.239f),V(.26f,.194f),V(.269f,.17f)); p.ClosePath(); p.Fill();
                p.BeginPath(); p.MoveTo(V(.791f,.17f));
                p.BezierCurveTo(V(.776f,.186f),V(.748f,.224f),V(.736f,.246f));
                p.BezierCurveTo(V(.753f,.267f),V(.775f,.282f),V(.784f,.271f));
                p.BezierCurveTo(V(.792f,.239f),V(.80f,.194f),V(.791f,.17f)); p.ClosePath(); p.Fill();
            }
            else if(bodyShape==AvatarAccessory.BearBody)
            {
                Ellipse(.311f,.217f,.041f,.043f,cream); Ellipse(.749f,.217f,.041f,.043f,cream);
            }
            Ellipse(.53f,.625f,.235f,.2f,cream);
            var alternateFace=Equipment&(AvatarAccessory.HeartEyes|AvatarAccessory.StarEyes|AvatarAccessory.SpiralEyes|AvatarAccessory.PixelFace|AvatarAccessory.SkullFace|AvatarAccessory.CatFace);
            if(alternateFace==AvatarAccessory.None)
            {
                Ellipse(.35f,.545f,.044f,.02f,new Color32(247,169,184,255)); Ellipse(.71f,.545f,.044f,.02f,new Color32(247,169,184,255));
            }
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
            else if((Equipment&AvatarAccessory.Blush)!=0)
            {
                Ellipse(.43f,.488f,.018f,.026f,ink); Ellipse(.63f,.488f,.018f,.026f,ink);
                Ellipse(.35f,.548f,.052f,.033f,new Color32(247,155,178,255));
                Ellipse(.71f,.548f,.052f,.033f,new Color32(247,155,178,255));
                p.BeginPath(); p.MoveTo(V(.402f,.443f)); p.QuadraticCurveTo(V(.43f,.432f),V(.458f,.443f));
                p.MoveTo(V(.602f,.443f)); p.QuadraticCurveTo(V(.63f,.432f),V(.658f,.443f));
                p.MoveTo(V(.514f,.564f)); p.QuadraticCurveTo(V(.53f,.581f),V(.546f,.564f)); p.Stroke();
                p.strokeColor=new Color32(223,112,140,255); p.lineWidth=Mathf.Max(.65f,size*.008f);
                p.BeginPath();
                for(int mark=0;mark<3;mark++)
                {
                    float x=.327f+mark*.021f;
                    p.MoveTo(V(x,.539f)); p.LineTo(V(x-.006f,.557f));
                    p.MoveTo(V(x+.36f,.539f)); p.LineTo(V(x+.354f,.557f));
                }
                p.Stroke(); p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
            }
            else if((Equipment&AvatarAccessory.Mischievous)!=0)
            {
                Ellipse(.43f,.482f,.021f,.027f,ink);
                p.BeginPath(); p.MoveTo(V(.603f,.488f)); p.QuadraticCurveTo(V(.631f,.469f),V(.659f,.479f));
                p.MoveTo(V(.398f,.433f)); p.QuadraticCurveTo(V(.435f,.416f),V(.464f,.445f));
                p.MoveTo(V(.601f,.443f)); p.QuadraticCurveTo(V(.632f,.423f),V(.666f,.428f)); p.Stroke();
                p.fillColor=ink; p.BeginPath(); p.MoveTo(V(.479f,.555f));
                p.BezierCurveTo(V(.518f,.577f),V(.563f,.575f),V(.59f,.547f));
                p.BezierCurveTo(V(.576f,.614f),V(.5f,.615f),V(.479f,.555f)); p.ClosePath(); p.Fill();
                p.fillColor=cream; p.BeginPath(); p.MoveTo(V(.535f,.568f));
                p.BezierCurveTo(V(.55f,.566f),V(.565f,.56f),V(.58f,.551f));
                p.LineTo(V(.561f,.59f)); p.ClosePath(); p.Fill();
            }
            else if((Equipment&AvatarAccessory.Tearful)!=0)
            {
                Ellipse(.43f,.482f,.025f,.034f,ink); Ellipse(.63f,.482f,.025f,.034f,ink);
                Ellipse(.424f,.473f,.007f,.01f,cream); Ellipse(.624f,.473f,.007f,.01f,cream);
                p.BeginPath(); p.MoveTo(V(.397f,.438f)); p.QuadraticCurveTo(V(.43f,.424f),V(.462f,.443f));
                p.MoveTo(V(.599f,.443f)); p.QuadraticCurveTo(V(.63f,.424f),V(.663f,.438f));
                p.MoveTo(V(.49f,.583f)); p.QuadraticCurveTo(V(.512f,.56f),V(.53f,.574f));
                p.QuadraticCurveTo(V(.548f,.56f),V(.57f,.583f)); p.Stroke();
                p.fillColor=new Color32(151,202,239,255); p.BeginPath(); p.MoveTo(V(.403f,.517f));
                p.BezierCurveTo(V(.39f,.541f),V(.378f,.556f),V(.383f,.574f));
                p.BezierCurveTo(V(.389f,.593f),V(.42f,.595f),V(.426f,.575f));
                p.BezierCurveTo(V(.428f,.556f),V(.412f,.533f),V(.403f,.517f)); p.ClosePath(); p.Fill();
                p.BeginPath(); p.MoveTo(V(.657f,.517f));
                p.BezierCurveTo(V(.67f,.541f),V(.682f,.556f),V(.677f,.574f));
                p.BezierCurveTo(V(.671f,.593f),V(.64f,.595f),V(.634f,.575f));
                p.BezierCurveTo(V(.632f,.556f),V(.648f,.533f),V(.657f,.517f)); p.ClosePath(); p.Fill();
            }
            else if((Equipment&AvatarAccessory.HeartEyes)!=0)
            {
                var rose=new Color32(234,102,149,255);
                Heart(.43f,.482f,.049f,rose); Heart(.63f,.482f,.049f,rose);
                Ellipse(.35f,.557f,.043f,.023f,new Color32(250,182,197,255)); Ellipse(.71f,.557f,.043f,.023f,new Color32(250,182,197,255));
                p.BeginPath(); p.MoveTo(V(.485f,.561f)); p.QuadraticCurveTo(V(.53f,.619f),V(.575f,.561f)); p.Stroke();
            }
            else if((Equipment&AvatarAccessory.StarEyes)!=0)
            {
                Star(.43f,.478f,.057f,gold,true); Star(.63f,.478f,.057f,gold,true);
                Star(.333f,.567f,.022f,lavender); Star(.727f,.567f,.022f,lavender);
                p.fillColor=ink; p.BeginPath(); p.MoveTo(V(.474f,.558f));
                p.BezierCurveTo(V(.512f,.573f),V(.548f,.573f),V(.586f,.558f));
                p.BezierCurveTo(V(.58f,.635f),V(.48f,.635f),V(.474f,.558f)); p.ClosePath(); p.Fill();
                p.strokeColor=cream; p.lineWidth=size*.018f;
                p.BeginPath(); p.MoveTo(V(.491f,.573f)); p.QuadraticCurveTo(V(.53f,.588f),V(.569f,.573f)); p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
            }
            else if((Equipment&AvatarAccessory.SpiralEyes)!=0)
            {
                p.strokeColor=new Color32(116,86,164,255);
                for(int eye=0;eye<2;eye++)
                {
                    float x=.43f+eye*.2f;
                    p.BeginPath();
                    for(int step=0;step<=24;step++)
                    {
                        float angle=step*Mathf.PI*.15f;
                        float radius=.004f+step*.002f;
                        var point=V(x+Mathf.Cos(angle)*radius,.482f+Mathf.Sin(angle)*radius);
                        if(step==0)p.MoveTo(point);else p.LineTo(point);
                    }
                    p.Stroke();
                }
                p.strokeColor=ink;
                p.BeginPath(); p.MoveTo(V(.482f,.57f)); p.LineTo(V(.506f,.59f));
                p.LineTo(V(.53f,.567f)); p.LineTo(V(.554f,.59f)); p.LineTo(V(.578f,.57f)); p.Stroke();
            }
            else if((Equipment&AvatarAccessory.PixelFace)!=0)
            {
                p.lineJoin=LineJoin.Miter; p.lineCap=LineCap.Butt;
                p.fillColor=new Color32(204,231,211,255); p.BeginPath(); p.MoveTo(V(.37f,.44f));
                p.LineTo(V(.69f,.44f)); p.LineTo(V(.69f,.47f)); p.LineTo(V(.73f,.47f));
                p.LineTo(V(.73f,.69f)); p.LineTo(V(.69f,.69f)); p.LineTo(V(.69f,.73f));
                p.LineTo(V(.37f,.73f)); p.LineTo(V(.37f,.69f)); p.LineTo(V(.33f,.69f));
                p.LineTo(V(.33f,.47f)); p.LineTo(V(.37f,.47f)); p.ClosePath(); p.Fill(); p.Stroke();
                for(int eye=0;eye<2;eye++)
                {
                    float x=.404f+eye*.212f;
                    p.fillColor=ink; p.BeginPath(); p.MoveTo(V(x,.495f));
                    p.LineTo(V(x+.036f,.495f)); p.LineTo(V(x+.036f,.54f)); p.LineTo(V(x,.54f)); p.ClosePath(); p.Fill();
                }
                p.lineWidth=size*.025f;
                p.BeginPath(); p.MoveTo(V(.464f,.594f)); p.LineTo(V(.464f,.622f));
                p.LineTo(V(.492f,.622f)); p.LineTo(V(.492f,.644f)); p.LineTo(V(.568f,.644f));
                p.LineTo(V(.568f,.622f)); p.LineTo(V(.596f,.622f)); p.LineTo(V(.596f,.594f)); p.Stroke();
                p.lineWidth=Mathf.Max(.65f,size*.0135f); p.lineJoin=LineJoin.Round; p.lineCap=LineCap.Round;
            }
            else if((Equipment&AvatarAccessory.SkullFace)!=0)
            {
                p.fillColor=new Color32(252,252,248,255); p.BeginPath(); p.MoveTo(V(.53f,.42f));
                p.BezierCurveTo(V(.70f,.42f),V(.722f,.53f),V(.706f,.604f));
                p.BezierCurveTo(V(.698f,.635f),V(.668f,.64f),V(.655f,.648f));
                p.LineTo(V(.655f,.710f)); p.BezierCurveTo(V(.60f,.739f),V(.46f,.739f),V(.405f,.710f));
                p.LineTo(V(.405f,.648f)); p.BezierCurveTo(V(.392f,.64f),V(.362f,.635f),V(.354f,.604f));
                p.BezierCurveTo(V(.338f,.53f),V(.36f,.42f),V(.53f,.42f)); p.ClosePath(); p.Fill(); p.Stroke();
                Ellipse(.444f,.555f,.041f,.048f,ink); Ellipse(.616f,.555f,.041f,.048f,ink);
                p.fillColor=ink; p.BeginPath(); p.MoveTo(V(.53f,.603f));
                p.LineTo(V(.505f,.64f)); p.LineTo(V(.555f,.64f)); p.ClosePath(); p.Fill();
                p.BeginPath(); p.MoveTo(V(.427f,.679f)); p.LineTo(V(.633f,.679f));
                for(int tooth=0;tooth<3;tooth++)
                {
                    float x=.468f+tooth*.062f;
                    p.MoveTo(V(x,.653f)); p.LineTo(V(x,.714f));
                }
                p.Stroke();
            }
            else if((Equipment&AvatarAccessory.CatFace)!=0)
            {
                Ellipse(.43f,.48f,.042f,.047f,mint,true); Ellipse(.63f,.48f,.042f,.047f,mint,true);
                Ellipse(.43f,.48f,.010f,.029f,ink); Ellipse(.63f,.48f,.010f,.029f,ink);
                Ellipse(.421f,.47f,.007f,.009f,cream); Ellipse(.621f,.47f,.007f,.009f,cream);
                Ellipse(.354f,.575f,.031f,.023f,new Color32(250,182,197,255)); Ellipse(.706f,.575f,.031f,.023f,new Color32(250,182,197,255));
                p.fillColor=new Color32(225,132,153,255); p.BeginPath(); p.MoveTo(V(.509f,.555f));
                p.LineTo(V(.551f,.555f)); p.LineTo(V(.53f,.578f)); p.ClosePath(); p.Fill();
                p.BeginPath(); p.MoveTo(V(.53f,.578f)); p.LineTo(V(.53f,.594f));
                p.QuadraticCurveTo(V(.508f,.624f),V(.486f,.595f));
                p.MoveTo(V(.53f,.594f)); p.QuadraticCurveTo(V(.552f,.624f),V(.574f,.595f));
                p.MoveTo(V(.324f,.568f)); p.LineTo(V(.384f,.583f));
                p.MoveTo(V(.319f,.604f)); p.LineTo(V(.38f,.606f));
                p.MoveTo(V(.736f,.568f)); p.LineTo(V(.676f,.583f));
                p.MoveTo(V(.741f,.604f)); p.LineTo(V(.68f,.606f)); p.Stroke();
            }
            else
            {
                Ellipse(.43f,.48f,.021f,.03f,ink); Ellipse(.63f,.48f,.021f,.03f,ink);
                p.BeginPath(); p.MoveTo(V(.49f,.555f)); p.QuadraticCurveTo(V(.53f,.605f),V(.57f,.555f)); p.Stroke();
            }
            if((Equipment & AvatarAccessory.Beret)!=0)
            {
                p.fillColor=new Color32(112,150,156,255); p.BeginPath(); p.MoveTo(H(.27f,.23f));
                p.BezierCurveTo(H(.20f,.205f),H(.29f,.125f),H(.40f,.10f));
                p.BezierCurveTo(H(.39f,.085f),H(.365f,.053f),H(.39f,.047f));
                p.BezierCurveTo(H(.42f,.039f),H(.44f,.054f),H(.44f,.087f));
                p.BezierCurveTo(H(.55f,.039f),H(.71f,.06f),H(.76f,.11f));
                p.BezierCurveTo(H(.81f,.19f),H(.65f,.22f),H(.49f,.25f));
                p.BezierCurveTo(H(.38f,.27f),H(.29f,.265f),H(.27f,.23f)); p.ClosePath(); p.Fill(); p.Stroke();
                HeadEllipse(.43f,.177f,.023f,.015f,gold); HeadEllipse(.453f,.16f,.024f,.019f,gold);
            }
            if((Equipment & AvatarAccessory.Crown)!=0)
            {
                p.fillColor=gold; p.BeginPath(); p.MoveTo(H(.30f,.24f));
                p.LineTo(H(.27f,.105f)); p.LineTo(H(.39f,.165f));
                p.LineTo(H(.43f,.035f)); p.LineTo(H(.535f,.135f));
                p.LineTo(H(.64f,.035f)); p.LineTo(H(.68f,.165f));
                p.LineTo(H(.80f,.105f)); p.LineTo(H(.77f,.24f));
                p.QuadraticCurveTo(H(.53f,.27f),H(.30f,.24f)); p.ClosePath(); p.Fill(); p.Stroke();
                HeadBox(.315f,.215f,.44f,.045f,.018f,new Color32(243,186,99,255));
                HeadStar(.535f,.174f,.040f,cream,true);
                HeadEllipse(.36f,.188f,.017f,.020f,mint); HeadEllipse(.71f,.188f,.017f,.020f,mint);
            }
            if((Equipment & AvatarAccessory.WizardHat)!=0)
            {
                var violet=new Color32(120,103,164,255);
                p.fillColor=violet; p.BeginPath(); p.MoveTo(H(.30f,.24f));
                p.BezierCurveTo(H(.3733333f,.1566667f),H(.4366667f,.095f),H(.49f,.055f));
                p.QuadraticCurveTo(H(.52f,.015f),H(.58f,.024f));
                p.QuadraticCurveTo(H(.59f,.087f),H(.65f,.16f));
                p.LineTo(H(.76f,.24f)); p.ClosePath(); p.Fill(); p.Stroke();
                p.fillColor=gold; p.BeginPath(); p.MoveTo(H(.335f,.203f));
                p.BezierCurveTo(H(.465f,.215f),H(.5866667f,.2146667f),H(.70f,.202f));
                p.LineTo(H(.745f,.24f)); p.QuadraticCurveTo(H(.53f,.261f),H(.30f,.24f));
                p.ClosePath(); p.Fill(); p.Stroke();
                HeadEllipse(.53f,.256f,.325f,.045f,lavender,true);
                HeadStar(.53f,.127f,.034f,gold);
            }
            if((Equipment & AvatarAccessory.Headphones)!=0)
            {
                p.BeginPath(); p.MoveTo(P(.235f,.405f));
                p.BezierCurveTo(P(.235f,.12f),P(.825f,.12f),P(.825f,.405f));
                p.lineWidth=size*.069f; p.Stroke();
                p.strokeColor=mint; p.lineWidth=size*.039f; p.Stroke();
                p.strokeColor=ink; p.lineWidth=Mathf.Max(.65f,size*.0135f);
                PhoneEllipse(.235f,.418f,.044f,.096f,mint,true);
                PhoneEllipse(.825f,.418f,.044f,.096f,mint,true);
                PhoneEllipse(.235f,.418f,.018f,.062f,cream);
                PhoneEllipse(.825f,.418f,.018f,.062f,cream);
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
