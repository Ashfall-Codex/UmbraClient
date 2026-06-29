using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.PlayerData.Redraw;
using PlayerChanges = UmbraSync.PlayerData.Data.PlayerChanges;

namespace UmbraSync.Utils;

public static class VariousExtensions
{
    public static string ToByteString(this int bytes, bool addSuffix = true)
    {
        string[] suffix = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return addSuffix ? $"{dblSByte:0.00} {suffix[i]}" : $"{dblSByte:0.00}";
    }

    public static string ToByteString(this long bytes, bool addSuffix = true)
    {
        string[] suffix = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return addSuffix ? $"{dblSByte:0.00} {suffix[i]}" : $"{dblSByte:0.00}";
    }

    public static void CancelDispose(this CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // swallow it
        }
    }

    public static CancellationTokenSource CancelRecreate(this CancellationTokenSource? cts)
    {
        cts?.CancelDispose();
        return new CancellationTokenSource();
    }

    public static Dictionary<ObjectKind, HashSet<PlayerChanges>> CheckUpdatedData(this CharacterData newData, Guid applicationBase,
        CharacterData? oldData, ILogger logger, PairHandler cachedPlayer, bool forceApplyCustomization, bool forceApplyMods)
    {
        oldData ??= new();
        var charaDataToUpdate = new Dictionary<ObjectKind, HashSet<PlayerChanges>>();
        foreach (ObjectKind objectKind in Enum.GetValues<ObjectKind>())
        {
            charaDataToUpdate[objectKind] = [];
            oldData.FileReplacements.TryGetValue(objectKind, out var existingFileReplacements);
            newData.FileReplacements.TryGetValue(objectKind, out var newFileReplacements);
            oldData.GlamourerData.TryGetValue(objectKind, out var existingGlamourerData);
            newData.GlamourerData.TryGetValue(objectKind, out var newGlamourerData);

            bool hasNewButNotOldFileReplacements = newFileReplacements != null && existingFileReplacements == null;
            bool hasOldButNotNewFileReplacements = existingFileReplacements != null && newFileReplacements == null;

            bool hasNewButNotOldGlamourerData = newGlamourerData != null && existingGlamourerData == null;
            bool hasOldButNotNewGlamourerData = existingGlamourerData != null && newGlamourerData == null;

            bool hasNewAndOldFileReplacements = newFileReplacements != null && existingFileReplacements != null;
            bool hasNewAndOldGlamourerData = newGlamourerData != null && existingGlamourerData != null;

            if (hasNewButNotOldFileReplacements || hasOldButNotNewFileReplacements || hasNewButNotOldGlamourerData || hasOldButNotNewGlamourerData)
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Some new data arrived: NewButNotOldFiles:{hasNewButNotOldFileReplacements}," +
                    " OldButNotNewFiles:{hasOldButNotNewFileReplacements}, NewButNotOldGlam:{hasNewButNotOldGlamourerData}, OldButNotNewGlam:{hasOldButNotNewGlamourerData}) => {change}, {change2}",
                    applicationBase,
                    cachedPlayer, objectKind, hasNewButNotOldFileReplacements, hasOldButNotNewFileReplacements, hasNewButNotOldGlamourerData, hasOldButNotNewGlamourerData, PlayerChanges.ModFiles, PlayerChanges.Glamourer);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Glamourer);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
            }
            else
            {
                if (hasNewAndOldFileReplacements)
                {
                    bool listsAreEqual = oldData.FileReplacements[objectKind].SequenceEqual(newData.FileReplacements[objectKind], PlayerData.Data.FileReplacementDataComparer.Instance);
                    if (!listsAreEqual || forceApplyMods)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (FileReplacements not equal) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.ModFiles);
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                        // XXX: This logic is disabled disabled because it seems to skip redrawing for something as basic as toggling a gear mod
#if false
                        if (forceApplyMods || objectKind != ObjectKind.Player)
                        {
                            charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                        }
                        else
                        {
                            var existingFace = existingFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/face/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var existingHair = existingFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/hair/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var existingTail = existingFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/tail/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var newFace = newFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/face/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var newHair = newFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/hair/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var newTail = newFileReplacements.Where(g => g.GamePaths.Any(p => p.Contains("/tail/", StringComparison.OrdinalIgnoreCase)))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var existingTransients = existingFileReplacements.Where(g => g.GamePaths.Any(g => !g.EndsWith("mdl") && !g.EndsWith("tex") && !g.EndsWith("mtrl")))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();
                            var newTransients = newFileReplacements.Where(g => g.GamePaths.Any(g => !g.EndsWith("mdl") && !g.EndsWith("tex") && !g.EndsWith("mtrl")))
                                .OrderBy(g => string.IsNullOrEmpty(g.Hash) ? g.FileSwapPath : g.Hash, StringComparer.OrdinalIgnoreCase).ToList();

                            logger.LogTrace("[BASE-{appbase}] ExistingFace: {of}, NewFace: {fc}; ExistingHair: {eh}, NewHair: {nh}; ExistingTail: {et}, NewTail: {nt}; ExistingTransient: {etr}, NewTransient: {ntr}", applicationBase,
                                existingFace.Count, newFace.Count, existingHair.Count, newHair.Count, existingTail.Count, newTail.Count, existingTransients.Count, newTransients.Count);

                            var differentFace = !existingFace.SequenceEqual(newFace, PlayerData.Data.FileReplacementDataComparer.Instance);
                            var differentHair = !existingHair.SequenceEqual(newHair, PlayerData.Data.FileReplacementDataComparer.Instance);
                            var differentTail = !existingTail.SequenceEqual(newTail, PlayerData.Data.FileReplacementDataComparer.Instance);
                            var differenTransients = !existingTransients.SequenceEqual(newTransients, PlayerData.Data.FileReplacementDataComparer.Instance);
                            if (differentFace || differentHair || differentTail || differenTransients)
                            {
                                logger.LogDebug("[BASE-{appbase}] Different Subparts: Face: {face}, Hair: {hair}, Tail: {tail}, Transients: {transients} => {change}", applicationBase,
                                    differentFace, differentHair, differentTail, differenTransients, PlayerChanges.ForcedRedraw);
                                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                            }
                        }
#endif
                        // XXX: Redraw on mod file changes always
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                    }
                }

                if (hasNewAndOldGlamourerData)
                {
                    bool glamourerDataDifferent = !string.Equals(oldData.GlamourerData[objectKind], newData.GlamourerData[objectKind], StringComparison.Ordinal);
                    if (glamourerDataDifferent || forceApplyCustomization)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Glamourer different) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Glamourer);
                        charaDataToUpdate[objectKind].Add(PlayerChanges.Glamourer);
                    }
                }
            }

            oldData.CustomizePlusData.TryGetValue(objectKind, out var oldCustomizePlusData);
            newData.CustomizePlusData.TryGetValue(objectKind, out var newCustomizePlusData);

            oldCustomizePlusData ??= string.Empty;
            newCustomizePlusData ??= string.Empty;

            bool customizeDataDifferent = !string.Equals(oldCustomizePlusData, newCustomizePlusData, StringComparison.Ordinal);
            if (customizeDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newCustomizePlusData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff customize data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Customize);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Customize);
            }

            if (objectKind != ObjectKind.Player) continue;

            bool manipDataDifferent = !string.Equals(oldData.ManipulationData, newData.ManipulationData, StringComparison.Ordinal);
            if (manipDataDifferent || forceApplyMods)
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff manip data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.ModManip);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ModManip);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
            }

            bool heelsOffsetDifferent = !string.Equals(oldData.HeelsData, newData.HeelsData, StringComparison.Ordinal);
            if (heelsOffsetDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HeelsData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff heels data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Heels);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Heels);
            }

            bool honorificDataDifferent = !string.Equals(oldData.HonorificData, newData.HonorificData, StringComparison.Ordinal);
            if (honorificDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HonorificData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff honorific data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Honorific);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Honorific);
            }

            bool petNamesDataDifferent = !string.Equals(oldData.PetNamesData, newData.PetNamesData, StringComparison.Ordinal);
            if (petNamesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.PetNamesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff petnames data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.PetNames);
                charaDataToUpdate[objectKind].Add(PlayerChanges.PetNames);
            }

            bool moodlesDataDifferent = !string.Equals(oldData.MoodlesData, newData.MoodlesData, StringComparison.Ordinal);
            if (moodlesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.MoodlesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff moodles data) => {change}", applicationBase, cachedPlayer, objectKind, PlayerChanges.Moodles);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Moodles);
            }
        }

        foreach (KeyValuePair<ObjectKind, HashSet<PlayerChanges>> data in charaDataToUpdate.ToList())
        {
            if (!data.Value.Any()) charaDataToUpdate.Remove(data.Key);
            else charaDataToUpdate[data.Key] = [.. data.Value.OrderByDescending(p => (int)p)];
        }

        return charaDataToUpdate;
    }

    /// <summary>
    /// Calcule, pour chaque ObjectKind qui requiert un ForcedRedraw, le niveau de redraw approprié
    /// (Hard/Soft/DeferredSoft) à partir du même diff old/new que <see cref="CheckUpdatedData"/>.
    /// N'est consommé que si EnableSoftRedraw est ON ; sinon l'appelant force HardRedraw.
    /// GARDE-FOUS : nouvelles données complètes (kind présent d'un seul côté) ou pas d'ancien snapshot
    /// ou manip changé -> HardRedraw. Le reste est délégué à <see cref="PairRedrawPathRules"/> qui
    /// retombe sur HardRedraw en cas de doute.
    /// </summary>
    public static Dictionary<ObjectKind, PairRedrawDecision> ComputeRedrawDecisions(this CharacterData newData,
        CharacterData? oldData, Dictionary<ObjectKind, HashSet<PlayerChanges>> changes)
    {
        var decisions = new Dictionary<ObjectKind, PairRedrawDecision>();

        foreach (var (kind, kindChanges) in changes)
        {
            if (!kindChanges.Contains(PlayerChanges.ForcedRedraw)) continue;

            // Pas de baseline -> on ne peut pas raisonner sur un diff : hard.
            if (oldData == null)
            {
                decisions[kind] = PairRedrawDecision.HardRedraw;
                continue;
            }

            bool hasOld = oldData.FileReplacements.TryGetValue(kind, out var oldRepl);
            bool hasNew = newData.FileReplacements.TryGetValue(kind, out var newRepl);

            // Arrivée/disparition complète des FileReplacements pour ce kind (cas "new but not old"
            // de CheckUpdatedData) -> premier chargement complet : hard.
            if (!(hasOld && hasNew))
            {
                decisions[kind] = PairRedrawDecision.HardRedraw;
                continue;
            }

            bool manipChanged = kind == ObjectKind.Player && kindChanges.Contains(PlayerChanges.ModManip);
            decisions[kind] = PairRedrawPathRules.ResolveModRedrawDecision(kind, oldRepl, newRepl, manipChanged);
        }

        return decisions;
    }

    public static T DeepClone<T>(this T obj)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj))!;
    }

    public static unsafe int? ObjectTableIndex(this IGameObject? gameObject)
    {
        if (gameObject == null || gameObject.Address == IntPtr.Zero)
        {
            return null;
        }

        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address)->ObjectIndex;
    }
}