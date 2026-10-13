using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.Services.CharaData.Models;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private string _joinLobbyId = string.Empty;
    private void DrawGposeTogether()
    {
        if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.GposeTogether.BrioRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.GposeTogether.ConnectionRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Tab.GposeTogether"), FontAwesomeIcon.Camera);
        DrawHelpFoldout(Loc.Get("CharaDataHub.GposeTogether.Help"));

        using var disabled = ImRaii.Disabled(!_charaDataManager.BrioAvailable || !_uiSharedService.ApiController.IsConnected);

        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.GposeTogether.LobbyControls"), FontAwesomeIcon.DoorOpen);
        if (string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CharaDataHub.GposeTogether.CreateLobby")))
            {
                _charaDataGposeTogetherManager.CreateNewLobby();
            }
            ImGuiHelpers.ScaledDummy(5);
            ImGui.SetNextItemWidth(250);
            ImGui.InputTextWithHint("##lobbyId", Loc.Get("CharaDataHub.GposeTogether.LobbyIdPlaceholder"), ref _joinLobbyId, 30);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, Loc.Get("CharaDataHub.GposeTogether.JoinLobby")))
            {
                _charaDataGposeTogetherManager.JoinGPoseLobby(_joinLobbyId);
                _joinLobbyId = string.Empty;
            }
            if (!string.IsNullOrEmpty(_charaDataGposeTogetherManager.LastGPoseLobbyId)
                && _uiSharedService.IconTextButton(FontAwesomeIcon.LongArrowAltRight, string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.GposeTogether.RejoinLobby"), _charaDataGposeTogetherManager.LastGPoseLobbyId)))
            {
                _charaDataGposeTogetherManager.JoinGPoseLobby(_charaDataGposeTogetherManager.LastGPoseLobbyId);
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.Get("CharaDataHub.GposeTogether.CurrentLobby"));
            ImGui.SameLine();
            UiSharedService.ColorTextWrapped(_charaDataGposeTogetherManager.CurrentGPoseLobbyId, ImGuiColors.ParsedGreen);
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Clipboard))
            {
                ImGui.SetClipboardText(_charaDataGposeTogetherManager.CurrentGPoseLobbyId);
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.CopyLobbyIdTooltip"));
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowLeft, Loc.Get("CharaDataHub.GposeTogether.LeaveLobby")))
                {
                    _charaDataGposeTogetherManager.LeaveGPoseLobby();
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.LeaveTooltip.Main") + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.GposeTogether.LeaveTooltip.Hint"));
        }
        UiSharedService.DistanceSeparator();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowUp, Loc.Get("CharaDataHub.GposeTogether.SendCharacterData")))
            {
                _ = _charaDataGposeTogetherManager.PushCharacterDownloadDto();
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.SendCharacterDataTooltip"));
            if (!_uiSharedService.IsInGpose)
            {
                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.GposeTogether.AssignRequiresGpose"), UiSharedService.AccentColor, 300);
            }
            UiSharedService.EndSectionCard();
            ImGui.TextUnformatted(Loc.Get("CharaDataHub.GposeTogether.UsersInLobby"));
            var gposeCharas = _dalamudUtilService.GetGposeCharactersFromObjectTable().ToList();
            var self = _dalamudUtilService.GetPlayerCharacter();
            if (self != null)
            {
                gposeCharas = gposeCharas.Where(c => c != null && !string.Equals(c.Name.TextValue, self.Name.TextValue, StringComparison.Ordinal)).ToList();
            }

            using (ImRaii.Child("charaChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGuiHelpers.ScaledDummy(3);

                if (!_charaDataGposeTogetherManager.UsersInLobby.Any() && !string.IsNullOrEmpty(_charaDataGposeTogetherManager.CurrentGPoseLobbyId))
                {
                    UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.GposeTogether.NoUsers"), UiSharedService.AccentColor);
                }
                else
                {
                    foreach (var user in _charaDataGposeTogetherManager.UsersInLobby)
                    {
                        DrawLobbyUser(user, gposeCharas);
                    }
                }
            }
        }
    }

    private void DrawLobbyUser(GposeLobbyUserData user,
        List<Dalamud.Game.ClientState.Objects.Types.ICharacter?> gposeCharas)
    {
        using var id = ImRaii.PushId(user.UserData.UID);
        using var indent = ImRaii.PushIndent(5f);
        var sameMapAndServer = _charaDataGposeTogetherManager.IsOnSameMapAndServer(user);
        var width = ImGui.GetContentRegionAvail().X - 5;
        UiSharedService.DrawGrouped(() =>
        {
            var availWidth = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            var note = _serverConfigurationManager.GetNoteForUid(user.UserData.UID);
            var userText = note == null ? user.UserData.AliasOrUID : $"{note} ({user.UserData.AliasOrUID})";
            UiSharedService.ColorText(userText, ImGuiColors.ParsedGreen);

            var buttonsize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X;
            var buttonsize2 = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;
            ImGui.SameLine();
            ImGui.SetCursorPosX(availWidth - (buttonsize + buttonsize2 + ImGui.GetStyle().ItemSpacing.X));
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || user.Address == nint.Zero))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                {
                    _ = _charaDataGposeTogetherManager.ApplyCharaData(user);
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.ApplyDataTooltip.Main") + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.GposeTogether.ApplyDataTooltip.Note"));
            ImGui.SameLine();
            using (ImRaii.Disabled(!_uiSharedService.IsInGpose || user.CharaData == null || sameMapAndServer.SameEverything))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
                {
                    _ = _charaDataGposeTogetherManager.SpawnAndApplyData(user);
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.SpawnTooltip.Main") + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.GposeTogether.SpawnTooltip.Note"));


            using (ImRaii.Group())
            {
                UiSharedService.ColorText(Loc.Get("CharaDataHub.GposeTogether.MapInfo"), ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.ExternalLinkSquareAlt, ImGuiColors.DalamudGrey);
            }
            UiSharedService.AttachToolTip(user.WorldDataDescriptor + UiSharedService.TooltipSeparator);

            ImGui.SameLine();
            _uiSharedService.IconText(FontAwesomeIcon.Map, sameMapAndServer.SameMap ? ImGuiColors.ParsedGreen : UiSharedService.AccentColor);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && user.WorldData != null)
            {
                _dalamudUtilService.SetMarkerAndOpenMap(new(user.WorldData.Value.PositionX, user.WorldData.Value.PositionY, user.WorldData.Value.PositionZ), user.Map);
            }
            UiSharedService.AttachToolTip((sameMapAndServer.SameMap ? Loc.Get("CharaDataHub.GposeTogether.Tooltip.SameMap") : Loc.Get("CharaDataHub.GposeTogether.Tooltip.NotSameMap")) + UiSharedService.TooltipSeparator
                + Loc.Get("CharaDataHub.GposeTogether.Tooltip.MapClick") + Environment.NewLine
                + Loc.Get("CharaDataHub.GposeTogether.Tooltip.MapRequirement"));

            ImGui.SameLine();
            _uiSharedService.IconText(FontAwesomeIcon.Globe, sameMapAndServer.SameServer ? ImGuiColors.ParsedGreen : UiSharedService.AccentColor);
            UiSharedService.AttachToolTip((sameMapAndServer.SameMap ? Loc.Get("CharaDataHub.GposeTogether.Tooltip.SameServer") : Loc.Get("CharaDataHub.GposeTogether.Tooltip.NotSameServer")) + UiSharedService.TooltipSeparator
                + Loc.Get("CharaDataHub.GposeTogether.Tooltip.ServerNote"));

            ImGui.SameLine();
            _uiSharedService.IconText(FontAwesomeIcon.Running, sameMapAndServer.SameEverything ? ImGuiColors.ParsedGreen : UiSharedService.AccentColor);
            UiSharedService.AttachToolTip((sameMapAndServer.SameEverything ? Loc.Get("CharaDataHub.GposeTogether.Tooltip.SameInstance") : Loc.Get("CharaDataHub.GposeTogether.Tooltip.NotSameInstance")) + UiSharedService.TooltipSeparator +
                Loc.Get("CharaDataHub.GposeTogether.Tooltip.InstanceNote") + Environment.NewLine
                + Loc.Get("CharaDataHub.GposeTogether.Tooltip.InstanceSpawnNote"));

            using (ImRaii.Disabled(!_uiSharedService.IsInGpose))
            {
                ImGui.SetNextItemWidth(200);
                using (var combo = UiSharedService.Combo("##character", string.IsNullOrEmpty(user.AssociatedCharaName) ? Loc.Get("CharaDataHub.GposeTogether.NoCharacter") : CharaName(user.AssociatedCharaName)))
                {
                    if (combo)
                    {
                        foreach (var chara in gposeCharas)
                        {
                            if (chara == null) continue;

                            if (ImGui.Selectable(CharaName(chara.Name.TextValue), chara.Address == user.Address))
                            {
                                user.AssociatedCharaName = chara.Name.TextValue;
                                user.Address = chara.Address;
                            }
                        }
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(user.Address == nint.Zero))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    {
                        user.AssociatedCharaName = string.Empty;
                        user.Address = nint.Zero;
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.UnassignTooltip"));
                if (_uiSharedService.IsInGpose && user.Address == nint.Zero)
                {
                    ImGui.SameLine();
                    _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle, UiSharedService.AccentColor);
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeTogether.NoCharacterWarning"));
                }
            }
        }, 5, width);
        ImGuiHelpers.ScaledDummy(5);
    }
}