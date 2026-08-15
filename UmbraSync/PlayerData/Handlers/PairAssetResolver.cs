using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.WebAPI.Files;

namespace UmbraSync.PlayerData.Handlers;

/// <summary>
/// Traduit les remplacements de fichiers annoncés par un pair en redirections Penumbra exploitables et signale ce qui manque encore en local.
/// </summary>
public sealed class PairAssetResolver
{
    private readonly ILogger _logger;
    private readonly UserData _userData;
    private readonly FileCacheManager _fileDbManager;
    private readonly CompressedAlternateManager _compressedAlternateManager;
    private readonly FileDownloadManager _downloadManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;

    public PairAssetResolver(ILogger logger, UserData userData, FileCacheManager fileDbManager,
        CompressedAlternateManager compressedAlternateManager, FileDownloadManager downloadManager,
        PlayerPerformanceConfigService playerPerformanceConfigService)
    {
        _logger = logger;
        _userData = userData;
        _fileDbManager = fileDbManager;
        _compressedAlternateManager = compressedAlternateManager;
        _downloadManager = downloadManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
    }
    
    public sealed record Resolution(
        List<FileReplacementData> MissingFiles,
        HashSet<string> LocallyPresentFiles,
        Dictionary<(string GamePath, string? Hash), string> ModdedPaths);

    /// <summary>Mode de compression pour ce pair : override par UID sinon config globale.</summary>
    public TextureCompressionMode ComputeCompressedAlternateUsage()
    {
        var cfg = _playerPerformanceConfigService.Current;
        if (cfg.UIDsToOverride.Exists(uid =>
                string.Equals(uid, _userData.UID, StringComparison.Ordinal)
                || string.Equals(uid, _userData.Alias, StringComparison.Ordinal)))
        {
            return TextureCompressionMode.AlwaysSourceQuality;
        }
        return cfg.TextureCompressionMode;
    }

    public Resolution Resolve(Guid applicationBase, CharacterData charaData, TextureCompressionMode compressedUsage, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        var moddedDictionary = new Dictionary<(string GamePath, string? Hash), string>();
        var locallyPresentFiles = new HashSet<string>(StringComparer.Ordinal);
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        var locallyPresentFileSet = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        bool hasMigrationChanges = false;

        try
        {
            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();

                var replacementItem = item;
                var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash, preferSubst: true);
                bool confirmed = _compressedAlternateManager.TryGetCachedCompressedAlternate(item.Hash, out var altHash);

                if (compressedUsage == TextureCompressionMode.AlwaysSourceQuality)
                {
                    // Rien : on garde la source.
                }
                else if (compressedUsage == TextureCompressionMode.CompressedNewDownloads)
                {
                    // BC7 seulement si la source n'est pas déjà en local.
                    if (fileCache == null && confirmed && altHash != null)
                    {
                        replacementItem = new FileReplacementData { GamePaths = item.GamePaths, Hash = altHash };
                        fileCache = _fileDbManager.GetFileCacheByHash(altHash, preferSubst: true);
                    }
                }
                else 
                {
                    if (confirmed)
                    {
                        if (altHash != null)
                        {
                            replacementItem = new FileReplacementData { GamePaths = item.GamePaths, Hash = altHash };
                            fileCache = _fileDbManager.GetFileCacheByHash(altHash, preferSubst: true);
                        }
                    }
                    else
                    {

                        locallyPresentFileSet[item.Hash] = 0;
                        fileCache = null;
                    }
                }

                if (fileCache != null)
                {
                    if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, replacementItem.GamePaths[0].Split(".")[^1]);
                    }

                    foreach (var gamePath in item.GamePaths)
                    {
                        outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                    }
                }
                else
                {
                    _logger.LogTrace("Missing file: {hash}", replacementItem.Hash);
                    missingFiles.Add(replacementItem);
                }
            });

            locallyPresentFiles = new HashSet<string>(locallyPresentFileSet.Keys, StringComparer.Ordinal);

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList())
            {
                foreach (var gamePath in item.GamePaths)
                {
                    _logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase, gamePath, item.FileSwapPath);
                    moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements", applicationBase);
        }
        if (hasMigrationChanges) _fileDbManager.WriteOutFullCsv();
        st.Stop();
        _logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);

        return new Resolution([.. missingFiles], locallyPresentFiles, moddedDictionary);
    }

    /// <summary>
    /// Vrai si au moins un fichier attendu manque en local et reste téléchargeable.
    /// </summary>
    public bool HasMissingFiles(CharacterData data)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in data.FileReplacements.SelectMany(k => k.Value))
        {
            if (!string.IsNullOrEmpty(replacement.FileSwapPath))
                continue;

            var hash = replacement.Hash;
            if (string.IsNullOrWhiteSpace(hash) || !seen.Add(hash))
                continue;

            var fileCache = _fileDbManager.GetFileCacheByHash(hash);
            if (fileCache is null || !File.Exists(fileCache.ResolvedFilepath))
            {
                if (fileCache is not null)
                    _fileDbManager.RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);

                if (!_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, hash, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }
}
