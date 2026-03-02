namespace UmbraSync.MareConfiguration.Models;

[Serializable]
public class SyncOverrideEntry
{
    public bool? DisableSounds { get; set; }
    public bool? DisableAnimations { get; set; }
    public bool? DisableVfx { get; set; }
    public bool? DisableHousingMods { get; set; }

    public bool IsEmpty => DisableSounds is null && DisableAnimations is null && DisableVfx is null && DisableHousingMods is null;
}