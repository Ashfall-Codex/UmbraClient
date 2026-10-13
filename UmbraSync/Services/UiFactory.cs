using Dalamud.Interface.ImGuiFileDialog;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Group;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Notification;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;

namespace UmbraSync.Services;

public class UiFactory(
    ILoggerFactory loggerFactory,
    MareMediator mareMediator,
    ApiController apiController,
    UiSharedService uiSharedService,
    PairManager pairManager,
    SyncshellDiscoveryService syncshellDiscoveryService,
    ServerConfigurationManager serverConfigManager,
    UmbraProfileManager umbraProfileManager,
    PerformanceCollectorService performanceCollectorService,
    NotificationTracker notificationTracker,
    DalamudUtilService dalamudUtilService,
    FileDialogManager fileDialogManager,
    IpcManager ipcManager,
    MareConfigService mareConfigService)
{
    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        return new SyncshellAdminUI(loggerFactory.CreateLogger<SyncshellAdminUI>(), mareMediator,
            apiController, uiSharedService, pairManager, syncshellDiscoveryService, dto, performanceCollectorService, notificationTracker,
            dalamudUtilService, fileDialogManager, umbraProfileManager);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(Pair pair)
    {
        return new StandaloneProfileUi(loggerFactory.CreateLogger<StandaloneProfileUi>(), mareMediator,
            uiSharedService, serverConfigManager, mareConfigService, umbraProfileManager, apiController, pair, performanceCollectorService,
            ipcManager, dalamudUtilService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            mareMediator, uiSharedService, apiController, performanceCollectorService);
    }

    public PlayerAnalysisUI CreatePlayerAnalysisUi(Pair pair)
    {
        return new PlayerAnalysisUI(loggerFactory.CreateLogger<PlayerAnalysisUI>(), pair,
            mareMediator, uiSharedService, performanceCollectorService);
    }
}