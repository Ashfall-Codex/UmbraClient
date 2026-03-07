using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Localization;
using UmbraSync.Services.CharaData.Models;

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
                            var color = UiSharedService.GetBoolColor(_charaDataManager.UploadTask.Result.Success);
                            UiSharedService.ColorTextWrapped(_charaDataManager.UploadTask.Result.Output, color);
                        }
                    });
                }
                else if (_charaDataManager.UploadTask?.IsCompleted ?? false)
                {
                    var color = UiSharedService.GetBoolColor(_charaDataManager.UploadTask.Result.Success);
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
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataAppearance(dataDto, updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataPoses(updateDto);
    }

    private void DrawEditCharaDataAccessAndSharing(CharaDataExtendedUpdateDto updateDto)
    {
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Mcd.Access.Title"));

        ImGui.SetNextItemWidth(200);
        var dtoAccessType = updateDto.AccessType;
        if (ImGui.BeginCombo(Loc.Get("CharaDataHub.Mcd.Access.RestrictionsLabel"), GetAccessTypeString(dtoAccessType)))
        {
            foreach (var accessType in Enum.GetValues(typeof(AccessTypeDto)).Cast<AccessTypeDto>())
            {
                if (ImGui.Selectable(GetAccessTypeString(accessType), accessType == dtoAccessType))
                {
                    updateDto.AccessType = accessType;
                }
            }

            ImGui.EndCombo();
        }
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Access.Help") + UiSharedService.TooltipSeparator
            + Loc.Get("CharaDataHub.Mcd.Access.Specified") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.DirectPairs") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.AllPairs") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.Everyone") + UiSharedService.TooltipSeparator
            + Loc.Get("CharaDataHub.Mcd.Access.NoteCode") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.NotePause") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.NoteSpecific"));

        DrawSpecific(updateDto);

        ImGui.SetNextItemWidth(200);
        var dtoShareType = updateDto.ShareType;
        using (ImRaii.Disabled(dtoAccessType == AccessTypeDto.Public))
        {
            if (ImGui.BeginCombo(Loc.Get("CharaDataHub.Mcd.Access.SharingLabel"), GetShareTypeString(dtoShareType)))
            {
                foreach (var shareType in Enum.GetValues(typeof(ShareTypeDto)).Cast<ShareTypeDto>())
                {
                    if (ImGui.Selectable(GetShareTypeString(shareType), shareType == dtoShareType))
                    {
                        updateDto.ShareType = shareType;
                    }
                }

                ImGui.EndCombo();
            }
        }
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcd.Access.SharingHelp") + UiSharedService.TooltipSeparator
            + Loc.Get("CharaDataHub.Mcd.Access.CodeOnly") + Environment.NewLine
            + Loc.Get("CharaDataHub.Mcd.Access.Shared") + UiSharedService.TooltipSeparator
            + Loc.Get("CharaDataHub.Mcd.Access.SharedNote"));

        ImGuiHelpers.ScaledDummy(10f);
    }

    private void DrawEditCharaDataAppearance(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        _uiSharedService.BigText("Appearance");

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, "Set Appearance to Current Appearance"))
        {
            _charaDataManager.SetAppearanceData(dataDto.Id);
        }
        _uiSharedService.DrawHelpText("This will overwrite the appearance data currently stored in this Character Data entry with your current appearance.");
        ImGui.SameLine();
        using (ImRaii.Disabled(dataDto.HasMissingFiles || !updateDto.IsAppearanceEqual || _charaDataManager.DataApplicationTask != null))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.CheckCircle, "Preview Saved Apperance on Self"))
            {
                _charaDataManager.ApplyDataToSelf(dataDto);
            }
        }
        _uiSharedService.DrawHelpText("This will download and apply the saved character data to yourself. Once loaded it will automatically revert itself within 15 seconds." + UiSharedService.TooltipSeparator
            + "Note: Weapons will not be displayed correctly unless using the same job as the saved data.");

        ImGui.TextUnformatted("Contains Glamourer Data");
        ImGui.SameLine();
        bool hasGlamourerdata = !string.IsNullOrEmpty(updateDto.GlamourerData);
        ImGui.SameLine(200);
        _uiSharedService.BooleanToColoredIcon(hasGlamourerdata, false);

        ImGui.TextUnformatted("Contains Files");
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
            ImGui.TextUnformatted($"{dataDto.FileGamePaths.DistinctBy(k => k.HashOrFileSwap).Count()} unique file hashes (original upload: {dataDto.OriginalFiles.DistinctBy(k => k.HashOrFileSwap).Count()} file hashes)");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            ImGui.TextUnformatted($"{dataDto.FileGamePaths.Count} associated game paths");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            ImGui.TextUnformatted($"{dataDto.FileSwaps.Count} file swaps");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            if (!dataDto.HasMissingFiles)
            {
                UiSharedService.ColorTextWrapped("All files to download this character data are present on the server", ImGuiColors.HealerGreen);
            }
            else
            {
                UiSharedService.ColorTextWrapped($"{dataDto.MissingFiles.DistinctBy(k => k.HashOrFileSwap).Count()} files to download this character data are missing on the server", UiSharedService.AccentColor);
                ImGui.NewLine();
                ImGui.SameLine(pos);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleUp, "Attempt to upload missing files and restore Character Data"))
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
            UiSharedService.ColorTextWrapped("New data was set. It may contain files that require to be uploaded (will happen on Saving to server)", UiSharedService.AccentColor);
        }

        ImGui.TextUnformatted("Contains Manipulation Data");
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
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Mcd.Appearance.Title"));
        string code = dataDto.FullId;
        using (ImRaii.Disabled())
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputText("##CharaDataCode", ref code, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcd.Appearance.CodeLabel"));
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Copy))
        {
            ImGui.SetClipboardText(code);
        }
        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Appearance.CopyTooltip"));

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
        if (ImGui.Checkbox(Loc.Get("CharaDataHub.Mcd.Appearance.Expires"), ref isExpiring))
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
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Mcd.Poses.Title"));
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
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Mcd.Online.Title"));

        DrawHelpFoldout("In this tab you can create, view and edit your own Character Data that is stored on the server." + Environment.NewLine + Environment.NewLine
            + "Character Data Online functions similar to the previous MCDF standard for exporting your character, except that you do not have to send a file to the other person but solely a code." + Environment.NewLine + Environment.NewLine
            + "There would be a bit too much to explain here on what you can do here in its entirety, however, all elements in this tab have help texts attached what they are used for. Please review them carefully." + Environment.NewLine + Environment.NewLine
            + "Be mindful that when you share your Character Data with other people there is a chance that, with the help of unsanctioned 3rd party plugins, your appearance could be stolen irreversibly, just like when using MCDF.");

        ImGuiHelpers.ScaledDummy(5);
        using (ImRaii.Disabled((!_charaDataManager.GetAllDataTask?.IsCompleted ?? false)
            || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleDown, Loc.Get("CharaDataHub.Mcd.Online.DownloadAll")))
            {
                var cts = EnsureFreshCts(ref _disposalCts);
                _ = _charaDataManager.GetAllData(cts.Token);
            }
        }
        if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
        {
            UiSharedService.AttachToolTip("You can only refresh all character data from server every minute. Please wait.");
        }

        using (var table = ImRaii.Table("Own Character Data", 12, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
            new Vector2(ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X, 110)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Code"));
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Description"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Created"));
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Updated"));
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.DownloadCount"), ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Downloadable"), ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Files"), ImGuiTableColumnFlags.WidthFixed, 32);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Glamourer"), ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Customize"), ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcd.Online.Table.Expires"), ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                foreach (var entry in _charaDataManager.OwnCharaData.Values.OrderBy(b => b.CreatedDate))
                {
                    var uDto = _charaDataManager.GetUpdateDto(entry.Id);
                    ImGui.TableNextColumn();
                    if (string.Equals(entry.Id, SelectedDtoId, StringComparison.Ordinal))
                        _uiSharedService.IconText(FontAwesomeIcon.CaretRight);

                    ImGui.TableNextColumn();
                    DrawAddOrRemoveFavorite(entry);

                    ImGui.TableNextColumn();
                    var idText = entry.FullId;
                    if (uDto?.HasChanges ?? false)
                    {
                        UiSharedService.ColorText(idText, UiSharedService.AccentColor);
                        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.UnsavedEntry"));
                    }
                    else
                    {
                        ImGui.TextUnformatted(idText);
                    }
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.Description);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    UiSharedService.AttachToolTip(entry.Description);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.CreatedDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.UpdatedDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.DownloadCount.ToString(CultureInfo.CurrentCulture));
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    bool isDownloadable = !entry.HasMissingFiles
                        && !string.IsNullOrEmpty(entry.GlamourerData);
                    _uiSharedService.BooleanToColoredIcon(isDownloadable, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    UiSharedService.AttachToolTip(isDownloadable ? Loc.Get("CharaDataHub.Mcd.Online.Downloadable") : Loc.Get("CharaDataHub.Mcd.Online.NotDownloadable"));

                    ImGui.TableNextColumn();
                    var count = entry.FileGamePaths.Concat(entry.FileSwaps).Count();
                    ImGui.TextUnformatted(count.ToString(CultureInfo.CurrentCulture));
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    UiSharedService.AttachToolTip(count == 0 ? Loc.Get("CharaDataHub.Mcd.Online.NoFiles") : Loc.Get("CharaDataHub.Mcd.Online.HasFiles"));

                    ImGui.TableNextColumn();
                    bool hasGlamourerData = !string.IsNullOrEmpty(entry.GlamourerData);
                    _uiSharedService.BooleanToColoredIcon(hasGlamourerData, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    UiSharedService.AttachToolTip(string.IsNullOrEmpty(entry.GlamourerData) ? Loc.Get("CharaDataHub.Mcd.Online.NoGlamourer") : Loc.Get("CharaDataHub.Mcd.Online.HasGlamourer"));

                    ImGui.TableNextColumn();
                    bool hasCustomizeData = !string.IsNullOrEmpty(entry.CustomizeData);
                    _uiSharedService.BooleanToColoredIcon(hasCustomizeData, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    UiSharedService.AttachToolTip(string.IsNullOrEmpty(entry.CustomizeData) ? Loc.Get("CharaDataHub.Mcd.Online.NoCustomize") : Loc.Get("CharaDataHub.Mcd.Online.HasCustomize"));

                    ImGui.TableNextColumn();
                    FontAwesomeIcon eIcon = FontAwesomeIcon.None;
                    if (!Equals(DateTime.MaxValue, entry.ExpiryDate))
                        eIcon = FontAwesomeIcon.Clock;
                    _uiSharedService.IconText(eIcon, UiSharedService.AccentColor);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    if (eIcon != FontAwesomeIcon.None)
                    {
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcd.Online.ExpiresOn"), entry.ExpiryDate.ToLocalTime()));
                    }
                }
            }
        }

        using (ImRaii.Disabled(!_charaDataManager.Initialized || _charaDataManager.DataCreationTask != null || _charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CharaDataHub.Mcd.Online.NewEntry")))
            {
                var cts = EnsureFreshCts(ref _closalCts);
                _charaDataManager.CreateCharaDataEntry(cts.Token);
                _selectNewEntry = true;
            }
        }
        if (_charaDataManager.DataCreationTask != null)
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.NewEntryCooldown"));
        }
        if (!_charaDataManager.Initialized)
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcd.Online.InitNotice"));
        }

        if (_charaDataManager.Initialized)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcd.Online.EntryCount"), _charaDataManager.OwnCharaData.Count, _charaDataManager.MaxCreatableCharaData));
            if (_charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData)
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

        ImGuiHelpers.ScaledDummy(10);
        ImGui.Separator();

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