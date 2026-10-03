using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public static class DrawUIMotion
    {
        private const int FRAME_DELAY_MS = 20;
        private const int REDUCED_DURATION_MS = 80;
        private const int MAX_STAGGER_MS = 140;
        private static readonly ConditionalWeakTable<VisualElement, Motion> _motions = new ConditionalWeakTable<VisualElement, Motion>();
        private static readonly List<StylePropertyName> _properties = new List<StylePropertyName> { "opacity", "translate", "scale" };
        private static readonly List<TimeValue> _zeroDuration = new List<TimeValue> { new TimeValue(0) };
        private static readonly List<EasingFunction> _easing = new List<EasingFunction> { new EasingFunction(EasingMode.EaseOut) };

        public static bool HasAncestorClass(VisualElement element, string className)
        {
            for (var current = element; current != null; current = current.parent)
                if (current.ClassListContains(className)) return true;
            return false;
        }

        public static bool IsReducedMotion(VisualElement element) => HasAncestorClass(element, "reduce-motion");

        public static void Enter(VisualElement element, int duration = 220, float offset = 8, int delay = 0)
        {
            if (element == null) return;
            GetMotion(element).Enter(Math.Max(0, duration), offset, Math.Max(0, delay), false);
        }

        public static void Stagger(VisualElement container, int step = 28, int duration = 220, float offset = 8)
        {
            if (container == null) return;
            int index = 0;
            foreach (var child in container.Children())
                Enter(child, duration, offset, Math.Min(MAX_STAGGER_MS, index++ * Math.Max(0, step)));
        }

        public static void ShowModal(VisualElement overlay, VisualElement panel)
        {
            overlay?.RemoveFromClassList("enter");
            Enter(overlay, 160, 0);
            if (panel != null) GetMotion(panel).Enter(220, 12, 0, true);
        }

        public static void HideModal(VisualElement overlay, VisualElement panel, Action completed = null)
        {
            if (overlay == null) { completed?.Invoke(); return; }
            overlay.pickingMode = PickingMode.Ignore;
            overlay.SetEnabled(false);
            if (panel != null) GetMotion(panel).Exit(150, 10, true, null);
            GetMotion(overlay).Exit(170, 0, false, completed ?? overlay.RemoveFromHierarchy);
        }

        public static void ShowSheet(VisualElement sheet)
        {
            if (sheet != null) GetMotion(sheet).Enter(200, 16, 0, false);
        }

        public static void HideSheet(VisualElement sheet, Action completed = null)
        {
            if (sheet == null) { completed?.Invoke(); return; }
            sheet.pickingMode = PickingMode.Ignore;
            GetMotion(sheet).Exit(150, 12, false, completed ?? sheet.RemoveFromHierarchy);
        }

        public static void ShowToast(VisualElement toast, int visibleMilliseconds = 4200)
        {
            if (toast == null) return;
            var motion = GetMotion(toast);
            motion.Enter(180, 8, 0, false);
            motion.After(Math.Max(500, visibleMilliseconds), () => motion.Exit(140, 5, false, toast.RemoveFromHierarchy));
        }

        public static void Reset(VisualElement element)
        {
            if (element != null && _motions.TryGetValue(element, out var motion)) motion.Reset();
        }

        private static Motion GetMotion(VisualElement element) => _motions.GetValue(element, value => new Motion(value));

        private sealed class Motion
        {
            private readonly VisualElement _element;
            private readonly StyleFloat _opacity;
            private readonly StyleTranslate _translate;
            private readonly StyleScale _scale;
            private readonly StyleList<StylePropertyName> _transitionProperties;
            private readonly StyleList<TimeValue> _transitionDuration;
            private readonly StyleList<EasingFunction> _transitionEasing;
            private IVisualElementScheduledItem _start, _finish, _after;
            private int _version;

            public Motion(VisualElement element)
            {
                _element = element;
                _opacity = element.style.opacity;
                _translate = element.style.translate;
                _scale = element.style.scale;
                _transitionProperties = element.style.transitionProperty;
                _transitionDuration = element.style.transitionDuration;
                _transitionEasing = element.style.transitionTimingFunction;
                element.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            }

            public void Enter(int duration, float offset, int delay, bool scale)
            {
                Reset();
                bool reduced = IsReducedMotion(_element);
                bool button = _element is Button;
                int actualDuration = reduced ? REDUCED_DURATION_MS : duration;
                float actualOffset = reduced || button ? 0 : offset;
                int actualDelay = reduced ? FRAME_DELAY_MS : FRAME_DELAY_MS + delay;
                int version = _version;
                SetTransition(0, button);
                _element.style.opacity = 0;
                if (!button) _element.style.translate = new Translate(0, actualOffset);
                if (scale && !reduced && !button) _element.style.scale = new Scale(new Vector3(.98f, .98f, 1));
                _start = _element.schedule.Execute(() =>
                {
                    if (version != _version) return;
                    _start = null;
                    SetTransition(actualDuration, button);
                    _element.style.opacity = _opacity.keyword == StyleKeyword.Undefined ? _opacity.value : 1;
                    if (!button) _element.style.translate = new Translate(0, 0);
                    if (scale && !button) _element.style.scale = new Scale(Vector3.one);
                    _finish = _element.schedule.Execute(() =>
                    {
                        if (version != _version) return;
                        _finish = null;
                        Restore();
                    }).StartingIn(actualDuration + FRAME_DELAY_MS);
                }).StartingIn(actualDelay);
            }

            public void Exit(int duration, float offset, bool scale, Action completed)
            {
                Cancel();
                bool reduced = IsReducedMotion(_element);
                int actualDuration = reduced ? REDUCED_DURATION_MS : duration;
                int version = _version;
                SetTransition(actualDuration, false);
                _element.style.opacity = 0;
                _element.style.translate = new Translate(0, reduced ? 0 : offset);
                if (scale) _element.style.scale = new Scale(new Vector3(reduced ? 1 : .985f, reduced ? 1 : .985f, 1));
                _finish = _element.schedule.Execute(() =>
                {
                    if (version != _version) return;
                    _finish = null;
                    completed?.Invoke();
                }).StartingIn(actualDuration + FRAME_DELAY_MS);
            }

            public void After(int delay, Action action)
            {
                _after?.Pause();
                int version = _version;
                _after = _element.schedule.Execute(() =>
                {
                    if (version != _version) return;
                    _after = null;
                    action();
                }).StartingIn(delay);
            }

            private void SetTransition(int duration, bool button)
            {
                // 버튼은 USS의 누름·복원 전환 시간을 유지합니다.
                if (button) return;
                _element.style.transitionProperty = _properties;
                _element.style.transitionDuration = duration == 0 ? _zeroDuration
                    : new List<TimeValue> { new TimeValue(duration, TimeUnit.Millisecond) };
                _element.style.transitionTimingFunction = _easing;
            }

            private void OnDetach(DetachFromPanelEvent evt) => Reset();

            private void Cancel()
            {
                _version++;
                _start?.Pause(); _finish?.Pause(); _after?.Pause();
                _start = _finish = _after = null;
            }

            private void Restore()
            {
                _element.style.opacity = _opacity;
                _element.style.translate = _translate;
                _element.style.scale = _scale;
                _element.style.transitionProperty = _transitionProperties;
                _element.style.transitionDuration = _transitionDuration;
                _element.style.transitionTimingFunction = _transitionEasing;
            }

            public void Reset()
            {
                Cancel();
                Restore();
            }
        }
    }
}
