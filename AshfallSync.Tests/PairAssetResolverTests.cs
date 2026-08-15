using Microsoft.Extensions.Logging.Abstractions;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Handlers;
using Xunit;

namespace AshfallSync.Tests;

public class PairAssetResolverTests
{
    private static PairAssetResolver Create(FakeFileCache cache,
        FakeForbiddenTransfers? forbidden = null,
        FakeCompressionSettings? compression = null,
        string uid = "UID1", string alias = "")
        => new(NullLogger.Instance,
            new UserData(uid, string.IsNullOrEmpty(alias) ? null : alias),
            cache,
            new CompressedAlternateManager(),
            forbidden ?? new FakeForbiddenTransfers(),
            compression ?? new FakeCompressionSettings());

    private static PairAssetResolver.Resolution Resolve(PairAssetResolver resolver, CharacterData data,
        TextureCompressionMode mode = TextureCompressionMode.AlwaysSourceQuality)
        => resolver.Resolve(Guid.NewGuid(), data, mode, CancellationToken.None);

    [Fact]
    public void Fichier_present_produit_une_redirection()
    {
        var cache = new FakeFileCache().With("HASH1", "/cache/hash1.mdl");
        var data = Build.Data(Build.Replacement("HASH1", "chara/human/c0101/hair.mdl"));

        var result = Resolve(Create(cache), data);

        Assert.Empty(result.MissingFiles);
        Assert.Equal("/cache/hash1.mdl", result.ModdedPaths[("chara/human/c0101/hair.mdl", "HASH1")]);
    }
    
    [Fact]
    public void Fichier_absent_ne_produit_aucune_redirection()
    {
        var cache = new FakeFileCache().With("PRESENT", "/cache/present.mdl");
        var data = Build.Data(
            Build.Replacement("PRESENT", "chara/present.mdl"),
            Build.Replacement("ABSENT", "chara/absent.mdl"));

        var result = Resolve(Create(cache), data);

        Assert.Single(result.MissingFiles);
        Assert.Equal("ABSENT", result.MissingFiles[0].Hash);
        Assert.True(result.ModdedPaths.ContainsKey(("chara/present.mdl", "PRESENT")));
        Assert.DoesNotContain(result.ModdedPaths.Keys, k => k.GamePath == "chara/absent.mdl");
    }
    
    [Fact]
    public void Un_remplacement_sans_hash_ni_swap_est_ignore()
    {
        var cache = new FakeFileCache().With("PRESENT", "/cache/present.mdl");
        var data = Build.Data(
            Build.Replacement("PRESENT", "chara/present.mdl"),
            Build.Replacement(string.Empty, "chara/vide.mdl"));

        var result = Resolve(Create(cache), data);

        Assert.Empty(result.MissingFiles);
        Assert.Single(result.ModdedPaths);
    }

    [Fact]
    public void HasMissingFiles_ignore_aussi_les_remplacements_sans_hash()
    {
        var data = Build.Data(Build.Replacement("   ", "chara/vide.mdl"));

        Assert.False(Create(new FakeFileCache()).HasMissingFiles(data));
    }

    [Fact]
    public void Un_hash_couvre_tous_ses_game_paths()
    {
        var cache = new FakeFileCache().With("SHARED", "/cache/shared.tex");
        var data = Build.Data(Build.Replacement("SHARED", "chara/a.tex", "chara/b.tex", "chara/c.tex"));

        var result = Resolve(Create(cache), data);

        Assert.Equal(3, result.ModdedPaths.Count);
        Assert.All(result.ModdedPaths.Values, v => Assert.Equal("/cache/shared.tex", v));
    }

    [Fact]
    public void Les_file_swaps_passent_sans_toucher_au_cache()
    {
        var data = Build.Data(Build.Swap("chara/vanilla/target.mdl", "chara/source.mdl"));

        var result = Resolve(Create(new FakeFileCache()), data);

        Assert.Empty(result.MissingFiles);
        Assert.Equal("chara/vanilla/target.mdl", result.ModdedPaths[("chara/source.mdl", null)]);
    }

    [Fact]
    public void Extension_manquante_declenche_une_migration_et_un_flush()
    {
        var cache = new FakeFileCache().With("NOEXT", "/cache/noext");
        var data = Build.Data(Build.Replacement("NOEXT", "chara/thing.mdl"));

        var result = Resolve(Create(cache), data);

        Assert.Equal(1, cache.FlushCount);
        Assert.Equal("/cache/noext.mdl", result.ModdedPaths[("chara/thing.mdl", "NOEXT")]);
    }

    [Fact]
    public void HasMissingFiles_ignore_les_transferts_interdits()
    {
        var data = Build.Data(Build.Replacement("FORBIDDEN", "chara/x.mdl"));
        var resolver = Create(new FakeFileCache(), new FakeForbiddenTransfers("FORBIDDEN"));

        Assert.False(resolver.HasMissingFiles(data));
    }

    [Fact]
    public void HasMissingFiles_signale_un_fichier_telechargeable()
    {
        var data = Build.Data(Build.Replacement("RETRIABLE", "chara/x.mdl"));

        Assert.True(Create(new FakeFileCache()).HasMissingFiles(data));
    }

    [Fact]
    public void HasMissingFiles_purge_les_entrees_fantomes()
    {
        var cache = new FakeFileCache().With("GHOST", "/cache/ghost.mdl", existsOnDisk: false);
        var data = Build.Data(Build.Replacement("GHOST", "chara/x.mdl"));

        Assert.True(Create(cache).HasMissingFiles(data));
        Assert.Equal(["GHOST"], cache.Removed);
    }

    [Fact]
    public void Un_uid_surcharge_force_la_qualite_source()
    {
        var compression = new FakeCompressionSettings
        {
            Mode = TextureCompressionMode.AlwaysCompressed,
            UidsToOverride = ["UID1"],
        };

        var resolver = Create(new FakeFileCache(), compression: compression);

        Assert.Equal(TextureCompressionMode.AlwaysSourceQuality, resolver.ComputeCompressedAlternateUsage());
    }

    [Fact]
    public void L_alias_est_reconnu_par_la_surcharge_au_meme_titre_que_l_uid()
    {
        var compression = new FakeCompressionSettings
        {
            Mode = TextureCompressionMode.AlwaysCompressed,
            UidsToOverride = ["MonAlias"],
        };

        var resolver = Create(new FakeFileCache(), compression: compression, uid: "UID1", alias: "MonAlias");

        Assert.Equal(TextureCompressionMode.AlwaysSourceQuality, resolver.ComputeCompressedAlternateUsage());
    }

    [Fact]
    public void Sans_surcharge_le_mode_global_s_applique()
    {
        var compression = new FakeCompressionSettings { Mode = TextureCompressionMode.AlwaysCompressed };

        var resolver = Create(new FakeFileCache(), compression: compression);

        Assert.Equal(TextureCompressionMode.AlwaysCompressed, resolver.ComputeCompressedAlternateUsage());
    }
}
