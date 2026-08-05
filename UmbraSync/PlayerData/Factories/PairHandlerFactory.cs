using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.PlayerData.Redraw;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync.PlayerData.Factories;

public class PairHandlerFactory(ILoggerFactory loggerFactory, GameObjectHandlerFactory gameObjectHandlerFactory, IpcManager ipcManager,
    FileDownloadManagerFactory fileDownloadManagerFactory, DalamudUtilService dalamudUtilService,
    PluginWarningNotificationService pluginWarningNotificationManager, IHostApplicationLifetime hostApplicationLifetime,
    FileCacheManager fileCacheManager, MareMediator mareMediator, PlayerPerformanceService playerPerformanceService,
    PairAnalyzerFactory pairAnalyzerFactory,
    MareConfigService configService, VisibilityService visibilityService,
    ApplicationSemaphoreService applicationSemaphoreService, ServerConfigurationManager serverConfigurationManager,
    PairRedrawCoordinator pairRedrawCoordinator, CompressedAlternateManager compressedAlternateManager,
    PlayerPerformanceConfigService playerPerformanceConfigService)
{
    private readonly CompressedAlternateManager _compressedAlternateManager = compressedAlternateManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService = playerPerformanceConfigService;
    private readonly MareConfigService _configService = configService;
    private readonly DalamudUtilService _dalamudUtilService = dalamudUtilService;
    private readonly FileCacheManager _fileCacheManager = fileCacheManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory = fileDownloadManagerFactory;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory = gameObjectHandlerFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime = hostApplicationLifetime;
    private readonly IpcManager _ipcManager = ipcManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly MareMediator _mareMediator = mareMediator;
    private readonly PlayerPerformanceService _playerPerformanceService = playerPerformanceService;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager = pluginWarningNotificationManager;
    private readonly PairAnalyzerFactory _pairAnalyzerFactory = pairAnalyzerFactory;
    private readonly VisibilityService _visibilityService = visibilityService;
    private readonly ApplicationSemaphoreService _applicationSemaphoreService = applicationSemaphoreService;
    private readonly ServerConfigurationManager _serverConfigurationManager = serverConfigurationManager;
    private readonly PairRedrawCoordinator _pairRedrawCoordinator = pairRedrawCoordinator;

    public PairHandler Create(Pair pair)
    {
        return new PairHandler(_loggerFactory.CreateLogger<PairHandler>(), pair, _pairAnalyzerFactory.Create(pair), _gameObjectHandlerFactory,
            _ipcManager, _fileDownloadManagerFactory.Create(), _pluginWarningNotificationManager, _dalamudUtilService, _hostApplicationLifetime,
            _fileCacheManager, _mareMediator, _playerPerformanceService, _configService, _visibilityService, _applicationSemaphoreService, _serverConfigurationManager,
            _pairRedrawCoordinator, _compressedAlternateManager, _playerPerformanceConfigService);
    }
}