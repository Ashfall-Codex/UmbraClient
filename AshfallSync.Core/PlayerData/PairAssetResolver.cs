using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using UmbraSync.API.Data;
using UmbraSync.Core.Abstractions;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration.Models;

namespace UmbraSync.PlayerData.Handlers;

/// <summary>
/// Traduit les remplacements de fichiers annoncés par un pair en redirections Penumbra exploitables,
/// et signale ce qui manque encore en local. Ne touche ni au jeu ni aux IPC.
/// </summary>
public sealed class PairAssetResolver
{
    private readonly ILogger _logger;
    private readonly UserData _userData;
    private readonly IFileCacheLookup _fileCache;
    private readonly CompressedAlternateManager _compressedAlternateManager;
    private readonly IForbiddenTransferRegistry _forbiddenTransfers;
    private readonly ITextureCompressionSettings _compressionSettings;

    public PairAssetResolver(ILogger logger, UserData userData, IFileCacheLookup fileCache,
        CompressedAlternateManager compressedAlternateManager, IForbiddenTransferRegistry forbiddenTransfers,
        ITextureCompressionSettings compressionSettings)
    {
        _logger = logger;
        _userData = userData;
        _fileCache = fileCache;
        _compressedAlternateManager = compressedAlternateManager;
        _forbiddenTransfers = forbiddenTransfers;
        _compressionSettings = compressionSettings;
    }

    /// <param name="MissingFiles">Remplacements dont le fichier n'est pas (encore) en cache local.</param>
    /// <param name="LocallyPresentFiles">
    /// Hashes déjà présents en local envoyés au download uniquement pour découvrir un alternate
    /// (mode AlwaysCompressed) : si aucun alt n'existe, le download ne les re-téléchargera pas.
    /// </param>
    /// <param name="ModdedPaths">Redirections à poser dans Penumbra : (gamePath, hash source) → fichier réel.</param>
    public sealed record Resolution(
        List<FileReplacementData> MissingFiles,
        HashSet<string> LocallyPresentFiles,
        Dictionary<(string GamePath, string? Hash), string> ModdedPaths);

    /// <summary>Mode de compression pour ce pair : override par UID sinon config globale.</summary>
    public TextureCompressionMode ComputeCompressedAlternateUsage()
    {
        foreach (var uid in _compressionSettings.UidsToOverride)
        {
            if (string.Equals(uid, _userData.UID, StringComparison.Ordinal)
                || string.Equals(uid, _userData.Alias, StringComparison.Ordinal))
            {
                return TextureCompressionMode.AlwaysSourceQuality;
            }
        }

        return _compressionSettings.Mode;
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
            // Un remplacement sans swap ET sans hash ne désigne aucun fichier : il ne sera jamais
            // résolu ni téléchargeable. Le compter comme manquant fait boucler la boucle de download
            // sur un hash vide et envoie des getFileSizes([""]) au serveur jusqu'à épuisement des
            // tentatives. HasMissingFiles filtrait déjà ce cas, pas Resolve.
            var replacementList = charaData.FileReplacements
                .SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath)))
                .Where(v => !string.IsNullOrWhiteSpace(v.Hash))
                .ToList();

            var blankHashes = charaData.FileReplacements
                .SelectMany(k => k.Value)
                .Count(v => string.IsNullOrEmpty(v.FileSwapPath) && string.IsNullOrWhiteSpace(v.Hash));
            if (blankHashes > 0)
            {
                _logger.LogWarning("[BASE-{appBase}] {count} remplacement(s) ignoré(s) : ni chemin de swap ni hash",
                    applicationBase, blankHashes);
            }

            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();

                var replacementItem = item;
                var fileCache = _fileCache.GetByHash(item.Hash, preferSubst: true);
                // "confirmed" = on connaît le statut d'alternate de ce hash (existe ou n'existera jamais).
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
                        fileCache = _fileCache.GetByHash(altHash, preferSubst: true);
                    }
                }
                else // AlwaysCompressed
                {
                    if (confirmed)
                    {
                        // On sait : s'il y a un alt on l'utilise (même si la source est en local -> gain VRAM), sinon source.
                        if (altHash != null)
                        {
                            replacementItem = new FileReplacementData { GamePaths = item.GamePaths, Hash = altHash };
                            fileCache = _fileCache.GetByHash(altHash, preferSubst: true);
                        }
                    }
                    else
                    {
                        // Statut inconnu : envoyer la source au download pour découvrir un alt, mais la marquer "déjà présente"
                        // pour ne pas la re-télécharger si aucun alt n'existe.
                        locallyPresentFileSet[item.Hash] = 0;
                        fileCache = null;
                    }
                }

                if (fileCache != null)
                {
                    if (string.IsNullOrEmpty(Path.GetExtension(fileCache.ResolvedFilepath)))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileCache.MigrateToExtension(fileCache, replacementItem.GamePaths[0].Split(".")[^1]);
                    }

                    // Clé = gamePath + hash original ; valeur = fichier réel (source ou BC7 selon substitution).
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
        if (hasMigrationChanges) _fileCache.Flush();
        st.Stop();
        _logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);

        return new Resolution([.. missingFiles], locallyPresentFiles, moddedDictionary);
    }

    /// <summary>
    /// Vrai si au moins un fichier attendu manque en local et reste téléchargeable. Purge au passage
    /// les entrées de cache dont le fichier a disparu du disque.
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

            var fileCache = _fileCache.GetByHash(hash);
            if (fileCache is null || !_fileCache.Exists(fileCache))
            {
                if (fileCache is not null)
                    _fileCache.Remove(fileCache.Hash, fileCache.PrefixedFilePath);

                if (!_forbiddenTransfers.IsForbidden(hash))
                    return true;
            }
        }

        return false;
    }
}
