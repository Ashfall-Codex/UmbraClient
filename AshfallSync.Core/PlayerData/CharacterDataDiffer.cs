using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.PlayerData.Data;

namespace UmbraSync.Utils;

public static class CharacterDataDiffer
{
    public static Dictionary<ObjectKind, HashSet<PlayerChanges>> CheckUpdatedData(this CharacterData newData, Guid applicationBase,
        CharacterData? oldData, ILogger logger, string playerLabel, bool forceApplyCustomization, bool forceApplyMods)
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
                    playerLabel, objectKind, hasNewButNotOldFileReplacements, hasOldButNotNewFileReplacements, hasNewButNotOldGlamourerData, hasOldButNotNewGlamourerData, PlayerChanges.ModFiles, PlayerChanges.Glamourer);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Glamourer);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
            }
            else
            {
                if (hasNewAndOldFileReplacements)
                {
                    bool listsAreEqual = oldData.FileReplacements[objectKind].SequenceEqual(newData.FileReplacements[objectKind], FileReplacementDataComparer.Instance);
                    if (!listsAreEqual || forceApplyMods)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (FileReplacements not equal) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.ModFiles);
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ModFiles);
                        // Un changement de fichiers de mod force toujours un redraw
                        charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
                    }
                }

                if (hasNewAndOldGlamourerData)
                {
                    bool glamourerDataDifferent = !string.Equals(oldData.GlamourerData[objectKind], newData.GlamourerData[objectKind], StringComparison.Ordinal);
                    if (glamourerDataDifferent || forceApplyCustomization)
                    {
                        logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Glamourer different) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.Glamourer);
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
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff customize data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.Customize);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Customize);
            }

            if (objectKind != ObjectKind.Player) continue;

            bool manipDataDifferent = !string.Equals(oldData.ManipulationData, newData.ManipulationData, StringComparison.Ordinal);
            if (manipDataDifferent || forceApplyMods)
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff manip data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.ModManip);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ModManip);
                charaDataToUpdate[objectKind].Add(PlayerChanges.ForcedRedraw);
            }

            bool heelsOffsetDifferent = !string.Equals(oldData.HeelsData, newData.HeelsData, StringComparison.Ordinal);
            if (heelsOffsetDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HeelsData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff heels data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.Heels);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Heels);
            }

            bool honorificDataDifferent = !string.Equals(oldData.HonorificData, newData.HonorificData, StringComparison.Ordinal);
            if (honorificDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.HonorificData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff honorific data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.Honorific);
                charaDataToUpdate[objectKind].Add(PlayerChanges.Honorific);
            }

            bool petNamesDataDifferent = !string.Equals(oldData.PetNamesData, newData.PetNamesData, StringComparison.Ordinal);
            if (petNamesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.PetNamesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff petnames data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.PetNames);
                charaDataToUpdate[objectKind].Add(PlayerChanges.PetNames);
            }

            bool moodlesDataDifferent = !string.Equals(oldData.MoodlesData, newData.MoodlesData, StringComparison.Ordinal);
            if (moodlesDataDifferent || (forceApplyCustomization && !string.IsNullOrEmpty(newData.MoodlesData)))
            {
                logger.LogDebug("[BASE-{appBase}] Updating {object}/{kind} (Diff moodles data) => {change}", applicationBase, playerLabel, objectKind, PlayerChanges.Moodles);
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
}
