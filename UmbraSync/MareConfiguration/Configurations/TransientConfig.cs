using UmbraSync.API.Dto.Slot;

namespace UmbraSync.MareConfiguration.Configurations;

public class TransientConfig : IMareConfiguration
{
    public Dictionary<string, HashSet<string>> PlayerPersistentTransientCache { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SlotSyncshellDto> LastJoinedSlotSyncshellPerUid { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PendingSlotLeaveGidPerUid { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 2;
}