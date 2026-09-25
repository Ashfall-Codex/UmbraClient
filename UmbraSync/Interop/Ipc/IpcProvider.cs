using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using UmbraSync.Services;
using UmbraSync.WebAPI;
using UmbraSync.API.Data;
using UmbraSync.PlayerData.Pairs;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Interop.Ipc;

public class IpcProvider : IHostedService, IMediatorSubscriber
{
    private const string UmbraApiLabelPrefix = "Umbra";

    private const int RpProfileApiVersion = 1;

    private readonly ILogger<IpcProvider> _logger;
    private readonly IDalamudPluginInterface _pi;
    private readonly MareConfigService _mareConfig;
    private readonly Lazy<PairManager> _pairManager;
    private readonly Lazy<UmbraProfileManager> _profileManager;
    private readonly Lazy<ApiController> _apiController;
    private readonly Lazy<DalamudUtilService> _dalamudUtil;

    private ICallGateProvider<int>? _rpVersionProvider;
    private ICallGateProvider<uint, bool>? _rpIsPairedProvider;
    private ICallGateProvider<uint, string?>? _rpProfileProvider;
    private ICallGateProvider<uint, byte[]?>? _rpPortraitProvider;
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
        CharaDataManager charaDataManager, MareMediator mareMediator, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _pi = pi;
        _mareConfig = mareConfig;
        _pairManager = new Lazy<PairManager>(serviceProvider.GetRequiredService<PairManager>);
        _profileManager = new Lazy<UmbraProfileManager>(serviceProvider.GetRequiredService<UmbraProfileManager>);
        _apiController = new Lazy<ApiController>(serviceProvider.GetRequiredService<ApiController>);
        _dalamudUtil = new Lazy<DalamudUtilService>(serviceProvider.GetRequiredService<DalamudUtilService>);
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

        RegisterRpProfileApi();

        _loadFileProviderMare = _pi.GetIpcProvider<string, IGameObject, bool>($"{UmbraApiLabelPrefix}.LoadMcdf");
        _loadFileAsyncProviderMare = _pi.GetIpcProvider<string, IGameObject, Task<bool>>($"{UmbraApiLabelPrefix}.LoadMcdfAsync");
        _handledGameAddressesMare = _pi.GetIpcProvider<List<nint>>($"{UmbraApiLabelPrefix}.GetHandledAddresses");
        HandleMareImpersonation(automatic: true);

        _logger.LogInformation("Started IpcProviderService");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fiche RP d'une paire, offerte aux autres plugins Ashfall installés sur ce poste.
    /// UmbraSync reste seul juge du droit de regard : il ne répond que pour une paire
    /// visible, et seulement si l'utilisateur laisse l'option active.
    /// </summary>
    private void RegisterRpProfileApi()
    {
        _rpVersionProvider = _pi.GetIpcProvider<int>("UmbraSync.Profile.Version");
        _rpVersionProvider.RegisterFunc(() => RpProfileApiVersion);

        _rpIsPairedProvider = _pi.GetIpcProvider<uint, bool>("UmbraSync.Profile.IsPaired");
        _rpIsPairedProvider.RegisterFunc(objectId => ResolveProfileOwner(objectId) != null);

        _rpProfileProvider = _pi.GetIpcProvider<uint, string?>("UmbraSync.Profile.GetRpProfile");
        _rpProfileProvider.RegisterFunc(GetRpProfile);

        _rpPortraitProvider = _pi.GetIpcProvider<uint, byte[]?>("UmbraSync.Profile.GetRpPortrait");
        _rpPortraitProvider.RegisterFunc(GetRpPortrait);
    }

    /// <summary>
    /// À qui appartient le profil visé, ou null s'il n'y a rien à servir. Deux cas : une
    /// paire visible, ou soi-même, dont le profil vit en configuration locale et ne demande
    /// donc ni serveur ni appairage.
    /// </summary>
    private UserData? ResolveProfileOwner(uint objectId)
    {
        if (objectId is 0 or uint.MaxValue) return null;

        // Son propre profil échappe à l'option de partage : ce sont ses données, lues sur
        // sa machine, par un plugin qu'il a lui-même installé. L'option protège le profil
        // des autres, pas le sien.
        if (_dalamudUtil.Value.GetPlayerCharacter() is { } localPlayer
            && localPlayer.EntityId == objectId)
        {
            var uid = _apiController.Value.UID;
            return string.IsNullOrEmpty(uid) ? null : new UserData(uid);
        }

        if (!_mareConfig.Current.ShareRpProfileWithPlugins) return null;

        return _pairManager.Value.GetVisiblePairByObjectId(objectId)?.UserData;
    }

    private string? GetRpProfile(uint objectId)
    {
        var owner = ResolveProfileOwner(objectId);
        if (owner == null) return null;

        try
        {
            var profile = _profileManager.Value.GetUmbraProfile(owner);

            // Les champs vides sont omis : le plugin qui affiche n'a pas à distinguer une
            // chaîne vide d'un champ que la personne n'a jamais rempli.
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["firstName"] = Empty(profile.RpFirstName),
                ["lastName"] = Empty(profile.RpLastName),
                ["title"] = Empty(profile.RpTitle),
                ["age"] = Empty(profile.RpAge),
                ["race"] = Empty(profile.RpRace),
                ["ethnicity"] = Empty(profile.RpEthnicity),
                ["height"] = Empty(profile.RpHeight),
                ["build"] = Empty(profile.RpBuild),
                ["residence"] = Empty(profile.RpResidence),
                ["occupation"] = Empty(profile.RpOccupation),
                ["affiliation"] = Empty(profile.RpAffiliation),
                ["alignment"] = Empty(profile.RpAlignment),
                ["additionalInfo"] = Empty(profile.RpDescription),
                ["nameColor"] = Empty(profile.RpNameColor),
                ["isNsfw"] = profile.IsRpNSFW,
                // Le champ s'appelle Name côté UmbraSync, label côté contrat : c'est un
                // intitulé affiché, pas un identifiant.
                ["customFields"] = profile.RpCustomFields?
                    .Where(f => !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Value))
                    .OrderBy(f => f.Order)
                    .Select(f => new Dictionary<string, string>(StringComparer.Ordinal) { ["label"] = f.Name, ["value"] = f.Value })
                    .ToList(),
            };

            foreach (var key in payload.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                payload.Remove(key);

            return System.Text.Json.JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profil RP indisponible pour l'objet {ObjectId}", objectId);
            return null;
        }

        static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private byte[]? GetRpPortrait(uint objectId)
    {
        var owner = ResolveProfileOwner(objectId);
        if (owner == null) return null;

        try
        {
            // Octets bruts : les deux plugins vivent dans le même processus, encoder en
            // base64 ne ferait que gonfler une image déjà décodée ici.
            var data = _profileManager.Value.GetUmbraProfile(owner).RpImageData.Value;
            return data.Length == 0 ? null : data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Portrait RP indisponible pour l'objet {ObjectId}", objectId);
            return null;
        }
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

        _rpVersionProvider?.UnregisterFunc();
        _rpIsPairedProvider?.UnregisterFunc();
        _rpProfileProvider?.UnregisterFunc();
        _rpPortraitProvider?.UnregisterFunc();

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