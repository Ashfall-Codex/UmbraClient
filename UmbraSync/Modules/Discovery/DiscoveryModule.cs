using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;
using UmbraSync.Modules.Discovery.Services;
using UmbraSync.Modules.Discovery.UI;
using UmbraSync.Modules.Discovery.WebAPI;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;

namespace UmbraSync.Modules.Discovery;

public sealed class DiscoveryModule : IUmbraModule
{
    public ModuleId Id => ModuleId.Discovery;
    public string TitleKey => "Modules.Discovery.Title";
    public string DescriptionKey => "Modules.Discovery.Desc";
    public FontAwesomeIcon Icon => FontAwesomeIcon.SearchLocation;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DiscoveryConfigProvider>();
        services.AddSingleton<DiscoveryApiClient>();
        services.AddSingleton<AutoDetectRequestService>();
        services.AddSingleton<NearbyDiscoveryService>();
        services.AddSingleton<NearbyPendingService>();
        services.AddSingleton<AutoDetectSuppressionService>();
        services.AddSingleton<SyncshellDiscoveryService>();
        services.AddSingleton<PermanentSyncshellAutoDetectMonitor>();
        services.AddSingleton<TemporarySyncshellNotificationService>();

        services.AddHostedService(sp => sp.GetRequiredService<TemporarySyncshellNotificationService>());
        services.AddHostedService(sp => sp.GetRequiredService<NearbyDiscoveryService>());
        services.AddHostedService(sp => sp.GetRequiredService<SyncshellDiscoveryService>());
        services.AddHostedService(sp => sp.GetRequiredService<PermanentSyncshellAutoDetectMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoDetectSuppressionService>());

        services.AddScoped<AutoDetectUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<AutoDetectUi>());
        services.AddScoped<WindowMediatorSubscriberBase, PairRequestToastUi>();
    }
}
