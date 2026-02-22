using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using OtterGui.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.User;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
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
using OtterGuiImGuiClip = OtterGui.ImGuiClip;

namespace UmbraSync.UI;

public class CompactUi : WindowMediatorSubscriberBase
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
    private readonly Stopwatch _timeout = new();
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
    private bool _buttonState;
    private string _characterOrCommentFilter = string.Empty;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private SocialSubSection _socialSubSection = SocialSubSection.IndividualPairs;
    private Vector2 _lastPosition = Vector2.One;
    private Vector2 _lastSize = Vector2.One;
    private string _pairToAdd = string.Empty;
    private int _secretKeyIdx = -1;
    private bool _showModalForUserAddition;
    private bool _wasOpen;
    private bool _nearbyOpen = true;
    private bool _selfAnalysisOpen = false;
    private List<Services.Mediator.NearbyEntry> _nearbyEntries = new();
    private int _notificationCount;
    private const long SelfAnalysisSizeWarningThreshold = 300L * 1024 * 1024;
    private const long SelfAnalysisTriangleWarningThreshold = 150_000;
    private CompactUiSection _activeSection = CompactUiSection.Social;
    private const float SidebarWidth = 53f;
    private const float SidebarIconSize = 25f;
    private const float ContentFontScale = UiSharedService.ContentFontScale;
    private static readonly Vector4 SidebarButtonColor = new(0f, 0f, 0f, 0f);
    private static readonly Vector4 SidebarButtonHoverColor = new(0x30 / 255f, 0x19 / 255f, 0x46 / 255f, 1f);
    private static readonly Vector4 SidebarButtonActiveColor = new(0x50 / 255f, 0x17 / 255f, 0x83 / 255f, 1f);
    private static readonly Vector4 MutedCardBackground = new(0.10f, 0.10f, 0.13f, 0.78f);
    private static readonly Vector4 MutedCardBorder = new(0.55f, 0.55f, 0.62f, 0.82f);
    private const float SidebarIndicatorAnimSpeed = 18f;
    private readonly Dictionary<CompactUiSection, (Vector2 Min, Vector2 Max)> _sidebarButtonRects = new();
    private Vector2 _sidebarIndicatorPos;
    private Vector2 _sidebarIndicatorSize;
    private bool _sidebarIndicatorInitialized;
    private Vector2 _sidebarWindowPos;

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
        Syncshells
    }

    private readonly Dictionary<string, DrawUserPair> _drawUserPairCache = new(StringComparer.Ordinal);

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
        UmbraProfileManager umbraProfileManager)
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
        var tagHandler = new TagHandler(_serverManager);

        _groupPanel = new(this, uiShared, _pairManager, uidDisplayHandler, _serverManager, _charaDataManager, _autoDetectRequestService, _configService, umbraProfileManager);
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

    private void DrawDefaultSyncSettings()
    {
        ImGuiHelpers.ScaledDummy(3f);
        using (ImRaii.PushId("sync-defaults"))
        {
            var soundLabel = Loc.Get("CompactUi.SyncDefaults.AudioLabel");
            var animLabel = Loc.Get("CompactUi.SyncDefaults.AnimationLabel");
            var vfxLabel = Loc.Get("CompactUi.SyncDefaults.VfxLabel");
            var soundSubject = Loc.Get("CompactUi.SyncDefaults.AudioSubject");
            var animSubject = Loc.Get("CompactUi.SyncDefaults.AnimationSubject");
            var vfxSubject = Loc.Get("CompactUi.SyncDefaults.VfxSubject");

            bool soundsDisabled = _configService.Current.DefaultDisableSounds;
            bool animsDisabled = _configService.Current.DefaultDisableAnimations;
            bool vfxDisabled = _configService.Current.DefaultDisableVfx;
            bool showNearby = _configService.Current.EnableAutoDetectDiscovery;
            int pendingInvites = _nearbyPending.Pending.Count;

            var soundIcon = soundsDisabled ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp;
            var animIcon = animsDisabled ? FontAwesomeIcon.WindowClose : FontAwesomeIcon.Running;
            var vfxIcon = vfxDisabled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.Sun;

            var extraPadding = new Vector2(6f, 4f) * ImGuiHelpers.GlobalScale;
            var originalPadding = ImGui.GetStyle().FramePadding;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, originalPadding + extraPadding);

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float audioWidth = _uiSharedService.GetIconTextButtonSize(soundIcon, soundLabel);
            float animWidth = _uiSharedService.GetIconTextButtonSize(animIcon, animLabel);
            float vfxWidth = _uiSharedService.GetIconTextButtonSize(vfxIcon, vfxLabel);
            float totalWidth = audioWidth + animWidth + vfxWidth + spacing * 2f;
            float available = ImGui.GetContentRegionAvail().X;
            float startCursorX = ImGui.GetCursorPosX();
            if (totalWidth < available)
            {
                ImGui.SetCursorPosX(startCursorX + (available - totalWidth) / 2f);
            }

            DrawDefaultSyncButton(soundIcon, soundLabel, audioWidth, soundsDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableSounds = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(soundSubject, state));
                },
                () => DisableStateTooltip(soundSubject, _configService.Current.DefaultDisableSounds));

            DrawDefaultSyncButton(animIcon, animLabel, animWidth, animsDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableAnimations = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(animSubject, state));
                },
                () => DisableStateTooltip(animSubject, _configService.Current.DefaultDisableAnimations), spacing);

            DrawDefaultSyncButton(vfxIcon, vfxLabel, vfxWidth, vfxDisabled,
                state =>
                {
                    _configService.Current.DefaultDisableVfx = state;
                    _configService.Save();
                    Mediator.Publish(new ApplyDefaultsToAllSyncsMessage(vfxSubject, state));
                },
                () => DisableStateTooltip(vfxSubject, _configService.Current.DefaultDisableVfx), spacing);

            ImGui.PopStyleVar();

            if (showNearby && pendingInvites > 0)
            {
                ImGuiHelpers.ScaledDummy(3f);
                UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SyncDefaults.AutoDetectPending"), pendingInvites), ImGuiColors.DalamudYellow);
            }

            DrawSelfAnalysisPreview();
        }
    }

    private void DrawSelfAnalysisPreview()
    {
        using (ImRaii.PushId("self-analysis"))
        {
            var headerSummary = _characterAnalyzer.CurrentSummary;
            bool highlightWarning = !headerSummary.IsEmpty
                                     && !headerSummary.HasUncomputedEntries
                                     && headerSummary.TotalCompressedSize >= SelfAnalysisSizeWarningThreshold;

            Vector4? cardBg = null;
            Vector4? cardBorder = null;
            if (highlightWarning)
            {
                var y = ImGuiColors.DalamudYellow;
                cardBg = new Vector4(y.X, y.Y, y.Z, 0.12f);
                cardBorder = new Vector4(y.X, y.Y, y.Z, 0.75f);
            }

            UiSharedService.DrawCard("self-analysis-card", () =>
            {
                bool arrowState = _selfAnalysisOpen;
                UiSharedService.DrawArrowToggle(ref arrowState, "##self-analysis-toggle");
                _selfAnalysisOpen = arrowState;

                ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("CompactUi.SelfAnalysis.Header"));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _selfAnalysisOpen = !_selfAnalysisOpen;
                }

                if (!_selfAnalysisOpen)
                {
                    return;
                }

                ImGuiHelpers.ScaledDummy(4f);

                var summary = _characterAnalyzer.CurrentSummary;
                bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;

                if (isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(
                        string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SelfAnalysis.AnalyzingStatus"), _characterAnalyzer.CurrentFile, System.Math.Max(_characterAnalyzer.TotalFiles, 1)),
                        ImGuiColors.DalamudYellow);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, Loc.Get("CompactUi.SelfAnalysis.CancelButton")))
                    {
                        _characterAnalyzer.CancelAnalyze();
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CompactUi.SelfAnalysis.CancelTooltip"));
                }
                else
                {
                    bool recalculate = !summary.HasUncomputedEntries && !summary.IsEmpty;
                    var label = Loc.Get(recalculate ? "CompactUi.SelfAnalysis.RecalculateButton" : "CompactUi.SelfAnalysis.StartButton");
                    var icon = recalculate ? FontAwesomeIcon.Sync : FontAwesomeIcon.PlayCircle;
                    if (_uiSharedService.IconTextButton(icon, label))
                    {
                        _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: recalculate);
                    }
                    UiSharedService.AttachToolTip(recalculate
                        ? Loc.Get("CompactUi.SelfAnalysis.RecalculateTooltip")
                        : Loc.Get("CompactUi.SelfAnalysis.StartTooltip"));
                }

                if (summary.IsEmpty && !isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.SelfAnalysis.NoData"),
                        ImGuiColors.DalamudGrey2);
                    return;
                }

                if (summary.HasUncomputedEntries && !isAnalyzing)
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.SelfAnalysis.UncomputedWarning"),
                        ImGuiColors.DalamudYellow);
                }

                ImGuiHelpers.ScaledDummy(3f);

                UiSharedService.DrawGrouped(() =>
                {
                    using var table = ImUtf8.Table("self-analysis-stats", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings);
                    if (table)
                    {
                        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch, 0.55f);
                        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.45f);

                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.Files"), summary.TotalFiles.ToString("N0", CultureInfo.CurrentCulture));

                        var compressedValue = UiSharedService.ByteToString(summary.TotalCompressedSize);
                        Vector4? compressedColor = null;
                        FontAwesomeIcon? compressedIcon = null;
                        Vector4? compressedIconColor = null;
                        string? compressedTooltip = null;
                        if (summary.HasUncomputedEntries)
                        {
                            compressedColor = ImGuiColors.DalamudYellow;
                            compressedTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.ComputeSizes");
                        }
                        else if (summary.TotalCompressedSize >= SelfAnalysisSizeWarningThreshold)
                        {
                            compressedColor = ImGuiColors.DalamudYellow;
                            compressedTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.SizeWarning");
                            compressedIcon = FontAwesomeIcon.ExclamationTriangle;
                            compressedIconColor = ImGuiColors.DalamudYellow;
                        }

                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.CompressedSize"), compressedValue, compressedColor, compressedTooltip, compressedIcon, compressedIconColor);
                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.ExtractedSize"), UiSharedService.ByteToString(summary.TotalOriginalSize));

                        Vector4? trianglesColor = null;
                        FontAwesomeIcon? trianglesIcon = null;
                        Vector4? trianglesIconColor = null;
                        string? trianglesTooltip = null;
                        if (summary.TotalTriangles >= SelfAnalysisTriangleWarningThreshold)
                        {
                            trianglesColor = ImGuiColors.DalamudYellow;
                            trianglesTooltip = Loc.Get("CompactUi.SelfAnalysis.Tooltip.TriangleWarning");
                            trianglesIcon = FontAwesomeIcon.ExclamationTriangle;
                            trianglesIconColor = ImGuiColors.DalamudYellow;
                        }
                        DrawSelfAnalysisStatRow(Loc.Get("CompactUi.SelfAnalysis.Stat.Triangles"), UiSharedService.TrisToString(summary.TotalTriangles), trianglesColor, trianglesTooltip, trianglesIcon, trianglesIconColor);
                    }
                }, rounding: 4f, expectedWidth: ImGui.GetContentRegionAvail().X, drawBorder: false);

                string lastAnalysisText;
                Vector4 lastAnalysisColor = ImGuiColors.DalamudGrey2;
                if (isAnalyzing)
                {
                    lastAnalysisText = Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.InProgress");
                    lastAnalysisColor = ImGuiColors.DalamudYellow;
                }
                else if (_characterAnalyzer.LastCompletedAnalysis.HasValue)
                {
                    var localTime = _characterAnalyzer.LastCompletedAnalysis.Value.ToLocalTime();
                    lastAnalysisText = string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.At"), localTime.ToString("g", CultureInfo.CurrentCulture));
                }
                else
                {
                    lastAnalysisText = Loc.Get("CompactUi.SelfAnalysis.LastAnalysis.Never");
                }

                ImGuiHelpers.ScaledDummy(2f);
                UiSharedService.ColorTextWrapped(lastAnalysisText, lastAnalysisColor);

                ImGuiHelpers.ScaledDummy(3f);

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("CompactUi.SelfAnalysis.OpenDetailsButton")))
                {
                    Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
                }
            }, background: cardBg, border: cardBorder, stretchWidth: true);
        }
    }

    private static void DrawSelfAnalysisStatRow(string label, string value, Vector4? valueColor = null, string? tooltip = null, FontAwesomeIcon? icon = null, Vector4? iconColor = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        if (icon.HasValue)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (iconColor.HasValue)
                {
                    using var iconColorPush = ImRaii.PushColor(ImGuiCol.Text, iconColor.Value);
                    ImGui.TextUnformatted(icon.Value.ToIconString());
                }
                else
                {
                    ImGui.TextUnformatted(icon.Value.ToIconString());
                }
            }
            ImGui.SameLine(0f, 4f);
        }

        if (valueColor.HasValue)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, valueColor.Value);
            ImGui.TextUnformatted(value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            UiSharedService.AttachToolTip(tooltip);
        }
    }

    private void DrawDefaultSyncButton(FontAwesomeIcon icon, string label, float width, bool currentState,
        Action<bool> onToggle, Func<string> tooltipProvider, float spacingOverride = -1f)
    {
        if (spacingOverride >= 0f)
        {
            ImGui.SameLine(0, spacingOverride);
        }

        var colorsPushed = 0;
        if (currentState)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.35f, 1f));
            colorsPushed++;
        }

        if (_uiSharedService.IconTextButton(icon, label, width))
        {
            var newState = !currentState;
            onToggle(newState);
        }

        if (colorsPushed > 0)
        {
            ImGui.PopStyleColor(colorsPushed);
        }

        UiSharedService.AttachToolTip(tooltipProvider());
    }

    private static string DisableStateTooltip(string context, bool disabled)
    {
        var state = Loc.Get(disabled ? "CompactUi.SyncDefaults.State.Disabled" : "CompactUi.SyncDefaults.State.Enabled");
        return string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.SyncDefaults.Tooltip"), context, state);
    }

    private void DrawAddCharacter()
    {
        ImGui.Dummy(new(10));
        var keys = _serverManager.CurrentServer!.SecretKeys;
        if (keys.Count > 0)
        {
            if (_secretKeyIdx == -1) _secretKeyIdx = keys.First().Key;
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("CompactUi.AddCharacter.AddCurrentWithKey")))
            {
                _serverManager.CurrentServer!.Authentications.Add(new MareConfiguration.Models.Authentication()
                {
                    CharacterName = _uiSharedService.PlayerName,
                    WorldId = _uiSharedService.WorldId,
                    SecretKeyIdx = _secretKeyIdx
                });

                _serverManager.Save();

                _ = _apiController.CreateConnections();
            }

            var secretKeyLabel = $"{Loc.Get("CompactUi.AddCharacter.SecretKeyLabel")}##addCharacterSecretKey";
            _uiSharedService.DrawCombo(secretKeyLabel, keys, (f) => f.Value.FriendlyName, (f) => _secretKeyIdx = f.Key);
        }
        else
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.AddCharacter.NoSecretKeys"), ImGuiColors.DalamudYellow);
        }
    }

    private void DrawAddPair()
    {
        var style = ImGui.GetStyle();
        float buttonHeight = ImGui.GetFrameHeight() + style.FramePadding.Y * 0.5f;
        float glyphWidth;
        using (_uiSharedService.IconFont.Push())
            glyphWidth = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X;
        var buttonWidth = glyphWidth + style.FramePadding.X * 2f;

        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(MathF.Max(0, availWidth - buttonWidth - style.ItemSpacing.X));
        ImGui.InputTextWithHint("##otheruid", Loc.Get("CompactUi.AddPair.OtherUidPlaceholder"), ref _pairToAdd, 20);
        ImGui.SameLine();
        var canAdd = !_pairManager.DirectPairs.Exists(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (_uiSharedService.IconPlusButtonCentered(height: buttonHeight))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            var target = _pairToAdd.IsNullOrEmpty() ? Loc.Get("CompactUi.AddPair.OtherUserFallback") : _pairToAdd;
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.AddPair.PairWithFormat"), target));
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawFilter()
    {
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);

        var users = GetFilteredUsers();
        var userCount = users.Count;

        var spacing = userCount > 0
            ? playButtonSize.X + ImGui.GetStyle().ItemSpacing.X
            : 0;

        ImGui.SetNextItemWidth(WindowContentWidth - spacing);
        ImGui.InputTextWithHint("##filter", Loc.Get("CompactUi.Filter.Placeholder"), ref _characterOrCommentFilter, 255);

        if (userCount == 0) return;

        var pausedUsers = users.Where(u => u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();
        var resumedUsers = users.Where(u => !u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();

        if (pausedUsers.Count == 0 && resumedUsers.Count == 0) return;
        ImGui.SameLine();

        switch (_buttonState)
        {
            case true when pausedUsers.Count == 0:
                _buttonState = false;
                break;

            case false when resumedUsers.Count == 0:
                _buttonState = true;
                break;

            case true:
                users = pausedUsers;
                break;

            case false:
                users = resumedUsers;
                break;
        }

        if (_timeout.ElapsedMilliseconds > 5000)
            _timeout.Reset();

        var button = _buttonState ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;

        using (ImRaii.Disabled(_timeout.IsRunning))
        {
            bool clicked = button == FontAwesomeIcon.Pause
                ? _uiSharedService.IconPauseButtonCentered(playButtonSize.Y)
                : _uiSharedService.IconButtonCentered(button, playButtonSize.Y);
            if (clicked && UiSharedService.CtrlPressed())
            {
                foreach (var entry in users)
                {
                    var perm = entry.UserPair!.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, perm));
                }

                _timeout.Start();
                _buttonState = !_buttonState;
            }
            if (!_timeout.IsRunning)
            {
                var action = button == FontAwesomeIcon.Play ? Loc.Get("CompactUi.Pairs.ResumeAction") : Loc.Get("CompactUi.Pairs.PauseAction");
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Pairs.MultiToggleTooltip"), action, users.Count, userCount));
            }
            else
            {
                var secondsRemaining = (5000 - _timeout.ElapsedMilliseconds) / 1000;
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Pairs.NextExecutionTooltip"), secondsRemaining));
            }
        }
    }

    private void DrawPairList()
    {
        using (ImRaii.PushId("addpair")) DrawAddPair();

        using (ImRaii.PushId("pairs")) DrawPairs();
        TransferPartHeight = ImGui.GetCursorPosY();
        using (ImRaii.PushId("filter")) DrawFilter();
    }

    private void DrawPairs()
    {
        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float ySize;
        if (TransferPartHeight <= 0)
        {
            float reserve = ImGui.GetFrameHeightWithSpacing() * 2f;
            ySize = availableHeight - reserve;
            if (ySize <= 0)
            {
                ySize = System.Math.Max(availableHeight, 1f);
            }
        }
        else
        {
            ySize = (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y) - TransferPartHeight - ImGui.GetCursorPosY();
        }
        var allUsers = GetFilteredUsers().OrderBy(u => u.GetPairSortKey(), StringComparer.Ordinal).ToList();
        var visibleUsersSource = allUsers.Where(u => u.IsVisible).ToList();
        var nonVisibleUsers = allUsers.Where(u => !u.IsVisible).ToList();
        var nearbyEntriesForDisplay = _configService.Current.EnableAutoDetectDiscovery
            ? GetNearbyEntriesForDisplay()
            : [];

        ImGui.BeginChild("list", new Vector2(WindowContentWidth, ySize), border: false);

        var pendingCount = _nearbyPending.Pending.Count;
        if (pendingCount > 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CompactUi.AutoDetect.PendingInvitation"), ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(4);
        }

        var visibleUsers = visibleUsersSource.Select(c =>
        {
            var cacheKey = "Visible" + c.UserData.UID;
            if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
            {
                drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager);
                _drawUserPairCache[cacheKey] = drawPair;
            }
            else
            {
                drawPair.UpdateData();
            }
            return drawPair;
        }).ToList();

        var onlineUsers = nonVisibleUsers.Where(u => u.UserPair!.OtherPermissions.IsPaired() && (u.IsOnline || u.UserPair!.OwnPermissions.IsPaused()))
            .Select(c =>
            {
                var cacheKey = "Online" + c.UserData.UID;
                if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
                {
                    drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager);
                    _drawUserPairCache[cacheKey] = drawPair;
                }
                else
                {
                    drawPair.UpdateData();
                }
                return drawPair;
            }).ToList();

        var offlineUsers = nonVisibleUsers.Where(u => !u.UserPair!.OtherPermissions.IsPaired() || (!u.IsOnline && !u.UserPair!.OwnPermissions.IsPaused()))
            .Select(c =>
            {
                var cacheKey = "Offline" + c.UserData.UID;
                if (!_drawUserPairCache.TryGetValue(cacheKey, out var drawPair))
                {
                    drawPair = new DrawUserPair(cacheKey, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _serverManager);
                    _drawUserPairCache[cacheKey] = drawPair;
                }
                else
                {
                    drawPair.UpdateData();
                }
                return drawPair;
            }).ToList();

        Action? drawVisibleExtras = null;
        if (nearbyEntriesForDisplay.Count > 0)
        {
            var entriesForExtras = nearbyEntriesForDisplay;
            drawVisibleExtras = () => DrawNearbyCard(entriesForExtras);
        }

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, offlineUsers, drawVisibleExtras);

        ImGui.EndChild();
    }

    private List<Services.Mediator.NearbyEntry> GetNearbyEntriesForDisplay()
    {
        if (_nearbyEntries.Count == 0)
        {
            return [];
        }

        return _nearbyEntries
            .Where(e => e.IsMatch && e.AcceptPairRequests && !string.IsNullOrEmpty(e.Token) && !IsAlreadyPairedQuickMenu(e))
            .OrderBy(e => e.Distance)
            .ToList();
    }

    private void DrawNearbyCard(IReadOnlyList<Services.Mediator.NearbyEntry> nearbyEntries)
    {
        if (nearbyEntries.Count == 0)
        {
            return;
        }

        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushId("group-Nearby"))
        {
            UiSharedService.DrawCard("nearby-card", () =>
            {
                bool nearbyState = _nearbyOpen;
                UiSharedService.DrawArrowToggle(ref nearbyState, "##nearby-toggle");
                _nearbyOpen = nearbyState;

                ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
                var onUmbra = nearbyEntries.Count;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Nearby.Header"), onUmbra));
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _nearbyOpen = !_nearbyOpen;
                }

                if (!_nearbyOpen)
                {
                    return;
                }

                ImGuiHelpers.ScaledDummy(4f);
                var indent = 18f * ImGuiHelpers.GlobalScale;
                ImGui.Indent(indent);
                var pending = _autoDetectRequestService.GetPendingRequestsSnapshot();
                var pendingUids = new HashSet<string>(pending.Select(p => p.Uid!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
                var pendingTokens = new HashSet<string>(pending.Select(p => p.Token!).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
                var actionButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.UserPlus);
                using var table = ImUtf8.Table("nearby-table", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerV);
                if (table)
                {
                    ImGui.TableSetupColumn(Loc.Get("CompactUi.Nearby.Table.Name"), ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn(Loc.Get("CompactUi.Nearby.Table.Action"), ImGuiTableColumnFlags.WidthFixed, actionButtonSize.X);

                    var rowHeight = MathF.Max(ImGui.GetFrameHeight(), ImGui.GetTextLineHeight()) + ImGui.GetStyle().ItemSpacing.Y;
                    OtterGuiImGuiClip.ClippedDraw(nearbyEntries, e =>
                    {
                        bool alreadyPaired = false;
                        if (!string.IsNullOrEmpty(e.Uid))
                        {
                            alreadyPaired = _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, e.Uid, StringComparison.Ordinal));
                        }
                        bool alreadyInvited = (!string.IsNullOrEmpty(e.Uid) && pendingUids.Contains(e.Uid))
                                              || (!string.IsNullOrEmpty(e.Token) && pendingTokens.Contains(e.Token));

                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        var name = e.DisplayName ?? e.Name;
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(name);
                        ImGui.TableSetColumnIndex(1);
                        var curX = ImGui.GetCursorPosX();
                        var availX = ImGui.GetContentRegionAvail().X; // width of the action column
                        ImGui.SetCursorPosX(curX + MathF.Max(0, availX - actionButtonSize.X));

                        using (ImRaii.PushId(e.Token ?? e.Uid ?? e.Name))
                        {
                            if (alreadyPaired)
                            {
                                using (ImRaii.Disabled())
                                {
                                    _uiSharedService.IconButton(FontAwesomeIcon.UserPlus);
                                }
                                UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.Reason.Paired"));
                            }
                            else if (alreadyInvited)
                            {
                                using (ImRaii.Disabled())
                                {
                                    _uiSharedService.IconButton(FontAwesomeIcon.UserPlus);
                                }
                                UiSharedService.AttachToolTip(Loc.Get("AutoDetectUi.Nearby.Reason.AlreadyInvited"));
                            }
                            else if (_uiSharedService.IconButton(FontAwesomeIcon.UserPlus))
                            {
                                _ = _autoDetectRequestService.SendRequestAsync(e.Token!, e.Uid, e.DisplayName);
                            }
                        }
                        UiSharedService.AttachToolTip(Loc.Get("CompactUi.Nearby.InviteTooltip"));
                    }, rowHeight);
                }

                ImGui.Unindent(indent);
            }, stretchWidth: true);
        }
        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawSidebar()
    {
        bool isConnected = _apiController.ServerState is ServerState.Connected;
        bool hasNotifications = _notificationCount > 0;

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        _sidebarButtonRects.Clear();

        ImGuiHelpers.ScaledDummy(6f);
        DrawConnectionIcon();
        ImGuiHelpers.ScaledDummy(4f);
        DrawSidebarUid();
        ImGuiHelpers.ScaledDummy(8f);
        string notificationsTooltip = hasNotifications
            ? Loc.Get("CompactUi.Sidebar.Notifications")
            : Loc.Get("CompactUi.Sidebar.NotificationsEmpty");
        DrawSidebarButton(FontAwesomeIcon.Bell, notificationsTooltip, CompactUiSection.Notifications, hasNotifications, hasNotifications, _notificationCount, null, ImGuiColors.DalamudOrange);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.GlobeEurope, "Social", CompactUiSection.Social, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        ImGuiHelpers.ScaledDummy(3f);
        int pendingInvites = _nearbyPending.Pending.Count;
        bool highlightAutoDetect = pendingInvites > 0;
        string autoDetectTooltip = highlightAutoDetect
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Sidebar.AutoDetectPending"), pendingInvites)
            : Loc.Get("CompactUi.Sidebar.AutoDetect");
        DrawSidebarButton(FontAwesomeIcon.BroadcastTower, autoDetectTooltip, CompactUiSection.AutoDetect, isConnected, highlightAutoDetect, pendingInvites);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("CompactUi.Sidebar.CharacterAnalysis"), CompactUiSection.CharacterAnalysis, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.Running, Loc.Get("CompactUi.Sidebar.CharacterDataHub"), CompactUiSection.CharacterDataHub, isConnected);
        ImGuiHelpers.ScaledDummy(12f);
        DrawSidebarButton(FontAwesomeIcon.UserCircle, Loc.Get("CompactUi.Sidebar.EditProfile"), CompactUiSection.EditProfile, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.Cog, Loc.Get("CompactUi.Sidebar.Settings"), CompactUiSection.Settings);

        drawList.ChannelsSetCurrent(0);
        DrawSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, string tooltip, CompactUiSection section, bool enabled = true, bool highlight = false, int badgeCount = 0, Action? onClick = null, Vector4? highlightColor = null)
    {
        using var id = ImRaii.PushId((int)section);
        float regionWidth = ImGui.GetContentRegionAvail().X;
        float buttonWidth = SidebarIconSize * ImGuiHelpers.GlobalScale;
        float offset = System.Math.Max(0f, (regionWidth - buttonWidth) / 2f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        bool isActive = _activeSection == section;

        if (DrawSidebarSquareButton(icon, isActive, highlight, enabled, badgeCount, highlightColor))
        {
            if (onClick != null)
            {
                onClick.Invoke();
            }
            else
            {
                _activeSection = section;
            }
        }

        _sidebarButtonRects[section] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawConnectionIcon()
    {
        var state = _apiController.ServerState;
        var hasServer = _serverManager.HasServers;
        var currentServer = hasServer ? _serverManager.CurrentServer : null;
        bool isLinked = currentServer != null && !currentServer.FullPause;
        var icon = isLinked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;

        using var id = ImRaii.PushId("connection-icon");
        float childWidth = SidebarWidth * ImGuiHelpers.GlobalScale;
        float buttonWidth = SidebarIconSize * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX((childWidth - buttonWidth) / 2f);

        bool isTogglingDisabled = !hasServer || state is ServerState.Reconnecting or ServerState.Disconnecting;

        var connectionColor = state is ServerState.Connected
            ? new Vector4(0.25f, 0.85f, 0.45f, 1f)
            : new Vector4(0.90f, 0.25f, 0.25f, 1f);
        if (DrawSidebarSquareButton(icon, false, false, !isTogglingDisabled, 0, null, connectionColor) && !isTogglingDisabled)
        {
            ToggleConnection();
        }

        var tooltip = hasServer
            ? (isLinked
                ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.DisconnectTooltip"), currentServer!.ServerName)
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.ConnectTooltip"), currentServer!.ServerName))
            : Loc.Get("CompactUi.Connection.NoServer");
        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawSidebarUid()
    {
        var uidText = GetUidText();
        var uidColor = GetUidColor();
        bool isConnected = _apiController.ServerState is ServerState.Connected;

        float regionWidth = ImGui.GetContentRegionAvail().X;
        float padding = 4f * ImGuiHelpers.GlobalScale;
        float maxTextWidth = regionWidth - padding * 2f;

        var textSize = ImGui.CalcTextSize(uidText);
        float fontScale = 1f;
        if (textSize.X > maxTextWidth && maxTextWidth > 0)
        {
            fontScale = maxTextWidth / textSize.X;
            fontScale = MathF.Max(fontScale, 0.55f); // minimum readability
        }

        using var scalePush = UiSharedService.PushFontScale(fontScale);
        textSize = ImGui.CalcTextSize(uidText);
        
        float textX = ImGui.GetCursorPosX() + (regionWidth - textSize.X) / 2f;
        ImGui.SetCursorPosX(textX);

        if (isConnected)
        {
            var screenPos = ImGui.GetCursorScreenPos();
            float btnHeight = textSize.Y;
            ImGui.InvisibleButton("##sidebarUid", new Vector2(textSize.X, btnHeight));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();

            var font = ImGui.GetFont();
            float fontSize = ImGui.GetFontSize();
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(font, fontSize, screenPos, ImGui.GetColorU32(uidColor), uidText);

            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked)
                ImGui.SetClipboardText(_apiController.DisplayName);

            UiSharedService.AttachToolTip(Loc.Get("CompactUi.Uid.CopyTooltip"));
        }
        else
        {
            ImGui.TextColored(uidColor, uidText);
        }
    }

    private bool DrawSidebarSquareButton(FontAwesomeIcon icon, bool isActive, bool highlight, bool enabled, int badgeCount, Vector4? highlightColor, Vector4? iconColorOverride = null)
    {
        float size = SidebarIconSize * ImGuiHelpers.GlobalScale;

        bool useIndicator = isActive;
        bool useAccent = highlight && enabled;
        var buttonColor = useAccent ? UiSharedService.AccentColor : SidebarButtonColor;
        var hoverColor = useAccent ? UiSharedService.AccentHoverColor : SidebarButtonHoverColor;
        var activeColor = useAccent ? UiSharedService.AccentActiveColor : SidebarButtonActiveColor;
        if (useIndicator)
        {
            buttonColor = new Vector4(0f, 0f, 0f, 0f);
            hoverColor = buttonColor;
            activeColor = buttonColor;
        }

        string iconText = icon.ToIconString();
        Vector2 iconSize;
        using (_uiSharedService.IconFont.Push())
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var start = ImGui.GetCursorScreenPos();
        bool clicked;

        using var disabled = ImRaii.Disabled(!enabled);
        using var buttonColorPush = ImRaii.PushColor(ImGuiCol.Button, buttonColor);
        using var hoverColorPush = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColor);
        using var activeColorPush = ImRaii.PushColor(ImGuiCol.ButtonActive, activeColor);

        clicked = ImGui.Button("##sidebar-icon", new Vector2(size, size));

        using (_uiSharedService.IconFont.Push())
        {
            var textPos = new Vector2(
                MathF.Round(start.X + (size - iconSize.X) / 2f),
                MathF.Round(start.Y + (size - iconSize.Y) / 2f));
            uint iconColor = !enabled
                ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.9f, 1f));
            if (enabled)
            {
                if (iconColorOverride.HasValue)
                {
                    iconColor = ImGui.ColorConvertFloat4ToU32(iconColorOverride.Value);
                }
                if (highlight)
                {
                    var color = highlightColor ?? new Vector4(0.45f, 0.85f, 0.45f, 1f);
                    iconColor = ImGui.ColorConvertFloat4ToU32(color);
                }
                else if (isActive)
                {
                    iconColor = ImGui.GetColorU32(ImGuiCol.Text);
                }
            }
            ImGui.GetWindowDrawList().AddText(textPos, iconColor, iconText);
        }

        if (badgeCount > 0)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            float radius = 6f * ImGuiHelpers.GlobalScale;
            var center = new Vector2(max.X - radius * 0.8f, min.Y + radius * 0.8f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(UiSharedService.AccentColor));
            string badgeText = badgeCount > 9 ? "9+" : badgeCount.ToString(CultureInfo.CurrentCulture);
            var textSize = ImGui.CalcTextSize(badgeText);
            drawList.AddText(center - textSize / 2f, ImGui.GetColorU32(ImGuiCol.Text), badgeText);
        }

        return clicked && enabled;
    }

    private void DrawSidebarIndicator(ImDrawListPtr drawList)
    {
        if (!_sidebarButtonRects.TryGetValue(_activeSection, out var rect))
        {
            return;
        }

        var windowPos = ImGui.GetWindowPos();
        var targetPos = rect.Min;
        var targetSize = rect.Max - rect.Min;

        if (!_sidebarIndicatorInitialized || _sidebarWindowPos != windowPos)
        {
            _sidebarIndicatorPos = targetPos;
            _sidebarIndicatorSize = targetSize;
            _sidebarIndicatorInitialized = true;
            _sidebarWindowPos = windowPos;
        }
        else
        {
            float dt = ImGui.GetIO().DeltaTime;
            float lerpT = 1f - MathF.Exp(-SidebarIndicatorAnimSpeed * dt);
            _sidebarIndicatorPos = Vector2.Lerp(_sidebarIndicatorPos, targetPos, lerpT);
            _sidebarIndicatorSize = Vector2.Lerp(_sidebarIndicatorSize, targetSize, lerpT);
        }

        float padding = 2f * ImGuiHelpers.GlobalScale;
        var min = _sidebarIndicatorPos - new Vector2(padding);
        var max = _sidebarIndicatorPos + _sidebarIndicatorSize + new Vector2(padding);
        float rounding = 6f * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(UiSharedService.AccentColor), rounding);
    }


    private void ToggleConnection()
    {
        if (!_serverManager.HasServers) return;

        _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
        _serverManager.Save();
        _ = _apiController.CreateConnections();
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
        if (_socialSubSection == SocialSubSection.IndividualPairs)
        {
            DrawPairSectionBody();
        }
        else
        {
            DrawSyncshellSection();
        }
    }

    private void DrawSocialSwitchButtons()
    {
        var individualLabel = Loc.Get("CompactUi.Sidebar.IndividualPairs");
        var syncshellLabel = Loc.Get("CompactUi.Sidebar.Syncshells");
        var icons = new[] { FontAwesomeIcon.User, FontAwesomeIcon.UserFriends };
        var labels = new[] { individualLabel, syncshellLabel };

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
            bool isActive = (i == 0 && _socialSubSection == SocialSubSection.IndividualPairs)
                         || (i == 1 && _socialSubSection == SocialSubSection.Syncshells);

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
                _socialSubSection = i == 0 ? SocialSubSection.IndividualPairs : SocialSubSection.Syncshells;
            }
        }
    }

    private void DrawAutoDetectSection()
    {
        using (ImRaii.PushId("autodetect-inline")) _autoDetectUi.DrawInline();
    }

    private void DrawNotificationsSection()
    {
        var notifications = _notificationTracker.GetEntries();
        if (notifications.Count == 0)
        {
            DrawEmptySectionCard(
                "notifications-empty",
                Loc.Get("CompactUi.Sidebar.Notifications"),
                Loc.Get("CompactUi.Notifications.Empty"),
                Loc.Get("CompactUi.Notifications.EmptyTooltip"));
            return;
        }

        if (ImGui.Button(Loc.Get("CompactUi.Notifications.ClearAll")))
        {
            _notificationTracker.Clear();
        }

        ImGuiHelpers.ScaledDummy(4f);

        foreach (var notification in notifications.OrderByDescending(n => n.CreatedAt))
        {
            switch (notification.Category)
            {
                case NotificationCategory.AutoDetect:
                    DrawAutoDetectNotification(notification);
                    break;
                case NotificationCategory.Syncshell:
                    DrawSyncshellNotification(notification);
                    break;
                case NotificationCategory.McdfShare:
                    DrawMcdfShareNotification(notification);
                    break;
                default:
                    UiSharedService.DrawCard($"notification-{notification.Category}-{notification.Id}", () =>
                    {
                        ImGui.TextUnformatted(notification.Title);
                        if (!string.IsNullOrEmpty(notification.Description))
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                            ImGui.TextWrapped(notification.Description);
                            ImGui.PopStyleColor();
                        }

                        ImGuiHelpers.ScaledDummy(3f);
                        using (ImRaii.PushId($"notification-default-{notification.Id}"))
                        {
                            if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear") + "##" + notification.Id))
                            {
                                _notificationTracker.Remove(notification.Category, notification.Id);
                            }
                        }
                    }, stretchWidth: true);
                    break;
            }

            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawAutoDetectNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-autodetect-{notification.Id}", () =>
        {
            var displayName = ResolveRequesterDisplayName(notification.Id);
            var title = Loc.Get("AutoDetect.Notification.IncomingTitle");
            var body = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.IncomingBodyIdFirst"),
                BuildRequesterLabel(notification.Id, displayName));

            ImGui.TextUnformatted(title);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
            ImGui.TextWrapped(body);
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(3f);

            bool hasPending = _nearbyPending.Pending.ContainsKey(notification.Id);
            using (ImRaii.PushId(notification.Id))
            {
                using (ImRaii.Disabled(!hasPending))
                {
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Accept")))
                    {
                        TriggerAcceptAutoDetectNotification(notification.Id);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Decline")))
                    {
                        _nearbyPending.Remove(notification.Id);
                    }
                }

                if (!hasPending)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                    {
                        _notificationTracker.Remove(NotificationCategory.AutoDetect, notification.Id);
                    }
                }
            }
        }, stretchWidth: true);
    }

    private static void DrawEmptySectionCard(string id, string header, string description, string tooltip)
    {
        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.9f))
        {
            UiSharedService.DrawCard(id, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(header);

                ImGuiHelpers.ScaledDummy(3f);
                UiSharedService.ColorTextWrapped(description, ImGuiColors.DalamudGrey3);
            }, background: MutedCardBackground, border: MutedCardBorder, stretchWidth: true);
        }

        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawSyncshellNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-syncshell-{notification.Id}", () =>
        {
            ImGui.TextUnformatted(notification.Title);
            if (!string.IsNullOrEmpty(notification.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                ImGui.TextWrapped(notification.Description);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(3f);

            using (ImRaii.PushId($"syncshell-{notification.Id}"))
            {
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                {
                    _notificationTracker.Remove(NotificationCategory.Syncshell, notification.Id);
                }
            }
        }, stretchWidth: true);
    }

    private void DrawMcdfShareNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-mcdf-{notification.Id}", () =>
        {
            ImGui.TextUnformatted(notification.Title);
            if (!string.IsNullOrEmpty(notification.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                ImGui.TextUnformatted(notification.Description);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(3f);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey2);
            ImGui.TextUnformatted(notification.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(3f);
            using (ImRaii.PushId($"mcdf-{notification.Id}"))
            {
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                {
                    _notificationTracker.Remove(NotificationCategory.McdfShare, notification.Id);
                }
            }
        }, stretchWidth: true);
    }

    private void TriggerAcceptAutoDetectNotification(string uid)
    {
        _ = Task.Run(async () =>
        {
            bool accepted = await _nearbyPending.AcceptAsync(uid).ConfigureAwait(false);
            if (!accepted)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("CompactUi.Notifications.AutoDetectTitle"), string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Notifications.AcceptFailed"), uid), NotificationType.Warning, TimeSpan.FromSeconds(5)));
                _notificationTracker.Upsert(NotificationEntry.AcceptPairRequestFailed(uid));
            }
        });
    }

    private static string BuildRequesterLabel(string uid, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return uid;
        if (string.Equals(displayName, uid, StringComparison.OrdinalIgnoreCase)) return uid;
        return displayName;
    }

    private string ResolveRequesterDisplayName(string uid)
    {
        var nearby = _nearbyEntries.FirstOrDefault(e => e.IsMatch
            && string.Equals(e.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (nearby != null && !string.IsNullOrWhiteSpace(nearby.Name))
            return nearby.Name;

        if (_nearbyPending.Pending.TryGetValue(uid, out var pendingName)
            && !string.IsNullOrWhiteSpace(pendingName)
            && !string.Equals(pendingName, _apiController.DisplayName, StringComparison.OrdinalIgnoreCase))
            return pendingName;

        return string.Empty;
    }

    private void DrawNewUserNoteModal()
    {
        var newUserModalTitle = Loc.Get("CompactUi.NewUserModal.Title");
        if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
        {
            _lastAddedUser = _pairManager.LastAddedUser;
            _pairManager.LastAddedUser = null;
            ImGui.OpenPopup(newUserModalTitle);
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        if (ImGui.BeginPopupModal(newUserModalTitle, ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
        {
            if (_lastAddedUser == null)
            {
                _showModalForUserAddition = false;
            }
            else
            {
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.NewUserModal.Description"), _lastAddedUser.UserData.AliasOrUID));
                ImGui.InputTextWithHint("##noteforuser", string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.NewUserModal.NotePlaceholder"), _lastAddedUser.UserData.AliasOrUID), ref _lastAddedUserComment, 100);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("CompactUi.NewUserModal.SaveButton")))
                {
                    _serverManager.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                    _lastAddedUser = null;
                    _lastAddedUserComment = string.Empty;
                    _showModalForUserAddition = false;
                }
            }

            UiSharedService.SetScaledWindowSize(275);
            ImGui.EndPopup();
        }
    }

    private static bool RequiresServerConnection(CompactUiSection section)
    {
        return section is CompactUiSection.Notifications
            or CompactUiSection.Social
            or CompactUiSection.AutoDetect
            or CompactUiSection.CharacterAnalysis
            or CompactUiSection.CharacterDataHub;
    }

    private bool IsAlreadyPairedQuickMenu(Services.Mediator.NearbyEntry entry)
    {
        try
        {
            if (!string.IsNullOrEmpty(entry.Uid) &&
                _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, entry.Uid, StringComparison.Ordinal)))
            {
                return true;
            }

            var key = entry.DisplayName ?? entry.Name;
            if (string.IsNullOrEmpty(key)) return false;

            return _pairManager.DirectPairs.Any(p => string.Equals(p.UserData.AliasOrUID, key, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DrawAccentSeparator()
    {
        var hSepColor = UiSharedService.AccentColor with { W = 0.6f };
        var hSepDrawList = ImGui.GetWindowDrawList();
        var hSepCursor = ImGui.GetCursorScreenPos();
        var hSepStart = new Vector2(hSepCursor.X, hSepCursor.Y);
        var hSepEnd = new Vector2(hSepCursor.X + WindowContentWidth, hSepCursor.Y);
        hSepDrawList.AddLine(hSepStart, hSepEnd, ImGui.GetColorU32(hSepColor), 1f * ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads.ToList();

        if (currentUploads.Count > 0)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Upload);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalUploads = currentUploads.Count;

            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            ImGui.TextUnformatted($"{doneUploads}/{totalUploads}");
            var uploadText = $"({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            var textSize = ImGui.CalcTextSize(uploadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(uploadText);
        }

        var currentDownloads = _currentDownloads.SelectMany(d => d.Value.Values).ToList();

        if (currentDownloads.Count > 0)
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Download);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            ImGui.TextUnformatted($"{doneDownloads}/{totalDownloads}");
            var downloadText =
                $"({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            var textSize = ImGui.CalcTextSize(downloadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(downloadText);
        }
        ImGuiHelpers.ScaledDummy(2);
    }

    private List<Pair> GetFilteredUsers()
    {
        return _pairManager.DirectPairs.Where(p =>
        {
            if (_characterOrCommentFilter.IsNullOrEmpty()) return true;
            return p.UserData.AliasOrUID.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ||
                   (p.GetNote()?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (p.PlayerName?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false);
        }).ToList();
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => Loc.Get("CompactUi.ServerErrors.AttemptingToConnect"),
            ServerState.Reconnecting => Loc.Get("CompactUi.ServerErrors.Reconnecting"),
            ServerState.Disconnected => Loc.Get("CompactUi.ServerErrors.Disconnected"),
            ServerState.Disconnecting => Loc.Get("CompactUi.ServerErrors.Disconnecting"),
            ServerState.Unauthorized => string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.ServerErrors.Unauthorized"), _apiController.AuthFailureMessage),
            ServerState.Offline => Loc.Get("CompactUi.ServerErrors.Offline"),
            ServerState.VersionMisMatch =>
                Loc.Get("CompactUi.ServerErrors.VersionMismatch"),
            ServerState.RateLimited => Loc.Get("CompactUi.ServerErrors.RateLimited"),
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => Loc.Get("CompactUi.ServerErrors.NoSecretKey"),
            ServerState.MultiChara => Loc.Get("CompactUi.ServerErrors.MultiChara"),
            _ => string.Empty
        };
    }

    private Vector4 GetUidColor()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => UiSharedService.AccentColor,
            ServerState.Connected => UiSharedService.AccentColor,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => UiSharedService.AccentColor,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => UiSharedService.AccentColor,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => UiSharedService.AccentColor
        };
    }

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => Loc.Get("CompactUi.UidStatus.Reconnecting"),
            ServerState.Connecting => Loc.Get("CompactUi.UidStatus.Connecting"),
            ServerState.Disconnected => Loc.Get("CompactUi.UidStatus.Disconnected"),
            ServerState.Disconnecting => Loc.Get("CompactUi.UidStatus.Disconnecting"),
            ServerState.Unauthorized => Loc.Get("CompactUi.UidStatus.Unauthorized"),
            ServerState.VersionMisMatch => Loc.Get("CompactUi.UidStatus.VersionMismatch"),
            ServerState.Offline => Loc.Get("CompactUi.UidStatus.Offline"),
            ServerState.RateLimited => Loc.Get("CompactUi.UidStatus.RateLimited"),
            ServerState.NoSecretKey => Loc.Get("CompactUi.UidStatus.NoSecretKey"),
            ServerState.MultiChara => Loc.Get("CompactUi.UidStatus.MultiChara"),
            ServerState.Connected => _apiController.DisplayName,
            _ => string.Empty
        };
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