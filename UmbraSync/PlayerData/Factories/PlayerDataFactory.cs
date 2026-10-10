using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Data;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using CharacterData = UmbraSync.PlayerData.Data.CharacterData;
#pragma warning disable CS8500

namespace UmbraSync.PlayerData.Factories;

public class PlayerDataFactory
{
    private static readonly string[] _allowedExtensionsForGamePaths = [".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk"];
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;
    private readonly ILogger<PlayerDataFactory> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly XivDataAnalyzer _modelAnalyzer;
    private readonly MareMediator _mareMediator;
    private readonly TransientResourceManager _transientResourceManager;

    public PlayerDataFactory(ILogger<PlayerDataFactory> logger, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        TransientResourceManager transientResourceManager, FileCacheManager fileReplacementFactory,
        PerformanceCollectorService performanceCollector, XivDataAnalyzer modelAnalyzer, MareMediator mareMediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _transientResourceManager = transientResourceManager;
        _fileCacheManager = fileReplacementFactory;
        _performanceCollector = performanceCollector;
        _modelAnalyzer = modelAnalyzer;
        _mareMediator = mareMediator;
        _logger.LogTrace("Creating {this}", nameof(PlayerDataFactory));
    }

    // Renvoie false si la construction a échoué : previousData est alors laissé intact.
    public async Task<bool> BuildCharacterData(CharacterData previousData, GameObjectHandler? playerRelatedObject, CancellationToken token)
    {
        if (!_ipcManager.Initialized)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        if (playerRelatedObject == null) return false;

        bool pointerIsZero = true;
        try
        {
            pointerIsZero = playerRelatedObject.Address == IntPtr.Zero;
            try
            {
                pointerIsZero = await CheckForNullDrawObject(playerRelatedObject.Address).ConfigureAwait(false);
            }
            catch
            {
                pointerIsZero = true;
                _logger.LogDebug("NullRef for {object}", playerRelatedObject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data for {object}", playerRelatedObject);
        }

        if (pointerIsZero)
        {
            _logger.LogTrace("Pointer was zero for {objectKind}", playerRelatedObject.ObjectKind);
            previousData.FileReplacements.Remove(playerRelatedObject.ObjectKind);
            previousData.GlamourerString.Remove(playerRelatedObject.ObjectKind);
            previousData.CustomizePlusScale.Remove(playerRelatedObject.ObjectKind);
            return true;
        }

        // Construction dans une copie : en cas d'échec, previousData garde la dernière valeur fiable
        var workingData = CloneForBuild(previousData);

        try
        {
            await _performanceCollector.LogPerformanceAsync(this, $"CreateCharacterData>{playerRelatedObject.ObjectKind}", async () =>
            {
                await CreateCharacterData(workingData, playerRelatedObject, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled creating Character data for {object}", playerRelatedObject);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to create {object} data", playerRelatedObject);
            return false;
        }

        previousData.FileReplacements = workingData.FileReplacements;
        previousData.GlamourerString = workingData.GlamourerString;
        previousData.CustomizePlusScale = workingData.CustomizePlusScale;
        previousData.HeelsData = workingData.HeelsData;
        previousData.HonorificData = workingData.HonorificData;
        previousData.ManipulationString = workingData.ManipulationString;
        previousData.PetNamesData = workingData.PetNamesData;
        previousData.MoodlesData = workingData.MoodlesData;
        return true;
    }

    public static CharacterData CloneForBuild(CharacterData source)
    {
        return new CharacterData
        {
            FileReplacements = source.FileReplacements.ToDictionary(d => d.Key, d => new HashSet<FileReplacement>(d.Value, FileReplacementComparer.Instance)),
            GlamourerString = source.GlamourerString.ToDictionary(d => d.Key, d => d.Value),
            CustomizePlusScale = source.CustomizePlusScale.ToDictionary(d => d.Key, d => d.Value),
            HeelsData = source.HeelsData,
            HonorificData = source.HonorificData,
            ManipulationString = source.ManipulationString,
            PetNamesData = source.PetNamesData,
            MoodlesData = source.MoodlesData,
        };
    }

    private async Task<bool> CheckForNullDrawObject(IntPtr playerPointer)
    {
        return await _dalamudUtil.RunOnFrameworkThread(() => CheckForNullDrawObjectUnsafe(playerPointer)).ConfigureAwait(false);
    }

    private static unsafe bool CheckForNullDrawObjectUnsafe(IntPtr playerPointer)
    {
        return ((Character*)playerPointer)->GameObject.DrawObject == null;
    }

    private async Task<CharacterData> CreateCharacterData(CharacterData previousData, GameObjectHandler playerRelatedObject, CancellationToken token)
    {
        var objectKind = playerRelatedObject.ObjectKind;

        _logger.LogDebug("Building character data for {obj}", playerRelatedObject);

        if (!previousData.FileReplacements.TryGetValue(objectKind, out HashSet<FileReplacement>? value))
        {
            previousData.FileReplacements[objectKind] = new(FileReplacementComparer.Instance);
        }
        else
        {
            value.Clear();
        }

        previousData.CustomizePlusScale.Remove(objectKind);

        // wait until chara is not drawing and present so nothing spontaneously explodes
        await _dalamudUtil.WaitWhileCharacterIsDrawing(_logger, playerRelatedObject, Guid.NewGuid(), 30000, ct: token).ConfigureAwait(false);
        int totalWaitTime = 10000;
        while (!await _dalamudUtil.IsObjectPresentAsync(await _dalamudUtil.CreateGameObjectAsync(playerRelatedObject.Address).ConfigureAwait(false)).ConfigureAwait(false) && totalWaitTime > 0)
        {
            _logger.LogTrace("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, token).ConfigureAwait(false);
            totalWaitTime -= 50;
        }
        Dictionary<string, List<ushort>>? boneIndices =
            objectKind != ObjectKind.Player
            ? null
            : await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetSkeletonBoneIndices(playerRelatedObject)).ConfigureAwait(false);

        DateTime start = DateTime.UtcNow;

        // penumbra call, it's currently broken
        Dictionary<string, HashSet<string>>? resolvedPaths;

        resolvedPaths = await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false);
        if (resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

        previousData.FileReplacements[objectKind] =
                new HashSet<FileReplacement>(resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key)), FileReplacementComparer.Instance)
                .Where(p => p.HasFileReplacement).ToHashSet();
        previousData.FileReplacements[objectKind].RemoveWhere(c => c.GamePaths.Any(g => !_allowedExtensionsForGamePaths.Any(e => g.EndsWith(e, StringComparison.OrdinalIgnoreCase))));

        _logger.LogDebug("== Static Replacements ==");
        foreach (var replacement in previousData.FileReplacements[objectKind].Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("=> {repl}", replacement);
        }

        // if it's pet then it's summoner, if it's summoner we actually want to keep all filereplacements alive at all times
        // or we get into redraw city for every change and nothing works properly
        if (objectKind == ObjectKind.Pet)
        {
            foreach (var item in previousData.FileReplacements[objectKind].Where(i => i.HasFileReplacement).SelectMany(p => p.GamePaths))
            {
                _logger.LogDebug("Persisting {item}", item);
                _transientResourceManager.AddSemiTransientResource(objectKind, item);
            }
        }

        _logger.LogDebug("Handling transient update for {obj}", playerRelatedObject);

        // remove all potentially gathered paths from the transient resource manager that are resolved through static resolving
        _transientResourceManager.ClearTransientPaths(playerRelatedObject.Address, previousData.FileReplacements[objectKind].SelectMany(c => c.GamePaths).ToList());

        // get all remaining paths and resolve them
        var transientPaths = ManageSemiTransientData(objectKind, playerRelatedObject.Address);
        var resolvedTransientPaths = await GetFileReplacementsFromPaths(transientPaths, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);

        _logger.LogDebug("== Transient Replacements ==");
        foreach (var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            _logger.LogDebug("=> {repl}", replacement);
            previousData.FileReplacements[objectKind].Add(replacement);
        }

        // clean up all semi transient resources that don't have any file replacement (aka null resolve)
        _transientResourceManager.CleanUpSemiTransientResources(objectKind, [.. previousData.FileReplacements[objectKind]]);

        // make sure we only return data that actually has file replacements
        foreach (var item in previousData.FileReplacements)
        {
            previousData.FileReplacements[item.Key] = new HashSet<FileReplacement>(item.Value.Where(v => v.HasFileReplacement).OrderBy(v => v.ResolvedPath, StringComparer.Ordinal), FileReplacementComparer.Instance);
        }

        // gather up data from ipc
        previousData.ManipulationString = _ipcManager.Penumbra.GetMetaManipulations();
        Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        Task<string> getGlamourerData = _ipcManager.Glamourer.GetCharacterCustomizationAsync(playerRelatedObject.Address);
        Task<string?> getCustomizeData = _ipcManager.CustomizePlus.GetScaleAsync(playerRelatedObject.Address);
        Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();
        previousData.GlamourerString[playerRelatedObject.ObjectKind] = await getGlamourerData.ConfigureAwait(false);
        _logger.LogDebug("Glamourer is now: {data}", previousData.GlamourerString[playerRelatedObject.ObjectKind]);
        var customizeScale = await getCustomizeData.ConfigureAwait(false);
        previousData.CustomizePlusScale[playerRelatedObject.ObjectKind] = customizeScale ?? string.Empty;
        _logger.LogDebug("Customize is now: {data}", previousData.CustomizePlusScale[playerRelatedObject.ObjectKind]);
        previousData.HonorificData = await getHonorificTitle.ConfigureAwait(false);
        _logger.LogDebug("Honorific is now: {data}", previousData.HonorificData);
        previousData.HeelsData = await getHeelsOffset.ConfigureAwait(false);
        _logger.LogDebug("Heels is now: {heels}", previousData.HeelsData);
        if (objectKind == ObjectKind.Player)
        {
            previousData.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            _logger.LogDebug("Pet Nicknames is now: {petnames}", previousData.PetNamesData);
            previousData.MoodlesData = await _ipcManager.Moodles.GetStatusAsync(playerRelatedObject.Address).ConfigureAwait(false) ?? string.Empty;
        }

        if (previousData.FileReplacements.TryGetValue(objectKind, out HashSet<FileReplacement>? fileReplacements))
        {
            var toCompute = fileReplacements.Where(f => !f.IsFileSwap).ToArray();
            _logger.LogDebug("Getting Hashes for {amount} Files", toCompute.Length);
            var computedPaths = _fileCacheManager.GetFileCachesByPaths(toCompute.Select(c => c.ResolvedPath).ToArray());
            foreach (var file in toCompute)
            {
                file.Hash = computedPaths[file.ResolvedPath]?.Hash ?? string.Empty;
            }
            var removed = fileReplacements.RemoveWhere(f => !f.IsFileSwap && string.IsNullOrEmpty(f.Hash));
            if (removed > 0)
            {
                _logger.LogDebug("Removed {amount} of invalid files", removed);
            }
        }

        if (objectKind == ObjectKind.Player)
        {
            try
            {
                await VerifyPlayerAnimationBones(boneIndices, previousData, objectKind).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to verify player animations, continuing without further verification");
            }
        }

        _logger.LogInformation("Building character data for {obj} took {time}ms", objectKind, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start.Ticks).TotalMilliseconds);

        return previousData;
    }

    private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices, CharacterData previousData, ObjectKind objectKind)
    {
        if (boneIndices == null) return;

        foreach (var kvp in boneIndices)
        {
            _logger.LogDebug("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key, kvp.Value.Any() ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
        }

        if (boneIndices.All(u => u.Value.Count == 0)) return;

        int noValidationFailed = 0;
        foreach (var file in previousData.FileReplacements[objectKind].Where(f => !f.IsFileSwap && f.GamePaths.First().EndsWith("pap", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var skeletonIndices = await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
            bool validationFailed = false;
            if (skeletonIndices != null)
            {
                // 105 is the maximum vanilla skellington spoopy bone index
                if (skeletonIndices.All(k => k.Value.Max() <= 105))
                {
                    _logger.LogTrace("All indices of {path} are <= 105, ignoring", file.ResolvedPath);
                    continue;
                }

                _logger.LogDebug("Verifying bone indices for {path}, found {x} skeletons", file.ResolvedPath, skeletonIndices.Count);

                foreach (var boneCount in skeletonIndices.Select(k => k).ToList())
                {
                    if (boneCount.Value.Max() > boneIndices.SelectMany(b => b.Value).Max())
                    {
                        _logger.LogWarning("Found more bone indices on the animation {path} skeleton {skl} (max indice {idx}) than on any player related skeleton (max indice {idx2})",
                            file.ResolvedPath, boneCount.Key, boneCount.Value.Max(), boneIndices.SelectMany(b => b.Value).Max());
                        validationFailed = true;
                        break;
                    }
                }
            }

            if (validationFailed)
            {
                noValidationFailed++;
                _logger.LogDebug("Removing {file} from sent file replacements and transient data", file.ResolvedPath);
                previousData.FileReplacements[objectKind].Remove(file);
                foreach (var gamePath in file.GamePaths)
                {
                    _transientResourceManager.RemoveTransientResource(objectKind, gamePath);
                }
            }

        }

        if (noValidationFailed > 0)
        {
            _mareMediator.Publish(new NotificationMessage("Invalid Skeleton Setup",
                $"Your client is attempting to send {noValidationFailed} animation files with invalid bone data. Those animation files have been removed from your sent data. " +
                $"Verify that you are using the correct skeleton for those animation files (Check /xllog for more information).",
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) = await _ipcManager.Penumbra.ResolvePathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for (int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for (int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = new List<string>(reverse[i].Select(c => c.ToLowerInvariant()).ToList());
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    private HashSet<string> ManageSemiTransientData(ObjectKind objectKind, IntPtr charaPointer)
    {
        _transientResourceManager.PersistTransientResources(charaPointer, objectKind);

        HashSet<string> pathsToResolve = new(StringComparer.Ordinal);
        foreach (var path in _transientResourceManager.GetSemiTransientResources(objectKind).Where(path => !string.IsNullOrEmpty(path)))
        {
            pathsToResolve.Add(path);
        }

        return pathsToResolve;
    }
}
#pragma warning restore CS8500