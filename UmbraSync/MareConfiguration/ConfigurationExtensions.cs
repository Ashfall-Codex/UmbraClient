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

    public static bool HasValidRgpdConsent(this MareConfig configuration)
    {
        return configuration.RgpdConsentGiven
            && configuration.AcceptedRgpdVersion >= MareConfig.ExpectedRgpdVersion;
    }
}