#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    internal sealed class DrawBrowserTextInput : IDisposable
    {
        [Serializable] private sealed class InputFields { public InputField[] Fields; public int ChatShortcutToken; }
        [Serializable] private sealed class InputField
        {
            public int Id, Limit, Keyboard;
            public float X, Y, Width, Height, ClipX, ClipY, ClipWidth, ClipHeight, HitX, HitY, HitWidth, HitHeight, FontSize;
            public bool Multiline, Password, Correction, Rtl, SubmitOnCompositionEnd, ChatShortcut;
            public string Value, Placeholder, Name, Color, Background;
        }

        private sealed class BrowserKeyDownEvent : KeyDownEvent
        {
            public DrawBrowserTextInput Owner { get; private set; }
            private TextField _field;

            public BrowserKeyDownEvent(DrawBrowserTextInput owner, TextField field, KeyCode key, EventModifiers eventModifiers)
            {
                Owner = owner; _field = field; keyCode = key; modifiers = eventModifiers;
            }

            protected override void PreDispatch(IPanel panel)
            {
                base.PreDispatch(panel);
                bool allowClosedEscape = keyCode == KeyCode.Escape && Owner?._active == null;
                if (Owner == null || Owner._disposed || Owner._active != _field && !allowClosedEscape)
                    StopImmediatePropagation();
            }

            protected override void PostDispatch(IPanel panel)
            {
                try { base.PostDispatch(panel); }
                finally
                {
                    if (keyCode == KeyCode.Escape && Owner?._active == _field) Owner?.Close(true);
                    Owner = null; _field = null;
                }
            }
        }

        private readonly VisualElement _root;
        private readonly Func<VisualElement> _chatShortcutTarget;
        private readonly Action _openChatShortcut;
        private readonly Dictionary<TextField, int> _ids = new Dictionary<TextField, int>();
        private readonly Dictionary<int, TextField> _fields = new Dictionary<int, TextField>();
        private readonly Dictionary<TextField, InputField> _layouts = new Dictionary<TextField, InputField>();
        private readonly float[] _keyboardMetrics = new float[3];
        private readonly IVisualElementScheduledItem _poller;
        private TextField _active;
        private VisualElement _configuredChatShortcut;
        private TextElement _text;
        private StyleFloat _opacity;
        private bool _hideKeyboard, _captureKeyboard, _reading, _disposed;
        private string _knownValue;
        private int _nextId, _activeId, _chatShortcutVersion;
        private float _nextLayout;

        public DrawBrowserTextInput(VisualElement root, Func<VisualElement> chatShortcutTarget, Action openChatShortcut)
        {
            _root = root;
            _chatShortcutTarget = chatShortcutTarget;
            _openChatShortcut = openChatShortcut;
            root.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            root.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<DetachFromPanelEvent>(OnDetached);
            _poller = root.schedule.Execute(Poll).Every(16);
            RefreshFields();
        }

        public bool Focus(TextField field)
        {
            if (_disposed || field == null || field.isReadOnly || field.panel != _root.panel
                || !_root.Contains(field) || !IsVisible(field)) return false;
            RefreshFields();
            if (!_ids.TryGetValue(field, out int id)) return false;
            field.Focus();
            if (_disposed || field.isReadOnly || field.panel != _root.panel || !_root.Contains(field)
                || !IsVisible(field) || !_ids.TryGetValue(field, out id)) return false;
            if (_active != field) Open(field, id);
            else if (DrawBrowserInterop.DrawBrowserInputOpen(id) != 1) Close(false);
            return _active == field && DrawBrowserInterop.DrawBrowserInputFocused(id) == 1;
        }

        public void Blur(TextField field, bool focusCanvas)
        {
            if (_disposed || field == null) return;
            DrawBrowserInterop.DrawBrowserInputTakeChatShortcut();
            if (_active != field) return;
            SynchronizeValue();
            ReadValue(true);
            if (_active == field) Close(focusCanvas);
        }

        public void RefreshChatShortcut()
        {
            if (!_disposed) RefreshFields();
        }

        private static TextField ParentField(VisualElement element)
        {
            while (element != null)
            {
                if (element is TextField field) return field;
                element = element.parent;
            }
            return null;
        }

        private static bool IsVisible(VisualElement element)
        {
            if (element.panel == null || !element.enabledInHierarchy) return false;
            for (var parent = element; parent != null; parent = parent.parent)
                if (parent.resolvedStyle.display == DisplayStyle.None || parent.resolvedStyle.visibility == Visibility.Hidden) return false;
            return true;
        }

        private static bool IsRightToLeft(VisualElement element)
        {
            for (var parent = element; parent != null; parent = parent.parent)
                if (parent.languageDirection != LanguageDirection.Inherit) return parent.languageDirection == LanguageDirection.RTL;
            return false;
        }

        private static Rect Intersection(Rect first, Rect second)
        {
            float left = Mathf.Max(first.xMin, second.xMin), top = Mathf.Max(first.yMin, second.yMin);
            float right = Mathf.Min(first.xMax, second.xMax), bottom = Mathf.Min(first.yMax, second.yMax);
            return new Rect(left, top, Mathf.Max(0, right - left), Mathf.Max(0, bottom - top));
        }

        private void RefreshFields()
        {
            _nextLayout = Time.unscaledTime + .08f;
            Rect panel = _root.panel?.visualTree.worldBound ?? default;
            if (panel.width <= 0 || panel.height <= 0) return;
            var found = new HashSet<TextField>();
            var configuration = new List<InputField>();
            var chatShortcut = _chatShortcutTarget?.Invoke();
            if (_configuredChatShortcut != chatShortcut)
            {
                _configuredChatShortcut = chatShortcut;
                _chatShortcutVersion++;
            }
            foreach (var field in _root.Query<TextField>().ToList())
            {
                if (field.isReadOnly || !IsVisible(field) || configuration.Count >= 128) continue;
                var input = field.Q<VisualElement>(className: TextField.inputUssClassName);
                if (input == null) continue;
                Rect bounds = input.LocalToWorld(input.contentRect);
                if (bounds.width <= 0 || bounds.height <= 0 || !panel.Overlaps(bounds)) continue;
                Rect clip = Intersection(bounds, panel);
                Rect hit = Intersection(input.worldBound, panel);
                for (var ancestor = input.parent; ancestor != null; ancestor = ancestor.parent)
                    if (ancestor.ClassListContains(ScrollView.viewportUssClassName))
                    {
                        clip = Intersection(clip, ancestor.worldBound);
                        hit = Intersection(hit, ancestor.worldBound);
                    }
                if (clip.width <= 0 || clip.height <= 0 || ParentField(_root.panel.Pick(clip.center)) != field) continue;
                found.Add(field);
                if (!_ids.TryGetValue(field, out int id)) { id = ++_nextId; _ids.Add(field, id); _fields.Add(id, field); }
                var text = input.Q<TextElement>();
                var style = (text ?? input).resolvedStyle;
                var layout = new InputField
                {
                    Id = id, X = (bounds.x - panel.x) / panel.width, Y = (bounds.y - panel.y) / panel.height,
                    Width = bounds.width / panel.width, Height = bounds.height / panel.height,
                    ClipX = (clip.x - panel.x) / panel.width, ClipY = (clip.y - panel.y) / panel.height,
                    ClipWidth = clip.width / panel.width, ClipHeight = clip.height / panel.height,
                    HitX = (hit.x - panel.x) / panel.width, HitY = (hit.y - panel.y) / panel.height,
                    HitWidth = hit.width / panel.width, HitHeight = hit.height / panel.height,
                    FontSize = style.fontSize / panel.width, Color = "#" + ColorUtility.ToHtmlStringRGBA(style.color),
                    Background = "#" + ColorUtility.ToHtmlStringRGBA(input.resolvedStyle.backgroundColor),
                    Limit = field.maxLength >= 0 ? Math.Min(field.maxLength, 65536) : 65536,
                    Value = field.value ?? "", Placeholder = field.textEdition.placeholder ?? "",
                    Name = string.IsNullOrEmpty(field.name) ? field.label : field.name,
                    Multiline = field.multiline, Password = field.isPasswordField,
                    Keyboard = (int)field.textEdition.keyboardType, Correction = field.textEdition.autoCorrection,
                    Rtl = IsRightToLeft(text ?? input),
                    ChatShortcut = field == chatShortcut,
                    SubmitOnCompositionEnd = field.ClassListContains("chat-input") || field.ClassListContains("lobby-chat-input") || field.ClassListContains("guess-input")
                };
                _layouts[field] = layout;
                configuration.Add(layout);
            }
            if (_active != null && !found.Contains(_active) && _active == chatShortcut
                && DrawBrowserInterop.IsMobile && DrawBrowserInterop.DrawBrowserKeyboardMetrics(_keyboardMetrics) == 1
                && !_active.isReadOnly && IsVisible(_active)
                && _root.Contains(_active) && ParentField(_root.panel.focusController.focusedElement as VisualElement) == _active
                && _layouts.TryGetValue(_active, out var activeLayout) && configuration.Count < 128)
            {
                activeLayout.Value = _active.value ?? "";
                activeLayout.ChatShortcut = _active == chatShortcut;
                found.Add(_active);
                configuration.Add(activeLayout);
            }
            if (_active != null && !found.Contains(_active)) Close(false);
            var removed = new List<TextField>();
            foreach (var field in _ids.Keys) if (!found.Contains(field)) removed.Add(field);
            foreach (var field in removed) { _fields.Remove(_ids[field]); _ids.Remove(field); _layouts.Remove(field); }
            DrawBrowserInterop.DrawBrowserInputConfigure(JsonUtility.ToJson(new InputFields
            {
                Fields = configuration.ToArray(), ChatShortcutToken = chatShortcut != null ? _chatShortcutVersion : 0
            }));
            var focused = ParentField(_root.panel?.focusController.focusedElement as VisualElement);
            if (_active == null && focused != null && found.Contains(focused) && _ids.TryGetValue(focused, out int focusedId))
                Open(focused, focusedId);
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            if (_disposed) return;
            if (_chatShortcutTarget?.Invoke() != _configuredChatShortcut) RefreshFields();
            var field = ParentField(evt.target as VisualElement);
            if (field == null || field.isReadOnly) return;
            if (!_ids.ContainsKey(field)) RefreshFields();
            if (_ids.TryGetValue(field, out int id)) Open(field, id);
        }

        private void Open(TextField field, int id)
        {
            if (_active == field) return;
            Close(false);
            _active = field; _activeId = id;
            _knownValue = field.value;
            field.RegisterValueChangedCallback(OnValueChanged);
            _hideKeyboard = field.textEdition.hideSoftKeyboard;
            field.textEdition.hideSoftKeyboard = true;
            _captureKeyboard = WebGLInput.captureAllKeyboardInput;
            WebGLInput.captureAllKeyboardInput = false;
            var input = field.Q<VisualElement>(className: TextField.inputUssClassName);
            _text = input?.Q<TextElement>();
            if (_text != null) { _opacity = _text.style.opacity; _text.style.opacity = 0; }
            if (DrawBrowserInterop.DrawBrowserInputOpen(id) != 1) Close(false);
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            if (ParentField(evt.target as VisualElement) != _active || _active == null) return;
            if (ParentField(evt.relatedTarget as VisualElement) == _active) return;
            Close(false);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_active != null && !(evt is BrowserKeyDownEvent browser && ReferenceEquals(browser.Owner, this)))
                evt.StopImmediatePropagation();
        }

        private void Poll()
        {
            if (_disposed) return;
            if (_chatShortcutTarget?.Invoke() != _configuredChatShortcut) RefreshFields();
            int shortcutToken = DrawBrowserInterop.DrawBrowserInputTakeChatShortcut();
            if (shortcutToken != 0 && shortcutToken == _chatShortcutVersion && _configuredChatShortcut != null
                && _chatShortcutTarget?.Invoke() == _configuredChatShortcut) _openChatShortcut?.Invoke();
            int browserId = DrawBrowserInterop.DrawBrowserInputActive();
            int browserState = browserId != 0 ? DrawBrowserInterop.DrawBrowserInputState(browserId) : 0;
            if ((browserState & 128) != 0 && _fields.TryGetValue(browserId, out var shortcut))
            {
                if (_chatShortcutTarget?.Invoke() != shortcut)
                {
                    DrawBrowserInterop.DrawBrowserInputClose(browserId, 0);
                    RefreshFields();
                    return;
                }
                shortcut.Focus();
            }
            if (browserId != _activeId && _fields.TryGetValue(browserId, out var focused))
            {
                Open(focused, browserId);
                focused.Focus();
            }
            if (_active != null)
            {
                SynchronizeValue();
                int state = _activeId == browserId ? browserState : DrawBrowserInterop.DrawBrowserInputState(_activeId);
                var source = _active;
                if ((state & 64) != 0) ReadValue(false);
                if ((state & 1) != 0) { ReadValue(true); Dispatch(KeyCode.Return, EventModifiers.None); }
                if ((state & 2) != 0) { ReadValue(true); Dispatch(KeyCode.Escape, EventModifiers.None, source); }
                else if ((state & 8) != 0) { ReadValue(true); MoveFocus((state & 16) != 0); }
                else if ((state & 4) != 0) { var blurred = _active; Close(false); blurred?.Blur(); }
                if (_active != null && !IsVisible(_active)) Close(false);
                SynchronizeValue();
            }
            if (Time.unscaledTime >= _nextLayout) RefreshFields();
        }

        private void ReadValue(bool commit)
        {
            if (_active == null || _active.isDelayed && !commit) return;
            string value = DrawBrowserInterop.DrawBrowserInputValue(_activeId);
            if (value == null) return;
            var field = _active;
            _reading = true;
            try { if (field.value != value) field.value = value; }
            finally { _reading = false; }
            if (_active != field) return;
            _knownValue = field.value;
            if (field.value != value) DrawBrowserInterop.DrawBrowserInputSetValue(_activeId, field.value ?? "");
        }

        private void OnValueChanged(ChangeEvent<string> evt)
        {
            if (!_reading && ReferenceEquals(evt.target, _active)) SynchronizeValue();
        }

        private void SynchronizeValue()
        {
            if (_active == null || _reading || _active.value == _knownValue) return;
            _knownValue = _active.value;
            DrawBrowserInterop.DrawBrowserInputSetValue(_activeId, _knownValue ?? "");
        }

        private void Dispatch(KeyCode key, EventModifiers modifiers, TextField source = null)
        {
            var field = source ?? _active;
            if (field?.panel != _root.panel || _root.panel == null) return;
            var target = key == KeyCode.Return || field != _active ? field : (VisualElement)_text ?? field;
            var evt = new BrowserKeyDownEvent(this, field, key, modifiers) { target = target };
            target.SendEvent(evt);
        }

        private void MoveFocus(bool backwards)
        {
            var previous = _active;
            Dispatch(KeyCode.Tab, backwards ? EventModifiers.Shift : EventModifiers.None);
            if (_active != previous || previous == null) return;
            var controls = _root.Query<VisualElement>().ToList().FindAll(element => element.focusable && element.tabIndex >= 0
                && IsVisible(element) && (element is TextField || element is Button || element is Toggle || element is DropdownField));
            int index = controls.FindIndex(element => element == previous);
            if (controls.Count == 0) return;
            int next = (index + (backwards ? -1 : 1) + controls.Count) % controls.Count;
            Close(true);
            controls[next].Focus();
        }

        private void Close(bool focusCanvas)
        {
            if (_active == null) return;
            SynchronizeValue();
            var field = _active; var text = _text; int id = _activeId;
            string value = DrawBrowserInterop.DrawBrowserInputValue(id);
            _active = null; _text = null; _activeId = 0;
            field.UnregisterValueChangedCallback(OnValueChanged);
            if (text != null) text.style.opacity = _opacity;
            field.textEdition.hideSoftKeyboard = _hideKeyboard;
            WebGLInput.captureAllKeyboardInput = _captureKeyboard;
            DrawBrowserInterop.DrawBrowserInputClose(id, focusCanvas ? 1 : 0);
            if (value != null && field.value != value) field.value = value;
            if (value != null && field.value != value) DrawBrowserInterop.DrawBrowserInputSetValue(id, field.value ?? "");
            if (focusCanvas) field.Blur();
        }

        private void OnDetached(DetachFromPanelEvent evt) { if (ReferenceEquals(evt.target, _root)) Dispose(); }

        public void Dispose()
        {
            if (_disposed) return;
            Close(false); _disposed = true; _poller.Pause();
            _root.UnregisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            _root.UnregisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            _root.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _root.UnregisterCallback<DetachFromPanelEvent>(OnDetached);
            DrawBrowserInterop.DrawBrowserInputShutdown();
            _fields.Clear(); _ids.Clear(); _layouts.Clear();
        }
    }
}
#endif
