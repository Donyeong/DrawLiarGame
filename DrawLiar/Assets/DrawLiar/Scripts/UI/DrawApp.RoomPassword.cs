using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed partial class DrawApp
    {
        private string _createRoomPassword = "", _roomOptionsPassword = "";
        private bool _roomPasswordEventsAttached;
        private VisualElement _roomPasswordOverlay, _roomPasswordReturnFocus;
        private TextField _roomPasswordField;
        private TaskCompletionSource<string> _roomPasswordCompletion;
#if ENABLE_INPUT_SYSTEM
        private UnityEngine.InputSystem.Keyboard _roomPasswordKeyboard;
        private bool _roomPasswordComposing;
        private int _roomPasswordCompositionFrame = -1;
#endif

        private void AttachRoomPasswordEvents()
        {
            if (lobby == null || _roomPasswordEventsAttached) return;
            lobby.RoomPasswordRequested += RequestRoomPasswordAsync;
            _roomPasswordEventsAttached = true;
        }

        private void DetachRoomPasswordEvents()
        {
            if (lobby == null || !_roomPasswordEventsAttached) return;
            lobby.RoomPasswordRequested -= RequestRoomPasswordAsync;
            _roomPasswordEventsAttached = false;
        }

        private static bool IsValidRoomPassword(string password) => password != null && password.Length >= 4 && password.Length <= 32
            && !string.IsNullOrWhiteSpace(password) && !password.Any(char.IsControl);

        private static void ValidateRoomPassword(bool isPrivate, string password, bool keepExisting)
        {
            if (!isPrivate || keepExisting && string.IsNullOrEmpty(password)) return;
            if (!IsValidRoomPassword(password)) throw new InvalidOperationException(DrawLocalization.Text("비밀번호는 4~32자로 입력하세요."));
        }

        private static TextField RoomPasswordField(VisualElement parent, string label, string value, string name)
        {
            var field = Field(parent, label, value);
            field.name = name;
            field.maxLength = 32;
            field.isPasswordField = true;
            field.textEdition.autoCorrection = false;
            field.textEdition.keyboardType = TouchScreenKeyboardType.Default;
            field.AddToClassList("room-password-input");
            return field;
        }

        private async Task<string> RequestRoomPasswordAsync(RoomPasswordPrompt prompt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isActiveAndEnabled || root?.panel == null) throw new OperationCanceledException();
            CancelRoomPasswordPrompt();
            var completion = _roomPasswordCompletion = DrawAsync.Completion<string>();
            _roomPasswordReturnFocus = root.focusController?.focusedElement as VisualElement;
            var popup = _roomPasswordOverlay = Box(root, "overlay enter utility-overlay room-password-overlay");
            popup.name = "room-password-overlay";
            var modal = Box(popup, "modal utility-popup room-password-popup");
            modal.name = "room-password-popup";
            var heading = Box(modal, "row utility-popup-header");
            Text(heading, "비밀번호 입력", "utility-popup-title grow");
            IconButton(heading, "닫기", DrawUIIcon.Kind.Close, CancelRoomPasswordPrompt, "room-password-close");
            RawText(modal, string.IsNullOrWhiteSpace(prompt.RoomName) ? DisplayRoomCode(prompt.RoomCode) : prompt.RoomName, "social-confirm-room");
            var field = _roomPasswordField = RoomPasswordField(modal, "방 비밀번호", "", "room-password-input");
            Text(modal, "비밀번호는 4~32자로 입력하세요.", "rules");
            if (!string.IsNullOrWhiteSpace(prompt.Error) && prompt.Error != "비밀번호가 필요한 방입니다.")
                Text(modal, prompt.Error, "notice room-password-error").name = "room-password-error";
            var actions = Box(modal, "row social-confirm-actions room-password-actions");
            Button(actions, "취소", CancelRoomPasswordPrompt, "secondary grow", DrawSound.UiCancel).name = "room-password-cancel";
            void Submit()
            {
                if (_roomPasswordCompletion != completion || !IsValidRoomPassword(field.value)) return;
                string password = field.value;
                HideRoomPasswordPrompt(completion);
                completion.TrySetResult(password);
            }
            var submit = Button(actions, "입장", Submit, "primary grow", DrawSound.UiConfirm);
            submit.name = "room-password-submit";
            submit.SetEnabled(false);
            field.RegisterValueChangedCallback(evt => { if (ReferenceEquals(evt.target, field)) submit.SetEnabled(IsValidRoomPassword(evt.newValue)); });
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
#if ENABLE_INPUT_SYSTEM && (UNITY_EDITOR || !UNITY_WEBGL)
                if (_roomPasswordComposing || _roomPasswordCompositionFrame == Time.frameCount) return;
#endif
                Submit();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            popup.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (_roomPasswordOverlay != popup || !ReferenceEquals(evt.target, popup)) return;
                CancelRoomPasswordPrompt();
                evt.StopImmediatePropagation();
            });
            popup.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
                {
                    CancelRoomPasswordPrompt();
                    evt.StopImmediatePropagation();
                    return;
                }
                if (evt.keyCode != KeyCode.Tab) return;
                var controls = modal.Query<VisualElement>().ToList().Where(control => control.canGrabFocus && control.enabledInHierarchy
                    && control.tabIndex >= 0 && control.resolvedStyle.visibility == Visibility.Visible && IsVisible(control)).ToList();
                if (controls.Count == 0) return;
                int index = controls.IndexOf(root.focusController?.focusedElement as VisualElement);
                int next = index < 0 ? (evt.shiftKey ? controls.Count - 1 : 0) : (index + (evt.shiftKey ? -1 : 1) + controls.Count) % controls.Count;
                root.focusController?.IgnoreEvent(evt);
                controls[next].Focus();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            DrawUIMotion.ShowModal(popup, modal);
#if ENABLE_INPUT_SYSTEM
            _roomPasswordKeyboard = UnityEngine.InputSystem.Keyboard.current;
            if (_roomPasswordKeyboard != null)
            {
                _roomPasswordKeyboard.onIMECompositionChange += OnRoomPasswordComposition;
                _roomPasswordKeyboard.onTextInput += OnRoomPasswordTextInput;
            }
#endif
            field.Focus();
            HideMobileScrollers();
            try
            {
                using (cancellationToken.Register(() => completion.TrySetCanceled())) return await completion.Task;
            }
            finally { HideRoomPasswordPrompt(completion); }
        }

        private void CancelRoomPasswordPrompt()
        {
            CancelRoomPasswordPrompt(false);
        }

        private void CancelRoomPasswordPrompt(bool immediate)
        {
            var completion = _roomPasswordCompletion;
            if (completion == null) return;
            HideRoomPasswordPrompt(completion, immediate);
            completion.TrySetCanceled();
        }

        private void HideRoomPasswordPrompt(TaskCompletionSource<string> completion, bool immediate = false)
        {
            if (_roomPasswordCompletion != completion) return;
            var popup = _roomPasswordOverlay;
            var field = _roomPasswordField;
            var returnFocus = _roomPasswordReturnFocus;
            _roomPasswordCompletion = null;
            _roomPasswordOverlay = _roomPasswordReturnFocus = null;
            _roomPasswordField = null;
#if ENABLE_INPUT_SYSTEM
            if (_roomPasswordKeyboard != null)
            {
                _roomPasswordKeyboard.onIMECompositionChange -= OnRoomPasswordComposition;
                _roomPasswordKeyboard.onTextInput -= OnRoomPasswordTextInput;
            }
            _roomPasswordKeyboard = null;
            _roomPasswordComposing = false;
            _roomPasswordCompositionFrame = -1;
#endif
            field?.Blur();
            field?.SetValueWithoutNotify("");
            if (popup != null)
            {
                if (immediate) popup.RemoveFromHierarchy();
                else DrawUIMotion.HideModal(popup, popup.Q<VisualElement>(className: "modal"));
            }
            if (returnFocus?.panel != null && root != null)
                root.schedule.Execute(() => { if (_roomPasswordOverlay == null && this != null && isActiveAndEnabled && returnFocus.panel != null) returnFocus.Focus(); }).StartingIn(180);
        }

#if ENABLE_INPUT_SYSTEM
        private void OnRoomPasswordComposition(UnityEngine.InputSystem.LowLevel.IMECompositionString composition)
        {
            _roomPasswordComposing = composition.Count > 0;
            _roomPasswordCompositionFrame = Time.frameCount;
        }

        private void OnRoomPasswordTextInput(char character)
        {
            if (!_roomPasswordComposing) return;
            _roomPasswordComposing = false;
            _roomPasswordCompositionFrame = Time.frameCount;
        }
#endif

        private void ClearRoomOptionsPassword()
        {
            _roomOptionsPassword = "";
            ClearRoomPasswordField(root?.Q<TextField>("room-options-password"));
        }

        private void ClearCreateRoomPassword()
        {
            _createRoomPassword = "";
            ClearRoomPasswordField(root?.Q<TextField>("create-room-password"));
        }

        private static void ClearRoomPasswordField(TextField field)
        {
            field?.Blur();
            field?.SetValueWithoutNotify("");
        }

        private void ClearRoomPasswordSecrets()
        {
            CancelRoomPasswordPrompt(true);
            ClearCreateRoomPassword();
            ClearRoomOptionsPassword();
        }
    }
}
