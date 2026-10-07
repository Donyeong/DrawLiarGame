using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class AvatarElement
    {
        private void PaintMemeBack(AvatarDrawContext d, AvatarAccessory part)
        {
            var p=d.Painter;
            d.ResetStroke();
            switch(part)
            {
                case AvatarAccessory.ToastBackpack:
                    p.fillColor=new Color32(209,151,88,255); p.BeginPath(); p.MoveTo(d.V(.22f,.355f));
                    p.BezierCurveTo(d.V(.11f,.285f),d.V(.035f,.438f),d.V(.128f,.53f));
                    p.LineTo(d.V(.128f,.805f)); p.BezierCurveTo(d.V(.126f,.866f),d.V(.178f,.899f),d.V(.251f,.891f));
                    p.LineTo(d.V(.809f,.891f)); p.BezierCurveTo(d.V(.882f,.899f),d.V(.934f,.866f),d.V(.932f,.805f));
                    p.LineTo(d.V(.932f,.53f)); p.BezierCurveTo(d.V(.985f,.438f),d.V(.949f,.285f),d.V(.84f,.355f));
                    p.BezierCurveTo(d.V(.71f,.29f),d.V(.65f,.322f),d.V(.53f,.318f));
                    p.BezierCurveTo(d.V(.41f,.322f),d.V(.35f,.29f),d.V(.22f,.355f)); p.ClosePath(); p.Fill(); p.Stroke();
                    d.Box(.158f,.40f,.744f,.45f,.065f,new Color32(255,222,160,255),false);
                    d.Ellipse(.144f,.577f,.012f,.021f,new Color32(177,115,69,255));
                    d.Ellipse(.154f,.731f,.009f,.016f,new Color32(177,115,69,255));
                    d.Ellipse(.917f,.632f,.012f,.018f,new Color32(177,115,69,255));
                    break;
                case AvatarAccessory.SharkTail:
                    p.fillColor=new Color32(139,187,215,255); p.BeginPath(); p.MoveTo(d.V(.73f,.72f));
                    p.BezierCurveTo(d.V(.819f,.66f),d.V(.833f,.565f),d.V(.937f,.416f));
                    p.BezierCurveTo(d.V(.957f,.523f),d.V(.925f,.611f),d.V(.907f,.67f));
                    p.LineTo(d.V(.968f,.754f)); p.BezierCurveTo(d.V(.953f,.839f),d.V(.882f,.865f),d.V(.831f,.86f));
                    p.BezierCurveTo(d.V(.873f,.827f),d.V(.901f,.803f),d.V(.902f,.775f));
                    p.LineTo(d.V(.742f,.815f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=new Color32(208,231,239,255); p.lineWidth=Mathf.Max(.65f,d.Size*.016f);
                    p.BeginPath(); p.MoveTo(d.V(.895f,.732f)); p.QuadraticCurveTo(d.V(.89f,.65f),d.V(.93f,.518f)); p.Stroke();
                    break;
                case AvatarAccessory.SpeechSign:
                    d.Box(.826f,.253f,.026f,.64f,.009f,new Color32(197,152,105,255));
                    p.fillColor=d.Cream; p.BeginPath(); p.MoveTo(d.V(.81f,.245f));
                    p.LineTo(d.V(.813f,.318f)); p.LineTo(d.V(.879f,.245f)); p.ClosePath(); p.Fill(); p.Stroke();
                    d.Box(.699f,.078f,.266f,.188f,.032f,d.Cream);
                    for(int dot=0;dot<3;dot++)d.Ellipse(.765f+dot*.066f,.169f,.014f,.017f,d.Ink);
                    break;
                case AvatarAccessory.PixelWings:
                    p.lineJoin=LineJoin.Miter;
                    for(int side=0;side<2;side++)
                    {
                        float center=side==0?0:1.06f,direction=side==0?1:-1;
                        Vector2 W(float x,float y)=>d.V(center+x*direction,y);
                        p.fillColor=new Color32(171,213,197,255); p.BeginPath(); p.MoveTo(W(.105f,.376f));
                        p.LineTo(W(.16f,.376f)); p.LineTo(W(.16f,.435f)); p.LineTo(W(.215f,.435f));
                        p.LineTo(W(.215f,.494f)); p.LineTo(W(.27f,.494f)); p.LineTo(W(.27f,.553f));
                        p.LineTo(W(.325f,.553f)); p.LineTo(W(.325f,.715f)); p.LineTo(W(.27f,.715f));
                        p.LineTo(W(.27f,.659f)); p.LineTo(W(.215f,.659f)); p.LineTo(W(.215f,.603f));
                        p.LineTo(W(.16f,.603f)); p.LineTo(W(.16f,.547f)); p.LineTo(W(.105f,.547f));
                        p.ClosePath(); p.Fill(); p.Stroke();
                        p.strokeColor=d.Cream; p.lineWidth=Mathf.Max(.65f,d.Size*.012f);
                        p.BeginPath(); p.MoveTo(W(.132f,.424f)); p.LineTo(W(.132f,.505f));
                        p.LineTo(W(.186f,.505f)); p.LineTo(W(.186f,.564f)); p.Stroke();
                        d.ResetStroke(); p.lineJoin=LineJoin.Miter;
                    }
                    break;
                case AvatarAccessory.CozyBlanket:
                    p.fillColor=new Color32(154,197,185,255); p.BeginPath(); p.MoveTo(d.V(.28f,.352f));
                    p.BezierCurveTo(d.V(.169f,.479f),d.V(.101f,.756f),d.V(.078f,.899f));
                    p.BezierCurveTo(d.V(.154f,.951f),d.V(.218f,.936f),d.V(.305f,.914f));
                    p.BezierCurveTo(d.V(.444f,.895f),d.V(.638f,.895f),d.V(.777f,.914f));
                    p.BezierCurveTo(d.V(.862f,.936f),d.V(.922f,.951f),d.V(.969f,.899f));
                    p.BezierCurveTo(d.V(.944f,.756f),d.V(.889f,.479f),d.V(.78f,.352f));
                    p.QuadraticCurveTo(d.V(.53f,.423f),d.V(.28f,.352f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=new Color32(226,239,219,255); p.lineWidth=Mathf.Max(.65f,d.Size*.012f);
                    p.BeginPath(); p.MoveTo(d.V(.123f,.664f)); p.LineTo(d.V(.156f,.76f)); p.LineTo(d.V(.115f,.847f));
                    p.MoveTo(d.V(.922f,.664f)); p.LineTo(d.V(.894f,.76f)); p.LineTo(d.V(.934f,.847f)); p.Stroke();
                    for(int fringe=0;fringe<3;fringe++)
                    {
                        float x=.115f+fringe*.042f;
                        p.BeginPath(); p.MoveTo(d.V(x,.903f)); p.LineTo(d.V(x-.009f,.932f));
                        p.MoveTo(d.V(1.06f-x,.903f)); p.LineTo(d.V(1.069f-x,.932f)); p.Stroke();
                    }
                    break;
            }
            d.ResetStroke();
        }

        private void PaintMemeFace(AvatarDrawContext d, AvatarAccessory part)
        {
            var p=d.Painter;
            d.ResetStroke();
            switch(part)
            {
                case AvatarAccessory.GoggleEyes:
                    for(int eye=0;eye<2;eye++)
                    {
                        float x=.43f+eye*.2f;
                        d.Ellipse(x,.48f,.077f,.072f,new Color32(195,219,220,255),true);
                        d.Ellipse(x,.48f,.061f,.057f,d.Cream);
                        d.Ellipse(x+(eye==0?-.012f:.012f),.481f,.029f,.037f,d.Ink);
                        d.Ellipse(x+(eye==0?-.022f:.002f),.466f,.009f,.012f,Color.white);
                    }
                    p.BeginPath(); p.MoveTo(d.V(.507f,.474f)); p.QuadraticCurveTo(d.V(.53f,.453f),d.V(.553f,.474f));
                    p.MoveTo(d.V(.353f,.46f)); p.LineTo(d.V(.315f,.446f));
                    p.MoveTo(d.V(.707f,.46f)); p.LineTo(d.V(.745f,.446f)); p.Stroke();
                    break;
                case AvatarAccessory.SwirlGlasses:
                    for(int eye=0;eye<2;eye++)
                    {
                        float x=.43f+eye*.2f;
                        d.Ellipse(x,.48f,.076f,.065f,new Color32(235,188,107,255),true);
                        d.Ellipse(x,.48f,.062f,.052f,d.Cream);
                        p.lineWidth=Mathf.Max(.65f,d.Size*.008f); p.BeginPath();
                        for(int point=0;point<=32;point++)
                        {
                            float angle=point*Mathf.PI/8,radius=.003f+point*.00142f;
                            var position=d.V(x+Mathf.Cos(angle)*radius,.48f+Mathf.Sin(angle)*radius*.87f);
                            if(point==0)p.MoveTo(position);else p.LineTo(position);
                        }
                        p.Stroke(); d.ResetStroke();
                    }
                    p.BeginPath(); p.MoveTo(d.V(.506f,.467f)); p.LineTo(d.V(.554f,.467f));
                    p.MoveTo(d.V(.354f,.465f)); p.LineTo(d.V(.314f,.451f));
                    p.MoveTo(d.V(.706f,.465f)); p.LineTo(d.V(.746f,.451f)); p.Stroke();
                    break;
                case AvatarAccessory.CensorBar:
                    d.Box(.301f,.418f,.458f,.121f,.014f,new Color32(32,28,40,255));
                    p.strokeColor=new Color32(102,96,114,255); p.lineWidth=Mathf.Max(.65f,d.Size*.01f);
                    p.BeginPath(); p.MoveTo(d.V(.332f,.442f)); p.LineTo(d.V(.451f,.442f)); p.Stroke();
                    break;
                case AvatarAccessory.SweatSticker:
                    p.fillColor=new Color32(129,195,226,255); p.BeginPath(); p.MoveTo(d.V(.765f,.333f));
                    p.BezierCurveTo(d.V(.775f,.378f),d.V(.821f,.42f),d.V(.815f,.467f));
                    p.BezierCurveTo(d.V(.809f,.518f),d.V(.73f,.517f),d.V(.725f,.467f));
                    p.BezierCurveTo(d.V(.721f,.42f),d.V(.753f,.376f),d.V(.765f,.333f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=d.Cream; p.lineWidth=Mathf.Max(.65f,d.Size*.013f);
                    p.BeginPath(); p.MoveTo(d.V(.748f,.429f)); p.QuadraticCurveTo(d.V(.732f,.475f),d.V(.754f,.487f)); p.Stroke();
                    break;
                case AvatarAccessory.MemeMoustache:
                    p.fillColor=d.Ink; p.BeginPath(); p.MoveTo(d.V(.53f,.561f));
                    p.BezierCurveTo(d.V(.501f,.537f),d.V(.463f,.554f),d.V(.424f,.551f));
                    p.BezierCurveTo(d.V(.39f,.547f),d.V(.382f,.522f),d.V(.397f,.51f));
                    p.BezierCurveTo(d.V(.343f,.52f),d.V(.363f,.576f),d.V(.417f,.589f));
                    p.BezierCurveTo(d.V(.468f,.602f),d.V(.504f,.591f),d.V(.53f,.573f));
                    p.BezierCurveTo(d.V(.556f,.591f),d.V(.592f,.602f),d.V(.643f,.589f));
                    p.BezierCurveTo(d.V(.697f,.576f),d.V(.717f,.52f),d.V(.663f,.51f));
                    p.BezierCurveTo(d.V(.678f,.522f),d.V(.67f,.547f),d.V(.636f,.551f));
                    p.BezierCurveTo(d.V(.597f,.554f),d.V(.559f,.537f),d.V(.53f,.561f)); p.ClosePath(); p.Fill();
                    break;
            }
            d.ResetStroke();
        }

        private void PaintMemeNeck(AvatarDrawContext d, AvatarAccessory part)
        {
            var p=d.Painter;
            d.ResetStroke();
            switch(part)
            {
                case AvatarAccessory.BellCollar:
                    p.lineWidth=d.Size*.052f; p.BeginPath(); p.MoveTo(d.V(.338f,.747f));
                    p.QuadraticCurveTo(d.V(.53f,.824f),d.V(.722f,.747f)); p.Stroke();
                    p.strokeColor=new Color32(228,148,168,255); p.lineWidth=d.Size*.031f; p.Stroke(); d.ResetStroke();
                    d.Ellipse(.53f,.798f,.022f,.026f,new Color32(239,189,98,255),true);
                    d.Ellipse(.53f,.831f,.051f,.05f,new Color32(251,211,123,255),true);
                    d.Ellipse(.53f,.842f,.009f,.011f,d.Ink);
                    p.BeginPath(); p.MoveTo(d.V(.53f,.849f)); p.LineTo(d.V(.53f,.873f)); p.Stroke();
                    d.Ellipse(.512f,.814f,.009f,.012f,d.Cream);
                    break;
                case AvatarAccessory.GiantBow:
                    p.fillColor=new Color32(202,117,153,255); p.BeginPath(); p.MoveTo(d.V(.49f,.81f));
                    p.LineTo(d.V(.41f,.899f)); p.LineTo(d.V(.445f,.898f)); p.LineTo(d.V(.467f,.92f));
                    p.LineTo(d.V(.532f,.823f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.BeginPath(); p.MoveTo(d.V(.57f,.81f)); p.LineTo(d.V(.65f,.899f));
                    p.LineTo(d.V(.615f,.898f)); p.LineTo(d.V(.593f,.92f)); p.LineTo(d.V(.528f,.823f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.fillColor=new Color32(244,165,194,255); p.BeginPath(); p.MoveTo(d.V(.508f,.776f));
                    p.LineTo(d.V(.346f,.699f)); p.BezierCurveTo(d.V(.268f,.744f),d.V(.277f,.829f),d.V(.355f,.847f));
                    p.LineTo(d.V(.508f,.809f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.BeginPath(); p.MoveTo(d.V(.552f,.776f)); p.LineTo(d.V(.714f,.699f));
                    p.BezierCurveTo(d.V(.792f,.744f),d.V(.783f,.829f),d.V(.705f,.847f));
                    p.LineTo(d.V(.552f,.809f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=new Color32(202,117,153,255); p.lineWidth=Mathf.Max(.65f,d.Size*.009f);
                    p.BeginPath(); p.MoveTo(d.V(.371f,.76f)); p.LineTo(d.V(.492f,.793f));
                    p.MoveTo(d.V(.689f,.76f)); p.LineTo(d.V(.568f,.793f)); p.Stroke(); d.ResetStroke();
                    d.Ellipse(.53f,.796f,.036f,.039f,new Color32(251,211,123,255),true);
                    break;
                case AvatarAccessory.NoodleScarf:
                    d.Box(.311f,.738f,.438f,.083f,.031f,new Color32(248,216,136,255));
                    p.strokeColor=new Color32(180,137,81,255); p.lineWidth=Mathf.Max(.65f,d.Size*.006f);
                    for(int noodle=0;noodle<3;noodle++)
                    {
                        float y=.755f+noodle*.021f;
                        p.BeginPath(); p.MoveTo(d.V(.342f,y)); p.BezierCurveTo(d.V(.421f,y+.019f),d.V(.627f,y+.016f),d.V(.718f,y)); p.Stroke();
                    }
                    p.strokeColor=new Color32(248,216,136,255); p.lineWidth=Mathf.Max(.65f,d.Size*.014f);
                    for(int noodle=0;noodle<3;noodle++)
                    {
                        float x=.677f+noodle*.028f;
                        p.BeginPath(); p.MoveTo(d.V(x,.799f)); p.BezierCurveTo(d.V(x-.014f,.838f),d.V(x+.024f,.881f),d.V(x-.007f,.903f)); p.Stroke();
                    }
                    break;
                case AvatarAccessory.ChunkyChain:
                    p.lineWidth=Mathf.Max(.65f,d.Size*.007f);
                    for(int link=0;link<13;link++)
                    {
                        float x=.35f+link*.03f,t=(x-.53f)/.18f,y=.749f+.06f*(1-t*t);
                        d.Ellipse(x,y,.019f,.025f,new Color32(244,201,104,255),true);
                        d.Ellipse(x,y,.006f,.012f,d.Body);
                    }
                    d.ResetStroke(); d.Box(.492f,.821f,.076f,.074f,.015f,new Color32(251,211,123,255));
                    d.Star(.53f,.858f,.021f,d.Cream);
                    break;
                case AvatarAccessory.CameraStrap:
                    p.lineWidth=d.Size*.037f; p.BeginPath(); p.MoveTo(d.V(.366f,.735f)); p.LineTo(d.V(.457f,.847f));
                    p.MoveTo(d.V(.694f,.735f)); p.LineTo(d.V(.603f,.847f)); p.Stroke();
                    p.strokeColor=new Color32(160,149,192,255); p.lineWidth=d.Size*.021f; p.Stroke(); d.ResetStroke();
                    d.Box(.459f,.785f,.048f,.035f,.009f,d.Ink,false);
                    d.Box(.437f,.811f,.186f,.118f,.021f,new Color32(149,192,207,255));
                    d.Ellipse(.53f,.868f,.039f,.038f,d.Ink,true);
                    d.Ellipse(.53f,.868f,.025f,.024f,new Color32(98,145,170,255));
                    d.Ellipse(.52f,.858f,.009f,.009f,d.Cream);
                    d.Box(.576f,.833f,.025f,.017f,.004f,d.Cream,false);
                    break;
            }
            d.ResetStroke();
        }

        private void PaintMemeHand(AvatarDrawContext d, AvatarAccessory part)
        {
            var p=d.Painter;
            d.ResetStroke();
            switch(part)
            {
                case AvatarAccessory.FishPlush:
                    p.fillColor=new Color32(153,199,209,255); p.BeginPath(); p.MoveTo(d.V(.117f,.651f));
                    p.LineTo(d.V(.05f,.603f)); p.BezierCurveTo(d.V(.0393333f,.635f),d.V(.0393333f,.668f),d.V(.05f,.702f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    p.BeginPath(); p.MoveTo(d.V(.17f,.604f)); p.LineTo(d.V(.209f,.54f));
                    p.LineTo(d.V(.246f,.605f)); p.ClosePath(); p.Fill(); p.Stroke();
                    d.Ellipse(.207f,.655f,.132f,.074f,new Color32(173,211,218,255),true);
                    d.Ellipse(.28f,.638f,.013f,.015f,d.Ink); d.Ellipse(.277f,.634f,.004f,.005f,d.Cream);
                    p.BeginPath(); p.MoveTo(d.V(.255f,.649f)); p.QuadraticCurveTo(d.V(.242f,.67f),d.V(.255f,.686f));
                    p.MoveTo(d.V(.323f,.66f)); p.LineTo(d.V(.333f,.666f)); p.Stroke();
                    d.Ellipse(.243f,.714f,.045f,.043f,d.Body,true);
                    break;
                case AvatarAccessory.SqueakyHammer:
                    d.Box(.197f,.513f,.033f,.296f,.012f,new Color32(244,199,105,255));
                    d.Box(.082f,.406f,.26f,.14f,.034f,new Color32(242,166,173,255));
                    d.Ellipse(.092f,.476f,.029f,.061f,new Color32(224,142,154,255),true);
                    d.Ellipse(.332f,.476f,.029f,.061f,new Color32(224,142,154,255),true);
                    p.strokeColor=new Color32(224,142,154,255); p.lineWidth=Mathf.Max(.65f,d.Size*.01f);
                    p.BeginPath(); p.MoveTo(d.V(.168f,.428f)); p.LineTo(d.V(.168f,.521f));
                    p.MoveTo(d.V(.255f,.428f)); p.LineTo(d.V(.255f,.521f)); p.Stroke(); d.ResetStroke();
                    d.Ellipse(.219f,.708f,.043f,.047f,d.Body,true);
                    break;
                case AvatarAccessory.TeaCup:
                    p.lineWidth=d.Size*.027f; p.BeginPath(); p.MoveTo(d.V(.139f,.634f));
                    p.BezierCurveTo(d.V(.055f,.611f),d.V(.046f,.699f),d.V(.148f,.702f)); p.Stroke();
                    p.strokeColor=d.Cream; p.lineWidth=d.Size*.013f; p.Stroke(); d.ResetStroke();
                    d.Ellipse(.217f,.76f,.114f,.02f,d.Cream,true);
                    p.fillColor=d.Cream; p.BeginPath(); p.MoveTo(d.V(.124f,.616f)); p.LineTo(d.V(.307f,.616f));
                    p.BezierCurveTo(d.V(.307f,.751f),d.V(.134f,.771f),d.V(.124f,.616f)); p.ClosePath(); p.Fill(); p.Stroke();
                    d.Ellipse(.215f,.617f,.091f,.022f,new Color32(186,137,89,255),true);
                    p.strokeColor=new Color32(120,111,132,180); p.lineWidth=Mathf.Max(.65f,d.Size*.007f);
                    p.BeginPath(); p.MoveTo(d.V(.173f,.574f)); p.QuadraticCurveTo(d.V(.149f,.547f),d.V(.173f,.521f));
                    p.QuadraticCurveTo(d.V(.197f,.493f),d.V(.173f,.465f));
                    p.MoveTo(d.V(.244f,.568f)); p.QuadraticCurveTo(d.V(.219f,.54f),d.V(.244f,.514f)); p.Stroke(); d.ResetStroke();
                    d.Ellipse(.285f,.715f,.043f,.043f,d.Body,true);
                    break;
                case AvatarAccessory.Banana:
                    p.fillColor=new Color32(249,217,100,255); p.BeginPath(); p.MoveTo(d.V(.086f,.488f));
                    p.BezierCurveTo(d.V(.064f,.658f),d.V(.126f,.8f),d.V(.299f,.766f));
                    p.BezierCurveTo(d.V(.172f,.728f),d.V(.111f,.604f),d.V(.119f,.5f));
                    p.LineTo(d.V(.109f,.468f)); p.ClosePath(); p.Fill(); p.Stroke();
                    p.fillColor=new Color32(151,115,76,255); p.BeginPath(); p.MoveTo(d.V(.094f,.475f));
                    p.LineTo(d.V(.101f,.443f)); p.LineTo(d.V(.126f,.449f)); p.LineTo(d.V(.119f,.481f));
                    p.ClosePath(); p.Fill(); p.Stroke();
                    p.strokeColor=d.Cream; p.lineWidth=Mathf.Max(.65f,d.Size*.012f);
                    p.BeginPath(); p.MoveTo(d.V(.099f,.522f)); p.BezierCurveTo(d.V(.09f,.642f),d.V(.137f,.728f),d.V(.248f,.754f)); p.Stroke(); d.ResetStroke();
                    d.Ellipse(.234f,.728f,.043f,.043f,d.Body,true);
                    break;
                case AvatarAccessory.TinyKeyboard:
                    d.Box(.053f,.598f,.283f,.194f,.023f,new Color32(157,147,186,255));
                    d.Box(.069f,.614f,.25f,.156f,.012f,new Color32(230,224,232,255),false);
                    for(int row=0;row<3;row++)
                        for(int key=0;key<5;key++)
                            d.Box(.083f+key*.045f,.626f+row*.034f,.034f,.022f,.004f,new Color32(102,91,122,255),false);
                    d.Box(.126f,.735f,.13f,.021f,.004f,new Color32(102,91,122,255),false);
                    d.Ellipse(.278f,.782f,.043f,.043f,d.Body,true);
                    break;
            }
            d.ResetStroke();
        }
    }
}
