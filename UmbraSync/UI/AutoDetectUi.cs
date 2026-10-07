using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Text;
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
    private const int MaxNearbyProfileCards = 15;

    public AutoDetectUi(ILogger<AutoDetectUi> logger, MareMediator mediator,
        MareConfigService configService,
        AutoDetectRequestService requestService, NearbyPendingService pendingService, PairManager pairManager,
        NearbyDiscoveryService discoveryService, SyncshellDiscoveryService syncshellDiscoveryService,
        PerformanceCollectorService performanceCollectorService, NotificationTracker notificationTracker,
        UmbraProfileManager profileManager, UiSharedService uiSharedService)
        : base(logger, mediator, "AutoDetect", performanceCollectorService)
    {
        _profileManager = profileManager;
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
                    Loc.Get("EmptyState.Nearby.Disabled.Button")))
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
                DrawNearbyPortrait(entry, rpProfile, hasRp ? rpName : displayName, nameColor);
                ImGui.SameLine();
                ImGui.BeginGroup();

                ImGui.AlignTextToFramePadding();
                UiSharedService.ColorText(hasRp ? rpName : displayName, nameColor);

                string actionLabel = canInvite ? Loc.Get("AutoDetectUi.Nearby.InviteButton") : status;
                float buttonWidth = ImGui.CalcTextSize(actionLabel).X + ImGui.GetStyle().FramePadding.X * 2f + 12f * scale;
                float actionWidth = canInvite ? buttonWidth : ImGui.CalcTextSize(actionLabel).X;

                ImGui.SameLine();
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rightEdge - actionWidth));
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

        if (ImGui.Button(Loc.Get("AutoDetectUi.Syncshell.RefreshButton")))
        {
            _ = _syncshellDiscoveryService.RefreshAsync(CancellationToken.None);
        }
        UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Syncshell.RefreshTooltip"));

        if (isRefreshing)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.Get("AutoDetectUi.Syncshell.Refreshing"));
        }

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.Search.ToIconString());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(260f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##syncshellSearch", Loc.Get("AutoDetectUi.Syncshell.Search"), ref _syncshellSearch, 64);
        string nsfwLabel = Loc.Get("AutoDetectUi.ShowNSFW");
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < ImGui.CalcTextSize(nsfwLabel).X + ImGui.GetFrameHeight() * 2.5f)
            ImGui.NewLine();
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

        UiSharedService.DrawCard("syncshell-" + entry.GID, () =>
        {
            // Nom à gauche, action à droite : c'est la ligne qu'on lit en parcourant la liste.
            ImGui.AlignTextToFramePadding();
            if (entry.IsNsfw)
            {
                UiSharedService.ColorText("[NSFW]", ImGuiColors.DalamudRed);
                ImGui.SameLine();
            }
            UiSharedService.ColorText(string.IsNullOrEmpty(entry.Alias) ? entry.GID : entry.Alias, UiSharedService.ThemeNavTextActive);

            string actionLabel = alreadyMember ? Loc.Get("AutoDetectUi.Syncshell.Status.Member")
                : joining ? Loc.Get("AutoDetectUi.Syncshell.Status.Joining")
                : Loc.Get("AutoDetectUi.Syncshell.JoinButton");
            float buttonWidth = ImGui.CalcTextSize(actionLabel).X + ImGui.GetStyle().FramePadding.X * 2f + 12f * scale;
            // Un état se cale sur le bord droit du bouton qu'il remplace, pas sur son bord gauche.
            float actionWidth = alreadyMember || joining ? ImGui.CalcTextSize(actionLabel).X : buttonWidth;

            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rightEdge - actionWidth));
            if (alreadyMember)
            {
                ImGui.AlignTextToFramePadding();
                UiSharedService.ColorText(actionLabel, ImGuiColors.HealerGreen);
            }
            else if (joining)
            {
                ImGui.AlignTextToFramePadding();
                UiSharedService.ColorText(actionLabel, ImGuiColors.DalamudGrey);
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

            float iconColumn = 24f * scale;
            float gap = 18f * scale;
            float membersStart = iconColumn + ownerWidth + gap;
            float barStart = membersStart + iconColumn + membersWidth + 10f * scale;

            DrawSyncshellIcon(FontAwesomeIcon.Crown);
            ImGui.SameLine(iconColumn);
            UiSharedService.ColorText(SyncshellOwner(entry), ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Syncshell.Table.Owner"));

            ImGui.SameLine(membersStart);
            DrawSyncshellIcon(FontAwesomeIcon.Users);
            ImGui.SameLine(membersStart + iconColumn);
            UiSharedService.ColorText(SyncshellMembers(entry), ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Syncshell.Table.Members"));

            if (entry.MaxUserCount > 0)
            {
                ImGui.SameLine(barStart);
                DrawSyncshellFillBar(entry.MemberCount / (float)entry.MaxUserCount);
            }

            if (entry.Tags is { Length: > 0 })
            {
                UiSharedService.ColorTextWrapped(string.Join("  ·  ", entry.Tags), UiSharedService.ThemeTextAccent,
                    rightEdge);
            }

            if (!string.IsNullOrEmpty(entry.Description))
            {
                UiSharedService.ColorTextWrapped(entry.Description, ImGuiColors.DalamudGrey, rightEdge);
            }
        }, stretchWidth: true);
    }

    private static void DrawSyncshellIcon(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(ImGuiColors.DalamudGrey3, icon.ToIconString());
    }

    private static void DrawSyncshellFillBar(float ratio)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(70f * scale, 4f * scale);
        var pos = ImGui.GetCursorScreenPos();
        pos.Y += (ImGui.GetTextLineHeight() - size.Y) / 2f;

        ratio = Math.Clamp(ratio, 0f, 1f);
        var fill = ratio >= 0.9f ? ImGuiColors.DalamudOrange : UiSharedService.AccentColor;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(UiSharedService.ThemeFrameBgActive), size.Y / 2f);
        if (ratio > 0f)
            drawList.AddRectFilled(pos, pos + new Vector2(size.X * ratio, size.Y), ImGui.GetColorU32(fill), size.Y / 2f);

        ImGui.Dummy(new Vector2(size.X, ImGui.GetTextLineHeight()));
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