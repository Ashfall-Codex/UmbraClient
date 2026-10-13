using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;

namespace UmbraSync.UI.Components;

internal static class ColorSwatchPicker
{
    private static readonly Vector4[] Presets =
    [
        new(0.74f, 0.55f, 1.00f, 1f), // violet
        new(0.95f, 0.50f, 0.70f, 1f), // rose
        new(1.00f, 0.60f, 0.30f, 1f), // orange
        new(0.96f, 0.82f, 0.35f, 1f), // or
        new(0.50f, 0.88f, 0.50f, 1f), // vert
        new(0.40f, 0.85f, 0.85f, 1f), // cyan
        new(0.45f, 0.65f, 1.00f, 1f), // bleu
        new(0.92f, 0.92f, 0.95f, 1f), // blanc
    ];

    public static bool TryParse(string? hex, out Vector4 color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return false;
        if (!int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        return true;
    }

    public static string ToHex(Vector4 color)
        => string.Create(CultureInfo.InvariantCulture,
            $"#{(int)MathF.Round(color.X * 255f):X2}{(int)MathF.Round(color.Y * 255f):X2}{(int)MathF.Round(color.Z * 255f):X2}");
    
    public static bool Draw(string id, ref string? current, Vector4 fallback)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float swatch = 20f * scale;
        float gap = 7f * scale;
        var dl = ImGui.GetWindowDrawList();
        bool changed = false;
        bool hasCurrent = TryParse(current, out var currentColor);

        for (int i = 0; i < Presets.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            var preset = Presets[i];
            var presetHex = ToHex(preset);
            var pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##{id}-color-{i}", new Vector2(swatch));
            bool hovered = ImGui.IsItemHovered();
            var center = pos + new Vector2(swatch / 2f);
            dl.AddCircleFilled(center, swatch / 2f - 1f * scale, ImGui.GetColorU32(preset));
            if (hasCurrent && string.Equals(current, presetHex, StringComparison.OrdinalIgnoreCase))
                dl.AddCircle(center, swatch / 2f + 1.5f * scale, ImGui.GetColorU32(Vector4.One), 0, 2f * scale);
            else if (hovered)
                dl.AddCircle(center, swatch / 2f + 1f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), 0, 1.5f * scale);
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked)
            {
                current = presetHex;
                changed = true;
            }
        }

        var rgb = hasCurrent ? new Vector3(currentColor.X, currentColor.Y, currentColor.Z) : new Vector3(fallback.X, fallback.Y, fallback.Z);
        ImGui.SameLine(0f, gap);
        if (ImGui.ColorEdit3($"##{id}-custom", ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
        {
            current = ToHex(new Vector4(rgb.X, rgb.Y, rgb.Z, 1f));
            changed = true;
        }
        UiSharedService.AttachToolTip(Loc.Get("Syncshell.Cards.BorderColor.Custom"));

        if (hasCurrent)
        {
            ImGui.SameLine(0f, gap);
            if (ImGui.SmallButton(Loc.Get("Syncshell.Cards.BorderColor.Auto") + $"##{id}-auto"))
            {
                current = null;
                changed = true;
            }
        }

        return changed;
    }
}
