using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

public sealed class HousingFurnitureSyncService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<HousingFurnitureSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly HousingShareManager _housingShareManager;
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ICommandManager _commandManager;

    public HousingFurnitureSyncService(
        ILogger<HousingFurnitureSyncService> logger,
        MareMediator mediator,
        HousingShareManager housingShareManager,
        MareConfigService configService,
        DalamudUtilService dalamudUtil,
        ICommandManager commandManager)
    {
        _logger = logger;
        _mediator = mediator;
        _housingShareManager = housingShareManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _commandManager = commandManager;
    }

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting HousingFurnitureSyncService");

        // Nettoyer les mods housing orphelins de sessions précédentes
        _housingShareManager.CleanupStaleMods();

        _mediator.Subscribe<HousingPlotEnteredMessage>(this, OnHousingPlotEntered);
        _mediator.Subscribe<HousingPlotLeftMessage>(this, _ => OnHousingPlotLeft());
        _mediator.Subscribe<ApplyDefaultsToAllSyncsMessage>(this, OnDefaultsChanged);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping HousingFurnitureSyncService");
        _mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnHousingPlotEntered(HousingPlotEnteredMessage msg)
    {
        _logger.LogDebug("Entered housing plot {Server}:{Territory}:{Ward}:{House}",
            msg.LocationInfo.ServerId, msg.LocationInfo.TerritoryId, msg.LocationInfo.WardId, msg.LocationInfo.HouseId);

        //Global : si la synchro housing est désactivée, ne pas appliquer
        if (_configService.Current.DefaultDisableHousingMods)
        {
            _logger.LogInformation("Synchro housing globalement désactivée, skip");
            return;
        }

        _ = TryApplyHousingModsAsync(msg.LocationInfo);
    }

    private void OnDefaultsChanged(ApplyDefaultsToAllSyncsMessage msg)
    {
        // Réagir quand housing est désactivé alors qu'un mod est déjà appliqué
        if (!_configService.Current.DefaultDisableHousingMods || !_housingShareManager.IsApplied) return;

        _logger.LogInformation("Housing désactivé globalement, suppression du mod appliqué");
        _ = RemoveAndRedrawAsync();
    }

    private async Task RemoveAndRedrawAsync()
    {
        try
        {
            await _housingShareManager.RemoveAppliedModsAsync().ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
            await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                _commandManager.ProcessCommand("/penumbra redraw furniture");
            }).ConfigureAwait(false);
            _logger.LogInformation("Redraw furniture exécuté après désactivation du housing");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du redraw furniture après désactivation housing");
        }
    }

    private void OnHousingPlotLeft()
    {
        _logger.LogDebug("Left housing plot");
        _housingShareManager.ScheduleDelayedCleanup();
    }

    private async Task TryApplyHousingModsAsync(LocationInfo location)
    {
        try
        {
            await _housingShareManager.CheckAndApplyForLocationAsync(location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply housing mods for location {Server}:{Territory}:{Ward}:{House}",
                location.ServerId, location.TerritoryId, location.WardId, location.HouseId);
        }
    }
}
