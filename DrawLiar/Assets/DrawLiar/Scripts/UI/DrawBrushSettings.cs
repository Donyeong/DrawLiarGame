using System.Collections.Generic;
using UnityEngine;

namespace DrawLiar
{
    public static class DrawBrushSettings
    {
        private const string PRESSURE_KEY = "DrawLiar.PressureEnabled";
        public const int BASE_COLOR_COUNT = 9;
        private static readonly Color32[] _palette =
        {
            new Color32(40,43,39,255), new Color32(225,127,103,255), new Color32(228,182,107,255),
            new Color32(246,241,230,255), DrawingSurface.PaperColor, new Color32(83,110,130,255),
            new Color32(166,171,159,255), new Color32(35,124,98,255), new Color32(157,147,219,255),
            new Color32(0,0,0,255), new Color32(92,92,92,255), new Color32(193,193,193,255),
            new Color32(219,46,57,255), new Color32(245,105,72,255), new Color32(249,168,75,255),
            new Color32(255,216,75,255), new Color32(134,193,87,255), new Color32(45,151,91,255),
            new Color32(35,174,164,255), new Color32(82,190,222,255), new Color32(50,121,208,255),
            new Color32(46,64,145,255), new Color32(108,77,168,255), new Color32(179,79,176,255),
            new Color32(230,94,150,255), new Color32(245,170,188,255), new Color32(243,201,162,255),
            new Color32(168,113,76,255), new Color32(107,70,58,255), new Color32(190,226,174,255),
            new Color32(188,225,242,255), new Color32(213,197,239,255)
        };

        public static IReadOnlyList<Color32> Palette => _palette;
        public static bool PressureEnabled => PlayerPrefs.GetInt(PRESSURE_KEY, 1) != 0;

        public static void SetPressureEnabled(bool enabled)
        {
            PlayerPrefs.SetInt(PRESSURE_KEY, enabled ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static float ResolveSize(float brushSize, bool enabled, bool pen, float pressure, bool eraser)
        {
            if (!enabled || !pen || eraser || float.IsNaN(pressure) || float.IsInfinity(pressure) || pressure <= 0)
                return brushSize;
            return Mathf.Max(.001f, brushSize * Mathf.Clamp(pressure, .2f, 1f));
        }
    }
}
