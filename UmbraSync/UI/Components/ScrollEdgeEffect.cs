using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UmbraSync.UI.Components;

public static unsafe class ScrollEdgeEffect
{
    private const float BandHeight = 28f;
    private const float MaxBandRatio = 0.25f;
    private const float EdgeOpacity = 0.96f;
    private const int MaxDepth = 32;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct Material(Vector3 Tint, float TintAlpha, float BlurOpacity);

    public static void DrawForCurrentWindow(UiSharedService.GlassLevel level, bool useBackdropBlur, int backgroundLayers)
    {
        var root = ImGuiP.GetCurrentWindow();
        if (root.Handle == null || root.Flags.HasFlag(ImGuiWindowFlags.NoBackground)) return;

        float glass = UiSharedService.GlassAlpha(UiSharedService.ResolveGlassLevel(level));
        var tint = UiSharedService.ThemeWindowBg;
        var material = new Material(
            new Vector3(tint.X, tint.Y, tint.Z),
            1f - MathF.Pow(1f - glass, backgroundLayers),
            useBackdropBlur && glass < 1f ? UiSharedService.BlurOpacity : 0f);

        DrawTree(root, material, 0);
    }

    private static void DrawTree(ImGuiWindowPtr window, in Material material, int depth)
    {
        if (depth > MaxDepth || !IsVisible(window)) return;
        if (window.ScrollMax.Y > 0f && !HasOwnBackground(window))
            DrawEdges(window, material);

        ref var children = ref window.DC.ChildWindows;
        for (var i = 0; i < children.Size; i++)
            DrawTree(children[i], material, depth + 1);
    }

    private static bool IsVisible(ImGuiWindowPtr window) => window.Active && !window.Hidden && !window.SkipItems;

    private static bool HasOwnBackground(ImGuiWindowPtr window)
    {
        if (!window.Flags.HasFlag(ImGuiWindowFlags.ChildWindow)) return false;
        if (window.WindowBorderSize > 0f) return true;

        var parent = window.ParentWindow;
        return window.Flags.HasFlag(ImGuiWindowFlags.NoMove) && parent.Handle != null && !parent.Flags.HasFlag(ImGuiWindowFlags.NoMove);
    }

    private static void DrawEdges(ImGuiWindowPtr window, in Material material)
    {
        var clip = window.InnerClipRect;
        float height = clip.Max.Y - clip.Min.Y;
        if (height <= 1f || clip.Max.X <= clip.Min.X) return;

        float band = MathF.Round(MathF.Min(BandHeight * ImGuiHelpers.GlobalScale, height * MaxBandRatio));
        if (band < 1f) return;

        float top = Math.Clamp(window.Scroll.Y / band, 0f, 1f);
        if (top > 0f)
        {
            var min = new Vector2(clip.Min.X, MathF.Round(clip.Min.Y));
            DrawBand(window, min, min + new Vector2(clip.Max.X - clip.Min.X, band), true, top, material);
        }

        float bottom = Math.Clamp((window.ScrollMax.Y - window.Scroll.Y) / band, 0f, 1f);
        if (bottom > 0f)
        {
            var min = new Vector2(clip.Min.X, MathF.Round(clip.Max.Y) - band);
            DrawBand(window, min, min + new Vector2(clip.Max.X - clip.Min.X, band), false, bottom, material);
        }
    }

    private static void DrawBand(ImGuiWindowPtr window, Vector2 min, Vector2 max, bool opensDownward, float strength, in Material material)
    {
        DrawRows(window.DrawList, min, max, min, max, opensDownward, strength, material);
        DrawOverDescendants(window, min, max, opensDownward, strength, material, 0);
    }

    private static void DrawOverDescendants(ImGuiWindowPtr window, Vector2 min, Vector2 max, bool opensDownward, float strength, in Material material, int depth)
    {
        if (depth > MaxDepth) return;

        ref var children = ref window.DC.ChildWindows;
        for (var i = 0; i < children.Size; i++)
        {
            ImGuiWindowPtr child = children[i];
            if (!IsVisible(child)) continue;

            var outer = child.OuterRectClipped;
            var clipMin = Vector2.Max(min, outer.Min);
            var clipMax = Vector2.Min(max, outer.Max);
            if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;

            DrawRows(child.DrawList, min, max, clipMin, clipMax, opensDownward, strength, material);
            DrawOverDescendants(child, min, max, opensDownward, strength, material, depth + 1);
        }
    }

    private static void DrawRows(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector2 clipMin, Vector2 clipMax, bool opensDownward, float strength, in Material material)
    {
        int rows = (int)(max.Y - min.Y);
        drawList.PushClipRect(clipMin, clipMax, false);

        if (material.BlurOpacity > 0f && UiSharedService.Backdrop is { Ready: true } backdrop)
        {
            for (var row = 0; row < rows; row++)
            {
                var rowMin = new Vector2(min.X, min.Y + row);
                backdrop.DrawInto(drawList, rowMin, new Vector2(max.X, rowMin.Y + 1f), RowOpacity(row, rows, opensDownward, strength) * material.BlurOpacity);
            }
        }

        for (var row = 0; row < rows; row++)
        {
            var rowMin = new Vector2(min.X, min.Y + row);
            var color = new Vector4(material.Tint, RowOpacity(row, rows, opensDownward, strength) * material.TintAlpha);
            drawList.AddRectFilled(rowMin, new Vector2(max.X, rowMin.Y + 1f), ImGui.GetColorU32(color));
        }

        drawList.PopClipRect();
    }

    private static float RowOpacity(int row, int rows, bool opensDownward, float strength)
    {
        float t = (row + 0.5f) / rows;
        if (!opensDownward) t = 1f - t;
        return strength * EdgeOpacity * (1f - t * t * (3f - 2f * t));
    }
}
