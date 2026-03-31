using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.MareConfiguration;

public class EstablishmentConfigService(string configDir) : ConfigurationServiceBase<EstablishmentConfig>(configDir)
{
    public const string ConfigName = "establishments.json";

    public override string ConfigurationName => ConfigName;
}
