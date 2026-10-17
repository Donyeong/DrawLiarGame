using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace DrawLiar
{
    [DefaultExecutionOrder(-32000)]
    public sealed class DrawWebTypography : MonoBehaviour
    {
        [SerializeField] private FontAsset _font;
        [SerializeField] private FontAsset _languageNamesFont;
        [SerializeField] private PanelSettings _panel;

#if UNITY_EDITOR
        public void Configure(FontAsset font, FontAsset languageNamesFont, PanelSettings panel)
        {
            _font = font;
            _languageNamesFont = languageNamesFont;
            _panel = panel;
        }
#endif

        private void Awake()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            DrawLocalizedTypography.SetBrowserFont(_font, _languageNamesFont);
            DrawLocalizedTypography.SetBrowserPanel(_panel);
#endif
        }
    }
}
