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
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData;
    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private readonly HashSet<UserData> _usersToPushDataTo = new(UserDataComparer.Instance);
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private Task<CharacterData>? _fileUploadTask;
    private readonly CancellationTokenSource _runtimeCts = new();

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
            if (_lastCreatedData == null || !string.Equals(newData.DataHash.Value, _lastCreatedData.DataHash.Value, StringComparison.Ordinal))
            {
                _lastCreatedData = newData;
                Logger.LogTrace("Nouveau hash de données stocké: {hash}", newData.DataHash.Value);
                PushToAllVisibleUsers(forced: true);
            }
            else
            {
                Logger.LogTrace("Hash identique au précédent: {hash}", newData.DataHash.Value);
            }
        });

        Mediator.Subscribe<PairOnlineMessage>(this, (msg) =>
        {
            if (_lastCreatedData == null || !_apiController.IsConnected) return;
            _usersToPushDataTo.Add(msg.User);
            PushCharacterData(forced: true);
        });
        Mediator.Subscribe<ConnectedMessage>(this, (_) => PushToAllVisibleUsers());
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _fileTransferManager.CancelUpload();
            _previouslyVisiblePlayers.Clear();
            _usersToPushDataTo.Clear();
            _uploadingCharacterData = null;
            _fileUploadTask = null;
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
            _pushLock.Dispose();
        }
        base.Dispose(disposing);
    }

    private void PushToAllVisibleUsers(bool forced = false)
    {
        foreach (var user in GetVisibleUsers())
        {
            _usersToPushDataTo.Add(user);
        }

        if (_usersToPushDataTo.Count > 0)
        {
            Logger.LogDebug("Push programmé pour {count} joueurs visibles (hash: {hash})",
                _usersToPushDataTo.Count, _lastCreatedData?.DataHash.Value ?? "UNKNOWN");
            PushCharacterData(forced);
        }
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected) return;

        var allVisibleUsers = GetVisibleUsers();
        var newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers, UserDataComparer.Instance).ToList();

        _previouslyVisiblePlayers.Clear();
        _previouslyVisiblePlayers.AddRange(allVisibleUsers);

        if (newVisibleUsers.Count == 0) return;

        Logger.LogDebug("Nouveaux joueurs visibles détectés: {users}",
            string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));

        foreach (var user in newVisibleUsers)
        {
            _usersToPushDataTo.Add(user);
        }
        PushCharacterData();
    }

    private void PushCharacterData(bool forced = false)
    {
        if (_lastCreatedData == null || _usersToPushDataTo.Count == 0) return;
        _ = PushCharacterDataAsync(forced);
    }

    private async Task PushCharacterDataAsync(bool forced = false)
    {
        await _pushLock.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);
        try
        {
            if (_lastCreatedData == null || _usersToPushDataTo.Count == 0)
                return;

            var hashChanged = !string.Equals(_uploadingCharacterData?.DataHash.Value, _lastCreatedData.DataHash.Value, StringComparison.Ordinal);
            forced |= hashChanged;

            if (_fileUploadTask == null || _fileUploadTask.IsCompleted || forced)
            {
                _uploadingCharacterData = _lastCreatedData.DeepClone();
                var uploadTargets = _usersToPushDataTo.ToList();

                Logger.LogDebug("Démarrage upload (hash: {hash}). Raison: TaskNull={taskNull}, TaskCompleted={taskCpl}, Forced={forced}",
                    _lastCreatedData.DataHash.Value,
                    _fileUploadTask == null,
                    _fileUploadTask?.IsCompleted ?? false,
                    forced);

                _fileUploadTask = _fileTransferManager.UploadFiles(_uploadingCharacterData, uploadTargets);
            }

            var dataToSend = await _fileUploadTask.ConfigureAwait(false);

            var users = _usersToPushDataTo.ToList();
            if (users.Count == 0)
                return;

            // Séparer les utilisateurs par collection override
            if (_collectionOverrideResolver.HasAnyCollectionOverride())
            {
                var (defaultUsers, overrideUsers) = _collectionOverrideResolver.SplitUsersByCollection(users);

                // Envoi par défaut (collection courante)
                if (defaultUsers.Count > 0)
                {
                    Logger.LogDebug("Push de {hash} (collection par défaut) vers {users}",
                        dataToSend.DataHash.Value,
                        string.Join(", ", defaultUsers.Select(k => k.AliasOrUID)));
                    await _apiController.PushCharacterData(dataToSend, defaultUsers).ConfigureAwait(false);
                }

                // Envoi par collection override
                foreach (var (collectionId, collectionUsers) in overrideUsers)
                {
                    try
                    {
                        var alternativeData = await _collectionOverrideResolver.BuildAlternativeCharacterData(
                            dataToSend, collectionId).ConfigureAwait(false);

                        if (alternativeData != null)
                        {
                            Logger.LogDebug("Push de collection override {collId} vers {users}",
                                collectionId,
                                string.Join(", ", collectionUsers.Select(k => k.AliasOrUID)));
                            await _apiController.PushCharacterData(alternativeData, collectionUsers).ConfigureAwait(false);
                        }
                        else
                        {
                            // Fallback: envoyer les données par défaut
                            Logger.LogWarning("Fallback sur collection par défaut pour {collId}", collectionId);
                            await _apiController.PushCharacterData(dataToSend, collectionUsers).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Erreur lors du push avec collection override {collId}, fallback sur défaut", collectionId);
                        await _apiController.PushCharacterData(dataToSend, collectionUsers).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // Pas d'override: comportement standard
                Logger.LogDebug("Push de {hash} vers {users}",
                    dataToSend.DataHash.Value,
                    string.Join(", ", users.Select(k => k.AliasOrUID)));
                await _apiController.PushCharacterData(dataToSend, users).ConfigureAwait(false);
            }

            _usersToPushDataTo.Clear();
        }
        finally
        {
            _pushLock.Release();
        }
    }

    private List<UserData> GetVisibleUsers() => _pairManager.GetVisibleUsers();
}