using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.UI;

public class PermissionWindowUI : WindowMediatorSubscriberBase
{
    public Pair Pair { get; init; }

    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private UserPermissions _ownPermissions;

    public PermissionWindowUI(ILogger<PermissionWindowUI> logger, Pair pair, MareMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.WindowTitle"), pair.UserData.AliasOrUID) + "###UmbraSyncPermissions" + pair.UserData.UID, performanceCollectorService)
    {
        Pair = pair;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _ownPermissions = pair.UserPair?.OwnPermissions.DeepClone() ?? default;
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
        SizeConstraints = new()
        {
            MinimumSize = new(450, 100),
            MaximumSize = new(450, 500)
        };
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        var paused = _ownPermissions.IsPaused();
        var disableSounds = _ownPermissions.IsDisableSounds();
        var disableAnimations = _ownPermissions.IsDisableAnimations();
        var disableVfx = _ownPermissions.IsDisableVFX();
        var style = ImGui.GetStyle();
        var indentSize = ImGui.GetFrameHeight() + style.ItemSpacing.X;

        _uiSharedService.BigText(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.Header"), Pair.UserData.AliasOrUID));
        ImGuiHelpers.ScaledDummy(1f);

        if (Pair.UserPair == null)
            return;

        if (ImGui.Checkbox(Loc.Get("Permissions.Pause"), ref paused))
        {
            _ownPermissions.SetPaused(paused);
        }
        _uiSharedService.DrawHelpText(Loc.Get("Permissions.Pause.Help") + UiSharedService.TooltipSeparator
            + Loc.Get("Permissions.Pause.Note"));
        var otherPerms = Pair.UserPair.OtherPermissions;

        var otherIsPaused = otherPerms.IsPaused();
        var otherDisableSounds = otherPerms.IsDisableSounds();
        var otherDisableAnimations = otherPerms.IsDisableAnimations();
        var otherDisableVFX = otherPerms.IsDisableVFX();

        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherIsPaused, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.Other.Paused"), Pair.UserData.AliasOrUID, !otherIsPaused ? Loc.Get("Permissions.Other.NotPrefix") : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        if (ImGui.Checkbox(Loc.Get("Permissions.DisableSounds"), ref disableSounds))
        {
            _ownPermissions.SetDisableSounds(disableSounds);
        }
        _uiSharedService.DrawHelpText(Loc.Get("Permissions.DisableSounds.Help") + UiSharedService.TooltipSeparator
            + Loc.Get("Permissions.DisableSounds.Note"));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableSounds, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.Other.Sounds"), Pair.UserData.AliasOrUID, !otherDisableSounds ? Loc.Get("Permissions.Other.NotPrefix") : string.Empty));
        }

        if (ImGui.Checkbox(Loc.Get("Permissions.DisableAnimations"), ref disableAnimations))
        {
            _ownPermissions.SetDisableAnimations(disableAnimations);
        }
        _uiSharedService.DrawHelpText(Loc.Get("Permissions.DisableAnimations.Help") + UiSharedService.TooltipSeparator
            + Loc.Get("Permissions.DisableAnimations.Note"));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableAnimations, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.Other.Animations"), Pair.UserData.AliasOrUID, !otherDisableAnimations ? Loc.Get("Permissions.Other.NotPrefix") : string.Empty));
        }

        if (ImGui.Checkbox(Loc.Get("Permissions.DisableVfx"), ref disableVfx))
        {
            _ownPermissions.SetDisableVFX(disableVfx);
        }
        _uiSharedService.DrawHelpText(Loc.Get("Permissions.DisableVfx.Help") + UiSharedService.TooltipSeparator
            + Loc.Get("Permissions.DisableVfx.Note"));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableVFX, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Permissions.Other.Vfx"), Pair.UserData.AliasOrUID, !otherDisableVFX ? Loc.Get("Permissions.Other.NotPrefix") : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        {
            var mareConfig = _uiSharedService.ConfigService;
            var overrides = mareConfig.Current.PairTargetSoundOverrides;
            var uid = Pair.UserData.UID;
            overrides.TryGetValue(uid, out var currentValue);
            var hasOverride = overrides.ContainsKey(uid);

            var previewLabel = !hasOverride
                ? Loc.Get("Settings.ChatTargetSound.Override.Default")
                : currentValue == 0
                    ? Loc.Get("Settings.ChatTargetSound.Override.Disabled")
                    : string.Format(CultureInfo.CurrentCulture, Loc.Get("Settings.ChatTargetSound.SoundItem"), currentValue);

            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo(Loc.Get("Settings.ChatTargetSound.Override.Label") + "##perm_sound_" + uid, previewLabel))
            {
                if (ImGui.Selectable(Loc.Get("Settings.ChatTargetSound.Override.Default"), !hasOverride))
                {
                    overrides.Remove(uid);
                    mareConfig.Save();
                }
                if (ImGui.Selectable(Loc.Get("Settings.ChatTargetSound.Override.Disabled"), hasOverride && currentValue == 0))
                {
                    overrides[uid] = 0;
                    mareConfig.Save();
                }
                for (var i = 1; i <= 16; i++)
                {
                    var label = string.Format(CultureInfo.CurrentCulture, Loc.Get("Settings.ChatTargetSound.SoundItem"), i);
                    if (ImGui.Selectable(label, hasOverride && currentValue == i))
                    {
                        overrides[uid] = i;
                        mareConfig.Save();
                    }
                }
                ImGui.EndCombo();
            }
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        bool hasChanges = _ownPermissions != Pair.UserPair.OwnPermissions;

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Save, Loc.Get("Permissions.Save")))
            {
                Mediator.Publish(new PairSyncOverrideChanged(Pair.UserData.UID,
                    _ownPermissions.IsDisableSounds(),
                    _ownPermissions.IsDisableAnimations(),
                    _ownPermissions.IsDisableVFX()));
                _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
            }
        UiSharedService.AttachToolTip(Loc.Get("Permissions.SaveTooltip"));

        var rightSideButtons = _uiSharedService.GetIconTextButtonSize(Dalamud.Interface.FontAwesomeIcon.Undo, Loc.Get("Permissions.Revert")) +
            _uiSharedService.GetIconTextButtonSize(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, Loc.Get("Permissions.Reset"));
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        ImGui.SameLine(availableWidth - rightSideButtons);

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Undo, Loc.Get("Permissions.Revert")))
            {
                _ownPermissions = Pair.UserPair.OwnPermissions.DeepClone();
            }
        UiSharedService.AttachToolTip(Loc.Get("Permissions.RevertTooltip"));

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, Loc.Get("Permissions.Reset")))
        {
            var defaults = _uiSharedService.ConfigService.Current;
            _ownPermissions.SetPaused(false);
            _ownPermissions.SetDisableSounds(defaults.DefaultDisableSounds);
            _ownPermissions.SetDisableAnimations(defaults.DefaultDisableAnimations);
            _ownPermissions.SetDisableVFX(defaults.DefaultDisableVfx);
            Mediator.Publish(new PairSyncOverrideChanged(Pair.UserData.UID,
                _ownPermissions.IsDisableSounds(),
                _ownPermissions.IsDisableAnimations(),
                _ownPermissions.IsDisableVFX()));
            _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
        }
        UiSharedService.AttachToolTip(Loc.Get("Permissions.ResetTooltip"));

        var ySize = ImGui.GetCursorPosY() + style.FramePadding.Y * ImGuiHelpers.GlobalScale + style.FrameBorderSize;
        ImGui.SetWindowSize(new(400, ySize));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}