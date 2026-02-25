using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Notification;

public enum NotificationCategory
{
    AutoDetect,
    Syncshell,
    McdfShare,
    Connection,
    PluginIntegration,
    Performance,
    UserStatus,
    Profile,
    System,
}

public sealed record NotificationEntry(NotificationCategory Category, string Id, string Title, string? Description, DateTime CreatedAt)
{
    public static NotificationEntry AutoDetect(string uid, string displayName)
    {
        var title = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.AutoDetect.RequestTitle"), displayName);
        var desc = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.AutoDetect.RequestBody"), displayName, uid);
        return new(NotificationCategory.AutoDetect, uid, title, desc, DateTime.UtcNow);
    }

    public static NotificationEntry SyncshellPublic(string gid, string aliasOrGid)
        => new(NotificationCategory.Syncshell, gid,
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Syncshell.Public.Title"), aliasOrGid),
            Loc.Get("Notification.Syncshell.Public.Body"), DateTime.UtcNow);

    public static NotificationEntry SyncshellNotPublic(string gid, string aliasOrGid)
        => new(NotificationCategory.Syncshell, gid,
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Syncshell.NotPublic.Title"), aliasOrGid),
            Loc.Get("Notification.Syncshell.NotPublic.Body"), DateTime.UtcNow);

    public static NotificationEntry McdfShareCreated(Guid shareId, string? description, int individualCount, int syncshellCount)
    {
        string safeDescription = string.IsNullOrEmpty(description)
            ? shareId.ToString("D", CultureInfo.InvariantCulture)
            : description;
        string title = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Created.Title"), safeDescription);
        string targetSummary = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Created.Summary"), individualCount, syncshellCount);
        return new(NotificationCategory.McdfShare, shareId.ToString("D", CultureInfo.InvariantCulture), title, targetSummary, DateTime.UtcNow);
    }

    public static NotificationEntry McdfShareReceived(Guid shareId, string? description, string ownerAliasOrUid)
    {
        string safeDescription = string.IsNullOrEmpty(description)
            ? shareId.ToString("D", CultureInfo.InvariantCulture)
            : description;
        string title = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Received.Title"), safeDescription);
        string body = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Received.Body"), ownerAliasOrUid);
        return new(NotificationCategory.McdfShare, $"received-{shareId:D}", title, body, DateTime.UtcNow);
    }

    // Performance Category
    public static NotificationEntry PlayerAutoBlockedTriangles(string uid, string aliasOrUid, long triUsage, long threshold)
        => new(NotificationCategory.Performance, $"autoblock-tri-{uid}",
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Performance.AutoBlockTriangles.Title"), aliasOrUid),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Performance.AutoBlockTriangles.Body"), triUsage, threshold),
            DateTime.UtcNow);

    public static NotificationEntry PlayerAutoBlockedVRAM(string uid, string aliasOrUid, string vramUsage, long thresholdMiB)
        => new(NotificationCategory.Performance, $"autoblock-vram-{uid}",
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Performance.AutoBlockVRAM.Title"), aliasOrUid),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Performance.AutoBlockVRAM.Body"), vramUsage, thresholdMiB),
            DateTime.UtcNow);

    // PluginIntegration Category
    public static NotificationEntry PenumbraInactive()
        => new(NotificationCategory.PluginIntegration, "penumbra-inactive",
            Loc.Get("Notification.PluginIntegration.PenumbraInactive.Title"),
            Loc.Get("Notification.PluginIntegration.PenumbraInactive.Body"),
            DateTime.UtcNow);

    public static NotificationEntry GlamourerInactive()
        => new(NotificationCategory.PluginIntegration, "glamourer-inactive",
            Loc.Get("Notification.PluginIntegration.GlamourerInactive.Title"),
            Loc.Get("Notification.PluginIntegration.GlamourerInactive.Body"),
            DateTime.UtcNow);

    public static NotificationEntry MissingPlugins(string playerName, List<string> missingPlugins)
        => new(NotificationCategory.PluginIntegration, $"missing-plugins-{playerName}",
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.PluginIntegration.MissingPlugins.Title"), playerName),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.PluginIntegration.MissingPlugins.Body"), string.Join(", ", missingPlugins)),
            DateTime.UtcNow);

    // Connection Category
    public static NotificationEntry ConnectionLost()
        => new(NotificationCategory.Connection, "connection-lost",
            Loc.Get("Notification.Connection.Lost.Title"),
            Loc.Get("Notification.Connection.Lost.Body"),
            DateTime.UtcNow);

    public static NotificationEntry AuthTokenRefreshFailed()
        => new(NotificationCategory.Connection, "auth-refresh-failed",
            Loc.Get("Notification.Connection.AuthFailed.Title"),
            Loc.Get("Notification.Connection.AuthFailed.Body"),
            DateTime.UtcNow);

    // System Category
    public static NotificationEntry ClientIncompatible(string currentVersion, string requiredVersion)
        => new(NotificationCategory.System, "client-incompatible",
            Loc.Get("Notification.System.ClientIncompatible.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.System.ClientIncompatible.Body"), currentVersion, requiredVersion),
            DateTime.UtcNow);

    public static NotificationEntry ClientOutdated(string currentVersion, string latestVersion)
        => new(NotificationCategory.System, "client-outdated",
            Loc.Get("Notification.System.ClientOutdated.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.System.ClientOutdated.Body"), currentVersion, latestVersion),
            DateTime.UtcNow);

    public static NotificationEntry ServerMessageError(string serverName, string message)
        => new(NotificationCategory.System, $"server-error-{DateTime.UtcNow.Ticks}",
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.System.ServerError.Title"), serverName),
            message,
            DateTime.UtcNow);

    public static NotificationEntry ServerMessageWarning(string serverName, string message)
        => new(NotificationCategory.System, $"server-warning-{DateTime.UtcNow.Ticks}",
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.System.ServerWarning.Title"), serverName),
            message,
            DateTime.UtcNow);

    // Profile Category
    public static NotificationEntry ProfileSaveFailed()
        => new(NotificationCategory.Profile, "profile-save-failed",
            Loc.Get("Notification.Profile.SaveFailed.Title"),
            Loc.Get("Notification.Profile.SaveFailed.Body"),
            DateTime.UtcNow);

    // UserStatus Category
    public static NotificationEntry SyncshellJoinFailed(string syncshellName, string reason)
        => new(NotificationCategory.UserStatus, $"syncshell-join-failed-{syncshellName}",
            Loc.Get("Notification.UserStatus.SyncshellJoinFailed.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.UserStatus.SyncshellJoinFailed.Body"), syncshellName, reason),
            DateTime.UtcNow);

    public static NotificationEntry AcceptPairRequestFailed(string uid)
        => new(NotificationCategory.UserStatus, $"accept-failed-{uid}",
            Loc.Get("Notification.UserStatus.AcceptFailed.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.UserStatus.AcceptFailed.Body"), uid),
            DateTime.UtcNow);

    public static NotificationEntry NearbyRequestFailed(string reason)
        => new(NotificationCategory.UserStatus, $"nearby-request-failed-{DateTime.UtcNow.Ticks}",
            Loc.Get("Notification.UserStatus.NearbyRequestFailed.Title"),
            reason,
            DateTime.UtcNow);

    public static NotificationEntry SlotConflict(string syncshellName)
        => new(NotificationCategory.UserStatus, $"slot-conflict-{syncshellName}",
            Loc.Get("Notification.UserStatus.SlotConflict.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.UserStatus.SlotConflict.Body"), syncshellName),
            DateTime.UtcNow);

    public static NotificationEntry TemporarySyncshellExpiring(string syncshellName, int minutesRemaining, string expiresAtLocal)
        => new(NotificationCategory.Syncshell, $"temp-syncshell-expiring-{syncshellName}",
            Loc.Get("Notification.Syncshell.TempExpiring.Title"),
            string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.Syncshell.TempExpiring.Body"), syncshellName, minutesRemaining, expiresAtLocal),
            DateTime.UtcNow);
}

public sealed class NotificationTracker
{
    private const int MaxStored = 100;

    private readonly MareMediator _mediator;
    private readonly NotificationsConfigService _configService;
    private readonly Dictionary<(NotificationCategory Category, string Id), NotificationEntry> _entries = new();
    private readonly Lock _lock = new();

    public NotificationTracker(MareMediator mediator, NotificationsConfigService configService)
    {
        _mediator = mediator;
        _configService = configService;
        LoadPersisted();
        PublishState();
    }

    public void Upsert(NotificationEntry entry)
    {
        using (_lock.EnterScope())
        {
            _entries[(entry.Category, entry.Id)] = entry;
            TrimIfNecessary_NoLock();
            Persist_NoLock();
        }
        PublishState();
    }

    public void Remove(NotificationCategory category, string id)
    {
        using (_lock.EnterScope())
        {
            _entries.Remove((category, id));
            Persist_NoLock();
        }
        PublishState();
    }

    public void Clear()
    {
        using (_lock.EnterScope())
        {
            _entries.Clear();
            Persist_NoLock();
        }
        PublishState();
    }

    public IReadOnlyList<NotificationEntry> GetEntries()
    {
        using (_lock.EnterScope())
        {
            return _entries.Values
                .OrderBy(e => e.CreatedAt)
                .ToList();
        }
    }

    public int Count
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _entries.Count;
            }
        }
    }

    private void PublishState()
    {
        _mediator.Publish(new NotificationStateChanged(Count));
    }

    private void LoadPersisted()
    {
        try
        {
            var list = _configService.Current.Notifications;
            foreach (var s in list)
            {
                if (!Enum.TryParse<NotificationCategory>(s.Category, out var cat)) continue;
                var entry = new NotificationEntry(cat, s.Id, s.Title, s.Description, s.CreatedAtUtc);
                _entries[(entry.Category, entry.Id)] = entry;
            }
            TrimIfNecessary_NoLock();
        }
        catch
        {
            // ignore load errors, start empty
        }
    }

    private void Persist_NoLock()
    {
        try
        {
            var stored = _entries.Values
                .OrderBy(e => e.CreatedAt)
                .Select(e => new StoredNotification
                {
                    Category = e.Category.ToString(),
                    Id = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    CreatedAtUtc = e.CreatedAt
                })
                .ToList();
            _configService.Current.Notifications = stored;
            _configService.Save();
        }
        catch
        {
            // ignore persistence errors
        }
    }

    private void TrimIfNecessary_NoLock()
    {
        if (_entries.Count <= MaxStored) return;
        foreach (var kv in _entries.Values.OrderByDescending(v => v.CreatedAt).Skip(MaxStored).ToList())
        {
            _entries.Remove((kv.Category, kv.Id));
        }
    }
}