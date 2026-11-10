using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.MareConfiguration;

public class ModulesConfigService(string configDir) : ConfigurationServiceBase<ModulesConfig>(configDir)
{
    public const string ConfigName = "modules.json";

    public override string ConfigurationName => ConfigName;
}
