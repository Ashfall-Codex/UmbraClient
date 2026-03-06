using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using UmbraSync.WebAPI.AutoDetect;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.Services.AutoDetect;

public class AutoDetectRequestService
{
    private readonly ILogger<AutoDetectRequestService> _logger;
    private readonly DiscoveryConfigProvider _configProvider;
    private readonly DiscoveryApiClient _client;
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamud;
    private readonly MareMediator _mediator;
    private readonly NotificationTracker _notificationTracker;
    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, DateTime> _activeCooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefusalTracker> _refusalTrackers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingRequestInfo> _pendingRequests = new(StringComparer.Ordinal);
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefusalLockDuration = TimeSpan.FromMinutes(15);
    private readonly Lazy<ApiController> _apiController;

    public AutoDetectRequestService(ILogger<AutoDetectRequestService> logger, DiscoveryConfigProvider configProvider, DiscoveryApiClient client, MareConfigService configService, MareMediator mediator, DalamudUtilService dalamudUtilService, IServiceProvider serviceProvider, NotificationTracker notificationTracker)
    {
        _logger = logger;
        _configProvider = configProvider;
        _client = client;
        _configService = configService;
        _mediator = mediator;
        _dalamud = dalamudUtilService;
        _notificationTracker = notificationTracker;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
    }

    public async Task<bool> SendRequestAsync(string? token, string? uid = null, string? targetDisplayName = null, CancellationToken ct = default)
    {
        if (!_configService.Current.AllowAutoDetectPairRequests)
        {
            _logger.LogDebug("Nearby request blocked: AllowAutoDetectPairRequests is disabled");
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Blocked.Title"), Loc.Get("Notification.Nearby.Blocked.Body"), NotificationType.Info));
            return false;
        }

        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(uid))
        {
            _logger.LogDebug("Nearby request blocked: no token or UID provided");
            return false;
        }

        var targetKey = BuildTargetKey(uid, token, targetDisplayName);
        if (!string.IsNullOrEmpty(targetKey))
        {
            var now = DateTime.UtcNow;
            using (_syncRoot.EnterScope())
            {
                if (_refusalTrackers.TryGetValue(targetKey, out var tracker))
                {
                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value > now)
                    {
                        PublishLockNotification(tracker.LockUntil.Value - now);
                        return false;
                    }

                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value <= now)
                    {
                        tracker.LockUntil = null;
                        tracker.Count = 0;
                        if (tracker.Count == 0 && tracker.LockUntil == null)
                        {
                            _refusalTrackers.Remove(targetKey);
                        }
                    }
                }

                if (_activeCooldowns.TryGetValue(targetKey, out var lastSent))
                {
                    var elapsed = now - lastSent;
                    if (elapsed < RequestCooldown)
                    {
                        PublishCooldownNotification(RequestCooldown - elapsed);
                        return false;
                    }

                    if (elapsed >= RequestCooldown)
                    {
                        _activeCooldowns.Remove(targetKey);
                    }
                }
            }
        }
        var endpoint = _configProvider.RequestEndpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogDebug("No request endpoint configured");
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Failed.Title"), Loc.Get("Notification.Nearby.Failed.NoEndpoint"), NotificationType.Error));
            _notificationTracker.Upsert(NotificationEntry.NearbyRequestFailed(Loc.Get("Notification.Nearby.Failed.NoEndpoint")));
            return false;
        }
        string? displayName = null;
        try
        {
            var me = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
            displayName = me?.Name.TextValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine player display name for nearby request");
        }

        var requestToken = string.IsNullOrEmpty(token) ? null : token;
        var requestUid = requestToken == null ? uid : null;

        _logger.LogInformation("Nearby: sending pair request via {endpoint}", endpoint);
        var ok = await _client.SendRequestAsync(endpoint!, requestToken, requestUid, displayName, ct).ConfigureAwait(false);
        if (ok)
        {
            if (!string.IsNullOrEmpty(targetKey))
            {
                using (_syncRoot.EnterScope())
                {
                    _activeCooldowns[targetKey] = DateTime.UtcNow;
                    if (_refusalTrackers.TryGetValue(targetKey, out var tracker))
                    {
                        tracker.Count = 0;
                        tracker.LockUntil = null;
                        if (tracker.Count == 0 && tracker.LockUntil == null)
                        {
                            _refusalTrackers.Remove(targetKey);
                        }
                    }
                }
            }
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Sent.Title"), Loc.Get("Notification.Nearby.Sent.Body"), NotificationType.Info));
            var pendingKey = EnsureTargetKey(targetKey);
            var label = !string.IsNullOrWhiteSpace(targetDisplayName)
                ? targetDisplayName!
                : (!string.IsNullOrEmpty(uid) ? uid : (!string.IsNullOrEmpty(token) ? token : pendingKey));
            _pendingRequests[pendingKey] = new PendingRequestInfo(pendingKey, uid, token, label, DateTime.UtcNow);
        }
        else
        {
            if (!string.IsNullOrEmpty(targetKey))
            {
                var now = DateTime.UtcNow;
                using (_syncRoot.EnterScope())
                {
                    _activeCooldowns.Remove(targetKey);
                    if (!_refusalTrackers.TryGetValue(targetKey, out var tracker))
                    {
                        tracker = new RefusalTracker();
                        _refusalTrackers[targetKey] = tracker;
                    }

                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value <= now)
                    {
                        tracker.LockUntil = null;
                        tracker.Count = 0;
                    }

                    tracker.Count++;
                    if (tracker.Count >= 3)
                    {
                        tracker.LockUntil = now.Add(RefusalLockDuration);
                    }
                }
                _pendingRequests.TryRemove(targetKey, out _);
            }
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Failed.Title"), Loc.Get("Notification.Nearby.Failed.Rejected"), NotificationType.Warning));
            _notificationTracker.Upsert(NotificationEntry.NearbyRequestFailed(Loc.Get("Notification.Nearby.Failed.Rejected")));
        }
        return ok;
    }

    public async Task<bool> SendAcceptNotifyAsync(string targetUid, CancellationToken ct = default)
    {
        var endpoint = _configProvider.AcceptEndpoint;
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogDebug("No accept endpoint configured");
            return false;
        }
        string? displayName = null;
        try
        {
            var me = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
            displayName = me?.Name.TextValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine player display name for accept notify");
        }
        _logger.LogInformation("Nearby: sending accept notify via {endpoint}", endpoint);
        return await _client.SendAcceptAsync(endpoint!, targetUid, displayName, ct).ConfigureAwait(false);
    }

    public async Task<bool> SendDirectUidRequestAsync(string uid, string? targetDisplayName = null, CancellationToken ct = default)
    {
        if (!_configService.Current.AllowAutoDetectPairRequests)
        {
            _logger.LogDebug("Nearby request blocked: AllowAutoDetectPairRequests is disabled");
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Blocked.Title"), Loc.Get("Notification.Nearby.Blocked.Body"), NotificationType.Info));
            return false;
        }

        if (string.IsNullOrEmpty(uid))
        {
            _logger.LogDebug("Direct pair request aborted: UID is empty");
            return false;
        }

        var targetKey = BuildTargetKey(uid, null, targetDisplayName);
        if (!string.IsNullOrEmpty(targetKey))
        {
            var now = DateTime.UtcNow;
            using (_syncRoot.EnterScope())
            {
                if (_refusalTrackers.TryGetValue(targetKey, out var tracker))
                {
                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value > now)
                    {
                        PublishLockNotification(tracker.LockUntil.Value - now);
                        return false;
                    }

                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value <= now)
                    {
                        tracker.LockUntil = null;
                        tracker.Count = 0;
                        if (tracker.Count == 0 && tracker.LockUntil == null)
                        {
                            _refusalTrackers.Remove(targetKey);
                        }
                    }
                }

                if (_activeCooldowns.TryGetValue(targetKey, out var lastSent))
                {
                    var elapsed = now - lastSent;
                    if (elapsed < RequestCooldown)
                    {
                        PublishCooldownNotification(RequestCooldown - elapsed);
                        return false;
                    }

                    if (elapsed >= RequestCooldown)
                    {
                        _activeCooldowns.Remove(targetKey);
                    }
                }
            }
        }

        try
        {
            await _apiController.Value.UserAddPair(new UserDto(new UserData(uid))).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(targetKey))
            {
                using (_syncRoot.EnterScope())
                {
                    _activeCooldowns[targetKey] = DateTime.UtcNow;
                    if (_refusalTrackers.TryGetValue(targetKey, out var tracker))
                    {
                        tracker.Count = 0;
                        tracker.LockUntil = null;
                        if (tracker.Count == 0 && tracker.LockUntil == null)
                        {
                            _refusalTrackers.Remove(targetKey);
                        }
                    }
                }
            }

            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Sent.Title"), Loc.Get("Notification.Nearby.Sent.Body"), NotificationType.Info));
            var pendingKey = EnsureTargetKey(targetKey);
            var label = !string.IsNullOrWhiteSpace(targetDisplayName) ? targetDisplayName! : uid;
            _pendingRequests[pendingKey] = new PendingRequestInfo(pendingKey, uid, null, label, DateTime.UtcNow);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct pair request failed for {uid}", uid);
            if (!string.IsNullOrEmpty(targetKey))
            {
                var now = DateTime.UtcNow;
                using (_syncRoot.EnterScope())
                {
                    _activeCooldowns.Remove(targetKey);
                    if (!_refusalTrackers.TryGetValue(targetKey, out var tracker))
                    {
                        tracker = new RefusalTracker();
                        _refusalTrackers[targetKey] = tracker;
                    }

                    if (tracker.LockUntil.HasValue && tracker.LockUntil.Value <= now)
                    {
                        tracker.LockUntil = null;
                        tracker.Count = 0;
                    }

                    tracker.Count++;
                    if (tracker.Count >= 3)
                    {
                        tracker.LockUntil = now.Add(RefusalLockDuration);
                    }
                }
                _pendingRequests.TryRemove(targetKey, out _);
            }

            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Failed.Title"), Loc.Get("Notification.Nearby.Failed.Rejected"), NotificationType.Warning));
            _notificationTracker.Upsert(NotificationEntry.NearbyRequestFailed(Loc.Get("Notification.Nearby.Failed.Rejected")));
            return false;
        }
    }

    private static string? BuildTargetKey(string? uid, string? token, string? displayName)
    {
        if (!string.IsNullOrEmpty(uid)) return "uid:" + uid;
        if (!string.IsNullOrEmpty(token)) return "token:" + token;
        if (!string.IsNullOrEmpty(displayName)) return "name:" + displayName;
        return null;
    }

    private void PublishCooldownNotification(TimeSpan remaining)
    {
        var durationText = FormatDuration(remaining);
        _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Cooldown.Title"), string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Nearby.Cooldown.Body"), durationText), NotificationType.Info, TimeSpan.FromSeconds(5)));
    }

    private void PublishLockNotification(TimeSpan remaining)
    {
        var durationText = FormatDuration(remaining);
        _mediator.Publish(new NotificationMessage(Loc.Get("Notification.Nearby.Locked.Title"), string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Nearby.Locked.Body"), durationText), NotificationType.Warning, TimeSpan.FromSeconds(5)));
    }

    private static string FormatDuration(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 1)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            return minutes == 1 ? "1 minute" : minutes + " minutes";
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return seconds == 1 ? "1 seconde" : seconds + " secondes";
    }

    private sealed class RefusalTracker
    {
        public int Count;
        public DateTime? LockUntil;
    }

    public IReadOnlyCollection<PendingRequestInfo> GetPendingRequestsSnapshot()
    {
        return _pendingRequests.Values.OrderByDescending(v => v.SentAt).ToList();
    }

    public void RemovePendingRequestByUid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        foreach (var kvp in _pendingRequests)
        {
            if (string.Equals(kvp.Value.Uid, uid, StringComparison.Ordinal))
            {
                _pendingRequests.TryRemove(kvp.Key, out _);
                break;
            }
        }
    }

    public void RemovePendingRequestByKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _pendingRequests.TryRemove(key, out _);
    }

    private static string EnsureTargetKey(string? targetKey)
    {
        return !string.IsNullOrEmpty(targetKey) ? targetKey : "target:" + Guid.NewGuid().ToString("N");
    }

    public sealed record PendingRequestInfo(string Key, string? Uid, string? Token, string TargetDisplayName, DateTime SentAt);
}