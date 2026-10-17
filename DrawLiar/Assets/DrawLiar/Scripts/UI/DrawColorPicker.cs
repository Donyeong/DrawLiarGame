using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawColorPicker : VisualElement
    {
        private const int HUE_SEGMENTS = 36;
        private const int SATURATION_SEGMENTS = 16;
        private readonly VisualElement _spectrum;
        private readonly VisualElement _brightness;
        private readonly VisualElement _preview;
        private readonly TextField _hex;
        private readonly TextField _red;
        private readonly TextField _green;
        private readonly TextField _blue;
        private Color32 _value = new Color32(0, 0, 0, 255);
        private float _hue;
        private float _saturation;
        private float _brightnessValue;
        private VisualElement _dragElement;
        private int _pointerId = -1;

        public event Action<Color32> ColorChanged;

        public Color32 Value
        {
            get => _value;
            set => SetRgb(value, false);
        }

        public DrawColorPicker()
        {
            AddToClassList("brush-color-picker");
            var body = new VisualElement();
            body.AddToClassList("brush-picker-body");
            Add(body);
            _spectrum = CreateSurface("brush-picker-spectrum", PaintSpectrum);
            body.Add(_spectrum);
            _preview = new VisualElement { pickingMode = PickingMode.Ignore };
            _preview.AddToClassList("brush-picker-preview");
            body.Add(_preview);
            _brightness = CreateSurface("brush-picker-brightness", PaintBrightness);
            body.Add(_brightness);

            var fields = new VisualElement();
            fields.AddToClassList("brush-picker-fields");
            body.Add(fields);
            _hex = new TextField { isDelayed = true, maxLength = 7 };
            _hex.AddToClassList("brush-picker-hex");
            ConfigureInput(_hex, TouchScreenKeyboardType.ASCIICapable);
            _hex.RegisterValueChangedCallback(OnHexChanged);
            fields.Add(_hex);
            _red = CreateChannel(fields, 0);
            _green = CreateChannel(fields, 1);
            _blue = CreateChannel(fields, 2);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                DrawLocalization.LanguageChanged += Localize;
                Localize();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                DrawLocalization.LanguageChanged -= Localize;
                EndDrag();
            });
            Localize();
            Synchronize();
        }

        private VisualElement CreateSurface(string className, Action<MeshGenerationContext> paint)
        {
            var surface = new VisualElement { focusable = true };
            surface.AddToClassList(className);
            surface.generateVisualContent += paint;
            surface.RegisterCallback<PointerDownEvent>(evt => StartDrag(surface, evt));
            surface.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_dragElement != surface || evt.pointerId != _pointerId) return;
                UpdatePosition(surface, evt.position);
                evt.StopPropagation();
            });
            surface.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_dragElement != surface || evt.pointerId != _pointerId) return;
                UpdatePosition(surface, evt.position);
                EndDrag();
                evt.StopPropagation();
            });
            surface.RegisterCallback<PointerCancelEvent>(evt =>
            {
                if (_dragElement != surface || evt.pointerId != _pointerId) return;
                EndDrag();
                evt.StopPropagation();
            });
            surface.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (_dragElement == surface && evt.pointerId == _pointerId) EndDrag();
            });
            surface.RegisterCallback<KeyDownEvent>(evt => OnSurfaceKey(surface, evt));
            return surface;
        }

        private TextField CreateChannel(VisualElement parent, int channel)
        {
            var field = new TextField { isDelayed = true };
            field.AddToClassList("brush-picker-rgb");
            ConfigureInput(field, TouchScreenKeyboardType.NumberPad);
            field.RegisterValueChangedCallback(evt =>
            {
                if (!ReferenceEquals(evt.target, field)) return;
                if (!int.TryParse(evt.newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                    || value < 0 || value > 255)
                {
                    byte previous = channel == 0 ? _value.r : channel == 1 ? _value.g : _value.b;
                    field.SetValueWithoutNotify(previous.ToString(CultureInfo.InvariantCulture));
                    return;
                }
                Color32 color = _value;
                if (channel == 0) color.r = (byte)value;
                else if (channel == 1) color.g = (byte)value;
                else color.b = (byte)value;
                SetRgb(color, true);
            });
            parent.Add(field);
            return field;
        }

        private static void ConfigureInput(TextField field, TouchScreenKeyboardType keyboard)
        {
            field.textEdition.keyboardType = keyboard;
            field.textEdition.autoCorrection = false;
            var input = field.Q<VisualElement>(className: TextField.inputUssClassName);
            if (input != null) input.languageDirection = LanguageDirection.LTR;
        }

        private void Localize()
        {
            _hex.label = DrawLocalization.Text("색상 코드");
            _red.label = DrawLocalization.Text("빨강");
            _green.label = DrawLocalization.Text("녹색");
            _blue.label = DrawLocalization.Text("파랑");
            _spectrum.tooltip = DrawLocalization.Text("색상과 채도");
            _brightness.tooltip = DrawLocalization.Text("명도");
        }

        private void OnHexChanged(ChangeEvent<string> evt)
        {
            if (!ReferenceEquals(evt.target, _hex)) return;
            string input = (evt.newValue ?? string.Empty).Trim();
            if (input.StartsWith("#", StringComparison.Ordinal)) input = input.Substring(1);
            if (input.Length != 6 || !uint.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                _hex.SetValueWithoutNotify(HexValue());
                return;
            }
            SetRgb(new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255), true);
        }

        private void SetRgb(Color32 color, bool notify)
        {
            color.a = 255;
            Color.RGBToHSV(color, out float hue, out float saturation, out float brightness);
            if (saturation > 0) _hue = hue;
            if (brightness > 0) _saturation = saturation;
            _brightnessValue = brightness;
            ApplyValue(color, notify);
        }

        private void ApplyHsv()
        {
            Color32 color = Color.HSVToRGB(_hue, _saturation, _brightnessValue);
            color.a = 255;
            ApplyValue(color, true);
        }

        private void ApplyValue(Color32 color, bool notify)
        {
            bool changed = !_value.Equals(color);
            _value = color;
            Synchronize();
            if (changed && notify) ColorChanged?.Invoke(_value);
        }

        private void Synchronize()
        {
            _hex.SetValueWithoutNotify(HexValue());
            _red.SetValueWithoutNotify(_value.r.ToString(CultureInfo.InvariantCulture));
            _green.SetValueWithoutNotify(_value.g.ToString(CultureInfo.InvariantCulture));
            _blue.SetValueWithoutNotify(_value.b.ToString(CultureInfo.InvariantCulture));
            _preview.style.backgroundColor = (Color)_value;
            _preview.tooltip = HexValue();
            _spectrum.MarkDirtyRepaint();
            _brightness.MarkDirtyRepaint();
        }

        private string HexValue() => "#" + ColorUtility.ToHtmlStringRGB(_value);

        private void StartDrag(VisualElement surface, PointerDownEvent evt)
        {
            if (_pointerId >= 0 || evt.button != 0 || !surface.enabledInHierarchy) return;
            Vector2 point = surface.WorldToLocal(evt.position);
            if (!surface.contentRect.Contains(point)) return;
            _dragElement = surface;
            _pointerId = evt.pointerId;
            surface.Focus();
            surface.CapturePointer(evt.pointerId);
            if (surface == _spectrum && _brightnessValue <= 0) _brightnessValue = 1;
            UpdatePosition(surface, evt.position);
            evt.StopPropagation();
        }

        private void UpdatePosition(VisualElement surface, Vector2 position)
        {
            Rect bounds = surface.contentRect;
            if (bounds.width <= 0 || bounds.height <= 0) return;
            Vector2 point = surface.WorldToLocal(position);
            if (surface == _spectrum)
            {
                _hue = Mathf.Clamp01((point.x - bounds.xMin) / bounds.width);
                _saturation = 1 - Mathf.Clamp01((point.y - bounds.yMin) / bounds.height);
            }
            else _brightnessValue = 1 - Mathf.Clamp01((point.y - bounds.yMin) / bounds.height);
            ApplyHsv();
        }

        private void EndDrag()
        {
            VisualElement surface = _dragElement;
            int pointerId = _pointerId;
            _dragElement = null;
            _pointerId = -1;
            if (surface != null && pointerId >= 0 && surface.HasPointerCapture(pointerId)) surface.ReleasePointer(pointerId);
        }

        private void OnSurfaceKey(VisualElement surface, KeyDownEvent evt)
        {
            float step = evt.shiftKey ? .1f : .01f;
            if (surface == _spectrum)
            {
                switch (evt.keyCode)
                {
                    case KeyCode.LeftArrow: _hue = Mathf.Clamp01(_hue - step); break;
                    case KeyCode.RightArrow: _hue = Mathf.Clamp01(_hue + step); break;
                    case KeyCode.UpArrow: _saturation = Mathf.Clamp01(_saturation + step); break;
                    case KeyCode.DownArrow: _saturation = Mathf.Clamp01(_saturation - step); break;
                    default: return;
                }
                if (_brightnessValue <= 0) _brightnessValue = 1;
            }
            else
            {
                switch (evt.keyCode)
                {
                    case KeyCode.UpArrow:
                    case KeyCode.RightArrow: _brightnessValue = Mathf.Clamp01(_brightnessValue + step); break;
                    case KeyCode.DownArrow:
                    case KeyCode.LeftArrow: _brightnessValue = Mathf.Clamp01(_brightnessValue - step); break;
                    case KeyCode.Home: _brightnessValue = 1; break;
                    case KeyCode.End: _brightnessValue = 0; break;
                    default: return;
                }
            }
            ApplyHsv();
            evt.StopPropagation();
        }

        private void PaintSpectrum(MeshGenerationContext context)
        {
            Rect bounds = _spectrum.contentRect;
            if (bounds.width <= 0 || bounds.height <= 0) return;
            int columns = HUE_SEGMENTS + 1;
            MeshWriteData mesh = context.Allocate(columns * (SATURATION_SEGMENTS + 1), HUE_SEGMENTS * SATURATION_SEGMENTS * 6);
            for (int row = 0; row <= SATURATION_SEGMENTS; row++)
            {
                float y = row / (float)SATURATION_SEGMENTS;
                for (int column = 0; column <= HUE_SEGMENTS; column++)
                {
                    float x = column / (float)HUE_SEGMENTS;
                    mesh.SetNextVertex(new Vertex
                    {
                        position = new Vector3(bounds.xMin + bounds.width * x, bounds.yMin + bounds.height * y, Vertex.nearZ),
                        tint = Color.HSVToRGB(x, 1 - y, 1)
                    });
                }
            }
            for (int row = 0; row < SATURATION_SEGMENTS; row++)
            {
                for (int column = 0; column < HUE_SEGMENTS; column++)
                {
                    ushort index = (ushort)(row * columns + column);
                    mesh.SetNextIndex(index);
                    mesh.SetNextIndex((ushort)(index + 1));
                    mesh.SetNextIndex((ushort)(index + columns));
                    mesh.SetNextIndex((ushort)(index + 1));
                    mesh.SetNextIndex((ushort)(index + columns + 1));
                    mesh.SetNextIndex((ushort)(index + columns));
                }
            }
            PaintMarker(context.painter2D, bounds, new Vector2(bounds.xMin + bounds.width * _hue, bounds.yMin + bounds.height * (1 - _saturation)));
        }

        private void PaintBrightness(MeshGenerationContext context)
        {
            Rect bounds = _brightness.contentRect;
            if (bounds.width <= 0 || bounds.height <= 0) return;
            MeshWriteData mesh = context.Allocate(4, 6);
            Color32 topColor = Color.HSVToRGB(_hue, _saturation, 1);
            mesh.SetNextVertex(new Vertex { position = new Vector3(bounds.xMin, bounds.yMin, Vertex.nearZ), tint = topColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(bounds.xMax, bounds.yMin, Vertex.nearZ), tint = topColor });
            mesh.SetNextVertex(new Vertex { position = new Vector3(bounds.xMax, bounds.yMax, Vertex.nearZ), tint = new Color32(0, 0, 0, 255) });
            mesh.SetNextVertex(new Vertex { position = new Vector3(bounds.xMin, bounds.yMax, Vertex.nearZ), tint = new Color32(0, 0, 0, 255) });
            mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
            mesh.SetNextIndex(2); mesh.SetNextIndex(3); mesh.SetNextIndex(0);
            PaintMarker(context.painter2D, bounds, new Vector2(bounds.center.x, bounds.yMin + bounds.height * (1 - _brightnessValue)));
        }

        private static void PaintMarker(Painter2D painter, Rect bounds, Vector2 center)
        {
            float radius = Mathf.Min(6, bounds.width * .3f, bounds.height * .3f);
            center.x = Mathf.Clamp(center.x, bounds.xMin + radius, bounds.xMax - radius);
            center.y = Mathf.Clamp(center.y, bounds.yMin + radius, bounds.yMax - radius);
            float curve = radius * .552285f;
            painter.BeginPath();
            painter.MoveTo(center + new Vector2(radius, 0));
            painter.BezierCurveTo(center + new Vector2(radius, curve), center + new Vector2(curve, radius), center + new Vector2(0, radius));
            painter.BezierCurveTo(center + new Vector2(-curve, radius), center + new Vector2(-radius, curve), center + new Vector2(-radius, 0));
            painter.BezierCurveTo(center + new Vector2(-radius, -curve), center + new Vector2(-curve, -radius), center + new Vector2(0, -radius));
            painter.BezierCurveTo(center + new Vector2(curve, -radius), center + new Vector2(radius, -curve), center + new Vector2(radius, 0));
            painter.ClosePath();
            painter.strokeColor = Color.black;
            painter.lineWidth = 3;
            painter.Stroke();
            painter.strokeColor = Color.white;
            painter.lineWidth = 1.5f;
            painter.Stroke();
        }

    }
}
