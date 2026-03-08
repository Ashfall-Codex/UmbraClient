using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Comparer;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Factories;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Events;
using UmbraSync.Services.Mediator;

namespace UmbraSync.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<UserData, Pair> _allClientPairs = new(UserDataComparer.Instance);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingOffline = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OnlineUserCharaDataDto> _pendingCharacterData = new(StringComparer.Ordinal);
    private static readonly TimeSpan OfflineDebounce = TimeSpan.FromSeconds(2);
    private readonly ConcurrentQueue<DateTime> _offlineBurstEvents = new();
    private static readonly TimeSpan OfflineBurstWindow = TimeSpan.FromSeconds(1);
    private const int OfflineBurstThreshold = 5;
    private DateTime _burstSuppressUntil = DateTime.MinValue;
    private readonly MareConfigService _configurationService;
    private readonly IContextMenu _dalamudContextMenu;
    private readonly NearbyDiscoveryService _nearbyDiscoveryService;
    private readonly AutoDetectRequestService _autoDetectRequestService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PairFactory _pairFactory;
    private readonly Lazy<ApiController> _apiController;
    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> _groupPairsInternal;
    private static readonly TimeSpan LazyRecreateDebounce = TimeSpan.FromMilliseconds(150);
    private CancellationTokenSource? _lazyRecreateDebounceCts;
    private readonly Lock _lazyRecreateLock = new();
    private bool _lazyRecreateScheduled;
    private int _pendingLazyRecreateCount;
    private static readonly TimeSpan GroupReapplyThrottleDelay = TimeSpan.FromMilliseconds(100);
    private const int MaxConcurrentGroupReapplies = 5;
    private readonly ConcurrentQueue<Pair> _pendingGroupReapplies = new();
    private CancellationTokenSource? _groupReapplyCts;
    private readonly Lock _groupReapplyLock = new();
    private bool _groupReapplyProcessing;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
                MareConfigService configurationService, MareMediator mediator,
                IContextMenu dalamudContextMenu, NearbyDiscoveryService nearbyDiscoveryService,
                AutoDetectRequestService autoDetectRequestService, DalamudUtilService dalamudUtilService,
                IServiceProvider serviceProvider) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
        _nearbyDiscoveryService = nearbyDiscoveryService;
        _autoDetectRequestService = autoDetectRequestService;
        _dalamudUtilService = dalamudUtilService;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => ClearPairs());
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();

        _dalamudContextMenu.OnMenuOpened += DalamudContextMenuOnOnOpenGameObjectContextMenu;
    }

    public List<Pair> DirectPairs => _directPairsInternal.Value;

    public Dictionary<GroupFullInfoDto, List<Pair>> GroupPairs => _groupPairsInternal.Value;
    public Dictionary<GroupData, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value);
    public Pair? LastAddedUser { get; internal set; }

    public void AddGroup(GroupFullInfoDto dto)
    {
        _allGroups[dto.Group] = dto;
        RecreateLazyDebounced();
    }

    public void AddGroupPair(GroupPairFullInfoDto dto, bool isInitialLoad = false)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
            _allClientPairs[dto.User] = _pairFactory.Create(dto.User);

        var pair = _allClientPairs[dto.User];
        var group = _allGroups[dto.Group];
        var prevPaused = pair.IsPaused;
        pair.GroupPair[group] = dto;

        if (!pair.IsPaused)
        {
            pair.ApplyLastReceivedData(forced: true);
        }
        else if (!prevPaused && pair.IsPaused)
        {
            Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
        }

        RecreateLazyDebounced();

        if (!isInitialLoad)
        {
            Mediator.Publish(new ApplyDefaultGroupPermissionsMessage(dto));
        }
    }

    public Pair? GetPairByUID(string uid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => uid.Equals(f.Key.UID, StringComparison.Ordinal));
        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public void AddUserPair(UserPairDto dto, bool addToLastAddedUser = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _pairFactory.Create(dto.User);
        }
        else
        {
            addToLastAddedUser = false;
        }

        var pair = _allClientPairs[dto.User];
        var prevPaused = pair.IsPaused;
        pair.UserPair = dto;
        if (addToLastAddedUser)
            LastAddedUser = pair;

        if (!pair.IsPaused)
        {
            pair.ApplyLastReceivedData(forced: true);
        }
        else if (!prevPaused && pair.IsPaused)
        {
            Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
        }

        RecreateLazyDebounced();

        if (addToLastAddedUser)
        {
            Mediator.Publish(new ApplyDefaultPairPermissionsMessage(dto));
        }
    }

    public void ClearPairs()
    {
        Logger.LogDebug("Clearing all Pairs");
        DisposePairs();
        _allClientPairs.Clear();
        _allGroups.Clear();
        _pendingCharacterData.Clear();
        LastAddedUser = null;
        RecreateLazyImmediate(); // Immédiat car nettoyage complet
    }

    public List<Pair> GetOnlineUserPairs() => _allClientPairs
        .Where(p => p.Value.IsOnline)
        .Select(p => p.Value)
        .ToList();

    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsVisible);

    public List<UserData> GetVisibleUsers() => _allClientPairs
        .Where(p => p.Value.IsVisible && p.Value.IsOnline)
        .Select(p => p.Key).ToList();

    public void MarkPairOffline(UserData user)
    {
        // Debounce offline to prevent brief server-side offline bursts from causing UI disconnect flicker
        var uid = user.UID;
        if (string.IsNullOrEmpty(uid)) return;

        // Track burst of offlines to coalesce visual updates on mass refresh
        var now = DateTime.UtcNow;
        _offlineBurstEvents.Enqueue(now);
        while (_offlineBurstEvents.TryPeek(out var ts) && (now - ts) > OfflineBurstWindow)
        {
            _ = _offlineBurstEvents.TryDequeue(out _);
        }
        if (_offlineBurstEvents.Count >= OfflineBurstThreshold)
        {
            var until = now.Add(OfflineDebounce);
            if (until > _burstSuppressUntil)
            {
                _burstSuppressUntil = until;
            }
        }

        // Cancel any existing pending offline for this UID
        if (_pendingOffline.TryRemove(uid, out var existingCts))
        {
            try
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Intentionally ignored: CTS may already be disposed due to a concurrent race
            }
        }

        var cts = new CancellationTokenSource();
        _pendingOffline[uid] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // Extend delay if we are currently within a detected burst window
                var delayUntil = _burstSuppressUntil;
                var extraDelay = delayUntil > DateTime.UtcNow ? delayUntil - DateTime.UtcNow : TimeSpan.Zero;
                var finalDelay = extraDelay > OfflineDebounce ? extraDelay : OfflineDebounce;
                await Task.Delay(finalDelay, cts.Token).ConfigureAwait(false);

                // After debounce, if still pending and pair exists, mark truly offline
                if (!_allClientPairs.TryGetValue(user, out var pair)) return;

                Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
                pair.MarkOffline();

                RecreateLazyDebounced();
            }
            catch (OperationCanceledException)
            {
                // ignored: offline was canceled due to online signal
            }
            catch (ObjectDisposedException)
            {
                // ignored: CTS was disposed by a concurrent call - this is expected during rapid pause/unpause
                Logger.LogTrace("CancellationTokenSource disposed during MarkPairOffline for {uid}", uid);
            }
            finally
            {
                // Clean up token only if it's still our CTS
                _pendingOffline.TryRemove(KeyValuePair.Create(uid, cts));
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - safe to ignore
                }
            }
        });
    }

    internal void CancelPendingOffline(string uid)
    {
        if (!string.IsNullOrEmpty(uid) && _pendingOffline.TryRemove(uid, out var existingCts))
        {
            try
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Intentionally ignored
            }
        }
    }

    public void MarkPairOnline(OnlineUserIdentDto dto, bool sendNotif = true)
    {
        CancelPendingOffline(dto.User.UID);
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("No user found for {uid}, ignoring online signal (no existing pair)", dto.User.UID);
            return;
        }

        Mediator.Publish(new ClearProfileDataMessage(dto.User));

        if (pair.HasCachedPlayer)
        {
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("Player {uid} already has cached player, forcing reapplication of data", dto.User.UID);
            pair.ApplyLastReceivedData(forced: true);
            RecreateLazyImmediate(); // Immédiat pour la réactivité lors de la connexion
            return;
        }
        pair.CreateCachedPlayer(dto);

        if (sendNotif && _configurationService.Current.ShowOnlineNotifications
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs && pair.UserPair != null
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs && !string.IsNullOrEmpty(pair.GetNote())
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNoteOrName();
            var msg = !string.IsNullOrEmpty(note)
                ? $"{note} ({pair.UserData.AliasOrUID}) is now online"
                : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(new NotificationMessage("User online", msg, NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        pair.ApplyLastReceivedData(forced: true);

        Mediator.Publish(new PairOnlineMessage(dto.User));

        RecreateLazyImmediate(); // Immédiat pour la réactivité lors de la connexion
    }

    public void ReceiveCharaData(OnlineUserCharaDataDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("No user found for {uid}, queuing character data for later", dto.User.UID);
            _pendingCharacterData[dto.User.UID] = dto;
            return;
        }

        _pendingCharacterData.TryRemove(dto.User.UID, out _);
        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")));
        pair.ApplyData(dto);
    }

    public void ApplyPendingCharacterData()
    {
        foreach (var pending in _pendingCharacterData.ToArray())
        {
            if (_allClientPairs.Values.FirstOrDefault(p => string.Equals(p.UserData.UID, pending.Key, StringComparison.Ordinal)) is { } pair)
            {
                Logger.LogInformation("Applying pending character data for {uid}", pending.Key);
                _pendingCharacterData.TryRemove(pending.Key, out _);
                Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Applied Pending Character Data")));
                pair.ApplyData(pending.Value);
            }
        }
    }

    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);

        foreach (var item in _allClientPairs.ToList())
        {
            foreach (var grpPair in item.Value.GroupPair.Select(k => k.Key).Where(grpPair => GroupDataComparer.Instance.Equals(grpPair.Group, data)).ToList())
            {
                _allClientPairs[item.Key].GroupPair.Remove(grpPair);
            }

            if (!_allClientPairs[item.Key].HasAnyConnection() && _allClientPairs.TryRemove(item.Key, out var pair))
            {
                pair.MarkOffline();
            }
        }

        RecreateLazyDebounced();
    }

    public void RemoveGroupPair(GroupPairDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            var group = _allGroups[dto.Group];
            pair.GroupPair.Remove(group);

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazyDebounced();
    }

    public void RemoveUserPair(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair = null;

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazyDebounced();
    }

    public void SetGroupInfo(GroupInfoDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var groupInfo))
        {
            return;
        }

        groupInfo.Group = dto.Group;
        groupInfo.Owner = dto.Owner;
        groupInfo.GroupPermissions = dto.GroupPermissions;
        groupInfo.MaxUserCount = dto.MaxUserCount;
        groupInfo.AutoDetectVisible = dto.AutoDetectVisible;
        groupInfo.PasswordTemporarilyDisabled = dto.PasswordTemporarilyDisabled;
        groupInfo.IsTemporary = dto.IsTemporary;
        groupInfo.ExpiresAt = dto.ExpiresAt;

        RecreateLazyDebounced();
    }

    public void UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("No user found for {uid}, ignoring permission update", dto.User.UID);
            return;
        }

        if (pair.UserPair == null)
        {
            Logger.LogDebug("No direct pair for {dto}, ignoring permission update", dto);
            return;
        }

        var prevPaused = pair.IsPaused;
        var prevPermissions = pair.UserPair.OtherPermissions;
        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused()
            || pair.UserPair.OtherPermissions.IsPaired() != dto.Permissions.IsPaired())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("UpdatePairPermissions: Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}. Global IsPaused: {global}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX(),
            pair.IsPaused);

        bool pauseChanged = prevPaused != pair.IsPaused;
        bool filterChanged = prevPermissions.IsDisableAnimations() != dto.Permissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.Permissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.Permissions.IsDisableVFX();

        if (pauseChanged || filterChanged)
        {
            if (!pair.IsPaused)
            {
                CancelPendingOffline(pair.UserData.UID);
                Logger.LogDebug("UpdatePairPermissions: triggering forced reapplication for {uid}", pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
            else if (pauseChanged && pair.IsPaused)
            {
                Logger.LogDebug("UpdatePairPermissions: triggering invalidation for {uid}", pair.UserData.UID);
                Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
            }
        }

        RecreateLazyDebounced();
    }

    public void UpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("No user found for {uid}, ignoring self permission update", dto.User.UID);
            return;
        }

        if (pair.UserPair == null)
        {
            Logger.LogDebug("No direct pair for {dto}, ignoring self permission update", dto);
            return;
        }

        var prevPaused = pair.IsPaused;
        var prevPermissions = pair.UserPair.OwnPermissions;
        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused()
            || pair.UserPair.OwnPermissions.IsPaired() != dto.Permissions.IsPaired())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("UpdateSelfPairPermissions: Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}. Global IsPaused: {global}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX(),
            pair.IsPaused);

        bool pauseChanged = prevPaused != pair.IsPaused;
        bool filterChanged = prevPermissions.IsDisableAnimations() != dto.Permissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.Permissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.Permissions.IsDisableVFX();

        if (pauseChanged || filterChanged)
        {
            if (!pair.IsPaused)
            {
                CancelPendingOffline(pair.UserData.UID);
                Logger.LogDebug("UpdateSelfPairPermissions: triggering forced reapplication for {uid}", pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
            else if (pauseChanged && pair.IsPaused)
            {
                Logger.LogDebug("UpdateSelfPairPermissions: triggering invalidation for {uid}", pair.UserData.UID);
                Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
            }
        }

        RecreateLazyDebounced();
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var existingPair) && existingPair.IsVisible && !existingPair.IsPaused)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogDebug("No group found for {dto}, ignoring status info", dto);
            return;
        }

        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("No user found for {uid}, ignoring status info", dto.User.UID);
            return;
        }

        if (!pair.GroupPair.TryGetValue(group, out var groupPair))
        {
            Logger.LogDebug("User {user} not in group {group}, ignoring status info", dto.User, dto.Group);
            return;
        }

        groupPair.GroupPairStatusInfo = dto.GroupUserInfo;
        RecreateLazyDebounced();
    }

    internal void SetGroupPairUserPermissions(GroupPairUserPermissionDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var group))
        {
            Logger.LogDebug("No group found for {dto}, ignoring group pair permissions update", dto);
            return;
        }

        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            Logger.LogDebug("No user found for {dto}, ignoring group pair permissions update", dto);
            return;
        }

        if (!pair.GroupPair.TryGetValue(group, out var groupPair))
        {
            Logger.LogDebug("User {user} not in group {group}, ignoring group pair permissions update", dto.User, dto.Group);
            return;
        }

        var prevPermissions = groupPair.GroupUserPermissions;
        bool pauseChanged = prevPermissions.IsPaused() != dto.GroupPairPermissions.IsPaused();
        bool filterChanged = prevPermissions.IsDisableAnimations() != dto.GroupPairPermissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.GroupPairPermissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.GroupPairPermissions.IsDisableVFX();
        bool wasPaused = pair.IsPaused;

        groupPair.GroupUserPermissions = dto.GroupPairPermissions;

        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("SetGroupPairUserPermissions: Group {gid}, User {uid}. Paused: {paused}, Global IsPaused: {global}",
                dto.Group.GID, pair.UserData.UID, groupPair.GroupUserPermissions.IsPaused(), pair.IsPaused);

        if (pauseChanged || filterChanged)
        {
            bool nowPaused = pair.IsPaused;
            if (!wasPaused && nowPaused)
            {
                Logger.LogDebug("SetGroupPairUserPermissions: pair {uid} became fully paused, invalidating", pair.UserData.UID);
                Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
            }
            else if (wasPaused && !nowPaused)
            {
                CancelPendingOffline(pair.UserData.UID);
                Logger.LogDebug("SetGroupPairUserPermissions: pair {uid} became active, reapplying", pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
            else if (!nowPaused && filterChanged)
            {
                CancelPendingOffline(pair.UserData.UID);
                Logger.LogDebug("SetGroupPairUserPermissions: filters changed for active pair {uid}, reapplying", pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
        }
        RecreateLazyDebounced();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var groupInfo))
        {
            Logger.LogDebug("No group found for {dto}, ignoring group permissions update", dto);
            return;
        }

        var pairsInGroup = GroupPairs.TryGetValue(groupInfo, out var pairs) ? pairs : new List<Pair>();

        var prevPermissions = groupInfo.GroupPermissions;

        bool pauseChanged = prevPermissions.IsPaused() != dto.Permissions.IsPaused();
        bool filterChanged = prevPermissions.IsDisableAnimations() != dto.Permissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.Permissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.Permissions.IsDisableVFX();

        Dictionary<string, bool>? prevPauseStates = null;
        if (pauseChanged)
        {
            prevPauseStates = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var p in pairsInGroup)
                prevPauseStates[p.UserData.UID] = p.IsPaused;
        }

        groupInfo.GroupPermissions = dto.Permissions;

        if (pauseChanged || filterChanged)
        {
            RecreateLazyDebounced();
            var pairsToReapply = new List<Pair>();
            foreach (var p in pairsInGroup)
            {
                bool wasPaused = prevPauseStates != null && prevPauseStates.TryGetValue(p.UserData.UID, out var wp) && wp;
                bool nowPaused = p.IsPaused;

                if (!wasPaused && nowPaused)
                {
                    Logger.LogDebug("SetGroupPermissions: pair {uid} became fully paused in group {gid}, invalidating", p.UserData.UID, groupInfo.Group.GID);
                    Mediator.Publish(new PlayerVisibilityMessage(p.Ident, IsVisible: false, Invalidate: true));
                }
                else if ((wasPaused && !nowPaused) || (!nowPaused && filterChanged))
                {
                    CancelPendingOffline(p.UserData.UID);
                    pairsToReapply.Add(p);
                }
            }

            if (pairsToReapply.Count > 0)
            {
                Logger.LogDebug("SetGroupPermissions: queueing {count} pairs for throttled reapplication in group {gid}", pairsToReapply.Count, groupInfo.Group.GID);
                QueueGroupReapplication(pairsToReapply);
            }
        }
        RecreateLazyDebounced();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var groupInfo))
        {
            Logger.LogDebug("No group found for {dto}, ignoring group status info update", dto);
            return;
        }
        groupInfo.GroupUserInfo = dto.GroupUserInfo;
        RecreateLazyDebounced();
    }

    internal void SetGroupUserPermissions(GroupPairUserPermissionDto dto)
    {
        if (!_allGroups.TryGetValue(dto.Group, out var groupInfo))
        {
            Logger.LogDebug("No group found for {dto}, ignoring group user permissions update", dto);
            return;
        }
        var pairsInGroup = GroupPairs.TryGetValue(groupInfo, out var pairs) ? pairs : new List<Pair>();
        var prevPermissions = groupInfo.GroupUserPermissions;

        bool pauseChanged = prevPermissions.IsPaused() != dto.GroupPairPermissions.IsPaused();
        bool filterChanged = prevPermissions.IsDisableAnimations() != dto.GroupPairPermissions.IsDisableAnimations()
            || prevPermissions.IsDisableSounds() != dto.GroupPairPermissions.IsDisableSounds()
            || prevPermissions.IsDisableVFX() != dto.GroupPairPermissions.IsDisableVFX();

        Dictionary<string, bool>? prevPauseStates = null;
        if (pauseChanged)
        {
            prevPauseStates = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var p in pairsInGroup)
                prevPauseStates[p.UserData.UID] = p.IsPaused;
        }

        groupInfo.GroupUserPermissions = dto.GroupPairPermissions;

        if (pauseChanged || filterChanged)
        {
            RecreateLazyDebounced();

            var pairsToReapply = new List<Pair>();
            foreach (var p in pairsInGroup)
            {
                bool wasPaused = prevPauseStates != null && prevPauseStates.TryGetValue(p.UserData.UID, out var wp) && wp;
                bool nowPaused = p.IsPaused;

                if (!wasPaused && nowPaused)
                {
                    // Pair est devenu entièrement pausé → invalider
                    Logger.LogDebug("SetGroupUserPermissions: pair {uid} became fully paused in group {gid}, invalidating", p.UserData.UID, groupInfo.Group.GID);
                    Mediator.Publish(new PlayerVisibilityMessage(p.Ident, IsVisible: false, Invalidate: true));
                }
                else if ((wasPaused && !nowPaused) || (!nowPaused && filterChanged))
                {
                    CancelPendingOffline(p.UserData.UID);
                    pairsToReapply.Add(p);
                }
                // else: état global inchangé → pas de redraw
            }

            if (pairsToReapply.Count > 0)
            {
                Logger.LogDebug("SetGroupUserPermissions: queueing {count} pairs for throttled reapplication in group {gid}", pairsToReapply.Count, groupInfo.Group.GID);
                QueueGroupReapplication(pairsToReapply);
            }
        }
        RecreateLazyDebounced();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _dalamudContextMenu.OnMenuOpened -= DalamudContextMenuOnOnOpenGameObjectContextMenu;

        // Nettoyer les CancellationTokenSource pour le batching
        _lazyRecreateDebounceCts?.Cancel();
        _lazyRecreateDebounceCts?.Dispose();
        _groupReapplyCts?.Cancel();
        _groupReapplyCts?.Dispose();

        DisposePairs();
    }

    private void DalamudContextMenuOnOnOpenGameObjectContextMenu(Dalamud.Game.Gui.ContextMenu.IMenuOpenedArgs args)
    {
        if (args.MenuType == Dalamud.Game.Gui.ContextMenu.ContextMenuType.Inventory) return;
        if (!_configurationService.Current.EnableRightClickMenus) return;
        try
        {
            Logger.LogDebug("[ContextMenu] Opened: Type={type}, TargetType={tgtType}", args.MenuType, args.Target.GetType().Name);
        }
        catch { /* logging should never break menu */ }

        TryAddAutoDetectPairRequestItem(args);

        foreach (var pair in _allClientPairs.Where((p => p.Value.IsVisible)))
        {
            pair.Value.AddContextMenu(args);
        }
    }

    private void TryAddAutoDetectPairRequestItem(IMenuOpenedArgs args)
    {
        if (!_configurationService.Current.EnableAutoDetectDiscovery) { Logger.LogDebug("[ContextMenu] Skipped pair request: AutoDetectDiscovery disabled"); return; }
        if (!_configurationService.Current.AllowAutoDetectPairRequests) { Logger.LogDebug("[ContextMenu] Skipped pair request: PairRequests not allowed"); return; }
        if (args.Target is not MenuTargetDefault target) { Logger.LogDebug("[ContextMenu] Skipped pair request: Target not MenuTargetDefault (was {t})", args.Target.GetType().Name); return; }

        uint targetObjectId = (uint)target.TargetObjectId;
        if (targetObjectId == 0 || targetObjectId == uint.MaxValue) { Logger.LogDebug("[ContextMenu] Skipped pair request: invalid targetObjectId={id}", targetObjectId); return; }

        if (_allClientPairs.Any(p => p.Value.GetPlayerCharacterId() == targetObjectId))
        {
            Logger.LogDebug("[ContextMenu] Skipped pair request: target is already a known pair (objId={id})", targetObjectId);
            return;
        }

        if (!_dalamudUtilService.TryGetPlayerCharacterByObjectId(targetObjectId, out var clickedPlayer))
        {
            Logger.LogDebug("[ContextMenu] Skipped pair request: could not resolve player for objId={id}", targetObjectId);
            return;
        }

        var entries = _nearbyDiscoveryService.SnapshotEntries();
        var clickedPlayerName = clickedPlayer.Name;

        static bool NamesEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return CompareInfo.Compare(left, right, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols) == 0;
        }

        static bool NameMatches(UmbraSync.Services.Mediator.NearbyEntry entry, string clicked) =>
            NamesEqual(entry.DisplayName ?? string.Empty, clicked) ||
            NamesEqual(entry.Name, clicked);

        var nearbyEntry = entries.FirstOrDefault(e =>
            e.IsMatch
            && NameMatches(e, clickedPlayerName)
            && e.WorldId == (ushort)clickedPlayer.HomeWorldId);

        if (nearbyEntry == null)
        {
            Logger.LogDebug("[ContextMenu] No AutoDetect match for {name}@{world} (entries={cnt}). Example entries: {examples}",
                clickedPlayerName,
                (ushort)clickedPlayer.HomeWorldId,
                entries.Count,
                string.Join(", ", entries.Take(5).Select(e => $"{e.Name}/{e.DisplayName}@{e.WorldId}")));
            return;
        }

        bool canSendRequest = nearbyEntry.AcceptPairRequests && !string.IsNullOrEmpty(nearbyEntry.Token);
        bool canAddPair = !string.IsNullOrEmpty(nearbyEntry.Uid)
            && !_allClientPairs.Keys.Any(p => string.Equals(p.UID, nearbyEntry.Uid, StringComparison.Ordinal));

        if (!canSendRequest && !canAddPair)
        {
            Logger.LogDebug("[ContextMenu] AutoDetect entry for {name}@{world} is not actionable (Token={hasToken}, Uid={hasUid}, Accepts={accepts})",
                clickedPlayerName, (ushort)clickedPlayer.HomeWorldId,
                !string.IsNullOrEmpty(nearbyEntry.Token),
                !string.IsNullOrEmpty(nearbyEntry.Uid),
                nearbyEntry.AcceptPairRequests);
            return;
        }

        Logger.LogDebug("[ContextMenu] AutoDetect match found for {name}@{world}: entry={entryName}/{display}@{entryWorld} (Uid={uid}, Token={hasToken})",
            clickedPlayerName, (ushort)clickedPlayer.HomeWorldId,
            nearbyEntry.Name, nearbyEntry.DisplayName ?? "<null>", nearbyEntry.WorldId, nearbyEntry.Uid ?? "<null>", !string.IsNullOrEmpty(nearbyEntry.Token));

        if (canSendRequest)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = Loc.Get("ContextMenu.AutoDetect.SendRequest"),
                PrefixColor = 708,
                PrefixChar = 'U',
                UseDefaultPrefix = false,
                IsEnabled = true,
                IsSubmenu = false,
                IsReturn = false,
                Priority = 1,
                OnClicked = clickedArgs =>
                {
                    _ = _autoDetectRequestService.SendRequestAsync(
                        nearbyEntry.Token!,
                        nearbyEntry.Uid,
                        nearbyEntry.DisplayName ?? nearbyEntry.Name);
                }
            });
        }

        if (canAddPair)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = Loc.Get("ContextMenu.AutoDetect.AddPair"),
                PrefixColor = 708,
                PrefixChar = 'U',
                UseDefaultPrefix = false,
                IsEnabled = true,
                IsSubmenu = false,
                IsReturn = false,
                Priority = 0,
                OnClicked = clickedArgs =>
                {
                    _ = _apiController.Value.UserAddPair(new UserDto(new UserData(nearbyEntry.Uid!)));
                }
            });
        }

        Logger.LogDebug("[ContextMenu] Added auto-detect menu for {name}@{world} (Uid={uid}) Request={reqEnabled} AddPair={addEnabled}",
            clickedPlayerName, (ushort)clickedPlayer.HomeWorldId, nearbyEntry.Uid, canSendRequest, canAddPair);
    }

    private Lazy<List<Pair>> DirectPairsLazy() => new(() => _allClientPairs.Select(k => k.Value)
        .Where(k => k.UserPair != null).ToList());

    private void DisposePairs()
    {
        Logger.LogDebug("Disposing all Pairs");
        foreach (var pending in _pendingOffline)
        {
            pending.Value.Cancel();
            pending.Value.Dispose();
        }
        _pendingOffline.Clear();

        Parallel.ForEach(_allClientPairs, item =>
        {
            item.Value.MarkOffline(wait: false);
        });

        RecreateLazyImmediate(); // Immédiat car nettoyage
    }

    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> GroupPairsLazy()
    {
        return new Lazy<Dictionary<GroupFullInfoDto, List<Pair>>>(() =>
        {
            Dictionary<GroupFullInfoDto, List<Pair>> outDict = new();
            foreach (var group in _allGroups)
            {
                outDict[group.Value] = _allClientPairs.Select(p => p.Value).Where(p => p.GroupPair.Any(g => GroupDataComparer.Instance.Equals(group.Key, g.Key.Group))).ToList();
            }
            return outDict;
        });
    }
    
    private void RecreateLazyImmediate()
    {
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
    }
    
    private void RecreateLazyDebounced()
    {
        lock (_lazyRecreateLock)
        {
            _pendingLazyRecreateCount++;

            if (_lazyRecreateScheduled)
            {
                // Un recalcul est déjà programmé
                return;
            }

            _lazyRecreateScheduled = true;
            _lazyRecreateDebounceCts?.Cancel();
            _lazyRecreateDebounceCts?.Dispose();
            _lazyRecreateDebounceCts = new CancellationTokenSource();
            var token = _lazyRecreateDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LazyRecreateDebounce, token).ConfigureAwait(false);

                    int coalesced;
                    lock (_lazyRecreateLock)
                    {
                        coalesced = _pendingLazyRecreateCount;
                        _pendingLazyRecreateCount = 0;
                        _lazyRecreateScheduled = false;
                    }

                    RecreateLazyImmediate();

                    if (coalesced > 1 && Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug("RecreateLazy coalesced {count} state changes into single update", coalesced);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Annulé par un nouveau changement
                }
            }, token);
        }
    }
    
    private void QueueGroupReapplication(IEnumerable<Pair> pairs)
    {
        foreach (var pair in pairs)
        {
            if (!pair.IsPaused)
            {
                CancelPendingOffline(pair.UserData.UID);
                _pendingGroupReapplies.Enqueue(pair);
            }
        }

        StartGroupReapplyProcessingIfNeeded();
    }

    private void StartGroupReapplyProcessingIfNeeded()
    {
        lock (_groupReapplyLock)
        {
            if (_groupReapplyProcessing || _pendingGroupReapplies.IsEmpty)
                return;

            _groupReapplyProcessing = true;
            _groupReapplyCts?.Cancel();
            _groupReapplyCts?.Dispose();
            _groupReapplyCts = new CancellationTokenSource();
            var token = _groupReapplyCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessGroupReappliesAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Error during group reapplication processing");
                }
                finally
                {
                    lock (_groupReapplyLock)
                    {
                        _groupReapplyProcessing = false;
                    }
                }
            }, token);
        }
    }

    private async Task ProcessGroupReappliesAsync(CancellationToken token)
    {
        int processed = 0;
        var batch = new List<Pair>();

        while (!token.IsCancellationRequested && _pendingGroupReapplies.TryDequeue(out var pair))
        {
            // Vérifier que le pair est toujours valide et non-pausé
            if (pair.IsPaused || pair.Handler == null)
                continue;

            batch.Add(pair);

            // Traiter par batch de MaxConcurrentGroupReapplies
            if (batch.Count >= MaxConcurrentGroupReapplies)
            {
                await ProcessBatchAsync(batch, token).ConfigureAwait(false);
                processed += batch.Count;
                batch.Clear();
                await Task.Delay(GroupReapplyThrottleDelay, token).ConfigureAwait(false);
            }
        }

        // Traiter le reste
        if (batch.Count > 0)
        {
            await ProcessBatchAsync(batch, token).ConfigureAwait(false);
            processed += batch.Count;
        }

        if (processed > 0)
            Logger.LogDebug("Processed {count} group reapplications with throttling", processed);
    }

    private async Task ProcessBatchAsync(List<Pair> batch, CancellationToken token)
    {
        var tasks = batch.Select(p => Task.Run(() =>
        {
            try
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.LogDebug("QueuedGroupReapply: triggering reapplication for {uid}", p.UserData.UID);
                p.ApplyLastReceivedData(forced: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to reapply data for {uid} during group permission change", p.UserData.UID);
            }
        }, token));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static readonly CompareInfo CompareInfo = CultureInfo.InvariantCulture.CompareInfo;
}