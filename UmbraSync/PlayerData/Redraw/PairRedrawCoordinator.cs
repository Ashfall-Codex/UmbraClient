using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.PlayerData.Redraw;

[StructLayout(LayoutKind.Auto)]
public readonly record struct PairRedrawBaseline(nint Address, int ObjectIndex, long Sequence)
{
    public bool IsValid => Address != nint.Zero && ObjectIndex >= 0;
}

public sealed class PairRedrawCoordinator : DisposableMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _eventLock = new();
    private readonly Dictionary<int, long> _lastRedrawSequenceByIndex = [];
    private long _redrawSequence;
    private DateTime _lastRedrawAtUtc = DateTime.MinValue;

    public PairRedrawCoordinator(ILogger<PairRedrawCoordinator> logger, MareMediator mediator,
        MareConfigService configService, IpcManager ipcManager, DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _configService = configService;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;

        Mediator.Subscribe<PenumbraRedrawMessage>(this, msg => RecordPenumbraRedraw(msg.ObjTblIdx));
    }

    private void RecordPenumbraRedraw(int objectIndex)
    {
        if (objectIndex < 0) return;
        lock (_eventLock)
        {
            _lastRedrawSequenceByIndex[objectIndex] = ++_redrawSequence;
        }
    }

    public PairRedrawBaseline CaptureBaseline(nint address)
    {
        var index = ReadObjectIndex(address);
        if (index < 0) return default;

        lock (_eventLock)
        {
            _lastRedrawSequenceByIndex.TryGetValue(index, out var sequence);
            return new PairRedrawBaseline(address, index, sequence);
        }
    }

    private bool HasRedrawSince(PairRedrawBaseline baseline, nint currentAddress)
    {
        if (!baseline.IsValid || currentAddress != baseline.Address) return false;
        if (ReadObjectIndex(currentAddress) != baseline.ObjectIndex) return false;

        lock (_eventLock)
        {
            return _lastRedrawSequenceByIndex.TryGetValue(baseline.ObjectIndex, out var sequence) && sequence > baseline.Sequence;
        }
    }

    private static unsafe int ReadObjectIndex(nint address)
    {
        if (address == nint.Zero) return -1;
        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->ObjectIndex;
    }

    private static readonly TimeSpan GateWaitTimeout = TimeSpan.FromSeconds(3);
    // Frames de settle avant un soft-reapply différé (changements texture/material seuls), cf. Lightless.
    private const int DeferredSoftReapplyFrames = 5;

    /// <summary>
    /// Exécute la décision de redraw. Hard → redraw Penumbra complet (avec espacement) ; Soft →
    /// réapplication Glamourer directe (sans flicker) ; DeferredSoft → settle de quelques frames puis soft.
    /// Tout est gardé en amont par EnableSoftRedraw : si OFF, l'appelant force HardRedraw.
    /// </summary>
    public async Task ExecuteDecisionAsync(PairRedrawDecision decision, ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token,
        PairRedrawBaseline? baseline = null)
    {
        switch (decision)
        {
            case PairRedrawDecision.None:
                return;

            case PairRedrawDecision.SoftReapply:
                if (AlreadyRedrawn(baseline, handler, callerLogger, applicationId, decision)) return;
                callerLogger.LogDebug("[{applicationId}] Redraw decision: SoftReapply", applicationId);
                await _ipcManager.Glamourer.ReapplyDirectAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
                return;

            case PairRedrawDecision.DeferredSoftReapply:
                callerLogger.LogDebug("[{applicationId}] Redraw decision: DeferredSoftReapply", applicationId);
                await _dalamudUtil.WaitForFrameworkFramesAsync(DeferredSoftReapplyFrames, token).ConfigureAwait(false);
                if (AlreadyRedrawn(baseline, handler, callerLogger, applicationId, decision)) return;
                await _ipcManager.Glamourer.ReapplyDirectAsync(callerLogger, handler, applicationId, token).ConfigureAwait(false);
                return;

            default: // HardRedraw (et tout cas inattendu, par prudence)
                if (AlreadyRedrawn(baseline, handler, callerLogger, applicationId, decision)) return;
                callerLogger.LogDebug("[{applicationId}] Redraw decision: HardRedraw", applicationId);
                await RedrawAsync(callerLogger, handler, applicationId, token, baseline).ConfigureAwait(false);
                return;
        }
    }

    // Un redraw Penumbra survenu après la pose des mods a rechargé l'acteur avec ceux-ci :
    // ni la réapplication Glamourer ni un second redraw n'apportent quoi que ce soit.
    private bool AlreadyRedrawn(PairRedrawBaseline? baseline, GameObjectHandler handler, ILogger callerLogger, Guid applicationId, PairRedrawDecision decision)
    {
        if (baseline is not { } captured || !HasRedrawSince(captured, handler.Address)) return false;
        callerLogger.LogDebug("[{applicationId}] Redraw decision: {decision} ignoré, Penumbra a déjà redessiné l'acteur depuis la pose des mods", applicationId, decision);
        return true;
    }

    public async Task RedrawAsync(ILogger callerLogger, GameObjectHandler handler, Guid applicationId, CancellationToken token,
        PairRedrawBaseline? baseline = null)
    {
        // Revérifié sur le framework thread juste avant l'IPC : un redraw survenu pendant l'attente
        // du créneau ou de l'espacement rend le nôtre inutile.
        Func<bool>? skipIfRedrawn = baseline is { } captured
            ? () =>
            {
                if (!HasRedrawSince(captured, handler.Address)) return false;
                callerLogger.LogDebug("[{applicationId}] HardRedraw ignoré au dernier moment, Penumbra a redessiné l'acteur entre-temps", applicationId);
                return true;
            }
            : null;

        if (!_configService.Current.EnableRedrawCoordination)
        {
            await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token, skipIfRedrawn).ConfigureAwait(false);
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
            // Le stamp doit être posé même sans le verrou : sinon le waiter suivant compare son
            // elapsed à un timestamp périmé, se croit en droit de partir tout de suite, et
            // l'espacement disparaît exactement quand il sert (parcelle bondée, applications simultanées).
            _lastRedrawAtUtc = DateTime.UtcNow;
            callerLogger.LogTrace("[{applicationId}] Redraw gate occupé > {timeout}s, espacement ignoré", applicationId, GateWaitTimeout.TotalSeconds);
        }
        
        await _ipcManager.Penumbra.RedrawAsync(callerLogger, handler, applicationId, token, skipIfRedrawn).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _gate.Dispose();
    }
}
