using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public static class DrawLocalizedTypography
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private static FontAsset _browserFont;
        private static FontAsset _browserLanguageNamesFont;
        private static PanelSettings _browserPanel;
#else
        private static FontAsset _defaultFont;
        private static FontAsset _languageNamesFont;
        private static FontAsset _scriptFont;
        private static Font _scriptSource;
        private static string _scriptResource;
#endif

        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            root.style.unityTextGenerator = TextGeneratorType.Advanced;
            ApplyFont(root);
            root.languageDirection = DrawLocalization.IsRightToLeft ? LanguageDirection.RTL : LanguageDirection.LTR;
            if (root.panel == null)
            {
                root.RegisterCallback<AttachToPanelEvent>(OnAttached);
                return;
            }
            // Dropdown 팝업은 UIDocument 바깥의 패널 루트에 붙는다.
            var panelRoot = root.panel.visualTree;
            panelRoot.style.unityTextGenerator = TextGeneratorType.Advanced;
            ApplyFont(panelRoot);
            panelRoot.languageDirection = root.languageDirection;
        }

        public static void ApplyLanguageNames(VisualElement element)
        {
            if (element == null) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_browserLanguageNamesFont == null) throw new System.InvalidOperationException("웹 언어 이름 폰트가 없습니다.");
            var font = _browserLanguageNamesFont;
#else
            if (_languageNamesFont == null)
            {
                _languageNamesFont = CreateFont("DrawLiar/Fonts/DrawLiarLanguageNames", 512);
                _languageNamesFont.fallbackFontAssetTable = new List<FontAsset> { GetBaseFont() };
            }
            var font = _languageNamesFont;
#endif
            element.style.unityTextGenerator = TextGeneratorType.Advanced;
            element.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        public static void SetBrowserFont(FontAsset font, FontAsset languageNamesFont)
        {
            if (font == null) throw new System.InvalidOperationException("웹 UI 폰트가 없습니다.");
            if (languageNamesFont == null) throw new System.InvalidOperationException("웹 언어 이름 폰트가 없습니다.");
            _browserFont = font;
            _browserLanguageNamesFont = languageNamesFont;
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

        private static FontAsset GetDefaultFont()
        {
            if (_browserFont == null) throw new System.InvalidOperationException("웹 UI 폰트가 없습니다.");
            return _browserFont;
        }
#else
        private static FontAsset GetDefaultFont()
        {
            var defaultFont = GetBaseFont();
            string resource = DrawLocalization.CurrentLanguageCode switch
            {
                "iw" => "DrawLiar/Fonts/NotoSansHebrew-Regular",
                "th" => "DrawLiar/Fonts/NotoSansThai-Regular",
                _ => null
            };
            if (_scriptResource == resource) return _scriptFont != null ? _scriptFont : defaultFont;
            if (_scriptFont != null)
            {
                DestroyGenerated(_scriptFont);
                _scriptFont = null;
            }
            if (_scriptSource != null) Resources.UnloadAsset(_scriptSource);
            _scriptSource = null;
            _scriptResource = resource;
            if (resource != null)
            {
                _scriptFont = CreateFont(resource, 512);
                _scriptSource = _scriptFont.sourceFontFile;
                _scriptFont.fallbackFontAssetTable = new List<FontAsset> { defaultFont };
            }
            return _scriptFont != null ? _scriptFont : defaultFont;
        }

        private static FontAsset GetBaseFont()
        {
            if (_defaultFont == null) _defaultFont = CreateFont("DrawLiar/Pretendard-Regular", 1024);
            return _defaultFont;
        }

        private static void DestroyGenerated(Object asset)
        {
            if (Application.isPlaying) Object.Destroy(asset);
            else Object.DestroyImmediate(asset);
        }

        private static FontAsset CreateFont(string resource, int atlasSize)
        {
            var source = Resources.Load<Font>(resource);
            if (source == null) throw new System.InvalidOperationException("UI 폰트를 찾을 수 없습니다: " + resource);
            var font = FontAsset.CreateFontAsset(source, 90, 9, GlyphRenderMode.SDFAA, atlasSize, atlasSize,
                AtlasPopulationMode.Dynamic, true);
            if (font == null) throw new System.InvalidOperationException("UI 폰트를 만들 수 없습니다: " + resource);
            font.name = source.name;
            font.fallbackFontAssetTable = new List<FontAsset>();
            return font;
        }
#endif

        private static void ApplyFont(VisualElement element)
        {
            element.style.unityFontDefinition = FontDefinition.FromSDFFont(GetDefaultFont());
        }

        private static void OnAttached(AttachToPanelEvent evt)
        {
            if (!(evt.currentTarget is VisualElement root)) return;
            root.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            Apply(root);
        }
    }
}
