using Dalamud.Plugin.Services;
using MessagePack;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingShare;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Factories;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.Files;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services.Housing;

public sealed class HousingShareManager : IDisposable
{
    internal const string HousingModPrefix = "UmbraHousing_";

    /// Octet de version pour le nouveau format avec transfert de fichiers.
    private const byte PayloadVersionFileTransfer = 1;

    private readonly ILogger<HousingShareManager> _logger;
    private readonly ApiController _apiController;
    private readonly HousingFurnitureScanner _scanner;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly MareMediator _mediator;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ICommandManager _commandManager;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly MareConfigService _configService;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<HousingShareEntryDto> _ownShares = new();
    private FileDownloadManager? _fileDownloadManager;
    private string? _installedModDirName;
    private Task? _currentTask;
    private CancellationTokenSource? _cleanupDelayCts;

    public HousingShareManager(ILogger<HousingShareManager> logger, ApiController apiController,
        HousingFurnitureScanner scanner, IpcCallerPenumbra penumbra, MareMediator mediator,
        DalamudUtilService dalamudUtil, ICommandManager commandManager,
        FileCacheManager fileCacheManager, FileUploadManager fileUploadManager,
        FileDownloadManagerFactory fileDownloadManagerFactory, MareConfigService configService)
    {
        _logger = logger;
        _apiController = apiController;
        _scanner = scanner;
        _penumbra = penumbra;
        _mediator = mediator;
        _dalamudUtil = dalamudUtil;
        _commandManager = commandManager;
        _fileCacheManager = fileCacheManager;
        _fileUploadManager = fileUploadManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _configService = configService;
    }

    public IReadOnlyList<HousingShareEntryDto> OwnShares => _ownShares;
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public bool IsApplied { get; private set; }
    public Guid? AppliedShareId { get; private set; }
    public string? AppliedShareOwnerUid { get; private set; }
    public string? ProgressStatus { get; private set; }
    public float ProgressPercent { get; private set; }

    public Task PublishAsync(LocationInfo location, string description, List<string> allowedIndividuals, List<string> allowedSyncshells, bool disableSourceMods = false)
    {
        return RunOperation(async () =>
        {
            if (!_dalamudUtil.IsInHousingMode)
            {
                LastError = Loc.Get("HousingShare.Error.NotInHousingMode");
                return;
            }

            var modPaths = _scanner.GetCollectedPaths();
            if (modPaths.Count == 0)
            {
                LastError = Loc.Get("HousingShare.Error.NoModsDetected");
                return;
            }

            // Hachage des fichiers locaux
            ProgressStatus = Loc.Get("HousingShare.Progress.Hashing");
            ProgressPercent = 0.10f;
            var resolvedPaths = modPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var fileCaches = _fileCacheManager.GetFileCachesByPaths(resolvedPaths);

            // Construire gamePath → hash
            var hashPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashList = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (gamePath, resolvedPath) in modPaths)
            {
                if (fileCaches.TryGetValue(resolvedPath, out var cacheEntity) && cacheEntity != null)
                {
                    hashPaths[gamePath] = cacheEntity.Hash;
                    hashList.Add(cacheEntity.Hash);
                }
                else
                {
                    _logger.LogWarning("Impossible de hacher le fichier {Path} pour le gamePath {GamePath}, ignoré", resolvedPath, gamePath);
                }
            }

            if (hashPaths.Count == 0)
            {
                LastError = Loc.Get("HousingShare.Error.HashingFailed");
                return;
            }

            // Upload des fichiers sur le serveur
            ProgressStatus = Loc.Get("HousingShare.Progress.Uploading");
            ProgressPercent = 0.30f;
            var uploadProgress = new Progress<string>(status => ProgressStatus = status);
            var missingLocally = await _fileUploadManager.UploadFiles(hashList.ToList(), uploadProgress).ConfigureAwait(false);
            if (missingLocally.Count > 0)
            {
                _logger.LogWarning("{Count} fichiers manquants localement lors de l'upload housing", missingLocally.Count);
            }

            // Sérialiser avec préfixe de version v1
            ProgressStatus = Loc.Get("HousingShare.Progress.Applying");
            ProgressPercent = 0.80f;
            var mapBytes = MessagePackSerializer.Serialize(hashPaths);
            var dataBytes = new byte[1 + mapBytes.Length];
            dataBytes[0] = PayloadVersionFileTransfer;
            Buffer.BlockCopy(mapBytes, 0, dataBytes, 1, mapBytes.Length);

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

            var uploadDto = new HousingShareUploadRequestDto
            {
                ShareId = shareId,
                Location = location,
                Description = description,
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells
            };

            await _apiController.HousingShareUpload(uploadDto).ConfigureAwait(false);
            await InternalRefreshAsync().ConfigureAwait(false);
            var furnitureCount = _scanner.CollectedFurnitureCount;

            // Arrêter le scan après la publication
            _scanner.StopScan();

            LastSuccess = string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Published"), furnitureCount);
            _logger.LogInformation("Housing share {ShareId} uploaded with {Count} furniture items ({Files} files, {Hashes} unique hashes)",
                shareId, furnitureCount, hashPaths.Count, hashList.Count);

            // Désactivation des mods sources si demandé (le mod reste sur disque mais est désactivé dans Penumbra)
            if (disableSourceMods)
            {
                var sourceMods = _scanner.GetSourceHousingModDirectories();
                if (sourceMods.Count > 0)
                {
                    var defaultCollId = await _penumbra.GetDefaultCollectionIdAsync().ConfigureAwait(false);
                    if (defaultCollId != null && defaultCollId.Value != Guid.Empty)
                    {
                        var disabledMods = new List<string>();
                        foreach (var modDirName in sourceMods)
                        {
                            var result = await _penumbra.TrySetModEnabledAsync(_logger, defaultCollId.Value, modDirName, false).ConfigureAwait(false);
                            _logger.LogInformation("Désactivation du mod source housing {ModDir} : {Result}", modDirName, result);
                            disabledMods.Add(modDirName);
                        }
                        if (disabledMods.Count > 0)
                        {
                            LastSuccess += " " + string.Format(CultureInfo.CurrentCulture,
                                Loc.Get("HousingShare.SourceMod.Disabled"), string.Join(", ", disabledMods));
                        }
                    }
                }
            }

            _mediator.Publish(new NotificationMessage(
                Loc.Get("HousingShare.Notification.ShareTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Published"), furnitureCount),
                NotificationType.Success,
                TimeSpan.FromSeconds(4)));
        });
    }

    public Task UpdateVisibilityAsync(Guid shareId, string description, List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            var dto = new HousingShareUpdateRequestDto
            {
                ShareId = shareId,
                Description = description,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells
            };

            var updated = await _apiController.HousingShareUpdate(dto).ConfigureAwait(false);
            if (updated == null)
            {
                LastError = Loc.Get("HousingShare.Error.UpdateFailed");
                return;
            }

            var idx = _ownShares.FindIndex(s => s.Id == shareId);
            if (idx >= 0) _ownShares[idx] = updated;

            LastSuccess = Loc.Get("HousingShare.Success.Updated");
            _logger.LogInformation("Housing share {ShareId} visibility updated", shareId);
        });
    }

    // Vérifie si un share housing existe pour cette localisation et l'applique le cas échéant.
    // Englobe toute la logique (recherche API + skip + download + apply + nettoyage).
    public Task CheckAndApplyForLocationAsync(LocationInfo location)
    {
        // Annuler le timer de nettoyage différé si on re-entre dans un housing
        CancelDelayedCleanup();

        return RunOperation(async () =>
        {
            ProgressStatus = Loc.Get("HousingShare.Progress.Searching");
            ProgressPercent = 0.02f;

            var shares = await _apiController.HousingShareGetForLocation(location).ConfigureAwait(false);
            if (shares.Count == 0)
            {
                _logger.LogDebug("Aucun share housing trouvé pour cette localisation");

                // Supprimer le mod de la maison précédente pour éviter que ses textures
                // ne s'appliquent aux meubles vanilla de cette maison (même game paths).
                if (IsApplied)
                {
                    _logger.LogInformation("Suppression du mod housing précédent (maison sans share)");
                    await RemoveInstalledModAsync().ConfigureAwait(false);
                    IsApplied = false;
                    AppliedShareId = null;
                    AppliedShareOwnerUid = null;
                    
                    try
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        await _dalamudUtil.RunOnFrameworkThread(() =>
                        {
                            _commandManager.ProcessCommand("/penumbra redraw furniture");
                        }).ConfigureAwait(false);
                        _logger.LogInformation("Penumbra redraw furniture exécuté après suppression du mod housing précédent");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Échec du redraw furniture après suppression du mod housing");
                    }
                }
                return;
            }

            var share = shares[0];
            _logger.LogInformation("Share housing {ShareId} trouvé de {Owner}", share.Id, share.OwnerUid);

            // Vérifier si housing est désactivé pour ce pair
            var pairOverride = _configService.Current.PairSyncOverrides.TryGetValue(share.OwnerUid, out var overrideEntry) ? overrideEntry : null;
            bool housingDisabledForPair = pairOverride?.DisableHousingMods ?? _configService.Current.DefaultDisableHousingMods;
            if (housingDisabledForPair)
            {
                _logger.LogInformation("Housing désactivé pour le pair {Owner}, skip", share.OwnerUid);
                return;
            }

            // Si le même share est déjà appliqué, ne pas re-appliquer
            if (IsApplied && AppliedShareId == share.Id)
            {
                _logger.LogInformation("Housing share {ShareId} déjà appliqué, skip", share.Id);
                return;
            }

            // Stocker l'UID du propriétaire pour le check per-pair
            AppliedShareOwnerUid = share.OwnerUid;

            // Déléguer au flow de téléchargement/application
            await InternalDownloadAndApplyAsync(share.Id).ConfigureAwait(false);
        });
    }

    public Task DownloadAndApplyAsync(Guid shareId)
    {
        return RunOperation(() => InternalDownloadAndApplyAsync(shareId));
    }

    // Logique interne de téléchargement et application.
    private async Task InternalDownloadAndApplyAsync(Guid shareId)
    {
        ProgressStatus = Loc.Get("HousingShare.Processing");
        ProgressPercent = 0.05f;

        var payload = await _apiController.HousingShareDownload(shareId).ConfigureAwait(false);
        if (payload == null)
        {
            LastError = Loc.Get("HousingShare.Error.Unavailable");
            return;
        }

        ProgressPercent = 0.10f;

        byte[] key = DeriveKey(payload.ShareId, payload.Salt);
        byte[] plaintext = new byte[payload.CipherData.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Nonce, payload.CipherData, payload.Tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt housing share {ShareId}", shareId);
            LastError = Loc.Get("HousingShare.Error.DecryptFailed");
            return;
        }

        Dictionary<string, string> modPaths;

        // Détection de la version du payload
        if (plaintext.Length > 0 && plaintext[0] < 0x80)
        {
            var version = plaintext[0];
            if (version == PayloadVersionFileTransfer)
            {
                modPaths = await ResolveFileTransferPayload(plaintext, shareId).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Version de payload housing inconnue : {Version}", version);
                LastError = Loc.Get("HousingShare.Error.EmptyShare");
                return;
            }
        }
        else
        {
            // Ancien format (MessagePack brut) : gamePath → filePath local
            modPaths = MessagePackSerializer.Deserialize<Dictionary<string, string>>(plaintext);
        }

        if (modPaths.Count == 0)
        {
            LastError = Loc.Get("HousingShare.Error.EmptyShare");
            return;
        }

        ProgressStatus = Loc.Get("HousingShare.Progress.CreatingMod");
        ProgressPercent = 0.60f;

        // Supprimer l'ancien mod s'il existe
        await RemoveInstalledModAsync().ConfigureAwait(false);

        // Créer le mod Penumbra sur disque
        var modDirName = $"{HousingModPrefix}{shareId:N}";
        var created = await CreatePenumbraModAsync(shareId, modPaths).ConfigureAwait(false);
        if (!created)
        {
            LastError = Loc.Get("HousingShare.Error.EmptyShare");
            return;
        }

        ProgressStatus = Loc.Get("HousingShare.Progress.RegisteringMod");
        ProgressPercent = 0.85f;

        // Enregistrer le mod auprès de Penumbra et forcer un rechargement
        var addResult = await _penumbra.AddModAsync(_logger, modDirName).ConfigureAwait(false);
        var reloadResult = await _penumbra.ReloadModAsync(_logger, modDirName).ConfigureAwait(false);
        _logger.LogDebug("AddMod({ModDir}) = {AddResult}, ReloadMod = {ReloadResult}", modDirName, addResult, reloadResult);

        // Obtenir la Default collection pour activer le mod
        var defaultCollId = await _penumbra.GetDefaultCollectionIdAsync().ConfigureAwait(false);
        if (defaultCollId == null || defaultCollId.Value == Guid.Empty)
        {
            _logger.LogWarning("Penumbra Default collection non disponible");
            LastError = Loc.Get("HousingShare.Error.EmptyShare");
            return;
        }

        // Activer le mod dans la Default collection avec priorité haute
        var enableResult = await _penumbra.TrySetModEnabledAsync(_logger, defaultCollId.Value, modDirName, true).ConfigureAwait(false);
        var priorityResult = await _penumbra.TrySetModPriorityAsync(_logger, defaultCollId.Value, modDirName, 100).ConfigureAwait(false);
        _logger.LogDebug("TrySetModEnabled = {EnableResult}, TrySetModPriority = {PriorityResult}", enableResult, priorityResult);

        ProgressPercent = 0.92f;

        _installedModDirName = modDirName;
        IsApplied = true;
        AppliedShareId = shareId;
        var appliedFurnitureCount = CountDistinctFurniture(modPaths.Keys);
        LastSuccess = string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Applied"), appliedFurnitureCount);
        _logger.LogInformation("Housing share {ShareId} applied via Penumbra mod {ModDir} ({Count} furniture, {Files} files)",
            shareId, modDirName, appliedFurnitureCount, modPaths.Count);

        _mediator.Publish(new HousingModsAppliedMessage(new LocationInfo()));

        // Forcer le rechargement des meubles via Penumbra (évite de sortir/re-entrer)
        ProgressStatus = Loc.Get("HousingShare.Progress.RedrawFurniture");
        ProgressPercent = 0.95f;
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                _commandManager.ProcessCommand("/penumbra redraw furniture");
            }).ConfigureAwait(false);
            _logger.LogInformation("Penumbra redraw furniture exécuté après installation du mod housing");

            ProgressPercent = 1.0f;

            _mediator.Publish(new NotificationMessage(
                Loc.Get("HousingShare.Notification.ShareTitle"),
                Loc.Get("HousingShare.Notification.FurnitureApplied"),
                NotificationType.Success,
                TimeSpan.FromSeconds(6)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du redraw furniture, l'utilisateur devra re-entrer dans la maison");
            _mediator.Publish(new NotificationMessage(
                Loc.Get("HousingShare.Notification.ShareTitle"),
                Loc.Get("HousingShare.Notification.ReenterForEffect"),
                NotificationType.Info,
                TimeSpan.FromSeconds(6)));
        }
    }

    public Task RemoveAppliedModsAsync()
    {
        return RunOperation(async () =>
        {
            if (!IsApplied) return;

            await RemoveInstalledModAsync().ConfigureAwait(false);

            IsApplied = false;
            AppliedShareId = null;
            AppliedShareOwnerUid = null;
            LastSuccess = Loc.Get("HousingShare.Success.Removed");
            _logger.LogInformation("Mod housing Penumbra supprimé");

            _mediator.Publish(new HousingModsRemovedMessage());
        });
    }

    public Task RefreshAsync()
    {
        return RunOperation(InternalRefreshAsync);
    }

    public Task DeleteAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            var result = await _apiController.HousingShareDelete(shareId).ConfigureAwait(false);
            if (!result)
            {
                LastError = Loc.Get("HousingShare.Error.DeleteRefused");
                return;
            }

            _ownShares.RemoveAll(s => s.Id == shareId);
            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("HousingShare.Success.Deleted");
        });
    }

    // Lance un timer de 15 secondes pour supprimer le mod housing après avoir quitté une maison.
    // Annulé automatiquement si on entre dans un autre housing avant la fin.
    public void ScheduleDelayedCleanup()
    {
        if (!IsApplied) return;

        CancelDelayedCleanup();
        _cleanupDelayCts = new CancellationTokenSource();
        var token = _cleanupDelayCts.Token;

        _logger.LogInformation("Nettoyage du mod housing programmé dans 15 secondes");
        _ = Task.Delay(TimeSpan.FromSeconds(15), token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            _logger.LogInformation("Timer de nettoyage housing expiré, suppression du mod");
            await RemoveAppliedModsAsync().ConfigureAwait(false);
        }, TaskScheduler.Default);
    }

    private void CancelDelayedCleanup()
    {
        if (_cleanupDelayCts != null)
        {
            _cleanupDelayCts.Cancel();
            _cleanupDelayCts.Dispose();
            _cleanupDelayCts = null;
            _logger.LogDebug("Timer de nettoyage housing annulé");
        }
    }

    public void Dispose()
    {
        CancelDelayedCleanup();
        _fileDownloadManager?.Dispose();
        CleanupStaleMods();
    }

    // Nettoie les mods housing orphelins (sessions précédentes, crash).
    public void CleanupStaleMods()
    {
        try
        {
            var modRoot = _penumbra.GetModDirectoryRaw();
            if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot)) return;

            foreach (var dir in Directory.GetDirectories(modRoot, $"{HousingModPrefix}*"))
            {
                var dirName = Path.GetFileName(dir);
                // Ne pas supprimer le mod actuellement installé
                if (string.Equals(dirName, _installedModDirName, StringComparison.Ordinal)) continue;

                _logger.LogInformation("Nettoyage du mod housing orphelin : {Dir}", dirName);
                try
                {
                    // Désenregistrer de Penumbra (ignoré si déjà absent)
                    _ = _penumbra.DeleteModAsync(_logger, dirName).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Impossible de désenregistrer le mod orphelin {Dir} de Penumbra", dirName);
                }

                try
                {
                    // Penumbra.DeleteMod peut déjà supprimer le répertoire, vérifier avant
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Impossible de supprimer le répertoire orphelin {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors du nettoyage des mods housing orphelins");
        }
    }

    private async Task<Dictionary<string, string>> ResolveFileTransferPayload(byte[] plaintext, Guid shareId)
    {
        // Désérialiser sans le premier octet (version)
        var hashMap = MessagePackSerializer.Deserialize<Dictionary<string, string>>(
            new ReadOnlyMemory<byte>(plaintext, 1, plaintext.Length - 1));

        if (hashMap == null || hashMap.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var modPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingFiles = new List<FileReplacementData>();

        // Résoudre les hashes via le cache local
        ProgressPercent = 0.15f;
        int cacheHits = 0, cacheMisses = 0, fileNotFound = 0;
        foreach (var (gamePath, hash) in hashMap)
        {
            var cached = _fileCacheManager.GetFileCacheByHash(hash);
            if (cached != null)
            {
                cacheHits++;
                var resolvedPath = cached.ResolvedFilepath;
                if (!File.Exists(resolvedPath))
                {
                    fileNotFound++;
                    _logger.LogWarning("Fichier cache introuvable sur disque : {Hash} → {Path}", hash, resolvedPath);
                }
                modPaths[gamePath] = resolvedPath;
            }
            else
            {
                // Regrouper par hash pour éviter les doublons
                var existing = missingFiles.Find(f => string.Equals(f.Hash, hash, StringComparison.Ordinal));
                if (existing == null)
                {
                    missingFiles.Add(new FileReplacementData
                    {
                        Hash = hash,
                        GamePaths = [gamePath]
                    });
                }
                else
                {
                    existing.GamePaths = existing.GamePaths.Concat([gamePath]).ToArray();
                }
                cacheMisses++;
            }
        }

        _logger.LogInformation("[HousingResolve] Résolution : {Hits} en cache, {Misses} manquants, {NotFound} fichiers introuvables sur disque",
            cacheHits, cacheMisses, fileNotFound);

        // Télécharger les fichiers manquants
        if (missingFiles.Count > 0)
        {
            ProgressStatus = string.Format(CultureInfo.CurrentCulture,
                Loc.Get("HousingShare.Progress.Downloading"), 0, missingFiles.Count);
            ProgressPercent = 0.20f;

            _fileDownloadManager ??= _fileDownloadManagerFactory.Create();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var downloadId = $"Housing_{shareId:N}";

            await _fileDownloadManager.InitiateDownloadList(downloadId, missingFiles, cts.Token).ConfigureAwait(false);
            await _fileDownloadManager.DownloadFiles(downloadId, missingFiles, cts.Token).ConfigureAwait(false);
            ProgressPercent = 0.55f;

            // Vérifier que les fichiers sont maintenant dans le cache
            foreach (var file in missingFiles.SelectMany(m => m.GamePaths, (entry, gp) => (entry.Hash, GamePath: gp)))
            {
                var localFile = _fileCacheManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath;
                if (localFile != null)
                {
                    modPaths[file.GamePath] = localFile;
                }
                else
                {
                    _logger.LogWarning("Fichier {Hash} toujours manquant après téléchargement pour {GamePath}", file.Hash, file.GamePath);
                }
            }
        }

        return modPaths;
    }

    // Crée un mod Penumbra sur disque avec la structure de fichiers miroir.
    private async Task<bool> CreatePenumbraModAsync(Guid shareId, Dictionary<string, string> modPaths)
    {
        var modRoot = _penumbra.GetModDirectoryRaw();
        if (string.IsNullOrEmpty(modRoot))
        {
            _logger.LogWarning("Impossible d'obtenir le répertoire de mods Penumbra");
            return false;
        }

        var modDirName = $"{HousingModPrefix}{shareId:N}";
        var modDir = Path.Combine(modRoot, modDirName);

        // Re-création propre si le répertoire existe déjà
        if (Directory.Exists(modDir))
        {
            try { Directory.Delete(modDir, recursive: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossible de supprimer l'ancien répertoire {Dir}", modDir);
            }
        }

        Directory.CreateDirectory(modDir);

        // Écrire meta.json
        var meta = new
        {
            FileVersion = 3,
            Name = $"UmbraSync Housing - {shareId}",
            Author = "UmbraSync",
            Description = "Mod de meubles housing partagé via UmbraSync.",
            Version = "1.0.0",
            Website = "",
            ModTags = new[] { "UmbraSync", "Housing" }
        };
        var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(modDir, "meta.json"), metaJson).ConfigureAwait(false);

        // Créer les fichiers dans la structure miroir et construire le mapping pour default_mod.json
        var filesMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        int copies = 0;
        int totalFiles = modPaths.Count;

        foreach (var (gamePath, localFilePath) in modPaths)
        {
            if (!File.Exists(localFilePath))
            {
                _logger.LogWarning("Fichier source introuvable pour {GamePath}: {Path}", gamePath, localFilePath);
                continue;
            }

            var targetPath = Path.Combine(modDir, gamePath.Replace('/', Path.DirectorySeparatorChar));
            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir != null) Directory.CreateDirectory(targetDir);

            try
            {
                // Copie directe du fichier (les symlinks ne fonctionnent pas sous Wine/XIV on Mac)
                File.Copy(localFilePath, targetPath, overwrite: true);
                copies++;
                // Progression de 0.60 à 0.85 pendant la copie des fichiers
                ProgressPercent = 0.60f + 0.25f * copies / totalFiles;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossible de copier {Source} vers {Target}", localFilePath, targetPath);
                continue;
            }

            // Clés et valeurs identiques : le fichier est placé dans le mod avec la même structure que le game path
            filesMapping[gamePath] = gamePath;
        }

        _logger.LogInformation("Mod housing créé : {Dir} ({Copies} fichiers copiés, {Total} dans le mapping)",
            modDirName, copies, filesMapping.Count);

        // Écrire default_mod.json
        var defaultMod = new
        {
            Files = filesMapping,
            FileSwaps = new Dictionary<string, string>(StringComparer.Ordinal),
            Manipulations = Array.Empty<object>()
        };
        var defaultModJson = JsonSerializer.Serialize(defaultMod, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(modDir, "default_mod.json"), defaultModJson).ConfigureAwait(false);

        return filesMapping.Count > 0;
    }

    // Supprime le mod housing installé de Penumbra et du disque.
    private async Task RemoveInstalledModAsync()
    {
        if (_installedModDirName == null) return;

        var modRoot = _penumbra.GetModDirectoryRaw();

        // Désenregistrer de Penumbra
        try
        {
            await _penumbra.DeleteModAsync(_logger, _installedModDirName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors de la suppression du mod {Dir} de Penumbra", _installedModDirName);
        }

        // Supprimer le répertoire sur disque
        if (!string.IsNullOrEmpty(modRoot))
        {
            var modDir = Path.Combine(modRoot, _installedModDirName);
            if (Directory.Exists(modDir))
            {
                try
                {
                    Directory.Delete(modDir, recursive: true);
                    _logger.LogDebug("Répertoire mod supprimé : {Dir}", modDir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Impossible de supprimer le répertoire {Dir}", modDir);
                }
            }
        }

        _installedModDirName = null;
    }

    private async Task InternalRefreshAsync()
    {
        var own = await _apiController.HousingShareGetOwn().ConfigureAwait(false);
        _ownShares.Clear();
        _ownShares.AddRange(own);
        LastSuccess = Loc.Get("HousingShare.Success.Refreshed");
    }

    private Task RunOperation(Func<Task> operation)
    {
        async Task Wrapper()
        {
            await _operationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                LastError = null;
                LastSuccess = null;
                ProgressStatus = null;
                ProgressPercent = 0f;
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during housing share operation");
                LastError = ex.Message;
            }
            finally
            {
                ProgressStatus = null;
                ProgressPercent = 0f;
                _operationSemaphore.Release();
            }
        }

        var task = Wrapper();
        _currentTask = task;
        return task;
    }

    // Compte le nombre de meubles distincts à partir des chemins de jeu.
    private static int CountDistinctFurniture(IEnumerable<string> gamePaths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in gamePaths)
        {
            var segments = path.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length >= 3 && segments[i].Length <= 5
                    && int.TryParse(segments[i], System.Globalization.NumberStyles.None, null, out _))
                {
                    keys.Add(string.Join('/', segments[..(i + 1)]));
                    break;
                }
            }
        }
        return keys.Count;
    }

    private static byte[] DeriveKey(Guid shareId, byte[] salt)
    {
        byte[] shareBytes = shareId.ToByteArray();
        byte[] material = new byte[shareBytes.Length + salt.Length];
        Buffer.BlockCopy(shareBytes, 0, material, 0, shareBytes.Length);
        Buffer.BlockCopy(salt, 0, material, shareBytes.Length, salt.Length);
        return SHA256.HashData(material);
    }
}
