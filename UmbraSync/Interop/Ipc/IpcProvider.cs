using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Interop.Ipc;

public class IpcProvider : IHostedService, IMediatorSubscriber
{
    private const string UmbraApiLabelPrefix = "Umbra";

    private readonly ILogger<IpcProvider> _logger;
    private readonly IDalamudPluginInterface _pi;
    private readonly MareConfigService _mareConfig;
    private readonly CharaDataManager _charaDataManager;
    private ICallGateProvider<string, IGameObject, bool>? _loadFileProvider;
    private ICallGateProvider<string, IGameObject, Task<bool>>? _loadFileAsyncProvider;
    private ICallGateProvider<List<nint>>? _handledGameAddresses;
    private readonly List<GameObjectHandler> _activeGameObjectHandlers = [];

    private ICallGateProvider<string, IGameObject, bool>? _loadFileProviderMare;
    private ICallGateProvider<string, IGameObject, Task<bool>>? _loadFileAsyncProviderMare;
    private ICallGateProvider<List<nint>>? _handledGameAddressesMare;

    private bool _impersonating = false;
    private DateTime _unregisterTime = DateTime.UtcNow;
    private CancellationTokenSource? _registerDelayCts = new();

    public bool MarePluginEnabled => IsExternalUmbraLoaded();
    public bool ImpersonationActive => _impersonating;

    public MareMediator Mediator { get; init; }

    public IpcProvider(ILogger<IpcProvider> logger, IDalamudPluginInterface pi, MareConfigService mareConfig,
        CharaDataManager charaDataManager, MareMediator mareMediator)
    {
        _logger = logger;
        _pi = pi;
        _mareConfig = mareConfig;
        _charaDataManager = charaDataManager;
        Mediator = mareMediator;

        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, (msg) =>
        {
            if (msg.OwnedObject) return;
            _activeGameObjectHandlers.Add(msg.GameObjectHandler);
        });
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, (msg) =>
        {
            if (msg.OwnedObject) return;
            _activeGameObjectHandlers.Remove(msg.GameObjectHandler);
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting IpcProvider Service");
        _loadFileProvider = _pi.GetIpcProvider<string, IGameObject, bool>("UmbraSync.LoadMcdf");
        _loadFileProvider.RegisterFunc(LoadMcdf);
        _loadFileAsyncProvider = _pi.GetIpcProvider<string, IGameObject, Task<bool>>("UmbraSync.LoadMcdfAsync");
        _loadFileAsyncProvider.RegisterFunc(LoadMcdfAsync);
        _handledGameAddresses = _pi.GetIpcProvider<List<nint>>("UmbraSync.GetHandledAddresses");
        _handledGameAddresses.RegisterFunc(GetHandledAddresses);

        _loadFileProviderMare = _pi.GetIpcProvider<string, IGameObject, bool>($"{UmbraApiLabelPrefix}.LoadMcdf");
        _loadFileAsyncProviderMare = _pi.GetIpcProvider<string, IGameObject, Task<bool>>($"{UmbraApiLabelPrefix}.LoadMcdfAsync");
        _handledGameAddressesMare = _pi.GetIpcProvider<List<nint>>($"{UmbraApiLabelPrefix}.GetHandledAddresses");
        HandleMareImpersonation(automatic: true);

        _logger.LogInformation("Started IpcProviderService");
        return Task.CompletedTask;
    }

    public void HandleMareImpersonation(bool automatic = false)
    {
        var externalUmbraLoaded = IsExternalUmbraLoaded();

        if (externalUmbraLoaded)
        {
            if (_impersonating)
            {
                _loadFileProviderMare?.UnregisterFunc();
                _loadFileAsyncProviderMare?.UnregisterFunc();
                _handledGameAddressesMare?.UnregisterFunc();
                _impersonating = false;
                _unregisterTime = DateTime.UtcNow;
                _logger.LogDebug("Unregistered Umbra Sync API");
            }
        }
        else
        {
            if (_mareConfig.Current.UmbraAPI)
            {
                var cancelToken = EnsureFreshCts(ref _registerDelayCts).Token;
                _ = Task.Run(async () =>
                {
                    // Wait before registering to reduce the chance of a race condition
                    if (automatic)
                        await Task.Delay(5000).ConfigureAwait(false);

                    if (cancelToken.IsCancellationRequested)
                        return;

                    if (externalUmbraLoaded)
                    {
                        _logger.LogDebug("Not registering Umbra Sync API: another Umbra API provider is loaded");
                        return;
                    }

                    _loadFileProviderMare?.RegisterFunc(LoadMcdf);
                    _loadFileAsyncProviderMare?.RegisterFunc(LoadMcdfAsync);
                    _handledGameAddressesMare?.RegisterFunc(GetHandledAddresses);
                    _impersonating = true;
                    _logger.LogDebug("Registered UmbraSync API");
                }, cancelToken);
            }
            else
            {
                EnsureFreshCts(ref _registerDelayCts);
                if (_impersonating)
                {
                    _loadFileProviderMare?.UnregisterFunc();
                    _loadFileAsyncProviderMare?.UnregisterFunc();
                    _handledGameAddressesMare?.UnregisterFunc();
                    _impersonating = false;
                    _unregisterTime = DateTime.UtcNow;
                    _logger.LogDebug("Unregistered UmbraSync API");
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping IpcProvider Service");
        _loadFileProvider?.UnregisterFunc();
        _loadFileAsyncProvider?.UnregisterFunc();
        _handledGameAddresses?.UnregisterFunc();

        TryCancel(_registerDelayCts);
        if (_impersonating)
        {
            _loadFileProviderMare?.UnregisterFunc();
            _loadFileAsyncProviderMare?.UnregisterFunc();
            _handledGameAddressesMare?.UnregisterFunc();
        }

        Mediator.UnsubscribeAll(this);
        CancelAndDispose(ref _registerDelayCts);
        return Task.CompletedTask;
    }

    private async Task<bool> LoadMcdfAsync(string path, IGameObject target)
    {
        await ApplyFileAsync(path, target).ConfigureAwait(false);

        return true;
    }

    private bool LoadMcdf(string path, IGameObject target)
    {
        _ = Task.Run(async () => await ApplyFileAsync(path, target).ConfigureAwait(false)).ConfigureAwait(false);

        return true;
    }

    private async Task ApplyFileAsync(string path, IGameObject target)
    {
        _charaDataManager.LoadMcdf(path);
        await (_charaDataManager.LoadedMcdfHeader ?? Task.CompletedTask).ConfigureAwait(false);
        _charaDataManager.McdfApplyToTarget(target.Name.TextValue);
    }

    private bool IsExternalUmbraLoaded()
    {
        if (_impersonating) return false;

        try
        {
            // If no handler is registered, this will throw and we treat it as not loaded.
            _pi.GetIpcSubscriber<List<nint>>($"{UmbraApiLabelPrefix}.GetHandledAddresses").InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<nint> GetHandledAddresses()
    {
        if (!_impersonating)
        {
            if ((DateTime.UtcNow - _unregisterTime).TotalSeconds >= 1.0)
            {
                _logger.LogWarning("GetHandledAddresses called when it should not be registered");
                _handledGameAddressesMare?.UnregisterFunc();
            }
            return [];
        }

        return _activeGameObjectHandlers.Where(g => g.Address != nint.Zero).Select(g => g.Address).Distinct().ToList();
    }

    private static CancellationTokenSource EnsureFreshCts(ref CancellationTokenSource? cts)
    {
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        return cts;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        TryCancel(cts);
        cts.Dispose();
        cts = null;
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed; safe to ignore
        }
    }
}