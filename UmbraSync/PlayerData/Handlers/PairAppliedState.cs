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

    private readonly Lock _reapplyGate = new();
    private bool _pendingModReapply;
    private int _modReapplyGeneration;

    public bool PendingModReapply
    {
        get { lock (_reapplyGate) return _pendingModReapply; }
        set { lock (_reapplyGate) _pendingModReapply = value; }
    }

    public int ModReapplyGeneration
    {
        get { lock (_reapplyGate) return _modReapplyGeneration; }
    }

    /// <summary>
    /// Demande de reprise venue de l'extérieur du pipeline (Penumbra relancé, handler invalidé,
    /// reprise de la main...). Elle incrémente une génération : une application déjà en vol ne
    /// l'efface pas en se terminant, puisqu'elle a démarré avant.
    /// </summary>
    public void RequestModReapply()
    {
        lock (_reapplyGate)
        {
            _modReapplyGeneration++;
            _pendingModReapply = true;
        }
    }

    /// <summary>Efface la reprise après succès, sauf si une demande est arrivée depuis <paramref name="observedGeneration"/>.</summary>
    public void ClearModReapply(int observedGeneration)
    {
        lock (_reapplyGate)
        {
            if (_modReapplyGeneration == observedGeneration)
                _pendingModReapply = false;
        }
    }

    /// <summary>Objets possédés (monture, familier, compagnon) absents au moment de l'application.</summary>
    public HashSet<ObjectKind> PendingOwnedObjects { get; } = [];

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
