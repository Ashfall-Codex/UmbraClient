using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Handlers;

namespace UmbraSync.UI.Components;

public class DrawUserPair : DrawPairBase
{
    private static readonly Vector4 Violet = new(0.63f, 0.25f, 1f, 1f);
    protected readonly MareMediator _mediator;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly CharaDataManager _charaDataManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public void UpdateData()
    {
        // DrawUserPair uses _pair which is already updated in the PairManager
    }

    public DrawUserPair(string id, Pair entry, UidDisplayHandler displayHandler, ApiController apiController,
        MareMediator mareMediator, SelectGroupForPairUi selectGroupForPairUi,
        UiSharedService uiSharedService, CharaDataManager charaDataManager,
        ServerConfigurationManager serverConfigurationManager)
        : base(id, entry, apiController, displayHandler, uiSharedService)
    {
        if (_pair.UserPair == null) throw new ArgumentException("Pair must be UserPair", nameof(entry));
        _pair = entry;
        _selectGroupForPairUi = selectGroupForPairUi;
        _mediator = mareMediator;
        _charaDataManager = charaDataManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    public bool IsOnline => _pair.IsOnline;
    public bool IsVisible => _pair.IsVisible;
    public UserPairDto UserPair => _pair.UserPair!;

    protected override float GetRightSideExtraWidth()
    {
        float width = 0f;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;

        var individualSoundsDisabled = (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

        if (individualSoundsDisabled || individualAnimDisabled || individualVFXDisabled)
        {
            width += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ExclamationTriangle).X + spacingX * 0.5f;
        }

        if (_charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out _))
        {
            width += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Running).X + spacingX * 0.5f;
        }

        width += spacingX * 1.2f;
        return width;
    }

    protected override float GetLeftSideReservedWidth()
    {
        var style = ImGui.GetStyle();
        float spacing = style.ItemSpacing.X;
        float iconW = UiSharedService.GetIconSize(FontAwesomeIcon.Moon).X;

        int icons = 1;
        if (!(_pair.UserPair!.OwnPermissions.IsPaired() && _pair.UserPair!.OtherPermissions.IsPaired()))
            icons++;
        else if (_pair.UserPair!.OwnPermissions.IsPaused() || _pair.UserPair!.OtherPermissions.IsPaused())
            icons++;
        if (_pair.IsOnline && _pair.IsVisible)
            icons++;

        float iconsTotal = icons * iconW + Math.Max(0, icons - 1) * spacing;
        float cushion = spacing * 0.6f;
        return iconsTotal + cushion;
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        var online = _pair.IsOnline;
        var offlineGrey = ImGuiColors.DalamudGrey3;

        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        UiSharedService.ColorText(FontAwesomeIcon.Moon.ToIconString(), online ? Violet : offlineGrey);
        ImGui.PopFont();
        UiSharedService.AttachToolTip(online
            ? Loc.Get("DrawUserPair.Online")
            : Loc.Get("DrawUserPair.Offline"));
        if (!(_pair.UserPair!.OwnPermissions.IsPaired() && _pair.UserPair!.OtherPermissions.IsPaired()))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.ArrowUp.ToIconString(), UiSharedService.AccentColor);
            ImGui.PopFont();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.NotMutual"), _pair.UserData.AliasOrUID));
        }
        else if (_pair.UserPair!.OwnPermissions.IsPaused() || _pair.UserPair!.OtherPermissions.IsPaused())
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.PauseCircle.ToIconString(), ImGuiColors.DalamudYellow);
            ImGui.PopFont();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Paused"), _pair.UserData.AliasOrUID));
        }
        if (_pair is { IsOnline: true, IsVisible: true })
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.Eye.ToIconString(), Violet);
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(_pair));
            }
            ImGui.PopFont();
            var visibleTooltip = string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.VisibleHeader"), _pair.UserData.AliasOrUID, _pair.PlayerName!)
                + Environment.NewLine + Loc.Get("DrawUserPair.VisibleTarget");
            if (_pair.LastAppliedDataBytes >= 0)
            {
                visibleTooltip += UiSharedService.TooltipSeparator;
                visibleTooltip += ((!_pair.IsVisible) ? Loc.Get("DrawUserPair.VisibleLastPrefix") : string.Empty) + Loc.Get("DrawUserPair.VisibleMods") + Environment.NewLine;
                visibleTooltip += string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.VisibleFiles"), UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true));
                if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                {
                    visibleTooltip += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.VisibleVram"), UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true));
                }
                if (_pair.LastAppliedDataTris >= 0)
                {
                    var tris = _pair.LastAppliedDataTris > 1000 ? (_pair.LastAppliedDataTris / 1000d).ToString("0.0'k'", CultureInfo.CurrentCulture) : _pair.LastAppliedDataTris.ToString(CultureInfo.CurrentCulture);
                    visibleTooltip += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.VisibleTris"), tris);
                }
            }

            UiSharedService.AttachToolTip(visibleTooltip);
        }
    }

    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pauseIcon = _pair.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseIconSize = _uiSharedService.GetIconButtonSize(pauseIcon);
        var barButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars);
        var entryUID = _pair.UserData.AliasOrUID;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var edgePadding = UiSharedService.GetCardContentPaddingX() + 6f * ImGuiHelpers.GlobalScale;
        var rightEdgeGap = spacingX * 2f + ImGui.GetStyle().FramePadding.X;
        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - edgePadding - rightEdgeGap;
        var rightSidePos = windowEndX - barButtonSize.X;

        // Flyout Menu
        ImGui.SameLine(rightSidePos);
        ImGui.SetCursorPosY(originalY);

        // Use a unique popup ID per user to avoid rendering the same popup twice
        // when multiple list items call BeginPopup with the same name in the same frame.
        var popupId = $"User Flyout Menu##{_pair.UserData.UID}";

        bool buttonClicked;
        using (ImRaii.PushId($"info-{_pair.UserData.UID}"))
        {
            buttonClicked = _uiSharedService.IconButton(FontAwesomeIcon.Bars);
        }
        if (buttonClicked)
        {
            ImGui.OpenPopup(popupId);
        }
        if (ImGui.BeginPopup(popupId))
        {
            using (ImRaii.PushId($"buttons-{_pair.UserData.UID}")) DrawPairedClientMenu(_pair);
            ImGui.EndPopup();
        }

        rightSidePos -= pauseIconSize.X + spacingX;
        ImGui.SameLine(rightSidePos);
        ImGui.SetCursorPosY(originalY);
        using (ImRaii.PushId($"pause-{_pair.UserData.UID}"))
        {
            if (pauseIcon == FontAwesomeIcon.Pause ? _uiSharedService.IconPauseButtonCentered() : _uiSharedService.IconButtonCentered(pauseIcon))
            {
                _apiController.Pause(_pair.UserData);
            }
            var pauseKey = !_pair.IsPaused ? "DrawUserPair.Pause" : "DrawUserPair.Resume";
            UiSharedService.AttachToolTip(AppendSeenInfo(string.Format(CultureInfo.CurrentCulture, Loc.Get(pauseKey), entryUID)));
        }


        var individualSoundsDisabled = (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);
        if (individualSoundsDisabled || individualAnimDisabled || individualVFXDisabled)
        {
            var icon = FontAwesomeIcon.ExclamationTriangle;
            var iconwidth = _uiSharedService.GetIconButtonSize(icon);

            rightSidePos -= iconwidth.X + spacingX / 2f;
            ImGui.SameLine(rightSidePos);

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            _uiSharedService.IconText(icon);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted(Loc.Get("DrawUserPair.Permissions.Header"));

                if (individualSoundsDisabled)
                {
                    var userSoundsText = string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Permissions.SoundDisabled"), _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.VolumeMute);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Get("DrawUserPair.Permissions.StatusLine"),
                        _pair.UserPair!.OwnPermissions.IsDisableSounds() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled"),
                        _pair.UserPair!.OtherPermissions.IsDisableSounds() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled")));
                }

                if (individualAnimDisabled)
                {
                    var userAnimText = string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Permissions.AnimDisabled"), _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.WindowClose);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Get("DrawUserPair.Permissions.StatusLine"),
                        _pair.UserPair!.OwnPermissions.IsDisableAnimations() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled"),
                        _pair.UserPair!.OtherPermissions.IsDisableAnimations() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled")));
                }

                if (individualVFXDisabled)
                {
                    var userVFXText = string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Permissions.VfxDisabled"), _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.TimesCircle);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userVFXText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(
                        CultureInfo.CurrentCulture,
                        Loc.Get("DrawUserPair.Permissions.StatusLine"),
                        _pair.UserPair!.OwnPermissions.IsDisableVFX() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled"),
                        _pair.UserPair!.OtherPermissions.IsDisableVFX() ? Loc.Get("Common.Disabled") : Loc.Get("Common.Enabled")));
                }

                ImGui.EndTooltip();
            }
        }

        if (_charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData))
        {
            var icon = FontAwesomeIcon.Running;
            var iconwidth = _uiSharedService.GetIconButtonSize(icon);
            rightSidePos -= iconwidth.X + spacingX / 2f;
            ImGui.SameLine(rightSidePos);
            _uiSharedService.IconText(icon);

            UiSharedService.AttachToolTip(
                string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.SharedData"), sharedData.Count)
                + UiSharedService.TooltipSeparator
                + Loc.Get("DrawUserPair.SharedDataHint"));

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(_pair.UserData));
            }
        }

        return rightSidePos - spacingX;
    }

    private void DrawPairedClientMenu(Pair entry)
    {
        if (entry.IsVisible && _uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("DrawUserPair.Menu.Target")))
        {
            _mediator.Publish(new TargetPairMessage(entry));
            ImGui.CloseCurrentPopup();
        }
        if (!entry.IsEffectivelyPaused && _uiSharedService.IconTextButton(FontAwesomeIcon.User, Loc.Get("DrawUserPair.Menu.Profile")))
        {
            _displayHandler.OpenProfile(entry);
            ImGui.CloseCurrentPopup();
        }
        if (!entry.IsEffectivelyPaused)
        {
            UiSharedService.AttachToolTip(Loc.Get("DrawUserPair.Menu.ProfileTooltip"));
        }
        if (entry.IsVisible)
        {
#if DEBUG
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("DrawUserPair.Menu.Analysis")))
            {
                _displayHandler.OpenAnalysis(_pair);
                ImGui.CloseCurrentPopup();
            }
#endif
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, Loc.Get("DrawUserPair.Menu.Reload")))
            {
                entry.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip(Loc.Get("DrawUserPair.Menu.ReloadTooltip"));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, Loc.Get("DrawUserPair.Menu.CyclePause")))
        {
            _apiController.Pause(entry.UserData);
            ImGui.CloseCurrentPopup();
        }
        var entryUID = entry.UserData.AliasOrUID;
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Folder, Loc.Get("DrawUserPair.Menu.PairGroups")))
        {
            _selectGroupForPairUi.Open(entry);
        }
        UiSharedService.AttachToolTip(AppendSeenInfo(string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Menu.PairGroupsTooltip"), entryUID)));

        var isDisableSounds = entry.UserPair!.OwnPermissions.IsDisableSounds();
        string disableSoundsText = Loc.Get(isDisableSounds ? "DrawUserPair.Menu.EnableSounds" : "DrawUserPair.Menu.DisableSounds");
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
        if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _mediator.Publish(new PairSyncOverrideChanged(entry.UserData.UID, permissions.IsDisableSounds(), null, null));
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableAnims = entry.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = Loc.Get(isDisableAnims ? "DrawUserPair.Menu.EnableAnim" : "DrawUserPair.Menu.DisableAnim");
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
        if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _mediator.Publish(new PairSyncOverrideChanged(entry.UserData.UID, null, permissions.IsDisableAnimations(), null));
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableVFX = entry.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = Loc.Get(isDisableVFX ? "DrawUserPair.Menu.EnableVfx" : "DrawUserPair.Menu.DisableVfx");
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;
        if (_uiSharedService.IconTextButton(disableVFXIcon, disableVFXText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _mediator.Publish(new PairSyncOverrideChanged(entry.UserData.UID, null, null, permissions.IsDisableVFX()));
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("DrawUserPair.Menu.Unpair")) && UiSharedService.CtrlPressed())
        {
            _ = _apiController.UserRemovePair(new(entry.UserData));
        }
        UiSharedService.AttachToolTip(AppendSeenInfo(string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.Menu.UnpairTooltip"), entryUID)));
    }

    private string AppendSeenInfo(string tooltip)
    {
        if (_pair.IsVisible) return tooltip;

        var lastSeen = _serverConfigurationManager.GetNameForUid(_pair.UserData.UID);
        if (string.IsNullOrWhiteSpace(lastSeen)) return tooltip;

        return tooltip + " " + string.Format(CultureInfo.CurrentCulture, Loc.Get("DrawUserPair.SeenAs"), lastSeen);
    }
}