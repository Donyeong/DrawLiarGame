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
            }
            else
            {
                label.style.color = NicknameColor(effect);
                label.style.unityTextOutlineColor = (Color)(effect == "nickname-neon"
                    ? new Color32(190,233,236,255) : new Color32(255,250,241,255));
                label.style.unityTextOutlineWidth = effect == "nickname-neon" ? .45f : .2f;
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
                case "nickname-neon": return new Color32(146,83,170,255);
                default: return new Color32(190,106,104,255);
            }
        }

        private sealed class NicknameBinding
        {
            public string Effect = "";
            public readonly StyleColor Color, OutlineColor;
            public readonly StyleFloat OutlineWidth;

            public NicknameBinding(Label label)
            {
                Color = label.style.color;
                OutlineColor = label.style.unityTextOutlineColor;
                OutlineWidth = label.style.unityTextOutlineWidth;
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
            if (effect.Length == 0) return;
            var rect = label.contentRect;
            if (rect.width < 12 || rect.height < 8) return;
            var p = context.painter2D;
            float y = rect.yMax-1, left = rect.xMin+2, right = rect.xMax-2;
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
