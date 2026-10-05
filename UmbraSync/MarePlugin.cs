using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.PlayerData.Services;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync;

public class MarePlugin : MediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _mareConfigService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly RgpdDataService _rgpdDataService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly INotificationManager _notificationManager;
    private IServiceScope? _runtimeServiceScope;
    private Task? _launchTask;

    public MarePlugin(ILogger<MarePlugin> logger, MareConfigService mareConfigService,
        ServerConfigurationManager serverConfigurationManager,
        DalamudUtilService dalamudUtil,
        RgpdDataService rgpdDataService,
        IServiceScopeFactory serviceScopeFactory, INotificationManager notificationManager, MareMediator mediator) : base(logger, mediator)
    {
        _mareConfigService = mareConfigService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtil = dalamudUtil;
        _rgpdDataService = rgpdDataService;
        _serviceScopeFactory = serviceScopeFactory;
        _notificationManager = notificationManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;
        Logger.LogInformation("Launching {name} {major}.{minor}.{build}.{rev}", "Umbra Sync", version.Major, version.Minor, version.Build, version.Revision);
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(MarePlugin), Services.Events.EventSeverity.Informational,
            $"Starting Umbra Sync {version.Major}.{version.Minor}.{version.Build}.{version.Revision}")));

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (msg) => { if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager); });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
        // Consentement retiré : les services de synchronisation s'arrêtent et l'écran de consentement revient.
        Mediator.Subscribe<RgpdConsentUpdatedMessage>(this, (msg) =>
        {
            if (msg.ConsentGiven || !_dalamudUtil.IsLoggedIn) return;
            if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager);
        });

        Mediator.StartQueueProcessing();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();

        DalamudUtilOnLogOut();

        Logger.LogDebug("Halting MarePlugin");

        return Task.CompletedTask;
    }

    private void DalamudUtilOnLogIn()
    {
        Logger.LogDebug("Client login");
        if (_launchTask == null || _launchTask.IsCompleted) _launchTask = Task.Run(WaitForPlayerAndLaunchCharacterManager);
    }

    private void DalamudUtilOnLogOut()
    {
        Logger.LogDebug("Client logout");

        StopRuntimeServices();
    }

    private void StopRuntimeServices()
    {
        var scope = Interlocked.Exchange(ref _runtimeServiceScope, null);
        try
        {
            scope?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error while stopping runtime services");
        }
    }

    private async Task WaitForPlayerAndLaunchCharacterManager()
    {
        while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false))
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        try
        {
            Logger.LogDebug("Launching Managers");

            StopRuntimeServices();
            _runtimeServiceScope = _serviceScopeFactory.CreateScope();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<UiService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CommandManagerService>();
            if (!_mareConfigService.Current.HasValidSetup() || !_serverConfigurationManager.HasValidConfig())
            {
                Mediator.Publish(new SwitchToIntroUiMessage());
                return;
            }

            if (!_rgpdDataService.IsRgpdConsentValid)
            {
                Logger.LogInformation("RGPD consent missing or expired, showing consent screen");
                Mediator.Publish(new SwitchToRgpdConsentUiMessage());
                return;
            }
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CacheCreationService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<TransientResourceManager>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<OnlinePlayerManager>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<PairLedger>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<NotificationService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<SyncDefaultsService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatNameReplacementService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatEmoteHighlightService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatProximityBlendService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatTargetSoundService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<ChatTypingDetectionService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<GuiHookService>();
            _runtimeServiceScope.ServiceProvider.GetRequiredService<CharacterAnalyzer>();

#if !DEBUG
            if (_mareConfigService.Current.LogLevel != LogLevel.Information)
            {
                Mediator.Publish(new NotificationMessage("Abnormal Log Level",
                    $"Your log level is set to '{_mareConfigService.Current.LogLevel}' which is not recommended for normal usage. Set it to '{LogLevel.Information}' in \"Umbra Settings -> Debug\" unless instructed otherwise.",
                    MareConfiguration.Models.NotificationType.Error, TimeSpan.FromSeconds(15000)));
            }
#endif
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error during launch of managers");
            if (Utils.MemoryPressure.IsOutOfMemory(ex))
            {
                Utils.MemoryPressure.NotifyStartupFailure(_notificationManager);
            }
        }
    }
}