using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;
using UmbraSync.Modules.Housing.Services;
using UmbraSync.Modules.Housing.UI;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;
using NpcPoseCatalog = UmbraSync.Modules.Housing.Services.NpcPoseCatalog;

namespace UmbraSync.Modules.Housing;

public sealed class HousingModule : IUmbraModule
{
    public ModuleId Id => ModuleId.Housing;
    public string TitleKey => "Modules.Housing.Title";
    public string DescriptionKey => "Modules.Housing.Desc";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Home;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<HousingFurnitureScanner>();
        services.AddSingleton<HousingFurnitureRedrawService>();
        services.AddSingleton<HousingShareManager>();
        services.AddSingleton<HousingFurnitureSyncService>();
        services.AddSingleton<ArrPathResolver>();
        services.AddSingleton<ArrScenarioFileService>();
        services.AddSingleton<HousingScenarioManager>();
        services.AddSingleton<HousingScenarioSyncService>();
        services.AddSingleton<NativeNpcSpawner>();
        services.AddSingleton<LookAtService>();
        services.AddSingleton<NpcLiveAppearanceService>();
        services.AddSingleton<HousingNpcScenarioStore>();
        services.AddSingleton<NpcPoseCatalog>();
        services.AddSingleton<HousingNpcScenarioService>();
        services.AddSingleton<HousingOwnershipService>();
        services.AddSingleton<StageShareManager>();
        services.AddSingleton<StageShareSyncService>();
        services.AddSingleton<DecorCatalogManager>();
        services.AddSingleton<DecorReportPopupHandler>();

        services.AddHostedService(sp => sp.GetRequiredService<HousingFurnitureSyncService>());
        services.AddHostedService(sp => sp.GetRequiredService<HousingScenarioSyncService>());
        services.AddHostedService(sp => sp.GetRequiredService<HousingNpcScenarioService>());
        services.AddHostedService(sp => sp.GetRequiredService<HousingOwnershipService>());
        services.AddHostedService(sp => sp.GetRequiredService<StageShareSyncService>());

        services.AddScoped<HousingNpcSceneEditorUi>();
        services.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<HousingNpcSceneEditorUi>());
        services.AddScoped<WindowMediatorSubscriberBase, HousingNpcGizmoOverlayUi>();
        services.AddScoped<WindowMediatorSubscriberBase, HousingNpcApplyOverlayUi>();
    }
}
