using MessagePack;
using Microsoft.Extensions.Logging;
using Stagehand.Api;
using Stagehand.Definitions;
using Stagehand.Definitions.ModResources;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.StageShare;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Factories;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using UmbraSync.WebAPI.Files;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Modules.Housing.Services;

public sealed partial class StageShareManager : IDisposable
{
    public const int MaxStageNameLength = 128;
    public const int MaxDescriptionLength = 1024;
    private const byte PayloadVersion = 1;
    private const int MaxDefinitionJsonBytes = 8 * 1024 * 1024;
    private const int MaxFileCount = 2048;
    private const int MaxCipherBytes = 16 * 1024 * 1024;
    private const string DefaultStagesFolderName = "Stages";

    public sealed record AppliedStage(Guid ShareId, string OwnerUid, string StageName, bool Visible);

    private readonly ILogger<StageShareManager> _logger;
    private readonly ApiController _apiController;
    private readonly IpcCallerStagehand _stagehand;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly MareConfigService _configService;
    private readonly MareMediator _mediator;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<StageShareEntryDto> _ownShares = [];
    private readonly ConcurrentDictionary<Guid, string> _pendingRepublish = new();
    private IReadOnlyList<AppliedStage> _appliedStages = [];
    private IReadOnlyList<StageShareEntryDto> _availableStages = [];
    private FileDownloadManager? _fileDownloadManager;
    private Task? _currentTask;
    private string? _stagesFolderHint;
    private bool _disposed;

    public StageShareManager(ILogger<StageShareManager> logger, ApiController apiController, IpcCallerStagehand stagehand,
        IpcCallerPenumbra penumbra, FileCacheManager fileCacheManager, FileUploadManager fileUploadManager,
        FileDownloadManagerFactory fileDownloadManagerFactory, MareConfigService configService, MareMediator mediator)
    {
        _logger = logger;
        _apiController = apiController;
        _stagehand = stagehand;
        _penumbra = penumbra;
        _fileCacheManager = fileCacheManager;
        _fileUploadManager = fileUploadManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _configService = configService;
        _mediator = mediator;
    }

    public IReadOnlyList<StageShareEntryDto> OwnShares => _ownShares;
    public IReadOnlyList<AppliedStage> AppliedStages => _appliedStages;
    public IReadOnlyList<StageShareEntryDto> AvailableStages => _availableStages;
    public IReadOnlyDictionary<Guid, string> PendingRepublish => _pendingRepublish;
    public StageLocationDto? AppliedLocation { get; private set; }
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public string? ProgressStatus { get; private set; }
    public float ProgressPercent { get; private set; }
    public bool IsStagehandAvailable => _stagehand.APIAvailable;
    public string StagehandUnavailableReason => _stagehand.DescribeUnavailability();
    public static int SupportedFormatVersion => StageDefinition.CurrentFormatVersion;

    public Task<StageLocationDto?> GetLocationAsync() => _stagehand.GetLocationAsync();

    public Task<LocalStageDefinition[]> GetLocalStagesAsync() => _stagehand.GetLocalStageDefinitionsAsync();

    public string? ResolveLocalStagePath(LocalStageDefinition stage)
    {
        if (string.IsNullOrWhiteSpace(stage.Filename)) return null;
        if (Path.IsPathRooted(stage.Filename)) return stage.Filename;

        string? firstCandidate = null;
        foreach (string? folder in CandidateStageFolders())
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string candidate = Path.Combine(folder, stage.Filename);
            firstCandidate ??= candidate;
            if (File.Exists(candidate)) return candidate;
        }
        return firstCandidate;
    }

    private IEnumerable<string?> CandidateStageFolders()
    {
        yield return _configService.Current.StagehandStagesPathOverride;
        yield return _stagesFolderHint;
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return string.IsNullOrEmpty(documents) ? null : Path.Combine(documents, DefaultStagesFolderName);
    }

    public void MarkLocalStageEdited(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        try
        {
            _stagesFolderHint = Path.GetDirectoryName(fullPath) ?? _stagesFolderHint;
        }
        catch (ArgumentException)
        {
            return;
        }

        foreach (var (shareIdText, sourcePath) in _configService.Current.StageShareSourceFiles)
        {
            if (!string.Equals(sourcePath, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (Guid.TryParse(shareIdText, out Guid shareId))
                _pendingRepublish[shareId] = fullPath;
        }
    }

    public string? GetSourceFile(Guid shareId)
        => _configService.Current.StageShareSourceFiles.TryGetValue(shareId.ToString(), out string? path) ? path : null;

    public Task PublishAsync(LocalStageDefinition stage, StageLocationDto location, string description,
        List<string> allowedIndividuals, List<string> allowedSyncshells, Guid? existingShareId = null, CancellationToken ct = default)
    {
        return RunOperation(async () =>
        {
            if (!_stagehand.APIAvailable)
            {
                LastError = Loc.Get("Stages.Error.StagehandMissing");
                return;
            }

            string? path = ResolveLocalStagePath(stage);
            if (path == null || !File.Exists(path))
            {
                LastError = Loc.Get("Stages.Error.FileNotFound");
                return;
            }

            ProgressStatus = Loc.Get("Stages.Progress.Reading");
            ProgressPercent = 0.05f;

            string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
                using var validation = new MemoryStream(Encoding.UTF8.GetBytes(text));
                if (root is not JsonObject || !StageDefinition.TryParseJSONStream(validation, out StageDefinition? parsed) || parsed == null)
                    root = null;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
            {
                _logger.LogWarning(ex, "Stage local illisible");
                root = null;
            }

            if (root == null)
            {
                LastError = Loc.Get("Stages.Error.ParseFailed");
                return;
            }

            int formatVersion = StageDefinitionRewriter.ReadFormatVersion(root);
            string stageName = StageDefinitionRewriter.ReadStageName(root);
            if (string.IsNullOrWhiteSpace(stageName)) stageName = stage.Name;
            if (string.IsNullOrWhiteSpace(stageName)) stageName = Path.GetFileNameWithoutExtension(path);
            stageName = Truncate(stageName, MaxStageNameLength);

            ProgressStatus = Loc.Get("Stages.Progress.Importing");
            ProgressPercent = 0.10f;

            var files = new List<StageShareFileRef>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            int skipped = 0;
            var resources = StageDefinitionRewriter.EnumerateResources(root);
            for (int i = 0; i < resources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var resource = resources[i];
                ProgressPercent = 0.10f + 0.20f * i / Math.Max(1, resources.Count);
                if (string.Equals(resource.Type, "Game", StringComparison.Ordinal)) continue;

                (string Hash, long Size)? imported = await Task.Run(() => ImportResource(resource), ct).ConfigureAwait(false);
                if (imported == null)
                {
                    StageDefinitionRewriter.RemoveResource(resource);
                    skipped++;
                    continue;
                }

                StageDefinitionRewriter.ReplaceWithMarker(resource.Node, imported.Value.Hash);
                hashes.Add(imported.Value.Hash);
                files.Add(new StageShareFileRef { Hash = imported.Value.Hash, Size = imported.Value.Size, GamePath = resource.GamePath });
            }

            if (files.Count > MaxFileCount)
            {
                LastError = Loc.Get("Stages.Error.TooLarge");
                return;
            }

            if (hashes.Count > 0)
            {
                ProgressStatus = Loc.Get("Stages.Progress.Uploading");
                ProgressPercent = 0.35f;
                var uploadProgress = new Progress<string>(status => ProgressStatus = status);
                var missingLocally = await _fileUploadManager.UploadFiles([.. hashes], uploadProgress, ct).ConfigureAwait(false);
                if (missingLocally.Count > 0)
                {
                    _logger.LogWarning("{Count} fichiers manquants localement lors de l'envoi du Stage", missingLocally.Count);
                    LastError = string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.Error.UploadFailed"), missingLocally.Count);
                    return;
                }
            }

            ProgressStatus = Loc.Get("Stages.Progress.Publishing");
            ProgressPercent = 0.80f;

            var plaintext = new StageSharePlaintextV1
            {
                StageName = stageName,
                StageFormatVersion = formatVersion,
                DefinitionJson = root.ToJsonString(),
                Translation = [stage.Translation.X, stage.Translation.Y, stage.Translation.Z],
                Rotation = [stage.Rotation.X, stage.Rotation.Y, stage.Rotation.Z, stage.Rotation.W],
                UniformScale = stage.UniformScale,
                Files = files,
            };
            if (Encoding.UTF8.GetByteCount(plaintext.DefinitionJson) > MaxDefinitionJsonBytes)
            {
                LastError = Loc.Get("Stages.Error.TooLarge");
                return;
            }

            byte[] body = MessagePackSerializer.Serialize(plaintext, cancellationToken: ct);
            byte[] dataBytes = new byte[1 + body.Length];
            dataBytes[0] = PayloadVersion;
            Buffer.BlockCopy(body, 0, dataBytes, 1, body.Length);
            if (dataBytes.Length > MaxCipherBytes)
            {
                LastError = Loc.Get("Stages.Error.TooLarge");
                return;
            }

            Guid shareId = existingShareId ?? Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = ShareCryptoHelper.DeriveKey(shareId, salt);
            byte[] cipher = new byte[dataBytes.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, dataBytes, cipher, tag);
            }

            await _apiController.StageShareUpload(new StageShareUploadRequestDto
            {
                ShareId = shareId,
                Location = location,
                Description = Truncate(description, MaxDescriptionLength),
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells,
                StageName = stageName,
                StageFormatVersion = formatVersion,
            }).ConfigureAwait(false);

            _configService.Current.StageShareSourceFiles[shareId.ToString()] = path;
            _configService.Save();
            _pendingRepublish.TryRemove(shareId, out _);

            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = skipped > 0
                ? string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.Success.PublishedSkipped"), files.Count, skipped)
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.Success.Published"), files.Count);
            _mediator.Publish(new NotificationMessage(Loc.Get("Stages.Notification.Title"), LastSuccess, NotificationType.Info));
        });
    }

    private (string Hash, long Size)? ImportResource(StageDefinitionRewriter.ModResource resource)
    {
        string extension = ExtensionOf(resource.GamePath);
        try
        {
            switch (resource.Type)
            {
                case "Disk":
                {
                    string? existingMarker = StageDefinitionRewriter.ReadMarker(resource.Node);
                    if (existingMarker != null)
                    {
                        var cached = _fileCacheManager.GetFileCacheByHash(existingMarker);
                        return cached != null && File.Exists(cached.ResolvedFilepath) ? (existingMarker, new FileInfo(cached.ResolvedFilepath).Length) : null;
                    }

                    string? diskPath = resource.Node["SourceDiskPath"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath)) return null;

                    string? modDirectory = _penumbra.ModDirectory;
                    if (!string.IsNullOrEmpty(modDirectory)
                        && diskPath.StartsWith(modDirectory, StringComparison.OrdinalIgnoreCase)
                        && _fileCacheManager.GetFileCachesByPaths([diskPath]).TryGetValue(diskPath, out var entry)
                        && entry != null)
                    {
                        return (entry.Hash, new FileInfo(diskPath).Length);
                    }

                    return ImportFileIntoCache(diskPath, extension);
                }
                case "Embedded":
                {
                    string? base64 = resource.Node["CompressedDataBytes"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(base64)) return null;
                    byte[] compressed = Convert.FromBase64String(base64);
                    ModCompressionScheme scheme = ReadCompressionScheme(resource.Node["CompressionScheme"]);
                    byte[] bytes = EmbeddedModResourceDefinition.DecompressDataBytes(compressed, scheme);
                    return ImportBytesIntoCache(bytes, extension);
                }
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ressource de Stage ignorée ({type})", resource.Type);
            return null;
        }
    }

    private static ModCompressionScheme ReadCompressionScheme(JsonNode? node)
    {
        if (node == null) return ModCompressionScheme.None;
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return (ModCompressionScheme)node.GetValue<int>();
        string? text = node.GetValue<string>();
        return Enum.TryParse(text, ignoreCase: true, out ModCompressionScheme scheme) ? scheme : ModCompressionScheme.None;
    }

    private (string Hash, long Size)? ImportFileIntoCache(string sourcePath, string extension)
    {
        string hash = sourcePath.GetFileHash();
        string target = _fileCacheManager.GetCacheFilePath(hash, extension);
        if (!File.Exists(target))
            File.Copy(sourcePath, target);
        return RegisterCacheFile(hash, target);
    }

    private (string Hash, long Size)? ImportBytesIntoCache(byte[] bytes, string extension)
    {
        string hash = Convert.ToHexString(SHA1.HashData(bytes));
        string target = _fileCacheManager.GetCacheFilePath(hash, extension);
        if (!File.Exists(target))
            File.WriteAllBytes(target, bytes);
        return RegisterCacheFile(hash, target);
    }

    private (string Hash, long Size)? RegisterCacheFile(string hash, string target)
    {
        var entry = _fileCacheManager.GetFileCacheByHash(hash) ?? _fileCacheManager.CreateCacheEntry(target, hash);
        if (entry == null) return null;
        return (hash, new FileInfo(target).Length);
    }

    private static string ExtensionOf(string gamePath)
    {
        int dot = gamePath.LastIndexOf('.');
        string extension = dot >= 0 && dot < gamePath.Length - 1 ? gamePath[(dot + 1)..] : "dat";
        return SafeExtensionPattern().IsMatch(extension) ? extension.ToLowerInvariant() : "dat";
    }

    private static bool IsSafeGamePath(string gamePath)
        => !string.IsNullOrWhiteSpace(gamePath)
           && !gamePath.Contains("..", StringComparison.Ordinal)
           && !gamePath.Contains(':', StringComparison.Ordinal)
           && !gamePath.StartsWith('/') && !gamePath.StartsWith('\\')
           && SafeExtensionPattern().IsMatch(ExtensionOf(gamePath));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex("^[a-zA-Z0-9]{1,8}$", RegexOptions.None, 100)]
    private static partial Regex SafeExtensionPattern();

    public Task UpdateVisibilityAsync(Guid shareId, string description, List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            var updated = await _apiController.StageShareUpdate(new StageShareUpdateRequestDto
            {
                ShareId = shareId,
                Description = Truncate(description, MaxDescriptionLength),
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells,
            }).ConfigureAwait(false);

            if (updated == null)
            {
                LastError = Loc.Get("Stages.Error.UpdateFailed");
                return;
            }

            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("Stages.Success.Updated");
        });
    }

    public Task DeleteAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            bool deleted = await _apiController.StageShareDelete(shareId).ConfigureAwait(false);
            if (!deleted)
            {
                LastError = Loc.Get("Stages.Error.DeleteFailed");
                return;
            }

            _configService.Current.StageShareSourceFiles.Remove(shareId.ToString());
            _configService.Save();
            _pendingRepublish.TryRemove(shareId, out _);
            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("Stages.Success.Deleted");
        });
    }

    public Task RefreshAsync() => RunOperation(InternalRefreshAsync);

    private async Task InternalRefreshAsync()
    {
        var own = await _apiController.StageShareGetOwn().ConfigureAwait(false);
        _ownShares.Clear();
        _ownShares.AddRange(own);
        LastSuccess = Loc.Get("Stages.Success.Refreshed");
    }

    public Task RefreshAvailableAsync(StageLocationDto location, CancellationToken ct = default)
    {
        return RunOperation(async () =>
        {
            AppliedLocation = location;
            if (!_stagehand.APIAvailable)
            {
                _availableStages = [];
                ForgetApplied();
                return;
            }

            ProgressStatus = Loc.Get("Stages.Progress.Searching");
            ProgressPercent = 0.02f;

            var shares = await _apiController.StageShareGetForLocation(location).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var config = _configService.Current;
            shares.RemoveAll(s => s == null
                                  || s.Location != location
                                  || s.IsOwner
                                  || s.StageFormatVersion > SupportedFormatVersion
                                  || IsDisabledForOwner(config, s.OwnerUid));
            _availableStages = shares;

            var wanted = new HashSet<Guid>(shares.Select(s => s.Id));
            foreach (var applied in _appliedStages.Where(a => !wanted.Contains(a.ShareId)).ToList())
            {
                await RemoveAppliedInternalAsync(applied).ConfigureAwait(false);
            }

            if (IsIndoor(location) && shares.Count > 0 && !_appliedStages.Any(a => a.Visible))
                await ShowInternalAsync(shares[0].Id, ct).ConfigureAwait(false);
        });
    }

    public static bool IsIndoor(StageLocationDto location) => location.HouseId >= 0 && location.RoomId >= 0;

    public Task ShowStageAsync(Guid shareId, CancellationToken ct = default)
        => RunOperation(() => ShowInternalAsync(shareId, ct));

    private async Task ShowInternalAsync(Guid shareId, CancellationToken ct)
    {
        var entry = _availableStages.FirstOrDefault(s => s.Id == shareId);
        if (entry == null || !_stagehand.APIAvailable) return;

        foreach (var other in _appliedStages.Where(a => a.Visible && a.ShareId != shareId).ToList())
        {
            if (await _stagehand.SetTemporaryStageVisibleAsync(StageIdFor(other.ShareId), false).ConfigureAwait(false))
                _appliedStages = [.. _appliedStages.Select(a => a.ShareId == other.ShareId ? a with { Visible = false } : a)];
        }

        var applied = _appliedStages.FirstOrDefault(a => a.ShareId == shareId);
        if (applied != null)
        {
            if (await _stagehand.SetTemporaryStageVisibleAsync(StageIdFor(shareId), true).ConfigureAwait(false))
                _appliedStages = [.. _appliedStages.Select(a => a.ShareId == shareId ? a with { Visible = true } : a)];
            return;
        }

        await ApplyShareAsync(entry, ct).ConfigureAwait(false);
    }

    public Task HideStageAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            var applied = _appliedStages.FirstOrDefault(a => a.ShareId == shareId);
            if (applied == null || !applied.Visible) return;
            if (await _stagehand.SetTemporaryStageVisibleAsync(StageIdFor(shareId), false).ConfigureAwait(false))
                _appliedStages = [.. _appliedStages.Select(a => a.ShareId == shareId ? a with { Visible = false } : a)];
        });
    }

    private static bool IsDisabledForOwner(MareConfig config, string ownerUid)
    {
        var pairOverride = config.PairSyncOverrides.TryGetValue(ownerUid, out var entry) ? entry : null;
        return pairOverride?.DisableStages ?? config.DefaultDisableStages;
    }

    private async Task ApplyShareAsync(StageShareEntryDto share, CancellationToken ct)
    {
        ProgressStatus = Loc.Get("Stages.Progress.Downloading");
        ProgressPercent = 0.10f;

        var payload = await _apiController.StageShareDownload(share.Id).ConfigureAwait(false);
        if (payload == null || payload.CipherData == null || payload.Nonce == null || payload.Salt == null || payload.Tag == null)
        {
            _logger.LogWarning("Stage {ShareId} indisponible au téléchargement", share.Id);
            return;
        }

        byte[] key = ShareCryptoHelper.DeriveKey(payload.ShareId, payload.Salt);
        byte[] plaintextBytes = new byte[payload.CipherData.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Nonce, payload.CipherData, payload.Tag, plaintextBytes);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Déchiffrement du Stage {ShareId} impossible", share.Id);
            LastError = Loc.Get("Stages.Error.DecryptFailed");
            return;
        }

        if (plaintextBytes.Length < 2 || plaintextBytes[0] != PayloadVersion)
        {
            _logger.LogWarning("Version de Stage inconnue pour {ShareId}", share.Id);
            return;
        }

        StageSharePlaintextV1? plaintext;
        try
        {
            plaintext = MessagePackSerializer.Deserialize<StageSharePlaintextV1>(
                new ReadOnlyMemory<byte>(plaintextBytes, 1, plaintextBytes.Length - 1), cancellationToken: ct);
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogWarning(ex, "Contenu de Stage illisible pour {ShareId}", share.Id);
            return;
        }
        if (plaintext == null || string.IsNullOrEmpty(plaintext.DefinitionJson)) return;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(plaintext.DefinitionJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Définition de Stage illisible pour {ShareId}", share.Id);
            return;
        }
        if (root == null) return;

        var resolved = await ResolveFilesAsync(share.Id, plaintext.Files ?? [], ct).ConfigureAwait(false);
        int count = StageDefinitionRewriter.ResolveMarkers(root, hash => resolved.TryGetValue(hash, out string? p) ? p : null, out var unresolved);
        if (unresolved.Count > 0)
            _logger.LogWarning("Stage {ShareId} : {Count} ressources non résolues retirées", share.Id, unresolved.Count);
        _logger.LogDebug("Stage {ShareId} : {Count} ressources résolues", share.Id, count);

        ProgressStatus = Loc.Get("Stages.Progress.Applying");
        ProgressPercent = 0.90f;

        float[] t = plaintext.Translation is { Length: 3 } ? plaintext.Translation : [0f, 0f, 0f];
        float[] r = plaintext.Rotation is { Length: 4 } ? plaintext.Rotation : [0f, 0f, 0f, 1f];
        string stageId = StageIdFor(share.Id);
        string stageName = string.IsNullOrWhiteSpace(plaintext.StageName) ? share.StageName : plaintext.StageName;
        bool created = await _stagehand.CreateOrUpdateTemporaryStageAsync(stageId, root.ToJsonString(),
            new Vector3(t[0], t[1], t[2]), new Quaternion(r[0], r[1], r[2], r[3]), plaintext.UniformScale <= 0f ? 1f : plaintext.UniformScale,
            "UmbraSync " + stageName).ConfigureAwait(false);
        if (!created)
        {
            LastError = Loc.Get("Stages.Error.ApplyFailed");
            return;
        }

        await _stagehand.SetTemporaryStageVisibleAsync(stageId, true).ConfigureAwait(false);
        _appliedStages = [.. _appliedStages, new AppliedStage(share.Id, share.OwnerUid, stageName, true)];
        _logger.LogInformation("Stage {ShareId} appliqué", share.Id);
        _mediator.Publish(new StageAppliedMessage(share.Id, share.OwnerUid, stageName));
    }

    private async Task<Dictionary<string, string>> ResolveFilesAsync(Guid shareId, List<StageShareFileRef> files, CancellationToken ct)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<FileReplacementData>();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.Hash) || !IsSafeGamePath(file.GamePath)) continue;
            if (resolved.ContainsKey(file.Hash)) continue;
            var cached = _fileCacheManager.GetFileCacheByHash(file.Hash);
            if (cached != null && File.Exists(cached.ResolvedFilepath))
            {
                resolved[file.Hash] = cached.ResolvedFilepath;
                continue;
            }
            if (!missing.Exists(m => string.Equals(m.Hash, file.Hash, StringComparison.Ordinal)))
                missing.Add(new FileReplacementData { Hash = file.Hash, GamePaths = [file.GamePath] });
        }

        if (missing.Count == 0) return resolved;

        ProgressStatus = string.Format(CultureInfo.CurrentCulture, Loc.Get("Stages.Progress.DownloadingFiles"), missing.Count);
        ProgressPercent = 0.30f;
        _fileDownloadManager ??= _fileDownloadManagerFactory.Create();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        string downloadId = $"Stage_{shareId:N}";
        await _fileDownloadManager.InitiateDownloadList(downloadId, missing, linkedCts.Token).ConfigureAwait(false);
        await _fileDownloadManager.DownloadFiles(downloadId, missing, linkedCts.Token).ConfigureAwait(false);

        foreach (var file in missing)
        {
            var cached = _fileCacheManager.GetFileCacheByHash(file.Hash);
            if (cached != null && File.Exists(cached.ResolvedFilepath))
                resolved[file.Hash] = cached.ResolvedFilepath;
            else
                _logger.LogWarning("Fichier {Hash} toujours manquant après téléchargement du Stage", file.Hash);
        }
        return resolved;
    }

    public Task RemoveAllAppliedAsync(string reason)
    {
        if (_appliedStages.Count == 0) return Task.CompletedTask;
        return RunOperation(async () =>
        {
            _logger.LogDebug("Retrait des Stages appliqués : {Reason}", reason);
            foreach (var applied in _appliedStages.ToList())
            {
                await RemoveAppliedInternalAsync(applied).ConfigureAwait(false);
            }
        });
    }

    private async Task RemoveAppliedInternalAsync(AppliedStage applied)
    {
        await _stagehand.DestroyTemporaryStageAsync(StageIdFor(applied.ShareId)).ConfigureAwait(false);
        _appliedStages = [.. _appliedStages.Where(a => a.ShareId != applied.ShareId)];
        _mediator.Publish(new StageRemovedMessage(applied.ShareId));
    }

    public void ForgetApplied()
    {
        var previous = _appliedStages;
        _appliedStages = [];
        foreach (var applied in previous)
            _mediator.Publish(new StageRemovedMessage(applied.ShareId));
    }

    private static string StageIdFor(Guid shareId) => $"{IpcCallerStagehand.TemporaryStageIdPrefix}{shareId:N}";

    private Task RunOperation(Func<Task> operation)
    {
        async Task Wrapper()
        {
            await _operationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                LastError = null;
                LastSuccess = null;
                ProgressStatus = null;
                ProgressPercent = 0f;
                await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Opération de partage de Stage annulée");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur pendant une opération de partage de Stage");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _fileDownloadManager?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dispose du gestionnaire de téléchargement des Stages en échec");
        }
        _fileDownloadManager = null;
    }
}
