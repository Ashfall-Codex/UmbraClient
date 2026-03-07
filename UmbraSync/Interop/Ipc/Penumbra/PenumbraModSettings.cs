using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;
using PenumbraApi = global::Penumbra.Api.Helpers;
using PenumbraEnum = global::Penumbra.Api.Enums;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;
namespace UmbraSync.Interop.Ipc.Penumbra;

public sealed class PenumbraModSettings : IDisposable
{
    private readonly PenumbraCore _core;
    private readonly PenumbraApi.EventSubscriber<PenumbraEnum.ModSettingChange, Guid, string, bool> _penumbraModSettingChanged;
    private readonly PenumbraIpc.GetAllModSettings _penumbraGetAllModSettings;

    // Debouncing pour les changements d'options de mod
    private readonly TimeSpan _modSettingDebounce = TimeSpan.FromSeconds(2);
    private DateTime _lastModSettingTrigger = DateTime.MinValue;
    private CancellationTokenSource? _modSettingDebounceCts;
    private readonly Lock _modSettingDebounceLock = new();

    public PenumbraModSettings(PenumbraCore core)
    {
        _core = core;

        // Initialiser l'API pour récupérer les settings de mods
        _penumbraGetAllModSettings = new PenumbraIpc.GetAllModSettings(_core.PluginInterface);

        // S'abonner aux changements de settings de mods
        _penumbraModSettingChanged = PenumbraIpc.ModSettingChanged.Subscriber(
            _core.PluginInterface,
            HandlePenumbraModSettingChanged);
    }
    
    private void HandlePenumbraModSettingChanged(PenumbraEnum.ModSettingChange change, Guid _, string __, bool ___)
    {
        switch (change)
        {
            case PenumbraEnum.ModSettingChange.EnableState:
                _core.Logger.LogDebug("Penumbra mod EnableState changed, triggering immediate sync");
                _core.Mediator.Publish(new PenumbraModSettingChangedMessage());
                _core.CheckAPI();
                break;

            case PenumbraEnum.ModSettingChange.Setting:
                _core.Logger.LogTrace("Penumbra mod Setting changed, triggering debounced sync");
                TriggerDebouncedModSettingSync();
                break;
        }
    }
    
    private void TriggerDebouncedModSettingSync()
    {
        lock (_modSettingDebounceLock)
        {
            _lastModSettingTrigger = DateTime.UtcNow;
            _modSettingDebounceCts?.Cancel();
            _modSettingDebounceCts?.Dispose();
            _modSettingDebounceCts = new CancellationTokenSource();

            var cts = _modSettingDebounceCts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_modSettingDebounce, cts.Token).ConfigureAwait(false);

                    while (DateTime.UtcNow - _lastModSettingTrigger < _modSettingDebounce && !cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(_modSettingDebounce, cts.Token).ConfigureAwait(false);
                    }

                    if (!cts.Token.IsCancellationRequested)
                    {
                        _core.Logger.LogDebug("Penumbra mod settings stabilized, triggering sync after debounce");
                        _core.Mediator.Publish(new PenumbraModSettingChangedMessage());
                    }
                }
                catch (OperationCanceledException)
                {
                    _core.Logger.LogTrace("Mod setting debounce canceled by newer change");
                }
                catch (Exception ex)
                {
                    _core.Logger.LogError(ex, "Error during mod setting debounce");
                }
            }, cts.Token);
        }
    }
    
    public Task<HashSet<string>?> GetEnabledModRootsAsync()
    {
        if (!_core.APIAvailable) return Task.FromResult<HashSet<string>?>(null);

        return _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var coll = new PenumbraIpc.GetCollection(_core.PluginInterface).Invoke(PenumbraEnum.ApiCollectionType.Current);
            if (coll == null) return null;
            var collId = coll.Value.Id;

            var (ec, all) = _penumbraGetAllModSettings.Invoke(collId, ignoreInheritance: false, ignoreTemporary: true, key: 0);
            if (ec != PenumbraEnum.PenumbraApiEc.Success || all == null || all.Count == 0)
                return null;

            HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in all)
            {
                var modDirName = kv.Key;
                var settings = kv.Value;
                var isEnabled = settings.Item1;
                if (!isEnabled) continue;
                var root = modDirName;
                var lastSlash = root.LastIndexOf('\\');
                if (lastSlash > 0)
                    root = root[..lastSlash];

                result.Add(root);
            }

            return result;
        });
    }
    
    public Task<List<string>> GetEnabledModPathsForDefaultCollectionAsync()
    {
        if (!_core.APIAvailable) return Task.FromResult(new List<string>());

        return _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            // Récupérer le répertoire racine des mods Penumbra
            var modRoot = _core.ModDirectory;
            if (string.IsNullOrEmpty(modRoot))
                return new List<string>();

            var coll = new PenumbraIpc.GetCollection(_core.PluginInterface).Invoke(PenumbraEnum.ApiCollectionType.Default);
            if (coll == null) return new List<string>();
            var collId = coll.Value.Id;

            var (ec, all) = _penumbraGetAllModSettings.Invoke(collId, ignoreInheritance: false, ignoreTemporary: true, key: 0);
            if (ec != PenumbraEnum.PenumbraApiEc.Success || all == null || all.Count == 0)
                return new List<string>();

            var result = new List<string>();
            foreach (var kv in all)
            {
                var modDirName = kv.Key;
                var settings = kv.Value;
                var isEnabled = settings.Item1;
                if (!isEnabled) continue;

                // Combiner le répertoire racine Penumbra avec le nom du dossier du mod
                var fullPath = Path.Combine(modRoot, modDirName);
                result.Add(fullPath);
            }

            return result;
        });
    }

    public void Dispose()
    {
        _modSettingDebounceCts?.Cancel();
        _modSettingDebounceCts?.Dispose();
        _penumbraModSettingChanged.Dispose();
    }
}
