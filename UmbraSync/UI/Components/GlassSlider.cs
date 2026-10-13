using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;

namespace UmbraSync.UI.Components;
public static class GlassSlider
{
    public const int Notches = 41;
    private const int MajorEvery = 4;
    private static float DragStartValue;

    public static bool Draw(string id, ref float value, float width, string? tooltip = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();

        var iconSize = 14f * scale;
        var gap = 10f * scale;
        var knobRadius = 7f * scale;
        var trackThickness = 4f * scale;
        var tickGap = 6f * scale;
        var height = knobRadius * 2f + tickGap + 3f * scale;

        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + knobRadius;

        var muted = ImGui.GetColorU32(UiSharedService.ThemeNavText);
        DrawGlassIcon(dl, new Vector2(origin.X, centerY - iconSize * 0.5f), iconSize, muted, filled: false);

        var trackStart = origin.X + iconSize + gap + knobRadius;
        var trackEnd = origin.X + width - iconSize - gap - knobRadius;
        var trackLength = Math.Max(1f, trackEnd - trackStart);

        ImGui.SetCursorScreenPos(new Vector2(trackStart - knobRadius, origin.Y));
        ImGui.InvisibleButton(id, new Vector2(trackLength + knobRadius * 2f, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        if (ImGui.IsItemActivated())
            DragStartValue = value;

        if (active)
        {
            var raw = Math.Clamp((ImGui.GetIO().MousePos.X - trackStart) / trackLength, 0f, 1f);
            value = Snap(raw);
        }
        var released = ImGui.IsItemDeactivated() && Math.Abs(value - DragStartValue) > 0.0001f;

        value = Math.Clamp(value, 0f, 1f);
        var knobX = trackStart + value * trackLength;
        var half = trackThickness * 0.5f;
        var rest = ImGui.GetColorU32(UiSharedService.ThemeButtonBg with { W = 0.85f });
        var fill = ImGui.GetColorU32(UiSharedService.AccentColor);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(trackEnd, centerY + half), rest, half);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(knobX, centerY + half), fill, half);
        var tickY = centerY + knobRadius + tickGap * 0.5f;
        for (var i = 0; i < Notches; i++)
        {
            var t = i / (float)(Notches - 1);
            var x = trackStart + t * trackLength;
            var current = Math.Abs(t - value) < 0.001f;
            var major = i % MajorEvery == 0;
            var color = current ? UiSharedService.ThemeNavTextActive
                : major ? ImGuiColors.DalamudGrey3
                : ImGuiColors.DalamudGrey3 with { W = 0.55f };
            var dot = current ? 1.6f : major ? 1.1f : 0.7f;
            dl.AddCircleFilled(new Vector2(x, tickY), dot * scale, ImGui.GetColorU32(color), 8);
        }

        var radius = knobRadius * (active ? 1.08f : hovered ? 1.04f : 1f);
        dl.AddCircleFilled(new Vector2(knobX, centerY + 1f * scale), radius, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 24);
        dl.AddCircleFilled(new Vector2(knobX, centerY), radius, ImGui.GetColorU32(UiSharedService.ThemeNavTextActive), 24);

        DrawGlassIcon(dl, new Vector2(origin.X + width - iconSize, centerY - iconSize * 0.5f), iconSize, muted, filled: true);

        if (tooltip != null && hovered && !active)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.Dummy(new Vector2(width, 0f));
        return released;
    }

    private static float Snap(float raw)
    {
        var steps = Notches - 1;
        return MathF.Round(raw * steps) / steps;
    }

    private static void DrawGlassIcon(ImDrawListPtr dl, Vector2 pos, float size, uint color, bool filled)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var square = size * 0.72f;
        var rounding = square * 0.28f;
        var back = pos;
        var front = pos + new Vector2(size - square, size - square);

        dl.AddRect(back, back + new Vector2(square), color, rounding, ImDrawFlags.None, 1.2f * scale);
        if (filled)
            dl.AddRectFilled(front, front + new Vector2(square), color, rounding);
        else
            dl.AddRect(front, front + new Vector2(square), color, rounding, ImDrawFlags.None, 1.2f * scale);
    }
}
