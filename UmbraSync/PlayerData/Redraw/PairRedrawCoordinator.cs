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

    public async Task RedrawAsync(ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!_configService.Current.EnableRedrawCoordination)
        {
            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
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

            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
            _lastRedrawAtUtc = DateTime.UtcNow;
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
