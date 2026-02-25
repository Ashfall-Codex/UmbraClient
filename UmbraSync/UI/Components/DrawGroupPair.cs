using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Data;
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

        bool individuallyPaired = _pair.UserPair != null;
        bool showPrefix = _pair.IsEffectivelyPaused || (individuallyPaired && (_pair.IsOnline || _pair.IsVisible));
        bool showRole = _fullInfoDto.GroupPairStatusInfo.IsModerator()
            || string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal)
            || _fullInfoDto.GroupPairStatusInfo.IsPinned();

        float prefixWidth = 0f;
        if (showPrefix)
        {
            var prefixIcon = _pair.IsEffectivelyPaused ? FontAwesomeIcon.PauseCircle : FontAwesomeIcon.Moon;
            prefixWidth = UiSharedService.GetIconSize(prefixIcon).X;
        }

        var presenceIcon = _pair.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.CloudMoon;
        float presenceWidth = UiSharedService.GetIconSize(presenceIcon).X;

        float roleWidth = 0f;
        if (showRole)
        {
            var roleIcon = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal)
                ? FontAwesomeIcon.Crown
                : (_fullInfoDto.GroupPairStatusInfo.IsModerator() ? FontAwesomeIcon.UserShield : FontAwesomeIcon.Thumbtack);
            roleWidth = UiSharedService.GetIconSize(roleIcon).X;
        }

        float total = 0f;
        bool hideCloudMoon = !_pair.IsEffectivelyPaused && individuallyPaired && _pair.IsOnline && !_pair.IsVisible;
        if (showPrefix)
        {
            total += prefixWidth + spacing * 1.2f;
        }

        if (!hideCloudMoon)
            total += presenceWidth;

        if (showRole)
        {
            total += spacing + roleWidth;
        }

        total += spacing * 0.6f;
        return total;
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
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
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(presenceIcon.ToIconString(), presenceColor);
            ImGui.PopFont();

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
        var pauseIcon = _pair.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        float pauseMaxW = MathF.Max(
            _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).X,
            _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play).X
        );
        float barsW = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars).X;
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
                    _apiController.Pause(_pair.UserData);
                }
                UiSharedService.AttachToolTip(AppendSeenInfo((_pair.IsPaused ? "Resume" : "Pause") + " syncing with " + entryUID));
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
                buttonClicked = _uiSharedService.IconButtonCentered(FontAwesomeIcon.Bars);
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
        if (ImGui.BeginPopup(popupMenuId))
        {
            if ((userIsModerator || userIsOwner) && !(entryIsMod || entryIsOwner))
            {
                var pinText = entryIsPinned ? "Désépingler" : "Épingler";
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Thumbtack, pinText))
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsPinned;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip("Épingler cet utilisateur à la Syncshell. Les utilisateurs épinglés ne seront pas supprimés lors d'un nettoyage manuel.");

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Retirer") && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupRemoveUser(_fullInfoDto);
                }

                UiSharedService.AttachToolTip("Maintenez CTRL et cliquez pour retirer " + (_pair.UserData.AliasOrUID) + " de la Syncshell");
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, "Bannir"))
                {
                    ImGui.CloseCurrentPopup();
                    _mediator.Publish(new OpenBanUserPopupMessage(_pair, _group));
                }
                UiSharedService.AttachToolTip("Bannir cet utilisateur de la Syncshell");
            }

            if (userIsOwner)
            {
                string modText = entryIsMod ? "Retirer modérateur" : "Promouvoir modérateur";
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserShield, modText) && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsModerator;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip("Maintenez CTRL pour changer le statut de modérateur de " + (_fullInfoDto.UserAliasOrUID) + Environment.NewLine +
                    "Les modérateurs peuvent exclure, bannir/débannir, épingler/désépingler les utilisateurs et nettoyer la Syncshell.");
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crown, "Transférer la propriété") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupChangeOwnership(_fullInfoDto);
                }
                UiSharedService.AttachToolTip("Maintenez CTRL+SHIFT et cliquez pour transférer la propriété de cette Syncshell à " + (_fullInfoDto.UserAliasOrUID) + Environment.NewLine + "ATTENTION : Cette action est irréversible.");
            }

            if (userIsOwner || (userIsModerator && !(entryIsMod || entryIsOwner)))
                ImGui.Separator();

            if (_pair.IsVisible && _uiSharedService.IconTextButton(FontAwesomeIcon.Eye, "Cibler le joueur"))
            {
                _mediator.Publish(new TargetPairMessage(_pair));
                ImGui.CloseCurrentPopup();
            }
            if (!_pair.IsEffectivelyPaused && _uiSharedService.IconTextButton(FontAwesomeIcon.User, "Ouvrir le profil"))
            {
                _displayHandler.OpenProfile(_pair);
                ImGui.CloseCurrentPopup();
            }

#pragma warning disable S1199 // UI menu flow
            {
                ImGui.Separator();

                var uid = _pair.UserData.UID;

                var isDisableSounds = localOverride?.DisableSounds
                    ?? (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false);
                var disableSoundsText = Loc.Get(isDisableSounds ? "DrawUserPair.Menu.EnableSounds" : "DrawUserPair.Menu.DisableSounds");
                var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
                if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText))
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

                var isDisableAnims = localOverride?.DisableAnimations
                    ?? (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false);
                var disableAnimsText = Loc.Get(isDisableAnims ? "DrawUserPair.Menu.EnableAnim" : "DrawUserPair.Menu.DisableAnim");
                var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
                if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText))
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

                var isDisableVFX = localOverride?.DisableVfx
                    ?? (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false);
                var disableVFXText = Loc.Get(isDisableVFX ? "DrawUserPair.Menu.EnableVfx" : "DrawUserPair.Menu.DisableVfx");
                var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;
                if (_uiSharedService.IconTextButton(disableVFXIcon, disableVFXText))
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
            }

            if (_pair.IsVisible)
            {
#if DEBUG
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, "Open Analysis"))
                {
                    _displayHandler.OpenAnalysis(_pair);
                    ImGui.CloseCurrentPopup();
                }
#endif
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Recharger les données"))
                {
                    _pair.ApplyLastReceivedData(forced: true);
                    ImGui.CloseCurrentPopup();
                }
                UiSharedService.AttachToolTip("Réapplique les dernières données de personnage reçues");
            }
            ImGui.EndPopup();
        }

        ImGui.PopID();

        return baseX - spacing;
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