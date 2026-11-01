using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace UmbraSync.UI.Components;

public enum CapsuleButtonRole
{
    Normal,
    Destructive,
}

public static class CapsuleButton
{
    private const float VisualHeight = 28f;
    private const float MinimumHitSize = 44f;
    private const float HorizontalPadding = 12f;
    private const float VerticalPadding = 4f;
    private const float IconSpacing = 6f;
    private const float PressedLabelOpacity = 0.7f;

    public static float HitRegionHeight => MathF.Max(MinimumHitSize * ImGuiHelpers.GlobalScale, CapsuleHeight());

    public static float MeasureWidth(string label, FontAwesomeIcon? icon = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float content = ImGui.CalcTextSize(label).X;
        if (icon is { } symbol) content += IconSize(symbol).X + IconSpacing * scale;
        return MathF.Max(MinimumHitSize * scale, content + HorizontalPadding * scale * 2f);
    }

    public static bool Draw(string label, FontAwesomeIcon? icon = null, CapsuleButtonRole role = CapsuleButtonRole.Normal)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = MeasureWidth(label, icon);
        float hitHeight = HitRegionHeight;
        float capsuleHeight = CapsuleHeight();

        var origin = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(label, new Vector2(width, hitHeight));
        bool hovered = ImGui.IsItemHovered();
        bool pressed = hovered && ImGui.IsItemActive();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var fill = pressed ? UiSharedService.ThemeCapsulePressed
            : hovered ? UiSharedService.ThemeCapsuleHovered
            : UiSharedService.ThemeCapsule;
        var border = hovered ? UiSharedService.ThemeCapsuleBorderHovered : UiSharedService.ThemeCapsuleBorder;
        var labelColor = role == CapsuleButtonRole.Destructive ? UiSharedService.ThemeDestructive : UiSharedService.ThemeCapsuleLabel;
        if (pressed) labelColor.W *= PressedLabelOpacity;

        var min = new Vector2(origin.X, MathF.Round(origin.Y + (hitHeight - capsuleHeight) / 2f));
        var max = min + new Vector2(width, capsuleHeight);
        float radius = capsuleHeight / 2f;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), radius);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), radius, ImDrawFlags.None, scale);

        var textSize = ImGui.CalcTextSize(label);
        float contentWidth = textSize.X;
        if (icon is { } measured) contentWidth += IconSize(measured).X + IconSpacing * scale;
        float x = MathF.Round(min.X + (width - contentWidth) / 2f);
        uint color = ImGui.GetColorU32(labelColor);

        if (icon is { } symbol)
        {
            var iconSize = IconSize(symbol);
            using (ImRaii.PushFont(UiBuilder.IconFont))
                drawList.AddText(new Vector2(x, MathF.Round(min.Y + (capsuleHeight - iconSize.Y) / 2f)), color, symbol.ToIconString());
            x += iconSize.X + IconSpacing * scale;
        }

        drawList.AddText(new Vector2(x, MathF.Round(min.Y + (capsuleHeight - textSize.Y) / 2f)), color, label);
        return clicked;
    }

    private static float CapsuleHeight()
        => MathF.Max(VisualHeight * ImGuiHelpers.GlobalScale, ImGui.GetTextLineHeight() + VerticalPadding * ImGuiHelpers.GlobalScale * 2f);

    private static Vector2 IconSize(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString());
    }
}
