using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.StageShare;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Modules.Housing.Services;

public sealed class StageShareSyncService : IHostedService, IMediatorSubscriber
{
    private const int LocationDebounceMs = 1500;

    private readonly ILogger<StageShareSyncService> _logger;
    private readonly MareMediator _mediator;
    private readonly StageShareManager _manager;
    private CancellationTokenSource? _debounceCts;
    private StageLocationDto? _currentLocation;

    public StageShareSyncService(ILogger<StageShareSyncService> logger, MareMediator mediator, StageShareManager manager)
    {
        _logger = logger;
        _mediator = mediator;
        _manager = manager;
    }

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting StageShareSyncService");
        _mediator.Subscribe<StagehandLocationChangedMessage>(this, OnLocationChanged);
        _mediator.Subscribe<StagehandAvailabilityChangedMessage>(this, OnAvailabilityChanged);
        _mediator.Subscribe<StagehandLocalStageEditedMessage>(this, msg => _manager.MarkLocalStageEdited(msg.FullPath));
        _mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());
        _mediator.Subscribe<DisconnectedMessage>(this, _ => Fire(() => _manager.RemoveAllAppliedAsync("déconnexion"), "retrait à la déconnexion"));
        _mediator.Subscribe<StageSyncPreferenceChangedMessage>(this, _ => ReapplyCurrent());
        _mediator.Subscribe<ApplyDefaultsToAllSyncsMessage>(this, _ => ReapplyCurrent());
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping StageShareSyncService");
        _mediator.UnsubscribeAll(this);
        CancelDebounce();
        try
        {
            await Task.WhenAny(_manager.RemoveAllAppliedAsync("arrêt"), Task.Delay(3000, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Retrait des Stages à l'arrêt en échec");
        }
    }

    private void OnLocationChanged(StagehandLocationChangedMessage msg)
    {
        _currentLocation = msg.Location;
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounceCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LocationDebounceMs, cts.Token).ConfigureAwait(false);
                await _manager.RemoveAllAppliedAsync("changement de lieu").ConfigureAwait(false);
                await _manager.RefreshAvailableAsync(msg.Location, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Synchro des Stages au changement de lieu annulée");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Synchro des Stages au changement de lieu en échec");
            }
        });
    }

    private void OnAvailabilityChanged(StagehandAvailabilityChangedMessage msg)
    {
        if (msg.Available)
        {
            Fire(ApplyAtCurrentLocationAsync, "synchro des Stages à l'arrivée de Stagehand");
            return;
        }
        _manager.ForgetApplied();
    }

    private void OnConnected()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Random.Shared.Next(2000, 8000)).ConfigureAwait(false);
                await ApplyAtCurrentLocationAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Synchro des Stages à la connexion en échec");
            }
        });
    }

    private void ReapplyCurrent()
    {
        var location = _currentLocation ?? _manager.AppliedLocation;
        if (location == null) return;
        Fire(() => _manager.RefreshAvailableAsync(location.Value), "rafraîchissement des Stages");
    }

    private async Task ApplyAtCurrentLocationAsync()
    {
        if (!_manager.IsStagehandAvailable) return;
        var location = await _manager.GetLocationAsync().ConfigureAwait(false);
        if (location == null) return;
        _currentLocation = location;
        await _manager.RefreshAvailableAsync(location.Value).ConfigureAwait(false);
    }

    private void Fire(Func<Task> operation, string description)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Operation} en échec", description);
            }
        });
    }

    private void CancelDebounce()
    {
        var previous = Interlocked.Exchange(ref _debounceCts, null);
        if (previous == null) return;
        try
        {
            previous.Cancel();
            previous.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "Anti-rebond déjà libéré");
        }
    }
}
