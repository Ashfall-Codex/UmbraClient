using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.FileCache;
using UmbraSync.Interop;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.PlayerData.Services;
using UmbraSync.Services;
using UmbraSync.Services.Events;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;
using UmbraSync.UI.Components.Popup;
using UmbraSync.UI.Handlers;
using UmbraSync.WebAPI;
using UmbraSync.WebAPI.Files;

namespace UmbraSync;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IHost _host;

#pragma warning disable CA2211, CS8618, MA0069, S1104, S2223
    public static Plugin? Instance { get; private set; }
#pragma warning restore CA2211, CS8618, MA0069, S1104, S2223
    public Action<IFramework>? RealOnFrameworkUpdate { get; set; }
    public void OnFrameworkUpdate(IFramework framework)
    {
        RealOnFrameworkUpdate?.Invoke(framework);
    }

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IDataManager gameData,
        IFramework framework, IObjectTable objectTable, IClientState clientState, ICondition condition, IChatGui chatGui,
        IGameGui gameGui, IDtrBar dtrBar, IToastGui toastGui, IPluginLog pluginLog, ITargetManager targetManager, INotificationManager notificationManager,
        ITextureProvider textureProvider, IContextMenu contextMenu, IGameInteropProvider gameInteropProvider,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList, IKeyState keyState)
    {
        Instance = this;
        _host = new HostBuilder()
        .UseContentRoot(pluginInterface.ConfigDirectory.FullName)
        .ConfigureLogging(lb =>
        {
            lb.ClearProviders();
            lb.AddDalamudLogging(pluginLog);
            lb.SetMinimumLevel(LogLevel.Trace);
        })
        .ConfigureServices(collection =>
        {
            collection.AddSingleton(new WindowSystem("UmbraSync"));
            collection.AddSingleton<FileDialogManager>();

            // add dalamud services
            collection.AddSingleton(_ => pluginInterface);
            collection.AddSingleton(_ => pluginInterface.UiBuilder);
            collection.AddSingleton(_ => commandManager);
            collection.AddSingleton(_ => gameData);
            collection.AddSingleton(_ => framework);
            collection.AddSingleton(_ => objectTable);
            collection.AddSingleton(_ => clientState);
            collection.AddSingleton(_ => condition);
            collection.AddSingleton(_ => chatGui);
            collection.AddSingleton(_ => gameGui);
            collection.AddSingleton(_ => dtrBar);
            collection.AddSingleton(_ => toastGui);
            collection.AddSingleton(_ => pluginLog);
            collection.AddSingleton(_ => targetManager);
            collection.AddSingleton(_ => notificationManager);
            collection.AddSingleton(_ => textureProvider);
            collection.AddSingleton(_ => contextMenu);
            collection.AddSingleton(_ => gameInteropProvider);
            collection.AddSingleton(_ => namePlateGui);
            collection.AddSingleton(_ => gameConfig);
            collection.AddSingleton(_ => partyList);
            collection.AddSingleton(_ => keyState);

            // add mare related singletons
            collection.AddSingleton<MareMediator>();
            collection.AddSingleton<FileCacheManager>();
            collection.AddSingleton<ServerConfigurationManager>();
            collection.AddSingleton<ApiController>();
            collection.AddSingleton<PerformanceCollectorService>();
            collection.AddSingleton<ApplicationSemaphoreService>();
            collection.AddSingleton<HubFactory>();
            collection.AddSingleton<FileUploadManager>();
            collection.AddSingleton<FileTransferOrchestrator>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.DiscoveryConfigProvider>();
            collection.AddSingleton<UmbraSync.WebAPI.AutoDetect.DiscoveryApiClient>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.AutoDetectRequestService>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.NearbyDiscoveryService>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.NearbyPendingService>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.AutoDetectSuppressionService>();
            collection.AddSingleton<UmbraSync.Services.AutoDetect.SyncshellDiscoveryService>();
            collection.AddSingleton<MarePlugin>();
            collection.AddSingleton<UmbraProfileManager>();
            collection.AddSingleton<GameObjectHandlerFactory>();
            collection.AddSingleton<FileDownloadDeduplicator>();
            collection.AddSingleton<FileDownloadManagerFactory>();
            collection.AddSingleton<PairHandlerFactory>();
            collection.AddSingleton<PairAnalyzerFactory>();
            collection.AddSingleton<PairFactory>();
            collection.AddSingleton<XivDataAnalyzer>();
            collection.AddSingleton<CharacterAnalyzer>();
            collection.AddSingleton<TokenProvider>();
            collection.AddSingleton<AccountRegistrationService>();
            collection.AddSingleton<PluginWarningNotificationService>();
            collection.AddSingleton<FileCompactor>();
            collection.AddSingleton<TagHandler>();
            collection.AddSingleton<SyncDefaultsService>();
            collection.AddSingleton<UidDisplayHandler>();
            collection.AddSingleton<PluginWatcherService>();
            collection.AddSingleton<PairPerformanceMetricsCache>();
            collection.AddSingleton<PlayerPerformanceService>();

            collection.AddSingleton<CharaDataManager>();
            collection.AddSingleton<CharaDataFileHandler>();
            collection.AddSingleton<CharaDataCharacterHandler>();
            collection.AddSingleton<CharaDataNearbyManager>();
            collection.AddSingleton<CharaDataGposeTogetherManager>();
            collection.AddSingleton<McdfShareManager>();

            collection.AddSingleton<VfxSpawnManager>();
            collection.AddSingleton<BlockedCharacterHandler>();
            collection.AddSingleton<IpcProvider>();
            collection.AddSingleton<VisibilityService>();
            collection.AddSingleton<EventAggregator>();
            collection.AddSingleton<DalamudUtilService>();
            collection.AddSingleton<DtrEntry>();
            collection.AddSingleton<PairManager>();
            collection.AddSingleton<PairHandlerRegistry>();
            collection.AddSingleton<PairStateCache>();
            collection.AddSingleton<PairLedger>();
            collection.AddSingleton<RedrawManager>();
            collection.AddSingleton<IpcCallerPenumbra>();
            collection.AddSingleton<IpcCallerGlamourer>();
            collection.AddSingleton<IpcCallerCustomize>();
            collection.AddSingleton<IpcCallerHeels>();
            collection.AddSingleton<IpcCallerHonorific>();
            collection.AddSingleton<IpcCallerMoodles>();
            collection.AddSingleton<IpcCallerPetNames>();
            collection.AddSingleton<IpcCallerBrio>();
            collection.AddSingleton<IpcCallerMare>();
            collection.AddSingleton<IpcManager>();
            collection.AddSingleton<NotificationService>();
            collection.AddSingleton<TemporarySyncshellNotificationService>();
            collection.AddSingleton<PartyListTypingService>();
            collection.AddSingleton<TypingIndicatorStateService>();
            collection.AddSingleton<TypingRemoteNotificationService>();
            collection.AddSingleton<ChatTwoCompatibilityService>();
            collection.AddSingleton<NotificationTracker>();
            collection.AddSingleton<PenumbraPrecacheService>();
            collection.AddSingleton<SlotService>();
            collection.AddSingleton<HousingMonitorService>();
            collection.AddSingleton<HousingFurnitureScanner>();
            collection.AddSingleton<HousingShareManager>();
            collection.AddSingleton<HousingFurnitureSyncService>();
            collection.AddSingleton<RgpdDataService>();

            collection.AddSingleton((s) => new RpConfigService(pluginInterface.ConfigDirectory.FullName, s.GetRequiredService<DalamudUtilService>()));
            collection.AddSingleton((s) => new MareConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new NotesConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerTagConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new SyncshellConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new TransientConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new XivDataStorageService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new PlayerPerformanceConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new ServerBlockConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new CharaDataConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new RemoteConfigCacheService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton((s) => new NotificationsConfigService(pluginInterface.ConfigDirectory.FullName));
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<MareConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<ServerConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<NotesConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<ServerTagConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<SyncshellConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<TransientConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<XivDataStorageService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<PlayerPerformanceConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<ServerBlockConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<CharaDataConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<RemoteConfigCacheService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<NotificationsConfigService>());
            collection.AddSingleton<IConfigService<IMareConfiguration>>(s => s.GetRequiredService<RpConfigService>());
            collection.AddSingleton<ConfigurationMigrator>();
            collection.AddSingleton<ConfigurationSaveService>();
            collection.AddSingleton<IPopupHandler, ReportPopupHandler>();
            collection.AddSingleton<IPopupHandler, BanUserPopupHandler>();
            collection.AddSingleton<IPopupHandler, SlotPopupHandler>();
            collection.AddSingleton<WindowMediatorSubscriberBase, PopupHandler>();


            collection.AddSingleton<HubFactory>();

            // add scoped services
            collection.AddScoped<CacheMonitor>();
            collection.AddScoped<UiFactory>();
            collection.AddScoped<SettingsUi>();
            collection.AddScoped<CompactUi>();
            collection.AddScoped<EditProfileUi>();
            collection.AddScoped<DataAnalysisUi>();
            collection.AddScoped<CharaDataHubUi>();
            collection.AddScoped<AutoDetectUi>();
            collection.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<SettingsUi>());
            collection.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<CompactUi>());
            collection.AddScoped<WindowMediatorSubscriberBase, IntroUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, DownloadUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, PairRequestToastUi>();
            collection.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<AutoDetectUi>());
            collection.AddScoped<WindowMediatorSubscriberBase, ChangelogUi>();
            collection.AddScoped<WindowMediatorSubscriberBase, PopoutProfileUi>();
            collection.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<DataAnalysisUi>());
            collection.AddScoped<WindowMediatorSubscriberBase, EventViewerUI>();
            collection.AddScoped<WindowMediatorSubscriberBase, CharaDataHubUi>();
            collection.AddScoped<WindowMediatorSubscriberBase>(sp => sp.GetRequiredService<EditProfileUi>());
            collection.AddScoped<WindowMediatorSubscriberBase, TypingIndicatorOverlay>();
            collection.AddScoped<CacheCreationService>();
            collection.AddScoped<TransientResourceManager>();
            collection.AddScoped<PlayerDataFactory>();
            collection.AddScoped<OnlinePlayerManager>();
            collection.AddScoped<UiService>();
            collection.AddScoped<CommandManagerService>();
            collection.AddScoped<UiSharedService>();
            collection.AddScoped<ChatService>();
            collection.AddScoped<ChatNameReplacementService>();
            collection.AddScoped<ChatEmoteHighlightService>();
            collection.AddScoped<ChatProximityBlendService>();
            collection.AddScoped<GuiHookService>();
            collection.AddScoped<ChatTypingDetectionService>();

            collection.AddHostedService(p => p.GetRequiredService<PluginWatcherService>());
            collection.AddHostedService(p => p.GetRequiredService<ConfigurationSaveService>());
            collection.AddHostedService(p => p.GetRequiredService<MareMediator>());
            collection.AddHostedService(p => p.GetRequiredService<NotificationService>());
            collection.AddHostedService(p => p.GetRequiredService<TemporarySyncshellNotificationService>());
            collection.AddSingleton<UmbraSync.Services.AutoDetect.PermanentSyncshellAutoDetectMonitor>();
            collection.AddHostedService(p => p.GetRequiredService<FileCacheManager>());
            collection.AddHostedService(p => p.GetRequiredService<ConfigurationMigrator>());
            collection.AddHostedService(p => p.GetRequiredService<DalamudUtilService>());
            collection.AddHostedService(p => p.GetRequiredService<PerformanceCollectorService>());
            collection.AddHostedService(p => p.GetRequiredService<DtrEntry>());
            collection.AddHostedService(p => p.GetRequiredService<EventAggregator>());
            collection.AddHostedService(p => p.GetRequiredService<MarePlugin>());
            collection.AddHostedService(p => p.GetRequiredService<IpcProvider>());
            collection.AddHostedService(p => p.GetRequiredService<UmbraSync.Services.AutoDetect.NearbyDiscoveryService>());
            collection.AddHostedService(p => p.GetRequiredService<UmbraSync.Services.AutoDetect.SyncshellDiscoveryService>());
            collection.AddHostedService(p => p.GetRequiredService<UmbraSync.Services.AutoDetect.PermanentSyncshellAutoDetectMonitor>());
            collection.AddHostedService(p => p.GetRequiredService<ChatTwoCompatibilityService>());
            collection.AddHostedService(p => p.GetRequiredService<UmbraSync.Services.AutoDetect.AutoDetectSuppressionService>());
            collection.AddHostedService(p => p.GetRequiredService<PenumbraPrecacheService>());
            collection.AddHostedService(p => p.GetRequiredService<HousingMonitorService>());
            collection.AddHostedService(p => p.GetRequiredService<HousingFurnitureSyncService>());
        })
        .Build();

        // Enregistrer les callbacks UI immédiatement pour satisfaire la validation Dalamud.
        // UiService ajoutera ses propres handlers fonctionnels plus tard lors du chargement.
        pluginInterface.UiBuilder.OpenConfigUi += () => { };
        pluginInterface.UiBuilder.OpenMainUi += () => { };

        var configService = _host.Services.GetRequiredService<MareConfigService>();
        Loc.Initialize(configService.Current.UiLanguage);
        if (!string.Equals(configService.Current.UiLanguage, Loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            configService.Current.UiLanguage = Loc.CurrentLanguage;
            configService.Save();
        }

        try
        {
            var partyListTypingService = _host.Services.GetRequiredService<PartyListTypingService>();
            pluginInterface.UiBuilder.Draw += partyListTypingService.Draw;
        }
        catch (Exception e)
        {
            pluginLog.Warning(e, "Failed to initialize PartyListTypingService draw hook");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _host.StartAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                pluginLog.Error(e, "HostBuilder startup exception");
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }
}