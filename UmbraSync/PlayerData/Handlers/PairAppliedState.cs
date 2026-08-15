using UmbraSync.API.Data;
using UmbraSync.Interop.Ipc.Penumbra;
using ObjectKind = UmbraSync.API.Data.Enum.ObjectKind;

namespace UmbraSync.PlayerData.Handlers;

/// <summary>
/// État mutable partagé d'un pair : ce qui a été reçu, ce qui a été appliqué, et ce qui reste à faire.
/// Regroupé ici pour que l'application, le revert et le suivi de visibilité travaillent sur la même
/// source plutôt que sur une dizaine de champs privés du handler.
/// </summary>
public sealed class PairAppliedState
{
    public CharacterData? CachedData { get; set; }
    public CharacterData? LastAppliedData { get; set; }
    public Dictionary<ObjectKind, Guid?> CustomizeIds { get; } = [];
    /// <summary>Collection Penumbra du pair et index auquel elle est liée.</summary>
    public PenumbraCollectionBinding Penumbra { get; } = new();
    public bool ForceApplyMods { get; set; }
    public bool PendingModReapply { get; set; }
    public Guid Deferred { get; set; } = Guid.Empty;
    public bool RedrawOnNextApplication { get; set; }
    public DateTime? LastDataReceivedAt { get; set; }
    public DateTime? LastApplyAttemptAt { get; set; }
    public DateTime? LastSuccessfulApplyAt { get; set; }
    public string? LastFailureReason { get; set; }
    public IReadOnlyList<string> LastBlockingConditions { get; set; } = Array.Empty<string>();

    /// <summary>Remet à zéro le suivi d'échec après une application réussie.</summary>
    public void ClearFailure()
    {
        LastFailureReason = null;
        LastBlockingConditions = Array.Empty<string>();
    }
}
