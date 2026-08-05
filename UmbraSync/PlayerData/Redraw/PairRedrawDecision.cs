namespace UmbraSync.PlayerData.Redraw;

/// <summary>
/// Niveau de redraw à appliquer après un changement de mods, du moins coûteux au plus coûteux.
/// L'ordre numérique sert au merge par sévérité (le plus fort gagne).
/// </summary>
public enum PairRedrawDecision
{
    /// <summary>Rien à faire.</summary>
    None = 0,
    /// <summary>Réapplication Glamourer directe (ReapplyState), sans redraw Penumbra — évite le flicker.</summary>
    SoftReapply = 1,
    /// <summary>Comme SoftReapply mais après quelques frames de settle (changements texture/material seuls).</summary>
    DeferredSoftReapply = 2,
    /// <summary>Redraw Penumbra complet (géométrie : mdl/skeleton/face/hair/tail/manip).</summary>
    HardRedraw = 3,
}
