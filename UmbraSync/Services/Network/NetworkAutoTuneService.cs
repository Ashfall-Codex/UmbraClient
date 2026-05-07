using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI;

namespace UmbraSync.Services.Network;

public sealed class NetworkAutoTuneService : DisposableMediatorSubscriberBase, IHostedService
{
    private const int ShortSessionThresholdSeconds = 60;
    private const int WindowSeconds = 180;
    private const int TriggerThreshold = 3;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan RevertCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan GracePeriodAfterLogin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GracePeriodAtStartup = TimeSpan.FromSeconds(90);

    private readonly MareConfigService _configService;
    private readonly ApiController _apiController;
    private readonly Queue<DateTime> _shortSessions = new();
    private readonly Lock _lock = new();

    private DateTime? _lastConnectedAt;
    private DateTime _gracePeriodEnd;
    private Timer? _revertTimer;

    public NetworkAutoTuneService(ILogger<NetworkAutoTuneService> logger, MareMediator mediator,
        MareConfigService configService, ApiController apiController) : base(logger, mediator)
    {
        _configService = configService;
        _apiController = apiController;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _gracePeriodEnd = DateTime.UtcNow + GracePeriodAtStartup;

        Mediator.Subscribe<HubReconnectedMessage>(this, _ => OnHubReconnected());
        Mediator.Subscribe<HubReconnectingMessage>(this, _ => OnHubLost());
        Mediator.Subscribe<HubClosedMessage>(this, _ => OnHubLost());
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => OnLogin());

        _revertTimer = new Timer(_ => SafeCheckRevert(), null, RevertCheckInterval, RevertCheckInterval);
        return Task.CompletedTask;
    }

    private void OnLogin()
    {
        lock (_lock)
        {
            _gracePeriodEnd = DateTime.UtcNow + GracePeriodAfterLogin;
            _shortSessions.Clear();
            _lastConnectedAt = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _revertTimer?.Dispose();
        _revertTimer = null;
        return Task.CompletedTask;
    }

    private void OnHubReconnected()
    {
        lock (_lock)
        {
            _lastConnectedAt = DateTime.UtcNow;
        }
    }

    private void OnHubLost()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (now < _gracePeriodEnd)
            {
                Logger.LogDebug("NetworkAutoTune: in grace period, ignoring hub-lost event");
                _lastConnectedAt = null;
                return;
            }

            if (_lastConnectedAt is { } connectedAt
                && (now - connectedAt).TotalSeconds < ShortSessionThresholdSeconds)
            {
                _shortSessions.Enqueue(now);

                var cutoff = now - TimeSpan.FromSeconds(WindowSeconds);
                while (_shortSessions.Count > 0 && _shortSessions.Peek() < cutoff)
                    _shortSessions.Dequeue();

                Logger.LogDebug("NetworkAutoTune: short session detected ({count}/{threshold} in {window}s window)",
                    _shortSessions.Count, TriggerThreshold, WindowSeconds);

                if (_shortSessions.Count >= TriggerThreshold)
                {
                    TryActivateAutoTune();
                }
            }

            _lastConnectedAt = null;
        }
    }

    private void TryActivateAutoTune()
    {
        var cfg = _configService.Current;
        if (!cfg.NetworkAutoTune || cfg.SlowConnection) return;

        Logger.LogInformation("NetworkAutoTune: activating SlowConnection automatically (network instability detected)");

        cfg.SlowConnection = true;
        cfg.SlowConnectionAutoEnabled = true;
        cfg.SlowConnectionAutoEnabledAt = DateTime.UtcNow;
        _configService.Save();

        _shortSessions.Clear();

        Mediator.Publish(new DualNotificationMessage(
            Loc.Get("Network.AutoTune.Activated.Title"),
            Loc.Get("Network.AutoTune.Activated.Body"),
            NotificationType.Warning,
            TimeSpan.FromSeconds(8)));

        _ = _apiController.CreateConnections();
    }

    private void SafeCheckRevert()
    {
        try
        {
            CheckRevert();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "NetworkAutoTune: revert check failed");
        }
    }

    private void CheckRevert()
    {
        var cfg = _configService.Current;
        if (!cfg.SlowConnectionAutoEnabled) return;
        if (cfg.SlowConnectionAutoEnabledAt is not { } enabledAt) return;
        if (DateTime.UtcNow - enabledAt < RetryAfter) return;

        Logger.LogInformation("NetworkAutoTune: 24h elapsed, reverting SlowConnection to retest network");

        cfg.SlowConnection = false;
        cfg.SlowConnectionAutoEnabled = false;
        cfg.SlowConnectionAutoEnabledAt = null;
        _configService.Save();

        lock (_lock)
        {
            _shortSessions.Clear();
            _lastConnectedAt = null;
        }

        Mediator.Publish(new DualNotificationMessage(
            Loc.Get("Network.AutoTune.Reverted.Title"),
            Loc.Get("Network.AutoTune.Reverted.Body"),
            NotificationType.Info,
            TimeSpan.FromSeconds(6)));

        _ = _apiController.CreateConnections();
    }
}
