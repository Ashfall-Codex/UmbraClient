using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Orchestrateur du sync des scénarios NPC : réagit aux events Mediator pour
/// déclencher le HousingScenarioManager (apply / cleanup différé) et nettoie
/// l'état orphelin au démarrage.
/// </summary>
public sealed class HousingScenarioSyncService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<HousingScenarioSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly ArrPathResolver _arrPathResolver;
    private readonly HousingScenarioManager _manager;

    public HousingScenarioSyncService(
        ILogger<HousingScenarioSyncService> logger,
        MareMediator mediator,
        ArrPathResolver arrPathResolver,
        HousingScenarioManager manager)
    {
        _logger = logger;
        _mediator = mediator;
        _arrPathResolver = arrPathResolver;
        _manager = manager;
    }

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting HousingScenarioSyncService");

        string? scenariosPath = _arrPathResolver.TryGetScenariosPath();
        if (scenariosPath != null)
        {
            _logger.LogInformation("ARR détecté, dossier Scenarios : {Path}", scenariosPath);
        }

        // Nettoyage crash-safe : supprime un éventuel temp file orphelin de la session précédente
        try
        {
            _manager.CleanupStaleState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup stale scenario state au startup en erreur");
        }

        _mediator.Subscribe<HousingPlotEnteredMessage>(this, OnHousingPlotEntered);
        _mediator.Subscribe<HousingPlotLeftMessage>(this, _ => OnHousingPlotLeft());
        _mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping HousingScenarioSyncService");
        _mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private void OnHousingPlotEntered(HousingPlotEnteredMessage msg)
    {
        _logger.LogDebug("Scenario sync : entered housing plot {Server}:{Territory}:{Ward}:{House}",
            msg.LocationInfo.ServerId, msg.LocationInfo.TerritoryId, msg.LocationInfo.WardId, msg.LocationInfo.HouseId);

        _ = TryApplyScenarioAsync(msg.LocationInfo);
    }

    private void OnHousingPlotLeft()
    {
        _logger.LogDebug("Scenario sync : left housing plot");
        _manager.ScheduleDelayedCleanup();
    }

    private void OnConnected()
    {
        _logger.LogDebug("Scenario sync : connected");
    }

    private async Task TryApplyScenarioAsync(LocationInfo location)
    {
        try
        {
            await _manager.CheckAndApplyForLocationAsync(location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec apply scénario pour location {Server}:{Territory}:{Ward}:{House}",
                location.ServerId, location.TerritoryId, location.WardId, location.HouseId);
        }
    }
}
