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