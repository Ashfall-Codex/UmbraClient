using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace UmbraSync.UI.Components;

// Briques communes aux assistants pas à pas (tour de nouveautés, et assistant d'installation à venir).
public static class WizardControls
{
    // Icône centrée, titre, sous-titre et séparateur.
    public static void DrawStepHeader(FontAwesomeIcon icon, string title, string subtitle, float availWidth)
    {
        ImGuiHelpers.ScaledDummy(2f);

        var iconText = icon.ToIconString();
        UiSharedService.SetFontScale(1.2f);
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconText);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availWidth - iconSize.X) / 2f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, iconText);
        UiSharedService.SetFontScale(1.0f);

        ImGuiHelpers.ScaledDummy(2f);
        DrawCentered(title, availWidth, null);
        DrawCentered(subtitle, availWidth, ImGuiColors.DalamudGrey);

        ImGuiHelpers.ScaledDummy(3f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(3f);
    }

    // Une rubrique : carte de section (icône et titre en accent) avec sa description. Les cartes se
    // referment toutes seules à la suivante ; la dernière se ferme avec UiSharedService.EndSectionCard.
    public static void DrawField(FontAwesomeIcon icon, string label, string description)
    {
        UiSharedService.BeginSectionCard(label, icon);
        UiSharedService.TextWrapped(description);
    }

    // Puces de progression : étape courante pleine, étapes passées atténuées, étapes à venir grises.
    public static void DrawStepDots(int count, int current, float availWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float radius = 4f * scale;
        float spacing = 14f * scale;
        float totalWidth = count * radius * 2f + (count - 1) * spacing;
        var origin = ImGui.GetCursorScreenPos();
        float startX = origin.X + MathF.Max(0f, (availWidth - totalWidth) / 2f);
        float y = origin.Y + radius + 2f * scale;

        ImGui.Dummy(new Vector2(0f, radius * 2f + 4f * scale));

        var drawList = ImGui.GetWindowDrawList();
        for (int i = 0; i < count; i++)
        {
            float x = startX + i * (radius * 2f + spacing) + radius;
            var color = (i - current) switch
            {
                0 => UiSharedService.AccentColor,
                < 0 => UiSharedService.AccentColor with { W = 0.4f },
                _ => new Vector4(0.3f, 0.3f, 0.3f, 1f),
            };
            drawList.AddCircleFilled(new Vector2(x, y), radius, ImGui.GetColorU32(color));
        }
    }

    private static void DrawCentered(string text, float availWidth, Vector4? color)
    {
        var size = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (availWidth - size.X) / 2f));
        if (color.HasValue)
            ImGui.TextColored(color.Value, text);
        else
            ImGui.TextUnformatted(text);
    }
}
