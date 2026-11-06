using UmbraSync.Modules.ChatPlus;
using UmbraSync.Modules.Discovery;
using UmbraSync.Modules.Housing;
using UmbraSync.Modules.Rp;

namespace UmbraSync.Modules;

public static class ModuleCatalog
{
    public static IReadOnlyList<IUmbraModule> All { get; } =
    [
        new RpModule(),
        new HousingModule(),
        new DiscoveryModule(),
        new ChatPlusModule(),
    ];

    public static IUmbraModule Get(ModuleId id) => All.First(m => m.Id == id);
}
