using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

public sealed class HousingScenarioSyncService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<HousingScenarioSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly ArrPathResolver _arrPathResolver;

    public HousingScenarioSyncService(
        ILogger<HousingScenarioSyncService> logger,
        MareMediator mediator,
        ArrPathResolver arrPathResolver)
    {
        _logger = logger;
        _mediator = mediator;
        _arrPathResolver = arrPathResolver;
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

    }

    private void OnHousingPlotLeft()
    {
        _logger.LogDebug("Scenario sync : left housing plot");

    }

    private void OnConnected()
    {
        _logger.LogDebug("Scenario sync : connected, scénario sync prêt à l'emploi");
    }
}
