using UnityEngine;

namespace DrawLiar
{
    public sealed partial class AvatarElement
    {
        private bool PaintMemeBody(AvatarDrawContext d,AvatarAccessory part)
        {
            var p=d.Painter;
            var fill=d.Body;
            switch(part)
            {
                case AvatarAccessory.LongCatBody:
                    p.fillColor=fill; p.BeginPath(); p.MoveTo(d.V(.74f,.71f));
                    p.BezierCurveTo(d.V(.96f,.77f),d.V(.99f,.52f),d.V(.88f,.53f));
                    p.BezierCurveTo(d.V(.80f,.54f),d.V(.83f,.64f),d.V(.87f,.62f));
                    p.BezierCurveTo(d.V(.90f,.71f),d.V(.78f,.72f),d.V(.74f,.71f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.BeginPath(); p.MoveTo(d.V(.29f,.33f));
                    p.BezierCurveTo(d.V(.27f,.23f),d.V(.28f,.16f),d.V(.30f,.10f));
                    p.LineTo(d.V(.415f,.205f)); p.BezierCurveTo(d.V(.49f,.18f),d.V(.59f,.18f),d.V(.66f,.205f));
                    p.LineTo(d.V(.765f,.10f)); p.BezierCurveTo(d.V(.79f,.18f),d.V(.78f,.26f),d.V(.77f,.33f));
                    p.LineTo(d.V(.77f,.79f)); p.BezierCurveTo(d.V(.77f,.90f),d.V(.29f,.90f),d.V(.29f,.79f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    p.fillColor=new Color32(248,172,198,255); p.BeginPath(); p.MoveTo(d.V(.314f,.157f));
                    p.BezierCurveTo(d.V(.33f,.18f),d.V(.351f,.203f),d.V(.368f,.22f));
                    p.LineTo(d.V(.31f,.245f)); p.ClosePath(); p.Fill();
                    p.BeginPath(); p.MoveTo(d.V(.75f,.157f));
                    p.BezierCurveTo(d.V(.734f,.18f),d.V(.713f,.203f),d.V(.696f,.22f));
                    p.LineTo(d.V(.754f,.245f)); p.ClosePath(); p.Fill();
                    break;
                case AvatarAccessory.PuddingBody:
                    p.fillColor=Color.Lerp(fill,d.Cream,.35f); p.BeginPath(); p.MoveTo(d.V(.335f,.29f));
                    p.BezierCurveTo(d.V(.43f,.26f),d.V(.63f,.26f),d.V(.725f,.29f));
                    p.LineTo(d.V(.84f,.77f)); p.BezierCurveTo(d.V(.87f,.89f),d.V(.19f,.89f),d.V(.22f,.77f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    p.fillColor=new Color32(174,105,73,255); p.BeginPath(); p.MoveTo(d.V(.335f,.29f));
                    p.BezierCurveTo(d.V(.435f,.245f),d.V(.63f,.245f),d.V(.725f,.29f));
                    p.LineTo(d.V(.742f,.36f)); p.BezierCurveTo(d.V(.68f,.38f),d.V(.67f,.34f),d.V(.642f,.357f));
                    p.BezierCurveTo(d.V(.61f,.38f),d.V(.635f,.416f),d.V(.606f,.414f));
                    p.BezierCurveTo(d.V(.57f,.414f),d.V(.60f,.353f),d.V(.55f,.355f));
                    p.BezierCurveTo(d.V(.48f,.36f),d.V(.43f,.345f),d.V(.414f,.37f));
                    p.BezierCurveTo(d.V(.40f,.413f),d.V(.376f,.414f),d.V(.37f,.373f));
                    p.LineTo(d.V(.318f,.36f)); p.ClosePath(); p.Fill(); p.Stroke();
                    d.Ellipse(.478f,.282f,.072f,.012f,new Color32(224,157,97,255));
                    break;
                case AvatarAccessory.MarshmallowBody:
                    d.Box(.25f,.21f,.56f,.65f,.11f,Color.Lerp(fill,d.Cream,.60f));
                    p.strokeColor=Color.Lerp(d.Ink,fill,.55f); p.lineWidth=d.Size*.009f;
                    p.BeginPath(); p.MoveTo(d.V(.32f,.28f));
                    p.BezierCurveTo(d.V(.39f,.25f),d.V(.67f,.25f),d.V(.74f,.28f)); p.Stroke();
                    d.ResetStroke();
                    d.Ellipse(.25f,.72f,.034f,.074f,Color.Lerp(fill,d.Cream,.60f),true);
                    d.Ellipse(.81f,.72f,.034f,.074f,Color.Lerp(fill,d.Cream,.60f),true);
                    break;
                case AvatarAccessory.GhostBody:
                    p.fillColor=Color.Lerp(fill,d.Cream,.78f); p.BeginPath(); p.MoveTo(d.V(.24f,.51f));
                    p.BezierCurveTo(d.V(.24f,.08f),d.V(.82f,.08f),d.V(.82f,.51f));
                    p.BezierCurveTo(d.V(.81f,.69f),d.V(.84f,.77f),d.V(.88f,.85f));
                    p.BezierCurveTo(d.V(.82f,.94f),d.V(.76f,.78f),d.V(.71f,.85f));
                    p.BezierCurveTo(d.V(.64f,.96f),d.V(.61f,.83f),d.V(.55f,.86f));
                    p.BezierCurveTo(d.V(.48f,.96f),d.V(.43f,.79f),d.V(.37f,.85f));
                    p.BezierCurveTo(d.V(.31f,.94f),d.V(.23f,.89f),d.V(.18f,.86f));
                    p.BezierCurveTo(d.V(.23f,.74f),d.V(.25f,.66f),d.V(.24f,.51f)); p.ClosePath(); p.Fill(); p.Stroke();
                    break;
                case AvatarAccessory.BlockBody:
                    p.lineJoin=UnityEngine.UIElements.LineJoin.Miter;
                    p.fillColor=fill; p.BeginPath(); p.MoveTo(d.V(.31f,.20f));
                    p.LineTo(d.V(.75f,.20f)); p.LineTo(d.V(.75f,.25f)); p.LineTo(d.V(.82f,.25f));
                    p.LineTo(d.V(.82f,.72f)); p.LineTo(d.V(.86f,.72f)); p.LineTo(d.V(.86f,.81f));
                    p.LineTo(d.V(.78f,.81f)); p.LineTo(d.V(.78f,.87f)); p.LineTo(d.V(.28f,.87f));
                    p.LineTo(d.V(.28f,.81f)); p.LineTo(d.V(.20f,.81f)); p.LineTo(d.V(.20f,.72f));
                    p.LineTo(d.V(.24f,.72f)); p.LineTo(d.V(.24f,.25f)); p.LineTo(d.V(.31f,.25f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    d.Box(.33f,.86f,.16f,.065f,0,d.Ink,false); d.Box(.59f,.86f,.16f,.065f,0,d.Ink,false);
                    d.Box(.31f,.28f,.055f,.055f,0,Color.Lerp(fill,Color.white,.45f),false);
                    d.Box(.365f,.335f,.04f,.04f,0,Color.Lerp(fill,Color.white,.45f),false);
                    break;
                default: return false;
            }
            d.ResetStroke(); return true;
        }

        private static bool HasMemeExpression(AvatarAccessory part)=>part==AvatarAccessory.BlankFace
            ||part==AvatarAccessory.SmugFace||part==AvatarAccessory.PanicFace||part==AvatarAccessory.SquishFace||part==AvatarAccessory.WideGrinFace;

        private bool PaintMemeExpression(AvatarDrawContext d,AvatarAccessory part)
        {
            if(!HasMemeExpression(part))return false;
            var p=d.Painter;
            p.lineWidth=Mathf.Max(.65f,d.Size*.009f);
            switch(part)
            {
                case AvatarAccessory.BlankFace:
                    d.Ellipse(.413f,.481f,.038f,.043f,Color.white,true); d.Ellipse(.647f,.481f,.038f,.043f,Color.white,true);
                    d.Ellipse(.418f,.49f,.009f,.010f,d.Ink); d.Ellipse(.652f,.49f,.009f,.010f,d.Ink);
                    p.BeginPath(); p.MoveTo(d.V(.508f,.583f)); p.LineTo(d.V(.552f,.583f)); p.Stroke();
                    break;
                case AvatarAccessory.SmugFace:
                    d.Ellipse(.43f,.49f,.022f,.027f,d.Ink); d.Ellipse(.635f,.495f,.023f,.020f,d.Ink);
                    p.BeginPath(); p.MoveTo(d.V(.395f,.445f)); p.BezierCurveTo(d.V(.43f,.431f),d.V(.447f,.441f),d.V(.469f,.446f));
                    p.MoveTo(d.V(.602f,.457f)); p.LineTo(d.V(.67f,.468f));
                    p.MoveTo(d.V(.476f,.574f)); p.BezierCurveTo(d.V(.513f,.606f),d.V(.567f,.592f),d.V(.594f,.553f));
                    p.MoveTo(d.V(.594f,.553f)); p.LineTo(d.V(.594f,.58f)); p.Stroke();
                    d.Ellipse(.704f,.564f,.028f,.012f,new Color32(247,169,184,255));
                    break;
                case AvatarAccessory.PanicFace:
                    d.Ellipse(.413f,.483f,.052f,.059f,Color.white,true); d.Ellipse(.65f,.489f,.049f,.053f,Color.white,true);
                    d.Ellipse(.427f,.491f,.011f,.015f,d.Ink); d.Ellipse(.638f,.487f,.010f,.015f,d.Ink);
                    p.BeginPath(); p.MoveTo(d.V(.37f,.411f)); p.LineTo(d.V(.449f,.398f));
                    p.MoveTo(d.V(.611f,.416f)); p.LineTo(d.V(.69f,.427f)); p.Stroke();
                    p.fillColor=d.Ink; p.BeginPath(); p.MoveTo(d.V(.495f,.573f));
                    p.BezierCurveTo(d.V(.511f,.546f),d.V(.529f,.593f),d.V(.541f,.57f));
                    p.BezierCurveTo(d.V(.573f,.54f),d.V(.585f,.595f),d.V(.566f,.611f));
                    p.BezierCurveTo(d.V(.54f,.635f),d.V(.536f,.588f),d.V(.52f,.613f));
                    p.BezierCurveTo(d.V(.493f,.636f),d.V(.482f,.599f),d.V(.495f,.573f)); p.ClosePath(); p.Fill();
                    p.fillColor=new Color32(141,212,241,255); p.BeginPath(); p.MoveTo(d.V(.318f,.404f));
                    p.BezierCurveTo(d.V(.29f,.443f),d.V(.275f,.474f),d.V(.299f,.481f));
                    p.BezierCurveTo(d.V(.344f,.491f),d.V(.337f,.442f),d.V(.318f,.404f)); p.ClosePath(); p.Fill(); p.Stroke();
                    break;
                case AvatarAccessory.SquishFace:
                    p.lineWidth=Mathf.Max(.65f,d.Size*.013f); p.BeginPath();
                    p.MoveTo(d.V(.387f,.456f)); p.LineTo(d.V(.439f,.485f)); p.LineTo(d.V(.383f,.505f));
                    p.MoveTo(d.V(.677f,.456f)); p.LineTo(d.V(.621f,.485f)); p.LineTo(d.V(.677f,.505f));
                    p.MoveTo(d.V(.477f,.574f)); p.LineTo(d.V(.505f,.594f)); p.LineTo(d.V(.531f,.573f));
                    p.LineTo(d.V(.555f,.596f)); p.LineTo(d.V(.582f,.574f)); p.Stroke();
                    d.Ellipse(.35f,.554f,.047f,.028f,new Color32(245,136,160,255));
                    d.Ellipse(.71f,.554f,.047f,.028f,new Color32(245,136,160,255));
                    break;
                case AvatarAccessory.WideGrinFace:
                    d.Ellipse(.416f,.478f,.016f,.026f,d.Ink); d.Ellipse(.644f,.478f,.016f,.026f,d.Ink);
                    p.fillColor=Color.white; p.BeginPath(); p.MoveTo(d.V(.419f,.56f));
                    p.BezierCurveTo(d.V(.485f,.581f),d.V(.575f,.581f),d.V(.641f,.56f));
                    p.BezierCurveTo(d.V(.638f,.673f),d.V(.422f,.673f),d.V(.419f,.56f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.lineWidth=Mathf.Max(.5f,d.Size*.005f); p.BeginPath();
                    for(int tooth=0;tooth<5;tooth++)
                    {
                        float x=.458f+tooth*.036f; p.MoveTo(d.V(x,.582f)); p.LineTo(d.V(x,.624f));
                    }
                    p.Stroke();
                    break;
            }
            d.ResetStroke(); return true;
        }

        private void PaintMemeHead(AvatarDrawContext d,AvatarAccessory part)
        {
            var p=d.Painter;
            var pink=new Color32(247,155,189,255);
            var shade=new Color32(218,111,162,255);
            switch(part)
            {
                case AvatarAccessory.PinkWig:
                    p.fillColor=pink; p.BeginPath(); p.MoveTo(d.H(.245f,.57f));
                    p.BezierCurveTo(d.H(.18f,.42f),d.H(.205f,.22f),d.H(.27f,.15f));
                    p.LineTo(d.H(.224f,.13f)); p.LineTo(d.H(.31f,.092f));
                    p.BezierCurveTo(d.H(.34f,.02f),d.H(.46f,.055f),d.H(.50f,.083f));
                    p.LineTo(d.H(.543f,.037f)); p.LineTo(d.H(.575f,.085f));
                    p.BezierCurveTo(d.H(.77f,.055f),d.H(.862f,.26f),d.H(.82f,.43f));
                    p.LineTo(d.H(.86f,.467f)); p.LineTo(d.H(.78f,.462f));
                    p.BezierCurveTo(d.H(.785f,.54f),d.H(.746f,.602f),d.H(.713f,.619f));
                    p.BezierCurveTo(d.H(.729f,.50f),d.H(.718f,.353f),d.H(.663f,.281f));
                    p.LineTo(d.H(.621f,.373f)); p.BezierCurveTo(d.H(.58f,.35f),d.H(.57f,.289f),d.H(.55f,.239f));
                    p.LineTo(d.H(.50f,.366f)); p.LineTo(d.H(.463f,.272f)); p.LineTo(d.H(.424f,.345f));
                    p.BezierCurveTo(d.H(.388f,.316f),d.H(.384f,.286f),d.H(.362f,.266f));
                    p.BezierCurveTo(d.H(.31f,.36f),d.H(.313f,.487f),d.H(.34f,.59f));
                    p.LineTo(d.H(.29f,.547f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=shade; p.lineWidth=Mathf.Max(.65f,d.Size*.008f);
                    p.BeginPath(); p.MoveTo(d.H(.315f,.2f)); p.BezierCurveTo(d.H(.26f,.31f),d.H(.267f,.43f),d.H(.286f,.48f));
                    p.MoveTo(d.H(.683f,.163f)); p.BezierCurveTo(d.H(.755f,.28f),d.H(.763f,.42f),d.H(.75f,.51f)); p.Stroke();
                    break;
                case AvatarAccessory.RoseBuns:
                    p.fillColor=pink; p.BeginPath(); p.MoveTo(d.H(.28f,.39f));
                    p.BezierCurveTo(d.H(.23f,.13f),d.H(.40f,.068f),d.H(.53f,.097f));
                    p.BezierCurveTo(d.H(.72f,.063f),d.H(.84f,.17f),d.H(.775f,.43f));
                    p.LineTo(d.H(.71f,.45f)); p.LineTo(d.H(.67f,.255f));
                    p.BezierCurveTo(d.H(.63f,.31f),d.H(.595f,.35f),d.H(.54f,.365f));
                    p.LineTo(d.H(.526f,.245f)); p.LineTo(d.H(.445f,.35f)); p.LineTo(d.H(.39f,.267f));
                    p.LineTo(d.H(.35f,.45f)); p.ClosePath(); p.Fill(); p.Stroke();
                    for(int bun=0;bun<2;bun++)
                    {
                        float x=.268f+bun*.524f;
                        d.HeadEllipse(x,.205f,.091f,.087f,pink,true);
                        p.strokeColor=shade; p.lineWidth=Mathf.Max(.65f,d.Size*.008f); p.BeginPath();
                        for(int step=0;step<=23;step++)
                        {
                            float angle=step*.55f,radius=.005f+step*.0032f;
                            var point=d.H(x+Mathf.Cos(angle)*radius,.205f+Mathf.Sin(angle)*radius);
                            if(step==0)p.MoveTo(point);else p.LineTo(point);
                        }
                        p.Stroke(); d.ResetStroke();
                        p.fillColor=new Color32(159,126,210,255); p.BeginPath(); p.MoveTo(d.H(x,.30f));
                        p.BezierCurveTo(d.H(x-.08f,.31f),d.H(x-.067f,.39f),d.H(x,.326f));
                        p.BezierCurveTo(d.H(x+.067f,.39f),d.H(x+.08f,.31f),d.H(x,.30f)); p.ClosePath(); p.Fill(); p.Stroke();
                    }
                    break;
                case AvatarAccessory.SharkHood:
                    p.fillColor=new Color32(126,183,218,255); p.BeginPath(); p.MoveTo(d.H(.22f,.42f));
                    p.BezierCurveTo(d.H(.12f,.105f),d.H(.38f,.047f),d.H(.53f,.087f));
                    p.LineTo(d.H(.565f,.005f)); p.LineTo(d.H(.643f,.099f));
                    p.BezierCurveTo(d.H(.80f,.135f),d.H(.90f,.265f),d.H(.837f,.463f));
                    p.LineTo(d.H(.742f,.468f)); p.LineTo(d.H(.72f,.33f));
                    p.BezierCurveTo(d.H(.57f,.293f),d.H(.44f,.293f),d.H(.34f,.33f));
                    p.LineTo(d.H(.316f,.468f)); p.ClosePath(); p.Fill(); p.Stroke();
                    for(int tooth=0;tooth<7;tooth++)
                    {
                        float x=.33f+tooth*.056f;
                        p.fillColor=Color.white; p.BeginPath(); p.MoveTo(d.H(x,.327f));
                        p.BezierCurveTo(d.H(x+.014f,.324f),d.H(x+.024f,.326f),d.H(x+.038f,.328f));
                        p.LineTo(d.H(x+.02f,.377f)); p.ClosePath(); p.Fill();
                    }
                    p.fillColor=d.Ink; p.BeginPath(); p.MoveTo(d.H(.298f,.223f));
                    p.BezierCurveTo(d.H(.298f,.189f),d.H(.331f,.189f),d.H(.331f,.223f));
                    p.BezierCurveTo(d.H(.331f,.257f),d.H(.298f,.257f),d.H(.298f,.223f)); p.ClosePath(); p.Fill();
                    p.BeginPath(); p.MoveTo(d.H(.729f,.223f));
                    p.BezierCurveTo(d.H(.729f,.189f),d.H(.762f,.189f),d.H(.762f,.223f));
                    p.BezierCurveTo(d.H(.762f,.257f),d.H(.729f,.257f),d.H(.729f,.223f)); p.ClosePath(); p.Fill();
                    break;
                case AvatarAccessory.FrogCap:
                    p.fillColor=new Color32(140,203,111,255); p.BeginPath(); p.MoveTo(d.H(.25f,.29f));
                    p.BezierCurveTo(d.H(.22f,.11f),d.H(.38f,.095f),d.H(.53f,.117f));
                    p.BezierCurveTo(d.H(.68f,.095f),d.H(.84f,.11f),d.H(.81f,.29f));
                    p.BezierCurveTo(d.H(.72f,.35f),d.H(.34f,.35f),d.H(.25f,.29f)); p.ClosePath(); p.Fill(); p.Stroke();
                    for(int eye=0;eye<2;eye++)
                    {
                        float x=.34f+eye*.38f;
                        d.HeadEllipse(x,.13f,.07f,.069f,new Color32(140,203,111,255),true);
                        d.HeadEllipse(x,.13f,.044f,.045f,Color.white); d.HeadEllipse(x+.007f,.126f,.019f,.025f,d.Ink);
                    }
                    p.BeginPath(); p.MoveTo(d.H(.437f,.266f)); p.BezierCurveTo(d.H(.48f,.291f),d.H(.58f,.291f),d.H(.623f,.266f)); p.Stroke();
                    break;
                case AvatarAccessory.PixelCrown:
                    p.lineJoin=UnityEngine.UIElements.LineJoin.Miter;
                    p.fillColor=new Color32(122,224,221,255); p.BeginPath(); p.MoveTo(d.H(.28f,.28f));
                    p.LineTo(d.H(.28f,.132f)); p.LineTo(d.H(.346f,.132f)); p.LineTo(d.H(.346f,.20f));
                    p.LineTo(d.H(.421f,.20f)); p.LineTo(d.H(.421f,.095f)); p.LineTo(d.H(.484f,.095f));
                    p.LineTo(d.H(.484f,.035f)); p.LineTo(d.H(.576f,.035f)); p.LineTo(d.H(.576f,.095f));
                    p.LineTo(d.H(.639f,.095f)); p.LineTo(d.H(.639f,.20f)); p.LineTo(d.H(.714f,.20f));
                    p.LineTo(d.H(.714f,.132f)); p.LineTo(d.H(.78f,.132f)); p.LineTo(d.H(.78f,.28f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    p.fillColor=new Color32(247,144,191,255); p.BeginPath(); p.MoveTo(d.H(.495f,.168f));
                    p.LineTo(d.H(.565f,.168f)); p.LineTo(d.H(.565f,.239f)); p.LineTo(d.H(.495f,.239f)); p.ClosePath(); p.Fill();
                    break;
            }
            d.ResetStroke();
        }
    }
}
