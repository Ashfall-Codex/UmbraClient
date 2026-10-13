using Microsoft.Extensions.Logging;
using UmbraSync.API.Data.Enum;
using UmbraSync.PlayerData.Data;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.PlayerData.Services;

#pragma warning disable MA0040

public sealed class CacheCreationService : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan GlobalDebounceDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FastDebounceDelay = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _cacheCreateLock = new(1);
    private readonly Dictionary<ObjectKind, GameObjectHandler> _cachesToCreate = [];
    private readonly PlayerDataFactory _characterDataFactory;
    private readonly CancellationTokenSource _cts = new();
    // Dernier état publié : jamais modifié en place, remplacé en bloc sous _playerDataLock
    private CharacterData _playerData = new();
    private readonly Lock _playerDataLock = new();
    private readonly HashSet<ObjectKind> _kindsClearedDuringBuild = [];
    private readonly Dictionary<ObjectKind, GameObjectHandler> _playerRelatedObjects = [];
    private Task? _cacheCreationTask;
    private CancellationTokenSource? _globalDebounceCts;
    private readonly Lock _debounceLock = new();
    private readonly HashSet<ObjectKind> _pendingDebouncedKinds = [];
    private readonly Dictionary<ObjectKind, int> _buildFailures = [];
    private const int MaxBuildRetries = 5;
    private int _pendingChangesCount;
    private bool _disposed;

    private bool _isZoning = false;
    private bool _haltCharaDataCreation;

    public CacheCreationService(ILogger<CacheCreationService> logger, MareMediator mediator, GameObjectHandlerFactory gameObjectHandlerFactory,
        PlayerDataFactory characterDataFactory, DalamudUtilService dalamudUtil) : base(logger, mediator)
    {
        _characterDataFactory = characterDataFactory;

        Mediator.Subscribe<CreateCacheForObjectMessage>(this, (msg) =>
        {
            Logger.LogDebug("Received CreateCacheForObject for {handler}, updating", msg.ObjectToCreateFor);
            _ = QueueCacheCreation(msg.ObjectToCreateFor.ObjectKind, msg.ObjectToCreateFor);
        });

        Mediator.Subscribe<ForcePlayerCacheRecreationMessage>(this, (msg) =>
        {
            Logger.LogDebug("Reconstruction complète du cache joueur demandée");
            _ = QueueCacheCreation(ObjectKind.Player);
        });

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (msg) => _isZoning = true);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (msg) => _isZoning = false);

        Mediator.Subscribe<HaltCharaDataCreation>(this, (msg) =>
        {
            _haltCharaDataCreation = !msg.Resume;
        });

        _playerRelatedObjects[ObjectKind.Player] = gameObjectHandlerFactory.Create(ObjectKind.Player, dalamudUtil.GetPlayerPointer, isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.MinionOrMount] = gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => dalamudUtil.GetMinionOrMount(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Pet] = gameObjectHandlerFactory.Create(ObjectKind.Pet, () => dalamudUtil.GetPet(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Companion] = gameObjectHandlerFactory.Create(ObjectKind.Companion, () => dalamudUtil.GetCompanion(), isWatched: true)
            .GetAwaiter().GetResult();

        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler != _playerRelatedObjects[ObjectKind.Player]) return;

            Logger.LogTrace("Removing pet data for {obj}", msg.GameObjectHandler);
            RemoveKindAndPublish(ObjectKind.Pet);
        });

        Mediator.Subscribe<ClearCacheForObjectMessage>(this, (msg) =>
        {
            // ignore pets
            if (msg.ObjectToCreateFor == _playerRelatedObjects[ObjectKind.Pet]) return;
            var kind = msg.ObjectToCreateFor.ObjectKind;
            _ = Task.Run(() =>
            {
                try
                {
                    Logger.LogTrace("Clearing cache for {obj}", msg.ObjectToCreateFor);
                    RemoveKindAndPublish(kind);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Erreur lors du nettoyage des données de {kind}", kind);
                }
            });
        });

        Mediator.Subscribe<CustomizePlusMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            foreach (var item in _playerRelatedObjects
                .Where(item => msg.Address == null || item.Value.Address == msg.Address)
                .Select(k => k.Key))
            {
                Logger.LogDebug("Received CustomizePlus change, queueing {obj}", item);
                QueueCacheCreationDebounced(item);
            }
        });
        Mediator.Subscribe<HeelsOffsetMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            Logger.LogDebug("Received Heels Offset change, queueing player");
            QueueCacheCreationDebounced(ObjectKind.Player);
        });
        Mediator.Subscribe<GlamourerChangedMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            var changedType = _playerRelatedObjects.FirstOrDefault(f => f.Value.Address == msg.Address);
            if (changedType.Key != default || changedType.Value != default)
            {
                Logger.LogDebug("Received Glamourer change, queueing {obj}", changedType.Key);
                QueueCacheCreationDebounced(changedType.Key, FastDebounceDelay);
            }
        });
        Mediator.Subscribe<HonorificMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            if (!string.Equals(msg.NewHonorificTitle, _playerData.HonorificData, StringComparison.Ordinal))
            {
                Logger.LogDebug("Received Honorific change, queueing player");
                QueueCacheCreationDebounced(ObjectKind.Player);
            }
        });
        Mediator.Subscribe<PetNamesMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            if (!string.Equals(msg.PetNicknamesData, _playerData.PetNamesData, StringComparison.Ordinal))
            {
                Logger.LogDebug("Received Pet Nicknames change, queueing player");
                QueueCacheCreationDebounced(ObjectKind.Player);
            }
        });
        Mediator.Subscribe<MoodlesMessage>(this, (msg) =>
        {
            if (_isZoning) return;
            var changedType = _playerRelatedObjects.FirstOrDefault(f => f.Value.Address == msg.Address);
            if (changedType.Key == ObjectKind.Player && changedType.Value != default)
            {
                Logger.LogDebug("Received Moodles change, queueing player");
                QueueCacheCreationDebounced(ObjectKind.Player);
            }
        });
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, (msg) =>
        {
            Logger.LogDebug("Received Penumbra Mod settings change, queueing player");
            QueueCacheCreationDebounced(ObjectKind.Player);
        });

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (msg) => ProcessCacheCreation());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (_disposed) return;
        _disposed = true;

        try
        {
            _playerRelatedObjects.Values.ToList().ForEach(p => p.Dispose());
            lock (_debounceLock)
            {
                _globalDebounceCts?.Cancel();
                _globalDebounceCts?.Dispose();
                _globalDebounceCts = null;
            }
            _cts.Cancel();
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Erreur pendant le Dispose de CacheCreationService");
        }
    }

    private void RemoveKindAndPublish(ObjectKind kind)
    {
        CharacterData updated;
        lock (_playerDataLock)
        {
            updated = PlayerDataFactory.CloneForBuild(_playerData);
            RemoveKind(updated, kind);
            _playerData = updated;
            _kindsClearedDuringBuild.Add(kind);
        }
        Mediator.Publish(new CharacterDataCreatedMessage(updated.ToAPI()));
    }

    private static void RemoveKind(CharacterData data, ObjectKind kind)
    {
        data.FileReplacements.Remove(kind);
        data.GlamourerString.Remove(kind);
        data.CustomizePlusScale.Remove(kind);
    }

    private void QueueCacheCreationDebounced(ObjectKind kind, TimeSpan? customDelay = null)
    {
        var delay = customDelay ?? GlobalDebounceDelay;

        lock (_debounceLock)
        {
            if (_disposed) return;

            // Les kinds s'accumulent : un seul timer, mais aucun changement n'est perdu
            _pendingDebouncedKinds.Add(kind);
            _globalDebounceCts?.Cancel();
            _globalDebounceCts?.Dispose();
            _globalDebounceCts = new CancellationTokenSource();
            var token = _globalDebounceCts.Token;
            _pendingChangesCount++;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);

                    List<ObjectKind> kinds;
                    lock (_debounceLock)
                    {
                        if (token.IsCancellationRequested) return;
                        kinds = [.. _pendingDebouncedKinds];
                        _pendingDebouncedKinds.Clear();
                        if (_pendingChangesCount > 1)
                        {
                            Logger.LogDebug("Debounce coalesced {count} changes into single update ({kinds})", _pendingChangesCount, string.Join(", ", kinds));
                        }
                        _pendingChangesCount = 0;
                    }

                    foreach (var pendingKind in kinds)
                    {
                        await QueueCacheCreation(pendingKind).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when debounce is reset
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Erreur lors de la mise en file des reconstructions");
                }
            }, CancellationToken.None);
        }
    }

    private void ScheduleBuildRetry(ObjectKind kind, GameObjectHandler handler)
    {
        int failures;
        lock (_debounceLock)
        {
            if (_disposed) return;
            _buildFailures.TryGetValue(kind, out failures);
            failures++;
            if (failures > MaxBuildRetries)
            {
                _buildFailures.Remove(kind);
                Logger.LogWarning("Construction des données de {kind} abandonnée après {count} échecs, en attente du prochain changement", kind, MaxBuildRetries);
                return;
            }
            _buildFailures[kind] = failures;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, failures - 1)));
        Logger.LogDebug("Nouvelle construction de {kind} dans {delay}s ({attempt}/{max})", kind, delay.TotalSeconds, failures, MaxBuildRetries);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                await QueueCacheCreation(kind, handler).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // arrêt du plugin
            }
            catch (ObjectDisposedException)
            {
                // arrêt du plugin
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Erreur lors de la replanification de {kind}", kind);
            }
        });
    }
    private async Task QueueCacheCreation(ObjectKind kind, GameObjectHandler? handler = null)
    {
        await _cacheCreateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cachesToCreate[kind] = handler ?? _playerRelatedObjects[kind];
        }
        finally
        {
            _cacheCreateLock.Release();
        }
    }

    private void ProcessCacheCreation()
    {
        if (_isZoning || _haltCharaDataCreation) return;

        if (_cachesToCreate.Count != 0 && (_cacheCreationTask?.IsCompleted ?? true))
        {
            _cacheCreateLock.Wait();
            var toCreate = _cachesToCreate.ToList();
            _cachesToCreate.Clear();
            _cacheCreateLock.Release();

            _cacheCreationTask = Task.Run(async () =>
            {
                try
                {
                    // Construction dans un objet neuf : l'état publié n'est remplacé qu'en cas de succès
                    CharacterData workingData;
                    lock (_playerDataLock)
                    {
                        workingData = PlayerDataFactory.CloneForBuild(_playerData);
                        _kindsClearedDuringBuild.Clear();
                    }

                    int succeeded = 0;
                    foreach (var obj in toCreate)
                    {
                        bool built;
                        try
                        {
                            built = await _characterDataFactory.BuildCharacterData(workingData, obj.Value, _cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Échec de la construction des données de {kind}", obj.Key);
                            built = false;
                        }

                        if (built)
                        {
                            succeeded++;
                            lock (_debounceLock)
                            {
                                _buildFailures.Remove(obj.Key);
                            }
                        }
                        else
                        {
                            ScheduleBuildRetry(obj.Key, obj.Value);
                        }
                    }

                    if (succeeded == 0) return;

                    lock (_playerDataLock)
                    {
                        // Un objet disparu pendant la construction ne doit pas être ressuscité
                        foreach (var kind in _kindsClearedDuringBuild)
                        {
                            RemoveKind(workingData, kind);
                        }
                        _kindsClearedDuringBuild.Clear();
                        _playerData = workingData;
                    }

                    Mediator.Publish(new CharacterDataCreatedMessage(workingData.ToAPI()));
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("Cache Creation cancelled");
                }
                catch (Exception ex)
                {
                    Logger.LogCritical(ex, "Error during Cache Creation Processing");
                }
                finally
                {
                    Logger.LogDebug("Cache Creation complete for {count} objects", toCreate.Count);
                }
            }, _cts.Token);
        }
        else if (_cachesToCreate.Count != 0)
        {
            Logger.LogDebug("Cache Creation stored until previous creation finished");
        }
    }
}
#pragma warning restore MA0040