using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Localization;
using UmbraSync.Services.CharaData.Models;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private void DrawEditCharaData(CharaDataFullExtendedDto? dataDto)
    {
        using var imguiid = ImRaii.PushId(dataDto?.Id ?? "NoData");

        if (dataDto == null)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Mcd.Edit.SelectEntry"), UiSharedService.AccentColor);
            return;
        }

        var updateDto = _charaDataManager.GetUpdateDto(dataDto.Id);

        if (updateDto == null)
        {
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Mcd.Edit.NoUpdateDto"), UiSharedService.AccentColor);
            return;
        }

        bool canUpdate = updateDto.HasChanges;
        if (canUpdate || _charaDataManager.CharaUpdateTask != null)
        {
            ImGuiHelpers.ScaledDummy(5);
        }

        var indent = ImRaii.PushIndent(10f);
        if (canUpdate || _charaDataManager.UploadTask != null)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGrouped(() =>
            {
                if (canUpdate)
                {
                    ImGui.AlignTextToFramePadding();
                    UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcd.Edit.UnsavedWarning"), UiSharedService.AccentColor);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleUp, Loc.Get("CharaDataHub.Mcd.Edit.Save")))
                        {
                            _charaDataManager.UploadCharaData(dataDto.Id);
                        }
                        ImGui.SameLine();
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, Loc.Get("CharaDataHub.Mcd.Edit.UndoAll")))
                        {
                            updateDto.UndoChanges();
                        }
                    }
                    if (_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted)
                    {
                        UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcd.Edit.Updating"), UiSharedService.AccentColor);
                    }
                }

                if (!_charaDataManager.UploadTask?.IsCompleted ?? false)
                {
                    DisableDisabled(() =>
                    {
                        if (_charaDataManager.UploadProgress != null)
                        {
                            UiSharedService.ColorTextWrapped(_charaDataManager.UploadProgress.Value ?? string.Empty, UiSharedService.AccentColor);
                        }
                        if ((!_charaDataManager.UploadTask?.IsCompleted ?? false) && _uiSharedService.IconTextButton(FontAwesomeIcon.Ban, Loc.Get("CharaDataHub.Mcd.Edit.CancelUpload")))
                        {
                            _charaDataManager.CancelUpload();
                        }
                        else if (_charaDataManager.UploadTask?.IsCompleted ?? false)
                        {
                            var color = UiSharedService.GetSuccessColor(_charaDataManager.UploadTask.Result.Success);
                            UiSharedService.ColorTextWrapped(_charaDataManager.UploadTask.Result.Output, color);
                        }
                    });
                }
                else if (_charaDataManager.UploadTask?.IsCompleted ?? false)
                {
                    var color = UiSharedService.GetSuccessColor(_charaDataManager.UploadTask.Result.Success);
                    UiSharedService.ColorTextWrapped(_charaDataManager.UploadTask.Result.Output, color);
                }
            });
        }
        indent.Dispose();

        if (canUpdate || _charaDataManager.CharaUpdateTask != null)
        {
            ImGuiHelpers.ScaledDummy(5);
        }

        using var child = ImRaii.Child("editChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);

        DrawEditCharaDataGeneral(dataDto, updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataAccessAndSharing(updateDto);
        DrawEditCharaDataAppearance(dataDto, updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataPoses(updateDto);
        UiSharedService.EndSectionCard();
    }

    private void DrawEditCharaDataAccessAndSharing(CharaDataExtendedUpdateDto updateDto)
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcd.Access.Title"), FontAwesomeIcon.Lock);
        ImGuiHelpers.ScaledDummy(3);

        var modes = new (AccessTypeDto Access, ShareTypeDto Share, FontAwesomeIcon Icon, string LabelKey, string DescKey, bool? GroupMode)[]
        {
            (AccessTypeDto.Individuals, ShareTypeDto.Private, FontAwesomeIcon.Lock,        "CharaDataHub.Mcd.Access.Mode.Invitation", "CharaDataHub.Mcd.Access.Mode.InvitationDesc", false),
            (AccessTypeDto.Individuals, ShareTypeDto.Private, FontAwesomeIcon.LayerGroup,  "CharaDataHub.Mcd.Access.Mode.Syncshell",  "CharaDataHub.Mcd.Access.Mode.SyncshellDesc",  true),
            (AccessTypeDto.ClosePairs,  ShareTypeDto.Shared,  FontAwesomeIcon.UserFriends, "CharaDataHub.Mcd.Access.Mode.Pairs",      "CharaDataHub.Mcd.Access.Mode.PairsDesc",      null),
            (AccessTypeDto.AllPairs,    ShareTypeDto.Shared,  FontAwesomeIcon.Users,       "CharaDataHub.Mcd.Access.Mode.AllPairs",   "CharaDataHub.Mcd.Access.Mode.AllPairsDesc",   null),
            (AccessTypeDto.Public,      ShareTypeDto.Private, FontAwesomeIcon.Globe,        "CharaDataHub.Mcd.Access.Mode.Public",     "CharaDataHub.Mcd.Access.Mode.PublicDesc",     null),
        };

        foreach (var mode in modes)
        {
            bool selected = mode.GroupMode.HasValue
                ? updateDto.AccessType == mode.Access && _liveGroupMode == mode.GroupMode.Value
                : updateDto.AccessType == mode.Access;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            bool clicked = _uiSharedService.IconTextButton(mode.Icon, Loc.Get(mode.LabelKey));
            if (selected) ImGui.PopStyleColor();
            if (clicked)
            {
                updateDto.AccessType = mode.Access;
                updateDto.ShareType = mode.Share;
                if (mode.GroupMode.HasValue) _liveGroupMode = mode.GroupMode.Value;
            }
            UiSharedService.AttachToolTip(Loc.Get(mode.DescKey));
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGuiHelpers.ScaledDummy(5);

        if (updateDto.AccessType == AccessTypeDto.Individuals)
        {
            DrawSpecific(updateDto);
        }

        ImGuiHelpers.ScaledDummy(5);
    }

    private void DrawEditCharaDataAppearance(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Edit.Appearance"), FontAwesomeIcon.Tshirt);

        bool hasAppearance = !string.IsNullOrEmpty(updateDto.GlamourerData);
        if (!hasAppearance)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Edit.Appearance.NoData"), ImGuiColors.DalamudGrey);
        }
        else if (!updateDto.IsAppearanceEqual)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Edit.Appearance.Unsaved"), UiSharedService.AccentColor);
        }
        else
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Edit.Appearance.UpToDate"), ImGuiColors.HealerGreen);
        }

        ImGuiHelpers.ScaledDummy(3);

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Camera, Loc.Get("CharaDataHub.Edit.SetAppearance")))
        {
            _charaDataManager.SetAppearanceData(dataDto.Id);
        }
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Edit.SetAppearance.Help"));
        ImGui.SameLine();
        using (ImRaii.Disabled(dataDto.HasMissingFiles || !updateDto.IsAppearanceEqual || _charaDataManager.DataApplicationTask != null))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.CheckCircle, Loc.Get("CharaDataHub.Edit.PreviewAppearance")))
            {
                _charaDataManager.ApplyDataToSelf(dataDto);
            }
        }
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Edit.PreviewAppearance.Help"));

        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Edit.HasGlamourer"));
        ImGui.SameLine();
        bool hasGlamourerdata = !string.IsNullOrEmpty(updateDto.GlamourerData);
        ImGui.SameLine(200);
        _uiSharedService.BooleanToColoredIcon(hasGlamourerdata, false);

        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Edit.HasFiles"));
        var hasFiles = (updateDto.FileGamePaths ?? []).Any() || (dataDto.OriginalFiles.Any());
        ImGui.SameLine(200);
        _uiSharedService.BooleanToColoredIcon(hasFiles, false);
        if (hasFiles && updateDto.IsAppearanceEqual)
        {
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20, 1);
            ImGui.SameLine();
            var pos = ImGui.GetCursorPosX();
            ImGui.NewLine();
            ImGui.SameLine(pos);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Edit.FileHashCount"), dataDto.FileGamePaths.DistinctBy(k => k.HashOrFileSwap, StringComparer.Ordinal).Count(), dataDto.OriginalFiles.DistinctBy(k => k.HashOrFileSwap, StringComparer.Ordinal).Count()));
            ImGui.NewLine();
            ImGui.SameLine(pos);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Edit.GamePaths"), dataDto.FileGamePaths.Count));
            ImGui.NewLine();
            ImGui.SameLine(pos);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Edit.FileSwaps"), dataDto.FileSwaps.Count));
            ImGui.NewLine();
            ImGui.SameLine(pos);
            if (!dataDto.HasMissingFiles)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Edit.FilesPresent"), ImGuiColors.HealerGreen);
            }
            else
            {
                UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Edit.FilesMissing"), dataDto.MissingFiles.DistinctBy(k => k.HashOrFileSwap, StringComparer.Ordinal).Count()), UiSharedService.AccentColor);
                ImGui.NewLine();
                ImGui.SameLine(pos);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleUp, Loc.Get("CharaDataHub.Edit.UploadMissing")))
                {
                    _charaDataManager.UploadMissingFiles(dataDto.Id);
                }
            }
        }
        else if (hasFiles && !updateDto.IsAppearanceEqual)
        {
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20, 1);
            ImGui.SameLine();
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Edit.NewDataSet"), UiSharedService.AccentColor);
        }

        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Edit.HasManipulation"));
        bool hasManipData = !string.IsNullOrEmpty(updateDto.ManipulationData);
        ImGui.SameLine(200);
        _uiSharedService.BooleanToColoredIcon(hasManipData, false);

        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.HasCustomize"));
        ImGui.SameLine();
        bool hasCustomizeData = !string.IsNullOrEmpty(updateDto.CustomizeData);
        ImGui.SameLine(200);
        _uiSharedService.BooleanToColoredIcon(hasCustomizeData, false);
    }

    private void DrawEditCharaDataGeneral(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcd.Appearance.Title"), FontAwesomeIcon.InfoCircle);
        if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
            SelectedDtoId = string.Empty;
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Edit.Close"));

        string creationTime = dataDto.CreatedDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string updateTime = dataDto.UpdatedDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        string downloadCount = dataDto.DownloadCount.ToString(CultureInfo.CurrentCulture);
        using (ImRaii.Disabled())
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##CreationDate", ref creationTime, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.Created"));
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(20);
        ImGui.SameLine();
        using (ImRaii.Disabled())
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##LastUpdate", ref updateTime, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.Updated"));
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(23);
        ImGui.SameLine();
        using (ImRaii.Disabled())
        {
            ImGui.SetNextItemWidth(50);
            ImGui.InputText("##DlCount", ref downloadCount, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.DownloadCount"));

        string description = updateDto.Description;
        ImGui.SetNextItemWidth(735);
        if (ImGui.InputText("##Description", ref description, 200))
        {
            updateDto.Description = description;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.DescriptionLabel"));
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Appearance.DescriptionHelp"));

        var expiryDate = updateDto.ExpiryDate;
        bool isExpiring = expiryDate != DateTime.MaxValue;
        if (ToggleSwitch.Draw(Loc.Get("CharaDataHub.Mcd.Appearance.Expires"), ref isExpiring))
        {
            updateDto.SetExpiry(isExpiring);
        }
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Appearance.ExpiresHelp"));
        using (ImRaii.Disabled(!isExpiring))
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo(Loc.Get("CharaDataHub.Mcd.Appearance.Year"), expiryDate.Year.ToString(CultureInfo.InvariantCulture)))
            {
                for (int year = DateTime.UtcNow.Year; year < DateTime.UtcNow.Year + 4; year++)
                {
                    if (ImGui.Selectable(year.ToString(CultureInfo.InvariantCulture), year == expiryDate.Year))
                    {
                        updateDto.SetExpiry(year, expiryDate.Month, expiryDate.Day);
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();

            int daysInMonth = DateTime.DaysInMonth(expiryDate.Year, expiryDate.Month);
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo(Loc.Get("CharaDataHub.Mcd.Appearance.Month"), expiryDate.Month.ToString(CultureInfo.InvariantCulture)))
            {
                for (int month = 1; month <= 12; month++)
                {
                    if (ImGui.Selectable(month.ToString(CultureInfo.InvariantCulture), month == expiryDate.Month))
                    {
                        updateDto.SetExpiry(expiryDate.Year, month, expiryDate.Day);
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();

            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo(Loc.Get("CharaDataHub.Mcd.Appearance.Day"), expiryDate.Day.ToString(CultureInfo.InvariantCulture)))
            {
                for (int day = 1; day <= daysInMonth; day++)
                {
                    if (ImGui.Selectable(day.ToString(CultureInfo.InvariantCulture), day == expiryDate.Day))
                    {
                        updateDto.SetExpiry(expiryDate.Year, expiryDate.Month, day);
                    }
                }
                ImGui.EndCombo();
            }
        }
        ImGuiHelpers.ScaledDummy(5);

        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcd.Appearance.Delete")))
            {
                _ = _charaDataManager.DeleteCharaData(dataDto);
                SelectedDtoId = string.Empty;
            }
        }
        if (!UiSharedService.CtrlPressed())
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Appearance.DeleteTooltip"));
        }
    }

    private void DrawEditCharaDataPoses(CharaDataExtendedUpdateDto updateDto)
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcd.Poses.Title"), FontAwesomeIcon.Running);
        var poseCount = updateDto.PoseList.Count();
        using (ImRaii.Disabled(poseCount >= maxPoses))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CharaDataHub.Mcd.Poses.Add")))
            {
                updateDto.AddPose();
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, poseCount == maxPoses))
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcd.Poses.Count"), poseCount, maxPoses));
        ImGuiHelpers.ScaledDummy(5);

        using var indent = ImRaii.PushIndent(10f);
        int poseNumber = 1;

        if (!_uiSharedService.IsInGpose && _charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Mcd.Poses.RequireGpose"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }
        else if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Mcd.Poses.RequireBrio"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        foreach (var pose in updateDto.PoseList)
        {
            ImGui.AlignTextToFramePadding();
            using var id = ImRaii.PushId("pose" + poseNumber);
            ImGui.TextUnformatted(poseNumber.ToString(CultureInfo.InvariantCulture));

            if (pose.Id == null)
            {
                ImGui.SameLine(50);
                _uiSharedService.IconText(FontAwesomeIcon.Plus, UiSharedService.AccentColor);
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.NotUploaded"));
            }

            bool poseHasChanges = updateDto.PoseHasChanges(pose);
            if (poseHasChanges)
            {
                ImGui.SameLine(50);
                _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle, UiSharedService.AccentColor);
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.UnsavedChanges"));
            }

            ImGui.SameLine(75);
            if (pose.Description == null && pose.WorldData == null && pose.PoseData == null)
            {
                UiSharedService.ColorText(Loc.Get("CharaDataHub.Mcd.Poses.ScheduledDeletion"), UiSharedService.AccentColor);
            }
            else
            {
                var desc = pose.Description ?? string.Empty;
                if (ImGui.InputTextWithHint("##description", Loc.Get("CharaDataHub.Mcd.Poses.DescriptionPlaceholder"), ref desc, 100))
                {
                    pose.Description = desc;
                    updateDto.UpdatePoseList();
                }
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcd.Poses.Delete")))
                {
                    updateDto.RemovePose(pose);
                }

                ImGui.SameLine();
                ImGuiHelpers.ScaledDummy(10, 1);
                ImGui.SameLine();
                bool hasPoseData = !string.IsNullOrEmpty(pose.PoseData);
                _uiSharedService.IconText(FontAwesomeIcon.Running, UiSharedService.GetBoolColor(hasPoseData));
                UiSharedService.AttachToolTip(hasPoseData
                    ? Loc.Get("CharaDataHub.Mcd.Poses.HasPoseData")
                    : Loc.Get("CharaDataHub.Mcd.Poses.NoPoseData"));
                ImGui.SameLine();

                using (ImRaii.Disabled(!_uiSharedService.IsInGpose || !(_charaDataManager.AttachingPoseTask?.IsCompleted ?? true) || !_charaDataManager.BrioAvailable))
                {
                    using var poseid = ImRaii.PushId("poseSet" + poseNumber);
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
                    {
                        _charaDataManager.AttachPoseData(pose, updateDto);
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.AttachPose"));
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!hasPoseData))
                {
                    using var poseid = ImRaii.PushId("poseDelete" + poseNumber);
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    {
                        pose.PoseData = string.Empty;
                        updateDto.UpdatePoseList();
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.DeletePoseData"));
                }

                ImGui.SameLine();
                ImGuiHelpers.ScaledDummy(10, 1);
                ImGui.SameLine();
                var worldData = pose.WorldData ?? default;
                bool hasWorldData = worldData != default;
                _uiSharedService.IconText(FontAwesomeIcon.Globe, UiSharedService.GetBoolColor(hasWorldData));
                var tooltipText = !hasWorldData ? Loc.Get("CharaDataHub.WorldDataTooltip.None") : Loc.Get("CharaDataHub.Mcd.Poses.WorldDataPresent");
                if (hasWorldData)
                {
                    tooltipText += UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.Mcd.Poses.WorldDataMap");
                }
                UiSharedService.AttachToolTip(tooltipText);
                if (hasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(position: new Vector3(worldData.PositionX, worldData.PositionY, worldData.PositionZ),
                        _dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map);
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!_uiSharedService.IsInGpose || !(_charaDataManager.AttachingPoseTask?.IsCompleted ?? true) || !_charaDataManager.BrioAvailable))
                {
                    using var worldId = ImRaii.PushId("worldSet" + poseNumber);
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
                    {
                        _charaDataManager.AttachWorldData(pose, updateDto);
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.AttachWorldData"));
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!hasWorldData))
                {
                    using var worldId = ImRaii.PushId("worldDelete" + poseNumber);
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    {
                        pose.WorldData = default(WorldData);
                        updateDto.UpdatePoseList();
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Poses.DeleteWorldData"));
                }
            }

            if (poseHasChanges)
            {
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Undo"))
                {
                    updateDto.RevertDeletion(pose);
                }
            }

            poseNumber++;
        }
    }

    private void DrawMcdOnline()
    {
        if (_onlineDataSubTab == 0)
        {
            DrawMcdfPage();
            return;
        }

        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcd.Online.Title"), FontAwesomeIcon.Database);

        DrawHelpFoldout(Loc.Get("CharaDataHub.Mcd.Online.Help"));

        EnsureMcdOnlineInitialized();

        ImGuiHelpers.ScaledDummy(5);
        DrawMcdOnlineRefreshButton(withLabel: true);

        _uiSharedService.IconText(FontAwesomeIcon.Search, ImGuiColors.DalamudGrey);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##mcdfOnlineSearch", Loc.Get("CharaDataHub.Mcd.Online.Search"), ref _mcdfOnlineSearch, 128);
        ImGuiHelpers.ScaledDummy(3);

        var filteredLiveData = _charaDataManager.OwnCharaData.Values
            .Where(e => string.IsNullOrWhiteSpace(_mcdfOnlineSearch)
                || (e.Description ?? string.Empty).Contains(_mcdfOnlineSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => b.CreatedDate)
            .ToList();

        if (filteredLiveData.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcd.Online.NoLiveEntries"), ImGuiColors.DalamudGrey);
        }
        else
        {
            float liveTableHeight = Math.Min(26f + filteredLiveData.Count * 26f, 300f);
            using (var table = ImRaii.Table("LiveData", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX,
                new Vector2(ImGui.GetContentRegionAvail().X, liveTableHeight)))
            {
                if (table)
                {
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
                    ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Description"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Updated"), ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcdf.OwnShares.Downloads"), ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();

                    foreach (var entry in filteredLiveData)
                    {
                        var uDto = _charaDataManager.GetUpdateDto(entry.Id);
                        ImGui.TableNextRow(ImGuiTableRowFlags.None, 26f);

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        if (string.Equals(entry.Id, SelectedDtoId, StringComparison.Ordinal))
                            _uiSharedService.IconText(FontAwesomeIcon.CaretRight);

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        DrawAddOrRemoveFavorite(entry);
                        ImGui.SameLine();
                        if (uDto?.HasChanges ?? false)
                        {
                            UiSharedService.ColorText(entry.Description, UiSharedService.AccentColor);
                            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.UnsavedEntry"));
                        }
                        else
                        {
                            ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id : entry.Description);
                        }
                        if (ImGui.IsItemClicked()) SelectedDtoId = string.Equals(SelectedDtoId, entry.Id, StringComparison.Ordinal) ? string.Empty : entry.Id;

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(entry.UpdatedDate.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
                        if (ImGui.IsItemClicked()) SelectedDtoId = string.Equals(SelectedDtoId, entry.Id, StringComparison.Ordinal) ? string.Empty : entry.Id;

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(entry.DownloadCount.ToString(CultureInfo.CurrentCulture));
                        if (ImGui.IsItemClicked()) SelectedDtoId = string.Equals(SelectedDtoId, entry.Id, StringComparison.Ordinal) ? string.Empty : entry.Id;

                        ImGui.TableNextColumn();
                        using (ImRaii.PushId("live" + entry.Id))
                        {
                            if (_uiSharedService.IconButton(FontAwesomeIcon.Copy))
                                ImGui.SetClipboardText(entry.Id);
                            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.CopyCode"));
                            ImGui.SameLine();
                            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                            {
                                _ = _charaDataManager.DeleteCharaData(entry);
                                SelectedDtoId = string.Empty;
                            }
                            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Online.DeleteTooltip"));
                        }
                    }
                }
            }
        }

        var liveCount = _charaDataManager.OwnCharaData.Count;

        using (ImRaii.Disabled(!_charaDataManager.Initialized || _charaDataManager.DataCreationTask != null || liveCount >= _charaDataManager.MaxCreatableCharaData))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CharaDataHub.Mcd.Online.NewEntry")))
            {
                var cts = EnsureFreshCts(ref _closalCts);
                _charaDataManager.CreateCharaDataEntry(cts.Token);
                _selectNewEntry = true;
            }
        }
        UiSharedService.AttachToolTip(_charaDataManager.DataCreationTask != null
            ? Loc.Get("CharaDataHub.Mcd.Online.NewEntryCooldown")
            : Loc.Get("CharaDataHub.Mcd.Online.NewEntryTooltip"));

        if (!_charaDataManager.Initialized)
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.InitNotice"));
        }

        if (_charaDataManager.Initialized)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcd.Online.EntryCount"), liveCount, _charaDataManager.MaxCreatableCharaData));
            if (liveCount >= _charaDataManager.MaxCreatableCharaData)
            {
                ImGui.AlignTextToFramePadding();
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcd.Online.EntryMaxed"), UiSharedService.AccentColor);
            }
        }

        if (_charaDataManager.DataCreationTask != null && !_charaDataManager.DataCreationTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcd.Online.Creating"), UiSharedService.AccentColor);
        }
        else if (_charaDataManager.DataCreationTask != null && _charaDataManager.DataCreationTask.IsCompleted)
        {
            var color = _charaDataManager.DataCreationTask.Result.Success ? ImGuiColors.HealerGreen : UiSharedService.AccentColor;
            UiSharedService.ColorTextWrapped(_charaDataManager.DataCreationTask.Result.Output, color);
        }

        UiSharedService.EndSectionCard();

        var charaDataEntries = _charaDataManager.OwnCharaData.Count;
        if (charaDataEntries != _dataEntries && _selectNewEntry && _charaDataManager.OwnCharaData.Any())
        {
            SelectedDtoId = _charaDataManager.OwnCharaData.OrderBy(o => o.Value.CreatedDate).Last().Value.Id;
            _selectNewEntry = false;
        }
        _dataEntries = _charaDataManager.OwnCharaData.Count;

        _ = _charaDataManager.OwnCharaData.TryGetValue(SelectedDtoId, out var dto);
        DrawEditCharaData(dto);
    }

    bool _selectNewEntry = false;
    int _dataEntries = 0;
    int _onlineDataSubTab = 0;

    private Task? _localMcdfScanTask;

    private void ScanLocalMcdfFolder()
    {
        if (_localMcdfScanTask is { IsCompleted: false }) return;

        var folder = _configService.Current.McdfLocalFolder;
        _localMcdfScanTime = DateTime.UtcNow;
        _localMcdfScanTask = Task.Run(() =>
        {
            try
            {
                _localMcdfFiles = ScanLocalMcdfFolderCore(folder);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Scan du dossier MCDF local impossible");
            }
            finally
            {
                _localMcdfScanTime = DateTime.UtcNow;
            }
        });
    }

    private static List<LocalMcdfEntry> ScanLocalMcdfFolderCore(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var results = new List<LocalMcdfEntry>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.mcdf", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                var description = fi.Name.Replace(".mcdf", string.Empty, StringComparison.OrdinalIgnoreCase);
                var relativePath = Path.GetRelativePath(folder, fi.DirectoryName ?? folder);
                var subFolder = string.Equals(relativePath, ".", StringComparison.Ordinal) ? string.Empty : relativePath;
                results.Add(new LocalMcdfEntry(file, fi.Name, description, fi.Length, fi.LastWriteTime, subFolder));
            }
            catch
            {
                // skip unreadable files
            }
        }

        // Inclure les sous-dossiers vides avec une entrée placeholder
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var dirName = Path.GetFileName(dir);
            if (!results.Any(r => string.Equals(r.SubFolder, dirName, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new LocalMcdfEntry(string.Empty, string.Empty, string.Empty, 0, DateTime.MinValue, dirName));
            }
        }

        return results
            .OrderBy(f => f.SubFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawMcdfDownloadPopup()
    {
        if (_mcdfOpenDownloadPopup)
        {
            ImGui.OpenPopup("##mcdfDownloadPopup");
            _mcdfOpenDownloadPopup = false;
        }

        if (!ImGui.BeginPopup("##mcdfDownloadPopup")) return;

        var localFolder = _configService.Current.McdfLocalFolder;
        if (_mcdfDownloadEntry == null || string.IsNullOrEmpty(localFolder))
        {
            ImGui.EndPopup();
            return;
        }

        var description = string.IsNullOrEmpty(_mcdfDownloadEntry.Description)
            ? _mcdfDownloadEntry.Id.ToString("D", CultureInfo.InvariantCulture)
            : _mcdfDownloadEntry.Description;

        // Titre centré
        var titleText = description;
        var titleSize = ImGui.CalcTextSize(titleText);
        var iconWidth = ImGui.GetFrameHeight();
        var totalTitleWidth = iconWidth + ImGui.GetStyle().ItemSpacing.X + titleSize.X;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - totalTitleWidth) / 2f + ImGui.GetCursorPosX());
        _uiSharedService.IconText(FontAwesomeIcon.Download, UiSharedService.AccentColor);
        ImGui.SameLine();
        UiSharedService.ColorText(titleText, UiSharedService.AccentColor);
        ImGui.Separator();

        // Si download en cours ou terminé
        if (_mcdfDownloadTask != null)
        {
            var barColor = new Vector4(96 / 255f, 74 / 255f, 128 / 255f, 0.86f);
            var barBgColor = new Vector4(25 / 255f, 22 / 255f, 28 / 255f, 0.86f);

            if (!_mcdfDownloadTask.IsCompleted)
            {
                var text = Loc.Get("CharaDataHub.Mcdf.Download.InProgress");
                var textSize = ImGui.CalcTextSize(text);
                ImGui.SetCursorPosX((300 - textSize.X) / 2f + ImGui.GetCursorPosX());
                ImGui.TextUnformatted(text);
                using (ImRaii.PushColor(ImGuiCol.PlotHistogram, barColor))
                using (ImRaii.PushColor(ImGuiCol.FrameBg, barBgColor))
                {
                    ImGui.ProgressBar(-1f * (float)DateTime.UtcNow.TimeOfDay.TotalSeconds % 1f, new Vector2(300, 20));
                }
            }
            else if (_mcdfDownloadTask.IsFaulted)
            {
                var text = Loc.Get("CharaDataHub.Mcdf.Download.Failed");
                var textSize = ImGui.CalcTextSize(text);
                ImGui.SetCursorPosX((300 - textSize.X) / 2f + ImGui.GetCursorPosX());
                UiSharedService.ColorText(text, ImGuiColors.DalamudRed);
                ImGuiHelpers.ScaledDummy(3);
                var btnText = Loc.Get("CharaDataHub.Mcdf.Download.Close");
                var btnWidth = ImGui.CalcTextSize(btnText).X + ImGui.GetStyle().FramePadding.X * 2;
                ImGui.SetCursorPosX((300 - btnWidth) / 2f + ImGui.GetCursorPosX());
                if (ImGui.Button(btnText))
                    ImGui.CloseCurrentPopup();
            }
            else
            {
                var text = Loc.Get("CharaDataHub.Mcdf.Download.Done");
                var textSize = ImGui.CalcTextSize(text);
                ImGui.SetCursorPosX((300 - textSize.X) / 2f + ImGui.GetCursorPosX());
                UiSharedService.ColorText(text, ImGuiColors.HealerGreen);
                using (ImRaii.PushColor(ImGuiCol.PlotHistogram, barColor))
                using (ImRaii.PushColor(ImGuiCol.FrameBg, barBgColor))
                {
                    ImGui.ProgressBar(1f, new Vector2(300, 20), "100%");
                }
                ImGuiHelpers.ScaledDummy(3);
                var btnText = Loc.Get("CharaDataHub.Mcdf.Download.Close");
                var btnWidth = ImGui.CalcTextSize(btnText).X + ImGui.GetStyle().FramePadding.X * 2;
                ImGui.SetCursorPosX((300 - btnWidth) / 2f + ImGui.GetCursorPosX());
                if (ImGui.Button(btnText))
                {
                    _localMcdfScanTime = DateTime.MinValue;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
            return;
        }

        // Sélection du dossier
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Download.PickFolder"));
        ImGuiHelpers.ScaledDummy(3);

        // Racine
        bool isRoot = string.IsNullOrEmpty(_mcdfDownloadFolder);
        if (ImGui.RadioButton("/ (racine)", isRoot))
            _mcdfDownloadFolder = string.Empty;

        // Sous-dossiers existants
        if (Directory.Exists(localFolder))
        {
            foreach (var dir in Directory.EnumerateDirectories(localFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var dirName = Path.GetFileName(dir);
                bool selected = string.Equals(_mcdfDownloadFolder, dirName, StringComparison.Ordinal);
                if (ImGui.RadioButton(dirName, selected))
                    _mcdfDownloadFolder = dirName;
            }
        }

        ImGuiHelpers.ScaledDummy(3);

        // Créer un nouveau dossier inline
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##dlNewFolder", Loc.Get("CharaDataHub.Mcdf.Local.NewFolderPlaceholder"), ref _mcdfDownloadNewFolder, 64);
        ImGui.SameLine();
        using (ImRaii.PushId("dlCreateFolder"))
        {
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_mcdfDownloadNewFolder)))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.FolderPlus))
                {
                    var newPath = Path.Combine(localFolder, _mcdfDownloadNewFolder.Trim());
                    try
                    {
                        Directory.CreateDirectory(newPath);
                        _mcdfDownloadFolder = _mcdfDownloadNewFolder.Trim();
                        _mcdfDownloadNewFolder = string.Empty;
                        _localMcdfScanTime = DateTime.MinValue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create folder {Name}", _mcdfDownloadNewFolder);
                    }
                }
            }
        }

        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(3);

        // Bouton télécharger centré
        var dlBtnText = Loc.Get("CharaDataHub.Mcdf.Download.Start");
        var dlBtnWidth = Math.Max(ImGui.CalcTextSize(dlBtnText).X + ImGui.GetStyle().FramePadding.X * 2, 200);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - dlBtnWidth) / 2f + ImGui.GetCursorPosX());
        using (ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor))
        if (ImGui.Button(dlBtnText, new Vector2(dlBtnWidth, 28)))
        {
            var safeName = string.Join("_", description.Split(Path.GetInvalidFileNameChars()));
            var targetDir = string.IsNullOrEmpty(_mcdfDownloadFolder) ? localFolder : Path.Combine(localFolder, _mcdfDownloadFolder);
            var targetPath = Path.Combine(targetDir, safeName + ".mcdf");
            _mcdfDownloadTask = _mcdfShareManager.DownloadShareToFileAsync(_mcdfDownloadEntry, targetPath, CancellationToken.None);
        }

        ImGui.EndPopup();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} o";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} Ko";
        if (bytes < 1024 * 1024 * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} Mo";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} Go";
    }

    private void DrawSpecific(CharaDataExtendedUpdateDto updateDto)
    {
        UiSharedService.DrawTree(Loc.Get("CharaDataHub.Mcd.Specific.Title"), () =>
        {
            using (ImRaii.PushId("user"))
            {
                using (ImRaii.Group())
                {
                    InputComboHybrid("##AliasToAdd", "##AliasToAddPicker", ref _specificIndividualAdd, _pairManager.DirectPairs,
                        static pair => (pair.UserData.UID, pair.UserData.Alias, pair.UserData.AliasOrUID, pair.GetNoteOrName()));
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrEmpty(_specificIndividualAdd)
                        || updateDto.UserList.Any(f => string.Equals(f.UID, _specificIndividualAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificIndividualAdd, StringComparison.Ordinal))))
                    {
                        if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
                        {
                            updateDto.AddUserToList(_specificIndividualAdd);
                            _specificIndividualAdd = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Specific.UserLabel"));
                    _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Specific.UserHelp") + UiSharedService.TooltipSeparator
                        + Loc.Get("CharaDataHub.Mcd.Specific.UserHelpNote"));

                    using (var lb = ImRaii.ListBox(Loc.Get("CharaDataHub.Mcd.Specific.AllowedIndividuals"), new(200, 200)))
                    {
                        foreach (var user in updateDto.UserList)
                        {
                            var userString = string.IsNullOrEmpty(user.Alias) ? user.UID : $"{user.Alias} ({user.UID})";
                            if (ImGui.Selectable(userString, string.Equals(user.UID, _selectedSpecificUserIndividual, StringComparison.Ordinal)))
                            {
                                _selectedSpecificUserIndividual = user.UID;
                            }
                        }
                    }

                    using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificUserIndividual)))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcd.Specific.RemoveUser")))
                        {
                            updateDto.RemoveUserFromList(_selectedSpecificUserIndividual);
                            _selectedSpecificUserIndividual = string.Empty;
                        }
                    }
                }
            }
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20);
            ImGui.SameLine();

            using (ImRaii.PushId("group"))
            {
                using (ImRaii.Group())
                {
                    InputComboHybrid("##GroupAliasToAdd", "##GroupAliasToAddPicker", ref _specificGroupAdd, _pairManager.Groups.Keys,
                        group => (group.GID, group.Alias, group.AliasOrGID, _serverConfigurationManager.GetNoteForGid(group.GID)));
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrEmpty(_specificGroupAdd)
                        || updateDto.GroupList.Any(f => string.Equals(f.GID, _specificGroupAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificGroupAdd, StringComparison.Ordinal))))
                    {
                        if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
                        {
                            updateDto.AddGroupToList(_specificGroupAdd);
                            _specificGroupAdd = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Specific.GroupLabel"));
                    _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Specific.GroupHelp") + UiSharedService.TooltipSeparator
                        + Loc.Get("CharaDataHub.Mcd.Specific.GroupHelpNote"));

                    using (var lb = ImRaii.ListBox(Loc.Get("CharaDataHub.Mcd.Specific.AllowedGroups"), new(200, 200)))
                    {
                        foreach (var group in updateDto.GroupList)
                        {
                            var userString = string.IsNullOrEmpty(group.Alias) ? group.GID : $"{group.Alias} ({group.GID})";
                            if (ImGui.Selectable(userString, string.Equals(group.GID, _selectedSpecificGroupIndividual, StringComparison.Ordinal)))
                            {
                                _selectedSpecificGroupIndividual = group.GID;
                            }
                        }
                    }

                    using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificGroupIndividual)))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("CharaDataHub.Mcd.Specific.RemoveGroup")))
                        {
                            updateDto.RemoveGroupFromList(_selectedSpecificGroupIndividual);
                            _selectedSpecificGroupIndividual = string.Empty;
                        }
                    }
                }
            }

            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(5);
        });
    }

    private void InputComboHybrid<T>(string inputId, string comboId, ref string value, IEnumerable<T> comboEntries,
        Func<T, (string Id, string? Alias, string AliasOrId, string? Note)> parseEntry)
    {
        const float ComponentWidth = 200;
        ImGui.SetNextItemWidth(ComponentWidth - ImGui.GetFrameHeight());
        ImGui.InputText(inputId, ref value, 20);
        ImGui.SameLine(0.0f, 0.0f);

        using var combo = ImRaii.Combo(comboId, string.Empty, ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft);
        if (!combo)
        {
            return;
        }

        if (_openComboHybridEntries is null || !string.Equals(_openComboHybridId, comboId, StringComparison.Ordinal))
        {
            var valueSnapshot = value;
            _openComboHybridEntries = comboEntries
                .Select(parseEntry)
                .Where(entry => entry.Id.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase)
                    || (entry.Alias is not null && entry.Alias.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase))
                    || (entry.Note is not null && entry.Note.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.Note is null ? entry.AliasOrId : $"{entry.Note} ({entry.AliasOrId})", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _openComboHybridId = comboId;
        }
        _comboHybridUsedLastFrame = true;

        // Is there a better way to handle this?
        var width = ComponentWidth - 2 * ImGui.GetStyle().FramePadding.X - (_openComboHybridEntries.Length > 8 ? ImGui.GetStyle().ScrollbarSize : 0);
        foreach (var (id, alias, aliasOrId, note) in _openComboHybridEntries)
        {
            var selected = !string.IsNullOrEmpty(value)
                && (string.Equals(id, value, StringComparison.Ordinal) || string.Equals(alias, value, StringComparison.Ordinal));
            using var font = ImRaii.PushFont(UiBuilder.MonoFont, note is null);
            if (ImGui.Selectable(note is null ? aliasOrId : $"{note} ({aliasOrId})", selected, ImGuiSelectableFlags.None, new(width, 0)))
            {
                value = aliasOrId;
            }
        }
    }
}