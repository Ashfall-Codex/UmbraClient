using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.User;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.Services;

public sealed class SyncDefaultsService : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly MareConfigService _configService;
    private readonly PairManager _pairManager;

    public SyncDefaultsService(ILogger<SyncDefaultsService> logger, MareMediator mediator,
        MareConfigService configService, ApiController apiController, PairManager pairManager) : base(logger, mediator)
    {
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;

        Mediator.Subscribe<ApplyDefaultPairPermissionsMessage>(this, OnApplyPairDefaults);
        Mediator.Subscribe<ApplyDefaultGroupPermissionsMessage>(this, OnApplyGroupDefaults);
        Mediator.Subscribe<ApplyDefaultsToAllSyncsMessage>(this, msg => ApplyDefaultsToAll(msg));
        Mediator.Subscribe<PairSyncOverrideChanged>(this, OnPairOverrideChanged);
        Mediator.Subscribe<GroupSyncOverrideChanged>(this, OnGroupOverrideChanged);
    }

    private void OnApplyPairDefaults(ApplyDefaultPairPermissionsMessage message)
    {
        var config = _configService.Current;
        var permissions = message.Pair.OwnPermissions;
        var overrides = TryGetPairOverride(message.Pair.User.UID);
        if (!ApplyDefaults(ref permissions, config, overrides))
            return;

        _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(message.Pair.User, permissions));
    }

    private void OnApplyGroupDefaults(ApplyDefaultGroupPermissionsMessage message)
    {
        if (!string.Equals(message.GroupPair.User.UID, _apiController.UID, StringComparison.Ordinal))
            return;

        var config = _configService.Current;
        var permissions = message.GroupPair.GroupUserPermissions;
        var overrides = TryGetGroupOverride(message.GroupPair.Group.GID);
        if (!ApplyDefaults(ref permissions, config, overrides))
            return;

        _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(message.GroupPair.Group, message.GroupPair.User, permissions));
    }

    private async Task ApplyDefaultsToAllAsync(ApplyDefaultsToAllSyncsMessage message)
    {
        try
        {
            var config = _configService.Current;
            var tasks = new List<Task>();
            int updatedPairs = 0;
            int updatedGroups = 0;

            foreach (var pair in _pairManager.DirectPairs.Where(p => p.UserPair != null).ToList())
            {
                var permissions = pair.UserPair!.OwnPermissions;
                var overrides = TryGetPairOverride(pair.UserData.UID);
                if (!ApplyDefaults(ref permissions, config, overrides))
                    continue;

                updatedPairs++;
                tasks.Add(_apiController.UserSetPairPermissions(new UserPermissionsDto(pair.UserData, permissions)));
            }

            var selfUser = new UserData(_apiController.UID);
            foreach (var groupInfo in _pairManager.Groups.Values.ToList())
            {
                var permissions = groupInfo.GroupUserPermissions;
                var overrides = TryGetGroupOverride(groupInfo.Group.GID);
                if (!ApplyDefaults(ref permissions, config, overrides))
                    continue;

                updatedGroups++;
                tasks.Add(_apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupInfo.Group, selfUser, permissions)));
            }

            if (tasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed applying default sync settings to all pairs/groups");
                }
            }

            var summary = BuildSummaryMessage(updatedPairs, updatedGroups);
            var primary = BuildPrimaryMessage(message);
            var combined = string.IsNullOrEmpty(primary) ? summary : string.Concat(primary, ' ', summary);
            Mediator.Publish(new DualNotificationMessage("Préférences appliquées", combined, NotificationType.Success));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error while applying default sync settings to all pairs/groups");
            Mediator.Publish(new DualNotificationMessage("Préférences appliquées", "Une erreur est survenue lors de l'application des paramètres par défaut.", NotificationType.Error));
        }
    }

    private void ApplyDefaultsToAll(ApplyDefaultsToAllSyncsMessage message) => _ = ApplyDefaultsToAllAsync(message);

    private static string? BuildPrimaryMessage(ApplyDefaultsToAllSyncsMessage message)
    {
        if (string.IsNullOrEmpty(message.Context) || message.Disabled == null)
            return null;

        var state = message.Disabled.Value ? "désactivée" : "activée";
        return $"Synchronisation {message.Context} par défaut {state}.";
    }

    private static string BuildSummaryMessage(int pairs, int groups)
    {
        if (pairs == 0 && groups == 0)
            return "Aucun pair ou syncshell n'avait besoin d'être modifié.";

        if (pairs > 0 && groups > 0)
            return $"Mise à jour de {pairs} pair(s) et {groups} syncshell(s).";

        if (pairs > 0)
            return $"Mise à jour de {pairs} pair(s).";

        return $"Mise à jour de {groups} syncshell(s).";
    }

    private void OnPairOverrideChanged(PairSyncOverrideChanged message)
    {
        var overrides = _configService.Current.PairSyncOverrides;
        var entry = overrides.TryGetValue(message.Uid, out var existing) ? existing : new SyncOverrideEntry();
        bool changed = false;

        if (message.DisableSounds.HasValue)
        {
            var val = message.DisableSounds.Value;
            var defaultVal = _configService.Current.DefaultDisableSounds;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableSounds != newValue)
            {
                entry.DisableSounds = newValue;
                changed = true;
            }
        }

        if (message.DisableAnimations.HasValue)
        {
            var val = message.DisableAnimations.Value;
            var defaultVal = _configService.Current.DefaultDisableAnimations;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableAnimations != newValue)
            {
                entry.DisableAnimations = newValue;
                changed = true;
            }
        }

        if (message.DisableVfx.HasValue)
        {
            var val = message.DisableVfx.Value;
            var defaultVal = _configService.Current.DefaultDisableVfx;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableVfx != newValue)
            {
                entry.DisableVfx = newValue;
                changed = true;
            }
        }

        if (message.DisableHousingMods.HasValue)
        {
            var val = message.DisableHousingMods.Value;
            var defaultVal = _configService.Current.DefaultDisableHousingMods;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableHousingMods != newValue)
            {
                entry.DisableHousingMods = newValue;
                changed = true;
            }
        }

        if (!changed) return;

        if (entry.IsEmpty)
            overrides.Remove(message.Uid);
        else
            overrides[message.Uid] = entry;

        _configService.Save();
    }

    private void OnGroupOverrideChanged(GroupSyncOverrideChanged message)
    {
        var overrides = _configService.Current.GroupSyncOverrides;
        var entry = overrides.TryGetValue(message.Gid, out var existing) ? existing : new SyncOverrideEntry();
        bool changed = false;

        if (message.DisableSounds.HasValue)
        {
            var val = message.DisableSounds.Value;
            var defaultVal = _configService.Current.DefaultDisableSounds;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableSounds != newValue)
            {
                entry.DisableSounds = newValue;
                changed = true;
            }
        }

        if (message.DisableAnimations.HasValue)
        {
            var val = message.DisableAnimations.Value;
            var defaultVal = _configService.Current.DefaultDisableAnimations;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableAnimations != newValue)
            {
                entry.DisableAnimations = newValue;
                changed = true;
            }
        }

        if (message.DisableVfx.HasValue)
        {
            var val = message.DisableVfx.Value;
            var defaultVal = _configService.Current.DefaultDisableVfx;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableVfx != newValue)
            {
                entry.DisableVfx = newValue;
                changed = true;
            }
        }

        if (message.DisableHousingMods.HasValue)
        {
            var val = message.DisableHousingMods.Value;
            var defaultVal = _configService.Current.DefaultDisableHousingMods;
            var newValue = val == defaultVal ? (bool?)null : val;
            if (entry.DisableHousingMods != newValue)
            {
                entry.DisableHousingMods = newValue;
                changed = true;
            }
        }

        if (!changed) return;

        if (entry.IsEmpty)
            overrides.Remove(message.Gid);
        else
            overrides[message.Gid] = entry;

        _configService.Save();
    }

    private SyncOverrideEntry? TryGetPairOverride(string uid)
    {
        var overrides = _configService.Current.PairSyncOverrides;
        return overrides.TryGetValue(uid, out var entry) ? entry : null;
    }

    private SyncOverrideEntry? TryGetGroupOverride(string gid)
    {
        var overrides = _configService.Current.GroupSyncOverrides;
        return overrides.TryGetValue(gid, out var entry) ? entry : null;
    }

    private static bool ApplyDefaults(ref UserPermissions permissions, MareConfig config, SyncOverrideEntry? overrides)
    {
        bool changed = false;
        if (overrides?.DisableSounds is bool overrideSounds)
        {
            if (permissions.IsDisableSounds() != overrideSounds)
            {
                permissions.SetDisableSounds(overrideSounds);
                changed = true;
            }
        }
        else if (permissions.IsDisableSounds() != config.DefaultDisableSounds)
        {
            permissions.SetDisableSounds(config.DefaultDisableSounds);
            changed = true;
        }

        if (overrides?.DisableAnimations is bool overrideAnims)
        {
            if (permissions.IsDisableAnimations() != overrideAnims)
            {
                permissions.SetDisableAnimations(overrideAnims);
                changed = true;
            }
        }
        else if (permissions.IsDisableAnimations() != config.DefaultDisableAnimations)
        {
            permissions.SetDisableAnimations(config.DefaultDisableAnimations);
            changed = true;
        }

        if (overrides?.DisableVfx is bool overrideVfx)
        {
            if (permissions.IsDisableVFX() != overrideVfx)
            {
                permissions.SetDisableVFX(overrideVfx);
                changed = true;
            }
        }
        else if (permissions.IsDisableVFX() != config.DefaultDisableVfx)
        {
            permissions.SetDisableVFX(config.DefaultDisableVfx);
            changed = true;
        }

        if (overrides?.DisableHousingMods is bool overrideHousing)
        {
            if (permissions.IsDisableHousing() != overrideHousing)
            {
                permissions.SetDisableHousing(overrideHousing);
                changed = true;
            }
        }
        else if (permissions.IsDisableHousing() != config.DefaultDisableHousingMods)
        {
            permissions.SetDisableHousing(config.DefaultDisableHousingMods);
            changed = true;
        }

        return changed;
    }

    private static bool ApplyDefaults(ref GroupUserPermissions permissions, MareConfig config, SyncOverrideEntry? overrides)
    {
        bool changed = false;
        if (overrides?.DisableSounds is bool overrideSounds)
        {
            if (permissions.IsDisableSounds() != overrideSounds)
            {
                permissions.SetDisableSounds(overrideSounds);
                changed = true;
            }
        }
        else if (permissions.IsDisableSounds() != config.DefaultDisableSounds)
        {
            permissions.SetDisableSounds(config.DefaultDisableSounds);
            changed = true;
        }

        if (overrides?.DisableAnimations is bool overrideAnims)
        {
            if (permissions.IsDisableAnimations() != overrideAnims)
            {
                permissions.SetDisableAnimations(overrideAnims);
                changed = true;
            }
        }
        else if (permissions.IsDisableAnimations() != config.DefaultDisableAnimations)
        {
            permissions.SetDisableAnimations(config.DefaultDisableAnimations);
            changed = true;
        }

        if (overrides?.DisableVfx is bool overrideVfx)
        {
            if (permissions.IsDisableVFX() != overrideVfx)
            {
                permissions.SetDisableVFX(overrideVfx);
                changed = true;
            }
        }
        else if (permissions.IsDisableVFX() != config.DefaultDisableVfx)
        {
            permissions.SetDisableVFX(config.DefaultDisableVfx);
            changed = true;
        }

        if (overrides?.DisableHousingMods is bool overrideHousing)
        {
            if (permissions.IsDisableHousing() != overrideHousing)
            {
                permissions.SetDisableHousing(overrideHousing);
                changed = true;
            }
        }
        else if (permissions.IsDisableHousing() != config.DefaultDisableHousingMods)
        {
            permissions.SetDisableHousing(config.DefaultDisableHousingMods);
            changed = true;
        }

        return changed;
    }
}