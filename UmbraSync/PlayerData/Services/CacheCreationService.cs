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
    private readonly CharacterData _playerData = new();
    private readonly Dictionary<ObjectKind, GameObjectHandler> _playerRelatedObjects = [];
    private Task? _cacheCreationTask;
    private CancellationTokenSource? _globalDebounceCts;
    private readonly Lock _debounceLock = new();
    private int _pendingChangesCount;

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
            _playerData.FileReplacements.Remove(ObjectKind.Pet);
            _playerData.GlamourerString.Remove(ObjectKind.Pet);
            _playerData.CustomizePlusScale.Remove(ObjectKind.Pet);
            Mediator.Publish(new CharacterDataCreatedMessage(_playerData.ToAPI()));
        });

        Mediator.Subscribe<ClearCacheForObjectMessage>(this, (msg) =>
        {
            // ignore pets
            if (msg.ObjectToCreateFor == _playerRelatedObjects[ObjectKind.Pet]) return;
            _ = Task.Run(() =>
            {
                Logger.LogTrace("Clearing cache for {obj}", msg.ObjectToCreateFor);
                _playerData.FileReplacements.Remove(msg.ObjectToCreateFor.ObjectKind);
                _playerData.GlamourerString.Remove(msg.ObjectToCreateFor.ObjectKind);
                _playerData.CustomizePlusScale.Remove(msg.ObjectToCreateFor.ObjectKind);
                Mediator.Publish(new CharacterDataCreatedMessage(_playerData.ToAPI()));
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

        _playerRelatedObjects.Values.ToList().ForEach(p => p.Dispose());
        _globalDebounceCts?.Cancel();
        _globalDebounceCts?.Dispose();
        _cts.Dispose();
    }
    
    private void QueueCacheCreationDebounced(ObjectKind kind, TimeSpan? customDelay = null)
    {
        var delay = customDelay ?? GlobalDebounceDelay;

        lock (_debounceLock)
        {
            // Cancel any pending debounce timer
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

                    await QueueCacheCreation(kind).ConfigureAwait(false);
                    lock (_debounceLock)
                    {
                        if (_pendingChangesCount > 1)
                        {
                            Logger.LogDebug("Debounce coalesced {count} changes into single update", _pendingChangesCount);
                        }
                        _pendingChangesCount = 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when debounce is reset
                }
            }, token);
        }
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
                    foreach (var obj in toCreate)
                    {
                        await _characterDataFactory.BuildCharacterData(_playerData, obj.Value, _cts.Token).ConfigureAwait(false);
                    }

                    Mediator.Publish(new CharacterDataCreatedMessage(_playerData.ToAPI()));
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