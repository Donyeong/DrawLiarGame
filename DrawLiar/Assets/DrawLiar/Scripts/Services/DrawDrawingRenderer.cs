using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DrawLiar
{
    public static class DrawDrawingRenderer
    {
        public const int WIDTH = 1200;
        public const int HEIGHT = 800;
        public static readonly Color32 PaperColor = new Color32(255, 255, 255, 255);

        private struct RasterStroke
        {
            public Vector2 A, B;
            public float StartRadius, EndRadius;
            public int Steps;
            public Color32 Color;
        }

        public static Texture2D Render(IReadOnlyList<DrawStroke> strokes, int? authorPlayerId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = CreatePaper();
            if (strokes != null)
            {
                for (int index = 0; index < strokes.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stroke = strokes[index];
                    if (!authorPlayerId.HasValue || stroke.AuthorPlayerId == authorPlayerId.Value)
                        Rasterize(stroke, pixels);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CreateTexture(pixels);
        }

        public static async Task<Texture2D> RenderAsync(IReadOnlyList<DrawStroke> strokes, int? authorPlayerId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Color32[] pixels = CreatePaper();
            var budget = Stopwatch.StartNew();
            try
            {
                if (strokes != null)
                {
                    for (int index = 0; index < strokes.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var stroke = strokes[index];
                        if (authorPlayerId.HasValue && stroke.AuthorPlayerId != authorPlayerId.Value) continue;
                        if (!TryPrepareStroke(stroke, 0, out var raster)) continue;
                        for (int step = 0; step <= raster.Steps; step++)
                        {
                            RasterizeStep(raster, pixels, step);
                            if ((step & 7) != 0 || budget.Elapsed.TotalMilliseconds < 8) continue;
                            cancellationToken.ThrowIfCancellationRequested();
                            await Task.Yield();
                            cancellationToken.ThrowIfCancellationRequested();
                            budget.Restart();
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return CreateTexture(pixels);
            }
            finally { pixels = null; }
        }

        private static Color32[] CreatePaper()
        {
            var pixels = new Color32[WIDTH * HEIGHT];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = PaperColor;
            return pixels;
        }

        private static Texture2D CreateTexture(Color32[] pixels)
        {
            var texture = new Texture2D(WIDTH, HEIGHT, TextureFormat.RGBA32, false)
            {
                name = "DrawLiarDrawingReplay",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false);
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        public static void Rasterize(DrawStroke stroke, Color32[] target, float expansion = 0)
        {
            if (target == null || target.Length != WIDTH * HEIGHT) throw new ArgumentException(nameof(target));
            if (!TryPrepareStroke(stroke, expansion, out var raster)) return;
            for (int step = 0; step <= raster.Steps; step++) RasterizeStep(raster, target, step);
        }

        private static bool TryPrepareStroke(DrawStroke stroke, float expansion, out RasterStroke raster)
        {
            raster = default;
            if (!IsFinite(stroke.X1) || !IsFinite(stroke.Y1) || !IsFinite(stroke.X2) || !IsFinite(stroke.Y2)
                || !IsFinite(stroke.StartSize) || !IsFinite(stroke.Size) || !IsFinite(expansion)) return false;
            var a = new Vector2(Mathf.Clamp01(stroke.X1) * WIDTH, (1 - Mathf.Clamp01(stroke.Y1)) * HEIGHT);
            var b = new Vector2(Mathf.Clamp01(stroke.X2) * WIDTH, (1 - Mathf.Clamp01(stroke.Y2)) * HEIGHT);
            float startSize = stroke.StartSize > 0 ? stroke.StartSize : stroke.Size;
            float startRadius = Mathf.Clamp(startSize * WIDTH * .5f, 1, 48) + expansion;
            float endRadius = Mathf.Clamp(stroke.Size * WIDTH * .5f, 1, 48) + expansion;
            raster = new RasterStroke
            {
                A = a,
                B = b,
                StartRadius = startRadius,
                EndRadius = endRadius,
                Steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(1, Mathf.Min(startRadius, endRadius) * .4f))),
                Color = expansion > 0 ? new Color32(120, 103, 158, 255)
                    : stroke.Eraser ? PaperColor : new Color32(stroke.R, stroke.G, stroke.B, 255)
            };
            return true;
        }

        private static void RasterizeStep(RasterStroke raster, Color32[] target, int step)
        {
            float progress = step / (float)raster.Steps;
            var point = Vector2.Lerp(raster.A, raster.B, progress);
            float radius = Mathf.Lerp(raster.StartRadius, raster.EndRadius, progress);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(point.x - radius - 1));
            int x1 = Mathf.Min(WIDTH - 1, Mathf.CeilToInt(point.x + radius + 1));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(point.y - radius - 1));
            int y1 = Mathf.Min(HEIGHT - 1, Mathf.CeilToInt(point.y + radius + 1));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), point);
                float alpha = Mathf.Clamp01(radius + .5f - distance);
                if (alpha <= 0) continue;
                int index = y * WIDTH + x;
                target[index] = Color32.Lerp(target[index], raster.Color, alpha);
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
