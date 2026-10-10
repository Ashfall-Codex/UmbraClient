using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using System.Numerics;

namespace UmbraSync.UI.Components;


public static class ProfileBanner
{
    public const float MaxBytes = 1.5f * 1024 * 1024;

    private const int Cols = 28;
    private const int Rows = 8;
    private const float ImageOpacity = 0.55f;
    private const float ClearRadius = 0.5f;
    private const float VerticalBias = 0.35f;

    private static readonly uint[] ShadeGrid = new uint[(Cols + 1) * (Rows + 1)];

    public static void Draw(ImDrawListPtr drawList, IDalamudTextureWrap texture,
        Vector2 min, Vector2 max, Vector4 background, float rounding)
    {
        var size = max - min;
        if (size.X <= 1f || size.Y <= 1f || texture.Width == 0 || texture.Height == 0) return;

        var (uv0, uv1) = CoverUv(texture.Width, texture.Height, size.X, size.Y);
        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, ImageOpacity));
        drawList.AddImageRounded(texture.Handle, min, max, uv0, uv1, tint, rounding);

        FadeEdges(drawList, min, size, background with { W = 1f }, rounding);
    }

    public static (Vector2 Uv0, Vector2 Uv1) CoverUv(float texWidth, float texHeight, float boxWidth, float boxHeight)
    {
        var texRatio = texWidth / texHeight;
        var boxRatio = boxWidth / boxHeight;

        if (texRatio > boxRatio)
        {
            var visible = boxRatio / texRatio;
            var offset = (1f - visible) * 0.5f;
            return (new Vector2(offset, 0f), new Vector2(offset + visible, 1f));
        }

        var visibleV = texRatio / boxRatio;
        var offsetV = (1f - visibleV) * VerticalBias;
        return (new Vector2(0f, offsetV), new Vector2(1f, offsetV + visibleV));
    }

    private static void FadeEdges(ImDrawListPtr drawList, Vector2 pos, Vector2 size, Vector4 background, float rounding)
    {
        var half = size * 0.5f;
        if (half.X <= rounding + 2f || half.Y <= rounding + 2f) return;

        var solidRadius = MathF.Min(1f - (rounding / half.X), 1f - (rounding / half.Y));
        if (solidRadius <= ClearRadius) return;

        var center = pos + half;
        var inner = pos + new Vector2(rounding, rounding);
        var innerSize = size - new Vector2(rounding * 2f, rounding * 2f);

        for (var r = 0; r <= Rows; r++)
        {
            for (var c = 0; c <= Cols; c++)
            {
                var point = inner + new Vector2(innerSize.X * c / Cols, innerSize.Y * r / Rows);
                var offset = point - center;
                var distance = new Vector2(offset.X / half.X, offset.Y / half.Y).Length();
                var t = Math.Clamp((distance - ClearRadius) / (solidRadius - ClearRadius), 0f, 1f);
                var opacity = t * t * (3f - 2f * t);
                ShadeGrid[(r * (Cols + 1)) + c] = ImGui.GetColorU32(background with { W = opacity });
            }
        }

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var top = r * (Cols + 1);
                var bottom = (r + 1) * (Cols + 1);
                var tl = ShadeGrid[top + c];
                var tr = ShadeGrid[top + c + 1];
                var br = ShadeGrid[bottom + c + 1];
                var bl = ShadeGrid[bottom + c];

                if (((tl | tr | br | bl) & 0xFF000000u) == 0u) continue;

                var cellMin = inner + new Vector2(innerSize.X * c / Cols, innerSize.Y * r / Rows);
                var cellMax = inner + new Vector2(innerSize.X * (c + 1) / Cols, innerSize.Y * (r + 1) / Rows);
                drawList.AddRectFilledMultiColor(cellMin, cellMax, tl, tr, br, bl);
            }
        }

        var offsetHalf = new Vector2(rounding * 0.5f, rounding * 0.5f);
        drawList.AddRect(pos + offsetHalf, pos + size - offsetHalf,
            ImGui.GetColorU32(background), rounding * 0.5f, ImDrawFlags.None, rounding);
    }
}
