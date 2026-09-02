using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.Group;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.AutoDetect;

public sealed class PermanentSyncshellAutoDetectMonitor : MediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly SyncshellDiscoveryService _discoveryService;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, (bool Visible, bool PwdTmp)> _states = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _immediateCheckRequested;

    public PermanentSyncshellAutoDetectMonitor(ILogger<PermanentSyncshellAutoDetectMonitor> logger, MareMediator mediator,
        ApiController apiController, PairManager pairManager, SyncshellDiscoveryService discoveryService)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _discoveryService = discoveryService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = new CancellationTokenSource();
        Mediator.Subscribe<ConnectedMessage>(this, _ => Reset());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => Reset());
        Mediator.Subscribe<SyncshellDiscoveryUpdated>(this, _ => RequestImmediateCheck());

        _loopTask = Task.Run(() => MonitorLoopAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Mediator.UnsubscribeAll(this);
        if (_loopCts == null) return;
        await BackgroundLoop.StopAsync(_loopCts, _loopTask).ConfigureAwait(false);
        _loopTask = null;
        _loopCts = null;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(10);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[AutoDetect] Permanent monitor check failed");
            }
            var slept = TimeSpan.Zero;
            while (slept < delay && !ct.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _immediateCheckRequested, 0) == 1)
                {
                    break;
                }

                try
                {
                    var step = TimeSpan.FromMilliseconds(200);
                    await Task.Delay(step, ct).ConfigureAwait(false);
                    slept += step;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var uid = _apiController.UID;
        if (string.IsNullOrEmpty(uid)) return;
        var groups = _pairManager.Groups.Values
            .Where(g => !g.IsTemporary && string.Equals(g.OwnerUID, uid, StringComparison.Ordinal))
            .Select(g => g.GID)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        using (_stateLock.EnterScope())
        {
            var toRemove = _states.Keys.Where(k => !groups.Contains(k, StringComparer.Ordinal)).ToList();
            foreach (var gid in toRemove)
            {
                _states.Remove(gid);
            }
        }

        foreach (var gid in groups)
        {
            if (ct.IsCancellationRequested) return;

            SyncshellDiscoveryStateDto? state = null;
            try
            {
                state = await _discoveryService.GetStateAsync(gid, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "[AutoDetect] GetStateAsync failed for {gid}", gid);
            }

            if (state == null) continue;

            bool visible = state.AutoDetectVisible;
            bool pwdTmp = state.PasswordTemporarilyDisabled;

            bool shouldPublish = false;
            using (_stateLock.EnterScope())
            {
                if (_states.TryGetValue(gid, out var prev))
                {
                    if (prev.Visible && !visible)
                    {
                        shouldPublish = true;
                    }
                    _states[gid] = (visible, pwdTmp);
                }
                else
                {
                    // Initialiser l'état
                    _states[gid] = (visible, pwdTmp);
                }
            }

            if (shouldPublish)
            {
                try
                {
                    Mediator.Publish(new SyncshellAutoDetectStateChanged(gid, false, pwdTmp));
                }
                catch
                {
                    // ignore publish failures
                }
            }
        }
    }

    private void Reset()
    {
        using (_stateLock.EnterScope())
        {
            _states.Clear();
        }
        RequestImmediateCheck();
    }

    private void RequestImmediateCheck()
    {
        Interlocked.Exchange(ref _immediateCheckRequested, 1);
    }
}