using UmbraSync.Services;
using Xunit;

namespace AshfallSync.Tests;

public class ProfileCardStyleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pas du json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public void Valeur_absente_ou_illisible_donne_les_reglages_par_defaut(string? json)
    {
        Assert.Same(ProfileCardStyle.Default, ProfileCardStyle.Parse(json));
    }

    [Fact]
    public void Json_trop_long_est_ignore()
    {
        var json = "{\"textColor\":\"#FFFFFF\",\"pad\":\"" + new string('x', ProfileCardStyle.MaxJsonLength) + "\"}";

        Assert.Same(ProfileCardStyle.Default, ProfileCardStyle.Parse(json));
    }

    [Fact]
    public void Aller_retour_conserve_les_reglages()
    {
        var style = new ProfileCardStyle { AccentColor = "#2E8B57", TextColor = "#1A2B3C", BannerOpacity = 0.8f, FadeEdges = false, FadeRadius = 0.3f };

        var parsed = ProfileCardStyle.Parse(style.Serialize());

        Assert.Equal(style, parsed);
    }

    [Fact]
    public void Reglages_par_defaut_se_serialisent_en_chaine_vide()
    {
        Assert.Equal(string.Empty, ProfileCardStyle.Default.Serialize());
        Assert.Equal(string.Empty, new ProfileCardStyle { TextColor = "pas une couleur" }.Serialize());
    }

    [Theory]
    [InlineData("{\"bannerOpacity\": 5}", 1f)]
    [InlineData("{\"bannerOpacity\": -2}", 0.1f)]
    [InlineData("{\"bannerOpacity\": 0.7}", 0.7f)]
    public void Opacite_est_bornee(string json, float expected)
    {
        Assert.Equal(expected, ProfileCardStyle.Parse(json).BannerOpacity, 3);
    }

    [Theory]
    [InlineData("{\"fadeRadius\": 3}", 0.9f)]
    [InlineData("{\"fadeRadius\": -1}", 0f)]
    public void Rayon_est_borne(string json, float expected)
    {
        Assert.Equal(expected, ProfileCardStyle.Parse(json).FadeRadius, 3);
    }

    [Theory]
    [InlineData("#ffffff", "#FFFFFF")]
    [InlineData("#1a2B3c", "#1A2B3C")]
    [InlineData("ffffff", null)]
    [InlineData("#fff", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("#-12345", null)]
    public void Couleur_du_texte_invalide_est_ignoree(string color, string? expected)
    {
        var json = "{\"textColor\":\"" + color + "\"}";

        Assert.Equal(expected, ProfileCardStyle.Parse(json).TextColor);
    }

    [Theory]
    [InlineData("#2e8b57", "#2E8B57")]
    [InlineData("vert", null)]
    [InlineData("#12345", null)]
    public void Couleur_du_profil_invalide_est_ignoree(string color, string? expected)
    {
        var json = "{\"accentColor\":\"" + color + "\"}";

        Assert.Equal(expected, ProfileCardStyle.Parse(json).AccentColor);
    }

    [Fact]
    public void Couleur_du_profil_seule_n_est_pas_le_reglage_par_defaut()
    {
        var style = new ProfileCardStyle { AccentColor = "#2E8B57" };

        Assert.False(style.IsDefault);
        Assert.Equal("#2E8B57", ProfileCardStyle.Parse(style.Serialize()).AccentColor);
    }

    [Fact]
    public void Champs_inconnus_et_manquants_sont_toleres()
    {
        var style = ProfileCardStyle.Parse("{\"fadeEdges\": false, \"optionFuture\": 42}");

        Assert.False(style.FadeEdges);
        Assert.Equal(ProfileCardStyle.DefaultBannerOpacity, style.BannerOpacity);
        Assert.Equal(ProfileCardStyle.DefaultFadeRadius, style.FadeRadius);
        Assert.Null(style.TextColor);
        Assert.Null(style.AccentColor);
    }
}
