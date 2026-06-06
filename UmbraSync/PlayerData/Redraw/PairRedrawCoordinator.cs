using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;

namespace UmbraSync.PlayerData.Redraw;

public sealed class PairRedrawCoordinator : DisposableMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly IpcManager _ipcManager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRedrawAtUtc = DateTime.MinValue;

    public PairRedrawCoordinator(ILogger<PairRedrawCoordinator> logger, MareMediator mediator,
        MareConfigService configService, IpcManager ipcManager)
        : base(logger, mediator)
    {
        _configService = configService;
        _ipcManager = ipcManager;
    }

    // Garde anti-blocage : la coordination est une optimisation, jamais une exigence de
    // correction. Un redraw qui ne complète pas (objet disparu après déconnexion d'une paire)
    // ne doit ni tenir le gate indéfiniment, ni faire attendre indéfiniment les appels suivants
    // — dont certains s'exécutent sur le framework thread (chemin d'apply sériel) : une attente
    // infinie y fige le jeu entier.
    private static readonly TimeSpan GateWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RedrawHoldTimeout = TimeSpan.FromSeconds(10);

    public async Task RedrawAsync(ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!_configService.Current.EnableRedrawCoordination)
        {
            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
            return;
        }

        if (!await _gate.WaitAsync(GateWaitTimeout, token).ConfigureAwait(false))
        {
            // Gate occupé trop longtemps : on exécute sans coordination plutôt que de bloquer
            // la chaîne (comportement identique à la coordination désactivée).
            callerLogger.LogWarning("[{applicationId}] Redraw gate occupé > {timeout}s, exécution sans coordination", applicationId, GateWaitTimeout.TotalSeconds);
            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
            return;
        }
        try
        {
            var minInterval = TimeSpan.FromMilliseconds(Math.Max(0, _configService.Current.MinRedrawIntervalMs));
            if (minInterval > TimeSpan.Zero)
            {
                var elapsed = DateTime.UtcNow - _lastRedrawAtUtc;
                if (elapsed < minInterval)
                {
                    var wait = minInterval - elapsed;
                    callerLogger.LogTrace("[{applicationId}] Redraw throttled, waiting {ms}ms", applicationId, (int)wait.TotalMilliseconds);
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }

            // Timeout de détention : libère le gate même si le redraw ne complète jamais.
            using var holdTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            holdTimeoutCts.CancelAfter(RedrawHoldTimeout);
            try
            {
                await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, holdTimeoutCts.Token).ConfigureAwait(false);
                _lastRedrawAtUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                callerLogger.LogWarning("[{applicationId}] Redraw sans réponse après {timeout}s, gate libéré", applicationId, RedrawHoldTimeout.TotalSeconds);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _gate.Dispose();
    }
}
