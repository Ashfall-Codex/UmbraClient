using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI;

namespace UmbraSync.Services;

public sealed class AshfallConnectAutoSyncService : DisposableMediatorSubscriberBase, IHostedService
{

    private readonly IServiceScopeFactory _scopeFactory;
    private CancellationTokenSource? _debounceCts;

    public AshfallConnectAutoSyncService(
        ILogger<AshfallConnectAutoSyncService> logger,
        MareMediator mediator,
        IServiceScopeFactory scopeFactory)
        : base(logger, mediator)
    {
        _scopeFactory = scopeFactory;

        Mediator.Subscribe<ConnectedMessage>(this, _ => ScheduleSync());
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        });
    }

    private void ScheduleSync()
    {
        // Annule le précédent debounce avant d'en démarrer un nouveau (anti-leak CTS).
        var previous = _debounceCts;
        _debounceCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Random.Shared.Next(2000, 8000), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                using var scope = _scopeFactory.CreateScope();
                var connectService = scope.ServiceProvider.GetRequiredService<AshfallConnectService>();
                var result = await connectService.SyncCharactersAsync(token).ConfigureAwait(false);
                if (result == AshfallConnectService.SyncResult.Synced)
                    Logger.LogDebug("Ashfall Connect : metadata synchronisée");
                else if (result == AshfallConnectService.SyncResult.NotLinked)
                    Logger.LogDebug("Ashfall Connect : UID non lié à un compte (skip sync)");
                // Failed → déjà loggé côté SyncCharactersAsync, on ne ré-emet rien.
            }
            catch (OperationCanceledException) { /* normal — debounce annulé */ }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Ashfall Connect : erreur lors de la sync auto");
            }
        }, token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
        base.Dispose(disposing);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}