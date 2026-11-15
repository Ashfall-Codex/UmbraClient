using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UmbraSync.UI.Components;

public static unsafe class WindowOverlap
{
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Footprint(Vector2 Min, Vector2 Max, float TitleBarHeight, int FocusOrder, bool Resizable);

    private const int TitleBarColorCount = 3;
    private static readonly List<(Vector2 Min, Vector2 Max)> ScratchRegions = [];

    public static float TitleBarHeightOf(ImGuiWindowPtr window)
        => (window.Flags & ImGuiWindowFlags.NoTitleBar) != ImGuiWindowFlags.None ? 0f : ImGui.GetFrameHeight();

    public static bool IsBackdropCandidate(ImGuiWindowPtr window)
        => window.Handle != null
           && (window.Flags & (ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs)) == ImGuiWindowFlags.None;

    public static bool TryRead(ImGuiWindowPtr window, float titleBarHeight, out Footprint live)
    {
        live = default;
        if (!IsBackdropCandidate(window)) return false;
        if (!(window.Active || window.WasActive) || window.Hidden) return false;
        if (window.DockIsActive && !window.DockTabIsVisible) return false;

        var min = window.Pos;
        var max = min + window.Size;
        if (max.X <= min.X || max.Y <= min.Y) return false;
        live = new Footprint(min, max, titleBarHeight, window.FocusOrder,
            !window.Collapsed && (window.Flags & ImGuiWindowFlags.NoResize) == ImGuiWindowFlags.None);
        return true;
    }

    private static bool TryReadBelow(ImGuiWindowPtr window, ImGuiWindowPtr ownWindow, int ownFocusOrder, out Footprint below)
    {
        below = default;
        if (window.Handle == ownWindow.Handle) return false;
        return TryRead(window, 0f, out below) && below.FocusOrder < ownFocusOrder;
    }

    public static bool IsTitleBarOverlapped(ImGuiWindowPtr ownWindow, float titleBarHeight)
    {
        if (!TryRead(ownWindow, titleBarHeight, out var own) || own.TitleBarHeight <= 0f) return false;

        var titleMin = own.Min;
        var titleMax = new Vector2(own.Max.X, MathF.Min(own.Max.Y, own.Min.Y + own.TitleBarHeight));
        ref var order = ref ImGui.GetCurrentContext().WindowsFocusOrder;
        int count = Math.Min(order.Size, own.FocusOrder);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadBelow(order[i], ownWindow, own.FocusOrder, out var below)) continue;
            if (MathF.Min(titleMax.X, below.Max.X) > MathF.Max(titleMin.X, below.Min.X)
                && MathF.Min(titleMax.Y, below.Max.Y) > MathF.Max(titleMin.Y, below.Min.Y))
                return true;
        }
        return false;
    }

    public static bool IsTitleBarOverlapped(string windowName)
    {
        var window = ImGuiP.FindWindowByName(windowName);
        return window.Handle != null && IsTitleBarOverlapped(window, TitleBarHeightOf(window));
    }

    public static void Collect(ImGuiWindowPtr ownWindow, float titleBarHeight, List<(Vector2 Min, Vector2 Max)> regions, out Footprint own)
    {
        regions.Clear();
        if (!TryRead(ownWindow, titleBarHeight, out own)) return;

        float border = ImGui.GetStyle().WindowBorderSize;
        var areaMin = new Vector2(own.Min.X + border, own.Min.Y + MathF.Max(border, own.TitleBarHeight));
        var areaMax = own.Max - new Vector2(border);
        if (areaMax.X <= areaMin.X || areaMax.Y <= areaMin.Y) return;

        float grip = own.Resizable ? MathF.Floor(ImGui.GetFontSize() * 1.35f) : 0f;
        var cornerMin = own.Max - new Vector2(grip);
        var cornerMax = own.Max;
        ref var order = ref ImGui.GetCurrentContext().WindowsFocusOrder;
        int count = Math.Min(order.Size, own.FocusOrder);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadBelow(order[i], ownWindow, own.FocusOrder, out var below)) continue;
            var min = Vector2.Max(areaMin, below.Min);
            var max = Vector2.Min(areaMax, below.Max);
            if (max.X <= min.X || max.Y <= min.Y) continue;
            AddExcludingCorner(regions, min, max, cornerMin, cornerMax);
        }
    }

    private static void AddExcludingCorner(List<(Vector2 Min, Vector2 Max)> regions, Vector2 min, Vector2 max, Vector2 cornerMin, Vector2 cornerMax)
    {
        bool hitsCorner = cornerMax.X > cornerMin.X && max.X > cornerMin.X && min.X < cornerMax.X && max.Y > cornerMin.Y && min.Y < cornerMax.Y;
        if (!hitsCorner)
        {
            regions.Add((min, max));
            return;
        }

        if (cornerMin.Y > min.Y)
            regions.Add((min, new Vector2(max.X, cornerMin.Y)));
        float bandTop = MathF.Max(min.Y, cornerMin.Y);
        if (cornerMin.X > min.X && max.Y > bandTop)
            regions.Add((new Vector2(min.X, bandTop), new Vector2(cornerMin.X, max.Y)));
    }

    public static void DrawBackdrop(in Footprint own, IReadOnlyList<(Vector2 Min, Vector2 Max)> regions)
    {
        if (regions.Count == 0) return;

        var style = ImGui.GetStyle();
        float border = style.WindowBorderSize;
        var shapeMin = own.Min + new Vector2(border);
        var shapeMax = own.Max - new Vector2(border);
        float rounding = MathF.Max(0f, style.WindowRounding - border);
        var corners = own.TitleBarHeight > 0f ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.RoundCornersAll;
        uint color = ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, 1f));
        var drawList = ImGui.GetWindowDrawList();

        foreach (var (min, max) in regions)
        {
            drawList.PushClipRect(min, max, false);
            drawList.AddRectFilled(shapeMin, shapeMax, color, rounding, corners);
            drawList.PopClipRect();
        }
    }

    // Fenêtres ouvertes par un ImGui.Begin brut : à appeler juste après Begin, avant le contenu.
    public static void DrawForCurrentWindow()
    {
        var window = ImGuiP.GetCurrentWindow();
        if (!IsBackdropCandidate(window)) return;
        Collect(window, TitleBarHeightOf(window), ScratchRegions, out var own);
        DrawBackdrop(own, ScratchRegions);
        ScratchRegions.Clear();
    }

    // À encadrer autour de Begin/End : la barre de titre devient opaque dès qu'elle recouvre une autre fenêtre.
    public static void PushTitleBarColors(string windowName)
    {
        float glass = UiSharedService.GlassAlpha(UiSharedService.ResolveGlassLevel(UiSharedService.GlassLevel.Regular));
        float titleGlass = IsTitleBarOverlapped(windowName) ? 1f : glass;
        var color = UiSharedService.WithAlpha(UiSharedService.ThemeTitleBar, titleGlass);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, color);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, color);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, color);
    }

    public static void PopTitleBarColors() => ImGui.PopStyleColor(TitleBarColorCount);
}
