using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Orchestrateur du sync des scénarios NPC
/// </summary>
public sealed class HousingScenarioSyncService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<HousingScenarioSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly HousingScenarioManager _manager;
    private LocationInfo? _currentPlotLocation;

    public HousingScenarioSyncService(
        ILogger<HousingScenarioSyncService> logger,
        MareMediator mediator,
        HousingScenarioManager manager)
    {
        _logger = logger;
        _mediator = mediator;
        _manager = manager;
    }

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting HousingScenarioSyncService");

        // Plus de nettoyage crash-safe nécessaire : les scènes partagées sont spawnées en mémoire
        // (aucun fichier écrit sur disque), elles disparaissent donc d'elles-mêmes au redémarrage.

        _mediator.Subscribe<HousingPlotEnteredMessage>(this, OnHousingPlotEntered);
        _mediator.Subscribe<HousingPlotLeftMessage>(this, _ => OnHousingPlotLeft());
        // Le changement de zone détruit les acteurs natifs des PNJ : l'état « appliqué » du manager
        // ne correspond plus à rien et doit être invalidé, sinon un retour rapide dans le logement
        // annule le nettoyage différé et le scénario n'est jamais respawné.
        _mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => _manager.InvalidateAppliedAfterZoneSwitch());
        _mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());
        _mediator.Subscribe<HousingNpcShareTestMessage>(this, _ => OnShareTest());

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

        _currentPlotLocation = msg.LocationInfo;
        _ = TryApplyScenarioAsync(msg.LocationInfo);
    }

    private void OnHousingPlotLeft()
    {
        _logger.LogDebug("Scenario sync : left housing plot");
        _currentPlotLocation = null;
        _manager.ScheduleDelayedCleanup();
    }

    private void OnShareTest()
    {
        _logger.LogInformation("npcsharetest : commande reçue");
        _ = Task.Run(async () =>
        {
            try
            {
                await _manager.ForceApplyOwnShareAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "npcsharetest échoué");
            }
        });
    }

    private void OnConnected()
    {
        var location = _currentPlotLocation;
        if (location == null)
        {
            _logger.LogDebug("Scenario sync : connected (hors housing, rien à faire)");
            return;
        }

        // Anti-burst (règle post-mortem 2026-05-05) : jitter 2-8s avant le premier RPC pour
        // étaler le burst de requêtes des services abonnés à ConnectedMessage au lieu de tout
        // concentrer sur la première seconde de la connexion.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Random.Shared.Next(2000, 8000)).ConfigureAwait(false);
                var current = _currentPlotLocation;
                if (current == null) return; // housing quitté pendant le jitter
                await _manager.CheckAndApplyForLocationAsync(current.Value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scenario sync au connect échoué");
            }
        });
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
