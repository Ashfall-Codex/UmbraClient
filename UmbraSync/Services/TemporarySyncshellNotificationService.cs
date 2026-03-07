using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.API.Dto.Group;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Services;

public sealed class TemporarySyncshellNotificationService : MediatorSubscriberBase, IHostedService
{
    private static readonly int[] NotificationThresholdMinutes = [30, 15, 5, 1];
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly NotificationTracker _notificationTracker;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, TrackedGroup> _trackedGroups = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public TemporarySyncshellNotificationService(ILogger<TemporarySyncshellNotificationService> logger, MareMediator mediator, PairManager pairManager,
        ApiController apiController, NotificationTracker notificationTracker)
        : base(logger, mediator)
    {
        _pairManager = pairManager;
        _apiController = apiController;
        _notificationTracker = notificationTracker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = new CancellationTokenSource();
        Mediator.Subscribe<ConnectedMessage>(this, _ => ResetTrackedGroups());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ResetTrackedGroups());
        _loopTask = Task.Run(() => MonitorLoopAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Mediator.UnsubscribeAll(this);
        if (_loopCts == null)
        {
            return;
        }

        try
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            if (_loopTask != null)
            {
                await _loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // loop already cancelled
        }
        finally
        {
            _loopTask = null;
            _loopCts.Dispose();
            _loopCts = null;
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                CheckGroups();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to check temporary syncshell expirations");
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckGroups()
    {
        var nowUtc = DateTime.UtcNow;
        var groupsSnapshot = _pairManager.Groups.Values.ToList();
        var notifications = new List<NotificationPayload>();
        var expiredGroups = new List<GroupFullInfoDto>();
        var seenTemporaryGids = new HashSet<string>(StringComparer.Ordinal);

        using (var _ = _stateLock.EnterScope())
        {
            foreach (var group in groupsSnapshot)
            {
                if (!group.IsTemporary || group.ExpiresAt == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(_apiController.UID) || !string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal))
                {
                    continue;
                }

                var gid = group.Group.GID;
                seenTemporaryGids.Add(gid);
                var expiresAtUtc = NormalizeToUtc(group.ExpiresAt.Value);
                var remaining = expiresAtUtc - nowUtc;

                if (!_trackedGroups.TryGetValue(gid, out var state))
                {
                    state = new TrackedGroup(expiresAtUtc);
                    _trackedGroups[gid] = state;
                }
                else if (state.ExpiresAtUtc != expiresAtUtc)
                {
                    state.UpdateExpiresAt(expiresAtUtc);
                }

                if (remaining <= TimeSpan.Zero)
                {
                    // Expiration reached: notify that the syncshell is no longer publicly visible
                    try
                    {
                        Mediator.Publish(new SyncshellAutoDetectStateChanged(gid, false, false));
                    }
                    catch
                    {
                        // ignore publish failures
                    }

                    _trackedGroups.Remove(gid);
                    expiredGroups.Add(group);
                    continue;
                }

                if (!state.LastRemaining.HasValue)
                {
                    state.UpdateRemaining(remaining);
                    continue;
                }

                var previousRemaining = state.LastRemaining.Value;

                foreach (var thresholdMinutes in NotificationThresholdMinutes)
                {
                    var threshold = TimeSpan.FromMinutes(thresholdMinutes);
                    if (previousRemaining > threshold && remaining <= threshold)
                    {
                        notifications.Add(new NotificationPayload(group, thresholdMinutes, expiresAtUtc));
                    }
                }

                state.UpdateRemaining(remaining);
            }

            var toRemove = _trackedGroups.Keys.Where(k => !seenTemporaryGids.Contains(k)).ToList();
            foreach (var gid in toRemove)
            {
                _trackedGroups.Remove(gid);
            }
        }

        foreach (var expiredGroup in expiredGroups)
        {
            Logger.LogInformation("Temporary syncshell {gid} expired locally; removing", expiredGroup.Group.GID);
            _pairManager.RemoveGroup(expiredGroup.Group);
        }

        foreach (var notification in notifications)
        {
            PublishNotification(notification.Group, notification.ThresholdMinutes, notification.ExpiresAtUtc);
        }
    }

    private void PublishNotification(GroupFullInfoDto group, int thresholdMinutes, DateTime expiresAtUtc)
    {
        string displayName = string.IsNullOrWhiteSpace(group.GroupAlias) ? group.Group.GID : group.GroupAlias!;
        string threshold = thresholdMinutes == 1 ? "1 minute" : $"{thresholdMinutes} minutes";
        string expiresLocal = expiresAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        string message = $"La Syncshell temporaire \"{displayName}\" sera supprimee dans {threshold} (a {expiresLocal}).";
        Mediator.Publish(new NotificationMessage("Syncshell temporaire", message, NotificationType.Warning, TimeSpan.FromSeconds(6)));
        _notificationTracker.Upsert(NotificationEntry.TemporarySyncshellExpiring(displayName, thresholdMinutes, expiresLocal));
    }

    private static DateTime NormalizeToUtc(DateTime expiresAt)
    {
        return expiresAt.Kind switch
        {
            DateTimeKind.Utc => expiresAt,
            DateTimeKind.Local => expiresAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)
        };
    }

    private void ResetTrackedGroups()
    {
        using (var _ = _stateLock.EnterScope())
        {
            _trackedGroups.Clear();
        }
    }

    private sealed class TrackedGroup
    {
        public TrackedGroup(DateTime expiresAtUtc)
        {
            ExpiresAtUtc = expiresAtUtc;
        }

        public DateTime ExpiresAtUtc { get; private set; }
        public TimeSpan? LastRemaining { get; private set; }

        public void UpdateExpiresAt(DateTime expiresAtUtc)
        {
            ExpiresAtUtc = expiresAtUtc;
            LastRemaining = null;
        }

        public void UpdateRemaining(TimeSpan remaining)
        {
            LastRemaining = remaining;
        }
    }

    private sealed record NotificationPayload(GroupFullInfoDto Group, int ThresholdMinutes, DateTime ExpiresAtUtc);
}