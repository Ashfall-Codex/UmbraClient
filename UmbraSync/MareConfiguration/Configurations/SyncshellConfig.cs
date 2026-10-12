namespace UmbraSync.MareConfiguration.Configurations;

[Serializable]
public class SyncshellConfig : IMareConfiguration
{
    public int Version { get; set; } = 0;
    public HashSet<string> FavoriteSyncshells { get; set; } = new(StringComparer.Ordinal);
}
