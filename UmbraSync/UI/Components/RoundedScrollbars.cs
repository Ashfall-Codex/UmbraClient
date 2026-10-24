using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace UmbraSync.UI.Components;

public static unsafe class RoundedScrollbars
{
    private const int MaxDepth = 32;

    public static void DrawForCurrentWindow()
    {
        var root = ImGuiP.GetCurrentWindow();
        if (root.Handle == null) return;
        DrawTree(root, 0);

        ref var popups = ref ImGui.GetCurrentContext().OpenPopupStack;
        for (var i = 0; i < popups.Size; i++)
        {
            var popup = new ImGuiWindowPtr(popups[i].Window);
            if (popup.Handle != null && IsOpenedFrom(popup, root))
                DrawTree(popup, 0);
        }
    }

    private static bool IsOpenedFrom(ImGuiWindowPtr popup, ImGuiWindowPtr root)
    {
        var parent = popup.ParentWindow;
        for (var depth = 0; parent.Handle != null && depth < MaxDepth; depth++)
        {
            if (parent.Handle == root.Handle) return true;
            parent = parent.ParentWindow;
        }
        return false;
    }

    private static void DrawTree(ImGuiWindowPtr window, int depth)
    {
        if (depth > MaxDepth || !window.Active || window.Hidden || window.SkipItems) return;
        if (window.ScrollbarY) Draw(window, ImGuiAxis.Y);
        if (window.ScrollbarX) Draw(window, ImGuiAxis.X);

        ref var children = ref window.DC.ChildWindows;
        for (var i = 0; i < children.Size; i++)
            DrawTree(children[i], depth + 1);
    }

    private static void Draw(ImGuiWindowPtr window, ImGuiAxis axis)
    {
        bool vertical = axis == ImGuiAxis.Y;
        var frame = ImGuiP.GetWindowScrollbarRect(window, axis);
        float frameWidth = frame.Max.X - frame.Min.X;
        float frameHeight = frame.Max.Y - frame.Min.Y;
        if (frameWidth <= 0f || frameHeight <= 0f) return;

        var style = ImGui.GetStyle();
        float alpha = 1f;
        float fontSize = ImGui.GetFontSize();
        if (vertical && frameHeight < fontSize + (style.FramePadding.Y * 2f))
            alpha = Clamp((frameHeight - fontSize) / (style.FramePadding.Y * 2f), 0f, 1f);
        if (alpha <= 0f) return;

        var inset = new Vector2(Clamp(MathF.Floor((frameWidth - 2f) * 0.5f), 0f, 3f), Clamp(MathF.Floor((frameHeight - 2f) * 0.5f), 0f, 3f));
        var areaMin = frame.Min + inset;
        var areaMax = frame.Max - inset;
        float lengthV = vertical ? areaMax.Y - areaMin.Y : areaMax.X - areaMin.X;
        if (lengthV <= 0f) return;

        float available = MathF.Truncate(vertical ? window.InnerRect.Max.Y - window.InnerRect.Min.Y : window.InnerRect.Max.X - window.InnerRect.Min.X);
        float contents = MathF.Truncate(vertical
            ? window.ContentSize.Y + (window.WindowPadding.Y * 2f)
            : window.ContentSize.X + (window.WindowPadding.X * 2f));
        float scroll = MathF.Truncate(vertical ? window.Scroll.Y : window.Scroll.X);
        float total = MathF.Max(MathF.Max(contents, available), 1f);
        float grabLength = Clamp(lengthV * (available / total), style.GrabMinSize, lengthV);
        float scrollMax = MathF.Max(1f, contents - available);
        float grabOffset = Clamp(scroll / scrollMax, 0f, 1f) * (lengthV - grabLength);

        var grabMin = vertical
            ? new Vector2(areaMin.X, areaMin.Y + grabOffset)
            : new Vector2(areaMin.X + grabOffset, areaMin.Y);
        var grabMax = vertical
            ? new Vector2(areaMax.X, grabMin.Y + grabLength)
            : new Vector2(grabMin.X + grabLength, areaMax.Y);

        uint id = ImGuiP.GetWindowScrollbarID(window, axis);
        var grabColor = ImGuiP.GetActiveID() == id ? UiSharedService.ThemeScrollbarActive
            : ImGuiP.GetHoveredID() == id ? UiSharedService.ThemeScrollbarHover
            : UiSharedService.ThemeScrollbarGrab;

        var trackMax = frame.Max - (vertical ? new Vector2(window.WindowBorderSize, 0f) : new Vector2(0f, window.WindowBorderSize));
        var drawList = window.DrawList;
        drawList.PushClipRect(window.OuterRectClipped.Min, window.OuterRectClipped.Max, false);
        drawList.AddRectFilled(frame.Min, trackMax, ImGui.GetColorU32(UiSharedService.ThemeScrollbarBg),
            PillRounding(frame.Min, trackMax));
        drawList.AddRectFilled(grabMin, grabMax, ImGui.GetColorU32(grabColor with { W = grabColor.W * alpha }),
            PillRounding(grabMin, grabMax));
        drawList.PopClipRect();
    }

    private static float PillRounding(Vector2 min, Vector2 max) => MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f;

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
}
