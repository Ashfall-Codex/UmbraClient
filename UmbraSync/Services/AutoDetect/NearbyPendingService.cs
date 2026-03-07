using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Services.AutoDetect;

public sealed record PendingEntry(string DisplayName, DateTime ReceivedAtUtc);

public sealed class NearbyPendingService : IMediatorSubscriber
{
    private static readonly TimeSpan ExpirationDuration = TimeSpan.FromMinutes(10);

    private readonly ILogger<NearbyPendingService> _logger;
    private readonly MareMediator _mediator;
    private readonly ApiController _api;
    private readonly AutoDetectRequestService _requestService;
    private readonly NotificationTracker _notificationTracker;
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex ReqRegex = new(@"^Nearby Request: .+ \[(?<uid>[A-Z0-9]+)\]$", RegexOptions.Compiled | RegexOptions.ExplicitCapture, RegexTimeout);
    private static readonly Regex AcceptRegex = new(@"^Nearby Accept: .+ \[(?<uid>[A-Z0-9]+)\]$", RegexOptions.Compiled | RegexOptions.ExplicitCapture, RegexTimeout);

    public NearbyPendingService(ILogger<NearbyPendingService> logger, MareMediator mediator, ApiController api, AutoDetectRequestService requestService, NotificationTracker notificationTracker, MareConfigService configService)
    {
        _logger = logger;
        _mediator = mediator;
        _api = api;
        _requestService = requestService;
        _notificationTracker = notificationTracker;
        _configService = configService;
        _mediator.Subscribe<NotificationMessage>(this, OnNotification);
        _mediator.Subscribe<ManualPairInviteMessage>(this, OnManualPairInvite);
        _mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => CleanupExpired());
        _mediator.Subscribe<DisconnectedMessage>(this, _ => ClearAllOnDisconnect());
        _mediator.Subscribe<PairOfflineMessage>(this, msg => RemoveIfPending(msg.User.UID));
    }

    public MareMediator Mediator => _mediator;

    public IReadOnlyDictionary<string, PendingEntry> Pending => _pending;

    private void OnNotification(NotificationMessage msg)
    {
        // Watch info messages for Nearby request pattern
        if (msg.Type != UmbraSync.MareConfiguration.Models.NotificationType.Info) return;
        var ma = AcceptRegex.Match(msg.Message);
        if (ma.Success)
        {
            var uidA = ma.Groups["uid"].Value;
            if (!string.IsNullOrEmpty(uidA))
            {
                _ = _api.UserAddPair(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uidA)));
                _pending.TryRemove(uidA, out _);
                _requestService.RemovePendingRequestByUid(uidA);
                _notificationTracker.Remove(NotificationCategory.AutoDetect, uidA);
                _logger.LogInformation("NearbyPending: auto-accepted pairing with {uid}", uidA);
                Mediator.Publish(new NotificationMessage(
                    Loc.Get("AutoDetect.Notification.AcceptedTitle"),
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.AcceptedBody"), uidA),
                    NotificationType.Info, TimeSpan.FromSeconds(5)));
            }
            return;
        }

        var m = ReqRegex.Match(msg.Message);
        if (!m.Success) return;
        var uid = m.Groups["uid"].Value;
        if (string.IsNullOrEmpty(uid)) return;
        if (_pending.ContainsKey(uid)) return;
        // Try to extract name as everything before space and '['
        var name = msg.Message;
        try
        {
            var idx = msg.Message.IndexOf(':');
            if (idx >= 0) name = msg.Message[(idx + 1)..].Trim();
            var br = name.LastIndexOf('[');
            if (br > 0) name = name[..br].Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse nearby pending name, using UID");
            name = uid;
        }
        RegisterPending(uid, name);
    }

    private void OnManualPairInvite(ManualPairInviteMessage msg)
    {
        if (!string.Equals(msg.TargetUid, _api.UID, StringComparison.Ordinal))
            return;

        var display = !string.IsNullOrWhiteSpace(msg.SourceAlias)
            ? msg.SourceAlias
            : (!string.IsNullOrWhiteSpace(msg.DisplayName) ? msg.DisplayName! : msg.SourceUid);

        RegisterPending(msg.SourceUid, display);
    }

    private void RegisterPending(string uid, string displayName)
    {
        _pending[uid] = new PendingEntry(displayName, DateTime.UtcNow);
        _logger.LogInformation("NearbyPending: received request from {uid} ({name})", uid, displayName);
        _notificationTracker.Upsert(NotificationEntry.AutoDetect(uid, displayName));

        if (!_configService.Current.UseInteractivePairRequestPopup)
        {
            _mediator.Publish(new NotificationMessage(
                Loc.Get("AutoDetect.Notification.IncomingTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.IncomingBody"), displayName, uid),
                NotificationType.Info, TimeSpan.FromSeconds(5)));
        }
    }

    private void CleanupExpired()
    {
        var cutoff = DateTime.UtcNow - ExpirationDuration;
        foreach (var kvp in _pending)
        {
            if (kvp.Value.ReceivedAtUtc < cutoff && _pending.TryRemove(kvp.Key, out _))
            {
                _notificationTracker.Remove(NotificationCategory.AutoDetect, kvp.Key);
                _logger.LogDebug("NearbyPending: expired request from {uid}", kvp.Key);
            }
        }
    }

    private void RemoveIfPending(string uid)
    {
        if (_pending.TryRemove(uid, out var entry))
        {
            _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);
            _logger.LogInformation("NearbyPending: removed incoming invite from {uid} (user went offline)", uid);
            _mediator.Publish(new NotificationMessage(
                Loc.Get("AutoDetect.Notification.DisconnectedTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.DisconnectedIncoming"), entry.DisplayName),
                MareConfiguration.Models.NotificationType.Info, TimeSpan.FromSeconds(5)));
        }
    }

    private void ClearAllOnDisconnect()
    {
        if (_pending.IsEmpty) return;
        foreach (var uid in _pending.Keys)
        {
            _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);
        }
        _pending.Clear();
        _logger.LogInformation("NearbyPending: cleared all incoming invitations (disconnected)");
    }

    public void Remove(string uid)
    {
        _pending.TryRemove(uid, out _);
        _requestService.RemovePendingRequestByUid(uid);
        _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);
    }

    public async Task<bool> AcceptAsync(string uid)
    {
        try
        {
            await _api.UserAddPair(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uid))).ConfigureAwait(false);
            _pending.TryRemove(uid, out _);
            _requestService.RemovePendingRequestByUid(uid);
            _ = _requestService.SendAcceptNotifyAsync(uid);
            _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NearbyPending: accept failed for {uid}", uid);
            return false;
        }
    }
}