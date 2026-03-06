using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;
using PenumbraEnum = global::Penumbra.Api.Enums;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;

namespace UmbraSync.Interop.Ipc.Penumbra;

public sealed class PenumbraCollections : IDisposable
{
    private readonly PenumbraCore _core;
    private readonly PenumbraIpc.AssignTemporaryCollection _penumbraAssignTemporaryCollection;
    private readonly PenumbraIpc.CreateTemporaryCollection _penumbraCreateNamedTemporaryCollection;
    private readonly PenumbraIpc.GetCollection _penumbraGetCollection;
    private readonly PenumbraIpc.DeleteTemporaryCollection _penumbraRemoveTemporaryCollection;
    private readonly PenumbraIpc.GetCollections _penumbraGetCollections;
    private readonly ConcurrentDictionary<Guid, string> _activeTemporaryCollections = new();
    private readonly MediatorSubscriberBase _subscriber;
    private int _cleanupScheduled;

    public PenumbraCollections(PenumbraCore core)
    {
        _core = core;

        // Initialiser les IPC de collections
        _penumbraCreateNamedTemporaryCollection = new PenumbraIpc.CreateTemporaryCollection(_core.PluginInterface);
        _penumbraRemoveTemporaryCollection = new PenumbraIpc.DeleteTemporaryCollection(_core.PluginInterface);
        _penumbraAssignTemporaryCollection = new PenumbraIpc.AssignTemporaryCollection(_core.PluginInterface);
        _penumbraGetCollection = new PenumbraIpc.GetCollection(_core.PluginInterface);
        _penumbraGetCollections = new PenumbraIpc.GetCollections(_core.PluginInterface);

        // S'abonner à l'événement d'initialisation de Penumbra pour cleanup
        _subscriber = new CollectionsSubscriber(_core.Logger, _core.Mediator, this);
    }

    private sealed class CollectionsSubscriber : MediatorSubscriberBase
    {
        private readonly PenumbraCollections _collections;

        public CollectionsSubscriber(ILogger logger, MareMediator mediator, PenumbraCollections collections)
            : base(logger, mediator)
        {
            _collections = collections;
            Mediator.Subscribe<PenumbraInitializedMessage>(this, _ => _collections.ScheduleCleanup());
        }
    }

    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (!_core.APIAvailable || collName == Guid.Empty) return;

        await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
            logger.LogTrace("Assigning Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            return collName;
        }).ConfigureAwait(false);
    }

    public async Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid)
    {
        if (!_core.APIAvailable) return Guid.Empty;

        var (collectionId, collectionName) = await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            Guid collId;
            var random = new Random();
            var collName = "UmbraSync_" + uid + "_" + random.Next().ToString();
            PenumbraEnum.PenumbraApiEc penEC = _penumbraCreateNamedTemporaryCollection.Invoke(uid + random.Next().ToString(), collName, out collId);
            logger.LogTrace("Creating Temp Collection {collName}, GUID: {collId}", collName, collId);
            if (penEC != PenumbraEnum.PenumbraApiEc.Success)
            {
                logger.LogError("Failed to create temporary collection for {collName} with error code {penEC}. Please include this line in any error reports", collName, penEC);
                return (Guid.Empty, string.Empty);
            }
            return (collId, collName);

        }).ConfigureAwait(false);

        if (collectionId != Guid.Empty)
        {
            _activeTemporaryCollections[collectionId] = collectionName;
        }

        return collectionId;
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!_core.APIAvailable || collId == Guid.Empty) return;

        await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            RemoveTemporaryCollection(logger, applicationId, collId);
        }).ConfigureAwait(false);

        _activeTemporaryCollections.TryRemove(collId, out _);
    }

    public void RemoveTemporaryCollection(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!_core.APIAvailable || collId == Guid.Empty) return;
        logger.LogTrace("[{applicationId}] Removing temp collection for {collId}", applicationId, collId);
        var ret2 = _penumbraRemoveTemporaryCollection.Invoke(collId);
        logger.LogTrace("[{applicationId}] RemoveTemporaryCollection: {ret2}", applicationId, ret2);
    }

    public (Guid id, string name)? GetCurrentCollection()
    {
        if (!_core.APIAvailable) return null;

        var coll = _penumbraGetCollection.Invoke(PenumbraEnum.ApiCollectionType.Current);
        if (coll == null) return null;

        return (coll.Value.Id, coll.Value.Name);
    }

    public Guid? GetDefaultCollectionId()
    {
        if (!_core.APIAvailable) return null;

        var coll = _penumbraGetCollection.Invoke(PenumbraEnum.ApiCollectionType.Default);
        return coll?.Id;
    }

    public async Task<Guid?> GetDefaultCollectionIdAsync()
    {
        if (!_core.APIAvailable)
        {
            _core.Logger.LogWarning("[GetDefaultCollId] APIAvailable=false, abandon");
            return null;
        }

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            try
            {
                var coll = _penumbraGetCollection.Invoke(PenumbraEnum.ApiCollectionType.Default);
                _core.Logger.LogDebug("[GetDefaultCollId] GetCollection(Default) → HasValue={HasValue}, Id={Id}",
                    coll.HasValue, coll?.Id);
                return coll?.Id;
            }
            catch (Exception ex)
            {
                _core.Logger.LogError(ex, "[GetDefaultCollId] Exception lors de GetCollection(Default)");
                return null;
            }
        }).ConfigureAwait(false);
    }

    private void ScheduleCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(CleanupTemporaryCollectionsAsync);
    }

    private async Task CleanupTemporaryCollectionsAsync()
    {
        if (!_core.APIAvailable)
        {
            Interlocked.Exchange(ref _cleanupScheduled, 0);
            return;
        }

        try
        {
            var collections = await _core.DalamudUtil.RunOnFrameworkThread(() => _penumbraGetCollections.Invoke()).ConfigureAwait(false);
            foreach (var (collectionId, name) in collections)
            {
                if (!IsUmbraCollectionName(name) || _activeTemporaryCollections.ContainsKey(collectionId))
                {
                    continue;
                }

                _core.Logger.LogDebug("Cleaning up stale temporary collection {CollectionName} ({CollectionId})", name, collectionId);
                var deleteResult = await _core.DalamudUtil.RunOnFrameworkThread(() =>
                {
                    var result = _penumbraRemoveTemporaryCollection.Invoke(collectionId);
                    _core.Logger.LogTrace("Cleanup RemoveTemporaryCollection result for {CollectionName} ({CollectionId}): {Result}", name, collectionId, result);
                    return result;
                }).ConfigureAwait(false);

                if (deleteResult == PenumbraEnum.PenumbraApiEc.Success)
                {
                    _activeTemporaryCollections.TryRemove(collectionId, out _);
                }
                else
                {
                    _core.Logger.LogDebug("Skipped removing temporary collection {CollectionName} ({CollectionId}). Result: {Result}", name, collectionId, deleteResult);
                }
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning(ex, "Failed to clean up Penumbra temporary collections");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupScheduled, 0);
        }
    }

    // Récupère la liste de toutes les collections Penumbra (Id → Nom).
    public async Task<Dictionary<Guid, string>> GetAllCollectionsAsync()
    {
        if (!_core.APIAvailable) return new Dictionary<Guid, string>();

        return await _core.DalamudUtil.RunOnFrameworkThread(() => _penumbraGetCollections.Invoke()).ConfigureAwait(false);
    }

    private static bool IsUmbraCollectionName(string? name)
        => !string.IsNullOrEmpty(name) && name.StartsWith("UmbraSync_", StringComparison.Ordinal);

    public void Dispose()
    {
        (_subscriber as IDisposable)?.Dispose();
    }
}
