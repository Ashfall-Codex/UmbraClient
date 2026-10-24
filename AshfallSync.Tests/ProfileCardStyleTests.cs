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

    [Fact]
    public void Fond_de_la_liste_est_conserve()
    {
        var style = new ProfileCardStyle { Background = 193720, BackgroundVeil = 0.3f };

        var parsed = ProfileCardStyle.Parse(style.Serialize());

        Assert.Equal(style, parsed);
        Assert.True(parsed.HasBackground);
    }

    [Theory]
    [InlineData("{\"background\": 12345}")]
    [InlineData("{\"background\": -193720}")]
    [InlineData("{\"background\": 0}")]
    public void Fond_hors_liste_est_ignore(string json)
    {
        var style = ProfileCardStyle.Parse(json);

        Assert.Equal(0, style.Background);
        Assert.False(style.HasBackground);
    }

    [Fact]
    public void Fond_hors_liste_seul_se_serialise_en_chaine_vide()
    {
        Assert.Equal(string.Empty, new ProfileCardStyle { Background = 12345 }.Serialize());
    }

    [Theory]
    [InlineData("{\"backgroundVeil\": 4}", 0.9f)]
    [InlineData("{\"backgroundVeil\": -1}", 0f)]
    [InlineData("{\"backgroundVeil\": 0.2}", 0.2f)]
    public void Voile_est_borne(string json, float expected)
    {
        Assert.Equal(expected, ProfileCardStyle.Parse(json).BackgroundVeil, 3);
    }

    [Fact]
    public void Fondu_du_fond_actif_par_defaut()
    {
        Assert.True(ProfileCardStyle.Parse("{\"background\": 193720}").BackgroundFade);
    }

    [Fact]
    public void Fondu_du_fond_desactive_est_conserve()
    {
        var style = new ProfileCardStyle { Background = 193720, BackgroundFade = false, BackgroundVeil = 0.6f };

        var parsed = ProfileCardStyle.Parse(style.Serialize());

        Assert.Equal(style, parsed);
        Assert.False(parsed.BackgroundFade);
    }

    [Theory]
    [InlineData("{\"backgroundDimming\": 4}", 0.9f)]
    [InlineData("{\"backgroundDimming\": -1}", 0f)]
    [InlineData("{\"backgroundDimming\": 0.35}", 0.35f)]
    [InlineData("{\"background\": 193720}", 0f)]
    public void Assombrissement_du_fond_est_borne(string json, float expected)
    {
        Assert.Equal(expected, ProfileCardStyle.Parse(json).BackgroundDimming, 3);
    }

    [Fact]
    public void Couleurs_de_la_feuille_sont_normalisees()
    {
        var style = ProfileCardStyle.Parse("{\"sheetTitleColor\": \"#c8a060\", \"sheetLabelColor\": \"rouge\", \"sheetTextColor\": \"#12345\"}");

        Assert.Equal("#C8A060", style.SheetTitleColor);
        Assert.Null(style.SheetLabelColor);
        Assert.Null(style.SheetTextColor);
    }

    [Fact]
    public void Couleurs_de_la_feuille_sont_conservees()
    {
        var style = new ProfileCardStyle
        {
            Background = 193720, SheetTitleColor = "#C8A060", SheetLabelColor = "#806040", SheetTextColor = "#201810",
        };

        Assert.Equal(style, ProfileCardStyle.Parse(style.Serialize()));
    }

    [Fact]
    public void Liste_des_fonds_sans_doublon()
    {
        Assert.Equal(ProfileBackgrounds.All.Count, ProfileBackgrounds.All.Select(b => b.IconId).Distinct().Count());
        Assert.Equal(ProfileBackgrounds.All.Count, ProfileBackgrounds.All.Select(b => b.Key).Distinct().Count());
    }
}
