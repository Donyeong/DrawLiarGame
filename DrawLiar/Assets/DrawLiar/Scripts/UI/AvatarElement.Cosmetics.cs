using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class AvatarElement
    {
        private string _cosmeticBackground = "", _cosmeticFrame = "";

        internal void SetCosmetics(string background, string frame)
        {
            background = background ?? "";
            frame = frame ?? "";
            if (_cosmeticBackground == background && _cosmeticFrame == frame) return;
            _cosmeticBackground = background;
            _cosmeticFrame = frame;
            MarkDirtyRepaint();
        }

        private void PaintCosmeticBackground(AvatarDrawContext d)
        {
            var p = d.Painter;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            switch (_cosmeticBackground)
            {
                case "background-aurora":
                    d.Box(.015f,.015f,.97f,.97f,.2f,new Color32(232,231,252,255),false);
                    BackgroundRibbon(d,.12f,new Color32(178,227,208,210));
                    BackgroundRibbon(d,.28f,new Color32(198,186,239,210));
                    BackgroundRibbon(d,.44f,new Color32(240,205,225,210));
                    d.Star(.17f,.15f,.035f,Color.white);
                    d.Star(.82f,.24f,.027f,Color.white);
                    d.Star(.16f,.67f,.023f,Color.white);
                    break;
                case "background-starlight":
                    d.Box(.015f,.015f,.97f,.97f,.2f,new Color32(219,223,248,255),false);
                    d.Ellipse(.79f,.2f,.095f,.095f,new Color32(255,245,201,255));
                    d.Ellipse(.83f,.16f,.079f,.079f,new Color32(219,223,248,255));
                    p.strokeColor = new Color32(172,171,220,255);
                    p.lineWidth = Mathf.Max(.7f,d.Size*.008f);
                    p.BeginPath(); p.MoveTo(d.V(.12f,.34f)); p.LineTo(d.V(.24f,.15f));
                    p.LineTo(d.V(.44f,.09f)); p.LineTo(d.V(.57f,.19f)); p.Stroke();
                    d.Star(.12f,.34f,.032f,Color.white);
                    d.Star(.24f,.15f,.026f,Color.white);
                    d.Star(.44f,.09f,.024f,Color.white);
                    d.Star(.88f,.61f,.033f,new Color32(255,245,201,255));
                    d.Star(.12f,.75f,.022f,Color.white);
                    break;
                case "background-petals":
                    d.Box(.015f,.015f,.97f,.97f,.2f,new Color32(255,233,238,255),false);
                    p.strokeColor = new Color32(208,164,169,255);
                    p.lineWidth = Mathf.Max(.7f,d.Size*.009f);
                    p.BeginPath(); p.MoveTo(d.V(.04f,.43f));
                    p.BezierCurveTo(d.V(.04f,.25f),d.V(.14f,.15f),d.V(.29f,.06f)); p.Stroke();
                    CosmeticFlower(d,.12f,.23f,.035f);
                    CosmeticFlower(d,.22f,.12f,.034f);
                    CosmeticFlower(d,.87f,.19f,.031f);
                    CosmeticPetal(d,.84f,.45f,.035f,new Color32(244,168,190,255));
                    CosmeticPetal(d,.16f,.66f,.026f,new Color32(255,183,204,255));
                    CosmeticPetal(d,.83f,.84f,.024f,new Color32(244,168,190,255));
                    break;
                case "background-ocean":
                    d.Box(.015f,.015f,.97f,.97f,.2f,new Color32(216,240,245,255),false);
                    d.Ellipse(.81f,.17f,.086f,.086f,new Color32(254,245,204,255));
                    BackgroundWave(d,.65f,new Color32(165,217,224,255));
                    BackgroundWave(d,.79f,new Color32(136,197,214,255));
                    p.strokeColor = new Color32(250,255,255,225);
                    p.lineWidth = Mathf.Max(.8f,d.Size*.012f);
                    p.BeginPath(); p.MoveTo(d.V(.06f,.69f));
                    p.BezierCurveTo(d.V(.17f,.63f),d.V(.26f,.64f),d.V(.32f,.68f));
                    p.MoveTo(d.V(.77f,.84f));
                    p.BezierCurveTo(d.V(.84f,.8f),d.V(.9f,.8f),d.V(.95f,.81f)); p.Stroke();
                    d.Ellipse(.12f,.3f,.018f,.018f,new Color32(255,255,255,180));
                    d.Ellipse(.88f,.48f,.013f,.013f,new Color32(255,255,255,180));
                    break;
                case "background-sunset":
                    d.Box(.015f,.015f,.97f,.97f,.2f,new Color32(255,226,210,255),false);
                    d.Ellipse(.5f,.31f,.19f,.19f,new Color32(255,191,140,255));
                    BackgroundWave(d,.62f,new Color32(241,178,180,255));
                    BackgroundWave(d,.76f,new Color32(202,164,199,255));
                    p.strokeColor = new Color32(161,130,156,255);
                    p.lineWidth = Mathf.Max(.7f,d.Size*.012f);
                    p.BeginPath(); p.MoveTo(d.V(.14f,.25f));
                    p.BezierCurveTo(d.V(.17f,.23f),d.V(.19f,.24f),d.V(.2f,.27f));
                    p.BezierCurveTo(d.V(.22f,.24f),d.V(.25f,.23f),d.V(.28f,.25f)); p.Stroke();
                    break;
            }
        }

        private static void BackgroundRibbon(AvatarDrawContext d, float y, Color color)
        {
            var p = d.Painter;
            p.fillColor = color; p.BeginPath(); p.MoveTo(d.V(.045f,y+.2f));
            p.BezierCurveTo(d.V(.28f,y-.12f),d.V(.62f,y+.23f),d.V(.955f,y+.01f));
            p.LineTo(d.V(.955f,y+.14f));
            p.BezierCurveTo(d.V(.62f,y+.37f),d.V(.27f,y+.03f),d.V(.045f,y+.34f));
            p.ClosePath(); p.Fill();
        }

        private static void BackgroundWave(AvatarDrawContext d, float y, Color color)
        {
            var p = d.Painter;
            p.fillColor = color; p.BeginPath(); p.MoveTo(d.V(.015f,y));
            p.BezierCurveTo(d.V(.28f,y-.15f),d.V(.54f,y+.12f),d.V(.985f,y-.01f));
            p.LineTo(d.V(.985f,.785f));
            p.BezierCurveTo(d.V(.985f,.93f),d.V(.93f,.985f),d.V(.785f,.985f));
            p.LineTo(d.V(.215f,.985f));
            p.BezierCurveTo(d.V(.07f,.985f),d.V(.015f,.93f),d.V(.015f,.785f));
            p.ClosePath(); p.Fill();
        }

        private static void CosmeticPetal(AvatarDrawContext d, float x, float y, float radius, Color fill)
        {
            var p = d.Painter;
            p.fillColor = fill; p.BeginPath(); p.MoveTo(d.V(x-radius,y+radius));
            p.BezierCurveTo(d.V(x-radius*1.4f,y-radius),d.V(x+radius,y-radius*1.4f),d.V(x+radius,y-radius));
            p.BezierCurveTo(d.V(x+radius*1.4f,y+radius),d.V(x-radius*.1f,y+radius*1.4f),d.V(x-radius,y+radius));
            p.ClosePath(); p.Fill();
        }

        private static void CosmeticFlower(AvatarDrawContext d, float x, float y, float radius)
        {
            for (int petal = 0; petal < 5; petal++)
            {
                float angle = petal*Mathf.PI*.4f;
                d.Ellipse(x+Mathf.Cos(angle)*radius*.7f,y+Mathf.Sin(angle)*radius*.7f,
                    radius*.53f,radius*.53f,new Color32(250,176,198,255));
            }
            d.Ellipse(x,y,radius*.33f,radius*.33f,new Color32(255,244,204,255));
        }

        private void PaintCosmeticFrame(AvatarDrawContext d)
        {
            var p = d.Painter;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            p.lineWidth = Mathf.Max(.8f,d.Size*.015f);
            switch (_cosmeticFrame)
            {
                case "frame-laurel":
                    p.strokeColor = new Color32(132,172,130,255);
                    for (int side = 0; side < 2; side++)
                    {
                        float direction = side == 0 ? 1 : -1;
                        float origin = side == 0 ? 0 : 1;
                        Vector2 V(float x,float y) => d.V(origin+x*direction,y);
                        p.BeginPath(); p.MoveTo(V(.28f,.95f));
                        p.BezierCurveTo(V(.02f,.87f),V(.005f,.39f),V(.13f,.13f)); p.Stroke();
                        for (int leaf = 0; leaf < 6; leaf++)
                        {
                            float y = .23f+leaf*.112f;
                            float x = leaf < 3 ? .061f : .049f+(leaf-2)*.023f;
                            p.fillColor = leaf%2 == 0 ? new Color32(167,205,151,255) : new Color32(204,218,155,255);
                            p.BeginPath(); p.MoveTo(V(x,y));
                            p.BezierCurveTo(V(x-.06f,y-.025f),V(x-.053f,y-.088f),V(x-.014f,y-.076f));
                            p.BezierCurveTo(V(x+.014f,y-.056f),V(x+.006f,y-.028f),V(x,y));
                            p.ClosePath(); p.Fill();
                        }
                    }
                    d.Star(.5f,.96f,.035f,new Color32(222,189,102,255));
                    break;
                case "frame-crystal":
                    CosmeticBorder(d,new Color32(162,194,226,255),.028f);
                    CosmeticBorder(d,new Color32(228,242,250,255),.05f);
                    CosmeticDiamond(d,.13f,.13f,.065f,new Color32(184,219,240,255));
                    CosmeticDiamond(d,.87f,.87f,.058f,new Color32(196,191,237,255));
                    CosmeticDiamond(d,.86f,.13f,.032f,new Color32(217,234,250,255));
                    break;
                case "frame-neon":
                    p.lineWidth = Mathf.Max(1.1f,d.Size*.03f);
                    CosmeticBorder(d,new Color32(220,173,231,160),.035f);
                    p.lineWidth = Mathf.Max(.7f,d.Size*.009f);
                    CosmeticBorder(d,new Color32(180,129,211,255),.033f);
                    CosmeticBorder(d,new Color32(132,211,218,255),.057f);
                    d.Star(.87f,.105f,.039f,new Color32(132,211,218,255));
                    break;
                case "frame-ribbon":
                    CosmeticBorder(d,new Color32(226,167,187,255),.035f);
                    d.Box(.15f,.879f,.7f,.083f,.023f,new Color32(230,168,188,255),false);
                    for (int side = 0; side < 2; side++)
                    {
                        float direction = side == 0 ? 1 : -1;
                        Vector2 V(float x,float y) => d.V(.5f+x*direction,y);
                        p.fillColor = new Color32(249,201,211,255); p.BeginPath(); p.MoveTo(V(0,.925f));
                        p.BezierCurveTo(V(-.05f,.87f),V(-.17f,.86f),V(-.18f,.885f));
                        p.LineTo(V(-.175f,.96f));
                        p.BezierCurveTo(V(-.15f,.98f),V(-.06f,.965f),V(0,.925f)); p.ClosePath(); p.Fill();
                    }
                    d.Ellipse(.5f,.925f,.026f,.036f,new Color32(211,137,164,255));
                    break;
                case "frame-stars":
                    CosmeticBorder(d,new Color32(225,207,143,255),.035f);
                    d.Star(.14f,.14f,.058f,new Color32(249,219,126,255));
                    d.Star(.87f,.19f,.032f,new Color32(212,195,237,255));
                    d.Star(.86f,.86f,.055f,new Color32(249,219,126,255));
                    d.Star(.13f,.79f,.029f,new Color32(212,195,237,255));
                    d.Ellipse(.45f,.055f,.013f,.013f,new Color32(225,207,143,255));
                    d.Ellipse(.63f,.95f,.013f,.013f,new Color32(225,207,143,255));
                    break;
            }
            d.ResetStroke();
        }

        private static void CosmeticBorder(AvatarDrawContext d, Color color, float inset)
        {
            var p = d.Painter;
            float far = 1-inset, radius = .15f;
            p.strokeColor = color; p.BeginPath(); p.MoveTo(d.V(inset+radius,inset));
            p.LineTo(d.V(far-radius,inset));
            p.BezierCurveTo(d.V(far,inset),d.V(far,inset),d.V(far,inset+radius));
            p.LineTo(d.V(far,far-radius));
            p.BezierCurveTo(d.V(far,far),d.V(far,far),d.V(far-radius,far));
            p.LineTo(d.V(inset+radius,far));
            p.BezierCurveTo(d.V(inset,far),d.V(inset,far),d.V(inset,far-radius));
            p.LineTo(d.V(inset,inset+radius));
            p.BezierCurveTo(d.V(inset,inset),d.V(inset,inset),d.V(inset+radius,inset));
            p.ClosePath(); p.Stroke();
        }

        private static void CosmeticDiamond(AvatarDrawContext d, float x, float y, float radius, Color fill)
        {
            var p = d.Painter;
            p.fillColor = fill; p.BeginPath(); p.MoveTo(d.V(x,y-radius));
            p.LineTo(d.V(x+radius*.7f,y)); p.LineTo(d.V(x,y+radius));
            p.LineTo(d.V(x-radius*.7f,y)); p.ClosePath(); p.Fill();
            p.fillColor = new Color32(250,252,255,210); p.BeginPath(); p.MoveTo(d.V(x,y-radius));
            p.LineTo(d.V(x,y+radius)); p.LineTo(d.V(x-radius*.7f,y)); p.ClosePath(); p.Fill();
        }
    }
}
