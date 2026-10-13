using Dalamud.Utility;
using K4os.Compression.LZ4.Streams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.FileCache;

public sealed class FileCacheManager : DisposableMediatorSubscriberBase, IHostedService
{
    public const string CachePrefix = "{cache}";
    public const string CsvSplit = "|";
    public const string PenumbraPrefix = "{penumbra}";
    public const string SubstPrefix = "{subst}";
    public const string SubstPath = "subst";
    public string CacheFolder => _configService.Current.CacheFolder;
    public string SubstFolder => CacheFolder.IsNullOrEmpty() ? string.Empty : CacheFolder.ToLowerInvariant().TrimEnd('\\') + "\\" + SubstPath;
    private readonly MareConfigService _configService;
    private readonly MareMediator _mareMediator;
    private readonly string _csvPath;
    private readonly ConcurrentDictionary<string, List<FileCacheEntity>> _fileCaches = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _getCachesByPathsSemaphore = new(1, 1);
    private readonly Lock _fileWriteLock = new();
    private readonly Lock _fileCachesLock = new();
    private readonly IpcManager _ipcManager;
    private readonly ILogger<FileCacheManager> _logger;
    private readonly CacheLeaseRegistry _cacheLeases;
    private readonly DateTime _startupUtc = DateTime.UtcNow;
    private bool _hasCheckedPenumbraOnLogin;

    public FileCacheManager(ILogger<FileCacheManager> logger, IpcManager ipcManager, MareConfigService configService, MareMediator mareMediator,
        CacheLeaseRegistry cacheLeases)
        : base(logger, mareMediator)
    {
        _logger = logger;
        _ipcManager = ipcManager;
        _cacheLeases = cacheLeases;
        _configService = configService;
        _mareMediator = mareMediator;
        _csvPath = Path.Combine(configService.ConfigurationDirectory, "FileCache.csv");

        Mediator.Subscribe<DalamudLoginMessage>(this, _ => CheckPenumbraAndNotify());
    }

    private string CsvBakPath => _csvPath + ".bak";

    private void CheckPenumbraAndNotify()
    {
        if (_hasCheckedPenumbraOnLogin) return;
        _hasCheckedPenumbraOnLogin = true;

        if (!_ipcManager.Penumbra.APIAvailable || string.IsNullOrEmpty(_ipcManager.Penumbra.ModDirectory))
        {
            Mediator.Publish(new NotificationMessage("Penumbra not connected",
                "Could not load local file cache data. Penumbra is not connected or not properly set up. Please enable and/or configure Penumbra properly to use Umbra. After, reload Umbra in the Plugin installer.",
                MareConfiguration.Models.NotificationType.Error));
        }
    }

    public FileCacheEntity? CreateCacheEntry(string path, string? hash = null)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Creating cache entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(_configService.Current.CacheFolder.ToLowerInvariant(), StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(_configService.Current.CacheFolder.ToLowerInvariant(), CachePrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        if (hash != null)
            return CreateFileCacheEntity(fi, prefixedPath, hash);
        else
            return CreateFileCacheEntity(fi, prefixedPath);
    }

    public FileCacheEntity? CreateSubstEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Creating substitute entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(SubstFolder, StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(SubstFolder, SubstPrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        var fakeHash = Path.GetFileNameWithoutExtension(fi.FullName).ToUpperInvariant();
        var result = CreateFileCacheEntity(fi, prefixedPath, fakeHash);
        return result;
    }

    public FileCacheEntity? CreateFileEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Creating file entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), PenumbraPrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return CreateFileCacheEntity(fi, prefixedPath);
    }

    public List<FileCacheEntity> GetAllFileCaches()
    {
        lock (_fileCachesLock)
        {
            return _fileCaches.Values.SelectMany(v => v).ToList();
        }
    }

    // Les listes sont modifiées sous _fileCachesLock : toute lecture passe par une copie prise sous ce verrou.
    private List<FileCacheEntity>? SnapshotEntries(string hash)
    {
        lock (_fileCachesLock)
        {
            return _fileCaches.TryGetValue(hash, out var entries) ? entries.ToList() : null;
        }
    }

    public List<FileCacheEntity> GetAllFileCachesByHash(string hash, bool ignoreCacheEntries = false, bool validate = true)
    {
        List<FileCacheEntity> output = [];
        var fileCacheEntities = SnapshotEntries(hash);
        if (fileCacheEntities != null)
        {
            var entries = fileCacheEntities.AsEnumerable();
            if (ignoreCacheEntries)
            {
                entries = entries.Where(c => !c.IsCacheEntry && !c.IsSubstEntry);
            }

            foreach (var fileCache in entries.ToList())
            {
                if (!validate)
                {
                    output.Add(fileCache);
                }
                else
                {
                    var validated = GetValidatedFileCache(fileCache);
                    if (validated != null) output.Add(validated);
                }
            }
        }

        return output;
    }

    public Task<List<FileCacheEntity>> ValidateLocalIntegrity(IProgress<(int, int, FileCacheEntity)> progress, CancellationToken cancellationToken)
    {
        _mareMediator.Publish(new HaltScanMessage(nameof(ValidateLocalIntegrity)));
        _logger.LogInformation("Validating local storage");
        var cacheEntries = GetAllFileCaches().Where(v => v.IsCacheEntry).ToList();
        List<FileCacheEntity> brokenEntities = [];
        int i = 0;
        foreach (var fileCache in cacheEntries)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (fileCache.IsSubstEntry) continue;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Validating {file}", fileCache.ResolvedFilepath);

            progress.Report((i, cacheEntries.Count, fileCache));
            i++;
            if (!File.Exists(fileCache.ResolvedFilepath))
            {
                brokenEntities.Add(fileCache);
                continue;
            }

            try
            {
                var computedHash = Crypto.GetFileHash(fileCache.ResolvedFilepath);
                if (!string.Equals(computedHash, fileCache.Hash, StringComparison.Ordinal))
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Failed to validate {file}, got hash {hash}, expected hash {expectedHash}", fileCache.ResolvedFilepath, computedHash, fileCache.Hash);
                    brokenEntities.Add(fileCache);
                }
            }
            catch (Exception e)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(e, "Error during validation of {file}", fileCache.ResolvedFilepath);
                brokenEntities.Add(fileCache);
            }
        }

        foreach (var brokenEntity in brokenEntities)
        {
            RemoveHashedFile(brokenEntity.Hash, brokenEntity.PrefixedFilePath);

            try
            {
                File.Delete(brokenEntity.ResolvedFilepath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete {file}", brokenEntity.ResolvedFilepath);
            }
        }

        _mareMediator.Publish(new ResumeScanMessage(nameof(ValidateLocalIntegrity)));
        return Task.FromResult(brokenEntities);
    }

    public string GetCacheFilePath(string hash, string extension)
    {
        return Path.Combine(_configService.Current.CacheFolder, hash + "." + extension);
    }

    public string GetSubstFilePath(string hash, string extension)
    {
        return Path.Combine(SubstFolder, hash + "." + extension);
    }

    public async Task<(string, byte[])> GetCompressedFileData(string fileHash, CancellationToken uploadToken)
    {
        var fileCache = GetFileCacheByHash(fileHash)!;
        using var fs = File.OpenRead(fileCache.ResolvedFilepath);
        var ms = new MemoryStream(64 * 1024);
        using var encstream = LZ4Stream.Encode(ms, new LZ4EncoderSettings() { CompressionLevel = K4os.Compression.LZ4.LZ4Level.L09_HC });
        await fs.CopyToAsync(encstream, uploadToken).ConfigureAwait(false);
        encstream.Close();
        fileCache.CompressedSize = encstream.Length;
        return (fileHash, ms.ToArray());
    }

    public FileCacheEntity? GetFileCacheByHash(string hash, bool preferSubst = false)
    {
        var caches = GetFileCachesByHash(hash);
        if (preferSubst && caches.Subst != null)
            return caches.Subst;
        return caches.Penumbra ?? caches.Cache;
    }

    public (FileCacheEntity? Penumbra, FileCacheEntity? Cache, FileCacheEntity? Subst) GetFileCachesByHash(string hash)
    {
        (FileCacheEntity? Penumbra, FileCacheEntity? Cache, FileCacheEntity? Subst) result = (null, null, null);
        var hashes = SnapshotEntries(hash);
        if (hashes != null)
        {
            // Validate peut invalider une entrée (null) ou réécrire son hash : on continue sur la suivante
            // et on ne renvoie que ce qui correspond encore au hash demandé.
            result.Penumbra = hashes.Where(p => p.PrefixedFilePath.StartsWith(PenumbraPrefix, StringComparison.Ordinal))
                .Select(GetValidatedFileCache).FirstOrDefault(p => HasRequestedHash(p, hash));
            result.Cache = hashes.Where(p => p.PrefixedFilePath.StartsWith(CachePrefix, StringComparison.Ordinal))
                .Select(GetValidatedFileCache).FirstOrDefault(p => HasRequestedHash(p, hash));
            result.Subst = hashes.Where(p => p.PrefixedFilePath.StartsWith(SubstPrefix, StringComparison.Ordinal))
                .Select(GetValidatedFileCache).FirstOrDefault(p => HasRequestedHash(p, hash));
        }
        return result;
    }

    private static bool HasRequestedHash(FileCacheEntity? fileCache, string requestedHash)
        => fileCache != null && string.Equals(fileCache.Hash, requestedHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>Note l'usage d'un fichier résolu pour un pair (ordre d'éviction du cache).</summary>
    public void MarkUsed(FileCacheEntity? fileCache)
    {
        if (fileCache == null || (!fileCache.IsCacheEntry && !fileCache.IsSubstEntry)) return;
        _cacheLeases.MarkUsed(fileCache.ResolvedFilepath);
    }

    private FileCacheEntity? GetFileCacheByPath(string path)
    {
        var cleanedPath = path.Replace("/", "\\", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()
            .Replace(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), "", StringComparison.OrdinalIgnoreCase);
        var entry = GetAllFileCaches().FirstOrDefault(f => f.ResolvedFilepath.EndsWith(cleanedPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            _logger.LogDebug("Found no entries for {path}", cleanedPath);
            return CreateFileEntry(path);
        }

        var validatedCacheEntry = GetValidatedFileCache(entry);

        return validatedCacheEntry;
    }

    public Dictionary<string, FileCacheEntity?> GetFileCachesByPaths(string[] paths)
    {
        _getCachesByPathsSemaphore.Wait();

        try
        {
            var cleanedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
                p => p.Replace("/", "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace(_ipcManager.Penumbra.ModDirectory!, _ipcManager.Penumbra.ModDirectory!.EndsWith('\\') ? PenumbraPrefix + '\\' : PenumbraPrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace(SubstFolder, SubstPrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace(_configService.Current.CacheFolder, _configService.Current.CacheFolder.EndsWith('\\') ? CachePrefix + '\\' : CachePrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace("\\\\", "\\", StringComparison.Ordinal),
                StringComparer.OrdinalIgnoreCase);

            Dictionary<string, FileCacheEntity?> result = new(StringComparer.OrdinalIgnoreCase);

            // Build a lookup of prefixed file path -> last seen FileCacheEntity in a safe way.
            // Using ToDictionary here can throw when duplicate keys are encountered due to transient
            // race conditions or inconsistent state during updates. Prefer a manual fill with
            // overwrite semantics to avoid AggregateException(ArgumentException: same key added).
            var dict = new Dictionary<string, FileCacheEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in GetAllFileCaches())
            {
                // Overwrite semantics: the latest entry wins for identical prefixed paths.
                // This mirrors how the cache treats updates and prevents duplicate-key crashes.
                dict[d.PrefixedFilePath] = d;
            }

            foreach (var entry in cleanedPaths)
            {

                if (dict.TryGetValue(entry.Value, out var entity))
                {
                    var validatedCache = GetValidatedFileCache(entity);
                    result.Add(entry.Key, validatedCache);
                }
                else
                {
                    if (entry.Value.StartsWith(PenumbraPrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateFileEntry(entry.Key));
                    else if (entry.Value.StartsWith(SubstPrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateSubstEntry(entry.Key));
                    else if (entry.Value.StartsWith(CachePrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateCacheEntry(entry.Key));
                }
            }

            return result;
        }
        finally
        {
            _getCachesByPathsSemaphore.Release();
        }
    }

    public void RemoveHashedFile(string hash, string prefixedFilePath)
    {
        lock (_fileCachesLock)
        {
            if (_fileCaches.TryGetValue(hash, out var caches))
            {
                var removedCount = caches.RemoveAll(c => string.Equals(c.PrefixedFilePath, prefixedFilePath, StringComparison.Ordinal));
                _logger.LogTrace("Removed from DB: {count} file(s) with hash {hash} and file cache {path}", removedCount, hash, prefixedFilePath);

                if (caches.Count == 0)
                {
                    _fileCaches.Remove(hash, out var _);
                }
            }
        }
    }

    /// <summary>
    /// Retire de l'index les entrées cache/subst qui pointent sur <paramref name="filePath"/>
    /// (fichier supprimé par l'éviction). Renvoie le nombre d'entrées retirées.
    /// </summary>
    public int RemoveEntriesForFile(string hash, string filePath)
    {
        var entries = SnapshotEntries(hash);
        if (entries == null) return 0;

        string fullPath = NormalizeFullPath(filePath);
        int removed = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsCacheEntry && !entry.IsSubstEntry) continue;
            if (!string.Equals(NormalizeFullPath(ReplacePathPrefixes(entry).ResolvedFilepath), fullPath, StringComparison.OrdinalIgnoreCase)) continue;

            RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            removed++;
        }

        return removed;
    }

    private static string NormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// Réconcilie l'index avec le contenu réel des dossiers de stockage, sans passer par le
    /// FileSystemWatcher (événements perdus sous Wine/macOS) : ajoute les fichiers non indexés,
    /// retire les entrées dont le fichier a disparu.
    /// </summary>
    public (int Added, int Removed) ReconcileStorageFolders(CancellationToken token)
    {
        string cacheFolder = _configService.Current.CacheFolder;
        if (string.IsNullOrEmpty(cacheFolder) || !Directory.Exists(cacheFolder)) return (0, 0);

        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storageEntries = GetAllFileCaches().Where(e => e.IsCacheEntry || e.IsSubstEntry).ToList();
        foreach (var entry in storageEntries)
        {
            indexed.Add(NormalizeFullPath(ReplacePathPrefixes(entry).ResolvedFilepath));
        }

        int added = 0;
        int removed = 0;
        string substFolder = SubstFolder;
        var folders = new List<(string Folder, bool IsSubst)> { (cacheFolder, false) };
        if (!string.IsNullOrEmpty(substFolder) && Directory.Exists(substFolder))
            folders.Add((substFolder, true));

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, isSubst) in folders)
        {
            foreach (var filePath in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (!CacheLeaseRegistry.TryGetHashFromCacheFileName(filePath, out _)) continue;

                string fullPath = NormalizeFullPath(filePath);
                onDisk.Add(fullPath);
                if (indexed.Contains(fullPath)) continue;

                try
                {
                    var entry = isSubst ? CreateSubstEntry(filePath) : CreateCacheEntry(filePath);
                    if (entry != null) added++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not index storage file {file} during reconciliation", filePath);
                }
            }
        }

        foreach (var entry in storageEntries)
        {
            token.ThrowIfCancellationRequested();
            string fullPath = NormalizeFullPath(entry.ResolvedFilepath);
            if (onDisk.Contains(fullPath) || File.Exists(fullPath)) continue;

            RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            removed++;
        }

        if (removed > 0)
            WriteOutFullCsv();

        return (added, removed);
    }

    public void UpdateHashedFile(FileCacheEntity fileCache, bool computeProperties = true)
    {
        _logger.LogTrace("Updating hash for {path}", fileCache.ResolvedFilepath);
        var oldHash = fileCache.Hash;
        var prefixedPath = fileCache.PrefixedFilePath;
        if (computeProperties)
        {
            var fi = new FileInfo(fileCache.ResolvedFilepath);
            fileCache.Size = fi.Length;
            fileCache.CompressedSize = null;
            fileCache.Hash = Crypto.GetFileHash(fileCache.ResolvedFilepath);
            fileCache.LastModifiedDateTicks = fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        RemoveHashedFile(oldHash, prefixedPath);
        AddHashedFile(fileCache);
    }

    public (FileState State, FileCacheEntity FileCache) ValidateFileCacheEntity(FileCacheEntity fileCache)
    {
        fileCache = ReplacePathPrefixes(fileCache);
        FileInfo fi = new(fileCache.ResolvedFilepath);
        if (!fi.Exists)
        {
            return (FileState.RequireDeletion, fileCache);
        }
        if (HasChangedOnDisk(fi, fileCache))
        {
            return (FileState.RequireUpdate, fileCache);
        }

        return (FileState.Valid, fileCache);
    }

    public void WriteOutFullCsv()
    {
        lock (_fileWriteLock)
        {
            StringBuilder sb = new();
            foreach (var entry in GetAllFileCaches().OrderBy(f => f.PrefixedFilePath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.CsvEntry);
            }

            if (File.Exists(_csvPath))
            {
                File.Copy(_csvPath, CsvBakPath, overwrite: true);
            }

            try
            {
                File.WriteAllText(_csvPath, sb.ToString());
                File.Delete(CsvBakPath);
            }
            catch
            {
                File.WriteAllText(CsvBakPath, sb.ToString());
            }
        }
    }

    internal FileCacheEntity MigrateFileHashToExtension(FileCacheEntity fileCache, string ext)
    {
        try
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            var extensionPath = fileCache.ResolvedFilepath.ToUpper(CultureInfo.InvariantCulture) + "." + ext;
            File.Move(fileCache.ResolvedFilepath, extensionPath, overwrite: true);
            var newHashedEntity = new FileCacheEntity(fileCache.Hash, fileCache.PrefixedFilePath + "." + ext, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            newHashedEntity.SetResolvedFilePath(extensionPath);
            AddHashedFile(newHashedEntity);
            _logger.LogTrace("Migrated from {oldPath} to {newPath}", fileCache.ResolvedFilepath, newHashedEntity.ResolvedFilepath);
            return newHashedEntity;
        }
        catch (Exception ex)
        {
            AddHashedFile(fileCache);
            _logger.LogWarning(ex, "Failed to migrate entity {entity}", fileCache.PrefixedFilePath);
            return fileCache;
        }
    }

    private void AddHashedFile(FileCacheEntity fileCache)
    {
        lock (_fileCachesLock)
        {
            if (!_fileCaches.TryGetValue(fileCache.Hash, out var entries))
            {
                _fileCaches[fileCache.Hash] = entries = [];
            }

            if (!entries.Exists(u => string.Equals(u.PrefixedFilePath, fileCache.PrefixedFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(fileCache);
            }
        }
    }

    private FileCacheEntity? CreateFileCacheEntity(FileInfo fileInfo, string prefixedPath, string? hash = null)
    {
        hash ??= Crypto.GetFileHash(fileInfo.FullName);
        var entity = new FileCacheEntity(hash, prefixedPath, fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileInfo.Length);
        entity = ReplacePathPrefixes(entity);
        AddHashedFile(entity);
        lock (_fileWriteLock)
        {
            File.AppendAllLines(_csvPath, new[] { entity.CsvEntry });
        }
        var result = GetFileCacheByPath(fileInfo.FullName);
        _logger.LogTrace("Creating cache entity for {name} success: {success}", fileInfo.FullName, (result != null));
        return result;
    }

    /// <summary>
    /// Re-résout tous les chemins absolus à partir des préfixes. Nécessaire après un changement
    /// de dossier de stockage : les entités déjà chargées pointeraient sinon vers l'ancien
    /// emplacement jusqu'à leur prochaine validation.
    /// </summary>
    public void ResolveAllPaths()
    {
        lock (_fileCachesLock)
        {
            foreach (var entity in _fileCaches.SelectMany(f => f.Value))
            {
                ReplacePathPrefixes(entity);
            }
        }
    }

    private FileCacheEntity? GetValidatedFileCache(FileCacheEntity fileCache)
    {
        var resultingFileCache = ReplacePathPrefixes(fileCache);
        resultingFileCache = Validate(resultingFileCache);
        return resultingFileCache;
    }

    private FileCacheEntity ReplacePathPrefixes(FileCacheEntity fileCache)
    {
        if (fileCache.PrefixedFilePath.StartsWith(PenumbraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(PenumbraPrefix, _ipcManager.Penumbra.ModDirectory, StringComparison.Ordinal));
        }
        else if (fileCache.PrefixedFilePath.StartsWith(SubstPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(SubstPrefix, SubstFolder, StringComparison.Ordinal));
        }
        else if (fileCache.PrefixedFilePath.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(CachePrefix, _configService.Current.CacheFolder, StringComparison.Ordinal));
        }

        return fileCache;
    }

    private FileCacheEntity? Validate(FileCacheEntity fileCache)
    {
        var file = new FileInfo(fileCache.ResolvedFilepath);
        if (!file.Exists)
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            return null;
        }

        if (HasChangedOnDisk(file, fileCache))
        {
            UpdateHashedFile(fileCache);
        }

        return fileCache;
    }

    // Taille comparée en plus du mtime : un fichier tronqué peut garder le même mtime
    // (copie qui préserve les dates, horloge Wine). Taille inconnue (-1/null) : mtime seul.
    private static bool HasChangedOnDisk(FileInfo file, FileCacheEntity fileCache)
    {
        if (!string.Equals(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
            return true;

        return fileCache.Size is > 0 && file.Length != fileCache.Size.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileCacheManager");
        lock (_fileWriteLock)
        {
            try
            {
                _logger.LogInformation("Checking for {bakPath}", CsvBakPath);

                if (File.Exists(CsvBakPath))
                {
                    _logger.LogInformation("{bakPath} found, moving to {csvPath}", CsvBakPath, _csvPath);

                    File.Move(CsvBakPath, _csvPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to move BAK to ORG, deleting BAK");
                try
                {
                    if (File.Exists(CsvBakPath))
                        File.Delete(CsvBakPath);
                }
                catch (Exception ex1)
                {
                    _logger.LogWarning(ex1, "Could not delete bak file");
                }
            }
        }

        if (File.Exists(_csvPath))
        {
            _logger.LogInformation("{csvPath} found, parsing", _csvPath);

            bool success = false;
            string[] entries = [];
            int attempts = 0;
            while (!success && attempts < 10)
            {
                try
                {
                    _logger.LogInformation("Attempting to read {csvPath}", _csvPath);
                    entries = File.ReadAllLines(_csvPath);
                    success = true;
                }
                catch (Exception ex)
                {
                    attempts++;
                    _logger.LogWarning(ex, "Could not open {file}, trying again", _csvPath);
                    Thread.Sleep(100);
                }
            }

            if (!entries.Any())
            {
                _logger.LogWarning("Could not load entries from {path}, continuing with empty file cache", _csvPath);
            }

            _logger.LogInformation("Found {amount} files in {path}", entries.Length, _csvPath);

            Dictionary<string, bool> processedFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var splittedEntry = entry.Split(CsvSplit, StringSplitOptions.None);
                try
                {
                    var hash = splittedEntry[0];
                    if (hash.Length != 40) throw new InvalidOperationException("Expected Hash length of 40, received " + hash.Length);
                    var path = splittedEntry[1];
                    var time = splittedEntry[2];

                    if (processedFiles.ContainsKey(path))
                    {
                        _logger.LogWarning("Already processed {file}, ignoring", path);
                        continue;
                    }

                    processedFiles.Add(path, value: true);

                    long size = -1;
                    long compressed = -1;
                    if (splittedEntry.Length > 3)
                    {
                        if (long.TryParse(splittedEntry[3], CultureInfo.InvariantCulture, out long result))
                        {
                            size = result;
                        }
                        if (long.TryParse(splittedEntry[4], CultureInfo.InvariantCulture, out long resultCompressed))
                        {
                            compressed = resultCompressed;
                        }
                    }
                    AddHashedFile(ReplacePathPrefixes(new FileCacheEntity(hash, path, time, size, compressed)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize entry {entry}, ignoring", entry);
                }
            }

            if (processedFiles.Count != entries.Length)
            {
                WriteOutFullCsv();
            }
        }

        _logger.LogInformation("Started FileCacheManager");
        
        _ = Task.Run(() =>
        {
            try { CleanupCrashArtifacts(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Crash artifact cleanup failed"); }
        });

        return Task.CompletedTask;
    }
    
    private void CleanupCrashArtifacts()
    {
        var cacheFolder = _configService.Current.CacheFolder;
        if (string.IsNullOrEmpty(cacheFolder) || !Directory.Exists(cacheFolder))
            return;

        string[] artifactExtensions = [".tmp", ".lz4tmp", ".cdntmp", ".blk"];
        int removedArtifacts = 0;
        int removedZeroByte = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheFolder, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    // Un temporaire créé depuis le démarrage appartient à un téléchargement en cours :
                    // l'énumération d'un gros dossier sous Wine peut prendre plusieurs secondes.
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.LastWriteTimeUtc >= _startupUtc || fileInfo.CreationTimeUtc >= _startupUtc)
                        continue;

                    var ext = fileInfo.Extension;
                    if (artifactExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(filePath);
                        removedArtifacts++;
                        continue;
                    }

                    if (fileInfo.Length == 0)
                    {
                        File.Delete(filePath);
                        removedZeroByte++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not clean up cache artifact {file}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate cache folder for crash artifact cleanup");
            return;
        }

        if (removedArtifacts > 0 || removedZeroByte > 0)
            _logger.LogInformation("Crash artifact cleanup: removed {artifacts} download temp file(s) and {zero} zero-byte file(s)", removedArtifacts, removedZeroByte);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        WriteOutFullCsv();
        return Task.CompletedTask;
    }
}