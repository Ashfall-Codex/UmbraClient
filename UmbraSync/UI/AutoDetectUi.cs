using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Dalamud.Interface.Textures.TextureWraps;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Group;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;
using UmbraSync.UI.Components;
using UmbraSync.Utils;
using UmbraSync.WebAPI;

namespace UmbraSync.UI;

public class AutoDetectUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly AutoDetectRequestService _requestService;
    private readonly NearbyDiscoveryService _discoveryService;
    private readonly NearbyPendingService _pendingService;
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _profileManager;
    private readonly UiSharedService _uiSharedService;
    private readonly Dictionary<string, (byte[] Data, Task<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> Task)> _nearbyTextureTasks = new(StringComparer.Ordinal);
    private List<Services.Mediator.NearbyEntry> _entries;
    private readonly HashSet<string> _acceptInFlight = new(StringComparer.Ordinal);
    private readonly SyncshellDiscoveryService _syncshellDiscoveryService;
    private readonly NotificationTracker _notificationTracker;
    private List<SyncshellDiscoveryEntryDto> _syncshellEntries = [];
    private bool _syncshellInitialized;
    private readonly HashSet<string> _syncshellJoinInFlight = new(StringComparer.OrdinalIgnoreCase);
    private string? _syncshellLastError;
    private bool _showNsfwSyncshells;
    private string _syncshellSearch = string.Empty;
    private int _activeTab;
    private readonly ApiController _apiController;
    private readonly ConcurrentDictionary<string, GroupProfileDto?> _discoveryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _discoveryProfileRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Source, Task<IDalamudTextureWrap?> Task)> _syncshellIconTasks = new(StringComparer.Ordinal);
    private Task? _discoveryProfileFetch;
    private DateTime _lastDiscoveryProfileFetchUtc = DateTime.MinValue;
    private const int MaxNearbyProfileCards = 15;

    public AutoDetectUi(ILogger<AutoDetectUi> logger, MareMediator mediator,
        MareConfigService configService,
        AutoDetectRequestService requestService, NearbyPendingService pendingService, PairManager pairManager,
        NearbyDiscoveryService discoveryService, SyncshellDiscoveryService syncshellDiscoveryService,
        PerformanceCollectorService performanceCollectorService, NotificationTracker notificationTracker,
        UmbraProfileManager profileManager, UiSharedService uiSharedService, ApiController apiController)
        : base(logger, mediator, "AutoDetect", performanceCollectorService)
    {
        _profileManager = profileManager;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _configService = configService;
        _requestService = requestService;
        _pendingService = pendingService;
        _pairManager = pairManager;
        _discoveryService = discoveryService;
        _syncshellDiscoveryService = syncshellDiscoveryService;
        _notificationTracker = notificationTracker;
        Mediator.Subscribe<Services.Mediator.DiscoveryListUpdated>(this, OnDiscoveryUpdated);
        Mediator.Subscribe<SyncshellDiscoveryUpdated>(this, OnSyncshellDiscoveryUpdated);
        _entries = _discoveryService.SnapshotEntries();

        Flags |= ImGuiWindowFlags.NoScrollbar;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(350, 220),
            MaximumSize = new Vector2(600, 600),
        };
    }

    public override bool DrawConditions()
    {
        return true;
    }

    protected override void DrawInternal()
    {
        using var idScope = ImRaii.PushId("autodetect-ui");

        var incomingInvites = _pendingService.Pending.ToList();
        var outgoingInvites = _requestService.GetPendingRequestsSnapshot();

        Vector4 accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        var incomingCount = incomingInvites.Count;
        var inviteLabel = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Tab.Invitations"), incomingCount);
        var nearbyLabel = Loc.Get("AutoDetectUi.Tab.Nearby");
        var syncCount = _syncshellEntries.Count > 0 ? _syncshellEntries.Count : _syncshellDiscoveryService.Entries.Count;
        var syncLabel = string.Create(CultureInfo.CurrentCulture, $"{Loc.Get("AutoDetectUi.Tab.SyncFinder")} ({syncCount})");

        var labels = new[] { inviteLabel, nearbyLabel, syncLabel };
        var icons = new[] { FontAwesomeIcon.Envelope, FontAwesomeIcon.MapMarkerAlt, FontAwesomeIcon.Search };
        Components.AccentTabBar.Draw("ad", labels, icons, accent, ref _activeTab);

        switch (_activeTab)
        {
            case 0:
                DrawInvitationsTab(incomingInvites, outgoingInvites);
                break;
            case 1:
                DrawNearbyTab();
                break;
            case 2:
                DrawSyncshellTab();
                break;
        }
    }

    public void DrawInline()
    {
        DrawInternal();
    }

    public int PendingInvitationCount => _pendingService.Pending.Count;

    public int SyncFinderEntryCount => _syncshellEntries.Count > 0 ? _syncshellEntries.Count : _syncshellDiscoveryService.Entries.Count;

    public void DrawInvitationsPage()
    {
        using var idScope = ImRaii.PushId("autodetect-invitations");
        DrawInvitationsTab(_pendingService.Pending.ToList(), _requestService.GetPendingRequestsSnapshot());
    }

    public void DrawNearbyPage()
    {
        using var idScope = ImRaii.PushId("autodetect-nearby");
        DrawNearbyTab();
    }

    public void DrawSyncFinderPage()
    {
        using var idScope = ImRaii.PushId("autodetect-syncfinder");
        DrawSyncshellTab();
    }

    private void DrawInvitationsTab(List<KeyValuePair<string, PendingEntry>> incomingInvites, IReadOnlyCollection<AutoDetectRequestService.PendingRequestInfo> outgoingInvites)
    {
        ImGuiHelpers.ScaledDummy(4);

        if (incomingInvites.Count == 0 && outgoingInvites.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.Envelope,
                Loc.Get("EmptyState.Invitations.Title"), Loc.Get("EmptyState.Invitations.Hint"));
            return;
        }

        DrawListHeading(FontAwesomeIcon.Inbox,
            string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.ReceivedHeader"), incomingInvites.Count));

        if (incomingInvites.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Invitations.ReceivedEmpty"), ImGuiColors.DalamudGrey3);
        }

        foreach (var (uid, entry) in incomingInvites.OrderByDescending(k => k.Value.ReceivedAtUtc))
        {
            bool processing = _acceptInFlight.Contains(uid);
            string ago = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.ReceivedAgo"), FormatDuration(DateTime.UtcNow - entry.ReceivedAtUtc));

            UiSharedService.DrawCard("incoming-" + uid, () =>
            {
                DrawInvitationIdentity(entry.DisplayName, uid, ago);

                if (processing)
                {
                    UiSharedService.ColorText(Loc.Get("AutoDetectUi.Invitations.Processing"), ImGuiColors.DalamudGrey);
                    return;
                }

                using (ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeSliderGrabActive))
                {
                    if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Accept")))
                    {
                        TriggerAccept(uid);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Decline")))
                {
                    _pendingService.Decline(uid);
                }
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("AutoDetect.Block")))
                {
                    _pendingService.Block(uid);
                }
                UiSharedService.AttachToolTip(Loc.Get("AutoDetect.Block.Tooltip"));
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(4);
        }

        ImGuiHelpers.ScaledDummy(6);
        DrawListHeading(FontAwesomeIcon.PaperPlane,
            string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.SentHeader"), outgoingInvites.Count));

        if (outgoingInvites.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Invitations.SentEmpty"), ImGuiColors.DalamudGrey3);
            return;
        }

        foreach (var info in outgoingInvites.OrderByDescending(i => i.SentAt))
        {
            string ago = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.SentAgo"), FormatDuration(DateTime.UtcNow - info.SentAt));

            UiSharedService.DrawCard("outgoing-" + info.Key, () =>
            {
                DrawInvitationIdentity(info.TargetDisplayName, info.Uid, ago);

                if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Remove")))
                {
                    _requestService.RemovePendingRequestByKey(info.Key);
                }
                UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Invitations.RemoveTooltip"));
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(4);
        }
    }

    private static void DrawListHeading(FontAwesomeIcon icon, string text)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.ThemeTextAccent, icon.ToIconString());
        ImGui.SameLine();
        UiSharedService.ColorText(text, UiSharedService.ThemeTextAccent);
        ImGuiHelpers.ScaledDummy(2);
    }

    private static void DrawInvitationIdentity(string? displayName, string? uid, string ago)
    {
        float rightEdge = ImGui.GetWindowContentRegionMax().X - UiSharedService.GetCardContentPaddingX();

        UiSharedService.ColorText(string.IsNullOrEmpty(displayName) ? uid ?? string.Empty : displayName, UiSharedService.ThemeNavTextActive);
        ImGui.SameLine();
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rightEdge - ImGui.CalcTextSize(ago).X));
        UiSharedService.ColorText(ago, ImGuiColors.DalamudGrey3);

        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(displayName))
        {
            UiSharedService.ColorText(uid, ImGuiColors.DalamudGrey);
        }
    }

    private void DrawNearbyTab()
    {
        if (!_configService.Current.EnableAutoDetectDiscovery)
        {
            // Activer la détection fait plus que basculer un réglage (notifications, suppression, demandes
            // d'appairage) : le bouton mène donc à la page de réglages plutôt que de dupliquer cette logique.
            if (_uiSharedService.DrawEmptyState(FontAwesomeIcon.BroadcastTower,
                    Loc.Get("EmptyState.Nearby.Disabled.Title"), Loc.Get("EmptyState.Nearby.Disabled.Hint"),
                    Loc.Get("EmptyState.Nearby.Disabled.Button"), FontAwesomeIcon.Cog))
                Mediator.Publish(new OpenAutoDetectSettingsMessage());
            return;
        }

        // Réciprocité : masqué (AFK ou hors JDR selon les réglages), on ne voit personne non plus.
        if (_discoveryService.IsHidden)
        {
            bool hiddenByAfk = _discoveryService.Visibility == NearbyVisibility.HiddenAfk;
            string hiddenTitle = hiddenByAfk ? Loc.Get("EmptyState.Nearby.HiddenAfk.Title") : Loc.Get("EmptyState.Nearby.HiddenNotRoleplaying.Title");
            string hiddenHint = hiddenByAfk ? Loc.Get("EmptyState.Nearby.HiddenAfk.Hint") : Loc.Get("EmptyState.Nearby.HiddenNotRoleplaying.Hint");
            if (_uiSharedService.DrawEmptyState(FontAwesomeIcon.EyeSlash, hiddenTitle, hiddenHint,
                    Loc.Get("EmptyState.Nearby.Disabled.Button"), FontAwesomeIcon.Cog))
                Mediator.Publish(new OpenAutoDetectSettingsMessage());
            return;
        }

        int maxDist = MareConfig.AutoDetectFixedMaxDistanceMeters;

        var sourceEntries = _entries.Count > 0 ? _entries : _discoveryService.SnapshotEntries();
        // Build snapshot of pending invites to gray-out buttons
        var pendingInvites = _requestService.GetPendingRequestsSnapshot();
        var pendingUids = new HashSet<string>(pendingInvites.Select(p => p.Uid!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
        var pendingTokens = new HashSet<string>(pendingInvites.Select(p => p.Token!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
        var orderedEntries = sourceEntries
            .Where(e => e.IsMatch)
            // Ordre alphabétique plutôt que par distance : la liste est stable et ne laisse pas deviner qui est le plus proche.
            .OrderBy(e => e.DisplayName ?? e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (orderedEntries.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.MapMarkerAlt,
                Loc.Get("EmptyState.Nearby.Empty.Title"), Loc.Get("EmptyState.Nearby.Empty.Hint"));
            return;
        }

        ImGuiHelpers.ScaledDummy(4);

        float scale = ImGuiHelpers.GlobalScale;

        for (int i = 0; i < orderedEntries.Count; i++)
        {
            var entry = orderedEntries[i];
            bool alreadyPaired = IsAlreadyPairedByUidOrAlias(entry);
            bool overDistance = !float.IsNaN(entry.Distance) && entry.Distance > maxDist;
            bool alreadyInvited = (!string.IsNullOrEmpty(entry.Uid) && pendingUids.Contains(entry.Uid))
                                  || (!string.IsNullOrEmpty(entry.Token) && pendingTokens.Contains(entry.Token));
            bool canRequest = entry.AcceptPairRequests && !string.IsNullOrEmpty(entry.Token) && !alreadyPaired && !alreadyInvited;

            string displayName = entry.DisplayName ?? entry.Name;

            string status = alreadyPaired
                ? Loc.Get("AutoDetectUi.Nearby.Status.Paired")
                : alreadyInvited
                    ? Loc.Get("AutoDetectUi.Nearby.Status.Invited")
                : overDistance
                    ? string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Nearby.Status.OutOfRange"), maxDist)
                    : !entry.AcceptPairRequests
                        ? Loc.Get("AutoDetectUi.Nearby.Status.InvitesDisabled")
                        : string.IsNullOrEmpty(entry.Token)
                            ? Loc.Get("AutoDetectUi.Nearby.Status.Unavailable")
                            : Loc.Get("AutoDetectUi.Nearby.Status.Available");

            string reason = alreadyPaired
                ? Loc.Get("AutoDetectUi.Nearby.Reason.Paired")
                : alreadyInvited
                    ? Loc.Get("AutoDetectUi.Nearby.Reason.AlreadyInvited")
                : overDistance
                    ? string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Nearby.Reason.OutOfRange"), maxDist)
                    : !entry.AcceptPairRequests
                        ? Loc.Get("AutoDetectUi.Nearby.Reason.InvitesDisabled")
                        : string.IsNullOrEmpty(entry.Token)
                            ? Loc.Get("AutoDetectUi.Nearby.Reason.Unavailable")
                            : string.Empty;

            bool canInvite = canRequest && !overDistance;
            float rightEdge = ImGui.GetWindowContentRegionMax().X - UiSharedService.GetCardContentPaddingX();

            // Plafond de requêtes de fiches : en zone dense, ne pas déclencher des dizaines de requêtes d'un coup.
            var rpProfile = i < MaxNearbyProfileCards ? GetNearbyProfile(entry) : null;
            var rpName = rpProfile == null ? string.Empty : $"{rpProfile.RpFirstName} {rpProfile.RpLastName}".Trim();
            bool hasRp = !string.IsNullOrEmpty(rpName);
            var nameColor = hasRp && _configService.Current.UseRpNameColors && !string.IsNullOrEmpty(rpProfile!.RpNameColor)
                ? UiSharedService.HexToVector4(rpProfile.RpNameColor)
                : UiSharedService.ThemeNavTextActive;

            UiSharedService.DrawCard("nearby-" + i, () =>
            {
                float rowTop = ImGui.GetCursorPosY();
                DrawNearbyPortrait(entry, rpProfile, hasRp ? rpName : displayName, nameColor);
                ImGui.SameLine();
                ImGui.BeginGroup();

                ImGui.AlignTextToFramePadding();
                UiSharedService.ColorText(hasRp ? rpName : displayName, nameColor);

                // Sous le nom RP : le titre, ou à défaut le pseudo, pour toujours savoir qui est la personne.
                if (hasRp)
                {
                    var subtitle = !string.IsNullOrWhiteSpace(rpProfile!.RpTitle) ? rpProfile.RpTitle : displayName;
                    ImGui.TextColored(ImGuiColors.DalamudGrey, subtitle);
                }

                if (hasRp)
                {
                    var pronouns = rpProfile!.RpCustomFields?.FirstOrDefault(f =>
                        f.Name.Contains("pronom", StringComparison.OrdinalIgnoreCase)
                        || f.Name.Contains("pronoun", StringComparison.OrdinalIgnoreCase))?.Value;
                    var identity = string.Join(" · ", new[] { rpProfile.RpRace, rpProfile.RpEthnicity, rpProfile.RpAge, pronouns }
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (!string.IsNullOrEmpty(identity))
                        ImGui.TextColored(ImGuiColors.DalamudGrey, identity);

                    var role = string.Join(" · ", new[] { rpProfile.RpOccupation, string.IsNullOrWhiteSpace(rpProfile.RpAffiliation) ? null : $"<{rpProfile.RpAffiliation}>" }
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (!string.IsNullOrEmpty(role))
                        ImGui.TextColored(ImGuiColors.DalamudGrey3, role);
                }
                ImGui.EndGroup();

                float textRight = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetScrollX();
                float rowHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y - rowTop;

                string actionLabel = canInvite ? Loc.Get("AutoDetectUi.Nearby.InviteButton") : status;
                float buttonWidth = ImGui.CalcTextSize(actionLabel).X + ImGui.GetStyle().FramePadding.X * 2f + 12f * scale;
                float actionWidth = canInvite ? buttonWidth : ImGui.CalcTextSize(actionLabel).X;

                ImGui.SetCursorPos(new Vector2(
                    MathF.Max(textRight + ImGui.GetStyle().ItemSpacing.X, rightEdge - actionWidth),
                    rowTop + MathF.Max(0f, (rowHeight - ImGui.GetFrameHeight()) / 2f)));
                if (canInvite)
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor))
                    using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeSliderGrabActive))
                    {
                        if (ImGui.Button(actionLabel, new Vector2(buttonWidth, 0)))
                        {
                            _ = _requestService.SendRequestAsync(entry.Token!, entry.Uid, entry.DisplayName);
                        }
                    }
                    UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.InviteTooltip"));
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    UiSharedService.ColorText(actionLabel, alreadyPaired ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        UiSharedService.AttachToolTip(reason);
                    }
                }
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(4);
        }
    }

    private UmbraProfileData? GetNearbyProfile(Services.Mediator.NearbyEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Uid) || string.IsNullOrEmpty(entry.Name)) return null;
        return _profileManager.GetUmbraProfile(new API.Data.UserData(entry.Uid), entry.Name, entry.WorldId);
    }

    private void DrawNearbyPortrait(Services.Mediator.NearbyEntry entry, UmbraProfileData? profile, string initialsSource, Vector4 color)
    {
        var size = new Vector2(56f * ImGuiHelpers.GlobalScale);
        var rounding = 10f * ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? texture = null;
        var data = profile?.RpImageData.Value ?? [];
        if (data.Length > 0)
        {
            var key = $"{entry.Uid}_{entry.Name}_{entry.WorldId}";
            if (!_nearbyTextureTasks.TryGetValue(key, out var cached)
                || (!ReferenceEquals(data, cached.Data) && !data.AsSpan().SequenceEqual(cached.Data)))
            {
                if (cached.Task != null)
                    cached.Task.DisposeResultWhenCompleted();
                cached = (data, Task.Run(() => _uiSharedService.LoadImageAsync(data)));
                _nearbyTextureTasks[key] = cached;
            }
            if (cached.Task.IsCompletedSuccessfully) texture = cached.Task.Result;
        }

        if (texture != null && texture.Handle != IntPtr.Zero)
        {
            dl.AddImageRounded(texture.Handle, start, start + size, Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(Vector4.One), rounding);
        }
        else
        {
            dl.AddRectFilled(start, start + size, ImGui.GetColorU32(color with { W = 0.16f }), rounding);
            var initials = UiSharedService.GetInitials(initialsSource);
            if (initials.Length > 0)
            {
                using var font = _uiSharedService.UidFont.Push();
                var textSize = ImGui.CalcTextSize(initials);
                dl.AddText(start + (size - textSize) / 2f, ImGui.GetColorU32(color with { W = 0.85f }), initials);
            }
        }

        dl.AddRect(start, start + size, ImGui.GetColorU32(color with { W = 0.45f }), rounding, ImDrawFlags.None, ImGuiHelpers.GlobalScale);
        ImGui.Dummy(size);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var pending in _nearbyTextureTasks.Values)
                pending.Task.DisposeResultWhenCompleted();
            _nearbyTextureTasks.Clear();
            foreach (var pending in _syncshellIconTasks.Values)
                pending.Task.DisposeResultWhenCompleted();
            _syncshellIconTasks.Clear();
        }

        base.Dispose(disposing);
    }

    private async Task JoinSyncshellAsync(SyncshellDiscoveryEntryDto entry)
    {
        if (!_syncshellJoinInFlight.Add(entry.GID))
        {
            return;
        }

        try
        {
            var joined = await _syncshellDiscoveryService.JoinAsync(entry.GID, CancellationToken.None).ConfigureAwait(false);
            if (joined)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("AutoDetectUi.Syncshell.NotificationTitle"), string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Syncshell.Joined"), entry.Alias ?? entry.GID), NotificationType.Success, TimeSpan.FromSeconds(5)));
                await _syncshellDiscoveryService.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _syncshellLastError = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Syncshell.JoinFailed"), entry.Alias ?? entry.GID);
                Mediator.Publish(new NotificationMessage(Loc.Get("AutoDetectUi.Syncshell.NotificationTitle"), _syncshellLastError, NotificationType.Warning, TimeSpan.FromSeconds(5)));
                _notificationTracker.Upsert(NotificationEntry.SyncshellJoinFailed(entry.Alias ?? entry.GID, "join returned false"));
            }
        }
        catch (Exception ex)
        {
            _syncshellLastError = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Syncshell.JoinError"), ex.Message);
            Mediator.Publish(new NotificationMessage(Loc.Get("AutoDetectUi.Syncshell.NotificationTitle"), _syncshellLastError, NotificationType.Error, TimeSpan.FromSeconds(5)));
            _notificationTracker.Upsert(NotificationEntry.SyncshellJoinFailed(entry.Alias ?? entry.GID, ex.Message));
        }
        finally
        {
            _syncshellJoinInFlight.Remove(entry.GID);
        }
    }

    private void DrawSyncshellTab()
    {
        if (!_syncshellInitialized)
        {
            _syncshellInitialized = true;
            _ = _syncshellDiscoveryService.RefreshAsync(CancellationToken.None);
        }

        bool isRefreshing = _syncshellDiscoveryService.IsRefreshing;
        var serviceError = _syncshellDiscoveryService.LastError;

        // Barre unique : actualiser, recherche sur toute la largeur restante, filtre NSFW calé à droite.
        var style = ImGui.GetStyle();
        if (_uiSharedService.IconButton(isRefreshing ? FontAwesomeIcon.Spinner : FontAwesomeIcon.SyncAlt) && !isRefreshing)
        {
            _ = _syncshellDiscoveryService.RefreshAsync(CancellationToken.None);
        }
        UiSharedService.AttachToolTip(Loc.Get(isRefreshing ? "AutoDetectUi.Syncshell.Refreshing" : "AutoDetectUi.Syncshell.RefreshTooltip"));

        string nsfwLabel = Loc.Get("AutoDetectUi.ShowNSFW");
        float toggleWidth = ToggleSwitch.MeasureWidth(nsfwLabel);
        ImGui.SameLine();
        float searchAvail = ImGui.GetContentRegionAvail().X;
        float searchInline = searchAvail - toggleWidth - style.ItemSpacing.X * 2f;
        bool toggleInline = searchInline >= 160f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextItemWidth(toggleInline ? searchInline : searchAvail);
        ImGui.InputTextWithHint("##syncshellSearch", Loc.Get("AutoDetectUi.Syncshell.Search"), ref _syncshellSearch, 64);
        if (toggleInline)
        {
            ImGui.SameLine(0, style.ItemSpacing.X * 2f);
        }
        ToggleSwitch.Draw(nsfwLabel, ref _showNsfwSyncshells);

        ImGuiHelpers.ScaledDummy(4);
        UiSharedService.DrawNotice(Loc.Get("AutoDetectUi.Syncshell.Description"), ImGuiColors.DalamudYellow, FontAwesomeIcon.ShieldAlt);

        if (!string.IsNullOrEmpty(serviceError))
        {
            UiSharedService.ColorTextWrapped(serviceError, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(_syncshellLastError))
        {
            UiSharedService.ColorTextWrapped(_syncshellLastError!, ImGuiColors.DalamudOrange);
        }

        var entries = _syncshellEntries.Count > 0 ? _syncshellEntries : _syncshellDiscoveryService.Entries.ToList();

        var filteredEntries = entries
            .Where(e => _showNsfwSyncshells || !e.IsNsfw)
            .Where(e => MatchesSyncshellSearch(e, _syncshellSearch))
            .OrderBy(e => e.Alias ?? e.GID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGuiHelpers.ScaledDummy(4);

        if (filteredEntries.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.Search,
                Loc.Get("EmptyState.SyncFinder.Title"), Loc.Get("AutoDetectUi.Syncshell.Empty"));
            return;
        }

        // Colonnes communes à toutes les cartes : sans elles, compteur et jauge se décalent
        // d'une carte à l'autre selon la longueur du nom du propriétaire.
        float ownerWidth = 0f;
        float membersWidth = 0f;
        foreach (var entry in filteredEntries)
        {
            ownerWidth = MathF.Max(ownerWidth, ImGui.CalcTextSize(SyncshellOwner(entry)).X);
            membersWidth = MathF.Max(membersWidth, ImGui.CalcTextSize(SyncshellMembers(entry)).X);
        }

        foreach (var entry in filteredEntries)
        {
            DrawSyncshellCard(entry, ownerWidth, membersWidth);
            ImGuiHelpers.ScaledDummy(4);
        }
    }

    private static string SyncshellOwner(SyncshellDiscoveryEntryDto entry)
        => string.IsNullOrEmpty(entry.OwnerAlias) ? entry.OwnerUID : entry.OwnerAlias;

    private static string SyncshellMembers(SyncshellDiscoveryEntryDto entry)
        => entry.MaxUserCount > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{entry.MemberCount} / {entry.MaxUserCount}")
            : entry.MemberCount.ToString(CultureInfo.CurrentCulture);

    private static bool MatchesSyncshellSearch(SyncshellDiscoveryEntryDto entry, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        return (entry.Alias?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.GID.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (entry.OwnerAlias?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Tags?.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private void DrawSyncshellCard(SyncshellDiscoveryEntryDto entry, float ownerWidth, float membersWidth)
    {
        bool alreadyMember = _pairManager.Groups.Keys.Any(g => string.Equals(g.GID, entry.GID, StringComparison.OrdinalIgnoreCase));
        bool joining = _syncshellJoinInFlight.Contains(entry.GID);
        float scale = ImGuiHelpers.GlobalScale;
        float rightEdge = ImGui.GetWindowContentRegionMax().X - UiSharedService.GetCardContentPaddingX();
        string name = string.IsNullOrEmpty(entry.Alias) ? entry.GID : entry.Alias;
        var profile = GetDiscoveryProfile(entry.GID, alreadyMember);
        // Même couleur que la carte de l'onglet Syncshells : celle du profil, sinon dérivée du GID.
        var tint = GroupPanel.GetSyncshellColor(entry.GID, profile);
        var icon = GetSyncshellIcon(entry.GID, profile, entry.IsNsfw);

        UiSharedService.DrawCard("syncshell-" + entry.GID, () =>
        {
            // Même anatomie que les cartes de l'onglet Syncshells : pastille d'initiales, nom, détails et jauge,
            // action à droite centrée sur la pastille. Tags et description dessous, en pleine largeur.
            var style = ImGui.GetStyle();
            float rowTop = ImGui.GetCursorPosY();
            float rowStartX = ImGui.GetCursorPosX();
            float tileSize = 48f * scale;
            DrawSyncshellTile(name, tint, icon, tileSize);

            string actionLabel = alreadyMember ? Loc.Get("AutoDetectUi.Syncshell.Status.Member")
                : joining ? Loc.Get("AutoDetectUi.Syncshell.Status.Joining")
                : Loc.Get("AutoDetectUi.Syncshell.JoinButton");
            var statusIcon = alreadyMember ? FontAwesomeIcon.Check : FontAwesomeIcon.Spinner;
            float buttonWidth = ImGui.CalcTextSize(actionLabel).X + style.FramePadding.X * 2f + 12f * scale;
            float actionWidth = alreadyMember || joining ? PillWidth(actionLabel, statusIcon) : buttonWidth;

            float lineHeight = ImGui.GetTextLineHeight();
            float barHeight = 5f * scale;
            bool hasBar = entry.MaxUserCount > 0;
            float blockHeight = lineHeight * 2f + style.ItemSpacing.Y + (hasBar ? style.ItemSpacing.Y + barHeight : 0f);
            float textX = rowStartX + tileSize + 10f * scale;
            float textWidth = MathF.Max(40f * scale, rightEdge - actionWidth - 10f * scale - textX);

            ImGui.SetCursorPos(new Vector2(textX, rowTop + MathF.Max(0f, (tileSize - blockHeight) / 2f)));
            ImGui.BeginGroup();

            float nameWidth = textWidth;
            if (entry.IsNsfw)
            {
                DrawPill("NSFW", ImGuiColors.DalamudRed, lineHeight);
                nameWidth -= ImGui.GetItemRectSize().X + 6f * scale;
                ImGui.SameLine(0, 6f * scale);
            }
            var shownName = UiSharedService.TruncateToWidth(name, nameWidth);
            UiSharedService.ColorText(shownName, UiSharedService.ThemeTextAccent);
            if (!string.Equals(shownName, name, StringComparison.Ordinal))
                UiSharedService.AttachToolTip(name);

            // Colonnes communes à toutes les cartes (largeurs calculées sur la liste filtrée).
            float iconColumn = 22f * scale;
            float membersStart = iconColumn + ownerWidth + 16f * scale;
            DrawSyncshellIcon(FontAwesomeIcon.Crown);
            ImGui.SameLine(iconColumn);
            UiSharedService.ColorText(SyncshellOwner(entry), ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Syncshell.Table.Owner"));

            ImGui.SameLine(membersStart);
            DrawSyncshellIcon(FontAwesomeIcon.Users);
            ImGui.SameLine(membersStart + iconColumn);
            UiSharedService.ColorText(SyncshellMembers(entry), ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Syncshell.Table.Members"));

            if (hasBar)
            {
                float barWidth = MathF.Min(textWidth, MathF.Max(170f * scale, membersStart + iconColumn + membersWidth));
                DrawSyncshellFillBar(entry.MemberCount / (float)entry.MaxUserCount, tint, new Vector2(barWidth, barHeight));
            }
            ImGui.EndGroup();

            float headerBottom = MathF.Max(rowTop + tileSize, ImGui.GetCursorPosY() - style.ItemSpacing.Y);
            ImGui.SetCursorPos(new Vector2(
                MathF.Max(textX, rightEdge - actionWidth),
                rowTop + MathF.Max(0f, (headerBottom - rowTop - ImGui.GetFrameHeight()) / 2f)));
            if (alreadyMember)
            {
                DrawPill(actionLabel, ImGuiColors.HealerGreen, ImGui.GetFrameHeight(), statusIcon);
            }
            else if (joining)
            {
                DrawPill(actionLabel, ImGuiColors.DalamudGrey, ImGui.GetFrameHeight(), statusIcon);
            }
            else
            {
                using (ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeSliderGrabActive))
                {
                    if (ImGui.Button(actionLabel, new Vector2(buttonWidth, 0)))
                    {
                        _syncshellLastError = null;
                        _ = JoinSyncshellAsync(entry);
                    }
                }
            }

            bool hasTags = entry.Tags is { Length: > 0 };
            // Retour sous l'en-tête uniquement s'il reste du contenu : un SetCursorPos sans item derrière
            // étendrait la carte et déclencherait l'assertion d'ImGui.
            if (hasTags || !string.IsNullOrEmpty(entry.Description))
                ImGui.SetCursorPos(new Vector2(rowStartX, headerBottom + 8f * scale));

            if (hasTags)
            {
                float gap = 4f * scale;
                float lineX = rowStartX;
                for (int i = 0; i < entry.Tags!.Length; i++)
                {
                    float width = PillWidth(entry.Tags[i]);
                    if (i > 0 && lineX + gap + width <= rightEdge)
                    {
                        ImGui.SameLine(0, gap);
                        lineX += gap + width;
                    }
                    else
                    {
                        lineX = rowStartX + width;
                    }
                    DrawPill(entry.Tags[i], UiSharedService.ThemeTextAccent, lineHeight + 2f * scale);
                }
            }

            if (!string.IsNullOrEmpty(entry.Description))
            {
                if (hasTags) ImGuiHelpers.ScaledDummy(1f);
                UiSharedService.ColorTextWrapped(entry.Description, ImGuiColors.DalamudGrey, rightEdge);
            }
        }, stretchWidth: true);
    }

    private void DrawSyncshellTile(string name, Vector4 tint, IDalamudTextureWrap? icon, float size)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(size);
        float rounding = 10f * ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(tint with { W = 0.22f }), rounding);
        if (icon != null && icon.Handle != IntPtr.Zero)
        {
            drawList.AddImageRounded(icon.Handle, min, max, Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), rounding);
        }
        else
        {
            using (_uiSharedService.UidFont.Push())
            {
                var initials = GroupPanel.GetSyncshellInitials(name);
                drawList.AddText(min + (new Vector2(size) - ImGui.CalcTextSize(initials)) / 2f, ImGui.GetColorU32(tint), initials);
            }
        }
        drawList.AddRect(min, max, ImGui.GetColorU32(tint with { W = 0.65f }), rounding, ImDrawFlags.None, ImGuiHelpers.GlobalScale);
        ImGui.Dummy(new Vector2(size));
    }

    private GroupProfileDto? GetDiscoveryProfile(string gid, bool alreadyMember)
    {
        var live = _profileManager.GetGroupProfile(gid);
        if (live != null) return live;
        if (_discoveryProfiles.TryGetValue(gid, out var fetched) && fetched != null) return fetched;

        if ((_discoveryProfileFetch?.IsCompleted ?? true)
            && DateTime.UtcNow - _lastDiscoveryProfileFetchUtc >= TimeSpan.FromMilliseconds(400)
            && _discoveryProfileRequests.TryAdd(gid, 0))
        {
            _lastDiscoveryProfileFetchUtc = DateTime.UtcNow;
            _discoveryProfileFetch = FetchDiscoveryProfileAsync(gid, alreadyMember);
        }

        return alreadyMember ? _profileManager.GetCachedGroupProfile(gid) : null;
    }

    private async Task FetchDiscoveryProfileAsync(string gid, bool alreadyMember)
    {
        try
        {
            var profile = await _apiController.GroupGetProfile(new GroupDto(new GroupData(gid))).ConfigureAwait(false);
            if (profile != null && alreadyMember)
                _profileManager.SetGroupProfile(gid, profile);
            else
                _discoveryProfiles[gid] = profile;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profil de syncshell indisponible pour {gid}", gid);
        }
    }

    private IDalamudTextureWrap? GetSyncshellIcon(string gid, GroupProfileDto? profile, bool listedNsfw)
    {
        // Une syncshell NSFW n'affiche pas son image tant que l'utilisateur n'a pas choisi de les voir.
        bool hideNsfw = (listedNsfw || profile?.IsNsfw == true) && !_configService.Current.ProfilesAllowNsfw;
        string? source = profile is { IsDisabled: false } && !hideNsfw ? profile.ProfileImageBase64 : null;

        if (string.IsNullOrEmpty(source))
        {
            if (_syncshellIconTasks.Remove(gid, out var stale))
                stale.Task.DisposeResultWhenCompleted();
            return null;
        }

        if (_syncshellIconTasks.TryGetValue(gid, out var entry) && string.Equals(entry.Source, source, StringComparison.Ordinal))
            return entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;

        if (entry.Task != null)
            entry.Task.DisposeResultWhenCompleted();
        _syncshellIconTasks[gid] = (source, LoadSyncshellIconAsync(source));
        return null;
    }

    private async Task<IDalamudTextureWrap?> LoadSyncshellIconAsync(string imageBase64)
    {
        try
        {
            return await _uiSharedService.LoadImageAsync(Convert.FromBase64String(imageBase64)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Image de syncshell illisible");
            return null;
        }
    }

    private static float PillWidth(string text, FontAwesomeIcon? icon = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = ImGui.CalcTextSize(text).X + 16f * scale;
        if (icon != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                width += ImGui.CalcTextSize(icon.Value.ToIconString()).X + 5f * scale;
        }
        return width;
    }

    private static void DrawPill(string text, Vector4 color, float height, FontAwesomeIcon? icon = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(PillWidth(text, icon), height);
        var min = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(color with { W = 0.14f }), height / 2f);
        drawList.AddRect(min, min + size, ImGui.GetColorU32(color with { W = 0.40f }), height / 2f, ImDrawFlags.None, scale);

        float x = min.X + 8f * scale;
        float textY = min.Y + (height - ImGui.GetTextLineHeight()) / 2f;
        if (icon != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.Value.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(new Vector2(x, min.Y + (height - glyphSize.Y) / 2f), ImGui.GetColorU32(color), glyph);
                x += glyphSize.X + 5f * scale;
            }
        }
        drawList.AddText(new Vector2(x, textY), ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    private static void DrawSyncshellIcon(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(ImGuiColors.DalamudGrey3, icon.ToIconString());
    }

    private static void DrawSyncshellFillBar(float ratio, Vector4 tint, Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        ratio = Math.Clamp(ratio, 0f, 1f);
        var fill = ratio >= 0.9f ? ImGuiColors.DalamudOrange : tint with { W = 0.9f };
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(UiSharedService.ThemeFrameBgActive), size.Y / 2f);
        if (ratio > 0f)
            drawList.AddRectFilled(pos, pos + new Vector2(MathF.Max(size.Y, size.X * ratio), size.Y), ImGui.GetColorU32(fill), size.Y / 2f);

        ImGui.Dummy(size);
    }

    private void OnDiscoveryUpdated(Services.Mediator.DiscoveryListUpdated msg)
    {
        _entries = msg.Entries;
    }

    private void OnSyncshellDiscoveryUpdated(SyncshellDiscoveryUpdated msg)
    {
        _syncshellEntries = msg.Entries;
    }

    private bool IsAlreadyPairedByUidOrAlias(Services.Mediator.NearbyEntry e)
    {
        try
        {
            // 1) Match by UID when available (authoritative)
            if (!string.IsNullOrEmpty(e.Uid))
            {
                foreach (var p in _pairManager.DirectPairs)
                {
                    if (string.Equals(p.UserData.UID, e.Uid, StringComparison.Ordinal))
                        return true;
                }
            }
            var key = NormalizeKey(e.DisplayName ?? e.Name);
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var p in _pairManager.DirectPairs)
            {
                if (string.Equals(NormalizeKey(p.UserData.AliasOrUID), key, StringComparison.Ordinal))
                    return true;
                if (!string.IsNullOrEmpty(p.UserData.Alias) &&
                    string.Equals(NormalizeKey(p.UserData.Alias), key, StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // ignore matching errors and treat as not paired
        }
        return false;
    }

    private static string NormalizeKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var formD = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private void TriggerAccept(string uid)
    {
        if (!_acceptInFlight.Add(uid)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await _pendingService.AcceptAsync(uid).ConfigureAwait(false);
                if (!ok)
                {
                    Mediator.Publish(new NotificationMessage(Loc.Get("AutoDetectUi.Notification.AcceptTitle"), string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Notification.AcceptFailed"), uid), NotificationType.Warning, TimeSpan.FromSeconds(5)));
                    _notificationTracker.Upsert(NotificationEntry.AcceptPairRequestFailed(uid));
                }
            }
            finally
            {
                _acceptInFlight.Remove(uid);
            }
        });
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes >= 1)
        {
            var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
            return minutes == 1 ? Loc.Get("AutoDetectUi.Duration.Minute.Single") : string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Duration.Minute.Plural"), minutes);
        }

        var seconds = Math.Max(1, (int)Math.Round(span.TotalSeconds));
        return seconds == 1 ? Loc.Get("AutoDetectUi.Duration.Second.Single") : string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Duration.Second.Plural"), seconds);
    }
}