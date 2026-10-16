using UnityEngine.UIElements;

namespace DrawLiar
{
    public sealed class DrawRedDot : VisualElement
    {
        public DrawRedDot()
        {
            AddToClassList("draw-red-dot");
            pickingMode = PickingMode.Ignore;
            focusable = false;
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
