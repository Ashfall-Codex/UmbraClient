using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Services.AutoDetect;

public sealed record PendingEntry(string DisplayName, DateTime ReceivedAtUtc);

public sealed class NearbyPendingService : IMediatorSubscriber, IDisposable
{
    private static readonly TimeSpan ExpirationDuration = TimeSpan.FromMinutes(10);

    private readonly ILogger<NearbyPendingService> _logger;
    private readonly MareMediator _mediator;
    private readonly ApiController _api;
    private readonly AutoDetectRequestService _requestService;
    private readonly NotificationTracker _notificationTracker;
    private readonly MareConfigService _configService;
    private readonly Lazy<PairManager> _pairManager;
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _declineCooldowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _recentAcceptNotifications = new(StringComparer.Ordinal);
    private static readonly TimeSpan AcceptNotificationDedupWindow = TimeSpan.FromSeconds(30);
    private CancellationTokenSource _connectionCts = new();
    private bool _disposed;

    public NearbyPendingService(ILogger<NearbyPendingService> logger, MareMediator mediator, ApiController api, AutoDetectRequestService requestService, NotificationTracker notificationTracker, MareConfigService configService, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _mediator = mediator;
        _api = api;
        _requestService = requestService;
        _notificationTracker = notificationTracker;
        _configService = configService;
        _pairManager = new Lazy<PairManager>(() => serviceProvider.GetRequiredService<PairManager>());
        _mediator.Subscribe<ManualPairInviteMessage>(this, OnManualPairInvite);
        _mediator.Subscribe<PairRequestAcceptedMessage>(this, OnPairRequestAccepted);
        _mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => CleanupExpired());
        _mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());
        _mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            CancelConnectionWork();
            ClearAllOnDisconnect();
        });
        _mediator.Subscribe<PairOfflineMessage>(this, msg => RemoveIfPending(msg.User.UID));
        _mediator.Subscribe<NearbyDetectionToggled>(this, msg =>
        {
            if (!msg.Enabled)
            {
                ClearAllOnDisconnect();
                _logger.LogInformation("NearbyPending: cleared all invitations (AutoDetect disabled)");
            }
        });
    }

    public MareMediator Mediator => _mediator;

    public IReadOnlyDictionary<string, PendingEntry> Pending => _pending;

    private void OnManualPairInvite(ManualPairInviteMessage msg)
    {
        if (!string.Equals(msg.TargetUid, _api.UID, StringComparison.Ordinal))
            return;

        var display = !string.IsNullOrWhiteSpace(msg.SourceAlias)
            ? msg.SourceAlias
            : (!string.IsNullOrWhiteSpace(msg.DisplayName) ? msg.DisplayName! : msg.SourceUid);

        // On avait nous-même invité ce joueur : sa « demande » est en fait son acceptation.
        // On complète la paire sans redemander de confirmation.
        if (!_configService.Current.AutoDetectBlockedUids.Contains(msg.SourceUid, StringComparer.Ordinal)
            && _requestService.TryTakeOutgoingTo(msg.SourceUid, out var outgoing))
        {
            _ = CompleteAcceptedOutgoingAsync(msg.SourceUid, outgoing.TargetDisplayName, display);
            return;
        }

        RegisterPending(msg.SourceUid, display);
    }

    // Le serveur a complété la paire après l'acceptation de la cible : rien à faire de plus que prévenir.
    private void OnPairRequestAccepted(PairRequestAcceptedMessage msg)
    {
        var uid = msg.Acceptor.UID;
        var label = _requestService.TryTakeOutgoingTo(uid, out var outgoing)
            ? outgoing.TargetDisplayName
            : msg.Acceptor.AliasOrUID;

        if (_pending.TryRemove(uid, out _))
            _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);

        _logger.LogInformation("NearbyPending: request to {uid} accepted by the other player", uid);
        PublishAccepted(uid, label);
    }

    // Le callback serveur et le filet client peuvent arriver tous les deux pour la même acceptation
    private void PublishAccepted(string uid, string label)
    {
        var now = DateTime.UtcNow;
        if (_recentAcceptNotifications.TryGetValue(uid, out var last) && now - last < AcceptNotificationDedupWindow)
            return;
        _recentAcceptNotifications[uid] = now;
        foreach (var kvp in _recentAcceptNotifications)
        {
            if (now - kvp.Value >= AcceptNotificationDedupWindow)
                _recentAcceptNotifications.TryRemove(kvp.Key, out _);
        }

        _mediator.Publish(new NotificationMessage(
            Loc.Get("AutoDetect.Notification.AcceptedTitle"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.AcceptedBody"), label),
            NotificationType.Success, TimeSpan.FromSeconds(5)));
    }

    private async Task CompleteAcceptedOutgoingAsync(string uid, string outgoingLabel, string fallbackDisplay)
    {
        try
        {
            if (!_pairManager.Value.IsAlreadyDirectPaired(uid))
                await _api.UserAddPair(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uid))).ConfigureAwait(false);

            _logger.LogInformation("NearbyPending: outgoing invite to {uid} accepted, pair completed automatically", uid);
            PublishAccepted(uid, outgoingLabel);
        }
        catch (Exception ex)
        {
            // Échec de l'ajout automatique : on retombe sur une invitation classique à accepter à la main
            _logger.LogWarning(ex, "NearbyPending: automatic pair completion failed for {uid}", uid);
            RegisterPending(uid, fallbackDisplay);
        }
    }

    private void RegisterPending(string uid, string displayName)
    {
        // Vérifier la blacklist persistante
        if (_configService.Current.AutoDetectBlockedUids.Contains(uid, StringComparer.Ordinal))
        {
            _logger.LogDebug("NearbyPending: invitation from {uid} ignored (player blocked)", uid);
            return;
        }

        // Vérifier le cooldown de refus
        if (_declineCooldowns.TryGetValue(uid, out var expiresAt))
        {
            if (DateTime.UtcNow < expiresAt)
            {
                _logger.LogDebug("NearbyPending: invitation from {uid} ignored (decline cooldown active until {expires})", uid, expiresAt);
                return;
            }
            _declineCooldowns.TryRemove(uid, out _);
        }

        _pending[uid] = new PendingEntry(displayName, DateTime.UtcNow);
        _logger.LogInformation("NearbyPending: received request from {uid}", uid);
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

    public void Decline(string uid)
    {
        _pending.TryRemove(uid, out _);
        _requestService.RemovePendingRequestByUid(uid);
        _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);

        var cooldownMinutes = _configService.Current.AutoDetectDeclineCooldownMinutes;
        if (cooldownMinutes > 0)
        {
            _declineCooldowns[uid] = DateTime.UtcNow.AddMinutes(cooldownMinutes);
            _logger.LogInformation("NearbyPending: declined {uid}, cooldown {minutes} min", uid, cooldownMinutes);
        }
    }

    public void Block(string uid)
    {
        _pending.TryRemove(uid, out _);
        _requestService.RemovePendingRequestByUid(uid);
        _notificationTracker.Remove(NotificationCategory.AutoDetect, uid);

        // La liste locale reste un filet : elle filtre même si le serveur n'est pas à jour
        var blockedList = _configService.Current.AutoDetectBlockedUids;
        if (!blockedList.Contains(uid, StringComparer.Ordinal))
        {
            blockedList.Add(uid);
            _configService.Save();
            _logger.LogInformation("NearbyPending: blocked {uid} permanently", uid);
        }

        _ = PushBlockToServerAsync(uid, block: true);
    }

    public void Unblock(string uid)
    {
        _configService.Current.AutoDetectBlockedUids.RemoveAll(u => string.Equals(u, uid, StringComparison.Ordinal));
        _configService.Save();
        _logger.LogInformation("NearbyPending: unblocked {uid}", uid);

        _ = PushBlockToServerAsync(uid, block: false);
    }

    private async Task PushBlockToServerAsync(string uid, bool block)
    {
        try
        {
            var dto = new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uid));
            if (block)
                await _api.UserBlock(dto).ConfigureAwait(false);
            else
                await _api.UserUnblock(dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Hors ligne ou serveur sans la méthode : la liste locale sera repoussée à la prochaine connexion
            _logger.LogDebug(ex, "NearbyPending: could not sync {action} of {uid} to the server", block ? "block" : "unblock", uid);
            if (block && _configService.Current.AutoDetectBlocksSyncedToServer)
            {
                _configService.Current.AutoDetectBlocksSyncedToServer = false;
                _configService.Save();
            }
        }
    }

    private void OnConnected()
    {
        CancelConnectionWork();
        var token = _connectionCts.Token;
        _ = SyncBlocksWithServerAsync(token);
    }

    /// <summary>
    /// Pousse une fois les blocages locaux vers le serveur, puis récupère ceux du serveur (autre appareil,
    /// réinstallation). Si le serveur ne connaît pas encore ces méthodes, on réessaie à la connexion suivante.
    /// </summary>
    private async Task SyncBlocksWithServerAsync(CancellationToken token)
    {
        try
        {
            // Règle anti-burst au connect
            await Task.Delay(Random.Shared.Next(2000, 8000), token).ConfigureAwait(false);

            if (!_configService.Current.AutoDetectBlocksSyncedToServer)
            {
                foreach (var uid in _configService.Current.AutoDetectBlockedUids.ToList())
                {
                    token.ThrowIfCancellationRequested();
                    await _api.UserBlock(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uid))).ConfigureAwait(false);
                }

                _configService.Current.AutoDetectBlocksSyncedToServer = true;
                _configService.Save();
                _logger.LogInformation("NearbyPending: local block list pushed to the server ({count} entries)", _configService.Current.AutoDetectBlockedUids.Count);
            }

            token.ThrowIfCancellationRequested();
            var serverBlocks = await _api.UserGetBlockedUsers().ConfigureAwait(false);
            var local = _configService.Current.AutoDetectBlockedUids;
            int added = 0;
            foreach (var user in serverBlocks ?? [])
            {
                if (string.IsNullOrEmpty(user?.UID) || local.Contains(user.UID, StringComparer.Ordinal)) continue;
                local.Add(user.UID);
                added++;
            }

            if (added > 0)
            {
                _configService.Save();
                _logger.LogInformation("NearbyPending: {count} block(s) retrieved from the server", added);
            }
        }
        catch (OperationCanceledException)
        {
            // Déconnexion ou arrêt du plugin
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NearbyPending: block list sync with the server failed, will retry on next connection");
        }
    }

    private void CancelConnectionWork()
    {
        if (_disposed) return;
        try
        {
            _connectionCts.Cancel();
            _connectionCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Déjà libéré
        }

        _connectionCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _mediator.UnsubscribeAll(this);
            _connectionCts.Cancel();
            _connectionCts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NearbyPending: error during dispose");
        }
    }

    public async Task<bool> AcceptAsync(string uid)
    {
        try
        {
            if (_pairManager.Value.IsAlreadyDirectPaired(uid))
            {
                _logger.LogInformation("NearbyPending: AcceptAsync skipped UserAddPair for {uid} (already direct paired locally)", uid);
            }
            else
            {
                await _api.UserAddPair(new UmbraSync.API.Dto.User.UserDto(new UmbraSync.API.Data.UserData(uid))).ConfigureAwait(false);
            }
            // Le serveur prévient lui-même le demandeur (Client_PairRequestAccepted) ; un ancien serveur
            // lui renvoie une demande, que son client complète automatiquement.
            _pending.TryRemove(uid, out _);
            _requestService.RemovePendingRequestByUid(uid);
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