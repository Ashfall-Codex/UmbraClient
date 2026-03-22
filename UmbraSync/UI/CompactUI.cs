using Dalamud.Bindings.ImGui;
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
    private readonly EditProfileUi _editProfileUi;
    private readonly SettingsUi _settingsUi;
    private readonly AutoDetectUi _autoDetectUi;
    private readonly DataAnalysisUi _dataAnalysisUi;
    private readonly CharaDataHubUi _charaDataHubUi;
    private readonly NotificationTracker _notificationTracker;
    private readonly EstablishmentConfigService _establishmentConfigService;
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
        AutoDetect,
        CharacterAnalysis,
        CharacterDataHub,
        EditProfile,
        Settings
    }

    private enum SocialSubSection
    {
        IndividualPairs,
        Syncshells,
        Annuaire
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
        EstablishmentConfigService establishmentConfigService)
        : base(logger, mediator, "###UmbraSyncMainUI", performanceCollectorService)
    {
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

        _groupPanel = new(this, uiShared, _pairManager, uidDisplayHandler, _serverManager, _charaDataManager, _autoDetectRequestService, _configService, syncshellConfig);
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

        using var fontScale = UiSharedService.PushFontScale(ContentFontScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding * ContentFontScale);

        ImGui.BeginChild("compact-sidebar", new Vector2(sidebarWidth, 0), false, ImGuiWindowFlags.NoScrollbar);
        DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        float separatorHeight = ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y * 2f;
        float separatorX = ImGui.GetCursorPosX();
        float separatorY = ImGui.GetCursorPosY();
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var end = new Vector2(start.X, start.Y + separatorHeight);
        var separatorColor = UiSharedService.AccentColor with { W = 0.6f };
        drawList.AddLine(start, end, ImGui.GetColorU32(separatorColor), 1f * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPos(new Vector2(separatorX + 6f * ImGuiHelpers.GlobalScale, separatorY));

        ImGui.BeginChild("compact-content", Vector2.Zero, false);
        WindowContentWidth = UiSharedService.GetWindowContentRegionWidth();

        if (!_apiController.IsCurrentVersion)
        {
            DrawUnsupportedVersionBanner();
            ImGui.Separator();
        }

        if (_apiController.ServerState is not ServerState.Connected)
        {
            UiSharedService.ColorTextWrapped(GetServerError(), GetUidColor());
            if (_apiController.ServerState is ServerState.NoSecretKey)
            {
                DrawAddCharacter();
            }
            DrawAccentSeparator();
        }

        DrawMainContent();

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
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.General.ConnectToServerNotice"), ImGuiColors.DalamudGrey3);
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
            case CompactUiSection.AutoDetect:
                DrawAutoDetectSection();
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
        // Dessiner Nearby juste SOUS la recherche GID/Alias dans la section Syncshell
        var nearbyEntriesForDisplay = _configService.Current.EnableAutoDetectDiscovery
            ? GetNearbyEntriesForDisplay()
            : [];

        using (ImRaii.PushId("syncshells"))
            _groupPanel.DrawSyncshells(drawAfterAdd: () =>
            {
                if (nearbyEntriesForDisplay.Count > 0)
                {
                    using (ImRaii.PushId("syncshell-nearby")) DrawNearbyCard(nearbyEntriesForDisplay);
                }
            });
        using (ImRaii.PushId("transfers")) DrawTransfers();
        TransferPartHeight = ImGui.GetCursorPosY() - TransferPartHeight;
        using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
        using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
    }

    private void DrawSocialSection()
    {
        DrawDefaultSyncSettings();
        ImGuiHelpers.ScaledDummy(2f);
        DrawSocialSwitchButtons();
        ImGuiHelpers.ScaledDummy(2f);
        using var socialBody = ImRaii.Child(
            "social-body",
            new Vector2(0, 0),
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        switch (_socialSubSection)
        {
            case SocialSubSection.IndividualPairs:
                DrawPairSectionBody();
                break;
            case SocialSubSection.Syncshells:
                DrawSyncshellSection();
                break;
            case SocialSubSection.Annuaire:
                DrawAnnuaireSection();
                break;
        }
    }

    private static readonly SocialSubSection[] SocialSubSections =
        [SocialSubSection.IndividualPairs, SocialSubSection.Syncshells, SocialSubSection.Annuaire];

    private void DrawSocialSwitchButtons()
    {
        var individualLabel = Loc.Get("CompactUi.Sidebar.IndividualPairs");
        var syncshellLabel = Loc.Get("CompactUi.Sidebar.Syncshells");
        var annuaireLabel = "Annuaire";
        var icons = new[] { FontAwesomeIcon.User, FontAwesomeIcon.UserFriends, FontAwesomeIcon.Book };
        var labels = new[] { individualLabel, syncshellLabel, annuaireLabel };

        const float btnH = 32f;
        const float btnSpacing = 8f;
        const float rounding = 4f;
        const float iconTextGap = 6f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var btnW = (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length;

        var accent = UiSharedService.AccentColor;
        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##socialTab_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool isActive = _socialSubSection == SocialSubSections[i];

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            // Measure icon
            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSz = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var labelSz = ImGui.CalcTextSize(labels[i]);
            var totalW = iconSz.X + iconTextGap + labelSz.X;
            var startX = p.X + (btnW - totalW) / 2f;

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            // Draw icon
            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(startX, p.Y + (btnH - iconSz.Y) / 2f), textColorU32, iconStr);
            ImGui.PopFont();

            // Draw label
            dl.AddText(new Vector2(startX + iconSz.X + iconTextGap, p.Y + (btnH - labelSz.Y) / 2f), textColorU32, labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked)
            {
                _socialSubSection = SocialSubSections[i];
            }
        }
    }

    private void DrawAutoDetectSection()
    {
        using (ImRaii.PushId("autodetect-inline")) _autoDetectUi.DrawInline();
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

    private static bool RequiresServerConnection(CompactUiSection section)
    {
        return section is CompactUiSection.Notifications
            or CompactUiSection.Social
            or CompactUiSection.AutoDetect
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