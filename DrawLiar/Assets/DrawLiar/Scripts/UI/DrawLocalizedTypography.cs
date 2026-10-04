using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.TextCore.Text;
#endif

namespace DrawLiar
{
    public static class DrawLocalizedTypography
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private static FontAsset _browserFont;
        private static PanelSettings _browserPanel;
#endif

        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            root.style.unityTextGenerator = TextGeneratorType.Advanced;
#if UNITY_WEBGL && !UNITY_EDITOR
            ApplyBrowserFont(root);
#endif
            root.languageDirection = DrawLocalization.IsRightToLeft ? LanguageDirection.RTL : LanguageDirection.LTR;
            if (root.panel == null)
            {
                root.RegisterCallback<AttachToPanelEvent>(OnAttached);
                return;
            }
            // Dropdown 팝업은 UIDocument 바깥의 패널 루트에 붙는다.
            var panelRoot = root.panel.visualTree;
            panelRoot.style.unityTextGenerator = TextGeneratorType.Advanced;
#if UNITY_WEBGL && !UNITY_EDITOR
            ApplyBrowserFont(panelRoot);
#endif
            panelRoot.languageDirection = root.languageDirection;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        public static void SetBrowserFont(FontAsset font)
        {
            if (font == null) throw new System.InvalidOperationException("웹 UI 폰트가 없습니다.");
            _browserFont = font;
        }

        public static void SetBrowserPanel(PanelSettings panel)
        {
            if (panel == null) throw new System.InvalidOperationException("웹 UI 패널이 없습니다.");
            _browserPanel = panel;
        }

        public static PanelSettings CreateBrowserPanel()
        {
            if (_browserPanel == null) throw new System.InvalidOperationException("웹 UI 패널이 없습니다.");
            return Object.Instantiate(_browserPanel);
        }

        private static void ApplyBrowserFont(VisualElement element)
        {
            if (_browserFont == null) throw new System.InvalidOperationException("웹 UI 폰트가 없습니다.");
            element.style.unityFontDefinition = FontDefinition.FromSDFFont(_browserFont);
        }
#endif

        private static void OnAttached(AttachToPanelEvent evt)
        {
            if (!(evt.currentTarget is VisualElement root)) return;
            root.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            Apply(root);
        }
    }
}
