using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using UmbraSync.UI;

namespace UmbraSync.UI.Components;

public sealed class BbCodeToolbar
{
    private readonly UiSharedService _uiSharedService;
    private Vector3 _colorVec = new(1f, 0.6f, 0.2f);

    public BbCodeToolbar(UiSharedService uiSharedService)
    {
        _uiSharedService = uiSharedService;
    }

    public void Draw(ref string text)
    {
        var spacing = 2f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiSharedService.ThemeButtonActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 2) * ImGuiHelpers.GlobalScale);

        IconButton(FontAwesomeIcon.Bold, "[b]", "[/b]", "Gras", ref text);
        ImGui.SameLine(0, spacing);
        IconButton(FontAwesomeIcon.Italic, "[i]", "[/i]", "Italique", ref text);
        ImGui.SameLine(0, spacing);
        TextButton("U̲", "[u]", "[/u]", "Souligné", ref text);
        ImGui.SameLine(0, spacing);
        IconButton(FontAwesomeIcon.AlignLeft, "[left]\n", "\n[/left]", "Aligner à gauche", ref text);
        ImGui.SameLine(0, spacing);
        IconButton(FontAwesomeIcon.AlignCenter, "[center]\n", "\n[/center]", "Centrer", ref text);
        ImGui.SameLine(0, spacing);
        IconButton(FontAwesomeIcon.AlignRight, "[right]\n", "\n[/right]", "Aligner à droite", ref text);
        ImGui.SameLine(0, spacing);
        IconButton(FontAwesomeIcon.AlignJustify, "[justify]\n", "\n[/justify]", "Justifier", ref text);
        ImGui.SameLine(0, spacing);

        using (var _ = _uiSharedService.IconFont.Push())
        {
            if (ImGui.Button(FontAwesomeIcon.PaintBrush.ToIconString() + "##bbcode_color"))
                ImGui.OpenPopup("bbcode_color_picker");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Couleur");

        if (ImGui.BeginPopup("bbcode_color_picker"))
        {
            if (ImGui.ColorPicker3("##bbcodeColorPicker", ref _colorVec, ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.NoSidePreview))
            { /* value read from ref */ }
            if (ImGui.Button("Insérer##bbcodeColorInsert"))
            {
                var hex = UiSharedService.Vector4ToHex(new Vector4(_colorVec, 1f));
                text += $"[color={hex}][/color]";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine(0, spacing);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.QuestionCircle, "Aide BBCode"))
            ImGui.OpenPopup("bbcode_help_popup");

        DrawHelpPopup();

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
    }

    private void IconButton(FontAwesomeIcon icon, string openTag, string closeTag, string tooltip, ref string text)
    {
        using (_uiSharedService.IconFont.Push())
        {
            if (ImGui.Button(icon.ToIconString() + $"##bb_{openTag}"))
                text += openTag + closeTag;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private static void TextButton(string label, string openTag, string closeTag, string tooltip, ref string text)
    {
        if (ImGui.Button(label + $"##bb_{openTag}"))
            text += openTag + closeTag;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }

    private static void DrawHelpPopup()
    {
        if (!ImGui.BeginPopup("bbcode_help_popup")) return;

        ImGui.TextColored(new Vector4(0.59f, 0.27f, 0.90f, 1f), "Formatage BBCode");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Encadrez votre texte avec des balises pour le mettre en forme.");
        ImGui.TextUnformatted("Les boutons de la barre d'outils insèrent les balises à la fin du texte.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "Style de texte");
        ImGui.Spacing();
        DrawHelpRow("[b]texte[/b]", "Gras");
        DrawHelpRow("[i]texte[/i]", "Italique");
        DrawHelpRow("[u]texte[/u]", "Souligné");
        DrawHelpRow("[color=Red]texte[/color]", "Couleur (nom ou #hex)");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Alignement");
        ImGui.Spacing();
        DrawHelpRow("[left]texte[/left]", "Aligné à gauche");
        DrawHelpRow("[center]texte[/center]", "Centré");
        DrawHelpRow("[right]texte[/right]", "Aligné à droite");
        DrawHelpRow("[justify]texte[/justify]", "Justifié");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Couleurs disponibles");
        ImGui.Spacing();
        ImGui.TextWrapped("Red, Orange, Yellow, Gold, Green, LightGreen,\nLightBlue, DarkBlue, Blue, Pink, Purple, White, Grey");
        ImGui.Spacing();
        ImGui.TextWrapped("Vous pouvez aussi utiliser un code hexadécimal :\n[color=#FF5500]texte[/color]");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Exemple");
        ImGui.Spacing();
        ImGui.TextWrapped("[justify][b]Titre[/b]\nCeci est un texte [color=Gold]doré[/color]\net [i]italique[/i].[/justify]");

        ImGui.EndPopup();
    }

    private static void DrawHelpRow(string tag, string description)
    {
        ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.20f, 1f), tag);
        ImGui.SameLine(280 * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(description);
    }
}
