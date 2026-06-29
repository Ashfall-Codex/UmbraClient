using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.PlayerData.Data;
using UmbraSync.PlayerData.Redraw;

namespace UmbraSync.Utils;

/// <summary>
/// Règles décidant du niveau de redraw selon les chemins de jeu touchés par un diff de mods.
///
/// Principe : un changement qui touche la géométrie (mdl/skeleton/face/hair/tail/animation) exige
/// un redraw Penumbra complet ; un changement purement texture/material peut se contenter d'une
/// réapplication Glamourer (soft), bien moins coûteuse et sans flicker.
/// </summary>
public static class PairRedrawPathRules
{
    // Marqueurs de chemin imposant un redraw complet (présents n'importe où dans le game path).
    private static readonly string[] FullRedrawPathMarkers =
    [
        "/face/", "/hair/", "/tail/", "/animation/", "/skeleton/",
    ];

    // Extensions imposant un redraw complet (géométrie / sound containers).
    private static readonly string[] FullRedrawExtensions = [".mdl", ".scd"];

    // Extensions « légères » : texture / material uniquement.
    private static readonly string[] TextureOrMaterialExtensions = [".tex", ".mtrl"];

    public static bool IsFullRedrawPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var ext in FullRedrawExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var marker in FullRedrawPathMarkers)
        {
            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Au moins un chemin du set impose un redraw complet.</summary>
    public static bool HasFullRedrawPath(IEnumerable<FileReplacementData> replacements)
    {
        foreach (var replacement in replacements)
        {
            foreach (var path in replacement.GamePaths)
            {
                if (IsFullRedrawPath(path)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tous les chemins du set sont texture/material ET aucun n'impose un redraw complet.
    /// Retourne false dès qu'un chemin full-redraw ou non-tex/mtrl apparaît (prudence).
    /// </summary>
    public static bool HasTextureOrMaterialOnlyPath(IEnumerable<FileReplacementData> replacements)
    {
        bool sawAny = false;
        foreach (var replacement in replacements)
        {
            foreach (var path in replacement.GamePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (IsFullRedrawPath(path)) return false;

                bool isTexOrMtrl = false;
                foreach (var ext in TextureOrMaterialExtensions)
                {
                    if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { isTexOrMtrl = true; break; }
                }
                if (!isTexOrMtrl) return false;
                sawAny = true;
            }
        }
        return sawAny;
    }

    /// <summary>
    /// Diff symétrique des FileReplacements (previous ⊖ current) via le comparer existant.
    /// </summary>
    private static List<FileReplacementData> EnumerateChangedEntries(
        IReadOnlyList<FileReplacementData>? previous, IReadOnlyList<FileReplacementData>? current)
    {
        var prev = previous ?? (IReadOnlyList<FileReplacementData>)Array.Empty<FileReplacementData>();
        var cur = current ?? (IReadOnlyList<FileReplacementData>)Array.Empty<FileReplacementData>();
        var cmp = FileReplacementDataComparer.Instance;

        var changed = new List<FileReplacementData>();
        changed.AddRange(prev.Except(cur, cmp));
        changed.AddRange(cur.Except(prev, cmp));
        return changed;
    }

    /// <summary>
    /// Décision de redraw pour un ObjectKind donné, à partir du diff de mods.
    /// GARDE-FOU : par prudence, en cas de doute on retourne HardRedraw (jamais None ici, l'appelant
    /// ne nous interroge que lorsqu'un redraw est requis). Un soft mal choisi = mod invisible.
    /// </summary>
    public static PairRedrawDecision ResolveModRedrawDecision(
        ObjectKind kind,
        IReadOnlyList<FileReplacementData>? previous,
        IReadOnlyList<FileReplacementData>? current,
        bool manipChanged)
    {
        // Le manip touche IMC/EQP -> géométrie : toujours hard.
        if (manipChanged) return PairRedrawDecision.HardRedraw;

        // Objets non-Player : on reste sur un hard redraw (aligné Lightless, plus sûr).
        if (kind != ObjectKind.Player) return PairRedrawDecision.HardRedraw;

        List<FileReplacementData> changed;
        try
        {
            changed = EnumerateChangedEntries(previous, current);
        }
        catch
        {
            // Tout échec de calcul de diff -> hard (jamais de soft hasardeux).
            return PairRedrawDecision.HardRedraw;
        }

        if (changed.Count == 0)
        {
            // Mod files signalés changés mais diff vide (cas limite) -> hard par prudence.
            return PairRedrawDecision.HardRedraw;
        }

        if (HasFullRedrawPath(changed)) return PairRedrawDecision.HardRedraw;
        if (HasTextureOrMaterialOnlyPath(changed)) return PairRedrawDecision.DeferredSoftReapply;
        return PairRedrawDecision.SoftReapply;
    }
}
