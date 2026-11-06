using UmbraSync.Modules;

namespace UmbraSync.MareConfiguration.Configurations;

public class ModulesConfig : IMareConfiguration
{
    public bool Rp { get; set; } = true;
    public bool Housing { get; set; } = true;
    public bool Discovery { get; set; } = true;
    public bool ChatPlus { get; set; } = true;
    public int Version { get; set; } = 0;

    public bool IsEnabled(ModuleId id) => id switch
    {
        ModuleId.Rp => Rp,
        ModuleId.Housing => Housing,
        ModuleId.Discovery => Discovery,
        ModuleId.ChatPlus => ChatPlus,
        _ => false,
    };

    public void Set(ModuleId id, bool enabled)
    {
        switch (id)
        {
            case ModuleId.Rp: Rp = enabled; break;
            case ModuleId.Housing: Housing = enabled; break;
            case ModuleId.Discovery: Discovery = enabled; break;
            case ModuleId.ChatPlus: ChatPlus = enabled; break;
        }
    }

    public void ApplyPreset(bool enabled)
    {
        foreach (var id in Enum.GetValues<ModuleId>())
            Set(id, enabled);
    }

    public IReadOnlySet<ModuleId> EnabledModules()
        => Enum.GetValues<ModuleId>().Where(IsEnabled).ToHashSet();
}
