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
        var config = _configService.Current;
        var overrides = config.PairSyncOverrides;
        var entry = overrides.TryGetValue(message.Uid, out var existing) ? existing : new SyncOverrideEntry();

        bool changed = ApplyRequestedOverrides(entry, config, message);

        bool scenariosChanged = ResolveOverride(message.DisableHousingScenarios, config.DefaultDisableHousingScenarios,
            entry.DisableHousingScenarios, out var scenarios);
        if (scenariosChanged)
        {
            entry.DisableHousingScenarios = scenarios;
            changed = true;
        }

        if (!changed) return;

        StoreOverride(overrides, message.Uid, entry);

        if (scenariosChanged)
            Mediator.Publish(new HousingScenarioSyncPreferenceChangedMessage());
    }

    private void OnGroupOverrideChanged(GroupSyncOverrideChanged message)
    {
        var config = _configService.Current;
        var overrides = config.GroupSyncOverrides;
        var entry = overrides.TryGetValue(message.Gid, out var existing) ? existing : new SyncOverrideEntry();

        if (!ApplyRequestedOverrides(entry, config, message)) return;

        StoreOverride(overrides, message.Gid, entry);
    }
    
    private static bool ApplyRequestedOverrides(SyncOverrideEntry entry, MareConfig config, ISyncOverrideChange requested)
    {
        bool changed = false;

        if (ResolveOverride(requested.DisableSounds, config.DefaultDisableSounds, entry.DisableSounds, out var sounds))
        {
            entry.DisableSounds = sounds;
            changed = true;
        }

        if (ResolveOverride(requested.DisableAnimations, config.DefaultDisableAnimations, entry.DisableAnimations, out var animations))
        {
            entry.DisableAnimations = animations;
            changed = true;
        }

        if (ResolveOverride(requested.DisableVfx, config.DefaultDisableVfx, entry.DisableVfx, out var vfx))
        {
            entry.DisableVfx = vfx;
            changed = true;
        }

        if (ResolveOverride(requested.DisableHousingMods, config.DefaultDisableHousingMods, entry.DisableHousingMods, out var housingMods))
        {
            entry.DisableHousingMods = housingMods;
            changed = true;
        }

        return changed;
    }
    
    private static bool ResolveOverride(bool? requested, bool defaultValue, bool? current, out bool? resolved)
    {
        resolved = current;
        if (!requested.HasValue) return false;

        var value = requested.Value == defaultValue ? (bool?)null : requested.Value;
        if (current == value) return false;

        resolved = value;
        return true;
    }

    private void StoreOverride(Dictionary<string, SyncOverrideEntry> overrides, string key, SyncOverrideEntry entry)
    {
        if (entry.IsEmpty)
            overrides.Remove(key);
        else
            overrides[key] = entry;

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

    private static (bool Sounds, bool Animations, bool Vfx, bool Housing) TargetPermissions(MareConfig config, SyncOverrideEntry? overrides)
        => (overrides?.DisableSounds ?? config.DefaultDisableSounds,
            overrides?.DisableAnimations ?? config.DefaultDisableAnimations,
            overrides?.DisableVfx ?? config.DefaultDisableVfx,
            overrides?.DisableHousingMods ?? config.DefaultDisableHousingMods);

    private static bool ApplyDefaults(ref UserPermissions permissions, MareConfig config, SyncOverrideEntry? overrides)
    {
        var target = TargetPermissions(config, overrides);
        bool changed = false;

        if (permissions.IsDisableSounds() != target.Sounds) { permissions.SetDisableSounds(target.Sounds); changed = true; }
        if (permissions.IsDisableAnimations() != target.Animations) { permissions.SetDisableAnimations(target.Animations); changed = true; }
        if (permissions.IsDisableVFX() != target.Vfx) { permissions.SetDisableVFX(target.Vfx); changed = true; }
        if (permissions.IsDisableHousing() != target.Housing) { permissions.SetDisableHousing(target.Housing); changed = true; }

        return changed;
    }

    private static bool ApplyDefaults(ref GroupUserPermissions permissions, MareConfig config, SyncOverrideEntry? overrides)
    {
        var target = TargetPermissions(config, overrides);
        bool changed = false;

        if (permissions.IsDisableSounds() != target.Sounds) { permissions.SetDisableSounds(target.Sounds); changed = true; }
        if (permissions.IsDisableAnimations() != target.Animations) { permissions.SetDisableAnimations(target.Animations); changed = true; }
        if (permissions.IsDisableVFX() != target.Vfx) { permissions.SetDisableVFX(target.Vfx); changed = true; }
        if (permissions.IsDisableHousing() != target.Housing) { permissions.SetDisableHousing(target.Housing); changed = true; }

        return changed;
    }
}