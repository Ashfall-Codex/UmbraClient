using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Globalization;
using System.Numerics;

namespace UmbraSync.UI.Components;

public static class SidebarControls
{
    public const float ButtonRounding = 6f;

    public static bool DrawButton(
        FontAwesomeIcon icon,
        string id,
        bool active,
        string tooltip,
        float size,
        int badge = 0,
        bool enabled = true,
        bool outlined = false,
        Vector4? hoverOverride = null,
        Vector4? iconColor = null)
    {
        var scaled = size * ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - scaled) / 2f));

        var idle = Vector4.Lerp(UiSharedService.ThemeButtonBg, UiSharedService.ThemeButtonHovered, 0.6f);
        var bg = active ? UiSharedService.AccentColor : idle;
        var hover = hoverOverride ?? (active ? UiSharedService.AccentColor : UiSharedService.ThemeButtonActive);

        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ButtonRounding * ImGuiHelpers.GlobalScale);

        if (!enabled) ImGui.BeginDisabled();
        if (iconColor.HasValue && enabled) ImGui.PushStyleColor(ImGuiCol.Text, iconColor.Value);
        bool clicked;
        ImGui.PushFont(UiBuilder.IconFont);
        clicked = ImGui.Button(icon.ToIconString() + id, new Vector2(scaled, scaled));
        ImGui.PopFont();
        if (iconColor.HasValue && enabled) ImGui.PopStyleColor();
        if (!enabled) ImGui.EndDisabled();

        if (outlined)
        {
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.45f }),
                ButtonRounding * ImGuiHelpers.GlobalScale,
                ImDrawFlags.None,
                1f * ImGuiHelpers.GlobalScale);
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        if (badge > 0)
            DrawBadge(badge);

        UiSharedService.AttachToolTip(tooltip);
        ImGuiHelpers.ScaledDummy(4f);

        return clicked && enabled;
    }

    public static void DrawSeparator(float buttonSize)
    {
        ImGuiHelpers.ScaledDummy(2f);

        var width = buttonSize * 0.62f * ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var start = cursor with { X = cursor.X + (avail - width) / 2f };

        ImGui.GetWindowDrawList().AddLine(
            start,
            start with { X = start.X + width },
            ImGui.GetColorU32(UiSharedService.ThemeSeparator),
            1f * ImGuiHelpers.GlobalScale);

        ImGuiHelpers.ScaledDummy(6f);
    }

    private static void DrawBadge(int count)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        var radius = 7f * ImGuiHelpers.GlobalScale;
        var center = new Vector2(max.X - radius * 0.7f, min.Y + radius * 0.7f);

        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, radius, ImGui.GetColorU32(UiSharedService.AccentColor));

        var label = count > 9 ? "9+" : count.ToString(CultureInfo.CurrentCulture);
        var textSz = ImGui.CalcTextSize(label);
        dl.AddText(center - textSz / 2f, ImGui.GetColorU32(UiSharedService.ThemeNavTextActive), label);
    }
}
