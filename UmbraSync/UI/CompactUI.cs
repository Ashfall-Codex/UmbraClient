using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;
using UmbraSync.UI.Handlers;
using UmbraSync.WebAPI.Files;
using UmbraSync.WebAPI.Files.Models;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public partial class CompactUi : WindowMediatorSubscriberBase
{
    public float TransferPartHeight { get; internal set; }
    public float WindowContentWidth { get; private set; }
    private readonly ApiController _apiController;
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, ConcurrentDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileUploadManager _fileTransferManager;
    private readonly GroupPanel _groupPanel;
    private readonly PairGroupsUi _pairGroupsUi;
    private readonly PairManager _pairManager;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly SelectPairForGroupUi _selectPairsForGroupUi;
    private readonly ServerConfigurationManager _serverManager;
    private readonly CharaDataManager _charaDataManager;
    private readonly NearbyPendingService _nearbyPending;
    private readonly AutoDetectRequestService _autoDetectRequestService;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiSharedService;
    private DateTime? _connectingSinceUtc;
    private readonly EditProfileUi _editProfileUi;
    private readonly SettingsUi _settingsUi;
    private readonly AutoDetectUi _autoDetectUi;
    private readonly DataAnalysisUi _dataAnalysisUi;
    private readonly CharaDataHubUi _charaDataHubUi;
    private readonly NotificationTracker _notificationTracker;
    private readonly EstablishmentConfigService _establishmentConfigService;
    private readonly DalamudUtilService _dalamudUtilService;
    private SocialSubSection _socialSubSection = SocialSubSection.IndividualPairs;
    private Vector2 _lastPosition = Vector2.One;
    private Vector2 _lastSize = Vector2.One;
    private bool _wasOpen;
    private List<Services.Mediator.NearbyEntry> _nearbyEntries = new();
    private int _notificationCount;
    private CompactUiSection _activeSection = CompactUiSection.Social;
    private const float ContentFontScale = UiSharedService.ContentFontScale;

    private enum CompactUiSection
    {
        Notifications,
        Social,
        CharacterAnalysis,
        CharacterDataHub,
        EditProfile,
        Settings
    }

    private enum SocialSubSection
    {
        IndividualPairs,
        Syncshells,
        Invitations,
        Nearby,
        SyncFinder,
        Profiles,
        DirectoryBrowse,
        DirectoryFavorites,
        DirectoryMine,
        DirectoryUpcoming,
        DirectoryWildRp
    }

    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, MareConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverManager, MareMediator mediator, FileUploadManager fileTransferManager, UidDisplayHandler uidDisplayHandler, CharaDataManager charaDataManager,
        NearbyPendingService nearbyPendingService,
        AutoDetectRequestService autoDetectRequestService,
        CharacterAnalyzer characterAnalyzer,
        PerformanceCollectorService performanceCollectorService,
        EditProfileUi editProfileUi,
        SettingsUi settingsUi,
        AutoDetectUi autoDetectUi,
        DataAnalysisUi dataAnalysisUi,
        CharaDataHubUi charaDataHubUi,
        NotificationTracker notificationTracker,
        SyncshellConfigService syncshellConfig,
        EstablishmentConfigService establishmentConfigService,
        DalamudUtilService dalamudUtilService,
        SlotService slotService,
        UmbraProfileManager umbraProfileManager)
        : base(logger, mediator, "###UmbraSyncMainUI", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _uiSharedService = uiShared;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _fileTransferManager = fileTransferManager;
        _uidDisplayHandler = uidDisplayHandler;
        _charaDataManager = charaDataManager;
        _nearbyPending = nearbyPendingService;
        _autoDetectRequestService = autoDetectRequestService;
        _characterAnalyzer = characterAnalyzer;
        _editProfileUi = editProfileUi;
        _settingsUi = settingsUi;
        _autoDetectUi = autoDetectUi;
        _dataAnalysisUi = dataAnalysisUi;
        _charaDataHubUi = charaDataHubUi;
        _notificationTracker = notificationTracker;
        _establishmentConfigService = establishmentConfigService;
        var tagHandler = new TagHandler(_serverManager);

        _groupPanel = new(logger, this, uiShared, _pairManager, uidDisplayHandler, _serverManager, _charaDataManager, _autoDetectRequestService, _configService, syncshellConfig, slotService, umbraProfileManager);
        _selectGroupForPairUi = new(tagHandler, uidDisplayHandler, _uiSharedService);
        _selectPairsForGroupUi = new(tagHandler, uidDisplayHandler);
        _pairGroupsUi = new(configService, tagHandler, apiController, _selectPairsForGroupUi, _uiSharedService);

#if DEBUG
        WindowName = "UmbraSync###UmbraSyncMainUIDev";
        Toggle();
#else
        WindowName = "UmbraSync###UmbracSyncMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<OpenAutoDetectSettingsMessage>(this, (_) =>
        {
            _settingsUi.ShowAutoDetectTab();
            _activeSection = CompactUiSection.Settings;
        });
        Mediator.Subscribe<DiscoveryListUpdated>(this, (msg) =>
        {
            _nearbyEntries = msg.Entries;
            // Update last-seen character names for matched entries
            foreach (var e in _nearbyEntries.Where(x => x.IsMatch))
            {
                var uid = e.Uid;
                var lastSeen = e.DisplayName ?? e.Name;
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(lastSeen))
                {
                    _serverManager.SetNameForUid(uid, lastSeen);
                }
            }
        });
        Mediator.Subscribe<NotificationStateChanged>(this, msg => _notificationCount = msg.TotalCount);
        Mediator.Subscribe<EstablishmentChangedMessage>(this, (_) => AnnuaireRefreshAll());
        Mediator.Subscribe<EstablishmentAffiliationRequestedMessage>(this, msg =>
        {
            var entry = NotificationEntry.EstablishmentAffiliationRequested(msg.Affiliation);
            _notificationTracker.Upsert(entry);
            Mediator.Publish(new NotificationMessage(entry.Title, entry.Description ?? string.Empty, NotificationType.Info));
        });
        Mediator.Subscribe<EstablishmentAffiliationRespondedMessage>(this, msg =>
        {
            var entry = NotificationEntry.EstablishmentAffiliationResponded(msg.Affiliation);
            _notificationTracker.Upsert(entry);
            Mediator.Publish(new NotificationMessage(entry.Title, entry.Description ?? string.Empty, NotificationType.Info));
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _drawUserPairCache.Clear();
            _groupPanel.ClearCache();
        });
        _notificationCount = _notificationTracker.Count;

        Flags |= ImGuiWindowFlags.NoDocking;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(1400, 2000),
        };
    }

    public override void PreDraw()
    {
        base.PreDraw();
        ImGui.PushStyleColor(ImGuiCol.Border, UiSharedService.ThemeTitleBar);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(1);
        base.PostDraw();
    }

    protected override void DrawInternal()
    {
        var sidebarWidth = ImGuiHelpers.ScaledVector2(SidebarWidth, 0).X;
        DrawContentBackdrop(sidebarWidth, UiSharedService.GlassAlpha(UiSharedService.ResolveGlassLevel(WindowGlassLevel)));

        DrawTitleAccent();

        using var fontScale = UiSharedService.PushFontScale(ContentFontScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * ContentFontScale);

        ImGui.BeginChild("compact-sidebar", new Vector2(sidebarWidth, 0), false, ImGuiWindowFlags.NoScrollbar);
        DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        float separatorX = ImGui.GetCursorPosX();
        float separatorY = ImGui.GetCursorPosY();
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        (float separatorTop, float separatorBottom) = GetChromeVerticalBounds();
        start = new Vector2(start.X, separatorTop);
        var end = new Vector2(start.X, separatorBottom);
        var separatorColor = UiSharedService.AccentColor with { W = 0.6f };
        drawList.AddLine(start, end, ImGui.GetColorU32(separatorColor), 1f * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPos(new Vector2(separatorX + 6f * ImGuiHelpers.GlobalScale, separatorY));
        using var contentBg = ImRaii.PushColor(ImGuiCol.ChildBg, UiSharedService.ThemeChildBg);

        ImGui.BeginChild("compact-content", Vector2.Zero, false);
        WindowContentWidth = UiSharedService.GetWindowContentRegionWidth();

        if (!_apiController.IsCurrentVersion)
        {
            DrawUnsupportedVersionBanner();
            ImGui.Separator();
        }

        bool connecting = IsConnecting;
        if (!connecting) _connectingSinceUtc = null;
        else _connectingSinceUtc ??= DateTime.UtcNow;

        if (_apiController.ServerState is ServerState.NoSecretKey)
        {
            UiSharedService.ColorTextWrapped(GetServerError(), GetUidColor());
            DrawAddCharacter();
            DrawAccentSeparator();
        }
        else if (_apiController.ServerState is not ServerState.Connected && !RequiresServerConnection(_activeSection))
        {
            // Les sections utilisables hors connexion gardent leur contenu, sous une ligne d'état discrète.
            DrawStatusInline(GetStatusContent());
            DrawAccentSeparator();
        }

        DrawMainContent();
        CheckWildRpExpiry();

        ImGui.EndChild();

        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (_lastSize != size || _lastPosition != pos)
        {
            _lastSize = size;
            _lastPosition = pos;
            Mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
        }

        ImGui.PopStyleVar();
    }
    
    private static (float Top, float Bottom) GetChromeVerticalBounds()
    {
        var style = ImGui.GetStyle();
        float winTop = ImGui.GetWindowPos().Y;
        return (winTop + ImGui.GetCursorStartPos().Y - style.WindowPadding.Y,
                winTop + ImGui.GetWindowHeight() - style.WindowBorderSize);
    }

    private static void DrawContentBackdrop(float sidebarWidth, float glassAlpha)
    {
        var style = ImGui.GetStyle();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        (float top, float bottom) = GetChromeVerticalBounds();
        float left = winPos.X + style.WindowPadding.X + sidebarWidth + style.ItemSpacing.X;
        float right = winPos.X + winSize.X - style.WindowBorderSize;

        if (right <= left || bottom <= top) return;
        var drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(winPos, winPos + winSize, false);
        drawList.AddRectFilled(
            new Vector2(left, top),
            new Vector2(right, bottom),
            ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, glassAlpha)),
            MathF.Max(0f, UiSharedService.RadiusWindow * ImGuiHelpers.GlobalScale - style.WindowBorderSize),
            ImDrawFlags.RoundCornersBottomRight);
        drawList.PopClipRect();
    }

    public override void OnClose()
    {
        _uidDisplayHandler.Clear();
        base.OnClose();
    }

    private void DrawMainContent()
    {
        if (_activeSection is CompactUiSection.EditProfile)
        {
            _editProfileUi.DrawInline();
            DrawNewUserNoteModal();
            return;
        }

        bool requiresConnection = RequiresServerConnection(_activeSection);
        if (requiresConnection && _apiController.ServerState is not ServerState.Connected)
        {
            if (_apiController.ServerState is ServerState.NoSecretKey)
                UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.General.ConnectToServerNotice"), ImGuiColors.DalamudGrey3);
            else
                DrawStatusPanel(GetStatusContent());
            DrawNewUserNoteModal();
            return;
        }

        switch (_activeSection)
        {
            case CompactUiSection.Notifications:
                DrawNotificationsSection();
                break;
            case CompactUiSection.Social:
                DrawSocialSection();
                break;
            case CompactUiSection.CharacterAnalysis:
                if (_dataAnalysisUi.IsOpen) _dataAnalysisUi.IsOpen = false;
                _dataAnalysisUi.DrawInline();
                break;
            case CompactUiSection.CharacterDataHub:
                if (_charaDataHubUi.IsOpen) _charaDataHubUi.IsOpen = false;
                _charaDataHubUi.DrawInline();
                break;
            case CompactUiSection.Settings:
                if (_settingsUi.IsOpen) _settingsUi.IsOpen = false;
                _settingsUi.DrawInline();
                break;
        }

        DrawNewUserNoteModal();
    }

    private void DrawPairSectionBody()
    {
        using var font = UiSharedService.PushFontScale(UiSharedService.ContentFontScale);
        using (ImRaii.PushId("pairlist")) DrawPairList();
        using (ImRaii.PushId("transfers")) DrawTransfers();
        TransferPartHeight = ImGui.GetCursorPosY() - TransferPartHeight;
        using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
        using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
    }

    private void DrawSyncshellSection()
    {
        using (ImRaii.PushId("syncshells"))
            _groupPanel.DrawSyncshells();
        using (ImRaii.PushId("transfers")) DrawTransfers();
        TransferPartHeight = ImGui.GetCursorPosY() - TransferPartHeight;
        using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
        using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
    }

    private readonly SideRail _socialRail = new();
    private bool _syncshellDefaultsExpanded;

    private void DrawSocialSection()
    {
        float railWidth = SideRail.WidthFor(ImGui.GetContentRegionAvail().X);
        bool railCollapsed = railWidth < SideRail.ExpandedWidth * ImGuiHelpers.GlobalScale;

        using (var rail = ImRaii.Child("social-rail", new Vector2(railWidth, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            if (rail)
            {
                int active = (int)_socialSubSection;
                _socialRail.Draw(BuildSocialRail(), ref active, railCollapsed);
                _socialSubSection = (SocialSubSection)active;
            }
        }

        ImGui.SameLine();
        var separatorStart = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(
            separatorStart,
            separatorStart with { Y = separatorStart.Y + ImGui.GetContentRegionAvail().Y },
            ImGui.GetColorU32(UiSharedService.WithAlpha(UiSharedService.AccentColor, 0.6f)),
            1f * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f * ImGuiHelpers.GlobalScale);

        // Les listes de paires et de syncshells gèrent leur propre défilement ; les autres pages défilent ici.
        bool selfScrolling = _socialSubSection is SocialSubSection.IndividualPairs or SocialSubSection.Syncshells
            or SocialSubSection.Profiles;
        using var socialBody = ImRaii.Child(
            "social-body",
            new Vector2(0, 0),
            false,
            selfScrolling ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None);
        if (!socialBody) return;

        // Les listes se dimensionnent sur WindowContentWidth : tant que le rail occupe la gauche,
        // elles doivent se caler sur la zone de contenu et non sur toute la fenêtre.
        var fullWidth = WindowContentWidth;
        WindowContentWidth = ImGui.GetContentRegionAvail().X;
        try
        {
            DrawSocialPage();
        }
        finally
        {
            WindowContentWidth = fullWidth;
        }
    }

    private void DrawSocialPage()
    {
        if (_socialSubSection is SocialSubSection.Syncshells)
        {
            // Les réglages par défaut sont repliés : ils ne sont utiles qu'à l'occasion, la liste passe avant.
            var expanded = _syncshellDefaultsExpanded;
            UiSharedService.DrawArrowToggle(ref expanded, "##syncshellDefaultsToggle");
            _syncshellDefaultsExpanded = expanded;
            ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
            UiSharedService.ColorText(Loc.Get("CompactUi.Social.DefaultSettings"), UiSharedService.ThemeTextAccent);
            if (expanded) DrawDefaultSyncSettings();
            ImGuiHelpers.ScaledDummy(2f);
        }
        else if (_socialSubSection is SocialSubSection.IndividualPairs)
        {
            DrawDefaultSyncSettings();
            ImGuiHelpers.ScaledDummy(2f);
        }

        switch (_socialSubSection)
        {
            case SocialSubSection.IndividualPairs:
                DrawPairSectionBody();
                break;
            case SocialSubSection.Syncshells:
                DrawSyncshellSection();
                break;
            case SocialSubSection.Invitations:
                _autoDetectUi.DrawInvitationsPage();
                break;
            case SocialSubSection.Nearby:
                _autoDetectUi.DrawNearbyPage();
                break;
            case SocialSubSection.SyncFinder:
                _autoDetectUi.DrawSyncFinderPage();
                break;
            case SocialSubSection.Profiles:
                _charaDataHubUi.DrawProfilesPage();
                break;
            case SocialSubSection.DirectoryBrowse:
            case SocialSubSection.DirectoryFavorites:
            case SocialSubSection.DirectoryMine:
            case SocialSubSection.DirectoryUpcoming:
            case SocialSubSection.DirectoryWildRp:
                DrawDirectoryPage();
                break;
        }

        if (!IsDirectoryPage(_socialSubSection))
            _directoryEnteredPage = null;
    }

    private SocialSubSection? _directoryEnteredPage;

    private static bool IsDirectoryPage(SocialSubSection page)
        => page is SocialSubSection.DirectoryBrowse or SocialSubSection.DirectoryFavorites
            or SocialSubSection.DirectoryMine or SocialSubSection.DirectoryUpcoming or SocialSubSection.DirectoryWildRp;

    private void DrawDirectoryPage()
    {
        int tab = _socialSubSection switch
        {
            SocialSubSection.DirectoryMine => 0,
            SocialSubSection.DirectoryFavorites => 1,
            SocialSubSection.DirectoryBrowse => 2,
            SocialSubSection.DirectoryUpcoming => 3,
            _ => 4,
        };
        bool entering = _directoryEnteredPage != _socialSubSection;
        _directoryEnteredPage = _socialSubSection;
        DrawAnnuaireSection(tab, entering);
    }

    private List<SideRailEntry> BuildSocialRail()
    {
        int invitations = _autoDetectUi.PendingInvitationCount;
        var invitationsLabel = invitations > 0
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetectUi.Tab.Invitations"), invitations)
            : Loc.Get("CompactUi.Social.Nav.Invitations");

        return
        [
            SideRailEntry.Group(Loc.Get("CompactUi.Social.Nav.Group.Contacts")),
            new((int)SocialSubSection.Invitations, invitationsLabel, FontAwesomeIcon.Envelope),
            new((int)SocialSubSection.IndividualPairs, Loc.Get("CompactUi.Sidebar.IndividualPairs"), FontAwesomeIcon.User),
            new((int)SocialSubSection.Syncshells, Loc.Get("CompactUi.Sidebar.Syncshells"), FontAwesomeIcon.UserFriends),
            new((int)SocialSubSection.Profiles, Loc.Get("CharaDataHub.Tab.Profiles"), FontAwesomeIcon.AddressBook),
            SideRailEntry.Group(Loc.Get("CompactUi.Social.Nav.Group.Discover")),
            new((int)SocialSubSection.Nearby, Loc.Get("AutoDetectUi.Tab.Nearby"), FontAwesomeIcon.MapMarkerAlt),
            new((int)SocialSubSection.SyncFinder, Loc.Get("AutoDetectUi.Tab.SyncFinder"), FontAwesomeIcon.Search),
            SideRailEntry.Group(Loc.Get("CompactUi.Social.Nav.Directory")),
            new((int)SocialSubSection.DirectoryMine, Loc.Get("Establishment.Directory.Tab.Mine"), FontAwesomeIcon.Home),
            new((int)SocialSubSection.DirectoryFavorites, Loc.Get("Establishment.Directory.Tab.Favorites"), FontAwesomeIcon.Star),
            new((int)SocialSubSection.DirectoryBrowse, Loc.Get("Establishment.Directory.Tab.Browse"), FontAwesomeIcon.Globe),
            new((int)SocialSubSection.DirectoryUpcoming, Loc.Get("Establishment.Directory.Tab.Upcoming"), FontAwesomeIcon.CalendarAlt),
            new((int)SocialSubSection.DirectoryWildRp, Loc.Get("WildRp.Tab.Title"), FontAwesomeIcon.Compass),
        ];
    }

    private void DrawUnsupportedVersionBanner()
    {
        var ver = _apiController.CurrentClientVersion;
        var unsupported = Loc.Get("CompactUi.UnsupportedVersion.Title");
        using (_uiSharedService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(unsupported);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(UiSharedService.AccentColor, unsupported);
        }

        var revision = ver.Revision >= 0 ? ver.Revision : 0;
        var version = $"{ver.Major}.{ver.Minor}.{ver.Build}.{revision}";
        UiSharedService.ColorTextWrapped(
            string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.UnsupportedVersion.Message"), version),
            UiSharedService.AccentColor);
    }

    private bool IsConnecting => _apiController.ServerState is ServerState.Connecting or ServerState.Reconnecting;

    private sealed record StatusAction(string Label, Action Run, string? Tooltip = null, bool Primary = false);

    private sealed record StatusContent(FontAwesomeIcon Icon, string Title, string? Body,
        StatusAction? Action = null, bool Spinner = false, bool Warning = false);

    private StatusContent GetStatusContent()
    {
        bool hasServer = _serverManager.HasServers;
        string serverName = hasServer ? _serverManager.CurrentServer.ServerName : string.Empty;
        var retry = new StatusAction(Loc.Get("CompactUi.Status.Retry"), RetryConnection);

        switch (_apiController.ServerState)
        {
            case ServerState.Connecting:
            case ServerState.Reconnecting:
                // L'annulation reste visible pendant toute la boucle de tentatives, qui alterne entre les deux états.
                string key = _apiController.ServerState is ServerState.Reconnecting ? "CompactUi.Connecting.Reconnect" : "CompactUi.Connecting.Title";
                return new(FontAwesomeIcon.Link, Loc.Get(key, serverName), null,
                    hasServer ? new StatusAction(Loc.Get("CompactUi.Connecting.Cancel"), ToggleConnection, Loc.Get("CompactUi.Connecting.Cancel.Tooltip")) : null,
                    Spinner: true);
            case ServerState.Disconnecting:
                return new(FontAwesomeIcon.Unlink, Loc.Get("CompactUi.Status.Disconnecting.Title", serverName), null, Spinner: true);
            case ServerState.Disconnected:
                string title = Loc.Get("CompactUi.Status.Disconnected.Title", serverName);
                if (!_configService.Current.HasValidRgpdConsent())
                    return new(FontAwesomeIcon.UserCheck, title, Loc.Get("CompactUi.Status.Disconnected.Consent"),
                        new StatusAction(Loc.Get("CompactUi.Status.Consent"), () => Mediator.Publish(new SwitchToRgpdConsentUiMessage()), Primary: true));
                if (hasServer && _serverManager.CurrentServer.FullPause)
                    return new(FontAwesomeIcon.PauseCircle, title, Loc.Get("CompactUi.Status.Disconnected.Paused"),
                        new StatusAction(Loc.Get("CompactUi.Status.Connect"), ToggleConnection, Primary: true));
                return new(FontAwesomeIcon.Unlink, title, Loc.Get("CompactUi.Status.Disconnected.Interrupted"),
                    hasServer ? retry with { Primary = true } : null);
            case ServerState.Offline when !_dalamudUtilService.IsLoggedIn:
                return new(FontAwesomeIcon.UserSlash, Loc.Get("CompactUi.Status.LoggedOut.Title"), Loc.Get("CompactUi.Status.LoggedOut.Body"));
            case ServerState.Offline:
                return new(FontAwesomeIcon.Server, Loc.Get("CompactUi.Status.Offline.Title"), GetServerError(), hasServer ? retry : null);
            case ServerState.Unauthorized:
                string reason = string.IsNullOrWhiteSpace(_apiController.AuthFailureMessage)
                    ? Loc.Get("CompactUi.Status.Unauthorized.Body")
                    : GetServerError();
                return new(FontAwesomeIcon.Lock, Loc.Get("CompactUi.Status.Unauthorized.Title"), reason, hasServer ? retry : null, Warning: true);
            case ServerState.VersionMisMatch:
                return new(FontAwesomeIcon.ArrowCircleUp, Loc.Get("CompactUi.Status.VersionMismatch.Title"), GetServerError(), Warning: true);
            case ServerState.RateLimited:
                return new(FontAwesomeIcon.HourglassHalf, Loc.Get("CompactUi.Status.RateLimited.Title"), GetServerError(), Warning: true);
            case ServerState.MultiChara:
                return new(FontAwesomeIcon.UserFriends, Loc.Get("CompactUi.Status.MultiChara.Title"), GetServerError(), Warning: true);
            default:
                return new(FontAwesomeIcon.Unlink, GetServerError(), null);
        }
    }

    private void RetryConnection() => _ = RetryConnectionAsync();

    private async Task RetryConnectionAsync()
    {
        try
        {
            await _apiController.CreateConnections().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual reconnection attempt failed");
        }
    }

    // Écran d'état centré, à la manière des écrans vides d'Apple : un symbole (ou un indicateur en mouvement),
    // un titre bref, une explication en couleur secondaire, et l'action utile quand il y en a une.
    private void DrawStatusPanel(StatusContent content)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail();
        float startX = ImGui.GetCursorPosX();
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float wrapWidth = MathF.Max(80f * scale, MathF.Min(avail.X - (24f * scale), 290f * scale));
        float symbolBlock = 64f * scale;
        bool slow = content.Spinner && _connectingSinceUtc is { } since && DateTime.UtcNow - since >= TimeSpan.FromSeconds(20);
        string? body = slow ? Loc.Get("CompactUi.Connecting.Slow") : content.Body;
        List<string> bodyLines = string.IsNullOrEmpty(body) ? [] : WrapLines(body, wrapWidth);
        float bodyHeight = MathF.Max(content.Spinner ? 3 : 0, bodyLines.Count) * lineHeight;
        float actionHeight = content.Action != null ? ImGui.GetFrameHeightWithSpacing() + (10f * scale) : 0f;

        float blockHeight = symbolBlock + (lineHeight * 1.4f) + (4f * scale) + bodyHeight + actionHeight;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (avail.Y - blockHeight) * 0.42f));

        var symbolCenter = ImGui.GetCursorScreenPos() + new Vector2(avail.X / 2f, (symbolBlock / 2f) - (6f * scale));
        if (content.Spinner)
        {
            UiSharedService.DrawSpinner(symbolCenter, 16f * scale, UiSharedService.AccentColor);
        }
        else
        {
            string icon = content.Icon.ToIconString();
            var iconColor = content.Warning ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey3;
            using (_uiSharedService.LargeIconFont.Push())
            {
                var iconSize = ImGui.CalcTextSize(icon);
                ImGui.GetWindowDrawList().AddText(symbolCenter - (iconSize / 2f), ImGui.GetColorU32(iconColor), icon);
            }
        }
        ImGui.Dummy(new Vector2(avail.X, symbolBlock));

        using (UiSharedService.PushFontScale(1.1f))
            DrawCenteredText(content.Title, UiSharedService.ThemeNavTextActive, startX, avail.X);
        ImGuiHelpers.ScaledDummy(4f);

        float bodyTop = ImGui.GetCursorPosY();
        foreach (var line in bodyLines)
            DrawCenteredText(line, ImGuiColors.DalamudGrey, startX, avail.X);
        ImGui.SetCursorPosY(bodyTop + bodyHeight);

        if (content.Action is { } action)
        {
            ImGuiHelpers.ScaledDummy(10f);
            var style = ImGui.GetStyle();
            float buttonWidth = MathF.Max(120f * scale, ImGui.CalcTextSize(action.Label).X + (style.FramePadding.X * 4f));
            ImGui.SetCursorPosX(startX + MathF.Max(0f, (avail.X - buttonWidth) / 2f));
            var accent = UiSharedService.AccentColor;
            using (ImRaii.PushColor(ImGuiCol.Button, accent, action.Primary)
                       .Push(ImGuiCol.ButtonHovered, Vector4.Lerp(accent, UiSharedService.ThemeNavTextActive, 0.15f), action.Primary)
                       .Push(ImGuiCol.ButtonActive, Vector4.Lerp(accent, UiSharedService.ThemeWindowBg, 0.2f), action.Primary))
            {
                if (ImGui.Button(action.Label, new Vector2(buttonWidth, 0f)))
                    action.Run();
            }
            if (action.Tooltip != null)
                UiSharedService.AttachToolTip(action.Tooltip);
        }
    }

    // Ligne d'état en tête des sections utilisables sans connexion (profil, réglages…).
    private void DrawStatusInline(StatusContent content)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (content.Spinner)
        {
            float radius = 6f * scale;
            float lineHeight = ImGui.GetTextLineHeight();
            UiSharedService.DrawSpinner(ImGui.GetCursorScreenPos() + new Vector2(radius + scale, lineHeight / 2f), radius, UiSharedService.AccentColor);
            ImGui.Dummy(new Vector2((radius * 2f) + (2f * scale), lineHeight));
        }
        else
        {
            _uiSharedService.IconText(content.Icon, content.Warning ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey3);
        }
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, content.Title);
        if (content.Body != null)
            UiSharedService.AttachToolTip(content.Body);

        if (content.Action is { } action)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(action.Label))
                action.Run();
            if (action.Tooltip != null)
                UiSharedService.AttachToolTip(action.Tooltip);
        }
    }

    // ImGui ne centre pas un texte renvoyé à la ligne : on découpe soi-même pour centrer chaque ligne.
    private static List<string> WrapLines(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            string current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    private static void DrawCenteredText(string text, Vector4 color, float startX, float width)
    {
        ImGui.SetCursorPosX(startX + MathF.Max(0f, (width - ImGui.CalcTextSize(text).X) / 2f));
        ImGui.TextColored(color, text);
    }

    private static bool RequiresServerConnection(CompactUiSection section)
    {
        return section is CompactUiSection.Notifications
            or CompactUiSection.Social
            or CompactUiSection.CharacterAnalysis
            or CompactUiSection.CharacterDataHub;
    }

    private void UiSharedService_GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }
}