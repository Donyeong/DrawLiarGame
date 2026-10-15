using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    internal static class DrawCosmeticPresentation
    {
        private static readonly ConditionalWeakTable<Label,NicknameBinding> NICKNAMES = new ConditionalWeakTable<Label,NicknameBinding>();
        private static readonly ConditionalWeakTable<VisualElement,BubbleBinding> BUBBLES = new ConditionalWeakTable<VisualElement,BubbleBinding>();
        private static readonly Color32[] PRISM_COLORS = { new Color32(236,159,184,255), new Color32(239,207,127,255),
            new Color32(131,196,180,255), new Color32(142,183,224,255), new Color32(184,158,222,255) };
        private const float NICKNAME_PLATE_PADDING = 4, NICKNAME_PLATE_RADIUS = 7;
        private static readonly Dictionary<string,(Gradient Fill,Color Border)> NICKNAME_PLATES = new Dictionary<string,(Gradient,Color)>();

        public static void ApplyNickname(Label label, CosmeticLoadout loadout)
        {
            if (label == null) return;
            string effect = KnownNickname(loadout?.NicknameEffect);
            label.enableRichText = false;
            label.parseEscapeSequences = false;
            if (effect.Length == 0 && !NICKNAMES.TryGetValue(label,out _)) return;
            var binding = NICKNAMES.GetValue(label,element => new NicknameBinding(element));
            if (binding.Effect == effect) return;
            binding.Effect = effect;
            label.EnableInClassList("cosmetic-nickname",effect.Length > 0);
            if (effect.Length == 0)
            {
                label.style.color = binding.Color;
                label.style.unityTextOutlineColor = binding.OutlineColor;
                label.style.unityTextOutlineWidth = binding.OutlineWidth;
                label.style.paddingLeft = binding.PaddingLeft;
                label.style.paddingRight = binding.PaddingRight;
            }
            else
            {
                label.style.color = NicknameColor(effect);
                label.style.unityTextOutlineColor = (Color)(effect == "nickname-neon"
                    ? new Color32(126,226,234,255) : new Color32(255,250,241,255));
                label.style.unityTextOutlineWidth = effect == "nickname-neon" ? .35f : .2f;
                label.style.paddingLeft = label.style.paddingRight = NICKNAME_PLATE_PADDING;
            }
            label.MarkDirtyRepaint();
        }

        public static void ApplyAvatar(AvatarElement avatar, CosmeticLoadout loadout)
        {
            avatar?.SetCosmetics(loadout?.AvatarBackground,loadout?.AvatarFrame);
        }

        public static void ApplyChatBubble(VisualElement element, CosmeticLoadout loadout)
        {
            if (element == null) return;
            string effect = KnownBubble(loadout?.ChatBubble);
            if (effect.Length == 0 && !BUBBLES.TryGetValue(element,out _)) return;
            var binding = BUBBLES.GetValue(element,bubble => new BubbleBinding(bubble));
            if (binding.Effect == effect)
            {
                if (effect.Length > 0) binding.ApplyTextColor();
                return;
            }
            binding.Effect = effect;
            element.EnableInClassList("cosmetic-chat-bubble",effect.Length > 0);
            if (effect.Length == 0) binding.Restore();
            else
            {
                Color fill, border;
                float radius = 12, width = 1;
                switch (effect)
                {
                    case "bubble-cloud":
                        fill = new Color32(242,250,255,255); border = new Color32(187,216,232,255); radius = 17;
                        break;
                    case "bubble-memo":
                        fill = new Color32(255,248,220,255); border = new Color32(222,201,150,255); radius = 5;
                        break;
                    case "bubble-neon":
                        fill = new Color32(243,235,254,255); border = new Color32(183,151,215,255); width = 1.5f;
                        break;
                    case "bubble-pixel":
                        fill = new Color32(230,246,234,255); border = new Color32(133,181,157,255); radius = 2; width = 2;
                        break;
                    default:
                        fill = new Color32(255,238,242,255); border = new Color32(230,183,200,255); radius = 14;
                        break;
                }
                var style = element.style;
                style.backgroundColor = fill;
                style.color = (Color)new Color32(64,53,83,255);
                style.borderTopColor = style.borderRightColor = style.borderBottomColor = style.borderLeftColor = border;
                style.borderTopWidth = style.borderRightWidth = style.borderBottomWidth = style.borderLeftWidth = width;
                style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomRightRadius = radius;
                style.borderBottomLeftRadius = effect == "bubble-pixel" ? 2 : 5;
                style.paddingTop = style.paddingBottom = 7;
                style.paddingLeft = 11;
                style.paddingRight = 17;
                if (element is Label) style.height = StyleKeyword.Auto;
                binding.ApplyTailColors();
                binding.ApplyTextColor();
            }
            element.MarkDirtyRepaint();
        }

        private static string KnownNickname(string effect)
        {
            switch (effect)
            {
                case "nickname-prism": case "nickname-gold": case "nickname-mint":
                case "nickname-neon": case "nickname-sunset": return effect;
                default: return "";
            }
        }

        private static string KnownBubble(string effect)
        {
            switch (effect)
            {
                case "bubble-cloud": case "bubble-memo": case "bubble-neon":
                case "bubble-pixel": case "bubble-petal": return effect;
                default: return "";
            }
        }

        private static Color NicknameColor(string effect)
        {
            switch (effect)
            {
                case "nickname-prism": return new Color32(123,100,169,255);
                case "nickname-gold": return new Color32(151,111,37,255);
                case "nickname-mint": return new Color32(53,133,115,255);
                case "nickname-neon": return new Color32(255,226,255,255);
                default: return new Color32(190,106,104,255);
            }
        }

        // 효과별 닉네임 배경판의 가로 그라데이션과 테두리 색. 네온만 어두운 판 위에 밝은 글자를 쓴다.
        private static (Gradient Fill,Color Border) NicknamePlate(string effect)
        {
            if (NICKNAME_PLATES.TryGetValue(effect,out var plate)) return plate;
            Color32[] stops; Color32 border;
            switch (effect)
            {
                case "nickname-prism":
                    stops = new[] { new Color32(251,226,235,255), new Color32(252,241,214,255), new Color32(224,244,236,255),
                        new Color32(225,236,251,255), new Color32(237,228,249,255) };
                    border = new Color32(214,197,237,255);
                    break;
                case "nickname-gold":
                    stops = new[] { new Color32(253,245,221,255), new Color32(247,229,178,255), new Color32(253,245,221,255) };
                    border = new Color32(224,193,116,255);
                    break;
                case "nickname-mint":
                    stops = new[] { new Color32(233,248,241,255), new Color32(213,239,228,255) };
                    border = new Color32(154,207,185,255);
                    break;
                case "nickname-neon":
                    stops = new[] { new Color32(48,32,74,255), new Color32(31,42,78,255) };
                    border = new Color32(126,226,234,255);
                    break;
                default:
                    stops = new[] { new Color32(252,227,232,255), new Color32(253,233,212,255), new Color32(238,228,246,255) };
                    border = new Color32(233,181,171,255);
                    break;
            }
            var keys = new GradientColorKey[stops.Length];
            for (int index = 0; index < stops.Length; index++) keys[index] = new GradientColorKey(stops[index],index/(float)(stops.Length-1));
            var fill = new Gradient();
            fill.SetKeys(keys,new[] { new GradientAlphaKey(1,0), new GradientAlphaKey(1,1) });
            plate = (fill,border);
            NICKNAME_PLATES.Add(effect,plate);
            return plate;
        }

        // 라벨이 줄을 채우도록 늘어나도 배경판과 밑줄은 실제 글자 영역에만 그린다. 줄바꿈되는 이름은 모든 줄을 덮는다.
        private static bool TryNicknameTextRect(Label label, out Rect rect, out float lineHeight)
        {
            rect = default; lineHeight = 0;
            var content = label.contentRect;
            if (label.panel == null || content.width < 4 || content.height < 6) return false;
            string text = label.text ?? "";
            var line = label.MeasureTextSize(text,0,VisualElement.MeasureMode.Undefined,0,VisualElement.MeasureMode.Undefined);
            var size = label.MeasureTextSize(text,content.width,VisualElement.MeasureMode.AtMost,0,VisualElement.MeasureMode.Undefined);
            float width = float.IsNaN(size.x) || size.x <= 0 ? content.width : Mathf.Min(size.x,content.width);
            float height = float.IsNaN(size.y) || size.y <= 0 ? content.height : Mathf.Min(size.y,content.height);
            lineHeight = float.IsNaN(line.y) || line.y <= 0 ? height : Mathf.Min(line.y,height);
            int align = (int)label.resolvedStyle.unityTextAlign;
            rect = new Rect(content.xMin+(content.width-width)*(align%3)*.5f,content.yMin+(content.height-height)*(align/3)*.5f,width,height);
            return true;
        }

        private static void PaintNicknamePlate(MeshGenerationContext context, Label label, string effect)
        {
            if (effect.Length == 0 || !TryNicknameTextRect(label,out var text,out float lineHeight)) return;
            var plate = NicknamePlate(effect);
            var outer = new Rect(text.xMin-NICKNAME_PLATE_PADDING,text.yMin,text.width+NICKNAME_PLATE_PADDING*2,text.height);
            float radius = Mathf.Min(NICKNAME_PLATE_RADIUS,lineHeight*.5f);
            var p = context.painter2D;
            p.fillColor = plate.Border;
            RoundedRect(p,outer,radius); p.Fill();
            var inner = new Rect(outer.xMin+1,outer.yMin+1,outer.width-2,outer.height-2);
            p.fillGradient = FillGradient.MakeLinearGradient(plate.Fill,new Vector2(inner.xMin,inner.center.y),new Vector2(inner.xMax,inner.center.y),AddressMode.Clamp);
            RoundedRect(p,inner,Mathf.Max(0,radius-1)); p.Fill();
            p.fillGradient = default;
        }

        private static void RoundedRect(Painter2D painter, Rect rect, float radius)
        {
            const float k = .5522848f;
            float x0 = rect.xMin, x1 = rect.xMax, y0 = rect.yMin, y1 = rect.yMax, c = radius*(1-k);
            painter.BeginPath(); painter.MoveTo(new Vector2(x0+radius,y0)); painter.LineTo(new Vector2(x1-radius,y0));
            painter.BezierCurveTo(new Vector2(x1-c,y0),new Vector2(x1,y0+c),new Vector2(x1,y0+radius)); painter.LineTo(new Vector2(x1,y1-radius));
            painter.BezierCurveTo(new Vector2(x1,y1-c),new Vector2(x1-c,y1),new Vector2(x1-radius,y1)); painter.LineTo(new Vector2(x0+radius,y1));
            painter.BezierCurveTo(new Vector2(x0+c,y1),new Vector2(x0,y1-c),new Vector2(x0,y1-radius)); painter.LineTo(new Vector2(x0,y0+radius));
            painter.BezierCurveTo(new Vector2(x0,y0+c),new Vector2(x0+c,y0),new Vector2(x0+radius,y0)); painter.ClosePath();
        }

        private sealed class NicknameBinding
        {
            public string Effect = "";
            public readonly StyleColor Color, OutlineColor;
            public readonly StyleFloat OutlineWidth;
            public readonly StyleLength PaddingLeft, PaddingRight;

            public NicknameBinding(Label label)
            {
                Color = label.style.color;
                OutlineColor = label.style.unityTextOutlineColor;
                OutlineWidth = label.style.unityTextOutlineWidth;
                PaddingLeft = label.style.paddingLeft;
                PaddingRight = label.style.paddingRight;
                // 배경판은 글자보다 먼저 그려야 글자를 가리지 않는다.
                label.generateVisualContent = (Action<MeshGenerationContext>)(context => PaintNicknamePlate(context,label,Effect)) + label.generateVisualContent;
                label.generateVisualContent += context => PaintNickname(context,label,Effect);
            }
        }

        private sealed class BubbleBinding
        {
            private readonly VisualElement _element;
            private readonly StyleColor _background, _color, _topColor, _rightColor, _bottomColor, _leftColor;
            private readonly StyleFloat _topWidth, _rightWidth, _bottomWidth, _leftWidth;
            private readonly StyleLength _topLeftRadius, _topRightRadius, _bottomRightRadius, _bottomLeftRadius;
            private readonly StyleLength _topPadding, _rightPadding, _bottomPadding, _leftPadding;
            private readonly StyleLength _height;
            private readonly Dictionary<Label,StyleColor> _textColors = new Dictionary<Label,StyleColor>();
            private readonly List<Label> _removedLabels = new List<Label>();
            private readonly Action<Label> _applyLabelColor;
            private VisualElement _tail;
            public string Effect = "";

            public BubbleBinding(VisualElement element)
            {
                _element = element;
                var style = element.style;
                _background = style.backgroundColor; _color = style.color;
                _topColor = style.borderTopColor; _rightColor = style.borderRightColor;
                _bottomColor = style.borderBottomColor; _leftColor = style.borderLeftColor;
                _topWidth = style.borderTopWidth; _rightWidth = style.borderRightWidth;
                _bottomWidth = style.borderBottomWidth; _leftWidth = style.borderLeftWidth;
                _topLeftRadius = style.borderTopLeftRadius; _topRightRadius = style.borderTopRightRadius;
                _bottomRightRadius = style.borderBottomRightRadius; _bottomLeftRadius = style.borderBottomLeftRadius;
                _topPadding = style.paddingTop; _rightPadding = style.paddingRight;
                _bottomPadding = style.paddingBottom; _leftPadding = style.paddingLeft;
                _height = style.height;
                _applyLabelColor = ApplyLabelColor;
                element.generateVisualContent += context => PaintBubble(context,element,Effect);
            }

            public void ApplyTextColor()
            {
                _removedLabels.Clear();
                foreach (var pair in _textColors)
                {
                    if (_element.Contains(pair.Key)) continue;
                    pair.Key.style.color = pair.Value;
                    _removedLabels.Add(pair.Key);
                }
                foreach (var label in _removedLabels) _textColors.Remove(label);
                _removedLabels.Clear();
                _element.Query<Label>().ForEach(_applyLabelColor);
            }

            private void ApplyLabelColor(Label label)
            {
                if (ReferenceEquals(label,_element) || _textColors.ContainsKey(label)) return;
                if (!label.ClassListContains("chat-message") && !label.ClassListContains("lobby-chat-message")
                    && !label.ClassListContains("cosmetic-chat-text") && !label.ClassListContains("player-speech-text")) return;
                _textColors.Add(label,label.style.color);
                label.style.color = (Color)new Color32(64,53,83,255);
            }

            public void ApplyTailColors()
            {
                var tail = _element.Q<VisualElement>(className:"player-speech-tail");
                if (!ReferenceEquals(_tail,tail))
                {
                    RestoreTail();
                    _tail = tail;
                }
                if (_tail == null) return;
                var style = _element.style;
                _tail.style.backgroundColor = style.backgroundColor;
                _tail.style.borderTopColor = style.borderTopColor;
                _tail.style.borderRightColor = style.borderRightColor;
                _tail.style.borderBottomColor = style.borderBottomColor;
                _tail.style.borderLeftColor = style.borderLeftColor;
            }

            private void RestoreTail()
            {
                if (_tail == null) return;
                _tail.style.backgroundColor = StyleKeyword.Null;
                _tail.style.borderTopColor = _tail.style.borderRightColor
                    = _tail.style.borderBottomColor = _tail.style.borderLeftColor = StyleKeyword.Null;
            }

            public void Restore()
            {
                var style = _element.style;
                style.backgroundColor = _background; style.color = _color;
                style.borderTopColor = _topColor; style.borderRightColor = _rightColor;
                style.borderBottomColor = _bottomColor; style.borderLeftColor = _leftColor;
                style.borderTopWidth = _topWidth; style.borderRightWidth = _rightWidth;
                style.borderBottomWidth = _bottomWidth; style.borderLeftWidth = _leftWidth;
                style.borderTopLeftRadius = _topLeftRadius; style.borderTopRightRadius = _topRightRadius;
                style.borderBottomRightRadius = _bottomRightRadius; style.borderBottomLeftRadius = _bottomLeftRadius;
                style.paddingTop = _topPadding; style.paddingRight = _rightPadding;
                style.paddingBottom = _bottomPadding; style.paddingLeft = _leftPadding;
                if (_element is Label) style.height = _height;
                foreach (var pair in _textColors) pair.Key.style.color = pair.Value;
                _textColors.Clear();
                RestoreTail();
                _tail = null;
            }
        }

        private static void PaintNickname(MeshGenerationContext context, Label label, string effect)
        {
            if (effect.Length == 0 || !TryNicknameTextRect(label,out var rect,out _) || rect.width < 12 || rect.height < 8) return;
            var p = context.painter2D;
            float y = rect.yMax-2.5f, left = rect.xMin+2, right = rect.xMax-2;
            p.lineWidth = 1.2f; p.lineCap = LineCap.Round;
            switch (effect)
            {
                case "nickname-prism":
                    for (int part = 0; part < PRISM_COLORS.Length; part++)
                        Line(p,new Vector2(Mathf.Lerp(left,right,part/5f),y),
                            new Vector2(Mathf.Lerp(left,right,(part+1)/5f),y),PRISM_COLORS[part]);
                    break;
                case "nickname-gold":
                    Line(p,new Vector2(left+3,y),new Vector2(right-3,y),new Color32(221,186,99,255));
                    Sparkle(p,new Vector2(left,y-1),2,new Color32(221,186,99,255));
                    Sparkle(p,new Vector2(right,y-1),2,new Color32(244,210,117,255));
                    break;
                case "nickname-mint":
                    Line(p,new Vector2(left,y),new Vector2(right,y),new Color32(157,213,189,255));
                    Ellipse(p,new Vector2(left+2,y-1),new Vector2(2.5f,1.2f),new Color32(103,174,141,255));
                    Ellipse(p,new Vector2(right-2,y-1),new Vector2(2.5f,1.2f),new Color32(103,174,141,255));
                    break;
                case "nickname-neon":
                    Line(p,new Vector2(left,y-1.5f),new Vector2(right,y-1.5f),new Color32(222,174,230,255));
                    Line(p,new Vector2(left,y),new Vector2(right,y),new Color32(128,207,213,255));
                    break;
                case "nickname-sunset":
                    float third = (right-left)/3;
                    Line(p,new Vector2(left,y),new Vector2(left+third,y),new Color32(228,150,159,255));
                    Line(p,new Vector2(left+third,y),new Vector2(right-third,y),new Color32(237,176,123,255));
                    Line(p,new Vector2(right-third,y),new Vector2(right,y),new Color32(188,157,199,255));
                    break;
            }
        }

        private static void PaintBubble(MeshGenerationContext context, VisualElement element, string effect)
        {
            if (effect.Length == 0) return;
            float width = element.layout.width, height = element.layout.height;
            if (width < 26 || height < 20 || float.IsNaN(width) || float.IsNaN(height)) return;
            var p = context.painter2D;
            p.lineCap = LineCap.Round; p.lineWidth = 1;
            var corner = new Vector2(width-9,height-6);
            switch (effect)
            {
                case "bubble-cloud":
                    Ellipse(p,corner+new Vector2(-3,0),new Vector2(3,1.8f),new Color32(205,226,241,255));
                    Ellipse(p,corner+new Vector2(0,-1),new Vector2(3,2.3f),new Color32(205,226,241,255));
                    Ellipse(p,corner+new Vector2(3,0),new Vector2(2.5f,1.8f),new Color32(205,226,241,255));
                    break;
                case "bubble-memo":
                    p.strokeColor = new Color32(232,204,148,255);
                    p.BeginPath(); p.MoveTo(new Vector2(5,6)); p.LineTo(new Vector2(5,height-6)); p.Stroke();
                    Line(p,new Vector2(width-18,5),new Vector2(width-8,5),new Color32(202,191,222,255));
                    Line(p,new Vector2(width-18,7),new Vector2(width-8,7),new Color32(202,191,222,255));
                    break;
                case "bubble-neon":
                    Line(p,new Vector2(width-20,3),new Vector2(width-11,3),new Color32(143,214,218,255));
                    Line(p,new Vector2(width-4,11),new Vector2(width-4,height-10),new Color32(143,214,218,255));
                    Sparkle(p,corner,3,new Color32(188,136,211,255));
                    break;
                case "bubble-pixel":
                    p.lineCap = LineCap.Butt; p.lineWidth = 3;
                    Line(p,new Vector2(width-14,5),new Vector2(width-6,5),new Color32(159,201,173,255));
                    Line(p,new Vector2(width-5,5),new Vector2(width-5,12),new Color32(159,201,173,255));
                    Line(p,new Vector2(5,height-6),new Vector2(11,height-6),new Color32(159,201,173,255));
                    break;
                case "bubble-petal":
                    for (int petal = 0; petal < 5; petal++)
                    {
                        float angle = petal*Mathf.PI*.4f;
                        Ellipse(p,corner+new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*2.1f,
                            new Vector2(1.6f,1.6f),new Color32(240,183,204,255));
                    }
                    Ellipse(p,corner,new Vector2(1.2f,1.2f),new Color32(247,222,149,255));
                    break;
            }
        }

        private static void Line(Painter2D painter, Vector2 start, Vector2 end, Color color)
        {
            painter.strokeColor = color;
            painter.BeginPath(); painter.MoveTo(start); painter.LineTo(end); painter.Stroke();
        }

        private static void Sparkle(Painter2D painter, Vector2 center, float size, Color color)
        {
            painter.fillColor = color; painter.BeginPath(); painter.MoveTo(center+new Vector2(0,-size));
            painter.LineTo(center+new Vector2(size*.3f,-size*.3f)); painter.LineTo(center+new Vector2(size,0));
            painter.LineTo(center+new Vector2(size*.3f,size*.3f)); painter.LineTo(center+new Vector2(0,size));
            painter.LineTo(center+new Vector2(-size*.3f,size*.3f)); painter.LineTo(center+new Vector2(-size,0));
            painter.LineTo(center+new Vector2(-size*.3f,-size*.3f)); painter.ClosePath(); painter.Fill();
        }

        private static void Ellipse(Painter2D painter, Vector2 center, Vector2 radius, Color color)
        {
            const float k = .5522848f;
            Vector2 V(float x,float y) => center+new Vector2(x*radius.x,y*radius.y);
            painter.fillColor = color; painter.BeginPath(); painter.MoveTo(V(1,0));
            painter.BezierCurveTo(V(1,k),V(k,1),V(0,1));
            painter.BezierCurveTo(V(-k,1),V(-1,k),V(-1,0));
            painter.BezierCurveTo(V(-1,-k),V(-k,-1),V(0,-1));
            painter.BezierCurveTo(V(k,-1),V(1,-k),V(1,0)); painter.ClosePath(); painter.Fill();
        }
    }
}
