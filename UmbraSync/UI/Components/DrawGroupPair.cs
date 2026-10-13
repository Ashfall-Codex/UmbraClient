using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Handlers;

namespace UmbraSync.UI.Components;

public class DrawGroupPair : DrawPairBase
{
    protected readonly MareMediator _mediator;
    private GroupPairFullInfoDto _fullInfoDto;
    private GroupFullInfoDto _group;
    private readonly CharaDataManager _charaDataManager;
    private readonly AutoDetectRequestService _autoDetectRequestService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly MareConfigService _mareConfig;
    public void UpdateData(GroupFullInfoDto group, GroupPairFullInfoDto fullInfoDto)
    {
        _group = group;
        _fullInfoDto = fullInfoDto;
    }

    public DrawGroupPair(string id, Pair entry, ApiController apiController,
        MareMediator mareMediator, GroupFullInfoDto group, GroupPairFullInfoDto fullInfoDto,
        UidDisplayHandler handler, UiSharedService uiSharedService, CharaDataManager charaDataManager,
        AutoDetectRequestService autoDetectRequestService, ServerConfigurationManager serverConfigurationManager,
        MareConfigService mareConfig)
        : base(id, entry, apiController, handler, uiSharedService)
    {
        _group = group;
        _fullInfoDto = fullInfoDto;
        _mediator = mareMediator;
        _charaDataManager = charaDataManager;
        _autoDetectRequestService = autoDetectRequestService;
        _serverConfigurationManager = serverConfigurationManager;
        _mareConfig = mareConfig;
    }

    protected override float GetRightSideExtraWidth()
    {
        float width = 0f;
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        bool showShared = _charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out _);

        var localOverride = _mareConfig.Current.PairSyncOverrides.TryGetValue(_pair.UserData.UID, out var ovW) ? ovW : null;
        var soundsDisabled = _fullInfoDto.GroupUserPermissions.IsDisableSounds();
        var animDisabled = _fullInfoDto.GroupUserPermissions.IsDisableAnimations();
        var vfxDisabled = _fullInfoDto.GroupUserPermissions.IsDisableVFX();
        var individualSoundsDisabled = (localOverride?.DisableSounds ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (localOverride?.DisableAnimations ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (localOverride?.DisableVfx ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);
        bool showInfo = individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled || animDisabled || soundsDisabled || vfxDisabled;
        bool showPlus = _pair.UserPair == null && _pair.IsOnline;

        if (showShared)
            width += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Running).X + spacing * 0.5f;
        if (showInfo)
        {
            var icon = (individualSoundsDisabled || individualAnimDisabled || individualVFXDisabled)
                ? FontAwesomeIcon.ExclamationTriangle
                : FontAwesomeIcon.InfoCircle;
            width += UiSharedService.GetIconSize(icon).X + spacing * 0.5f;
        }
        if (showPlus)
        {
            width += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X + spacing * 0.5f;
        }

        width += spacing * 1.2f;
        return width;
    }

    protected override float GetLeftSideReservedWidth()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        bool showRole = _fullInfoDto.GroupPairStatusInfo.IsModerator()
            || string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal)
            || _fullInfoDto.GroupPairStatusInfo.IsPinned();

        float roleWidth = 0f;
        if (showRole)
        {
            var roleIcon = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal)
                ? FontAwesomeIcon.Crown
                : (_fullInfoDto.GroupPairStatusInfo.IsModerator() ? FontAwesomeIcon.UserShield : FontAwesomeIcon.Thumbtack);
            roleWidth = UiSharedService.GetIconSize(roleIcon).X;
        }

        float total = MathF.Max(GetStatusIconsWidth(), StatusSlotWidth);

        if (showRole)
        {
            total += spacing + roleWidth;
        }

        total += spacing * 0.6f;
        return total;
    }

    // Colonne d'icônes de statut à largeur fixe : la lune, le nuage et l'œil n'ont pas la même
    // largeur, les noms se retrouvaient décalés d'une ligne à l'autre.
    private static float StatusSlotWidth => MathF.Max(UiSharedService.GetIconSize(FontAwesomeIcon.CloudMoon).X,
        MathF.Max(UiSharedService.GetIconSize(FontAwesomeIcon.Moon).X, UiSharedService.GetIconSize(FontAwesomeIcon.Eye).X));

    private float GetStatusIconsWidth()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        bool individuallyPaired = _pair.UserPair != null;
        bool showPrefix = _pair.IsEffectivelyPaused || (individuallyPaired && (_pair.IsOnline || _pair.IsVisible));

        float width = 0f;
        if (showPrefix)
        {
            var prefixIcon = _pair.IsEffectivelyPaused ? FontAwesomeIcon.PauseCircle : FontAwesomeIcon.Moon;
            width += UiSharedService.GetIconSize(prefixIcon).X;
        }

        bool hideCloudMoon = showPrefix && !_pair.IsEffectivelyPaused && !_pair.IsVisible;
        if (!hideCloudMoon)
        {
            if (showPrefix) width += spacing * 1.2f;
            var presenceIcon = _pair.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.CloudMoon;
            float presenceWidth = UiSharedService.GetIconSize(presenceIcon).X;
            if (_pair.IsVisible && _displayHandler.TryGetPresenceAvatar(_pair, out _))
                presenceWidth = Math.Max(presenceWidth, UidDisplayHandler.AvatarSize);
            width += presenceWidth;
        }
        return width;
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        float iconsWidth = GetStatusIconsWidth();
        if (iconsWidth < StatusSlotWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (StatusSlotWidth - iconsWidth) / 2f);

        var entryUID = _pair.UserData.AliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var presenceIcon = _pair.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.CloudMoon;
        var presenceColor = (_pair.IsOnline || _pair.IsVisible) ? new Vector4(0.63f, 0.25f, 1f, 1f) : ImGuiColors.DalamudGrey;
        var presenceText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Offline"), entryUID);

        ImGui.SetCursorPosY(textPosY);
        bool drewPrefixIcon = false;

        if (_pair.IsEffectivelyPaused)
        {
            presenceText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Unknown"), entryUID);

            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.PauseCircle.ToIconString(), ImGuiColors.DalamudYellow);
            ImGui.PopFont();

            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Paused"), entryUID));
            drewPrefixIcon = true;
        }
        else
        {
            bool individuallyPaired = _pair.UserPair != null;
            var violet = new Vector4(0.63f, 0.25f, 1f, 1f);
            if (individuallyPaired && (_pair.IsOnline || _pair.IsVisible))
            {
                ImGui.PushFont(UiBuilder.IconFont);
                UiSharedService.ColorText(FontAwesomeIcon.Moon.ToIconString(), violet);
                ImGui.PopFont();
                UiSharedService.AttachToolTip(Loc.Get("GroupPair.IndividuallyPaired.Short"));
                drewPrefixIcon = true;
            }
        }
        bool hideCloudMoon = drewPrefixIcon && !_pair.IsEffectivelyPaused && !_pair.IsVisible;

        if (!hideCloudMoon)
        {
            if (drewPrefixIcon)
                ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X * 1.2f);

            ImGui.SetCursorPosY(textPosY);
            if (_pair.IsVisible && _displayHandler.TryGetPresenceAvatar(_pair, out var avatarProfile))
            {
                var avatarSize = UidDisplayHandler.AvatarSize;
                var iconHeight = UiSharedService.GetIconSize(FontAwesomeIcon.Moon).Y;
                var avatarPos = ImGui.GetCursorScreenPos();
                avatarPos = new Vector2(avatarPos.X, avatarPos.Y + iconHeight / 2f - avatarSize / 2f);
                ImGui.SetCursorScreenPos(avatarPos);
                ImGui.InvisibleButton("##presenceAvatar", new Vector2(avatarSize));
                _displayHandler.DrawPresenceAvatar(_pair, avatarProfile, avatarPos, avatarSize);
            }
            else
            {
                ImGui.PushFont(UiBuilder.IconFont);
                UiSharedService.ColorText(presenceIcon.ToIconString(), presenceColor);
                ImGui.PopFont();
            }

            if (_pair.IsOnline && !_pair.IsVisible) presenceText = Loc.Get("GroupPair.OnlineSyncshellOnly");
            else if (_pair.IsOnline && _pair.IsVisible) presenceText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.VisibleHeader"), entryUID, _pair.PlayerName) + Environment.NewLine + Loc.Get("GroupPair.VisibleTarget");

            if (_pair.IsVisible)
            {
                if (ImGui.IsItemClicked())
                {
                    _mediator.Publish(new TargetPairMessage(_pair));
                }
                if (_pair.LastAppliedDataBytes >= 0)
                {
                    presenceText += UiSharedService.TooltipSeparator;
                    presenceText += ((!_pair.IsVisible) ? Loc.Get("GroupPair.VisibleLastPrefix") : string.Empty) + Loc.Get("GroupPair.VisibleMods") + Environment.NewLine;
                    presenceText += string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.VisibleFiles"), UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true));
                    if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                    {
                        presenceText += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.VisibleVram"), UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true));
                    }
                    if (_pair.LastAppliedDataTris >= 0)
                    {
                        presenceText += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.VisibleTris"),
                            _pair.LastAppliedDataTris > 1000 ? (_pair.LastAppliedDataTris / 1000d).ToString("0.0'k'", CultureInfo.CurrentCulture) : _pair.LastAppliedDataTris.ToString(CultureInfo.CurrentCulture));
                    }
                }
            }

            UiSharedService.AttachToolTip(presenceText);
        }

        if (entryIsOwner)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(Loc.Get("GroupPair.Owner"));
        }
        else if (entryIsMod)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(Loc.Get("GroupPair.Moderator"));
        }
        else if (entryIsPinned)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Thumbtack.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(Loc.Get("GroupPair.Pinned"));
        }
    }

    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var entryUID = _fullInfoDto.UserAliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var userIsOwner = string.Equals(_group.OwnerUID, _apiController.UID, StringComparison.OrdinalIgnoreCase);
        var userIsModerator = _group.GroupUserInfo.IsModerator();

        var localOverride = _mareConfig.Current.PairSyncOverrides.TryGetValue(_pair.UserData.UID, out var ovR) ? ovR : null;
        var soundsDisabled = _fullInfoDto.GroupUserPermissions.IsDisableSounds();
        var animDisabled = _fullInfoDto.GroupUserPermissions.IsDisableAnimations();
        var vfxDisabled = _fullInfoDto.GroupUserPermissions.IsDisableVFX();
        var individualSoundsDisabled = (localOverride?.DisableSounds ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (localOverride?.DisableAnimations ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (localOverride?.DisableVfx ?? false) || (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

        bool showShared = _charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData);
        bool showInfo = (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled || animDisabled || soundsDisabled || vfxDisabled);
        bool showPlus = _pair.UserPair == null && _pair.IsOnline;
        bool showBars = true;
        bool showPause = true;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var permIcon = (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled)
            ? FontAwesomeIcon.ExclamationTriangle
            : ((soundsDisabled || animDisabled || vfxDisabled) ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.None);
        float runningW = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Running).X;
        float infoMaxW = MathF.Max(
            UiSharedService.GetIconSize(FontAwesomeIcon.ExclamationTriangle).X,
            UiSharedService.GetIconSize(FontAwesomeIcon.InfoCircle).X
        );
        float plusW = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;
        bool pausedByYou = _pair.UserPair != null
            ? _pair.UserPair.OwnPermissions.IsPaused() : _fullInfoDto.GroupUserPermissions.IsPaused();
        var pauseIcon = pausedByYou ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        float pauseMaxW = MathF.Max(
            _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).X,
            _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play).X
        );
        float barsW = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.EllipsisH).X;
        float rightEdgeGap = spacing * 1.2f;
        float totalWidth = runningW + spacing
            + (showInfo ? infoMaxW + spacing : 0f)
            + (showPlus ? plusW + spacing : 0f)
            + pauseMaxW + spacing + barsW;

        float cardPaddingX = UiSharedService.GetCardContentPaddingX();
        float rightMargin = cardPaddingX + 6f * ImGuiHelpers.GlobalScale;
        float baseX = MathF.Max(
            ImGui.GetCursorPosX(),
            ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - rightMargin - rightEdgeGap - totalWidth
        );
        float currentX = baseX;

        ImGui.PushID($"grpPair-{_group.Group}-{_pair.UserData.UID}");

        ImGui.SameLine();
        ImGui.SetCursorPosX(baseX);
        ImGui.SetCursorPosY(textPosY);
        if (showShared)
        {
            _uiSharedService.IconText(FontAwesomeIcon.Running);
            UiSharedService.AttachToolTip($"This user has shared {sharedData!.Count} Character Data Sets with you." + UiSharedService.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(_pair.UserData));
            }
        }
        currentX += runningW + spacing;
        ImGui.SetCursorPosX(currentX);

        // Icône info/avertissement (à gauche du +)
        if (showInfo && permIcon != FontAwesomeIcon.None)
        {
            ImGui.SetCursorPosY(textPosY);
            float infoActualW = UiSharedService.GetIconSize(permIcon).X;
            ImGui.SetCursorPosX(currentX + (infoMaxW - infoActualW));
            if (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                _uiSharedService.IconText(permIcon);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();

                    ImGui.TextUnformatted(Loc.Get("GroupPair.Permissions.Header"));
                    if (_pair.UserPair == null)
                    {
                        UiSharedService.ColorText(Loc.Get("GroupPair.Permissions.SyncshellOnly"), ImGuiColors.DalamudGrey);
                    }

                    if (individualSoundsDisabled)
                    {
                        _uiSharedService.IconText(FontAwesomeIcon.VolumeMute);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(Loc.Get("GroupPair.Permissions.SoundLabel"));
                    }

                    if (individualAnimDisabled)
                    {
                        _uiSharedService.IconText(FontAwesomeIcon.WindowClose);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(Loc.Get("GroupPair.Permissions.AnimLabel"));
                    }

                    if (individualVFXDisabled)
                    {
                        _uiSharedService.IconText(FontAwesomeIcon.TimesCircle);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(Loc.Get("GroupPair.Permissions.VfxLabel"));
                    }

                    ImGui.EndTooltip();
                }
            }
            else
            {
                _uiSharedService.IconText(permIcon);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();

                    ImGui.TextUnformatted(Loc.Get("GroupPair.SyncshellPermissions.Header"));

                    if (soundsDisabled)
                    {
                        var userSoundsText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.SyncshellPermissions.SoundDisabled"), _pair.UserData.AliasOrUID);
                        _uiSharedService.IconText(FontAwesomeIcon.VolumeMute);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userSoundsText);
                    }

                    if (animDisabled)
                    {
                        var userAnimText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.SyncshellPermissions.AnimDisabled"), _pair.UserData.AliasOrUID);
                        _uiSharedService.IconText(FontAwesomeIcon.WindowClose);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userAnimText);
                    }

                    if (vfxDisabled)
                    {
                        var userVFXText = string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.SyncshellPermissions.VfxDisabled"), _pair.UserData.AliasOrUID);
                        _uiSharedService.IconText(FontAwesomeIcon.TimesCircle);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userVFXText);
                    }

                    ImGui.EndTooltip();
                }
            }
            currentX += infoMaxW + spacing;
            ImGui.SetCursorPosX(currentX);
        }

        // Bouton + (invitation pair)
        ImGui.SetCursorPosY(originalY);
        if (showPlus)
        {
            if (_uiSharedService.IconPlusButtonCentered())
            {
                var targetUid = _pair.UserData.UID;
                if (!string.IsNullOrEmpty(targetUid))
                {
                    _ = SendGroupPairInviteAsync(targetUid, entryUID);
                }
            }
            UiSharedService.AttachToolTip(AppendSeenInfo("Send pairing invite to " + entryUID));
            currentX += plusW + spacing;
            ImGui.SetCursorPosX(currentX);
        }

        // Slot 4: Pause/Play
        ImGui.SetCursorPosY(originalY);
        if (showPause)
        {
            using (ImRaii.PushId($"pause-{_pair.UserData.UID}"))
            {
                if (pauseIcon == FontAwesomeIcon.Pause ? _uiSharedService.IconPauseButtonCentered() : _uiSharedService.IconButtonCentered(pauseIcon))
                {
                    if (_pair.UserPair != null)
                    {
                        _mediator.Publish(new PauseMessage(_pair.UserData));
                    }
                    else
                    {
                        _mediator.Publish(new GroupPairPauseMessage(_group.Group, _pair.UserData, _fullInfoDto.GroupUserPermissions));
                    }
                }
                UiSharedService.AttachToolTip(AppendSeenInfo((pausedByYou ? "Resume" : "Pause") + " syncing with " + entryUID));
            }
        }
        currentX += pauseMaxW + spacing;
        ImGui.SetCursorPosX(currentX);
        if (showBars)
        {
            ImGui.SetCursorPosY(originalY);
            var popupId = $"Syncshell Flyout Menu##{_pair.UserData.UID}";
            bool buttonClicked;
            using (ImRaii.PushId($"info-{_pair.UserData.UID}"))
            {
                buttonClicked = _uiSharedService.IconButtonCentered(FontAwesomeIcon.EllipsisH);
            }
            if (buttonClicked)
            {
                ImGui.OpenPopup(popupId);
            }
        }
        currentX += barsW; // avance quand même pour cohérence interne
        ImGui.SetCursorPosX(currentX);
        // Must match the ID used in OpenPopup above
        var popupMenuId = $"Syncshell Flyout Menu##{_pair.UserData.UID}";
        using (PopupMenu.PushStyle(270f))
        {
            if (ImGui.BeginPopup(popupMenuId))
            {
                using (ImRaii.PushId($"menu-{_pair.UserData.UID}"))
                    DrawMemberMenu(userIsOwner, userIsModerator, entryIsOwner, entryIsMod, entryIsPinned, localOverride);
                ImGui.EndPopup();
            }
        }

        ImGui.PopID();

        return baseX - spacing;
    }

    // Même présentation que le menu des paires individuelles : actions, synchronisation, puis
    // la modération en bas, les actions destructrices en rouge.
    private void DrawMemberMenu(bool userIsOwner, bool userIsModerator, bool entryIsOwner, bool entryIsMod,
        bool entryIsPinned, SyncOverrideEntry? localOverride)
    {
        var entryName = _pair.UserData.AliasOrUID;

        if (_pair.IsVisible && PopupMenu.Row(FontAwesomeIcon.Eye, Loc.Get("DrawUserPair.Menu.Target"), "target"))
        {
            _mediator.Publish(new TargetPairMessage(_pair));
            ImGui.CloseCurrentPopup();
        }
        if (!_pair.IsEffectivelyPaused && PopupMenu.Row(FontAwesomeIcon.User, Loc.Get("DrawUserPair.Menu.Profile"), "profile"))
        {
            _displayHandler.OpenProfile(_pair);
            ImGui.CloseCurrentPopup();
        }
        if (_pair.IsVisible)
        {
#if DEBUG
            if (PopupMenu.Row(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("DrawUserPair.Menu.Analysis"), "analysis"))
            {
                _displayHandler.OpenAnalysis(_pair);
                ImGui.CloseCurrentPopup();
            }
#endif
            if (PopupMenu.Row(FontAwesomeIcon.Sync, Loc.Get("DrawUserPair.Menu.Reload"), "reload"))
            {
                _pair.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip(Loc.Get("DrawUserPair.Menu.ReloadTooltip"));
        }

        PopupMenu.Section(Loc.Get("DrawUserPair.Menu.SyncHeader"));
        var uid = _pair.UserData.UID;

        var isDisableSounds = localOverride?.DisableSounds ?? (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.VolumeUp, Loc.Get("Syncshell.Cards.Perm.Sound"), !isDisableSounds, "sounds"))
        {
            var newState = !isDisableSounds;
            _mediator.Publish(new PairSyncOverrideChanged(uid, newState, null, null));
            if (_pair.UserPair != null)
            {
                var permissions = _pair.UserPair.OwnPermissions;
                permissions.SetDisableSounds(newState);
                _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
            }
            _pair.ApplyLastReceivedData(forced: true);
        }

        var isDisableAnims = localOverride?.DisableAnimations ?? (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Running, Loc.Get("Syncshell.Cards.Perm.Anim"), !isDisableAnims, "anims"))
        {
            var newState = !isDisableAnims;
            _mediator.Publish(new PairSyncOverrideChanged(uid, null, newState, null));
            if (_pair.UserPair != null)
            {
                var permissions = _pair.UserPair.OwnPermissions;
                permissions.SetDisableAnimations(newState);
                _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
            }
            _pair.ApplyLastReceivedData(forced: true);
        }

        var isDisableVFX = localOverride?.DisableVfx ?? (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Sun, Loc.Get("Syncshell.Cards.Perm.Vfx"), !isDisableVFX, "vfx"))
        {
            var newState = !isDisableVFX;
            _mediator.Publish(new PairSyncOverrideChanged(uid, null, null, newState));
            if (_pair.UserPair != null)
            {
                var permissions = _pair.UserPair.OwnPermissions;
                permissions.SetDisableVFX(newState);
                _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
            }
            _pair.ApplyLastReceivedData(forced: true);
        }

        var isDisableHousing = localOverride?.DisableHousingMods ?? (_pair.UserPair?.OwnPermissions.IsDisableHousing() ?? false);
        if (PopupMenu.ToggleRow(FontAwesomeIcon.Home, Loc.Get("Syncshell.Cards.Perm.Housing"), !isDisableHousing, "housing"))
        {
            var newState = !isDisableHousing;
            _mediator.Publish(new PairSyncOverrideChanged(uid, null, null, null, newState));
            if (_pair.UserPair != null)
            {
                var permissions = _pair.UserPair.OwnPermissions;
                permissions.SetDisableHousing(newState);
                _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(_pair.UserData, permissions));
            }
        }

        UiSharedService.DrawTargetSoundOverrideCombo(_mareConfig, uid, "##pair_sound_");

        bool canModerate = (userIsModerator || userIsOwner) && !(entryIsMod || entryIsOwner);
        if (!canModerate && !userIsOwner) return;

        PopupMenu.Section(Loc.Get("GroupPair.Menu.ModerationHeader"));
        var danger = ImGuiColors.DalamudRed;

        if (canModerate)
        {
            if (PopupMenu.Row(FontAwesomeIcon.Thumbtack, Loc.Get(entryIsPinned ? "GroupPair.Menu.Unpin" : "GroupPair.Menu.Pin"), "pin"))
            {
                ImGui.CloseCurrentPopup();
                var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsPinned;
                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
            }
            UiSharedService.AttachToolTip(Loc.Get("GroupPair.Menu.PinTooltip"));
        }

        if (userIsOwner)
        {
            if (PopupMenu.Row(FontAwesomeIcon.UserShield, Loc.Get(entryIsMod ? "GroupPair.Menu.Demote" : "GroupPair.Menu.Promote"), "mod")
                && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsModerator;
                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
            }
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Menu.ModTooltip"), _fullInfoDto.UserAliasOrUID));
        }

        if (canModerate)
        {
            PopupMenu.Divider();
            if (PopupMenu.Row(FontAwesomeIcon.UserMinus, Loc.Get("GroupPair.Menu.Remove"), "remove", danger, danger) && UiSharedService.CtrlPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupRemoveUser(_fullInfoDto);
            }
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Menu.RemoveTooltip"), entryName));

            if (PopupMenu.Row(FontAwesomeIcon.UserSlash, Loc.Get("GroupPair.Menu.Ban"), "ban", danger, danger))
            {
                ImGui.CloseCurrentPopup();
                _mediator.Publish(new OpenBanUserPopupMessage(_pair, _group));
            }
            UiSharedService.AttachToolTip(Loc.Get("GroupPair.Menu.BanTooltip"));
        }

        if (userIsOwner)
        {
            var warn = ImGuiColors.DalamudOrange;
            if (PopupMenu.Row(FontAwesomeIcon.Crown, Loc.Get("GroupPair.Menu.Transfer"), "transfer", warn, warn)
                && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                ImGui.CloseCurrentPopup();
                _ = _apiController.GroupChangeOwnership(_fullInfoDto);
            }
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("GroupPair.Menu.TransferTooltip"), _fullInfoDto.UserAliasOrUID));
        }
    }

    private string AppendSeenInfo(string tooltip)
    {
        if (_pair.IsVisible) return tooltip;

        var lastSeen = _serverConfigurationManager.GetNameForUid(_pair.UserData.UID);
        if (string.IsNullOrWhiteSpace(lastSeen)) return tooltip;

        return tooltip + " (Vu sous : " + lastSeen + ")";
    }

    private async Task SendGroupPairInviteAsync(string targetUid, string displayName)
    {
        try
        {
            await _autoDetectRequestService.SendDirectUidRequestAsync(targetUid, displayName).ConfigureAwait(false);
        }
        catch
        {
            // errors are logged within the request service; ignore here
        }
    }
}