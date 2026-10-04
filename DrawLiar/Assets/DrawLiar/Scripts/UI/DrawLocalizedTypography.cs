using UnityEngine;
using UnityEngine.UIElements;

namespace DrawLiar
{
    public static class DrawLocalizedTypography
    {
        public static void Apply(VisualElement root)
        {
            if (root == null) return;
            // 글리프는 기존 브랜드 폰트와 시스템 폴백을 사용하고, 복합 문자는 엔진에서 조형한다.
            root.style.unityTextGenerator = TextGeneratorType.Advanced;
            root.languageDirection = DrawLocalization.IsRightToLeft ? LanguageDirection.RTL : LanguageDirection.LTR;
            if (root.panel == null)
            {
                root.RegisterCallback<AttachToPanelEvent>(OnAttached);
                return;
            }
            // Dropdown 팝업은 UIDocument 바깥의 패널 루트에 붙는다.
            var panelRoot = root.panel.visualTree;
            panelRoot.style.unityTextGenerator = TextGeneratorType.Advanced;
            panelRoot.languageDirection = root.languageDirection;
        }

        private static void OnAttached(AttachToPanelEvent evt)
        {
            if (!(evt.currentTarget is VisualElement root)) return;
            root.UnregisterCallback<AttachToPanelEvent>(OnAttached);
            Apply(root);
        }
    }
}
