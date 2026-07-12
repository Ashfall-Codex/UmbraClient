using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Services;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;


public sealed class HousingNpcSceneEditorUi : WindowMediatorSubscriberBase
{
    private readonly HousingNpcScenarioService _service;
    private readonly UiSharedService _uiShared;

    public HousingNpcSceneEditorUi(ILogger<HousingNpcSceneEditorUi> logger, MareMediator mediator,
        HousingNpcScenarioService service, UiSharedService uiShared,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Éditeur PNJ Housing###HousingNpcSceneEditor", performanceCollectorService)
    {
        _service = service;
        _uiShared = uiShared;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 320),
            MaximumSize = new Vector2(900, 1200),
        };
    }

    protected override void DrawInternal()
    {
        var loc = _service.CurrentLocation;
        if (loc == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Entre dans un logement pour éditer ses PNJ.");
            return;
        }

        var l = loc.Value;
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"Room — Serveur {l.ServerId}, Ward {l.WardId}, Parcelle {l.HouseId}, Division {l.DivisionId}, Chambre {l.RoomId}");
        ImGui.Separator();

        if (_uiShared.IconTextButton(FontAwesomeIcon.User, "Capturer depuis moi"))
            _ = _service.AddFromSelfAsync(string.Empty);
        UiSharedService.AttachToolTip("Crée un PNJ figé avec TON apparence actuelle, à ta position.");
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Crosshairs, "Capturer depuis ma cible"))
            _ = _service.AddFromTargetAsync(string.Empty);
        UiSharedService.AttachToolTip("Vise un personnage habillé, puis capture son apparence.");
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Sync, "Rafraîchir"))
            _ = _service.RefreshAsync();

        ImGuiHelpers.ScaledDummy(4f);

        var scenario = _service.GetCurrentScenario();
        if (scenario == null || scenario.Entries.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "Aucun PNJ ici. Place-toi à l'endroit voulu et clique « Ajouter un PNJ ».");
            return;
        }

        string? toRemove = null;
        bool dirty = false;

        foreach (var entry in scenario.Entries)
        {
            using var id = ImRaii.PushId(entry.Id);

            UiSharedService.DrawCard($"npc-{entry.Id}", () =>
            {
                _uiShared.IconText(FontAwesomeIcon.User);
                ImGui.SameLine();
                var name = entry.DisplayName;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.InputTextWithHint("##name", "Nom du PNJ", ref name, 64)) { entry.DisplayName = name; dirty = true; }

                var pos = new Vector3(entry.X, entry.Y, entry.Z);
                ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputFloat3("Position", ref pos)) { entry.X = pos.X; entry.Y = pos.Y; entry.Z = pos.Z; dirty = true; }

                var rot = entry.Rotation;
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                if (ImGui.SliderFloat("Rotation", ref rot, -3.14159f, 3.14159f)) { entry.Rotation = rot; dirty = true; }

                if (_uiShared.IconTextButton(FontAwesomeIcon.Crosshairs, "Placer à moi"))
                    _ = _service.MoveEntryToPlayerAsync(entry.Id);
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
                {
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Supprimer") && UiSharedService.CtrlPressed())
                        toRemove = entry.Id;
                }
                UiSharedService.AttachToolTip("Ctrl+clic pour supprimer");
            }, stretchWidth: true);

            ImGuiHelpers.ScaledDummy(3f);
        }

        if (toRemove != null)
            _ = _service.RemoveEntryAsync(toRemove);

        ImGui.Separator();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Appliquer & aperçu"))
            _ = _service.PersistAndRefreshAsync();
        if (dirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "modifs non appliquées");
        }
    }
}
