using MessagePack;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingScenario;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Manager du partage de scénarios NPC custom via housing share.
/// </summary>
public sealed class HousingScenarioManager : IDisposable
{
    private const byte PayloadVersionV1 = 0x01;
    private const string TempFilePrefix = "UmbraTemp_";

    private readonly ILogger<HousingScenarioManager> _logger;
    private readonly ApiController _apiController;
    private readonly MareMediator _mediator;
    private readonly ArrPathResolver _arrPathResolver;
    private readonly HousingScenarioStateService _stateService;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<HousingScenarioEntryDto> _ownShares = new();
    private Task? _currentTask;
    private CancellationTokenSource? _cleanupDelayCts;

    public HousingScenarioManager(
        ILogger<HousingScenarioManager> logger,
        ApiController apiController,
        MareMediator mediator,
        ArrPathResolver arrPathResolver,
        HousingScenarioStateService stateService)
    {
        _logger = logger;
        _apiController = apiController;
        _mediator = mediator;
        _arrPathResolver = arrPathResolver;
        _stateService = stateService;
    }

    public IReadOnlyList<HousingScenarioEntryDto> OwnShares => _ownShares;
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public bool IsApplied { get; private set; }
    public Guid? AppliedShareId { get; private set; }
    public string? AppliedShareOwnerUid { get; private set; }

    /// <summary>
    /// Publie un scénario ARR local : lit le JSON, chiffre, upload via SignalR.
    /// </summary>
    public Task PublishAsync(LocationInfo location, string scenarioFilePath, string description,
        List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            if (!File.Exists(scenarioFilePath))
            {
                LastError = "Fichier scénario introuvable.";
                _logger.LogWarning("Publish refusé : fichier introuvable {Path}", scenarioFilePath);
                return;
            }

            // Lire le JSON ARR brut
            string scenarioJson;
            try
            {
                scenarioJson = await File.ReadAllTextAsync(scenarioFilePath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastError = "Lecture du fichier scénario échouée.";
                _logger.LogWarning(ex, "Lecture impossible du scénario {Path}", scenarioFilePath);
                return;
            }

            // Détecter la version du format ARR depuis le champ "Version" au top-level du JSON
            int arrFormatVersion = TryDetectArrVersion(scenarioJson);

            // Construire le plaintext
            var plaintextDto = new HousingScenarioPlaintextV1
            {
                ScenarioJson = scenarioJson,
                ArrFormatVersion = arrFormatVersion,
                OriginalFileName = Path.GetFileName(scenarioFilePath),
            };

            byte[] mapBytes = MessagePackSerializer.Serialize(plaintextDto);
            byte[] dataBytes = new byte[1 + mapBytes.Length];
            dataBytes[0] = PayloadVersionV1;
            Buffer.BlockCopy(mapBytes, 0, dataBytes, 1, mapBytes.Length);

            // Chiffrement AES-GCM
            var shareId = Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = DeriveKey(shareId, salt);
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

            LastSuccess = "Scénario publié.";
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
            string? scenariosPath = _arrPathResolver.TryGetScenariosPath();
            if (scenariosPath == null)
            {
                _logger.LogDebug("ARR indisponible, scenario apply skip");
                return;
            }

            var shares = await _apiController.HousingScenarioGetForLocation(location).ConfigureAwait(false);
            if (shares.Count == 0)
            {
                _logger.LogDebug("Aucun scénario partagé pour cette location");
                if (IsApplied)
                {
                    await RemoveAppliedInternalAsync(scenariosPath).ConfigureAwait(false);
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
                await RemoveAppliedInternalAsync(scenariosPath).ConfigureAwait(false);
            }

            AppliedShareOwnerUid = share.OwnerUid;
            await ApplyAsync(share.Id, scenariosPath).ConfigureAwait(false);
        });
    }

    private async Task ApplyAsync(Guid shareId, string scenariosPath)
    {
        var payload = await _apiController.HousingScenarioDownload(shareId).ConfigureAwait(false);
        if (payload == null)
        {
            LastError = "Scénario indisponible (permissions ?).";
            return;
        }

        // Déchiffrement
        byte[] key = DeriveKey(payload.ShareId, payload.Salt);
        byte[] plaintext = new byte[payload.CipherData.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Nonce, payload.CipherData, payload.Tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Déchiffrement scénario {ShareId} échoué", shareId);
            LastError = "Déchiffrement scénario échoué.";
            return;
        }

        if (plaintext.Length < 2 || plaintext[0] != PayloadVersionV1)
        {
            _logger.LogWarning("Version de payload inconnue ou tronquée pour {ShareId}", shareId);
            LastError = "Format de payload inattendu.";
            return;
        }

        // Désérialiser
        HousingScenarioPlaintextV1 plaintextDto;
        try
        {
            plaintextDto = MessagePackSerializer.Deserialize<HousingScenarioPlaintextV1>(
                new ReadOnlyMemory<byte>(plaintext, 1, plaintext.Length - 1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Désérialisation payload scénario échouée {ShareId}", shareId);
            LastError = "Désérialisation scénario échouée.";
            return;
        }

        // File-drop : <scenariosPath>/UmbraTemp_{shareId:N}.json
        string tempFileName = $"{TempFilePrefix}{shareId:N}.json";
        string tempPath = Path.Combine(scenariosPath, tempFileName);
        try
        {
            await File.WriteAllTextAsync(tempPath, plaintextDto.ScenarioJson, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Écriture du temp file scénario échouée : {Path}", tempPath);
            LastError = "Écriture du fichier scénario temporaire échouée.";
            return;
        }

        // Persiste l'état AVANT toute action visible : si crash après ce point, le startup nettoie
        _stateService.Save(new HousingScenarioStateSnapshot
        {
            ActiveTempFile = tempFileName,
            AppliedShareId = shareId,
            AppliedAtUtc = DateTime.UtcNow,
        });

        IsApplied = true;
        AppliedShareId = shareId;
        LastSuccess = "Scénario appliqué.";
        _logger.LogInformation("Scénario {ShareId} appliqué (fichier {File})", shareId, tempFileName);

        // Le FSWatcher d'ARR va le pickup automatiquement via AutoLoadScenarios
    }

    /// <summary>
    /// Programme la suppression du scénario appliqué dans 15s, annulable si on re-entre dans un housing.
    /// </summary>
    public void ScheduleDelayedCleanup()
    {
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
            string? scenariosPath = _arrPathResolver.TryGetScenariosPath();
            if (scenariosPath == null) return;
            await RemoveAppliedInternalAsync(scenariosPath).ConfigureAwait(false);
        });
    }

    private Task RemoveAppliedInternalAsync(string scenariosPath)
    {
        try
        {
            if (AppliedShareId.HasValue)
            {
                string tempFileName = $"{TempFilePrefix}{AppliedShareId.Value:N}.json";
                string tempPath = Path.Combine(scenariosPath, tempFileName);
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    _logger.LogInformation("Temp scénario supprimé : {File}", tempFileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de la suppression du temp file scénario");
        }

        _stateService.Clear();

        IsApplied = false;
        AppliedShareId = null;
        AppliedShareOwnerUid = null;
        _mediator.Publish(new HousingScenarioRemovedMessage());

        return Task.CompletedTask;
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

    /// <summary>
    /// Restaure un état cohérent au démarrage du plugin :
    /// supprime un éventuel temp file orphelin et purge le state file.
    /// </summary>
    public void CleanupStaleState()
    {
        var snapshot = _stateService.Load();
        if (snapshot == null) return;

        try
        {
            string? scenariosPath = _arrPathResolver.TryGetScenariosPath();
            if (scenariosPath != null && !string.IsNullOrEmpty(snapshot.ActiveTempFile))
            {
                // Sécurité : on supprime UNIQUEMENT un fichier qui matche notre préfixe.
                if (snapshot.ActiveTempFile.StartsWith(TempFilePrefix, StringComparison.Ordinal)
                    && !snapshot.ActiveTempFile.Contains('/')
                    && !snapshot.ActiveTempFile.Contains('\\')
                    && !snapshot.ActiveTempFile.Contains(".."))
                {
                    string staleFile = Path.Combine(scenariosPath, snapshot.ActiveTempFile);
                    if (File.Exists(staleFile))
                    {
                        File.Delete(staleFile);
                        _logger.LogInformation("Temp scénario orphelin supprimé au startup : {File}", snapshot.ActiveTempFile);
                    }
                }
                else
                {
                    _logger.LogWarning("Nom de temp file scénario suspect, ignoré au cleanup : {File}", snapshot.ActiveTempFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup stale state scénario partiel");
        }
        finally
        {
            _stateService.Clear();
        }
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

    private static int TryDetectArrVersion(string scenarioJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (doc.RootElement.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.Number)
            {
                return v.GetInt32();
            }
        }
        catch
        {
            // ignored — la version n'est pas critique pour la transmission
        }
        return -1;
    }

    private static byte[] DeriveKey(Guid shareId, byte[] salt)
    {
        byte[] shareBytes = shareId.ToByteArray();
        byte[] material = new byte[shareBytes.Length + salt.Length];
        Buffer.BlockCopy(shareBytes, 0, material, 0, shareBytes.Length);
        Buffer.BlockCopy(salt, 0, material, shareBytes.Length, salt.Length);
        return SHA256.HashData(material);
    }

    public void Dispose()
    {
        CancelDelayedCleanup();
        _operationSemaphore.Dispose();
    }
}
