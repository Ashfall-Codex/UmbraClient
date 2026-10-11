using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.MareConfiguration;

public static class ConfigurationExtensions
{
    public static bool HasValidSetup(this MareConfig configuration)
    {
        return configuration.AcceptedAgreement && configuration.InitialScanComplete
                    && !string.IsNullOrEmpty(configuration.CacheFolder)
                    && Directory.Exists(configuration.CacheFolder);
    }

    // Installation déjà configurée dont le tour de nouveautés de cette version n'a pas encore été vu.
    // Une installation neuve passe par l'assistant d'installation et n'est pas concernée.
    public static bool HasPendingWhatsNewTour(this MareConfig configuration)
    {
        return configuration.HasValidSetup()
            && !string.Equals(configuration.WhatsNewTourSeen, MareConfig.CurrentWhatsNewTourVersion, StringComparison.Ordinal);
    }

    public static bool HasValidRgpdConsent(this MareConfig configuration)
    {
        return configuration.RgpdConsentGiven
            && configuration.AcceptedRgpdVersion >= MareConfig.ExpectedRgpdVersion;
    }
}