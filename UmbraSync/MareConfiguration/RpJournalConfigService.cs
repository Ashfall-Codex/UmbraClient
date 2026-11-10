using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.MareConfiguration;

public class RpJournalConfigService(string configDir) : ConfigurationServiceBase<RpJournalConfig>(configDir)
{
    public const string ConfigName = "journal.json";

    public override string ConfigurationName => ConfigName;
}
