using Microsoft.Extensions.Logging.Abstractions;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.PlayerData.Data;
using UmbraSync.Utils;
using Xunit;

namespace AshfallSync.Tests;

public class CharacterDataDifferTests
{
    private static Dictionary<ObjectKind, HashSet<PlayerChanges>> Diff(CharacterData newData, CharacterData? oldData,
        bool forceApplyCustomization = false, bool forceApplyMods = false)
        => newData.CheckUpdatedData(Guid.NewGuid(), oldData, NullLogger.Instance, "test", forceApplyCustomization, forceApplyMods);

    [Fact]
    public void Deux_instantanes_identiques_ne_produisent_aucun_changement()
    {
        var data = Build.Data(Build.Replacement("HASH1", "chara/a.mdl"));
        var same = Build.Data(Build.Replacement("HASH1", "chara/a.mdl"));

        Assert.Empty(Diff(same, data));
    }

    [Fact]
    public void Des_fichiers_apparaissant_pour_la_premiere_fois_declenchent_mods_glamourer_et_redraw()
    {
        var result = Diff(Build.Data(Build.Replacement("HASH1", "chara/a.mdl")), new CharacterData());

        var player = result[ObjectKind.Player];
        Assert.Contains(PlayerChanges.ModFiles, player);
        Assert.Contains(PlayerChanges.Glamourer, player);
        Assert.Contains(PlayerChanges.ForcedRedraw, player);
    }

    [Fact]
    public void Un_hash_de_fichier_different_declenche_mods_et_redraw()
    {
        var oldData = Build.Data(Build.Replacement("HASH1", "chara/a.mdl"));
        var newData = Build.Data(Build.Replacement("HASH2", "chara/a.mdl"));

        var player = Diff(newData, oldData)[ObjectKind.Player];

        Assert.Contains(PlayerChanges.ModFiles, player);
        Assert.Contains(PlayerChanges.ForcedRedraw, player);
    }

    /// <summary>
    /// Les manipulations et les fichiers voyagent ensemble : un changement de manip impose un redraw,
    /// sinon le personnage garde des métadonnées qui ne correspondent plus à ses modèles.
    /// </summary>
    [Fact]
    public void Une_manipulation_differente_declenche_manip_et_redraw()
    {
        var oldData = new CharacterData { ManipulationData = "AAAA" };
        var newData = new CharacterData { ManipulationData = "BBBB" };

        var player = Diff(newData, oldData)[ObjectKind.Player];

        Assert.Contains(PlayerChanges.ModManip, player);
        Assert.Contains(PlayerChanges.ForcedRedraw, player);
    }

    [Fact]
    public void Les_manipulations_ne_concernent_que_le_joueur()
    {
        var oldData = new CharacterData { ManipulationData = "AAAA" };
        var newData = new CharacterData { ManipulationData = "BBBB" };

        var result = Diff(newData, oldData);

        Assert.DoesNotContain(result.Keys, k => k != ObjectKind.Player);
    }

    [Fact]
    public void forceApplyMods_reapplique_meme_quand_les_fichiers_sont_identiques()
    {
        var oldData = Build.Data(Build.Replacement("HASH1", "chara/a.mdl"));
        var newData = Build.Data(Build.Replacement("HASH1", "chara/a.mdl"));

        var player = Diff(newData, oldData, forceApplyMods: true)[ObjectKind.Player];

        Assert.Contains(PlayerChanges.ModFiles, player);
        Assert.Contains(PlayerChanges.ModManip, player);
    }

    [Fact]
    public void Un_glamourer_different_ne_touche_pas_aux_mods()
    {
        var oldData = new CharacterData();
        oldData.GlamourerData[ObjectKind.Player] = "AAAA";
        var newData = new CharacterData();
        newData.GlamourerData[ObjectKind.Player] = "BBBB";

        var player = Diff(newData, oldData)[ObjectKind.Player];

        Assert.Contains(PlayerChanges.Glamourer, player);
        Assert.DoesNotContain(PlayerChanges.ModFiles, player);
    }

    [Fact]
    public void forceApplyCustomization_n_invente_pas_de_donnees_absentes()
    {
        var result = Diff(new CharacterData(), new CharacterData(), forceApplyCustomization: true);

        Assert.Empty(result);
    }

    [Fact]
    public void Chaque_greffon_optionnel_est_diffe_independamment()
    {
        var oldData = new CharacterData();
        var newData = new CharacterData
        {
            HeelsData = "heels",
            HonorificData = "titre",
            MoodlesData = "moodles",
            PetNamesData = "pets",
        };

        var player = Diff(newData, oldData)[ObjectKind.Player];

        Assert.Contains(PlayerChanges.Heels, player);
        Assert.Contains(PlayerChanges.Honorific, player);
        Assert.Contains(PlayerChanges.Moodles, player);
        Assert.Contains(PlayerChanges.PetNames, player);
    }

    [Fact]
    public void Un_ancien_instantane_null_est_traite_comme_vide()
    {
        var result = Diff(Build.Data(Build.Replacement("HASH1", "chara/a.mdl")), null);

        Assert.Contains(PlayerChanges.ModFiles, result[ObjectKind.Player]);
    }
}
