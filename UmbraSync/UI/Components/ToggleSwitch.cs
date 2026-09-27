using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace UmbraSync.UI.Components;

// Même contrat qu'ImGui.Checkbox : un seul item qui occupe une hauteur de frame, libellé
// cliquable, « ## » pour l'identifiant, true l'image où la valeur bascule.
public static class ToggleSwitch
{
    private const float AnimationSpeed = 16f;
    private static readonly Dictionary<uint, float> Positions = [];

    public static bool Draw(string label, ref bool value)
    {
        var style = ImGui.GetStyle();
        float frameHeight = ImGui.GetFrameHeight();
        float height = MathF.Round(frameHeight * 0.82f);
        float width = MathF.Round(height * 1.8f);
        float radius = height * 0.5f;

        int idMarker = label.IndexOf("##", StringComparison.Ordinal);
        ReadOnlySpan<char> visible = idMarker >= 0 ? label.AsSpan(0, idMarker) : label.AsSpan();
        float labelWidth = visible.IsEmpty ? 0f : style.ItemInnerSpacing.X + ImGui.CalcTextSize(visible).X;

        var pos = ImGui.GetCursorScreenPos();
        uint id = ImGui.GetID(label);
        bool toggled = ImGui.InvisibleButton(label, new Vector2(width + labelWidth, frameHeight));
        if (toggled) value = !value;
        bool hovered = ImGui.IsItemHovered();

        float target = value ? 1f : 0f;
        float t = Positions.TryGetValue(id, out float stored) ? stored : target;
        t += (target - t) * Math.Min(1f, ImGui.GetIO().DeltaTime * AnimationSpeed);
        if (Math.Abs(target - t) < 0.001f) t = target;
        Positions[id] = t;

        var track = Vector4.Lerp(UiSharedService.ThemeButtonHovered, UiSharedService.AccentColor, t);
        if (hovered) track = Vector4.Lerp(track, UiSharedService.ThemeSwitchKnobOn, 0.12f);

        var drawList = ImGui.GetWindowDrawList();
        var trackMin = new Vector2(pos.X, pos.Y + MathF.Round((frameHeight - height) * 0.5f));
        var trackMax = trackMin + new Vector2(width, height);
        drawList.AddRectFilled(trackMin, trackMax, ImGui.GetColorU32(track), radius);
        drawList.AddRect(trackMin, trackMax,
            ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.ThemeButtonActive, 1f - t)), radius);

        var knobCenter = new Vector2(trackMin.X + radius + t * (width - height), trackMin.Y + radius);
        var knob = Vector4.Lerp(UiSharedService.ThemeSwitchKnobOff, UiSharedService.ThemeSwitchKnobOn, t);
        drawList.AddCircleFilled(knobCenter, radius - 2f * ImGuiHelpers.GlobalScale, ImGui.GetColorU32(knob), 32);

        if (!visible.IsEmpty)
        {
            drawList.AddText(
                new Vector2(pos.X + width + style.ItemInnerSpacing.X, pos.Y + style.FramePadding.Y),
                ImGui.GetColorU32(ImGuiCol.Text),
                visible);
        }

        return toggled;
    }
}
