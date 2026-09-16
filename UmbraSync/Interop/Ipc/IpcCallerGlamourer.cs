using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Interop.Ipc;

public sealed class IpcCallerGlamourer : DisposableMediatorSubscriberBase, IIpcCaller
{
    // Clé commune aux plugins de synchronisation : ils s'appliquent par-dessus les uns des autres au lieu de se bloquer
    private const uint LockCode = 0x6D617265;
    private const uint LegacyLockCode = 0x626E7579;

    private readonly ILogger<IpcCallerGlamourer> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareMediator _mareMediator;
    private readonly RedrawManager _redrawManager;
    private readonly NotificationTracker _notificationTracker;

    private readonly ApiVersion _glamourerApiVersions;
    private readonly ApplyState? _glamourerApplyAll;
    private readonly ReapplyState? _glamourerReapply;
    private readonly GetStateBase64? _glamourerGetAllCustomization;
    private readonly RevertState _glamourerRevert;
    private readonly RevertStateName _glamourerRevertByName;
    private readonly UnlockState _glamourerUnlock;
    private readonly UnlockStateName _glamourerUnlockByName;
    private readonly GetDesignList _glamourerGetDesignList;
    private readonly ApplyDesign _glamourerApplyDesign;
    private readonly EventSubscriber<nint>? _glamourerStateChanged;
    private readonly EventSubscriber<nint, StateChangeType>? _glamourerStateChangedWithType;

    private bool _pluginLoaded;
    private Version _pluginVersion;

    private bool _shownGlamourerUnavailable = false;
    private int _ownRevertDepth;

    public IpcCallerGlamourer(ILogger<IpcCallerGlamourer> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, MareMediator mareMediator,
        RedrawManager redrawManager, NotificationTracker notificationTracker) : base(logger, mareMediator)
    {
        _notificationTracker = notificationTracker;
        _glamourerApiVersions = new ApiVersion(pi);
        _glamourerGetAllCustomization = new GetStateBase64(pi);
        _glamourerApplyAll = new ApplyState(pi);
        _glamourerReapply = new ReapplyState(pi);
        _glamourerRevert = new RevertState(pi);
        _glamourerRevertByName = new RevertStateName(pi);
        _glamourerUnlock = new UnlockState(pi);
        _glamourerUnlockByName = new UnlockStateName(pi);
        _glamourerGetDesignList = new GetDesignList(pi);
        _glamourerApplyDesign = new ApplyDesign(pi);

        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _mareMediator = mareMediator;
        _redrawManager = redrawManager;

        var plugin = PluginWatcherService.GetInitialPluginState(pi, "Glamourer");

        _pluginLoaded = plugin?.IsLoaded ?? false;
        _pluginVersion = plugin?.Version ?? new(0, 0, 0, 0);

        Mediator.SubscribeKeyed<PluginChangeMessage>(this, "Glamourer", (msg) =>
        {
            _pluginLoaded = msg.IsLoaded;
            _pluginVersion = msg.Version;
            CheckAPI();
            if (msg.IsLoaded && !APIAvailable)
            {
                _shownGlamourerUnavailable = false;
                _ = Task.Run(CheckAPIWithRetryAsync);
            }
        });

        _glamourerStateChanged = StateChanged.Subscriber(pi, GlamourerChanged);
        _glamourerStateChanged.Enable();
        _glamourerStateChangedWithType = StateChangedWithType.Subscriber(pi, GlamourerChangedWithType);
        _glamourerStateChangedWithType.Enable();

        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) =>
        {
            _shownGlamourerUnavailable = false;
            _ = Task.Run(CheckAPIWithRetryAsync);
        });
    }


    private const int IpcInitialDelayMs = 5000;
    private const int IpcCheckRetries = 10;
    private const int IpcDelayBetweenRetriesMs = 2500;

    // Libère un verrou encore posé avec l'ancienne clé Umbra (version précédente rechargée sans redémarrer le jeu)
    private void UnlockLegacyLock(int objectIndex)
        => _glamourerUnlock.Invoke(objectIndex, LegacyLockCode);

    private void UnlockLegacyLock(string playerName)
        => _glamourerUnlockByName.Invoke(playerName, LegacyLockCode);

    private GlamourerApiEc InvokeOwnRevert(Func<GlamourerApiEc> revert)
    {
        _ownRevertDepth++;
        try
        {
            return revert();
        }
        finally
        {
            _ownRevertDepth--;
        }
    }

    private async Task CheckAPIWithRetryAsync()
    {
        await Task.Delay(IpcInitialDelayMs).ConfigureAwait(false);

        for (int attempt = 1; attempt <= IpcCheckRetries; attempt++)
        {
            CheckAPI();

            if (APIAvailable)
            {
                _logger.LogDebug("Glamourer API available after {attempt} attempt(s)", attempt);
                return;
            }

            _logger.LogDebug("Glamourer API not available, attempt {attempt}/{maxRetries}", attempt, IpcCheckRetries);
            await Task.Delay(IpcDelayBetweenRetriesMs).ConfigureAwait(false);
        }

        _logger.LogWarning("Glamourer API still not available after {maxRetries} attempts", IpcCheckRetries);
        if (!APIAvailable && !_shownGlamourerUnavailable)
        {
            _shownGlamourerUnavailable = true;
            _mareMediator.Publish(new NotificationMessage(
                Loc.Get("Notification.PluginIntegration.GlamourerInactive.Title"),
                Loc.Get("Notification.PluginIntegration.GlamourerInactive.Body"),
                NotificationType.Error));
            _notificationTracker.Upsert(NotificationEntry.GlamourerInactive());
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();
        _glamourerStateChanged?.Dispose();
        _glamourerStateChangedWithType?.Dispose();
    }

    public bool APIAvailable { get; private set; }

    public void CheckAPI()
    {
        bool apiAvailable = false;
        try
        {
            bool versionValid = _pluginLoaded && _pluginVersion >= new Version(1, 3, 0, 10);
            try
            {
                var version = _glamourerApiVersions.Invoke();
                if (version is { Major: 1, Minor: >= 1 } && versionValid)
                {
                    apiAvailable = true;
                }
            }
            catch
            {
                // ignore
            }
            _shownGlamourerUnavailable = _shownGlamourerUnavailable && !apiAvailable;

            APIAvailable = apiAvailable;

            // Alerte posée pendant que Glamourer démarrait : elle n'a plus lieu d'être une fois
            // qu'il répond. Sans ce retrait elle restait dans le centre de notifications pour de bon.
            if (apiAvailable)
            {
                var stale = NotificationEntry.GlamourerInactive();
                _notificationTracker.Remove(stale.Category, stale.Id);
            }
        }
        catch
        {
            APIAvailable = apiAvailable;
        }
        // Notification is deferred to CheckAPIWithRetryAsync after all retries are exhausted
    }

    public async Task<GlamourerApiEc?> ApplyAllAsync(ILogger logger, GameObjectHandler handler, string? customization, Guid applicationId, CancellationToken token, bool allowImmediate = false)
    {
        if (!APIAvailable || string.IsNullOrEmpty(customization) || _dalamudUtil.IsZoning) return null;

        GlamourerApiEc? result = null;
        var semaphoreAcquired = false;
        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            semaphoreAcquired = true;

            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Calling on IPC: GlamourerApplyAll", applicationId);
                    UnlockLegacyLock(chara.ObjectIndex);
                    result = _glamourerApplyAll!.Invoke(customization, chara.ObjectIndex, LockCode);
                    if (result != GlamourerApiEc.Success)
                        logger.LogWarning("[{appid}] Glamourer a refusé l'application : {result}", applicationId, result);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Failed to apply Glamourer data", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                _redrawManager.RedrawSemaphore.Release();
            }
        }

        return result;
    }

    /// <summary>
    /// Réapplication Glamourer « soft » : recharge l'état glamourer du perso SANS redraw Penumbra
    /// complet ni acquisition du slot redraw. Utilisé pour les changements texture/material seuls
    /// (cf. PairRedrawDecision.SoftReapply/DeferredSoftReapply) — bien plus léger et sans flicker.
    /// </summary>
    public async Task ReapplyDirectAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _glamourerReapply == null || _dalamudUtil.IsZoning) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            if (handler.Address == nint.Zero) return;
            var gameObj = _dalamudUtil.CreateGameObject(handler.Address);
            if (gameObj is not ICharacter c) return;
            try
            {
                logger.LogDebug("[{appid}] Calling On IPC: GlamourerReapplyState (soft)", applicationId);
                UnlockLegacyLock(c.ObjectIndex);
                _glamourerReapply.Invoke(c.ObjectIndex, LockCode, ApplyFlag.Once);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{appid}] Failed to soft-reapply Glamourer data", applicationId);
            }
        }).ConfigureAwait(false);
    }

    public async Task<string> GetCharacterCustomizationAsync(IntPtr character)
    {
        if (!APIAvailable) return string.Empty;
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = _dalamudUtil.CreateGameObject(character);
                if (gameObj is ICharacter c)
                {
                    return _glamourerGetAllCustomization!.Invoke(c.ObjectIndex).Item2 ?? string.Empty;
                }
                return string.Empty;
            }).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task RevertAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;

        var semaphoreAcquired = false;
        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            semaphoreAcquired = true;

            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlock", applicationId);
                    UnlockLegacyLock(chara.ObjectIndex);
                    _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                    logger.LogDebug("[{appid}] Calling On IPC: GlamourerRevert", applicationId);
                    var revertResult = InvokeOwnRevert(() => _glamourerRevert.Invoke(chara.ObjectIndex, LockCode));
                    if (revertResult == GlamourerApiEc.InvalidKey)
                        logger.LogDebug("[{appid}] Revert Glamourer ignoré : état verrouillé par un autre plugin", applicationId);
                    else if (revertResult is not (GlamourerApiEc.Success or GlamourerApiEc.NothingDone))
                        logger.LogWarning("[{appid}] Glamourer a refusé le revert : {result}", applicationId, revertResult);
                    logger.LogDebug("[{appid}] Calling On IPC: PenumbraRedraw", applicationId);
                    _mareMediator.Publish(new PenumbraRedrawCharacterMessage(chara));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Error during GlamourerRevert", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                _redrawManager.RedrawSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Libère le verrou Umbra sans revert : l'apparence reste en place et un autre plugin peut
    /// appliquer la sienne par-dessus.
    /// </summary>
    public async Task UnlockAsync(ILogger logger, nint address, Guid applicationId)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning || address == nint.Zero) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            if (_dalamudUtil.CreateGameObject(address) is not ICharacter chara) return;
            try
            {
                UnlockLegacyLock(chara.ObjectIndex);
                var result = _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlock, result: {result}", applicationId, result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{appid}] Error during GlamourerUnlock", applicationId);
            }
        }).ConfigureAwait(false);
    }

    public void RevertNow(ILogger logger, Guid applicationId, int objectIndex)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;
        logger.LogTrace("[{applicationId}] Immediately reverting object index {objId}", applicationId, objectIndex);
        UnlockLegacyLock(objectIndex);
        InvokeOwnRevert(() => _glamourerRevert.Invoke(objectIndex, LockCode));
    }

    public void RevertByNameNow(ILogger logger, Guid applicationId, string name)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;
        logger.LogTrace("[{applicationId}] Immediately reverting {name}", applicationId, name);
        UnlockLegacyLock(name);
        InvokeOwnRevert(() => _glamourerRevertByName.Invoke(name, LockCode));
    }

    public async Task RevertByNameAsync(ILogger logger, string name, Guid applicationId)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            RevertByName(logger, name, applicationId);

        }).ConfigureAwait(false);
    }

    public void RevertByName(ILogger logger, string name, Guid applicationId)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;

        try
        {
            logger.LogDebug("[{appid}] Calling On IPC: GlamourerRevertByName", applicationId);
            UnlockLegacyLock(name);
            InvokeOwnRevert(() => _glamourerRevertByName.Invoke(name, LockCode));
            logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlockName", applicationId);
            _glamourerUnlockByName.Invoke(name, LockCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Glamourer RevertByName");
        }
    }

    public async Task<List<(Guid Id, string Name)>> GetDesignsAsync()
    {
        if (!APIAvailable) return new List<(Guid, string)>();
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                var designs = _glamourerGetDesignList.Invoke();
                return designs
                    .Select(kv => (kv.Key, kv.Value))
                    .OrderBy(d => d.Value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Récupération de la liste des designs Glamourer échouée");
            return new List<(Guid, string)>();
        }
    }

    public async Task ApplyDesignToSelfAsync(Guid designId, int objectIndex)
    {
        if (!APIAvailable) return;
        try
        {
            await _dalamudUtil.RunOnFrameworkThread(() => _glamourerApplyDesign.Invoke(designId, objectIndex, 0)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Application du design Glamourer sur soi échouée");
        }
    }
    
    public async Task ApplyStateToSelfAsync(string base64, int objectIndex)
    {
        if (!APIAvailable || string.IsNullOrEmpty(base64) || _glamourerApplyAll == null) return;
        try
        {
            await _dalamudUtil.RunOnFrameworkThread(() => _glamourerApplyAll.Invoke(base64, objectIndex, 0)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restauration de l'état Glamourer du joueur échouée");
        }
    }

    private void GlamourerChanged(nint address)
    {
        _mareMediator.Publish(new GlamourerChangedMessage(address));
    }

    private void GlamourerChangedWithType(nint address, StateChangeType changeType)
    {
        if (changeType == StateChangeType.Reset && _ownRevertDepth == 0)
            _mareMediator.Publish(new GlamourerResetMessage(address));
    }
}
