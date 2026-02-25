using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private string _questSessionJoinId = string.Empty;

    private void DrawQuestSync()
    {
        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("QuestSync.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        _uiSharedService.BigText(Loc.Get("QuestSync.Title"));
        DrawHelpFoldout(Loc.Get("QuestSync.HelpText"));

        using var disabled = ImRaii.Disabled(!_uiSharedService.ApiController.IsConnected);

        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText(Loc.Get("QuestSync.Session"));

        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.DrawGroupedCenteredColorText(Loc.Get("QuestSync.InDevelopment"), ImGuiColors.DalamudGrey3, 450);
        ImGuiHelpers.ScaledDummy(5);

        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText(Loc.Get("QuestSync.Controls"));

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("QuestSync.CreateSession")))
        {
            // Appeler QuestSessionCreate via l'API
        }

        ImGuiHelpers.ScaledDummy(5);
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##questSessionId", Loc.Get("QuestSync.SessionCode"), ref _questSessionJoinId, 30);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, Loc.Get("QuestSync.JoinSession")))
        {
            // Appeler QuestSessionJoin via l'API
            _questSessionJoinId = string.Empty;
        }

        UiSharedService.DistanceSeparator();
        ImGui.TextUnformatted(Loc.Get("QuestSync.Participants"));
        ImGuiHelpers.ScaledDummy(3);
        UiSharedService.ColorTextWrapped(Loc.Get("QuestSync.NoActiveSession"), ImGuiColors.DalamudGrey3);
    }
}
