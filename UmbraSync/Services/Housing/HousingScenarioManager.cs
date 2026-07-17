using MessagePack;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingScenario;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Manager du partage de scénarios NPC custom via housing share.
/// </summary>
public sealed class HousingScenarioManager : IDisposable
{
    private const byte PayloadVersionV1 = 0x01; // legacy : JSON ARR file-drop (obsolète, ignoré à la lecture)
    private const byte PayloadVersionV2 = 0x02; // scène PNJ native UmbraSync

    private readonly ILogger<HousingScenarioManager> _logger;
    private readonly ApiController _apiController;
    private readonly MareMediator _mediator;
    private readonly HousingNpcScenarioService _npcService;
    private readonly MareConfigService _configService;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<HousingScenarioEntryDto> _ownShares = new();
    private Task? _currentTask;
    private CancellationTokenSource? _cleanupDelayCts;
    private Guid? _lastOwnerReminderShareId;

    public HousingScenarioManager(
        ILogger<HousingScenarioManager> logger,
        ApiController apiController,
        MareMediator mediator,
        HousingNpcScenarioService npcService,
        MareConfigService configService)
    {
        _logger = logger;
        _apiController = apiController;
        _mediator = mediator;
        _npcService = npcService;
        _configService = configService;
    }

    public IReadOnlyList<HousingScenarioEntryDto> OwnShares => _ownShares;
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public bool IsApplied { get; private set; }
    public Guid? AppliedShareId { get; private set; }
    public string? AppliedShareOwnerUid { get; private set; }
    public Task PublishAsync(LocationInfo location, HousingNpcScenario scene, string description,
        List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            if (scene.Entries.Count == 0)
            {
                LastError = Localization.Loc.Get("HousingScenario.Error.EmptyScene");
                _logger.LogWarning("Publish refusé : scène vide");
                return;
            }

            string sceneJson = SerializeSceneForShare(scene);

            var plaintextDto = new HousingScenarioPlaintextV2
            {
                SceneJson = sceneJson,
                SceneFormatVersion = 1,
            };

            byte[] mapBytes = MessagePackSerializer.Serialize(plaintextDto);
            byte[] dataBytes = new byte[1 + mapBytes.Length];
            dataBytes[0] = PayloadVersionV2;
            Buffer.BlockCopy(mapBytes, 0, dataBytes, 1, mapBytes.Length);

            // Chiffrement AES-GCM
            var shareId = Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = ShareCryptoHelper.DeriveKey(shareId, salt);
            byte[] cipher = new byte[dataBytes.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, dataBytes, cipher, tag);
            }

            var uploadDto = new HousingScenarioUploadRequestDto
            {
                ShareId = shareId,
                Location = location,
                Description = description,
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells,
            };

            await _apiController.HousingScenarioUpload(uploadDto).ConfigureAwait(false);
            await InternalRefreshAsync().ConfigureAwait(false);

            LastSuccess = Localization.Loc.Get("HousingScenario.Success.Published");
            _logger.LogInformation("Scénario {ShareId} publié pour la location S{Server}:W{Ward}:H{House}",
                shareId, location.ServerId, location.WardId, location.HouseId);

            _mediator.Publish(new HousingScenarioPublishedMessage(shareId, location));
        });
    }

    /// <summary>
    /// Cherche un scénario partagé pour la location courante et l'applique en file-drop dans le dossier ARR.
    /// </summary>
    public Task CheckAndApplyForLocationAsync(LocationInfo location)
    {
        CancelDelayedCleanup();

        return RunOperation(async () =>
        {
            // Désactivation globale : l'utilisateur ne veut aucun scénario partagé.
            if (_configService.Current.DefaultDisableHousingScenarios)
            {
                _logger.LogDebug("Scénarios housing désactivés globalement, apply skip");
                return;
            }

            var shares = await _apiController.HousingScenarioGetForLocation(location).ConfigureAwait(false);

            shares.RemoveAll(s => !LocationMatches(s.Location, location));

            // Jamais d'auto-application : l'owner possède déjà le scénario original en local,
            // re-déposer la copie partagée dans ARR créerait un doublon (deux scénarios listés).
            // On lui rappelle néanmoins qu'un de ses scénarios est publié ici (une fois par visite).
            var ownShare = shares.Find(s => s.IsOwner);
            if (ownShare != null && _lastOwnerReminderShareId != ownShare.Id)
            {
                _lastOwnerReminderShareId = ownShare.Id;
                var reminderBody = string.IsNullOrWhiteSpace(ownShare.Description)
                    ? Localization.Loc.Get("HousingScenario.Notification.OwnerReminder")
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Localization.Loc.Get("HousingScenario.Notification.OwnerReminderWithDescription"), ownShare.Description);
                _mediator.Publish(new NotificationMessage(
                    Localization.Loc.Get("HousingScenario.Notification.Title"),
                    reminderBody,
                    MareConfiguration.Models.NotificationType.Info,
                    TimeSpan.FromSeconds(6)));
            }
            shares.RemoveAll(s => s.IsOwner);

            // Override per-pair : on ignore les shares dont l'owner a été explicitement désactivé.
            var pairOverrides = _configService.Current.PairSyncOverrides;
            shares.RemoveAll(s => !string.IsNullOrEmpty(s.OwnerUid)
                && pairOverrides.TryGetValue(s.OwnerUid, out var ov)
                && ov.DisableHousingScenarios == true);

            if (shares.Count == 0)
            {
                _logger.LogDebug("Aucun scénario partagé pour cette location");
                if (IsApplied)
                {
                    await RemoveAppliedInternalAsync().ConfigureAwait(false);
                }
                return;
            }

            // 1 seul actif par housing : le plus récent (décision produit)
            var share = shares[0];

            if (IsApplied && AppliedShareId == share.Id)
            {
                _logger.LogDebug("Scénario {ShareId} déjà appliqué, skip", share.Id);
                return;
            }

            // Si un autre scénario est appliqué, on le nettoie d'abord
            if (IsApplied)
            {
                await RemoveAppliedInternalAsync().ConfigureAwait(false);
            }

            AppliedShareOwnerUid = share.OwnerUid;
            await ApplyAsync(share, location).ConfigureAwait(false);
        });
    }

    private async Task ApplyAsync(HousingScenarioEntryDto share, LocationInfo location)
    {
        Guid shareId = share.Id;
        var payload = await _apiController.HousingScenarioDownload(shareId).ConfigureAwait(false);
        if (payload == null)
        {
            LastError = Localization.Loc.Get("HousingScenario.Error.Unavailable");
            return;
        }

        // Déchiffrement
        byte[] key = ShareCryptoHelper.DeriveKey(payload.ShareId, payload.Salt);
        byte[] plaintext = new byte[payload.CipherData.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Nonce, payload.CipherData, payload.Tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Déchiffrement scénario {ShareId} échoué", shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.Decrypt");
            return;
        }

        if (plaintext.Length < 2)
        {
            _logger.LogWarning("Payload tronqué pour {ShareId}", shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.Payload");
            return;
        }

        // Les partages v1 (file-drop ARR) ne sont plus supportés : UmbraSync spawne ses PNJ
        // nativement et ne dépend plus d'ARealmRepopulated. L'owner doit republier sa scène.
        if (plaintext[0] == PayloadVersionV1)
        {
            _logger.LogInformation("Partage {ShareId} au format ARR (v1) : obsolète, ignoré", shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.LegacyArr");
            return;
        }

        if (plaintext[0] != PayloadVersionV2)
        {
            _logger.LogWarning("Version de payload inconnue ({Version}) pour {ShareId}", plaintext[0], shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.Payload");
            return;
        }

        HousingNpcScenario? scene;
        try
        {
            var plaintextDto = MessagePackSerializer.Deserialize<HousingScenarioPlaintextV2>(
                new ReadOnlyMemory<byte>(plaintext, 1, plaintext.Length - 1));
            scene = JsonSerializer.Deserialize<HousingNpcScenario>(plaintextDto.SceneJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Désérialisation payload scénario échouée {ShareId}", shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.Deserialize");
            return;
        }

        if (scene == null || scene.Entries.Count == 0)
        {
            _logger.LogWarning("Scène partagée vide pour {ShareId}", shareId);
            LastError = Localization.Loc.Get("HousingScenario.Error.EmptyScene");
            return;
        }

        // Spawn natif : aucun fichier écrit, aucune dépendance à ARR. Le confinement à la room
        // exacte est garanti par le matching serveur (LocationInfo complet, RoomId inclus).
        await _npcService.ApplySharedSceneAsync(scene).ConfigureAwait(false);

        IsApplied = true;
        AppliedShareId = shareId;
        LastSuccess = Localization.Loc.Get("HousingScenario.Success.Applied");
        _logger.LogInformation("Scène partagée {ShareId} appliquée ({Count} PNJ)", shareId, scene.Entries.Count);

        // Informe l'utilisateur (toast/chat selon ses préférences de notification).
        var notifBody = string.IsNullOrWhiteSpace(share.Description)
            ? Localization.Loc.Get("HousingScenario.Notification.Applied")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Localization.Loc.Get("HousingScenario.Notification.AppliedWithDescription"), share.Description);
        _mediator.Publish(new NotificationMessage(
            Localization.Loc.Get("HousingScenario.Notification.Title"),
            notifBody,
            MareConfiguration.Models.NotificationType.Info,
            TimeSpan.FromSeconds(6)));
        _mediator.Publish(new HousingScenarioAppliedMessage(shareId, location, share.OwnerUid));
    }

    /// <summary>
    /// Programme la suppression du scénario appliqué dans 15s, annulable si on re-entre dans un housing.
    /// </summary>
    public void ScheduleDelayedCleanup()
    {
        // Sortie du housing : le rappel owner pourra se ré-afficher à la prochaine visite.
        _lastOwnerReminderShareId = null;

        if (!IsApplied) return;

        CancelDelayedCleanup();
        _cleanupDelayCts = new CancellationTokenSource();
        var token = _cleanupDelayCts.Token;

        _logger.LogInformation("Nettoyage du scénario programmé dans 15 secondes");
        _ = Task.Delay(TimeSpan.FromSeconds(15), token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                await RemoveAppliedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Échec du cleanup différé du scénario");
            }
        }, TaskScheduler.Default);
    }

    private void CancelDelayedCleanup()
    {
        if (_cleanupDelayCts != null)
        {
            _cleanupDelayCts.Cancel();
            _cleanupDelayCts.Dispose();
            _cleanupDelayCts = null;
            _logger.LogDebug("Timer de nettoyage scénario annulé");
        }
    }

    public Task RemoveAppliedAsync()
    {
        return RunOperation(async () =>
        {
            if (!IsApplied) return;
            await RemoveAppliedInternalAsync().ConfigureAwait(false);
        });
    }

    private async Task RemoveAppliedInternalAsync()
    {
        await _npcService.RemoveSharedSceneAsync().ConfigureAwait(false);

        IsApplied = false;
        AppliedShareId = null;
        AppliedShareOwnerUid = null;
        _mediator.Publish(new HousingScenarioRemovedMessage());
    }

    public Task RefreshAsync()
    {
        return RunOperation(InternalRefreshAsync);
    }

    private async Task InternalRefreshAsync()
    {
        var shares = await _apiController.HousingScenarioGetOwn().ConfigureAwait(false);
        _ownShares.Clear();
        _ownShares.AddRange(shares);
    }

    public Task UpdateVisibilityAsync(Guid shareId, string description, List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            var dto = new HousingScenarioUpdateRequestDto
            {
                ShareId = shareId,
                Description = description,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells,
            };

            var updated = await _apiController.HousingScenarioUpdate(dto).ConfigureAwait(false);
            if (updated == null)
            {
                LastError = "Mise à jour scénario refusée.";
                return;
            }

            int idx = _ownShares.FindIndex(s => s.Id == shareId);
            if (idx >= 0) _ownShares[idx] = updated;
            LastSuccess = "Scénario mis à jour.";
        });
    }

    public Task DeleteAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            bool ok = await _apiController.HousingScenarioDelete(shareId).ConfigureAwait(false);
            if (!ok)
            {
                LastError = "Suppression scénario refusée.";
                return;
            }
            _ownShares.RemoveAll(s => s.Id == shareId);
            LastSuccess = "Scénario supprimé.";
        });
    }


    private async Task RunOperation(Func<Task> action)
    {
        await _operationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            LastError = null;
            LastSuccess = null;
            _currentTask = action();
            await _currentTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opération scénario en erreur");
            LastError = "Erreur inattendue lors de l'opération scénario.";
        }
        finally
        {
            _operationSemaphore.Release();
        }
    }
    


    // Égalité stricte de localisation housing
    private static bool LocationMatches(LocationInfo a, LocationInfo b)
    {
        return a.ServerId == b.ServerId
            && a.TerritoryId == b.TerritoryId
            && a.DivisionId == b.DivisionId
            && a.WardId == b.WardId
            && a.HouseId == b.HouseId
            && a.RoomId == b.RoomId;
    }






    /// <summary>
    /// Sérialise la scène pour le partage, en retirant les données « live » (mods) de chaque PNJ :
    /// le visiteur ne possède pas les fichiers de mods, seule l'apparence brute est transmissible.
    /// </summary>
    private static string SerializeSceneForShare(HousingNpcScenario scene)
    {
        var copy = new HousingNpcScenario
        {
            Id = scene.Id,
            Title = scene.Title,
            Enabled = true,
            ServerId = scene.ServerId,
            TerritoryId = scene.TerritoryId,
            WardId = scene.WardId,
            HouseId = scene.HouseId,
            DivisionId = scene.DivisionId,
            RoomId = scene.RoomId,
        };

        foreach (var entry in scene.Entries)
        {
            copy.Entries.Add(new HousingNpcEntry
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Appearance = entry.Appearance,
                LiveData = null, // mods volontairement exclus du partage
                X = entry.X,
                Y = entry.Y,
                Z = entry.Z,
                Rotation = entry.Rotation,
                FacePlayer = entry.FacePlayer,
                Actions = entry.Actions,
                Looping = entry.Looping,
                LoopDelay = entry.LoopDelay,
            });
        }

        return JsonSerializer.Serialize(copy);
    }


    public void Dispose()
    {
        CancelDelayedCleanup();
        _operationSemaphore.Dispose();
    }
}
