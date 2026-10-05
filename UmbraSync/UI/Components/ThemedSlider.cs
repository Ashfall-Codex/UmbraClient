using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace UmbraSync.UI.Components;

public static partial class ThemedSlider
{
    private const int MaxNotches = 41;
    private const int MajorEvery = 4;
    private static readonly float[] NiceSteps = [1f, 2f, 5f, 10f, 20f, 25f, 50f, 100f, 200f, 250f, 500f, 1000f];

    [GeneratedRegex(@"%(?:\.(?<decimals>\d+))?[dfi]", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex FormatSpec();
    public static bool Int(string label, ref int value, int min, int max, string format = "%d", float? step = null, string? minText = null)
    {
        float v = value;
        float resolved = step ?? AutoIntStep(max - min);
        bool changed = Draw(label, ref v, min, max, format, integer: true, resolved, minText);
        if (changed) value = (int)MathF.Round(v);
        return changed;
    }

    public static bool Float(string label, ref float value, float min, float max, string format = "%.3f", float? step = null)
        => Draw(label, ref value, min, max, format, integer: false, step ?? (max - min) / (MaxNotches - 1), null);

    private static float AutoIntStep(int steps)
    {
        if (steps < MaxNotches) return 1f;
        foreach (var candidate in NiceSteps)
            if (steps / candidate < MaxNotches) return candidate;
        return MathF.Ceiling(steps / (float)(MaxNotches - 1));
    }

    private static bool Draw(string label, ref float value, float min, float max, string format, bool integer, float step, string? minText)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        int hashAt = label.IndexOf("##", StringComparison.Ordinal);
        string visibleLabel = hashAt >= 0 ? label[..hashAt] : label;

        var knobRadius = 7f * scale;
        var trackThickness = 4f * scale;
        var tickGap = 6f * scale;
        var height = knobRadius * 2f + tickGap + 3f * scale;
        var gap = 10f * scale;

        float range = max - min;
        step = Math.Max(step, range / 400f);
        int notches = range > 0f ? (int)MathF.Floor(range / step + 0.0001f) + 1 : 1;
        float totalWidth = Math.Max(120f * scale, ImGui.CalcItemWidth());
        // minText remplace la valeur affichée à la borne basse (par exemple « Auto » pour 0).
        string valueText = minText is not null && Math.Abs(value - min) < 0.0001f ? minText : FormatValue(format, value, integer);
        string widest = FormatValue(format, max, integer);
        float valueWidth = Math.Max(ImGui.CalcTextSize(valueText).X, ImGui.CalcTextSize(widest).X);
        if (minText is not null) valueWidth = Math.Max(valueWidth, ImGui.CalcTextSize(minText).X);

        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + knobRadius;
        var trackStart = origin.X + knobRadius;
        var trackEnd = origin.X + totalWidth - valueWidth - gap - knobRadius;
        var trackLength = Math.Max(1f, trackEnd - trackStart);

        ImGui.PushID(label);
        ImGui.SetCursorScreenPos(new Vector2(trackStart - knobRadius, origin.Y));
        ImGui.InvisibleButton("##themed_slider", new Vector2(trackLength + knobRadius * 2f, height));
        ImGui.PopID();
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        bool changed = false;
        if (active && range > 0f)
        {
            float raw = Math.Clamp((ImGui.GetIO().MousePos.X - trackStart) / trackLength, 0f, 1f);
            // Aimanté sur le cran le plus proche ; le bout de piste reste atteignable même hors pas.
            float next = raw >= 0.999f ? max : min + MathF.Round(raw * range / step) * step;
            if (integer) next = MathF.Round(next);
            next = Math.Clamp(next, min, max);
            if (Math.Abs(next - value) > 0.0001f)
            {
                value = next;
                changed = true;
            }
        }

        value = Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));
        float t = range > 0f ? Math.Clamp((value - min) / range, 0f, 1f) : 0f;
        var knobX = trackStart + t * trackLength;
        var half = trackThickness * 0.5f;
        var rest = ImGui.GetColorU32(UiSharedService.ThemeButtonBg with { W = 0.85f });
        var fill = ImGui.GetColorU32(UiSharedService.AccentColor);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(trackEnd, centerY + half), rest, half);
        dl.AddRectFilled(new Vector2(trackStart, centerY - half), new Vector2(knobX, centerY + half), fill, half);

        var tickY = centerY + knobRadius + tickGap * 0.5f;
        for (var i = 0; i < notches; i++)
        {
            var tt = range > 0f ? Math.Clamp(i * step / range, 0f, 1f) : 0f;
            var x = trackStart + tt * trackLength;
            var current = Math.Abs(min + i * step - value) < step * 0.01f;
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

        var textY = origin.Y + (knobRadius * 2f - ImGui.GetTextLineHeight()) * 0.5f;
        var valueX = origin.X + totalWidth - valueWidth;
        dl.AddText(new Vector2(valueX + valueWidth - ImGui.CalcTextSize(valueText).X, textY),
            ImGui.GetColorU32(UiSharedService.ThemeNavText), valueText);
        if (visibleLabel.Length > 0)
            dl.AddText(new Vector2(origin.X + totalWidth + ImGui.GetStyle().ItemInnerSpacing.X, textY),
                ImGui.GetColorU32(ImGuiCol.Text), visibleLabel);

        // Le dernier élément couvre toute la ligne, libellé compris : un SameLine() (icône d'aide)
        // se place alors à droite du libellé, sur la ligne du curseur.
        float labelWidth = visibleLabel.Length > 0 ? ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(visibleLabel).X : 0f;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(totalWidth + labelWidth, height));
        return changed;
    }

    private static string FormatValue(string format, float value, bool integer)
    {
        var match = FormatSpec().Match(format);
        if (!match.Success) return value.ToString(integer ? "0" : "0.###", CultureInfo.CurrentCulture);

        string number = match.Groups["decimals"].Success
            ? value.ToString("F" + match.Groups["decimals"].Value, CultureInfo.CurrentCulture)
            : integer ? ((int)MathF.Round(value)).ToString(CultureInfo.CurrentCulture) : value.ToString("F3", CultureInfo.CurrentCulture);
        return format[..match.Index] + number + format[(match.Index + match.Length)..];
    }
}
