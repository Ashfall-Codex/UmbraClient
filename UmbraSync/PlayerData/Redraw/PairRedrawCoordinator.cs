using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.PlayerData.Redraw;

public sealed class PairRedrawCoordinator : DisposableMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRedrawAtUtc = DateTime.MinValue;

    public PairRedrawCoordinator(ILogger<PairRedrawCoordinator> logger, MareMediator mediator,
        MareConfigService configService, IpcManager ipcManager, DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _configService = configService;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
    }

    private static readonly TimeSpan GateWaitTimeout = TimeSpan.FromSeconds(3);
    // Frames de settle avant un soft-reapply différé (changements texture/material seuls), cf. Lightless.
    private const int DeferredSoftReapplyFrames = 5;

    /// <summary>
    /// Exécute la décision de redraw. Hard → redraw Penumbra complet (avec espacement) ; Soft →
    /// réapplication Glamourer directe (sans flicker) ; DeferredSoft → settle de quelques frames puis soft.
    /// Tout est gardé en amont par EnableSoftRedraw : si OFF, l'appelant force HardRedraw.
    /// </summary>
    public async Task ExecuteDecisionAsync(PairRedrawDecision decision, ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        switch (decision)
        {
            case PairRedrawDecision.None:
                return;

            case PairRedrawDecision.SoftReapply:
                callerLogger.LogDebug("[{applicationId}] Redraw decision: SoftReapply", applicationId);
                await _ipcManager.Glamourer.ReapplyDirectAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
                return;

            case PairRedrawDecision.DeferredSoftReapply:
                callerLogger.LogDebug("[{applicationId}] Redraw decision: DeferredSoftReapply", applicationId);
                await _dalamudUtil.WaitForFrameworkFramesAsync(DeferredSoftReapplyFrames, token).ConfigureAwait(false);
                await _ipcManager.Glamourer.ReapplyDirectAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
                return;

            default: // HardRedraw (et tout cas inattendu, par prudence)
                callerLogger.LogDebug("[{applicationId}] Redraw decision: HardRedraw", applicationId);
                await RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
                return;
        }
    }

    public async Task RedrawAsync(ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!_configService.Current.EnableRedrawCoordination)
        {
            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
            return;
        }
        
        if (await _gate.WaitAsync(GateWaitTimeout, token).ConfigureAwait(false))
        {
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
                _lastRedrawAtUtc = DateTime.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            callerLogger.LogTrace("[{applicationId}] Redraw gate occupé > {timeout}s, espacement ignoré", applicationId, GateWaitTimeout.TotalSeconds);
        }
        
        await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _gate.Dispose();
    }
}
