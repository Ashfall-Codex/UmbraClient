using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace UmbraSync.UI.Components;

/// <param name="Id">Identifiant de la page, ou -1 pour un intitulé de groupe.</param>
public readonly record struct SideRailEntry(int Id, string Label, FontAwesomeIcon Icon = FontAwesomeIcon.None,
    bool Enabled = true, string? DisabledTooltip = null)
{
    public bool IsGroup => Id < 0;

    public static SideRailEntry Group(string label) => new(-1, label);
}

// Navigation latérale à un seul niveau : intitulés de groupe, entrées icône + libellé, indicateur
// animé sur l'entrée active. Sous CollapseBelowWidth, elle se replie sur les icônes seules.
public sealed class SideRail
{
    public const float ExpandedWidth = 150f;
    public const float CollapsedWidth = 38f;
    public const float CollapseBelowWidth = 520f;

    private const float ButtonHeight = 24f;
    private const float AnimationSpeed = 18f;

    private Vector2 _indicatorPos;
    private Vector2 _indicatorSize;
    private Vector2 _windowPos;
    private bool _indicatorInit;
    private bool _wasCollapsed;

    public static float WidthFor(float availableWidth)
        => (availableWidth < CollapseBelowWidth * ImGuiHelpers.GlobalScale ? CollapsedWidth : ExpandedWidth) * ImGuiHelpers.GlobalScale;

    public void Draw(IReadOnlyList<SideRailEntry> entries, ref int active, bool collapsed, Vector4? indicatorColor = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        (Vector2 Min, Vector2 Max)? activeRect = null;
        bool first = true;

        ImGuiHelpers.ScaledDummy(4f);

        foreach (var entry in entries)
        {
            if (entry.IsGroup)
            {
                DrawGroup(entry.Label, collapsed, first);
                first = false;
                continue;
            }

            first = false;
            float width = ImGui.GetContentRegionAvail().X;
            float height = ButtonHeight * scale;
            var pos = ImGui.GetCursorScreenPos();

            ImGui.PushID(entry.Id);
            bool clicked = ImGui.InvisibleButton("##sideRailEntry", new Vector2(width, height));
            ImGui.PopID();
            bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            bool isActive = active == entry.Id;

            if (isActive) activeRect = (pos, pos + new Vector2(width, height));

            if (hovered && !isActive && entry.Enabled)
            {
                float pad = 2f * scale;
                drawList.AddRectFilled(pos - new Vector2(pad), pos + new Vector2(width + pad, height + pad),
                    ImGui.GetColorU32(UiSharedService.ThemeRailHovered), UiSharedService.RadiusCard * scale);
            }

            var color = !entry.Enabled ? UiSharedService.WithAlpha(UiSharedService.ThemeNavText, 0.4f)
                : isActive ? UiSharedService.ThemeNavTextActive
                : hovered ? UiSharedService.ThemeNavTextHovered
                : UiSharedService.ThemeNavText;
            uint colorU32 = ImGui.GetColorU32(color);

            string icon = entry.Icon.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            var iconSize = ImGui.CalcTextSize(icon);
            float iconX = collapsed ? pos.X + (width - iconSize.X) / 2f : pos.X + 8f * scale;
            drawList.AddText(new Vector2(iconX, pos.Y + (height - iconSize.Y) / 2f), colorU32, icon);
            ImGui.PopFont();

            if (!collapsed)
            {
                var labelSize = ImGui.CalcTextSize(entry.Label);
                drawList.AddText(new Vector2(pos.X + 30f * scale, pos.Y + (height - labelSize.Y) / 2f), colorU32, entry.Label);
            }

            if (hovered)
            {
                if (!entry.Enabled && !string.IsNullOrEmpty(entry.DisabledTooltip))
                    UiSharedService.AttachToolTip(entry.Label + UiSharedService.TooltipSeparator + entry.DisabledTooltip);
                else if (collapsed)
                    UiSharedService.AttachToolTip(entry.Label);

                if (entry.Enabled) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (clicked && entry.Enabled) active = entry.Id;

            ImGuiHelpers.ScaledDummy(1f);
        }

        drawList.ChannelsSetCurrent(0);
        DrawIndicator(drawList, activeRect, collapsed, indicatorColor ?? UiSharedService.AccentColor);
        drawList.ChannelsMerge();
    }

    private static void DrawGroup(string label, bool collapsed, bool first)
    {
        float scale = ImGuiHelpers.GlobalScale;
        if (!first) ImGuiHelpers.ScaledDummy(6f);

        if (collapsed)
        {
            if (first) return;
            var pos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(pos.X + width * 0.2f, pos.Y),
                new Vector2(pos.X + width * 0.8f, pos.Y),
                ImGui.GetColorU32(UiSharedService.ThemeSeparator), 1f * scale);
            ImGuiHelpers.ScaledDummy(5f);
            return;
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f * scale);
        ImGui.TextColored(UiSharedService.ThemeTextAccent, label);
        ImGuiHelpers.ScaledDummy(1f);
    }

    private void DrawIndicator(ImDrawListPtr drawList, (Vector2 Min, Vector2 Max)? activeRect, bool collapsed, Vector4 color)
    {
        if (activeRect is not { } rect) return;

        var windowPos = ImGui.GetWindowPos();
        var targetSize = rect.Max - rect.Min;

        // Fenêtre déplacée ou rail replié : l'indicateur saute à sa place au lieu de traverser l'écran.
        if (!_indicatorInit || _windowPos != windowPos || _wasCollapsed != collapsed)
        {
            _indicatorPos = rect.Min;
            _indicatorSize = targetSize;
            _indicatorInit = true;
            _windowPos = windowPos;
            _wasCollapsed = collapsed;
        }
        else
        {
            float t = 1f - MathF.Exp(-AnimationSpeed * ImGui.GetIO().DeltaTime);
            _indicatorPos = Vector2.Lerp(_indicatorPos, rect.Min, t);
            _indicatorSize = Vector2.Lerp(_indicatorSize, targetSize, t);
        }

        float pad = 2f * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(
            _indicatorPos - new Vector2(pad),
            _indicatorPos + _indicatorSize + new Vector2(pad),
            ImGui.GetColorU32(color),
            UiSharedService.RadiusCard * ImGuiHelpers.GlobalScale);
    }
}
