using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public static class DrawSmoothScroll
    {
        private const float DECELERATION_RATE = .1f;
        private const float ELASTICITY = .09f;
        private const float WHEEL_IMPULSE = 650f / 3f;
        private const float MAX_SPEED = 3000f;
        private const float STOP_SPEED = 5f;
        private const float AXIS_BIAS = 1.5f;
        private const float EDGE_TOLERANCE = .5f;
        private static readonly ConditionalWeakTable<ScrollView, Binding> _bindings = new ConditionalWeakTable<ScrollView, Binding>();

        public static ScrollView Create(ScrollViewMode mode = ScrollViewMode.Vertical)
        {
            var scroll = new ScrollView(mode);
            Bind(scroll);
            return scroll;
        }

        public static void Bind(ScrollView scroll)
        {
            if (scroll != null) _bindings.GetValue(scroll, value => new Binding(value)).Refresh();
        }

        public static void RefreshAll(VisualElement root)
        {
            root?.Query<ScrollView>().ForEach(Bind);
        }

        private static Vector2 ScrollDelta(ScrollView scroll, Vector2 delta)
        {
            if (scroll.mode == ScrollViewMode.Vertical)
                return new Vector2(0, Mathf.Abs(delta.y) >= Mathf.Abs(delta.x) ? delta.y : delta.x);
            if (scroll.mode == ScrollViewMode.Horizontal)
                return new Vector2(Mathf.Abs(delta.x) > Mathf.Abs(delta.y) ? delta.x : delta.y, 0);
            return delta;
        }

        private static Vector2 ClampOffset(ScrollView scroll, Vector2 offset)
        {
            return new Vector2(
                Mathf.Clamp(offset.x, scroll.horizontalScroller.lowValue, Mathf.Max(scroll.horizontalScroller.lowValue, scroll.horizontalScroller.highValue)),
                Mathf.Clamp(offset.y, scroll.verticalScroller.lowValue, Mathf.Max(scroll.verticalScroller.lowValue, scroll.verticalScroller.highValue)));
        }

        private static bool CanScroll(ScrollView scroll, Vector2 delta)
        {
            if (!scroll.enabledInHierarchy || IsHidden(scroll)) return false;
            delta = ScrollDelta(scroll, delta);
            Vector2 offset = scroll.scrollOffset;
            return (delta.x < 0 && offset.x > scroll.horizontalScroller.lowValue + EDGE_TOLERANCE)
                || (delta.x > 0 && offset.x < scroll.horizontalScroller.highValue - EDGE_TOLERANCE)
                || (delta.y < 0 && offset.y > scroll.verticalScroller.lowValue + EDGE_TOLERANCE)
                || (delta.y > 0 && offset.y < scroll.verticalScroller.highValue - EDGE_TOLERANCE);
        }

        private static bool IsHidden(VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
                if (current.resolvedStyle.display == DisplayStyle.None) return true;
            return false;
        }

        private static ScrollView WheelReceiver(VisualElement target, Vector2 delta)
        {
            ScrollView first = null;
            for (var current = target; current != null; current = current.parent)
            {
                if (!(current is ScrollView scroll)) continue;
                first = scroll;
                break;
            }
            if (first == null) return null;

            // 가로 목록 위의 세로 입력은 바깥 페이지가 우선 받는다.
            if (first.mode == ScrollViewMode.Horizontal && Mathf.Abs(delta.x) <= Mathf.Abs(delta.y) * AXIS_BIAS)
            {
                for (var parent = first.parent; parent != null; parent = parent.parent)
                {
                    if (!(parent is ScrollView vertical) || vertical.mode == ScrollViewMode.Horizontal) continue;
                    first = vertical;
                    break;
                }
            }
            bool horizontal = first.mode == ScrollViewMode.Horizontal
                || (first.mode != ScrollViewMode.Vertical && Mathf.Abs(delta.x) > Mathf.Abs(delta.y) * AXIS_BIAS);
            for (VisualElement current = first; current != null; current = current.parent)
                if (current is ScrollView scroll && (horizontal ? scroll.mode != ScrollViewMode.Vertical : scroll.mode != ScrollViewMode.Horizontal)
                    && CanScroll(scroll, delta)) return scroll;
            return first;
        }

        private static void StopOtherScrolls(VisualElement target, ScrollView receiver)
        {
            for (var current = target; current != null; current = current.parent)
                if (current is ScrollView scroll && !ReferenceEquals(scroll, receiver) && _bindings.TryGetValue(scroll, out var binding)) binding.Stop();
        }

        private sealed class Binding
        {
            private readonly ScrollView _scroll;
            private IVisualElementScheduledItem _animation;
            private Vector2 _velocity, _lastOffset;
            private double _lastTime;
            private ScrollViewMode _lastMode;

            public Binding(ScrollView scroll)
            {
                _scroll = scroll;
                scroll.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
                scroll.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                scroll.RegisterCallback<AttachToPanelEvent>(OnAttach);
                scroll.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            }

            public void Refresh()
            {
                bool reduced = DrawUIMotion.IsReducedMotion(_scroll);
                _scroll.touchScrollBehavior = reduced ? ScrollView.TouchScrollBehavior.Clamped : ScrollView.TouchScrollBehavior.Elastic;
                _scroll.scrollDecelerationRate = reduced ? 0 : DECELERATION_RATE;
                _scroll.elasticity = ELASTICITY;
                if (reduced) _scroll.scrollOffset = ClampOffset(_scroll, _scroll.scrollOffset);
                Stop();
            }

            private void OnAttach(AttachToPanelEvent evt) => Refresh();
            private void OnDetach(DetachFromPanelEvent evt) => Stop();
            private void OnPointerDown(PointerDownEvent evt) => Stop();

            private void OnWheel(WheelEvent evt)
            {
                if (evt.actionKey) return;
                Vector2 delta = new Vector2(evt.delta.x, evt.delta.y);
                if (delta.sqrMagnitude == 0 || !ReferenceEquals(WheelReceiver(evt.target as VisualElement, delta), _scroll)) return;
                StopOtherScrolls(evt.target as VisualElement, _scroll);
                evt.StopImmediatePropagation();
                if (!CanScroll(_scroll, delta)) { Stop(); return; }
                delta = ScrollDelta(_scroll, delta);
                if (DrawUIMotion.IsReducedMotion(_scroll))
                {
                    Stop();
                    _scroll.scrollOffset = ClampOffset(_scroll, _scroll.scrollOffset + delta * _scroll.mouseWheelScrollSize);
                    return;
                }
                if (_scroll.mode != _lastMode || (_scroll.scrollOffset - _lastOffset).sqrMagnitude > EDGE_TOLERANCE * EDGE_TOLERANCE) Stop();
                if (delta.x * _velocity.x < 0) _velocity.x = 0;
                if (delta.y * _velocity.y < 0) _velocity.y = 0;
                if (_velocity.sqrMagnitude == 0) _lastTime = Time.unscaledTimeAsDouble;
                _velocity = Vector2.ClampMagnitude(_velocity + delta * WHEEL_IMPULSE, MAX_SPEED);
                _lastOffset = _scroll.scrollOffset;
                if (_animation == null) _animation = _scroll.schedule.Execute(Animate).Every(16);
                else if (!_animation.isActive) _animation.Resume();
            }

            private void Animate()
            {
                if (_scroll.panel == null || !_scroll.enabledInHierarchy || IsHidden(_scroll) || _scroll.mode != _lastMode
                    || DrawUIMotion.IsReducedMotion(_scroll) || (_scroll.scrollOffset - _lastOffset).sqrMagnitude > EDGE_TOLERANCE * EDGE_TOLERANCE)
                {
                    Stop();
                    return;
                }
                double now = Time.unscaledTimeAsDouble;
                float elapsed = Mathf.Clamp((float)(now - _lastTime), 0, .05f);
                _lastTime = now;
                float decay = Mathf.Pow(DECELERATION_RATE, elapsed);
                Vector2 next = _scroll.scrollOffset + _velocity * ((1 - decay) / -Mathf.Log(DECELERATION_RATE));
                Vector2 clamped = ClampOffset(_scroll, next);
                _scroll.scrollOffset = clamped;
                _lastOffset = _scroll.scrollOffset;
                _velocity *= decay;
                if (Mathf.Abs(next.x - clamped.x) > .001f) _velocity.x = 0;
                if (Mathf.Abs(next.y - clamped.y) > .001f) _velocity.y = 0;
                if (_velocity.sqrMagnitude < STOP_SPEED * STOP_SPEED) Stop();
            }

            public void Stop()
            {
                _animation?.Pause();
                _velocity = Vector2.zero;
                _lastOffset = _scroll.scrollOffset;
                _lastMode = _scroll.mode;
            }
        }
    }
}
