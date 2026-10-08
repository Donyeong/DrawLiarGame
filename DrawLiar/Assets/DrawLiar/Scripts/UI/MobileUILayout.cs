using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class MobileUILayout : IDisposable
    {
        private const int POLL_INTERVAL_MS = 125;
        private readonly VisualElement _root;
        private readonly PanelSettings _panelSettings;
        private Vector2Int _pcReferenceResolution;
        private float _pcMatch;
        private readonly IVisualElementScheduledItem _poll;
        private bool _editorMobilePreview, _disposed, _keyboardOpen, _hasMetrics, _refreshing, _geometryDirty = true;
        private StyleLength _pcPaddingLeft, _pcPaddingTop, _pcPaddingRight, _pcPaddingBottom;
        private int _screenWidth, _screenHeight, _unoccludedHeight;
        private ScreenOrientation _orientation;
        private Rect _safeArea;
        private float _keyboardHeight, _keyboardTop;
        private VisualElement _focusedField;
        private Vector4 _padding = new Vector4(-1, -1, -1, -1);
#if UNITY_WEBGL && !UNITY_EDITOR
        private readonly float[] _browserKeyboardMetrics = new float[3];
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _decorView, _visibleFrame, _windowManager;
        private bool _androidUnavailable;
        private int _androidSdk, _unoccludedNativeHeight;
#endif
#if UNITY_EDITOR
        public enum EditorTestMode { Automatic, PC, Mobile }
        public const string EDITOR_TEST_MODE_KEY = "DrawLiar.EditorTest.Mode";
        private readonly EditorTestMode _editorTestMode;
        private Rect? _previewSafeArea;
        private float _previewKeyboardHeight;
#endif

        public bool IsMobile { get; private set; }
        public bool IsPortrait { get; private set; }
        public Vector2 AvailableSize { get; private set; }
        public event Action LayoutChanged;

        public MobileUILayout(VisualElement root, PanelSettings panelSettings, bool forceMobilePreview = false)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _panelSettings = panelSettings ?? throw new ArgumentNullException(nameof(panelSettings));
            _pcReferenceResolution = panelSettings.referenceResolution;
            _pcMatch = panelSettings.match;
            _editorMobilePreview = Application.isEditor && forceMobilePreview;
#if UNITY_EDITOR
            _editorTestMode = (EditorTestMode)Mathf.Clamp(UnityEditor.SessionState.GetInt(EDITOR_TEST_MODE_KEY, 0), 0, 2);
            _editorMobilePreview |= _editorTestMode == EditorTestMode.Mobile;
#endif
            _root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _root.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            _root.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            _root.RegisterCallback<AttachToPanelEvent>(OnAttached);
            _poll = _root.schedule.Execute(Refresh).Every(POLL_INTERVAL_MS);
            Refresh();
        }

        public void Refresh()
        {
            if (_disposed) return;
            if (_refreshing) { _geometryDirty = true; return; }
            _refreshing = true;
            try { RefreshLayout(); }
            finally { _refreshing = false; }
        }

        private void RefreshLayout()
        {
            int width = Screen.width, height = Screen.height;
            if (width <= 0 || height <= 0) return;
#if UNITY_EDITOR
            if (_editorTestMode == EditorTestMode.Automatic && height > width) _editorMobilePreview = true;
            bool mobile = _editorTestMode != EditorTestMode.PC && (Application.isMobilePlatform || _editorMobilePreview);
#elif UNITY_WEBGL
            bool mobile = Application.isMobilePlatform || DrawBrowserInterop.IsMobile;
#else
            bool mobile = Application.isMobilePlatform || _editorMobilePreview;
#endif
            ScreenOrientation orientation = Screen.orientation;
            bool portrait = height >= width;
#if UNITY_WEBGL && !UNITY_EDITOR
            bool browserKeyboardOpen = mobile && DrawBrowserInterop.DrawBrowserKeyboardMetrics(_browserKeyboardMetrics) == 1;
            bool preserveBrowserLayout = browserKeyboardOpen && _hasMetrics && IsMobile && width == _screenWidth;
#endif
            if (Application.isMobilePlatform)
            {
                if (orientation is ScreenOrientation.Portrait or ScreenOrientation.PortraitUpsideDown) portrait = true;
                else if (orientation is ScreenOrientation.LandscapeLeft or ScreenOrientation.LandscapeRight) portrait = false;
                else if (_hasMetrics && _keyboardOpen && orientation == _orientation) portrait = IsPortrait;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (preserveBrowserLayout) portrait = height * Mathf.Max(1, _browserKeyboardMetrics[0]) >= width;
#endif
            if (!_hasMetrics || width != _screenWidth || portrait != IsPortrait)
            {
                _unoccludedHeight = height;
#if UNITY_ANDROID && !UNITY_EDITOR
                _unoccludedNativeHeight = 0;
#endif
            }

            Rect safeArea = Screen.safeArea;
            float keyboardHeight = 0, keyboardTop = 0;
            bool keyboardOpen = false;
            if (_focusedField != null && _focusedField.panel == null) _focusedField = null;
            bool inspectKeyboard = mobile && (_focusedField != null || _keyboardOpen);
#if UNITY_ANDROID && !UNITY_EDITOR
            inspectKeyboard |= mobile && _unoccludedNativeHeight == 0;
#endif
            if (inspectKeyboard)
            {
                keyboardOpen = TouchScreenKeyboard.visible;
                if (keyboardOpen) keyboardHeight = TouchScreenKeyboard.area.height;
#if UNITY_ANDROID && !UNITY_EDITOR
                ReadAndroidKeyboard(width, height, ref keyboardHeight, ref keyboardOpen);
#endif
            }
#if UNITY_EDITOR
            if (_previewSafeArea.HasValue) safeArea = _previewSafeArea.Value;
            if (_previewKeyboardHeight > 0) { keyboardOpen = true; keyboardHeight = _previewKeyboardHeight; }
#elif UNITY_WEBGL
            keyboardOpen = browserKeyboardOpen;
            if (keyboardOpen)
            {
                _unoccludedHeight = Mathf.RoundToInt(height * Mathf.Max(1, _browserKeyboardMetrics[0]));
                keyboardTop = height * Mathf.Clamp01(_browserKeyboardMetrics[1]);
                keyboardHeight = height * Mathf.Clamp01(_browserKeyboardMetrics[2]);
            }
#endif
            if (!keyboardOpen) _unoccludedHeight = height;
            keyboardHeight = mobile ? Mathf.Clamp(keyboardHeight, 0, height) : 0;
            bool changed = !_hasMetrics || mobile != IsMobile || portrait != IsPortrait || width != _screenWidth || height != _screenHeight
                || orientation != _orientation || safeArea != _safeArea || Mathf.Abs(keyboardHeight - _keyboardHeight) > .5f
                || Mathf.Abs(keyboardTop - _keyboardTop) > .5f || keyboardOpen != _keyboardOpen;
            if (mobile && !IsMobile)
            {
                _pcReferenceResolution = _panelSettings.referenceResolution;
                _pcMatch = _panelSettings.match;
                _pcPaddingLeft = _root.style.paddingLeft; _pcPaddingTop = _root.style.paddingTop;
                _pcPaddingRight = _root.style.paddingRight; _pcPaddingBottom = _root.style.paddingBottom;
            }
            else if (!mobile && IsMobile)
            {
                _panelSettings.referenceResolution = _pcReferenceResolution;
                _panelSettings.match = _pcMatch;
                _root.style.paddingLeft = _pcPaddingLeft; _root.style.paddingTop = _pcPaddingTop;
                _root.style.paddingRight = _pcPaddingRight; _root.style.paddingBottom = _pcPaddingBottom;
            }
            IsMobile = mobile; IsPortrait = portrait;
            _screenWidth = width; _screenHeight = height; _orientation = orientation;
            _safeArea = safeArea; _keyboardHeight = keyboardHeight; _keyboardTop = keyboardTop; _keyboardOpen = mobile && keyboardOpen; _hasMetrics = true;
            if (!changed && !_geometryDirty) return;
            _geometryDirty = false;
            _root.EnableInClassList("mobile", IsMobile);
            _root.EnableInClassList("mobile-portrait", IsMobile && IsPortrait);
            _root.EnableInClassList("mobile-landscape", IsMobile && !IsPortrait);
            _root.EnableInClassList("keyboard-open", _keyboardOpen);
            if (mobile)
            {
                var reference = portrait ? new Vector2Int(432, 936) : new Vector2Int(936, 432);
                float scale = Mathf.Min(width / (float)reference.x, Mathf.Max(1, _unoccludedHeight) / (float)reference.y);
                reference.x = Mathf.Max(reference.x, Mathf.CeilToInt(width / scale));
                if (_panelSettings.referenceResolution != reference) _panelSettings.referenceResolution = reference;
                if (_panelSettings.match != 0) _panelSettings.match = 0;
            }
            bool geometryChanged = ApplyInsets();
            if (changed || geometryChanged)
            {
                LayoutChanged?.Invoke();
                if (_keyboardOpen) ScrollFocusedField();
            }
        }

        private bool ApplyInsets()
        {
            if (_root.panel == null) return false;
            Vector2 screenEnd = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(_screenWidth, _screenHeight));
            Vector2 screenStart = RuntimePanelUtils.ScreenToPanel(_root.panel, Vector2.zero);
            Vector4 padding = Vector4.zero;
            if (IsMobile)
            {
                var safeStart = new Vector2(Mathf.Clamp(_safeArea.xMin, 0, _screenWidth), Mathf.Clamp(_screenHeight - _safeArea.yMax, 0, _screenHeight));
                var safeEnd = new Vector2(Mathf.Clamp(_safeArea.xMax, 0, _screenWidth), Mathf.Clamp(_screenHeight - _safeArea.yMin, 0, _screenHeight));
                Vector2 topLeft = RuntimePanelUtils.ScreenToPanel(_root.panel, safeStart);
                Vector2 bottomRight = RuntimePanelUtils.ScreenToPanel(_root.panel, safeEnd);
                float keyboardBottom = screenEnd.y - RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(0, _screenHeight - _keyboardHeight)).y;
                float keyboardTop = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(0, _keyboardTop)).y - screenStart.y;
                padding = new Vector4(Mathf.Max(0, topLeft.x - screenStart.x), Mathf.Max(0, topLeft.y - screenStart.y, keyboardTop),
                    Mathf.Max(0, screenEnd.x - bottomRight.x), Mathf.Max(0, screenEnd.y - bottomRight.y, keyboardBottom));
            }
            Vector2 available = new Vector2(Mathf.Max(0, screenEnd.x - screenStart.x - padding.x - padding.z), Mathf.Max(0, screenEnd.y - screenStart.y - padding.y - padding.w));
            bool changed = (padding - _padding).sqrMagnitude > .01f || (available - AvailableSize).sqrMagnitude > .01f;
            if (!changed) return false;
            _padding = padding; AvailableSize = available;
            if (IsMobile)
            {
                _root.style.paddingLeft = padding.x; _root.style.paddingTop = padding.y;
                _root.style.paddingRight = padding.z; _root.style.paddingBottom = padding.w;
            }
            return true;
        }

        private void ScrollFocusedField()
        {
            var field = _focusedField;
            if (field?.panel == null) return;
            field.schedule.Execute(() =>
            {
                if (!_disposed && field.panel != null && field == _focusedField)
                    field.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(field);
            }).StartingIn(1);
        }

        private void OnGeometryChanged(GeometryChangedEvent evt) { _geometryDirty = true; Refresh(); }
        private void OnAttached(AttachToPanelEvent evt) { _geometryDirty = true; Refresh(); }
        private void OnFocusIn(FocusInEvent evt)
        {
            var target = evt.target as VisualElement;
            _focusedField = FindTextInput(target);
            Refresh();
            if (_keyboardOpen) ScrollFocusedField();
        }
        private void OnFocusOut(FocusOutEvent evt)
        {
            var target = evt.target as VisualElement;
            if (FindTextInput(target) == _focusedField) _focusedField = null;
        }

        private static VisualElement FindTextInput(VisualElement target)
        {
            for (var element = target; element != null; element = element.parent)
                if (element.ClassListContains(TextInputBaseField<string>.ussClassName)) return element;
            return null;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void ReadAndroidKeyboard(int screenWidth, int screenHeight, ref float keyboardHeight, ref bool keyboardOpen)
        {
            if (_androidUnavailable) return;
            try
            {
                if (_decorView == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    using var window = activity.Call<AndroidJavaObject>("getWindow");
                    _decorView = window.Call<AndroidJavaObject>("getDecorView");
                    _windowManager = activity.Call<AndroidJavaObject>("getWindowManager");
                    _visibleFrame = new AndroidJavaObject("android.graphics.Rect");
                    using var version = new AndroidJavaClass("android.os.Build$VERSION");
                    _androidSdk = version.GetStatic<int>("SDK_INT");
                }
                int nativeHeight = _decorView.Call<int>("getHeight");
                if (nativeHeight <= 0) return;
                if (_androidSdk >= 30 && _unoccludedNativeHeight == 0)
                {
                    using var metrics = _windowManager.Call<AndroidJavaObject>("getCurrentWindowMetrics");
                    using var bounds = metrics.Call<AndroidJavaObject>("getBounds");
                    int fullHeight = bounds.Call<int>("height"), fullWidth = bounds.Call<int>("width");
                    if (fullHeight > 0 && fullWidth > 0)
                    {
                        _unoccludedNativeHeight = fullHeight;
                        _unoccludedHeight = Mathf.RoundToInt(fullHeight * screenWidth / (float)fullWidth);
                    }
                }
                int nativeKeyboard = 0;
                bool hasInsets = false;
                if (_androidSdk >= 30)
                {
                    using var insets = _decorView.Call<AndroidJavaObject>("getRootWindowInsets");
                    if (insets != null && insets.GetRawObject() != IntPtr.Zero)
                    {
                        using var types = new AndroidJavaClass("android.view.WindowInsets$Type");
                        int ime = types.CallStatic<int>("ime");
                        keyboardOpen = insets.Call<bool>("isVisible", ime);
                        using var dimensions = insets.Call<AndroidJavaObject>("getInsets", ime);
                        nativeKeyboard = keyboardOpen ? dimensions.Get<int>("bottom") : 0;
                        hasInsets = true;
                    }
                }
                if (_unoccludedNativeHeight == 0)
                    _unoccludedNativeHeight = Mathf.RoundToInt(nativeHeight * (_unoccludedHeight / (float)Math.Max(1, screenHeight)));
                if (!hasInsets)
                {
                    _decorView.Call("getWindowVisibleDisplayFrame", _visibleFrame);
                    nativeKeyboard = Mathf.Max(0, _unoccludedNativeHeight - _visibleFrame.Get<int>("bottom"));
                    keyboardOpen |= nativeKeyboard > _unoccludedNativeHeight * .15f;
                    if (!keyboardOpen) nativeKeyboard = 0;
                }
                if (!keyboardOpen) _unoccludedNativeHeight = Math.Max(_unoccludedNativeHeight, nativeHeight);
                keyboardHeight = CalculateKeyboardOcclusion(nativeKeyboard, _unoccludedNativeHeight, _unoccludedHeight, screenHeight);
            }
            catch (AndroidJavaException)
            {
                _androidUnavailable = true;
                ReleaseAndroid();
            }
        }
        private void ReleaseAndroid()
        {
            _decorView?.Dispose(); _decorView = null;
            _visibleFrame?.Dispose(); _visibleFrame = null;
            _windowManager?.Dispose(); _windowManager = null;
        }
#endif

        private static float CalculateKeyboardOcclusion(float nativeKeyboardHeight, float nativeFullHeight, float unityFullHeight, float unityCurrentHeight)
        {
            if (nativeFullHeight <= 0) return 0;
            float keyboardPixels = nativeKeyboardHeight * unityFullHeight / nativeFullHeight;
            return Mathf.Clamp(keyboardPixels - Mathf.Max(0, unityFullHeight - unityCurrentHeight), 0, unityCurrentHeight);
        }

#if UNITY_EDITOR
        public void SetPreviewInsets(Rect safeArea, float keyboardHeight)
        {
            _previewSafeArea = safeArea;
            _previewKeyboardHeight = Mathf.Max(0, keyboardHeight);
            Refresh();
        }
        public void ClearPreviewInsets()
        {
            _previewSafeArea = null; _previewKeyboardHeight = 0;
            Refresh();
        }
#endif

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _poll.Pause();
            _root.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _root.UnregisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            _root.UnregisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            _root.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            LayoutChanged = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            ReleaseAndroid();
#endif
        }
    }
}
