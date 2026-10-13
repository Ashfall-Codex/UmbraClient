using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using UmbraSync.API.Data.Enum;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Data;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.FileCache;

public sealed class TransientResourceManager : DisposableMediatorSubscriberBase
{
    private readonly Lock _cacheAdditionLock = new();
    private readonly HashSet<string> _cachedHandledPaths = new(StringComparer.Ordinal);
    private readonly TransientConfigService _configurationService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly string[] _fileTypesToHandle = ["tmb", "pap", "avfx", "atex", "sklb", "eid", "phyb", "scd", "skp", "shpk"];
    private readonly Lock _playerRelatedLock = new();
    private readonly HashSet<GameObjectHandler> _playerRelatedPointers = [];
    private ConcurrentDictionary<IntPtr, ObjectKind> _cachedFrameAddresses = [];

    public TransientResourceManager(ILogger<TransientResourceManager> logger, TransientConfigService configurationService,
        DalamudUtilService dalamudUtil, MareMediator mediator) : base(logger, mediator)
    {
        _configurationService = configurationService;
        _dalamudUtil = dalamudUtil;

        Mediator.Subscribe<PenumbraResourceLoadMessage>(this, Manager_PenumbraResourceLoadEvent);
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, (_) => Manager_PenumbraModSettingChanged());
        Mediator.Subscribe<PriorityFrameworkUpdateMessage>(this, (_) => DalamudUtil_FrameworkUpdate());
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            bool isPlayerRelated;
            lock (_playerRelatedLock)
            {
                isPlayerRelated = _playerRelatedPointers.Contains(msg.GameObjectHandler);
            }
            if (isPlayerRelated)
            {
                DalamudUtil_ClassJobChanged();
            }
        });
        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_playerRelatedLock)
            {
                _playerRelatedPointers.Add(msg.GameObjectHandler);
            }
        });
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_playerRelatedLock)
            {
                _playerRelatedPointers.Remove(msg.GameObjectHandler);
            }
        });
    }

    private GameObjectHandler[] SnapshotPlayerRelatedPointers()
    {
        lock (_playerRelatedLock)
        {
            return [.. _playerRelatedPointers];
        }
    }

    private string PlayerPersistentDataKey => _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult() + "_" + _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
    private ConcurrentDictionary<ObjectKind, HashSet<string>>? _semiTransientResources = null;
    private ConcurrentDictionary<ObjectKind, HashSet<string>> SemiTransientResources
    {
        get
        {
            var resources = _semiTransientResources;
            if (resources == null)
            {
                resources = new();
                resources.TryAdd(ObjectKind.Player, new HashSet<string>(StringComparer.Ordinal));
                if (_configurationService.Current.PlayerPersistentTransientCache.TryGetValue(PlayerPersistentDataKey, out var gamePaths))
                {
                    int restored = 0;
                    foreach (var gamePath in gamePaths)
                    {
                        if (string.IsNullOrEmpty(gamePath)) continue;

                        try
                        {
                            Logger.LogDebug("Loaded persistent transient resource {path}", gamePath);
                            resources[ObjectKind.Player].Add(gamePath);
                            restored++;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Error during loading persistent transient resource {path}", gamePath);
                        }
                    }
                    Logger.LogDebug("Restored {restored}/{total} semi persistent resources", restored, gamePaths.Count);
                }
                _semiTransientResources = resources;
            }

            return resources;
        }
    }
    private ConcurrentDictionary<IntPtr, HashSet<string>> TransientResources { get; } = new();

    public void CleanUpSemiTransientResources(ObjectKind objectKind, List<FileReplacement>? fileReplacement = null)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? value))
        {
            if (fileReplacement == null)
            {
                value.Clear();
                return;
            }

            foreach (var replacement in fileReplacement.Where(p => !p.HasFileReplacement).SelectMany(p => p.GamePaths).ToList())
            {
                value.RemoveWhere(p => string.Equals(p, replacement, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public HashSet<string> GetSemiTransientResources(ObjectKind objectKind)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out var result))
        {
            return result;
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    public List<string> GetTransientResources(IntPtr gameObject)
    {
        if (TransientResources.TryGetValue(gameObject, out var result))
        {
            return [.. result];
        }

        return [];
    }

    public void PersistTransientResources(IntPtr gameObject, ObjectKind objectKind)
    {
        if (!SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? value))
        {
            value = new HashSet<string>(StringComparer.Ordinal);
            SemiTransientResources[objectKind] = value;
        }

        if (!TransientResources.TryGetValue(gameObject, out var resources))
        {
            return;
        }

        var transientResources = resources.ToList();
        Logger.LogDebug("Persisting {count} transient resources", transientResources.Count);
        foreach (var gamePath in transientResources)
        {
            value.Add(gamePath);
        }

        if (objectKind == ObjectKind.Player && SemiTransientResources.TryGetValue(ObjectKind.Player, out var fileReplacements))
        {
            _configurationService.Current.PlayerPersistentTransientCache[PlayerPersistentDataKey] = fileReplacements.Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.Ordinal);
            _configurationService.Save();
        }
        TransientResources[gameObject].Clear();
    }

    internal void AddSemiTransientResource(ObjectKind objectKind, string item)
    {
        if (!SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? value))
        {
            value = new HashSet<string>(StringComparer.Ordinal);
            SemiTransientResources[objectKind] = value;
        }

        value.Add(item.ToLowerInvariant());
    }

    internal void ClearTransientPaths(IntPtr ptr, List<string> list)
    {
        if (TransientResources.TryGetValue(ptr, out var set))
        {
            foreach (var file in set.Where(p => list.Contains(p, StringComparer.OrdinalIgnoreCase)))
            {
                Logger.LogTrace("Removing From Transient: {file}", file);
            }

            int removed = set.RemoveWhere(p => list.Contains(p, StringComparer.OrdinalIgnoreCase));
            Logger.LogInformation("Removed {removed} previously existing transient paths", removed);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_sendTransientLock)
        {
            if (!_transientDisposed)
            {
                _transientDisposed = true;
                try
                {
                    _sendTransientCts.Cancel();
                    _sendTransientCts.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Erreur ignorée à l'annulation du debounce transitoire");
                }
            }
        }

        try
        {
            TransientResources.Clear();
            SemiTransientResources.Clear();
            if (SemiTransientResources.TryGetValue(ObjectKind.Player, out HashSet<string>? value))
            {
                _configurationService.Current.PlayerPersistentTransientCache[PlayerPersistentDataKey] = value;
                _configurationService.Save();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to persist transient resources during disposal");
        }
    }

    private void DalamudUtil_ClassJobChanged()
    {
        if (SemiTransientResources.TryGetValue(ObjectKind.Pet, out HashSet<string>? value))
        {
            value.Clear();
        }
    }

    private void DalamudUtil_FrameworkUpdate()
    {

        ConcurrentDictionary<nint, ObjectKind> frameAddresses = [];
        foreach (GameObjectHandler handler in SnapshotPlayerRelatedPointers())
        {
            nint address = handler.CurrentAddress();
            if (address == nint.Zero) continue;
            frameAddresses[address] = handler.ObjectKind;
        }
        _cachedFrameAddresses = frameAddresses;
        lock (_cacheAdditionLock)
        {
            _cachedHandledPaths.Clear();
        }
        foreach (var item in TransientResources.Where(item => !_dalamudUtil.IsGameObjectPresent(item.Key)).Select(i => i.Key).ToList())
        {
            Logger.LogDebug("Object not present anymore: {addr}", item.ToString("X", CultureInfo.InvariantCulture));
            TransientResources.TryRemove(item, out _);
        }
    }

    private void Manager_PenumbraModSettingChanged()
    {
        _ = Task.Run(() =>
        {
            Logger.LogDebug("Penumbra Mod Settings changed, verifying SemiTransientResources");
            foreach (var item in SnapshotPlayerRelatedPointers())
            {
                Mediator.Publish(new TransientResourceChangedMessage(item.Address));
            }
        });
    }

    private void Manager_PenumbraResourceLoadEvent(PenumbraResourceLoadMessage msg)
    {
        var gamePath = msg.GamePath.ToLowerInvariant();
        var gameObject = msg.GameObject;
        var filePath = msg.FilePath;

        // ignore files to not handle
        if (!_fileTypesToHandle.Any(type => gamePath.EndsWith(type, StringComparison.OrdinalIgnoreCase))) return;

        // ignore files not belonging to anything player related, avant la déduplication :
        // un autre joueur qui charge le même chemin dans la frame ne doit pas masquer le nôtre
        if (!_cachedFrameAddresses.TryGetValue(gameObject, out _)) return;

        // ignore files already processed this frame
        lock (_cacheAdditionLock)
        {
            if (!_cachedHandledPaths.Add(gameObject.ToString("X", CultureInfo.InvariantCulture) + "|" + gamePath)) return;
        }

        // replace individual mtrl stuff
        if (filePath.StartsWith("|", StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Split("|")[2];
        }
        // replace filepath
        filePath = filePath.ToLowerInvariant().Replace("\\", "/", StringComparison.OrdinalIgnoreCase);

        // ignore files that are the same
        var replacedGamePath = gamePath.ToLowerInvariant().Replace("\\", "/", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filePath, replacedGamePath, StringComparison.OrdinalIgnoreCase)) return;

        if (!TransientResources.TryGetValue(gameObject, out HashSet<string>? value))
        {
            value = new(StringComparer.OrdinalIgnoreCase);
            TransientResources[gameObject] = value;
        }

        if (value.Contains(replacedGamePath) ||
            SemiTransientResources.SelectMany(k => k.Value).Any(f => string.Equals(f, gamePath, StringComparison.OrdinalIgnoreCase)))
        {
            Logger.LogTrace("Not adding {replacedPath} : {filePath}", replacedGamePath, filePath);
        }
        else
        {
            var thing = SnapshotPlayerRelatedPointers().FirstOrDefault(f => f.Address == gameObject);
            value.Add(replacedGamePath);
            Logger.LogDebug("Adding {replacedGamePath} for {gameObject} ({filePath})", replacedGamePath, thing?.ToString() ?? gameObject.ToString("X", CultureInfo.InvariantCulture), filePath);
            ScheduleTransientResourceChanged(gameObject);
        }
    }

    // Un seul timer pour tous les objets, mais chaque objet modifié reçoit son message
    private void ScheduleTransientResourceChanged(IntPtr gameObject)
    {
        CancellationToken token;
        lock (_sendTransientLock)
        {
            if (_transientDisposed) return;
            _pendingTransientObjects.Add(gameObject);
            _sendTransientCts.Cancel();
            _sendTransientCts.Dispose();
            _sendTransientCts = new();
            token = _sendTransientCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                List<IntPtr> objects;
                lock (_sendTransientLock)
                {
                    if (token.IsCancellationRequested) return;
                    objects = [.. _pendingTransientObjects];
                    _pendingTransientObjects.Clear();
                }

                foreach (var obj in objects)
                {
                    Mediator.Publish(new TransientResourceChangedMessage(obj));
                }
            }
            catch (OperationCanceledException)
            {
                // debounce réarmé
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Erreur lors de la notification des ressources transitoires");
            }
        }, CancellationToken.None);
    }

    internal void RemoveTransientResource(ObjectKind objectKind, string path)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out var resources))
        {
            resources.RemoveWhere(f => string.Equals(path, f, StringComparison.OrdinalIgnoreCase));
            _configurationService.Current.PlayerPersistentTransientCache[PlayerPersistentDataKey] = resources;
            _configurationService.Save();
        }
    }

    private readonly Lock _sendTransientLock = new();
    private readonly HashSet<IntPtr> _pendingTransientObjects = [];
    private bool _transientDisposed;
    private CancellationTokenSource _sendTransientCts = new();
}