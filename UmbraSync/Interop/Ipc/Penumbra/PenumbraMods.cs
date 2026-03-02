using Microsoft.Extensions.Logging;
using PenumbraEnum = global::Penumbra.Api.Enums;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;

namespace UmbraSync.Interop.Ipc.Penumbra;

// Module IPC pour la gestion des mods Penumbra installés (ajout, suppression, rechargement, activation).
public sealed class PenumbraMods
{
    private readonly PenumbraCore _core;
    private readonly PenumbraIpc.AddMod _penumbraAddMod;
    private readonly PenumbraIpc.DeleteMod _penumbraDeleteMod;
    private readonly PenumbraIpc.ReloadMod _penumbraReloadMod;
    private readonly PenumbraIpc.TrySetMod _penumbraTrySetMod;
    private readonly PenumbraIpc.TrySetModPriority _penumbraTrySetModPriority;

    public PenumbraMods(PenumbraCore core)
    {
        _core = core;
        _penumbraAddMod = new PenumbraIpc.AddMod(_core.PluginInterface);
        _penumbraDeleteMod = new PenumbraIpc.DeleteMod(_core.PluginInterface);
        _penumbraReloadMod = new PenumbraIpc.ReloadMod(_core.PluginInterface);
        _penumbraTrySetMod = new PenumbraIpc.TrySetMod(_core.PluginInterface);
        _penumbraTrySetModPriority = new PenumbraIpc.TrySetModPriority(_core.PluginInterface);
    }

    // Enregistre un mod depuis un répertoire existant dans le dossier de mods Penumbra.
    public async Task<PenumbraEnum.PenumbraApiEc> AddModAsync(ILogger logger, string modDirName)
    {
        if (!_core.APIAvailable) return PenumbraEnum.PenumbraApiEc.SystemDisposed;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var ret = _penumbraAddMod.Invoke(modDirName);
            logger.LogDebug("AddMod({ModDir}) = {Result}", modDirName, ret);
            return ret;
        }).ConfigureAwait(false);
    }

    // Supprime un mod enregistré dans Penumbra.
    public async Task<PenumbraEnum.PenumbraApiEc> DeleteModAsync(ILogger logger, string modDirName)
    {
        if (!_core.APIAvailable) return PenumbraEnum.PenumbraApiEc.SystemDisposed;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var ret = _penumbraDeleteMod.Invoke(modDirName);
            logger.LogDebug("DeleteMod({ModDir}) = {Result}", modDirName, ret);
            return ret;
        }).ConfigureAwait(false);
    }
    
    // Recharge un mod existant (relecture depuis le disque).
    public async Task<PenumbraEnum.PenumbraApiEc> ReloadModAsync(ILogger logger, string modDirName)
    {
        if (!_core.APIAvailable) return PenumbraEnum.PenumbraApiEc.SystemDisposed;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var ret = _penumbraReloadMod.Invoke(modDirName);
            logger.LogDebug("ReloadMod({ModDir}) = {Result}", modDirName, ret);
            return ret;
        }).ConfigureAwait(false);
    }

    // Active ou désactive un mod dans une collection.
    public async Task<PenumbraEnum.PenumbraApiEc> TrySetModEnabledAsync(ILogger logger, Guid collId, string modDirName, bool enabled)
    {
        if (!_core.APIAvailable) return PenumbraEnum.PenumbraApiEc.SystemDisposed;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var ret = _penumbraTrySetMod.Invoke(collId, modDirName, enabled);
            logger.LogDebug("TrySetMod({CollId}, {ModDir}, enabled={Enabled}) = {Result}", collId, modDirName, enabled, ret);
            return ret;
        }).ConfigureAwait(false);
    }

    // Définit la priorité d'un mod dans une collection.
    public async Task<PenumbraEnum.PenumbraApiEc> TrySetModPriorityAsync(ILogger logger, Guid collId, string modDirName, int priority)
    {
        if (!_core.APIAvailable) return PenumbraEnum.PenumbraApiEc.SystemDisposed;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var ret = _penumbraTrySetModPriority.Invoke(collId, modDirName, priority);
            logger.LogDebug("TrySetModPriority({CollId}, {ModDir}, priority={Priority}) = {Result}", collId, modDirName, priority, ret);
            return ret;
        }).ConfigureAwait(false);
    }
}
