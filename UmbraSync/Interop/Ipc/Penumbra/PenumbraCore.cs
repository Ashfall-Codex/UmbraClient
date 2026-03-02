using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using PenumbraApi = global::Penumbra.Api.Helpers;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;

namespace UmbraSync.Interop.Ipc.Penumbra;

public sealed class PenumbraCore : DisposableMediatorSubscriberBase, IPenumbraComponent
{
    private readonly PenumbraApi.EventSubscriber _penumbraInit;
    private readonly PenumbraApi.EventSubscriber _penumbraDispose;
    private readonly PenumbraIpc.GetEnabledState _penumbraEnabled;
    private readonly PenumbraIpc.GetModDirectory _penumbraResolveModDir;
    private bool _pluginLoaded;
    private Version _pluginVersion;
    private bool _shownPenumbraUnavailable;
    private string? _penumbraModDirectory;
    public new ILogger Logger => base.Logger;
    public IDalamudPluginInterface PluginInterface { get; }
    public DalamudUtilService DalamudUtil { get; }
    public new MareMediator Mediator => base.Mediator;
    public RedrawManager RedrawManager { get; }
    public NotificationTracker NotificationTracker { get; }
    public bool APIAvailable { get; private set; }
    public string? ModDirectory
    {
        get => _penumbraModDirectory;
        private set
        {
            if (!string.Equals(_penumbraModDirectory, value, StringComparison.Ordinal))
            {
                _penumbraModDirectory = value;
                Mediator.Publish(new PenumbraDirectoryChangedMessage(_penumbraModDirectory));
            }
        }
    }

    public PenumbraCore(
        ILogger logger,
        IDalamudPluginInterface pluginInterface,
        DalamudUtilService dalamudUtil,
        MareMediator mediator,
        RedrawManager redrawManager,
        NotificationTracker notificationTracker) : base(logger, mediator)
    {
        PluginInterface = pluginInterface;
        DalamudUtil = dalamudUtil;
        RedrawManager = redrawManager;
        NotificationTracker = notificationTracker;
        _penumbraInit = PenumbraIpc.Initialized.Subscriber(pluginInterface, PenumbraInit);
        _penumbraDispose = PenumbraIpc.Disposed.Subscriber(pluginInterface, PenumbraDispose);
        _penumbraEnabled = new PenumbraIpc.GetEnabledState(pluginInterface);
        _penumbraResolveModDir = new PenumbraIpc.GetModDirectory(pluginInterface);
        var plugin = PluginWatcherService.GetInitialPluginState(pluginInterface, "Penumbra");
        _pluginLoaded = plugin?.IsLoaded ?? false;
        _pluginVersion = plugin?.Version ?? new(0, 0, 0, 0);
        mediator.SubscribeKeyed<PluginChangeMessage>(this, "Penumbra", HandlePluginChange);
        mediator.Subscribe<DalamudLoginMessage>(this, _ => HandleDalamudLogin());
        CheckModDirectory();
    }

    private void PenumbraInit()
    {
        Logger.LogDebug("Penumbra initialized");
        CheckAPI();
    }

    private void PenumbraDispose()
    {
        Logger.LogDebug("Penumbra disposed");
        APIAvailable = false;
    }

    private void HandlePluginChange(PluginChangeMessage msg)
    {
        _pluginLoaded = msg.IsLoaded;
        _pluginVersion = msg.Version;
        CheckAPI();
        if (msg.IsLoaded && !APIAvailable)
        {
            _shownPenumbraUnavailable = false;
            _ = Task.Run(CheckAPIWithRetryAsync);
        }
    }

    private void HandleDalamudLogin()
    {
        _shownPenumbraUnavailable = false;
        _ = Task.Run(CheckAPIWithRetryAsync);
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
                Logger.LogDebug("Penumbra API available after {attempt} attempt(s)", attempt);
                return;
            }

            Logger.LogDebug("Penumbra API not available, attempt {attempt}/{maxRetries}", attempt, maxRetries);
        }

        Logger.LogWarning("Penumbra API still not available after {maxRetries} attempts", maxRetries);
        if (!APIAvailable && !_shownPenumbraUnavailable)
        {
            _shownPenumbraUnavailable = true;
            Mediator.Publish(new NotificationMessage(
                Loc.Get("Notification.PluginIntegration.PenumbraInactive.Title"),
                Loc.Get("Notification.PluginIntegration.PenumbraInactive.Body"),
                NotificationType.Error));
            NotificationTracker.Upsert(NotificationEntry.PenumbraInactive());
        }
    }
    
    public void CheckAPI()
    {
        bool penumbraAvailable = false;
        try
        {
            penumbraAvailable = _pluginLoaded && _pluginVersion >= new Version(1, 5, 1, 0);
            try
            {
                penumbraAvailable &= _penumbraEnabled.Invoke();
            }
            catch
            {
                penumbraAvailable = false;
            }
            _shownPenumbraUnavailable = _shownPenumbraUnavailable && !penumbraAvailable;
            APIAvailable = penumbraAvailable;
        }
        catch
        {
            APIAvailable = penumbraAvailable;
        }
        finally
        {
            // Notification is deferred to CheckAPIWithRetryAsync after all retries are exhausted
        }
    }
    
    // Retourne le chemin brut du répertoire de mods Penumbra (sans transformation de casse).
    public string? GetModDirectoryRaw()
    {
        if (!APIAvailable) return null;
        try { return _penumbraResolveModDir.Invoke(); }
        catch { return null; }
    }

    public void CheckModDirectory()
    {
        if (!APIAvailable)
        {
            ModDirectory = string.Empty;
            return;
        }

        try
        {
            ModDirectory = _penumbraResolveModDir!.Invoke().ToLowerInvariant();
        }
        catch
        {
            ModDirectory = string.Empty;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _penumbraInit.Dispose();
            _penumbraDispose.Dispose();
        }
    }
}
