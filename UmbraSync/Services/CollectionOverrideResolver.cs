using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;

namespace UmbraSync.Services;

public class CollectionOverrideResolver
{
    private readonly ILogger<CollectionOverrideResolver> _logger;
    private readonly IpcManager _ipcManager;
    private readonly FileCacheManager _fileCacheManager;
    private readonly SyncshellConfigService _syncshellConfigService;
    private readonly PairManager _pairManager;

    public CollectionOverrideResolver(
        ILogger<CollectionOverrideResolver> logger,
        IpcManager ipcManager,
        FileCacheManager fileCacheManager,
        SyncshellConfigService syncshellConfigService,
        PairManager pairManager)
    {
        _logger = logger;
        _ipcManager = ipcManager;
        _fileCacheManager = fileCacheManager;
        _syncshellConfigService = syncshellConfigService;
        _pairManager = pairManager;
    }
    // Vérifie s'il existe au moins un override de collection pour un syncshell actif.
    public bool HasAnyCollectionOverride()
    {
        return _syncshellConfigService.Current.EnableCollectionOverrides
            && _syncshellConfigService.Current.GroupCollectionOverrides.Count > 0;
    }
    
    // Récupère les GIDs des syncshells qui ont un override de collection.
    
    public Dictionary<string, Guid> GetActiveOverrides()
    {
        return _syncshellConfigService.Current.GroupCollectionOverrides;
    }

    
    public (List<UserData> defaultUsers, Dictionary<Guid, List<UserData>> overrideUsers) SplitUsersByCollection(
        List<UserData> allVisibleUsers)
    {
        var overrides = _syncshellConfigService.Current.GroupCollectionOverrides;
        if (overrides.Count == 0)
        {
            return (allVisibleUsers, new Dictionary<Guid, List<UserData>>());
        }

        var defaultUsers = new List<UserData>();
        var overrideUsers = new Dictionary<Guid, List<UserData>>();

        // Récupérer les groupes organisés par paire
        var groupPairs = _pairManager.GroupPairs;

        foreach (var user in allVisibleUsers)
        {
            // Chercher si cet utilisateur appartient à un syncshell avec override
            Guid? collectionOverride = FindCollectionOverrideForUser(user, groupPairs, overrides);

            if (collectionOverride.HasValue)
            {
                if (!overrideUsers.TryGetValue(collectionOverride.Value, out var list))
                {
                    list = new List<UserData>();
                    overrideUsers[collectionOverride.Value] = list;
                }
                list.Add(user);
            }
            else
            {
                defaultUsers.Add(user);
            }
        }

        return (defaultUsers, overrideUsers);
    }


    public async Task<CharacterData?> BuildAlternativeCharacterData(
        CharacterData defaultData,
        Guid collectionId)
    {
        if (!_ipcManager.Initialized || !_ipcManager.Penumbra.APIAvailable)
        {
            _logger.LogWarning("Penumbra non disponible, impossible de résoudre la collection {collId}", collectionId);
            return null;
        }

        // Collecter tous les game paths depuis les FileReplacements par défaut
        var allGamePaths = defaultData.FileReplacements
            .SelectMany(kvp => kvp.Value)
            .SelectMany(fr => fr.GamePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allGamePaths.Length == 0)
        {
            _logger.LogDebug("Aucun game path à résoudre pour la collection {collId}", collectionId);
            return null;
        }

        // Résoudre les game paths contre la collection cible
        var (ec, resolvedForward, _) = await _ipcManager.Penumbra.ResolvePathsForCollectionAsync(
            collectionId, allGamePaths, []).ConfigureAwait(false);

        if (ec != global::Penumbra.Api.Enums.PenumbraApiEc.Success)
        {
            _logger.LogWarning("Échec de la résolution des paths pour la collection {collId}: {ec}", collectionId, ec);
            return null;
        }

        // Construire le mapping gamePath → resolvedPath pour la nouvelle collection
        var newResolutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allGamePaths.Length && i < resolvedForward.Length; i++)
        {
            var resolved = resolvedForward[i];
            if (!string.IsNullOrEmpty(resolved) &&
                !string.Equals(allGamePaths[i], resolved, StringComparison.OrdinalIgnoreCase))
            {
                newResolutions[allGamePaths[i]] = resolved;
            }
        }

        // Construire les nouveaux FileReplacements par ObjectKind
        var newFileReplacements = new Dictionary<ObjectKind, List<FileReplacementData>>();

        foreach (var kvp in defaultData.FileReplacements)
        {
            var objectKind = kvp.Key;
            var newReplacements = new List<FileReplacementData>();

            foreach (var originalFr in kvp.Value)
            {
                // Pour les file swaps, les garder tels quels
                if (!string.IsNullOrEmpty(originalFr.FileSwapPath))
                {
                    newReplacements.Add(originalFr);
                    continue;
                }

                // Récupérer les game paths de ce remplacement qui ont une résolution dans la nouvelle collection
                var resolvedGamePaths = originalFr.GamePaths
                    .Where(gp => newResolutions.ContainsKey(gp))
                    .ToArray();

                if (resolvedGamePaths.Length == 0) continue;

                newReplacements.Add(new FileReplacementData
                {
                    GamePaths = resolvedGamePaths,
                    Hash = string.Empty, // Sera rempli après
                });
            }

            newFileReplacements[objectKind] = newReplacements;
        }

        // Récupérer les hashes des fichiers résolus
        var allResolvedPaths = newResolutions.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (allResolvedPaths.Length > 0)
        {
            var cacheEntries = _fileCacheManager.GetFileCachesByPaths(allResolvedPaths);

            // Remplir les hashes et nettoyer les entrées sans hash
            foreach (var kvp in newFileReplacements)
            {
                var toRemove = new List<FileReplacementData>();
                foreach (var fr in kvp.Value)
                {
                    if (!string.IsNullOrEmpty(fr.FileSwapPath)) continue;
                    if (fr.GamePaths.Length == 0) continue;

                    var resolvedPath = newResolutions.TryGetValue(fr.GamePaths[0], out var rp) ? rp : null;
                    if (resolvedPath != null && cacheEntries.TryGetValue(resolvedPath, out var cacheEntry) && cacheEntry != null)
                    {
                        fr.Hash = cacheEntry.Hash;
                    }
                    else
                    {
                        toRemove.Add(fr);
                    }
                }

                foreach (var item in toRemove)
                {
                    kvp.Value.Remove(item);
                }
            }
        }

        // Construire le CharacterData alternatif (mêmes données IPC, fichiers différents)
        var alternativeData = new CharacterData
        {
            FileReplacements = newFileReplacements,
            GlamourerData = defaultData.GlamourerData,
            ManipulationData = defaultData.ManipulationData,
            HeelsData = defaultData.HeelsData,
            CustomizePlusData = defaultData.CustomizePlusData,
            HonorificData = defaultData.HonorificData,
            PetNamesData = defaultData.PetNamesData,
            MoodlesData = defaultData.MoodlesData,
        };

        var totalFiles = newFileReplacements.Values.Sum(v => v.Count);
        _logger.LogInformation("CharacterData alternatif construit pour collection {collId}: {count} fichiers", collectionId, totalFiles);

        return alternativeData;
    }
    
    private static Guid? FindCollectionOverrideForUser(
        UserData user,
        Dictionary<API.Dto.Group.GroupFullInfoDto, List<Pair>> groupPairs,
        Dictionary<string, Guid> overrides)
    {
        // Parcourir tous les groupes pour trouver ceux où cet utilisateur est membre
        foreach (var (groupInfo, pairs) in groupPairs)
        {
            // Vérifier si ce groupe a un override de collection
            if (!overrides.TryGetValue(groupInfo.Group.GID, out var collectionId))
                continue;

            // Vérifier si l'utilisateur est dans ce groupe
            if (pairs.Any(p => string.Equals(p.UserData.UID, user.UID, StringComparison.Ordinal)))
            {
                return collectionId;
            }
        }

        return null;
    }
}
