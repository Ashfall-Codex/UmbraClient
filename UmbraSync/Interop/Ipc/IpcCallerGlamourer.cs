using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
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
    private readonly ILogger<IpcCallerGlamourer> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareMediator _mareMediator;
    private readonly RedrawManager _redrawManager;
    private readonly NotificationTracker _notificationTracker;

    private readonly ApiVersion _glamourerApiVersions;
    private readonly ApplyState? _glamourerApplyAll;
    private readonly GetStateBase64? _glamourerGetAllCustomization;
    private readonly RevertState _glamourerRevert;
    private readonly RevertStateName _glamourerRevertByName;
    private readonly UnlockState _glamourerUnlock;
    private readonly UnlockStateName _glamourerUnlockByName;
    private readonly EventSubscriber<nint>? _glamourerStateChanged;

    private bool _pluginLoaded;
    private Version _pluginVersion;

    private bool _shownGlamourerUnavailable = false;
    private readonly uint LockCode = 0x626E7579;

    public IpcCallerGlamourer(ILogger<IpcCallerGlamourer> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, MareMediator mareMediator,
        RedrawManager redrawManager, NotificationTracker notificationTracker) : base(logger, mareMediator)
    {
        _notificationTracker = notificationTracker;
        _glamourerApiVersions = new ApiVersion(pi);
        _glamourerGetAllCustomization = new GetStateBase64(pi);
        _glamourerApplyAll = new ApplyState(pi);
        _glamourerRevert = new RevertState(pi);
        _glamourerRevertByName = new RevertStateName(pi);
        _glamourerUnlock = new UnlockState(pi);
        _glamourerUnlockByName = new UnlockStateName(pi);

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

        Mediator.Subscribe<DalamudLoginMessage>(this, (msg) =>
        {
            _shownGlamourerUnavailable = false;
            _ = Task.Run(CheckAPIWithRetryAsync);
        });
    }

    private async Task CheckAPIWithRetryAsync()
    {
        const int maxRetries = 5;
        const int delayBetweenRetries = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            await Task.Delay(delayBetweenRetries).ConfigureAwait(false);
            CheckAPI();

            if (APIAvailable)
            {
                _logger.LogDebug("Glamourer API available after {attempt} attempt(s)", attempt);
                return;
            }

            _logger.LogDebug("Glamourer API not available, attempt {attempt}/{maxRetries}", attempt, maxRetries);
        }

        _logger.LogWarning("Glamourer API still not available after {maxRetries} attempts", maxRetries);
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
        }
        catch
        {
            APIAvailable = apiAvailable;
        }
        // Notification is deferred to CheckAPIWithRetryAsync after all retries are exhausted
    }

    public async Task ApplyAllAsync(ILogger logger, GameObjectHandler handler, string? customization, Guid applicationId, CancellationToken token, bool allowImmediate = false)
    {
        if (!APIAvailable || string.IsNullOrEmpty(customization) || _dalamudUtil.IsZoning) return;

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
                    _glamourerApplyAll!.Invoke(customization, chara.ObjectIndex, LockCode);
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
                    _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                    logger.LogDebug("[{appid}] Calling On IPC: GlamourerRevert", applicationId);
                    _glamourerRevert.Invoke(chara.ObjectIndex, LockCode);
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

    public void RevertNow(ILogger logger, Guid applicationId, int objectIndex)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;
        logger.LogTrace("[{applicationId}] Immediately reverting object index {objId}", applicationId, objectIndex);
        _glamourerRevert.Invoke(objectIndex, LockCode);
    }

    public void RevertByNameNow(ILogger logger, Guid applicationId, string name)
    {
        if ((!APIAvailable) || _dalamudUtil.IsZoning) return;
        logger.LogTrace("[{applicationId}] Immediately reverting {name}", applicationId, name);
        _glamourerRevertByName.Invoke(name, LockCode);
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
            _glamourerRevertByName.Invoke(name, LockCode);
            logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlockName", applicationId);
            _glamourerUnlockByName.Invoke(name, LockCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Glamourer RevertByName");
        }
    }

    private void GlamourerChanged(nint address)
    {
        _mareMediator.Publish(new GlamourerChangedMessage(address));
    }
}