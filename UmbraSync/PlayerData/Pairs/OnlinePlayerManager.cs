using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Comparer;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using UmbraSync.WebAPI.Files;

namespace UmbraSync.PlayerData.Pairs;

public class OnlinePlayerManager : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly PairManager _pairManager;
    private readonly CollectionOverrideResolver _collectionOverrideResolver;
    private readonly Lock _stateLock = new();
    private CharacterData? _lastCreatedData;
    private long _dataGeneration;
    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private readonly HashSet<UserData> _usersToPushDataTo = new(UserDataComparer.Instance);
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    private int _pushRequested;
    private int _consecutivePushFailures;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _lastRebuildRequestUtc = DateTime.MinValue;
    private bool _disposed;
    private static readonly TimeSpan UploadFailureCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MissingFilesRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RebuildRequestCooldown = TimeSpan.FromSeconds(30);
    private const int MaxConsecutivePushFailures = 8;

    public OnlinePlayerManager(ILogger<OnlinePlayerManager> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, MareMediator mediator, FileUploadManager fileTransferManager,
        CollectionOverrideResolver collectionOverrideResolver) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _collectionOverrideResolver = collectionOverrideResolver;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var newData = msg.CharacterData;
            bool changed;
            lock (_stateLock)
            {
                changed = _lastCreatedData == null || !string.Equals(newData.DataHash.Value, _lastCreatedData.DataHash.Value, StringComparison.Ordinal);
                if (changed)
                {
                    _lastCreatedData = newData;
                    _dataGeneration++;
                }
            }

            if (changed)
            {
                Logger.LogTrace("Nouveau hash de données stocké: {hash}", newData.DataHash.Value);
                PushToAllVisibleUsers();
            }
            else
            {
                Logger.LogTrace("Hash identique au précédent: {hash}", newData.DataHash.Value);
            }
        });

        Mediator.Subscribe<PairOnlineMessage>(this, (msg) =>
        {
            if (!_apiController.IsConnected) return;
            lock (_stateLock)
            {
                if (_lastCreatedData == null) return;
                _usersToPushDataTo.Add(msg.User);
            }
            PushCharacterData();
        });
        Mediator.Subscribe<ConnectedMessage>(this, (_) =>
        {
            lock (_stateLock)
            {
                ResetRetryState();
            }
            PushToAllVisibleUsers();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _fileTransferManager.CancelUpload();
            _previouslyVisiblePlayers.Clear();
            lock (_stateLock)
            {
                _usersToPushDataTo.Clear();
                ResetRetryState();
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _runtimeCts.Cancel();
                _runtimeCts.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Erreur ignorée pendant le Dispose de OnlinePlayerManager");
            }
            // _pushLock n'est pas disposé : un push en vol peut encore le relâcher
        }
        base.Dispose(disposing);
    }

    private void PushToAllVisibleUsers()
    {
        var visibleUsers = GetVisibleUsers();
        int count;
        lock (_stateLock)
        {
            foreach (var user in visibleUsers)
            {
                _usersToPushDataTo.Add(user);
            }
            count = _usersToPushDataTo.Count;
        }

        if (count > 0)
        {
            Logger.LogDebug("Push programmé pour {count} joueurs visibles (hash: {hash})",
                count, _lastCreatedData?.DataHash.Value ?? "UNKNOWN");
            PushCharacterData();
        }
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected) return;

        var allVisibleUsers = GetVisibleUsers();
        var newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers, UserDataComparer.Instance).ToList();

        _previouslyVisiblePlayers.Clear();
        _previouslyVisiblePlayers.AddRange(allVisibleUsers);

        if (newVisibleUsers.Count == 0)
        {
            int pendingCount = 0;
            lock (_stateLock)
            {
                if (_usersToPushDataTo.Count > 0 && _nextRetryUtc > DateTime.MinValue && DateTime.UtcNow >= _nextRetryUtc)
                {
                    pendingCount = _usersToPushDataTo.Count;
                    _nextRetryUtc = DateTime.MinValue;
                }
            }

            if (pendingCount > 0)
            {
                Logger.LogDebug("Nouvelle tentative de push après échec pour {count} joueurs", pendingCount);
                PushCharacterData();
            }
            return;
        }

        Logger.LogDebug("Nouveaux joueurs visibles détectés: {users}",
            string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));

        lock (_stateLock)
        {
            foreach (var user in newVisibleUsers)
            {
                _usersToPushDataTo.Add(user);
            }
        }
        PushCharacterData();
    }

    private void PushCharacterData()
    {
        if (_disposed) return;
        lock (_stateLock)
        {
            if (_lastCreatedData == null || _usersToPushDataTo.Count == 0) return;
        }

        // Un passage déjà en attente du verrou traitera aussi ces destinataires
        if (Interlocked.Exchange(ref _pushRequested, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await PushCharacterDataAsync(_runtimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _pushRequested, 0);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref _pushRequested, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _pushRequested, 0);
                Logger.LogWarning(ex, "Erreur inattendue pendant le push des données");
            }
        });
    }

    private async Task PushCharacterDataAsync(CancellationToken token)
    {
        await _pushLock.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _pushRequested, 0);
        try
        {
            // Boucle jusqu'à ce que chaque destinataire en attente ait reçu la dernière version construite
            while (!token.IsCancellationRequested && _apiController.IsConnected)
            {
                CharacterData data;
                long generation;
                List<UserData> recipients;
                lock (_stateLock)
                {
                    if (_lastCreatedData == null || _usersToPushDataTo.Count == 0)
                        return;

                    if (_nextRetryUtc > DateTime.MinValue && DateTime.UtcNow < _nextRetryUtc)
                    {
                        Logger.LogDebug("Push en attente après échec ({remaining:F1}s restantes)",
                            (_nextRetryUtc - DateTime.UtcNow).TotalSeconds);
                        return;
                    }

                    data = _lastCreatedData;
                    generation = _dataGeneration;
                    recipients = _usersToPushDataTo.ToList();
                }

                if (!await UploadAndPushAsync(data, generation, recipients, token).ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            _pushLock.Release();
        }
    }

    private async Task<bool> UploadAndPushAsync(CharacterData data, long generation, List<UserData> recipients, CancellationToken token)
    {
        CharacterData dataToSend;
        try
        {
            Logger.LogDebug("Démarrage upload (hash: {hash}) pour {count} destinataires", data.DataHash.Value, recipients.Count);
            dataToSend = await _fileTransferManager.UploadFiles(data.DeepClone(), recipients, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RegisterPushFailure(ex);
            return false;
        }

        if (IsStale(generation))
        {
            Logger.LogDebug("Données modifiées pendant l'upload de {hash}, reprise avec la dernière version", data.DataHash.Value);
            return true;
        }

        List<UserData> served = [];
        bool allSucceeded = true;

        if (_collectionOverrideResolver.HasAnyCollectionOverride())
        {
            var (defaultUsers, overrideUsers) = _collectionOverrideResolver.SplitUsersByCollection(recipients);

            if (defaultUsers.Count > 0)
            {
                Logger.LogDebug("Push de {hash} (collection par défaut) vers {users}",
                    dataToSend.DataHash.Value,
                    string.Join(", ", defaultUsers.Select(k => k.AliasOrUID)));
                allSucceeded &= await PushToAsync(dataToSend, defaultUsers, served, token).ConfigureAwait(false);
            }

            foreach (var (collectionId, collectionUsers) in overrideUsers)
            {
                var overrideData = await GetOverrideDataAsync(dataToSend, collectionId, collectionUsers, token).ConfigureAwait(false);
                if (overrideData == null)
                {
                    allSucceeded = false;
                    continue;
                }

                Logger.LogDebug("Push de collection override {collId} vers {users}",
                    collectionId,
                    string.Join(", ", collectionUsers.Select(k => k.AliasOrUID)));
                allSucceeded &= await PushToAsync(overrideData, collectionUsers, served, token).ConfigureAwait(false);
            }
        }
        else
        {
            Logger.LogDebug("Push de {hash} vers {users}",
                dataToSend.DataHash.Value,
                string.Join(", ", recipients.Select(k => k.AliasOrUID)));
            allSucceeded &= await PushToAsync(dataToSend, recipients, served, token).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            // Si une nouvelle version est arrivée pendant le push, tout le monde doit la recevoir
            if (_dataGeneration == generation)
            {
                foreach (var user in served)
                {
                    _usersToPushDataTo.Remove(user);
                }
            }

            if (allSucceeded)
                ResetRetryState();
        }

        if (!allSucceeded)
        {
            RegisterPushFailure(null);
            return false;
        }

        return true;
    }

    private async Task<bool> PushToAsync(CharacterData data, List<UserData> users, List<UserData> served, CancellationToken token)
    {
        if (!await _apiController.PushCharacterData(data, users, token).ConfigureAwait(false))
            return false;

        served.AddRange(users);
        return true;
    }

    private async Task<CharacterData?> GetOverrideDataAsync(CharacterData defaultData, Guid collectionId, List<UserData> users, CancellationToken token)
    {
        CharacterData? alternativeData = null;
        try
        {
            alternativeData = await _collectionOverrideResolver.BuildAlternativeCharacterData(defaultData, collectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Erreur lors de la construction de la collection override {collId}", collectionId);
        }

        if (alternativeData == null)
        {
            // Ne jamais envoyer la tenue par défaut à une syncshell qui a sa propre collection
            Logger.LogWarning("Collection override {collId} indisponible, aucun envoi à ses membres", collectionId);
            return null;
        }

        try
        {
            return await _fileTransferManager.UploadFiles(alternativeData, users, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Upload des fichiers de la collection override {collId} échoué", collectionId);
            return null;
        }
    }

    private bool IsStale(long generation)
    {
        lock (_stateLock)
        {
            return _dataGeneration != generation;
        }
    }

    private void RegisterPushFailure(Exception? ex)
    {
        bool missingLocalFiles = ex is FileUploadManager.MissingLocalUploadFilesException;
        bool requestRebuild = false;
        bool abandoned = false;
        int failures;
        TimeSpan delay = TimeSpan.Zero;
        lock (_stateLock)
        {
            failures = ++_consecutivePushFailures;
            if (failures > MaxConsecutivePushFailures)
            {
                abandoned = true;
                _usersToPushDataTo.Clear();
                ResetRetryState();
            }
            else
            {
                var baseDelay = missingLocalFiles ? MissingFilesRetryDelay : UploadFailureCooldown;
                delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, baseDelay.Ticks << Math.Min(failures - 1, 10)));
                _nextRetryUtc = DateTime.UtcNow + delay;
            }

            if (missingLocalFiles && DateTime.UtcNow - _lastRebuildRequestUtc >= RebuildRequestCooldown)
            {
                _lastRebuildRequestUtc = DateTime.UtcNow;
                requestRebuild = true;
            }
        }

        if (abandoned)
        {
            Logger.LogWarning(ex, "Push abandonné après {count} échecs consécutifs, en attente du prochain changement", MaxConsecutivePushFailures);
        }
        else if (missingLocalFiles)
        {
            Logger.LogWarning("Fichiers introuvables localement à l'upload ({count}), nouvelle tentative dans {delay:F0}s",
                ((FileUploadManager.MissingLocalUploadFilesException)ex!).MissingHashes.Count, delay.TotalSeconds);
        }
        else
        {
            Logger.LogWarning(ex, "Upload ou push échoué ({failures}/{max}), nouvelle tentative dans {delay:F0}s",
                failures, MaxConsecutivePushFailures, delay.TotalSeconds);
        }

        if (requestRebuild)
        {
            Logger.LogDebug("Reconstruction des données du joueur demandée suite à des fichiers manquants");
            Mediator.Publish(new ForcePlayerCacheRecreationMessage());
        }
    }

    private void ResetRetryState()
    {
        _consecutivePushFailures = 0;
        _nextRetryUtc = DateTime.MinValue;
    }

    private List<UserData> GetVisibleUsers() => _pairManager.GetVisibleUsers();
}