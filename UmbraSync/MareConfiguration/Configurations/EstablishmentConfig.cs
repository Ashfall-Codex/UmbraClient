namespace UmbraSync.MareConfiguration.Configurations;

[Serializable]
public class EstablishmentConfig : IMareConfiguration
{
    public int Version { get; set; } = 0;
    public HashSet<Guid> BookmarkedEstablishments { get; set; } = [];
    public Dictionary<Guid, string> EstablishmentSyncSlotBindings { get; set; } = [];
    public bool EnableProximityNotifications { get; set; } = true;
    public bool EnableEventReminders { get; set; } = true;
}
