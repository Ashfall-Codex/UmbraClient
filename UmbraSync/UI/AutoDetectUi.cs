using Dalamud.Bindings.ImGui;
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

namespace UmbraSync.UI;

public class AutoDetectUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamud;
    private readonly AutoDetectRequestService _requestService;
    private readonly NearbyDiscoveryService _discoveryService;
    private readonly NearbyPendingService _pendingService;
    private readonly PairManager _pairManager;
    private List<Services.Mediator.NearbyEntry> _entries;
    private readonly HashSet<string> _acceptInFlight = new(StringComparer.Ordinal);
    private readonly SyncshellDiscoveryService _syncshellDiscoveryService;
    private readonly NotificationTracker _notificationTracker;
    private List<SyncshellDiscoveryEntryDto> _syncshellEntries = [];
    private bool _syncshellInitialized;
    private readonly HashSet<string> _syncshellJoinInFlight = new(StringComparer.OrdinalIgnoreCase);
    private string? _syncshellLastError;
    private bool _showNsfwSyncshells;
    private int _activeTab;

    public AutoDetectUi(ILogger<AutoDetectUi> logger, MareMediator mediator,
        MareConfigService configService, DalamudUtilService dalamudUtilService,
        AutoDetectRequestService requestService, NearbyPendingService pendingService, PairManager pairManager,
        NearbyDiscoveryService discoveryService, SyncshellDiscoveryService syncshellDiscoveryService,
        PerformanceCollectorService performanceCollectorService, NotificationTracker notificationTracker)
        : base(logger, mediator, "AutoDetect", performanceCollectorService)
    {
        _configService = configService;
        _dalamud = dalamudUtilService;
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
        const float btnH = 32f;
        const float btnSpacing = 8f;
        const float rounding = 4f;
        const float iconTextGap = 6f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var btnW = (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length;

        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##adTab_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool isActive = _activeTab == i;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSz = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var labelSz = ImGui.CalcTextSize(labels[i]);
            var totalW = iconSz.X + iconTextGap + labelSz.X;
            var startX = p.X + (btnW - totalW) / 2f;

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(startX, p.Y + (btnH - iconSz.Y) / 2f), textColorU32, iconStr);
            ImGui.PopFont();

            dl.AddText(new Vector2(startX + iconSz.X + iconTextGap, p.Y + (btnH - labelSz.Y) / 2f), textColorU32, labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) _activeTab = i;
        }

        ImGuiHelpers.ScaledDummy(4f);

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

    private void DrawInvitationsTab(List<KeyValuePair<string, PendingEntry>> incomingInvites, IReadOnlyCollection<AutoDetectRequestService.PendingRequestInfo> outgoingInvites)
    {
        if (incomingInvites.Count == 0 && outgoingInvites.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Invitations.EmptyAll"), ImGuiColors.DalamudGrey3);
            return;
        }

        if (incomingInvites.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Invitations.NoIncoming"), ImGuiColors.DalamudGrey3);
        }

        ImGuiHelpers.ScaledDummy(4);
        var receivedHeaderSample = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.ReceivedHeader"), 0);
        float leftWidth = Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.CalcTextSize(receivedHeaderSample).X + ImGui.GetStyle().FramePadding.X * 4f);
        var avail = ImGui.GetContentRegionAvail();

        ImGui.BeginChild("incoming-requests", new Vector2(leftWidth, avail.Y), true);
        ImGui.TextColored(ImGuiColors.DalamudOrange, string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.ReceivedHeader"), incomingInvites.Count));
        ImGui.Separator();
        if (incomingInvites.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("AutoDetectUi.Invitations.ReceivedEmpty"));
        }
        else
        {
            foreach (var (uid, entry) in incomingInvites.OrderByDescending(k => k.Value.ReceivedAtUtc))
            {
                using var id = ImRaii.PushId(uid);
                bool processing = _acceptInFlight.Contains(uid);
                ImGui.TextUnformatted(entry.DisplayName);
                ImGui.TextDisabled(uid);
                ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.ReceivedAgo"), FormatDuration(DateTime.UtcNow - entry.ReceivedAtUtc)));
                if (processing)
                {
                    ImGui.TextDisabled(Loc.Get("AutoDetectUi.Invitations.Processing"));
                }
                else
                {
                    if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Accept")))
                    {
                        TriggerAccept(uid);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Decline")))
                    {
                        _pendingService.Remove(uid);
                    }
                }
                ImGui.Separator();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("outgoing-requests", new Vector2(0, avail.Y), true);
        ImGui.TextColored(ImGuiColors.DalamudOrange, string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.SentHeader"), outgoingInvites.Count));
        ImGui.Separator();
        if (outgoingInvites.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("AutoDetectUi.Invitations.SentEmpty"));
            ImGui.EndChild();
            return;
        }

        foreach (var info in outgoingInvites.OrderByDescending(i => i.SentAt))
        {
            using var id = ImRaii.PushId(info.Key);
            ImGui.TextUnformatted(info.TargetDisplayName);
            if (!string.IsNullOrEmpty(info.Uid))
            {
                ImGui.TextDisabled(info.Uid);
            }

            ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Invitations.SentAgo"), FormatDuration(DateTime.UtcNow - info.SentAt)));
            if (ImGui.Button(Loc.Get("AutoDetectUi.Invitations.Remove")))
            {
                _requestService.RemovePendingRequestByKey(info.Key);
            }
            UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Invitations.RemoveTooltip"));
            ImGui.Separator();
        }

        ImGui.EndChild();
    }

    private void DrawNearbyTab()
    {
        if (!_configService.Current.EnableAutoDetectDiscovery)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Nearby.DisabledNotice"), ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(6);
        }

        int maxDist = MareConfig.AutoDetectFixedMaxDistanceMeters;

        var sourceEntries = _entries.Count > 0 ? _entries : _discoveryService.SnapshotEntries();
        // Build snapshot of pending invites to gray-out buttons
        var pendingInvites = _requestService.GetPendingRequestsSnapshot();
        var pendingUids = new HashSet<string>(pendingInvites.Select(p => p.Uid!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
        var pendingTokens = new HashSet<string>(pendingInvites.Select(p => p.Token!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
        var orderedEntries = sourceEntries
            .Where(e => e.IsMatch)
            .OrderBy(e => float.IsNaN(e.Distance) ? float.MaxValue : e.Distance)
            .ToList();

        if (orderedEntries.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Nearby.Empty"), ImGuiColors.DalamudGrey3);
            return;
        }

        if (!ImGui.BeginTable("autodetect-nearby", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Nearby.Table.Name"));
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Nearby.Table.World"));
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Nearby.Table.Distance"));
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Nearby.Table.Status"));
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Nearby.Table.Action"));
        ImGui.TableHeadersRow();

        for (int i = 0; i < orderedEntries.Count; i++)
        {
            var entry = orderedEntries[i];
            bool alreadyPaired = IsAlreadyPairedByUidOrAlias(entry);
            bool overDistance = !float.IsNaN(entry.Distance) && entry.Distance > maxDist;
            bool alreadyInvited = (!string.IsNullOrEmpty(entry.Uid) && pendingUids.Contains(entry.Uid))
                                  || (!string.IsNullOrEmpty(entry.Token) && pendingTokens.Contains(entry.Token));
            bool canRequest = entry.AcceptPairRequests && !string.IsNullOrEmpty(entry.Token) && !alreadyPaired && !alreadyInvited;

            string displayName = entry.DisplayName ?? entry.Name;
            string worldName = entry.WorldId == 0
                ? "-"
                : (_dalamud.WorldData.Value.TryGetValue(entry.WorldId, out var mappedWorld) ? mappedWorld : entry.WorldId.ToString(CultureInfo.InvariantCulture));
            string distanceText = float.IsNaN(entry.Distance) ? "-" : $"{entry.Distance:0.0} m";

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

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(displayName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(worldName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(distanceText);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status);

            ImGui.TableNextColumn();
            using (ImRaii.PushId(i))
            {
                if (canRequest && !overDistance)
                {
                    if (ImGui.Button(Loc.Get("AutoDetectUi.Nearby.InviteButton")))
                    {
                        _ = _requestService.SendRequestAsync(entry.Token!, entry.Uid, entry.DisplayName);
                    }
                    UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.InviteTooltip"));
                }
                else
                {
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

                    ImGui.TextDisabled(status);
                    if (!string.IsNullOrEmpty(reason))
                    {
                        UiSharedService.AttachToolTip(reason);
                    }
                }
            }
        }

        ImGui.EndTable();
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

        ImGuiHelpers.ScaledDummy(4);
        UiSharedService.TextWrapped(Loc.Get("AutoDetectUi.Syncshell.Description"));

        if (!string.IsNullOrEmpty(serviceError))
        {
            UiSharedService.ColorTextWrapped(serviceError, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(_syncshellLastError))
        {
            UiSharedService.ColorTextWrapped(_syncshellLastError!, ImGuiColors.DalamudOrange);
        }

        var entries = _syncshellEntries.Count > 0 ? _syncshellEntries : _syncshellDiscoveryService.Entries.ToList();

        ImGui.Checkbox(Loc.Get("AutoDetectUi.ShowNSFW"), ref _showNsfwSyncshells);
        ImGuiHelpers.ScaledDummy(4);

        var filteredEntries = entries
            .Where(e => _showNsfwSyncshells || !e.IsNsfw)
            .OrderBy(e => e.Alias ?? e.GID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filteredEntries.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(4);
            UiSharedService.ColorTextWrapped(Loc.Get("AutoDetectUi.Syncshell.Empty"), ImGuiColors.DalamudGrey3);
            return;
        }

        if (!ImGui.BeginTable("autodetect-syncshells", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Syncshell.Table.Name"), ImGuiTableColumnFlags.None, 2.5f);
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Syncshell.Table.Owner"), ImGuiTableColumnFlags.None, 1.5f);
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Syncshell.Table.Members"), ImGuiTableColumnFlags.None, 0.7f);
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Syncshell.Table.Capacity"), ImGuiTableColumnFlags.None, 0.7f);
        ImGui.TableSetupColumn(Loc.Get("AutoDetectUi.Syncshell.Table.Action"), ImGuiTableColumnFlags.None, 1);
        ImGui.TableHeadersRow();

        foreach (var entry in filteredEntries)
        {
            bool alreadyMember = _pairManager.Groups.Keys.Any(g => string.Equals(g.GID, entry.GID, StringComparison.OrdinalIgnoreCase));
            bool joining = _syncshellJoinInFlight.Contains(entry.GID);

            ImGui.TableNextColumn();
            var displayName = string.IsNullOrEmpty(entry.Alias) ? entry.GID : entry.Alias;
            if (entry.IsNsfw)
            {
                UiSharedService.ColorText("[NSFW] ", ImGuiColors.DalamudRed);
                ImGui.SameLine();
            }
            ImGui.TextUnformatted(displayName);

            // Info button next to name
            bool hasProfileInfo = !string.IsNullOrEmpty(entry.Description) || (entry.Tags != null && entry.Tags.Length > 0);
            if (hasProfileInfo)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 0.5f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 0.5f)))
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.6f, 0.8f, 1f, 1f)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
                {
                    ImGui.PushID($"info-{entry.GID}");
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextUnformatted(FontAwesomeIcon.InfoCircle.ToIconString());
                    ImGui.PopID();
                }
                // Tooltip with description + tags
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    if (entry.Tags != null && entry.Tags.Length > 0)
                    {
                        UiSharedService.ColorText(string.Join(", ", entry.Tags), new Vector4(0.6f, 0.8f, 1f, 1f));
                    }
                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        if (entry.Tags != null && entry.Tags.Length > 0) ImGuiHelpers.ScaledDummy(2f);
                        ImGui.PushTextWrapPos(300f * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(entry.Description);
                        ImGui.PopTextWrapPos();
                    }
                    ImGui.EndTooltip();
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrEmpty(entry.OwnerAlias) ? entry.OwnerUID : entry.OwnerAlias);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.MemberCount.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            var cap = entry.MaxUserCount > 0 ? entry.MaxUserCount.ToString(CultureInfo.InvariantCulture) : "-";
            ImGui.TextUnformatted(cap);

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(alreadyMember || joining))
            {
                if (alreadyMember)
                {
                    ImGui.TextDisabled(Loc.Get("AutoDetectUi.Syncshell.Status.Member"));
                }
                else if (joining)
                {
                    ImGui.TextDisabled(Loc.Get("AutoDetectUi.Syncshell.Status.Joining"));
                }
                else if (ImGui.Button(Loc.Get("AutoDetectUi.Syncshell.JoinButton")))
                {
                    _syncshellLastError = null;
                    _ = JoinSyncshellAsync(entry);
                }
            }
        }

        ImGui.EndTable();
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