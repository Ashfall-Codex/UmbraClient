using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.Modules;

public interface IModuleRegistry
{
    IReadOnlySet<ModuleId> Enabled { get; }
    bool IsEnabled(ModuleId id);
}

public sealed class ModuleRegistry(IReadOnlySet<ModuleId> enabled) : IModuleRegistry
{
    public IReadOnlySet<ModuleId> Enabled { get; } = enabled;

    public bool IsEnabled(ModuleId id) => Enabled.Contains(id);

    public static ModuleRegistry Snapshot(ModulesConfig config)
        => new(config.EnabledModules());
}
