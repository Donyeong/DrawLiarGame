using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public static class DrawButtonScale
    {
        private const string MOTION_CLASS = "draw-button-scale";
        private const string PRESSED_CLASS = "draw-pressed";
        private const string RELEASED_CLASS = "draw-released";
        private const int RELEASE_PEAK_MS = 70;
        private static readonly ConditionalWeakTable<Button, Binding> _bindings = new ConditionalWeakTable<Button, Binding>();

        public static void Bind(Button button, bool mobileOnly = true)
        {
            if (button == null) return;
            if (_bindings.TryGetValue(button, out var existing))
            {
                existing.MobileOnly = mobileOnly;
                existing.Reset();
                return;
            }
            _bindings.Add(button, new Binding(button, mobileOnly));
        }

        public static void Unbind(Button button)
        {
            if (button == null || !_bindings.TryGetValue(button, out var binding)) return;
            binding.Dispose();
            _bindings.Remove(button);
        }

        public static void Reset(Button button)
        {
            if (button != null && _bindings.TryGetValue(button, out var binding)) binding.Reset();
        }

        public static void ResetAll(VisualElement root)
        {
            root?.Query<Button>().ForEach(Reset);
        }

        private sealed class Binding : IDisposable
        {
            private const CallbackOptions POINTER_OPTIONS = CallbackOptions.TrickleDown | CallbackOptions.IncludeDisabled;
            private readonly Button _button;
            private IVisualElementScheduledItem _release;
            private int _pointerId = -1;
            private Rect _pressBounds;
            private bool _registered;

            public bool MobileOnly { get; set; }
            private bool Enabled => (!MobileOnly || DrawUIMotion.HasAncestorClass(_button, "mobile"))
                && _button.enabledInHierarchy && !DrawUIMotion.IsReducedMotion(_button);

            public Binding(Button button, bool mobileOnly)
            {
                _button = button;
                MobileOnly = mobileOnly;
                _button.RegisterCallback<AttachToPanelEvent>(OnAttach);
                _button.RegisterCallback<DetachFromPanelEvent>(OnDetach);
                Register();
                Reset();
            }

            private void Register()
            {
                if (_registered) return;
                _registered = true;
                _button.RegisterCallback<PointerDownEvent>(OnDown, POINTER_OPTIONS);
                _button.RegisterCallback<PointerUpEvent>(OnUp, POINTER_OPTIONS);
                _button.RegisterCallback<PointerMoveEvent>(OnMove, POINTER_OPTIONS);
                _button.RegisterCallback<PointerLeaveEvent>(OnLeave, POINTER_OPTIONS);
                _button.RegisterCallback<PointerCancelEvent>(OnCancel, POINTER_OPTIONS);
                _button.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut, CallbackOptions.IncludeDisabled);
                _button.RegisterCallback<NavigationSubmitEvent>(OnSubmit, POINTER_OPTIONS);
                _button.RegisterCallback<BlurEvent>(OnBlur, CallbackOptions.IncludeDisabled);
                _button.RegisterCallback<TransitionRunEvent>(OnTransition, CallbackOptions.IncludeDisabled);
            }

            private void Unregister()
            {
                if (!_registered) return;
                _registered = false;
                _button.UnregisterCallback<PointerDownEvent>(OnDown, POINTER_OPTIONS);
                _button.UnregisterCallback<PointerUpEvent>(OnUp, POINTER_OPTIONS);
                _button.UnregisterCallback<PointerMoveEvent>(OnMove, POINTER_OPTIONS);
                _button.UnregisterCallback<PointerLeaveEvent>(OnLeave, POINTER_OPTIONS);
                _button.UnregisterCallback<PointerCancelEvent>(OnCancel, POINTER_OPTIONS);
                _button.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut, CallbackOptions.IncludeDisabled);
                _button.UnregisterCallback<NavigationSubmitEvent>(OnSubmit, POINTER_OPTIONS);
                _button.UnregisterCallback<BlurEvent>(OnBlur, CallbackOptions.IncludeDisabled);
                _button.UnregisterCallback<TransitionRunEvent>(OnTransition, CallbackOptions.IncludeDisabled);
            }

            private void OnAttach(AttachToPanelEvent evt) { Register(); Reset(); }
            private void OnDetach(DetachFromPanelEvent evt) { Reset(); Unregister(); }
            private void OnBlur(BlurEvent evt) => Reset();

            private void OnDown(PointerDownEvent evt)
            {
                if (evt.button != 0 || _pointerId >= 0) return;
                if (!Enabled) { Reset(); return; }
                _release?.Pause();
                _pointerId = evt.pointerId;
                _pressBounds = _button.worldBound;
                _button.RemoveFromClassList(RELEASED_CLASS);
                _button.AddToClassList(PRESSED_CLASS);
            }

            private void OnUp(PointerUpEvent evt)
            {
                if (_pointerId != evt.pointerId) return;
                bool inside = _pressBounds.Contains(new Vector2(evt.position.x, evt.position.y));
                _pointerId = -1;
                if (Enabled && inside) Release();
                else Reset();
            }

            private void OnMove(PointerMoveEvent evt)
            {
                if (_pointerId == evt.pointerId && (!Enabled || !_pressBounds.Contains(new Vector2(evt.position.x, evt.position.y)))) Reset();
            }

            private void OnLeave(PointerLeaveEvent evt)
            {
                if (_pointerId == evt.pointerId && ReferenceEquals(evt.target, _button)) Reset();
            }

            private void OnCancel(PointerCancelEvent evt)
            {
                if (_pointerId == evt.pointerId) Reset();
            }

            private void OnCaptureOut(PointerCaptureOutEvent evt)
            {
                if (_pointerId == evt.pointerId) Reset();
            }

            private void OnSubmit(NavigationSubmitEvent evt)
            {
                if (Enabled) Release();
                else Reset();
            }

            private void OnTransition(TransitionRunEvent evt)
            {
                if (!Enabled && (_pointerId >= 0 || _button.ClassListContains(RELEASED_CLASS))) Reset();
            }

            private void Release()
            {
                _release?.Pause();
                _button.RemoveFromClassList(PRESSED_CLASS);
                _button.AddToClassList(RELEASED_CLASS);
                _release = _button.schedule.Execute(() =>
                {
                    _release = null;
                    _button.RemoveFromClassList(RELEASED_CLASS);
                }).StartingIn(RELEASE_PEAK_MS);
            }

            public void Reset()
            {
                _release?.Pause();
                _release = null;
                _pointerId = -1;
                _button.RemoveFromClassList(PRESSED_CLASS);
                _button.RemoveFromClassList(RELEASED_CLASS);
                _button.EnableInClassList(MOTION_CLASS, !MobileOnly || DrawUIMotion.HasAncestorClass(_button, "mobile"));
            }

            public void Dispose()
            {
                Reset();
                Unregister();
                _button.UnregisterCallback<AttachToPanelEvent>(OnAttach);
                _button.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
                _button.RemoveFromClassList(MOTION_CLASS);
            }
        }
    }
}
