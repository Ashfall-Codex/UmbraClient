using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private void DrawNearbyPoses()
    {
        _uiSharedService.BigText(Loc.Get("CharaDataHub.NearbyPoses.Title"));

        DrawHelpFoldout(Loc.Get("CharaDataHub.NearbyPoses.Help"));

        UiSharedService.DrawTree(Loc.Get("CharaDataHub.NearbyPoses.SettingsTitle"), () =>
        {
            string filterByUser = _charaDataNearbyManager.UserNoteFilter;
            if (ImGui.InputTextWithHint("##filterbyuser", Loc.Get("CharaDataHub.NearbyPoses.FilterByUser"), ref filterByUser, 50))
            {
                _charaDataNearbyManager.UserNoteFilter = filterByUser;
            }
            bool onlyCurrent = _configService.Current.NearbyOwnServerOnly;
            if (ImGui.Checkbox(Loc.Get("CharaDataHub.NearbyPoses.OnlyCurrentWorld"), ref onlyCurrent))
            {
                _configService.Current.NearbyOwnServerOnly = onlyCurrent;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.OnlyCurrentWorldHelp"));
            bool showOwn = _configService.Current.NearbyShowOwnData;
            if (ImGui.Checkbox(Loc.Get("CharaDataHub.NearbyPoses.ShowOwnData"), ref showOwn))
            {
                _configService.Current.NearbyShowOwnData = showOwn;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.ShowOwnDataHelp"));
            bool ignoreHousing = _configService.Current.NearbyIgnoreHousingLimitations;
            if (ImGui.Checkbox(Loc.Get("CharaDataHub.NearbyPoses.IgnoreHousing"), ref ignoreHousing))
            {
                _configService.Current.NearbyIgnoreHousingLimitations = ignoreHousing;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.IgnoreHousingHelp") + UiSharedService.TooltipSeparator
                + Loc.Get("CharaDataHub.NearbyPoses.IgnoreHousingNote"));
            bool showWisps = _configService.Current.NearbyDrawWisps;
            if (ImGui.Checkbox(Loc.Get("CharaDataHub.NearbyPoses.ShowWisps"), ref showWisps))
            {
                _configService.Current.NearbyDrawWisps = showWisps;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.ShowWispsHelp"));
            int maxWisps = _configService.Current.NearbyMaxWisps;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderInt(Loc.Get("CharaDataHub.NearbyPoses.MaxWisps"), ref maxWisps, 0, 200))
            {
                _configService.Current.NearbyMaxWisps = maxWisps;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.MaxWispsHelp"));
            int poseDetectionDistance = _configService.Current.NearbyDistanceFilter;
            ImGui.SetNextItemWidth(100);
            if (ImGui.SliderInt(Loc.Get("CharaDataHub.NearbyPoses.DetectionDistance"), ref poseDetectionDistance, 5, 1000))
            {
                _configService.Current.NearbyDistanceFilter = poseDetectionDistance;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.DetectionDistanceHelp"));
            bool alwaysShow = _configService.Current.NearbyShowAlways;
            if (ImGui.Checkbox(Loc.Get("CharaDataHub.NearbyPoses.ShowAlways"), ref alwaysShow))
            {
                _configService.Current.NearbyShowAlways = alwaysShow;
                _configService.Save();
            }
            _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.NearbyPoses.ShowAlwaysHelp") + UiSharedService.TooltipSeparator
                + Loc.Get("CharaDataHub.NearbyPoses.ShowAlwaysNote"));
        });

        if (!_uiSharedService.IsInGpose)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.NearbyPoses.GposeOnly"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        DrawUpdateSharedDataButton();

        UiSharedService.DistanceSeparator();

        using var child = ImRaii.Child("nearbyPosesChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);

        ImGuiHelpers.ScaledDummy(3f);

        using var indent = ImRaii.PushIndent(5f);
        if (_charaDataNearbyManager.NearbyData.Count == 0)
        {
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.NearbyPoses.NoneFound"), UiSharedService.AccentColor);
        }

        bool wasAnythingHovered = false;
        int i = 0;
        foreach (var pose in _charaDataNearbyManager.NearbyData.OrderBy(v => v.Value.Distance))
        {
            using var poseId = ImRaii.PushId("nearbyPose" + (i++));
            var pos = ImGui.GetCursorPos();
            var circleDiameter = 60f;
            var circleOriginX = ImGui.GetWindowContentRegionMax().X - circleDiameter - pos.X;
            float circleOffsetY = 0;

            UiSharedService.DrawGrouped(() =>
            {
                string? userNote = _serverConfigurationManager.GetNoteForUid(pose.Key.MetaInfo.Uploader.UID);
                var noteText = pose.Key.MetaInfo.IsOwnData ? Loc.Get("CharaDataHub.NearbyPoses.YouLabel") : (userNote == null ? pose.Key.MetaInfo.Uploader.AliasOrUID : $"{userNote} ({pose.Key.MetaInfo.Uploader.AliasOrUID})");
                ImGui.TextUnformatted(Loc.Get("CharaDataHub.NearbyPoses.PoseBy"));
                ImGui.SameLine();
                UiSharedService.ColorText(noteText, ImGuiColors.ParsedGreen);
                using (ImRaii.Group())
                {
                    UiSharedService.ColorText(Loc.Get("CharaDataHub.NearbyPoses.CharaDescriptionLabel"), ImGuiColors.DalamudGrey);
                    ImGui.SameLine();
                    _uiSharedService.IconText(FontAwesomeIcon.ExternalLinkAlt, ImGuiColors.DalamudGrey);
                }
                UiSharedService.AttachToolTip(pose.Key.MetaInfo.Description);
                UiSharedService.ColorText(Loc.Get("CharaDataHub.NearbyPoses.DescriptionLabel"), ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                UiSharedService.TextWrapped(pose.Key.Description ?? Loc.Get("CharaDataHub.NearbyPoses.NoDescription"), circleOriginX);
                var posAfterGroup = ImGui.GetCursorPos();
                var groupHeightCenter = (posAfterGroup.Y - pos.Y) / 2;
                circleOffsetY = (groupHeightCenter - circleDiameter / 2);
                if (circleOffsetY < 0) circleOffsetY = 0;
                ImGui.SetCursorPos(new Vector2(circleOriginX, pos.Y));
                ImGui.Dummy(new Vector2(circleDiameter, circleDiameter));
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.NearbyPoses.MapTooltip") + UiSharedService.TooltipSeparator
                    + pose.Key.WorldDataDescriptor);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(pose.Key.Position, pose.Key.Map);
                }
                ImGui.SetCursorPos(posAfterGroup);
                if (_uiSharedService.IsInGpose)
                {
                    _ = GposePoseAction(currentStart =>
                    {
                        ImGui.SetCursorPosX(currentStart);
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, Loc.Get("CharaDataHub.NearbyPoses.ApplyPose")))
                        {
                            _charaDataManager.ApplyFullPoseDataToGposeTarget(pose.Key);
                        }

                        return ImGui.GetCursorPosX();
                    }, string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.NearbyPoses.ApplyPoseTooltip"), CharaName(_gposeTarget)), _hasValidGposeTarget, ImGui.GetCursorPosX());
                    ImGui.SameLine();
                    GposeMetaInfoAction((_) =>
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CharaDataHub.NearbyPoses.SpawnAndPose")))
                        {
                            _charaDataManager.SpawnAndApplyWorldTransform(pose.Key.MetaInfo, pose.Key);
                        }
                    }, Loc.Get("CharaDataHub.NearbyPoses.SpawnAndPoseTooltip"), pose.Key.MetaInfo, _hasValidGposeTarget, true);
                }
            });
            if (ImGui.IsItemHovered())
            {
                wasAnythingHovered = true;
                _nearbyHovered = pose.Key;
            }
            var drawList = ImGui.GetWindowDrawList();
            var circleRadius = circleDiameter / 2f;
            var windowPos = ImGui.GetWindowPos();
            var scrollX = ImGui.GetScrollX();
            var scrollY = ImGui.GetScrollY();
            var circleCenter = new Vector2(windowPos.X + circleOriginX + circleRadius - scrollX, windowPos.Y + pos.Y + circleRadius + circleOffsetY - scrollY);
            var rads = pose.Value.Direction * (Math.PI / 180);

            float halfConeAngleRadians = 15f * (float)Math.PI / 180f;
            Vector2 baseDir1 = new Vector2((float)Math.Sin(rads - halfConeAngleRadians), -(float)Math.Cos(rads - halfConeAngleRadians));
            Vector2 baseDir2 = new Vector2((float)Math.Sin(rads + halfConeAngleRadians), -(float)Math.Cos(rads + halfConeAngleRadians));

            Vector2 coneBase1 = circleCenter + baseDir1 * circleRadius;
            Vector2 coneBase2 = circleCenter + baseDir2 * circleRadius;

            // Draw the cone as a filled triangle
            drawList.AddTriangleFilled(circleCenter, coneBase1, coneBase2, UiSharedService.Color(ImGuiColors.ParsedGreen));
            drawList.AddCircle(circleCenter, circleDiameter / 2, UiSharedService.Color(ImGuiColors.DalamudWhite), 360, 2);
            var distance = pose.Value.Distance.ToString("0.0", CultureInfo.CurrentCulture) + "y";
            var textSize = ImGui.CalcTextSize(distance);
            drawList.AddText(new Vector2(circleCenter.X - textSize.X / 2, circleCenter.Y + textSize.Y / 3f), UiSharedService.Color(ImGuiColors.DalamudWhite), distance);

            ImGuiHelpers.ScaledDummy(3);
        }

        if (!wasAnythingHovered) _nearbyHovered = null;
        _charaDataNearbyManager.SetHoveredVfx(_nearbyHovered);
    }

    private void DrawUpdateSharedDataButton()
    {
        using (ImRaii.Disabled(_charaDataManager.GetAllDataTask != null
            || (_charaDataManager.GetSharedWithYouTimeoutTask != null && !_charaDataManager.GetSharedWithYouTimeoutTask.IsCompleted)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleDown, Loc.Get("CharaDataHub.NearbyPoses.UpdateShared")))
            {
                var cts = EnsureFreshCts(ref _disposalCts);
                _ = _charaDataManager.GetAllSharedData(cts.Token);
            }
        }
        if (_charaDataManager.GetSharedWithYouTimeoutTask != null && !_charaDataManager.GetSharedWithYouTimeoutTask.IsCompleted)
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.NearbyPoses.UpdateSharedCooldown"));
        }
    }
}