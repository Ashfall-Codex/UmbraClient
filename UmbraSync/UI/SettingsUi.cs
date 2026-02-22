using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Comparer;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI;
using UmbraSync.WebAPI.Files;
using UmbraSync.WebAPI.Files.Models;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public class SettingsUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly IpcManager _ipcManager;
    private readonly IpcProvider _ipcProvider;
    private readonly CacheMonitor _cacheMonitor;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, ConcurrentDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileCompactor _fileCompactor;
    private readonly FileUploadManager _fileTransferManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly FileCacheManager _fileCacheManager;
    private readonly PairManager _pairManager;
    private readonly GuiHookService _guiHookService;
    private readonly AutoDetectSuppressionService _autoDetectSuppressionService;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly AccountRegistrationService _registerService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiShared;
    private readonly ChatTypingDetectionService _chatTypingDetectionService;
    private readonly RgpdDataService _rgpdDataService;
    private bool _rgpdDeleteConfirmShown;
    private bool _rgpdRevokeConfirmShown;
    private string? _rgpdExportStatusMessage;
    private static readonly string DtrDefaultPreviewText = DtrEntry.DefaultGlyph + " 123";
    private bool _deleteAccountPopupModalShown = false;
    private bool _emoteColorPaletteOpen = false;
    private bool _hrpColorPaletteOpen = false;
    private string _lastTab = string.Empty;
    private int _activeSettingsTab;
    private bool? _notesSuccessfullyApplied = null;
    private bool _overwriteExistingLabels = false;
    private bool _readClearCache = false;
    private CancellationTokenSource? _validationCts;
    private Task<List<FileCacheEntity>>? _validationTask;
    private bool _wasOpen = false;
    private readonly IProgress<(int, int, FileCacheEntity)> _validationProgress;
    private (int, int, FileCacheEntity) _currentProgress;

    private bool _registrationInProgress = false;
    private bool _registrationSuccess = false;
    private string? _registrationMessage;
    private int _serverConfigTab = 0;
    private bool _importResultPopupShown;
    private List<(string Name, bool Imported, string Reason)> _importResults = [];
    private readonly HashSet<int> _hoveredSecretKeys = [];
    private readonly PenumbraPrecacheService _precacheService;
    private const float SettingsSidebarWidth = 140f;
    private const float SettingsSidebarAnimSpeed = 18f;
    private readonly Dictionary<int, (Vector2 Min, Vector2 Max)> _settingsSidebarRects = new();
    private Vector2 _settingsSidebarIndicatorPos;
    private Vector2 _settingsSidebarIndicatorSize;
    private bool _settingsSidebarIndicatorInit;
    private Vector2 _settingsSidebarWindowPos;

    private static readonly string[] SettingsLabels = ["General", "Performance", "Storage", "Transfers", "AutoDetect", "Chat", "Compte", "Vie privée", "Avancé", "À propos"];
    private static readonly FontAwesomeIcon[] SettingsIcons = [
        FontAwesomeIcon.Cog, FontAwesomeIcon.Bolt, FontAwesomeIcon.Database,
        FontAwesomeIcon.Retweet, FontAwesomeIcon.BroadcastTower, FontAwesomeIcon.Comment,
        FontAwesomeIcon.UserCircle, FontAwesomeIcon.ShieldAlt,
        FontAwesomeIcon.Wrench, FontAwesomeIcon.InfoCircle
    ];
    private static readonly string[] SettingsDescriptionKeys = [
        "Settings.Section.General.Desc",
        "Settings.Section.Performance.Desc",
        "Settings.Section.Storage.Desc",
        "Settings.Section.Transfers.Desc",
        "Settings.Section.AutoDetect.Desc",
        "Settings.Section.Chat.Desc",
        "Settings.Section.Account.Desc",
        "Settings.Section.Privacy.Desc",
        "Settings.Section.Advanced.Desc",
        "Settings.Section.About.Desc",
    ];

    public SettingsUi(ILogger<SettingsUi> logger,
        UiSharedService uiShared, MareConfigService configService,
        PairManager pairManager, GuiHookService guiHookService,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        MareMediator mediator, PerformanceCollectorService performanceCollector,
        FileUploadManager fileTransferManager,
        FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager,
        FileCompactor fileCompactor, ApiController apiController,
        IpcManager ipcManager, IpcProvider ipcProvider, CacheMonitor cacheMonitor,
        DalamudUtilService dalamudUtilService, AccountRegistrationService registerService,
        AutoDetectSuppressionService autoDetectSuppressionService,
        PenumbraPrecacheService precacheService,
        ChatTypingDetectionService chatTypingDetectionService,
        RgpdDataService gdprDataService) : base(logger, mediator, "Umbra Settings", performanceCollector)
    {
        _configService = configService;
        _pairManager = pairManager;
        _guiHookService = guiHookService;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _performanceCollector = performanceCollector;
        _fileTransferManager = fileTransferManager;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _ipcProvider = ipcProvider;
        _cacheMonitor = cacheMonitor;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _autoDetectSuppressionService = autoDetectSuppressionService;
        _fileCompactor = fileCompactor;
        _uiShared = uiShared;
        _precacheService = precacheService;
        _chatTypingDetectionService = chatTypingDetectionService;
        _rgpdDataService = gdprDataService;
        AllowClickthrough = false;
        AllowPinning = false;
        _validationProgress = new Progress<(int, int, FileCacheEntity)>(v => _currentProgress = v);

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(420, 400),
            MaximumSize = new Vector2(900, 2000),
        };

        Mediator.Subscribe<OpenSettingsUiMessage>(this, (_) => Toggle());
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) => LastCreatedCharacterData = msg.CharacterData);
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
    }

    public CharacterData? LastCreatedCharacterData { private get; set; }
    private ApiController ApiController => _uiShared.ApiController;

    protected override void DrawInternal()
    {
        DrawSettingsContent();
    }

    public void DrawInline()
    {
        using (ImRaii.PushId("SettingsUiInline"))
        {
            DrawSettingsContent();
        }
    }

    public override void OnClose()
    {
        _uiShared.EditTrackerPosition = false;

        base.OnClose();
    }

    private void DrawBlockedTransfers()
    {
        _lastTab = "BlockedTransfers";
        UiSharedService.ColorTextWrapped("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                             "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                             "Ask your paired friend to send you the mod in question through other means or acquire the mod yourself.",
            ImGuiColors.DalamudGrey);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(
                $"Hash/Filename");
            ImGui.TableSetupColumn($"Forbidden by");

            ImGui.TableHeadersRow();

            foreach (var item in _fileTransferOrchestrator.ForbiddenTransfers)
            {
                ImGui.TableNextColumn();
                if (item is UploadFileTransfer transfer)
                {
                    ImGui.TextUnformatted(transfer.LocalFile);
                }
                else
                {
                    ImGui.TextUnformatted(item.Hash);
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ForbiddenBy);
            }
            ImGui.EndTable();
        }
    }

    private void DrawCurrentTransfers()
    {
        _lastTab = "Transfers";
        DrawSectionHeader(3);


        bool enablePenumbraPrecache = _configService.Current.EnablePenumbraPrecache;
        if (ImGui.Checkbox(Loc.Get("Settings.Transfer.Precache.Enable"), ref enablePenumbraPrecache))
        {
            _configService.Current.EnablePenumbraPrecache = enablePenumbraPrecache;
            _configService.Save();
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.Transfer.Precache.Enable.Help"));

        using (ImRaii.Disabled(!enablePenumbraPrecache))
        {
            // Etat du pré-cache
            var runningText = _precacheService.IsUploading
                ? Loc.Get("Settings.Transfer.Precache.Status.Running")
                : Loc.Get("Settings.Transfer.Precache.Status.Idle");
            var bytesSoFar = _precacheService.BytesUploadedThisRun;
            if (bytesSoFar > 0)
            {
                ImGui.TextUnformatted(string.Format(Loc.Get("Settings.Transfer.Precache.Status.WithBytes"), runningText, UiSharedService.ByteToString(bytesSoFar)));
            }
            else
            {
                ImGui.TextUnformatted(string.Format(Loc.Get("Settings.Transfer.Precache.Status"), runningText));
            }

            // Dernier lancement
            if (_precacheService.LastRunEndUtc.HasValue || _precacheService.LastRunStartUtc.HasValue)
            {
                var last = (_precacheService.LastRunEndUtc ?? _precacheService.LastRunStartUtc)!.Value.ToLocalTime();
                ImGui.TextUnformatted(string.Format(Loc.Get("Settings.Transfer.Precache.LastRun"), last.ToString("g", CultureInfo.CurrentCulture)));
            }

            // El famoso in progress
            if (_precacheService.IsUploading && !string.IsNullOrEmpty(_precacheService.StatusText))
            {
                UiSharedService.ColorTextWrapped(_precacheService.StatusText, ImGuiColors.DalamudGrey);
            }

            if (ImGui.Button(Loc.Get("Settings.Transfer.Precache.RunNow")))
            {
                _precacheService.TriggerManualPrecache();
            }
            UiSharedService.AttachToolTip(Loc.Get("Settings.Transfer.Precache.RunNow.Help"));
        }

        int maxParallelDownloads = _configService.Current.ParallelDownloads;
        int downloadSpeedLimit = _configService.Current.DownloadSpeedLimitInBytes;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Global Download Speed Limit");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(100 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.3f));
        if (ImGui.InputInt("###speedlimit", ref downloadSpeedLimit))
        {
            _configService.Current.DownloadSpeedLimitInBytes = downloadSpeedLimit;
            _configService.Save();
            Mediator.Publish(new DownloadLimitChangedMessage());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(100 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.3f));
        _uiShared.DrawCombo("###speed", [DownloadSpeeds.Bps, DownloadSpeeds.KBps, DownloadSpeeds.MBps],
            (s) => s switch
            {
                DownloadSpeeds.Bps => "Byte/s",
                DownloadSpeeds.KBps => "KB/s",
                DownloadSpeeds.MBps => "MB/s",
                _ => throw new NotSupportedException()
            }, (s) =>
            {
                _configService.Current.DownloadSpeedType = s;
                _configService.Save();
                Mediator.Publish(new DownloadLimitChangedMessage());
            }, _configService.Current.DownloadSpeedType);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("0 = No limit/infinite");
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderInt("Maximum Parallel Downloads", ref maxParallelDownloads, 1, 10))
        {
            _configService.Current.ParallelDownloads = maxParallelDownloads;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Limite le nombre de téléchargements simultanés pour éviter la surcharge. (défaut: 10)");

        ImGui.Spacing();
        _uiShared.BigText(Loc.Get("Settings.Transfer.PairProcessing.Title"));

        bool enableParallelPairProcessing = _configService.Current.EnableParallelPairProcessing;
        if (ImGui.Checkbox(Loc.Get("Settings.Transfer.PairProcessing.Enable"), ref enableParallelPairProcessing))
        {
            _configService.Current.EnableParallelPairProcessing = enableParallelPairProcessing;
            _configService.Save();
            Mediator.Publish(new PairProcessingLimitChangedMessage());
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.Transfer.PairProcessing.Enable.Help"));

        if (!enableParallelPairProcessing) ImGui.BeginDisabled();
        ImGui.Indent();
        int maxConcurrentPairApplications = _configService.Current.MaxConcurrentPairApplications;
        ImGui.SetNextItemWidth(MathF.Min(200 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderInt(Loc.Get("Settings.Transfer.PairProcessing.MaxConcurrent"), ref maxConcurrentPairApplications, 2, 10))
        {
            _configService.Current.MaxConcurrentPairApplications = maxConcurrentPairApplications;
            _configService.Save();
            Mediator.Publish(new PairProcessingLimitChangedMessage());
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.Transfer.PairProcessing.MaxConcurrent.Help"));
        ImGui.Unindent();
        if (!enableParallelPairProcessing) ImGui.EndDisabled();

        ImGui.Separator();
        _uiShared.BigText("Transfer UI");

        bool showTransferWindow = _configService.Current.ShowTransferWindow;
        if (ImGui.Checkbox("Show separate transfer window", ref showTransferWindow))
        {
            _configService.Current.ShowTransferWindow = showTransferWindow;
            _configService.Save();
        }
        _uiShared.DrawHelpText($"The download window will show the current progress of outstanding downloads.{Environment.NewLine}{Environment.NewLine}" +
            $"What do W/Q/P/D stand for?{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Downloads){Environment.NewLine}" +
            $"Q = Queued on Server, waiting for queue ready signal{Environment.NewLine}" +
            $"P = Processing download (aka downloading){Environment.NewLine}" +
            $"D = Decompressing download");
        if (!_configService.Current.ShowTransferWindow) ImGui.BeginDisabled();
        ImGui.Indent();
        bool editTransferWindowPosition = _uiShared.EditTrackerPosition;
        if (ImGui.Checkbox("Edit Transfer Window position", ref editTransferWindowPosition))
        {
            _uiShared.EditTrackerPosition = editTransferWindowPosition;
        }
        ImGui.Unindent();
        if (!_configService.Current.ShowTransferWindow) ImGui.EndDisabled();

        bool showTransferBars = _configService.Current.ShowTransferBars;
        if (ImGui.Checkbox("Show transfer bars rendered below players", ref showTransferBars))
        {
            _configService.Current.ShowTransferBars = showTransferBars;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render a progress bar during the download at the feet of the player you are downloading from.");

        if (!showTransferBars) ImGui.BeginDisabled();
        ImGui.Indent();
        bool transferBarShowText = _configService.Current.TransferBarsShowText;
        if (ImGui.Checkbox("Show Download Text", ref transferBarShowText))
        {
            _configService.Current.TransferBarsShowText = transferBarShowText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Shows download text (amount of MiB downloaded) in the transfer bars");
        int transferBarWidth = _configService.Current.TransferBarsWidth;
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderInt("Transfer Bar Width", ref transferBarWidth, 0, 500))
        {
            if (transferBarWidth < 10)
                transferBarWidth = 10;
            _configService.Current.TransferBarsWidth = transferBarWidth;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Width of the displayed transfer bars (will never be less wide than the displayed text)");
        int transferBarHeight = _configService.Current.TransferBarsHeight;
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderInt("Transfer Bar Height", ref transferBarHeight, 0, 50))
        {
            if (transferBarHeight < 2)
                transferBarHeight = 2;
            _configService.Current.TransferBarsHeight = transferBarHeight;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Height of the displayed transfer bars (will never be less tall than the displayed text)");
        bool showUploading = _configService.Current.ShowUploading;
        if (ImGui.Checkbox("Show 'Uploading' text below players that are currently uploading", ref showUploading))
        {
            _configService.Current.ShowUploading = showUploading;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render an 'Uploading' text at the feet of the player that is in progress of uploading data.");

        ImGui.Unindent();
        if (!showUploading) ImGui.BeginDisabled();
        ImGui.Indent();
        bool showUploadingBigText = _configService.Current.ShowUploadingBigText;
        if (ImGui.Checkbox("Large font for 'Uploading' text", ref showUploadingBigText))
        {
            _configService.Current.ShowUploadingBigText = showUploadingBigText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render an 'Uploading' text in a larger font.");

        ImGui.Unindent();

        if (!showUploading) ImGui.EndDisabled();
        if (!showTransferBars) ImGui.EndDisabled();

        ImGui.Separator();
        _uiShared.BigText("Current Transfers");

        if (ImGui.BeginTabBar("TransfersTabBar"))
        {
            if (ApiController.ServerState is ServerState.Connected && ImGui.BeginTabItem("Transfers"))
            {
                ImGui.TextUnformatted("Uploads");
                if (ImGui.BeginTable("UploadsTable", 3))
                {
                    ImGui.TableSetupColumn("File");
                    ImGui.TableSetupColumn("Uploaded");
                    ImGui.TableSetupColumn("Size");
                    ImGui.TableHeadersRow();
                    foreach (var transfer in _fileTransferManager.CurrentUploads.ToArray())
                    {
                        var color = UiSharedService.UploadColor((transfer.Transferred, transfer.Total));
                        var col = ImRaii.PushColor(ImGuiCol.Text, color);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(transfer.Hash);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Transferred));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Total));
                        col.Dispose();
                        ImGui.TableNextRow();
                    }

                    ImGui.EndTable();
                }
                ImGui.Separator();
                ImGui.TextUnformatted("Downloads");
                if (ImGui.BeginTable("DownloadsTable", 4))
                {
                    ImGui.TableSetupColumn("User");
                    ImGui.TableSetupColumn("Server");
                    ImGui.TableSetupColumn("Files");
                    ImGui.TableSetupColumn("Download");
                    ImGui.TableHeadersRow();

                    foreach (var transfer in _currentDownloads.ToArray())
                    {
                        var userName = transfer.Key.Name;
                        foreach (var entry in transfer.Value)
                        {
                            var color = UiSharedService.UploadColor((entry.Value.TransferredBytes, entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(userName);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Key);
                            var col = ImRaii.PushColor(ImGuiCol.Text, color);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Value.TransferredFiles + "/" + entry.Value.TotalFiles);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Value.TransferredBytes) + "/" + UiSharedService.ByteToString(entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            col.Dispose();
                            ImGui.TableNextRow();
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Blocked Transfers"))
            {
                DrawBlockedTransfers();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawChatConfig()
    {
        _lastTab = "Chat";
        DrawSectionHeader(5);

        _uiShared.BigText(Loc.Get("Settings.RpNamesHeader"));

        var useRpNamesOnNameplates = _configService.Current.UseRpNamesOnNameplates;
        if (ImGui.Checkbox(Loc.Get("Settings.RpNamesOnNameplates"), ref useRpNamesOnNameplates))
        {
            _configService.Current.UseRpNamesOnNameplates = useRpNamesOnNameplates;
            _configService.Save();
            _guiHookService.RequestRedraw(force: true);
        }

        var useRpNamesInChat = _configService.Current.UseRpNamesInChat;
        if (ImGui.Checkbox(Loc.Get("Settings.RpNamesInChat"), ref useRpNamesInChat))
        {
            _configService.Current.UseRpNamesInChat = useRpNamesInChat;
            _configService.Save();
        }

        var useRpNameColors = _configService.Current.UseRpNameColors;
        if (ImGui.Checkbox(Loc.Get("Settings.RpNameColors"), ref useRpNameColors))
        {
            _configService.Current.UseRpNameColors = useRpNameColors;
            _configService.Save();
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.RpNameColors.Help"));

        ImGui.Spacing();

        _uiShared.BigText(Loc.Get("Settings.EmoteHighlight.Header"));

        var emoteHighlightEnabled = _configService.Current.EmoteHighlightEnabled;
        if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.Enable"), ref emoteHighlightEnabled))
        {
            _configService.Current.EmoteHighlightEnabled = emoteHighlightEnabled;
            _configService.Save();
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.EmoteHighlight.Enable.Help"));

        if (emoteHighlightEnabled)
        {
            using (ImRaii.PushIndent())
            {
                var asterisks = _configService.Current.EmoteHighlightAsterisks;
                if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.Asterisks"), ref asterisks))
                {
                    _configService.Current.EmoteHighlightAsterisks = asterisks;
                    _configService.Save();
                }

                var angleBrackets = _configService.Current.EmoteHighlightAngleBrackets;
                if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.AngleBrackets"), ref angleBrackets))
                {
                    _configService.Current.EmoteHighlightAngleBrackets = angleBrackets;
                    _configService.Save();
                }

                var squareBrackets = _configService.Current.EmoteHighlightSquareBrackets;
                if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.SquareBrackets"), ref squareBrackets))
                {
                    _configService.Current.EmoteHighlightSquareBrackets = squareBrackets;
                    _configService.Save();
                }

                DrawColorPaletteRow(
                    "emote",
                    Loc.Get("Settings.EmoteHighlight.ColorKey"),
                    _configService.Current.EmoteHighlightColorKey,
                    ref _emoteColorPaletteOpen,
                    key => { _configService.Current.EmoteHighlightColorKey = key; _configService.Save(); });

                ImGui.Spacing();

                var parentheses = _configService.Current.EmoteHighlightParenthesesGray;
                if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.Parentheses"), ref parentheses))
                {
                    _configService.Current.EmoteHighlightParenthesesGray = parentheses;
                    _configService.Save();
                }
                _uiShared.DrawHelpText(Loc.Get("Settings.EmoteHighlight.Parentheses.Help"));

                if (parentheses)
                {
                    using (ImRaii.PushIndent())
                    {
                        var doubleParentheses = _configService.Current.EmoteHighlightDoubleParentheses;
                        if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.DoubleParentheses"), ref doubleParentheses))
                        {
                            _configService.Current.EmoteHighlightDoubleParentheses = doubleParentheses;
                            _configService.Save();
                        }

                        var chatTwoActive = _uiShared.ChatTwoExists;
                        using (ImRaii.Disabled(chatTwoActive))
                        {
                            var italic = !chatTwoActive && _configService.Current.EmoteHighlightParenthesesItalic;
                            if (ImGui.Checkbox(Loc.Get("Settings.EmoteHighlight.Parentheses.Italic"), ref italic))
                            {
                                _configService.Current.EmoteHighlightParenthesesItalic = italic;
                                _configService.Save();
                            }
                        }
                        if (chatTwoActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(Loc.Get("Settings.EmoteHighlight.Parentheses.Italic.ChatTwoWarning"));

                        DrawColorPaletteRow(
                            "hrp",
                            Loc.Get("Settings.EmoteHighlight.HrpColorKey"),
                            _configService.Current.EmoteHighlightParenthesesColorKey,
                            ref _hrpColorPaletteOpen,
                            key => { _configService.Current.EmoteHighlightParenthesesColorKey = key; _configService.Save(); });
                    }
                }
            }
        }

        ImGui.Spacing();

        _uiShared.BigText(Loc.Get("Settings.Typing.BubbleHeader"));
        using (ImRaii.PushIndent())
        {
            DrawTypingSettings();
        }
    }

    private void DrawColorPaletteRow(string id, string label, ushort currentKey, ref bool paletteOpen, Action<ushort> onSelect)
    {
        var uiColors = _dalamudUtilService.UiColors.Value;
        var previewSize = new Vector2(20 * ImGuiHelpers.GlobalScale, 20 * ImGuiHelpers.GlobalScale);

        var currentColor = uiColors.TryGetValue(currentKey, out var currentUiColor)
            ? UiColorToVector4(currentUiColor.Dark)
            : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        using (ImRaii.PushColor(ImGuiCol.Button, currentColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, currentColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, currentColor))
        {
            ImGui.Button($"##{id}_preview", previewSize);
        }

        ImGui.SameLine();
        var arrow = paletteOpen ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button(arrow.ToIconString() + $"##{id}_toggle"))
                paletteOpen = !paletteOpen;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        if (!paletteOpen)
            return;

        var buttonSize = new Vector2(20 * ImGuiHelpers.GlobalScale, 20 * ImGuiHelpers.GlobalScale);
        var spacing = 2 * ImGuiHelpers.GlobalScale;
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var buttonsPerRow = Math.Max(1, (int)((contentWidth + spacing) / (buttonSize.X + spacing)));

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, spacing)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 2.0f))
        {
            var col = 0;
            foreach (var (rowId, uiColor) in uiColors.OrderBy(kv => kv.Key))
            {
                var rgba = uiColor.Dark;
                if ((rgba & 0xFFFFFF00) == 0)
                    continue;

                var color = UiColorToVector4(rgba);
                var isSelected = currentKey == (ushort)rowId;

                using (ImRaii.PushColor(ImGuiCol.Button, color))
                using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(Math.Min(color.X * 1.2f, 1.0f), Math.Min(color.Y * 1.2f, 1.0f), Math.Min(color.Z * 1.2f, 1.0f), 1.0f)))
                using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(color.X * 0.8f, color.Y * 0.8f, color.Z * 0.8f, 1.0f)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, isSelected ? 2.0f : 0.0f))
                using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(1.0f, 1.0f, 1.0f, 1.0f)))
                {
                    if (ImGui.Button($"##{id}_color_{rowId}", buttonSize))
                        onSelect((ushort)rowId);
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"#{rowId}");

                col++;
                if (col < buttonsPerRow)
                    ImGui.SameLine();
                else
                    col = 0;
            }
            if (col != 0)
                ImGui.NewLine();
        }
    }

    private static Vector4 UiColorToVector4(uint rgba)
    {
        var r = ((rgba >> 24) & 0xFF) / 255.0f;
        var g = ((rgba >> 16) & 0xFF) / 255.0f;
        var b = ((rgba >> 8) & 0xFF) / 255.0f;
        var a = (rgba & 0xFF) / 255.0f;
        return new Vector4(r, g, b, a);
    }

    private static void DrawPrivacyDescription(float availWidth)
    {
        float margin = availWidth * 0.05f;
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + margin);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth - margin * 2f);
            ImGui.TextWrapped(Loc.Get("Settings.Section.Privacy.Desc"));
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawPrivacy()
    {
        _lastTab = "Privacy";

        // EU Pantone blue #003399
        // Custom section header
        ImGuiHelpers.ScaledDummy(6f);
        var availWidth = ImGui.GetContentRegionAvail().X;
        string iconStr = FontAwesomeIcon.ShieldAlt.ToIconString();
        using (_uiShared.IconFont.Push())
        {
            var iconSz = ImGui.CalcTextSize(iconStr);
            float scale = 1.6f;
            var scaledSz = iconSz * scale;
            var pos = ImGui.GetCursorScreenPos();
            float iconX = pos.X + (availWidth - scaledSz.X) / 2f;
            ImGui.Dummy(new Vector2(0, scaledSz.Y));
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, pos.Y), ImGui.GetColorU32(UiSharedService.AccentColor), iconStr);
        }
        ImGuiHelpers.ScaledDummy(4f);
        using (_uiShared.UidFont.Push())
        {
            var titleSz = ImGui.CalcTextSize(Loc.Get("Settings.Privacy.Title"));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
            UiSharedService.ColorText(Loc.Get("Settings.Privacy.Title"), UiSharedService.AccentColor);
        }
        ImGuiHelpers.ScaledDummy(2f);
        DrawPrivacyDescription(availWidth);
        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // --- Consent status ---
        UiSharedService.ColorTextWrapped(Loc.Get("Settings.Privacy.ConsentStatus"), UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);

        if (_rgpdDataService.IsRgpdConsentValid)
        {
            UiSharedService.ColorText(Loc.Get("Settings.Privacy.ConsentActive"), ImGuiColors.HealerGreen);
            if (_configService.Current.RgpdConsentDate.HasValue)
            {
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                    ImGui.TextUnformatted($"({_configService.Current.RgpdConsentDate.Value:dd/MM/yyyy})");
            }
            ImGuiHelpers.ScaledDummy(2f);

            var details = new List<string>();
            if (_configService.Current.RgpdConsentDataCollection) details.Add(Loc.Get("Settings.Privacy.ConsentDetail.Collection"));
            if (_configService.Current.RgpdConsentDataSharing) details.Add(Loc.Get("Settings.Privacy.ConsentDetail.Sharing"));
            if (_configService.Current.RgpdConsentThirdPartyPlugins) details.Add(Loc.Get("Settings.Privacy.ConsentDetail.ThirdParty"));
            foreach (var d in details)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(d);
            }
        }
        else
        {
            UiSharedService.ColorText(Loc.Get("Settings.Privacy.ConsentExpired"), ImGuiColors.DalamudYellow);
        }

        ImGuiHelpers.ScaledDummy(4f);
        UiSharedService.ColorTextWrapped(Loc.Get("Rgpd.Consent.Reassurance"), ImGuiColors.HealerGreen);

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // --- Data export ---
        UiSharedService.ColorTextWrapped(Loc.Get("Settings.Privacy.Export.Header"), UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Export.Description"));
        ImGuiHelpers.ScaledDummy(4f);

        if (_uiShared.IconTextButton(FontAwesomeIcon.FileExport, Loc.Get("Settings.Privacy.Export.LocalButton")))
        {
            Mediator.Publish(new RgpdDataExportRequestMessage());
            _rgpdExportStatusMessage = Loc.Get("Settings.Privacy.Export.InProgress");
        }

        if (ApiController.ServerAlive)
        {
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.CloudDownloadAlt, Loc.Get("Settings.Privacy.Export.ServerButton")))
            {
                _rgpdExportStatusMessage = Loc.Get("Settings.Privacy.Export.InProgress");
                _ = Task.Run(async () =>
                {
                    var result = await _apiController.UserRgpdExportData().ConfigureAwait(false);
                    if (result != null)
                    {
                        var exportDir = !string.IsNullOrEmpty(_configService.Current.ExportFolder)
                            ? _configService.Current.ExportFolder
                            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        Directory.CreateDirectory(exportDir);
                        var path = Path.Combine(exportDir, $"umbrasync_server_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(path, json);
                        _rgpdExportStatusMessage = string.Format(Loc.Get("Settings.Privacy.Export.Success"), path);
                    }
                    else
                    {
                        _rgpdExportStatusMessage = Loc.Get("Settings.Privacy.Export.Failed");
                    }
                });
            }
        }

        if (!string.IsNullOrEmpty(_rgpdExportStatusMessage))
        {
            ImGuiHelpers.ScaledDummy(2f);
            UiSharedService.TextWrapped(_rgpdExportStatusMessage);
        }

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // --- Data deletion ---
        UiSharedService.ColorTextWrapped(Loc.Get("Settings.Privacy.Delete.Header"), UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Delete.Description"));
        ImGuiHelpers.ScaledDummy(4f);

        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Settings.Privacy.Delete.LocalButton")))
        {
            _rgpdDeleteConfirmShown = true;
            ImGui.OpenPopup("###rgpdDeleteLocalPopup");
        }

        if (ImGui.BeginPopupModal(Loc.Get("Settings.Privacy.Delete.ConfirmTitle") + "###rgpdDeleteLocalPopup", ref _rgpdDeleteConfirmShown, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Delete.ConfirmMessage"));
            ImGui.Separator();
            ImGui.Spacing();
            var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - ImGui.GetStyle().ItemSpacing.X) / 2;

            if (ImGui.Button(Loc.Get("Settings.Privacy.Delete.ConfirmYes"), new Vector2(buttonSize, 0)))
            {
                Mediator.Publish(new RgpdLocalDataDeletionRequestMessage());
                _rgpdDeleteConfirmShown = false;
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Common.Cancel") + "##cancelRgpdDelete", new Vector2(buttonSize, 0)))
                _rgpdDeleteConfirmShown = false;

            UiSharedService.SetScaledWindowSize(350);
            ImGui.EndPopup();
        }

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // --- Revoke consent ---
        UiSharedService.ColorTextWrapped(Loc.Get("Settings.Privacy.Revoke.Header"), UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
        UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Revoke.Description"));
        ImGuiHelpers.ScaledDummy(4f);

        if (_uiShared.IconTextButton(FontAwesomeIcon.Ban, Loc.Get("Settings.Privacy.Revoke.Button")))
        {
            _rgpdRevokeConfirmShown = true;
            ImGui.OpenPopup("###rgpdRevokePopup");
        }

        if (ImGui.BeginPopupModal(Loc.Get("Settings.Privacy.Revoke.ConfirmTitle") + "###rgpdRevokePopup", ref _rgpdRevokeConfirmShown, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(Loc.Get("Settings.Privacy.Revoke.ConfirmMessage"));
            ImGui.Separator();
            ImGui.Spacing();
            var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - ImGui.GetStyle().ItemSpacing.X) / 2;

            if (ImGui.Button(Loc.Get("Settings.Privacy.Revoke.ConfirmYes"), new Vector2(buttonSize, 0)))
            {
                _rgpdDataService.RevokeRgpdConsent();
                _rgpdRevokeConfirmShown = false;
                Mediator.Publish(new SwitchToIntroUiMessage());
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Common.Cancel") + "##cancelRgpdRevoke", new Vector2(buttonSize, 0)))
                _rgpdRevokeConfirmShown = false;

            UiSharedService.SetScaledWindowSize(350);
            ImGui.EndPopup();
        }

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        // --- Your rights ---
        UiSharedService.ColorTextWrapped(Loc.Get("Settings.Privacy.Rights.Header"), UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);

        string[] rightKeys = [
            "Settings.Privacy.Rights.Access",
            "Settings.Privacy.Rights.Rectification",
            "Settings.Privacy.Rights.Erasure",
            "Settings.Privacy.Rights.Portability",
            "Settings.Privacy.Rights.Opposition",
            "Settings.Privacy.Rights.Limitation",
        ];
        foreach (var key in rightKeys)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            UiSharedService.TextWrapped(Loc.Get(key));
        }
    }

    private void DrawAdvanced()
    {
        _lastTab = "Advanced";
        DrawSectionHeader(8);

        // --- Plugin Compatibility ---
        _uiShared.BigText(Loc.Get("Settings.Advanced.PluginCompatibility"));
        UiSharedService.DrawCard("plugins-card", () =>
        {
            _ = _uiShared.DrawOtherPluginState();
        });

        ImGuiHelpers.ScaledDummy(4f);

        // --- Settings ---
        UiSharedService.DrawCard("advanced-settings-card", () =>
        {
            // Umbra API
            bool umbraApi = _configService.Current.UmbraAPI;
            if (ImGui.Checkbox(Loc.Get("Settings.Advanced.UmbraApi"), ref umbraApi))
            {
                _configService.Current.UmbraAPI = umbraApi;
                _configService.Save();
                _ipcProvider.HandleMareImpersonation();
            }
            _uiShared.DrawHelpText(Loc.Get("Settings.Advanced.UmbraApi.Help"));
            ImGui.SameLine();
            if (_ipcProvider.ImpersonationActive)
                UiSharedService.ColorTextWrapped(Loc.Get("Settings.Advanced.UmbraApi.Active"), UiSharedService.AccentColor);
            else if (!umbraApi)
                UiSharedService.ColorTextWrapped(Loc.Get("Settings.Advanced.UmbraApi.Disabled"), ImGuiColors.DalamudYellow);
            else if (_ipcProvider.MarePluginEnabled)
                UiSharedService.ColorTextWrapped(Loc.Get("Settings.Advanced.UmbraApi.OtherProvider"), ImGuiColors.DalamudYellow);
            else
                UiSharedService.ColorTextWrapped(Loc.Get("Settings.Advanced.UmbraApi.Unknown"), UiSharedService.AccentColor);

            ImGuiHelpers.ScaledDummy(2f);

            // Log Events
            bool logEvents = _configService.Current.LogEvents;
            if (ImGui.Checkbox(Loc.Get("Settings.Advanced.LogEvents"), ref logEvents))
            {
                _configService.Current.LogEvents = logEvents;
                _configService.Save();
            }
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.NotesMedical, Loc.Get("Settings.Advanced.OpenEventViewer")))
            {
                Mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
            }

            ImGuiHelpers.ScaledDummy(2f);

            // Hold combat
            bool holdCombatApplication = _configService.Current.HoldCombatApplication;
            if (ImGui.Checkbox(Loc.Get("Settings.Advanced.HoldCombat"), ref holdCombatApplication))
            {
                if (!holdCombatApplication)
                    Mediator.Publish(new CombatOrPerformanceEndMessage());
                _configService.Current.HoldCombatApplication = holdCombatApplication;
                _configService.Save();
            }

            ImGuiHelpers.ScaledDummy(2f);

            // Serialized applications
            bool serializedApplications = _configService.Current.SerialApplication;
            if (ImGui.Checkbox(Loc.Get("Settings.Advanced.SerialApply"), ref serializedApplications))
            {
                _configService.Current.SerialApplication = serializedApplications;
                _configService.Save();
            }
            _uiShared.DrawHelpText(Loc.Get("Settings.Advanced.SerialApply.Help"));
        });

        ImGuiHelpers.ScaledDummy(4f);

        // --- Debug ---
        _uiShared.BigText(Loc.Get("Settings.Advanced.Debug"));
        UiSharedService.DrawCard("debug-card", () =>
        {
#if DEBUG
            if (LastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
            {
                foreach (var l in JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }).Split('\n'))
                {
                    ImGui.TextUnformatted($"{l}");
                }
                ImGui.TreePop();
            }
#endif
            if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, Loc.Get("Settings.Advanced.Debug.CopyCharaData")))
            {
                if (LastCreatedCharacterData != null)
                    ImGui.SetClipboardText(JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }));
                else
                    ImGui.SetClipboardText("ERROR: No created character data, cannot copy.");
            }
            UiSharedService.AttachToolTip(Loc.Get("Settings.Advanced.Debug.CopyCharaData.Help"));

            ImGuiHelpers.ScaledDummy(2f);

            _uiShared.DrawCombo(Loc.Get("Settings.Advanced.Debug.LogLevel"), Enum.GetValues<LogLevel>(), (l) => l.ToString(), (l) =>
            {
                _configService.Current.LogLevel = l;
                _configService.Save();
            }, _configService.Current.LogLevel);

            ImGuiHelpers.ScaledDummy(2f);

            bool logPerformance = _configService.Current.LogPerformance;
            if (ImGui.Checkbox(Loc.Get("Settings.Advanced.Debug.LogPerf"), ref logPerformance))
            {
                _configService.Current.LogPerformance = logPerformance;
                _configService.Save();
            }
            _uiShared.DrawHelpText(Loc.Get("Settings.Advanced.Debug.LogPerf.Help"));

            using (ImRaii.Disabled(!logPerformance))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, Loc.Get("Settings.Advanced.Debug.PrintPerf")))
                    _performanceCollector.PrintPerformanceStats();
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, Loc.Get("Settings.Advanced.Debug.PrintPerf60")))
                    _performanceCollector.PrintPerformanceStats(60);
            }

            ImGuiHelpers.ScaledDummy(2f);

            if (ImGui.TreeNode(Loc.Get("Settings.Advanced.Debug.ActiveBlocks")))
            {
                var onlinePairs = _pairManager.GetOnlineUserPairs();
                foreach (var pair in onlinePairs)
                {
                    if (pair.IsApplicationBlocked)
                    {
                        ImGui.TextUnformatted(pair.PlayerName);
                        ImGui.SameLine();
                        ImGui.TextUnformatted(string.Join(", ", pair.HoldApplicationReasons));
                    }
                }
                ImGui.TreePop();
            }
        });
    }

    private void DrawFileStorageSettings()
    {
        _lastTab = "FileCache";
        DrawSectionHeader(2);

        UiSharedService.TextWrapped("Umbra stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
            "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        _uiShared.DrawFileScanState();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Umbra Storage Folder: " + (_cacheMonitor.MareWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.MareWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("mareMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartMareWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.MareWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, "Resume Monitoring"))
            {
                _cacheMonitor.StartMareWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            UiSharedService.AttachToolTip("Attempts to resume monitoring for both Penumbra and Umbra Storage. "
                + "Resuming the monitoring will also force a full scan to run." + Environment.NewLine
                + "If the button remains present after clicking it, consult /xllog for errors");
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            UiSharedService.AttachToolTip("Stops the monitoring for both Penumbra and Umbra Storage. "
                + "Do not stop the monitoring, unless you plan to move the Penumbra and Umbra Storage folders, to ensure correct functionality of Umbra." + Environment.NewLine
                + "If you stop the monitoring to move folders around, resume it after you are finished moving the files."
                + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
        }

        _uiShared.DrawCacheDirectorySetting();
        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            ImGui.TextUnformatted($"Currently utilized local storage: {_cacheMonitor.FileCacheSize / 1024.0 / 1024.0 / 1024.0:0.00} GiB");
        else
            ImGui.TextUnformatted($"Currently utilized local storage: Calculating...");
        bool isLinux = _dalamudUtilService.IsWine;
        if (!isLinux)
            ImGui.TextUnformatted($"Remaining space free on drive: {_cacheMonitor.FileCacheDriveFree / 1024.0 / 1024.0 / 1024.0:0.00} GiB");
        bool useFileCompactor = _configService.Current.UseCompactor;
        if (!useFileCompactor && !isLinux)
        {
            UiSharedService.ColorTextWrapped("Hint: To free up space when using Umbra consider enabling the File Compactor", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Use file compactor", ref useFileCompactor))
        {
            _configService.Current.UseCompactor = useFileCompactor;
            _configService.Save();
        }
        _uiShared.DrawHelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
            + "It is recommended to leave it enabled to save on space.");

        if (!_fileCompactor.MassCompactRunning)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.FileArchive, "Compact all files in storage"))
            {
                _ = Task.Run(() =>
                {
                    _fileCompactor.CompactStorage(compress: true);
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            UiSharedService.AttachToolTip("This will run compression on all files in your current storage folder." + Environment.NewLine
                + "You do not need to run this manually if you keep the file compactor enabled.");
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.File, "Decompact all files in storage"))
            {
                _ = Task.Run(() =>
                {
                    _fileCompactor.CompactStorage(compress: false);
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            UiSharedService.AttachToolTip("This will run decompression on all files in your current storage folder.");
        }
        else
        {
            UiSharedService.ColorText($"File compactor currently running ({_fileCompactor.Progress})", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.EndDisabled();
            ImGui.TextUnformatted("The file compactor is only available on Windows and NTFS drives.");
        }
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        ImGui.Separator();
        UiSharedService.TextWrapped("File Storage validation can make sure that all files in your local storage folder are valid. " +
            "Run the validation before you clear the Storage for no reason. " + Environment.NewLine +
            "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
            {
                _validationCts?.Cancel();
                _validationCts?.Dispose();
                _validationCts = new();
                var token = _validationCts.Token;
                _validationTask = Task.Run(() => _fileCacheManager.ValidateLocalIntegrity(_validationProgress, token));
            }
        }
        if (_validationTask != null && !_validationTask.IsCompleted)
        {
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _validationCts?.Cancel();
            }
        }

        if (_validationTask != null)
        {
            using (ImRaii.PushIndent(20f))
            {
                if (_validationTask.IsCompleted)
                {
                    UiSharedService.TextWrapped($"The storage validation has completed and removed {_validationTask.Result.Count} invalid files from storage.");
                }
                else
                {

                    UiSharedService.TextWrapped($"Storage validation is running: {_currentProgress.Item1}/{_currentProgress.Item2}");
                    UiSharedService.TextWrapped($"Current item: {_currentProgress.Item3.ResolvedFilepath}");
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted("To clear the local storage accept the following disclaimer");
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        UiSharedService.TextWrapped("I understand that: " + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
            + Environment.NewLine + "- This is not a step to try to fix sync issues."
            + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load.");
        if (!_readClearCache)
            ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Clear local storage") && UiSharedService.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                {
                    File.Delete(file);
                }
            });
        }
        UiSharedService.AttachToolTip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
            + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
            + "Umbra's storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
            + "If you still think you need to do this hold CTRL while pressing the button.");
        if (!_readClearCache)
            ImGui.EndDisabled();
        ImGui.Unindent();
    }

    private void DrawGeneral()
    {
        if (!string.Equals(_lastTab, "General", StringComparison.OrdinalIgnoreCase))
        {
            _notesSuccessfullyApplied = null;
        }

        _lastTab = "General";
        DrawSectionHeader(0);

        _uiShared.BigText("Notes");
        if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Export all your user notes to clipboard"))
        {
            ImGui.SetClipboardText(UiSharedService.GetNotes(_pairManager.DirectPairs.UnionBy(_pairManager.GroupPairs.SelectMany(p => p.Value), p => p.UserData, UserDataComparer.Instance).ToList()));
        }
        if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, "Import notes from clipboard"))
        {
            _notesSuccessfullyApplied = null;
            var notes = ImGui.GetClipboardText();
            _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, _overwriteExistingLabels);
        }

        ImGui.SameLine();
        ImGui.Checkbox("Overwrite existing notes", ref _overwriteExistingLabels);
        _uiShared.DrawHelpText("If this option is selected all already existing notes for UIDs will be overwritten by the imported notes.");
        if (_notesSuccessfullyApplied.HasValue && _notesSuccessfullyApplied.Value)
        {
            UiSharedService.ColorTextWrapped("User Notes successfully imported", UiSharedService.AccentColor);
        }
        else if (_notesSuccessfullyApplied.HasValue && !_notesSuccessfullyApplied.Value)
        {
            UiSharedService.ColorTextWrapped("Attempt to import notes from clipboard failed. Check formatting and try again", UiSharedService.AccentColor);
        }

        var openPopupOnAddition = _configService.Current.OpenPopupOnAdd;

        if (ImGui.Checkbox("Open Notes Popup on user addition", ref openPopupOnAddition))
        {
            _configService.Current.OpenPopupOnAdd = openPopupOnAddition;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will open a popup that allows you to set the notes for a user after successfully adding them to your individual pairs.");

        ImGui.Separator();
        _uiShared.BigText("UI");
        var selectedLanguage = _configService.Current.UiLanguage;
        if (!Loc.IsLanguageAvailable(selectedLanguage))
        {
            selectedLanguage = Loc.CurrentLanguage;
            _configService.Current.UiLanguage = selectedLanguage;
            _configService.Save();
        }

        var languageLabel = Loc.GetLanguageDisplayName(selectedLanguage);
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.BeginCombo("Interface Language##uiLanguage", string.IsNullOrEmpty(languageLabel) ? selectedLanguage : languageLabel))
        {
            foreach (var option in Loc.AvailableLanguages)
            {
                bool isSelected = string.Equals(option.Key, selectedLanguage, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(option.Value, isSelected))
                {
                    _configService.Current.UiLanguage = option.Key;
                    _configService.Save();
                    Loc.SetLanguage(option.Key);
                    selectedLanguage = option.Key;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
        _uiShared.DrawHelpText("Select the language used for Umbra's UI. Missing text falls back to English.");
        ImGuiHelpers.ScaledDummy(3f);

        var showCharacterNames = _configService.Current.ShowCharacterNames;
        var showVisibleSeparate = _configService.Current.ShowVisibleUsersSeparately;
        var showOfflineSeparate = _configService.Current.ShowOfflineUsersSeparately;
        var showProfiles = _configService.Current.ProfilesShow;
        var showNsfwProfiles = _configService.Current.ProfilesAllowNsfw;
        var showRpNsfwProfiles = _configService.Current.ProfilesAllowRpNsfw;
        var profileDelay = _configService.Current.ProfileDelay;
        var profileOnRight = _configService.Current.ProfilePopoutRight;
        var enableRightClickMenu = _configService.Current.EnableRightClickMenus;
        var enableDtrEntry = _configService.Current.EnableDtrEntry;
        var showUidInDtrTooltip = _configService.Current.ShowUidInDtrTooltip;
        var preferNoteInDtrTooltip = _configService.Current.PreferNoteInDtrTooltip;
        var useColorsInDtr = _configService.Current.UseColorsInDtr;
        var dtrColorsDefault = _configService.Current.DtrColorsDefault;
        var dtrColorsNotConnected = _configService.Current.DtrColorsNotConnected;
        var dtrColorsPairsInRange = _configService.Current.DtrColorsPairsInRange;

        if (ImGui.Checkbox("Enable Game Right Click Menu Entries", ref enableRightClickMenu))
        {
            _configService.Current.EnableRightClickMenus = enableRightClickMenu;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will add Umbra related right click menu entries in the game UI on paired players.");

        if (ImGui.Checkbox("Display status and visible pair count in Server Info Bar", ref enableDtrEntry))
        {
            _configService.Current.EnableDtrEntry = enableDtrEntry;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will add Umbra connection status and visible pair count in the Server Info Bar.\nYou can further configure this through your Dalamud Settings.");

        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Show visible character's UID in tooltip", ref showUidInDtrTooltip))
            {
                _configService.Current.ShowUidInDtrTooltip = showUidInDtrTooltip;
                _configService.Save();
            }

            if (ImGui.Checkbox("Prefer notes over player names in tooltip", ref preferNoteInDtrTooltip))
            {
                _configService.Current.PreferNoteInDtrTooltip = preferNoteInDtrTooltip;
                _configService.Save();
            }

            DrawDtrStyleCombo();

            if (ImGui.Checkbox("Color-code the Server Info Bar entry according to status", ref useColorsInDtr))
            {
                _configService.Current.UseColorsInDtr = useColorsInDtr;
                _configService.Save();
            }

            using (ImRaii.Disabled(!useColorsInDtr))
            {
                using var indent2 = ImRaii.PushIndent();
                if (InputDtrColors("Default", ref dtrColorsDefault))
                {
                    _configService.Current.DtrColorsDefault = dtrColorsDefault;
                    _configService.Save();
                }

                ImGui.SameLine();
                if (InputDtrColors("Not Connected", ref dtrColorsNotConnected))
                {
                    _configService.Current.DtrColorsNotConnected = dtrColorsNotConnected;
                    _configService.Save();
                }

                ImGui.SameLine();
                if (InputDtrColors("Pairs in Range", ref dtrColorsPairsInRange))
                {
                    _configService.Current.DtrColorsPairsInRange = dtrColorsPairsInRange;
                    _configService.Save();
                }
            }
        }

        var useNameColors = _configService.Current.UseNameColors;
        var nameColors = _configService.Current.NameColors;
        var autoPausedNameColors = _configService.Current.BlockedNameColors;
        if (ImGui.Checkbox("Coloriser les plaques de nom des paires", ref useNameColors))
        {
            _configService.Current.UseNameColors = useNameColors;
            _configService.Save();
            _guiHookService.RequestRedraw();
        }

        using (ImRaii.Disabled(!useNameColors))
        {
            using var indent = ImRaii.PushIndent();
            if (InputDtrColors("Couleur du nom", ref nameColors))
            {
                _configService.Current.NameColors = nameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }

            ImGui.SameLine();

            if (InputDtrColors(Loc.Get("Settings.Typing.BlockedNameColor"), ref autoPausedNameColors))
            {
                _configService.Current.BlockedNameColors = autoPausedNameColors;
                _configService.Save();
                _guiHookService.RequestRedraw();
            }
        }

        if (ImGui.Checkbox("Show separate Visible group", ref showVisibleSeparate))
        {
            _configService.Current.ShowVisibleUsersSeparately = showVisibleSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show all currently visible users in a special 'Visible' group in the main UI.");

        if (ImGui.Checkbox("Show separate Offline group", ref showOfflineSeparate))
        {
            _configService.Current.ShowOfflineUsersSeparately = showOfflineSeparate;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show all currently offline users in a special 'Offline' group in the main UI.");

        if (ImGui.Checkbox("Show player names", ref showCharacterNames))
        {
            _configService.Current.ShowCharacterNames = showCharacterNames;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show character names instead of UIDs when possible");

        if (ImGui.Checkbox("Show Profiles on Hover", ref showProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesShow = showProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show the configured user profile after a set delay");
        ImGui.Indent();
        if (!showProfiles) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Popout profiles on the right", ref profileOnRight))
        {
            _configService.Current.ProfilePopoutRight = profileOnRight;
            _configService.Save();
            Mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
        }
        _uiShared.DrawHelpText("Will show profiles on the right side of the main UI");
        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        if (ImGui.SliderFloat("Hover Delay", ref profileDelay, 1, 10))
        {
            _configService.Current.ProfileDelay = profileDelay;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Delay until the profile should be displayed");
        if (!showProfiles) ImGui.EndDisabled();
        ImGui.Unindent();
        if (ImGui.Checkbox("Show profiles marked as NSFW", ref showNsfwProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Will show profiles that have the NSFW tag enabled");

        if (ImGui.Checkbox("Show RP profiles marked as NSFW", ref showRpNsfwProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesAllowRpNsfw = showRpNsfwProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Will show RP profiles that have the RP NSFW tag enabled");

        ImGui.Separator();

        var disableOptionalPluginWarnings = _configService.Current.DisableOptionalPluginWarnings;
        var onlineNotifs = _configService.Current.ShowOnlineNotifications;
        var onlineNotifsPairsOnly = _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs;
        var onlineNotifsNamedOnly = _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs;
        _uiShared.BigText("Notifications");

        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        _uiShared.DrawCombo("Info Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            _configService.Current.InfoNotification = i;
            _configService.Save();
        }, _configService.Current.InfoNotification);
        _uiShared.DrawHelpText("The location where \"Info\" notifications will display."
                      + Environment.NewLine + "'Nowhere' will not show any Info notifications"
                      + Environment.NewLine + "'Chat' will print Info notifications in chat"
                      + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                      + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        _uiShared.DrawCombo("Warning Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            _configService.Current.WarningNotification = i;
            _configService.Save();
        }, _configService.Current.WarningNotification);
        _uiShared.DrawHelpText("The location where \"Warning\" notifications will display."
                              + Environment.NewLine + "'Nowhere' will not show any Warning notifications"
                              + Environment.NewLine + "'Chat' will print Warning notifications in chat"
                              + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
                              + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        _uiShared.DrawCombo("Error Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            _configService.Current.ErrorNotification = i;
            _configService.Save();
        }, _configService.Current.ErrorNotification);
        _uiShared.DrawHelpText("The location where \"Error\" notifications will display."
                              + Environment.NewLine + "'Nowhere' will not show any Error notifications"
                              + Environment.NewLine + "'Chat' will print Error notifications in chat"
                              + Environment.NewLine + "'Toast' will show Error toast notifications in the bottom right corner"
                              + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        if (ImGui.Checkbox("Disable optional plugin warnings", ref disableOptionalPluginWarnings))
        {
            _configService.Current.DisableOptionalPluginWarnings = disableOptionalPluginWarnings;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this will not show any \"Warning\" labeled messages for missing optional plugins.");
        if (ImGui.Checkbox("Enable online notifications", ref onlineNotifs))
        {
            _configService.Current.ShowOnlineNotifications = onlineNotifs;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this will show a small notification (type: Info) in the bottom right corner when pairs go online.");

        using (ImRaii.Disabled(!onlineNotifs))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Notify only for individual pairs", ref onlineNotifsPairsOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Enabling this will only show online notifications (type: Info) for individual pairs.");
            if (ImGui.Checkbox("Notify only for named pairs", ref onlineNotifsNamedOnly))
            {
                _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Enabling this will only show online notifications (type: Info) for pairs where you have set an individual note.");
        }
    }

    private bool _perfUnapplied = false;

    private void DrawPerformance()
    {
        DrawSectionHeader(1);

        bool recalculatePerformance = false;
        string? recalculatePerformanceUID = null;

        _uiShared.BigText("Global Configuration");

        bool showSelfAnalysisWarnings = _playerPerformanceConfigService.Current.ShowSelfAnalysisWarnings;
        if (ImGui.Checkbox("Display self-analysis warnings", ref showSelfAnalysisWarnings))
        {
            _playerPerformanceConfigService.Current.ShowSelfAnalysisWarnings = showSelfAnalysisWarnings;
            _playerPerformanceConfigService.Save();
        }
        _uiShared.DrawHelpText("Disable to suppress UmbraSync chat warnings when your character exceeds the self-analysis thresholds.");

        bool alwaysShrinkTextures = _playerPerformanceConfigService.Current.TextureShrinkMode == TextureShrinkMode.Always;
        bool deleteOriginalTextures = _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal;

        using (ImRaii.Disabled(deleteOriginalTextures))
        {
            if (ImGui.Checkbox("Shrink downloaded textures", ref alwaysShrinkTextures))
            {
                if (alwaysShrinkTextures)
                    _playerPerformanceConfigService.Current.TextureShrinkMode = TextureShrinkMode.Always;
                else
                    _playerPerformanceConfigService.Current.TextureShrinkMode = TextureShrinkMode.Never;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
                _cacheMonitor.ClearSubstStorage();
            }
        }
        _uiShared.DrawHelpText("Automatically shrinks texture resolution of synced players to reduce VRAM utilization." + UiSharedService.TooltipSeparator
            + "Texture Size Limit (DXT/BC5/BC7 Compressed): 2048x2048" + Environment.NewLine
            + "Texture Size Limit (A8R8G8B8 Uncompressed): 1024x1024" + UiSharedService.TooltipSeparator
            + "Enable to reduce lag in large crowds." + Environment.NewLine
            + "Disable this for higher quality during GPose.");

        using (ImRaii.Disabled(!alwaysShrinkTextures || _cacheMonitor.FileCacheSize < 0))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Delete original textures from disk", ref deleteOriginalTextures))
            {
                _playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal = deleteOriginalTextures;
                _playerPerformanceConfigService.Save();
                _ = Task.Run(() =>
                {
                    _cacheMonitor.DeleteSubstOriginals();
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            _uiShared.DrawHelpText("Deletes original, full-sized, textures from disk after downloading and shrinking." + UiSharedService.TooltipSeparator
                + "Caution!!! This will cause a re-download of all textures when the shrink option is disabled.");
        }

        var totalVramBytes = _pairManager.GetOnlineUserPairs().Where(p => p.IsVisible && p.LastAppliedApproximateVRAMBytes > 0).Sum(p => p.LastAppliedApproximateVRAMBytes);

        ImGui.TextUnformatted("Current VRAM utilization by all nearby players:");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, totalVramBytes < 2.0 * 1024.0 * 1024.0 * 1024.0))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, totalVramBytes >= 4.0 * 1024.0 * 1024.0 * 1024.0))
        using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor, totalVramBytes >= 6.0 * 1024.0 * 1024.0 * 1024.0))
            ImGui.TextUnformatted($"{totalVramBytes / 1024.0 / 1024.0 / 1024.0:0.00} GiB");

        ImGui.Separator();
        _uiShared.BigText("Individual Limits");
        bool autoPause = _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds;
        if (ImGui.Checkbox("Automatically block players exceeding thresholds", ref autoPause))
        {
            _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText("When enabled, it will automatically block the modded appearance of all players that exceed the thresholds defined below." + Environment.NewLine
            + "Will print a warning in chat when a player is blocked automatically.");
        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            var notifyDirectPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs;
            var notifyGroupPairs = _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs;
            if (ImGui.Checkbox("Display auto-block warnings for individual pairs", ref notifyDirectPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseDirectPairs = notifyDirectPairs;
                _playerPerformanceConfigService.Save();
            }
            if (ImGui.Checkbox("Display auto-block warnings for syncshell pairs", ref notifyGroupPairs))
            {
                _playerPerformanceConfigService.Current.NotifyAutoPauseGroupPairs = notifyGroupPairs;
                _playerPerformanceConfigService.Save();
            }
            var vramAuto = _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            var trisAuto = _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(MathF.Min(100 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.3f));
            if (ImGui.InputInt("Auto Block VRAM threshold", ref vramAuto))
            {
                _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            _uiShared.DrawHelpText("When a loading in player and their VRAM usage exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 550 MiB");
            ImGui.SetNextItemWidth(MathF.Min(100 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X * 0.3f));
            if (ImGui.InputInt("Auto Block Triangle threshold", ref trisAuto))
            {
                _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                _playerPerformanceConfigService.Save();
                _perfUnapplied = true;
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            _uiShared.DrawHelpText("When a loading in player and their triangle count exceeds this amount, automatically blocks the synced player." + UiSharedService.TooltipSeparator
                + "Default: 375 thousand");
            using (ImRaii.Disabled(!_perfUnapplied))
            {
                if (ImGui.Button("Apply Changes Now"))
                {
                    recalculatePerformance = true;
                    _perfUnapplied = false;
                }
            }
        }

        #region Whitelist
        ImGui.Separator();
        _uiShared.BigText("Whitelisted UIDs");
        bool ignoreDirectPairs = _playerPerformanceConfigService.Current.IgnoreDirectPairs;
        if (ImGui.Checkbox("Whitelist all individual pairs", ref ignoreDirectPairs))
        {
            _playerPerformanceConfigService.Current.IgnoreDirectPairs = ignoreDirectPairs;
            _playerPerformanceConfigService.Save();
            recalculatePerformance = true;
        }
        _uiShared.DrawHelpText("Individual pairs will never be affected by auto blocks.");
        ImGui.Dummy(new Vector2(5));
        UiSharedService.TextWrapped("The entries in the list below will be not have auto block thresholds enforced.");
        var whitelistAvail = ImGui.GetContentRegionAvail().X;
        float whitelistRightCol = MathF.Min(240 * ImGuiHelpers.GlobalScale, whitelistAvail * 0.55f);
        ImGui.SetNextItemWidth(MathF.Min(200 * ImGuiHelpers.GlobalScale, whitelistAvail * 0.5f));
        var whitelistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(whitelistRightCol);
        ImGui.InputText("##whitelistuid", ref _uidToAddForIgnore, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            ImGui.SetCursorPosX(whitelistRightCol);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to whitelist"))
            {
                if (!_serverConfigurationManager.IsUidWhitelisted(_uidToAddForIgnore))
                {
                    _serverConfigurationManager.AddWhitelistUid(_uidToAddForIgnore);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnore;
                }
                _uidToAddForIgnore = string.Empty;
            }
        }
        ImGui.SetCursorPosX(whitelistRightCol);
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));
        var playerList = _serverConfigurationManager.Whitelist;
        if (_selectedEntry > playerList.Count - 1)
            _selectedEntry = -1;
        ImGui.SetNextItemWidth(MathF.Min(200 * ImGuiHelpers.GlobalScale, whitelistAvail * 0.5f));
        ImGui.SetCursorPosY(whitelistPos.Y);
        using (var lb = ImRaii.ListBox("##whitelist"))
        {
            if (lb)
            {
                for (int i = 0; i < playerList.Count; i++)
                {
                    bool shouldBeSelected = _selectedEntry == i;
                    if (ImGui.Selectable(playerList[i] + "##" + i, shouldBeSelected))
                    {
                        _selectedEntry = i;
                    }
                    string? lastSeenName = _serverConfigurationManager.GetNameForUid(playerList[i]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip($"Last seen name: {lastSeenName}");
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedWhitelist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _serverConfigurationManager.RemoveWhitelistUid(_serverConfigurationManager.Whitelist[_selectedEntry]);
                if (_selectedEntry > playerList.Count - 1)
                    --_selectedEntry;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
            }
        }
        #endregion Whitelist

        #region Blacklist
        ImGui.Separator();
        _uiShared.BigText("Blacklisted UIDs");
        UiSharedService.TextWrapped("The entries in the list below will never have their characters displayed.");
        var blacklistAvail = ImGui.GetContentRegionAvail().X;
        float blacklistRightCol = MathF.Min(240 * ImGuiHelpers.GlobalScale, blacklistAvail * 0.55f);
        ImGui.SetNextItemWidth(MathF.Min(200 * ImGuiHelpers.GlobalScale, blacklistAvail * 0.5f));
        var blacklistPos = ImGui.GetCursorPos();
        ImGui.SetCursorPosX(blacklistRightCol);
        ImGui.InputText("##uid", ref _uidToAddForIgnoreBlacklist, 20);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnoreBlacklist)))
        {
            ImGui.SetCursorPosX(blacklistRightCol);
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to blacklist"))
            {
                if (!_serverConfigurationManager.IsUidBlacklisted(_uidToAddForIgnoreBlacklist))
                {
                    _serverConfigurationManager.AddBlacklistUid(_uidToAddForIgnoreBlacklist);
                    recalculatePerformance = true;
                    recalculatePerformanceUID = _uidToAddForIgnoreBlacklist;
                }
                _uidToAddForIgnoreBlacklist = string.Empty;
            }
        }
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive.\nVanity IDs are also acceptable.");
        ImGui.Dummy(new Vector2(10));
        var blacklist = _serverConfigurationManager.Blacklist;
        if (_selectedEntryBlacklist > blacklist.Count - 1)
            _selectedEntryBlacklist = -1;
        ImGui.SetNextItemWidth(MathF.Min(200 * ImGuiHelpers.GlobalScale, blacklistAvail * 0.5f));
        ImGui.SetCursorPosY(blacklistPos.Y);
        using (var lb = ImRaii.ListBox("##blacklist"))
        {
            if (lb)
            {
                for (int i = 0; i < blacklist.Count; i++)
                {
                    bool shouldBeSelected = _selectedEntryBlacklist == i;
                    if (ImGui.Selectable(blacklist[i] + "##BL" + i, shouldBeSelected))
                    {
                        _selectedEntryBlacklist = i;
                    }
                    string? lastSeenName = _serverConfigurationManager.GetNameForUid(blacklist[i]);
                    if (lastSeenName != null)
                    {
                        ImGui.SameLine();
                        _uiShared.IconText(FontAwesomeIcon.InfoCircle);
                        UiSharedService.AttachToolTip($"Last seen name: {lastSeenName}");
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntryBlacklist == -1))
        {
            using var pushId = ImRaii.PushId("deleteSelectedBlacklist");
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _serverConfigurationManager.RemoveBlacklistUid(_serverConfigurationManager.Blacklist[_selectedEntryBlacklist]);
                if (_selectedEntryBlacklist > blacklist.Count - 1)
                    --_selectedEntryBlacklist;
                _playerPerformanceConfigService.Save();
                recalculatePerformance = true;
            }
        }
        #endregion Blacklist

        if (recalculatePerformance)
            Mediator.Publish(new RecalculatePerformanceMessage(recalculatePerformanceUID));
    }

    private static bool InputDtrColors(string label, ref DtrEntry.Colors colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Foreground Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Glow Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (ret)
            colors = new(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));

        return ret;

        static Vector3 ConvertColor(uint color)
            => unchecked(new((byte)color / 255.0f, (byte)(color >> 8) / 255.0f, (byte)(color >> 16) / 255.0f));

        static uint ConvertBackColor(Vector3 color)
            => byte.CreateSaturating(color.X * 255.0f) | ((uint)byte.CreateSaturating(color.Y * 255.0f) << 8) | ((uint)byte.CreateSaturating(color.Z * 255.0f) << 16);
    }

    private void DrawServerConfiguration()
    {
        _lastTab = "Compte";
        DrawSectionHeader(6);

        var idx = _serverConfigurationManager.CurrentServerIndex;
        var playerName = _dalamudUtilService.GetPlayerName();
        var playerWorldId = _dalamudUtilService.GetHomeWorldId();
        var worldData = _uiShared.WorldData.OrderBy(u => u.Value, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
        string playerWorldName = worldData.GetValueOrDefault((ushort)playerWorldId, $"{playerWorldId}");

        var selectedServer = _serverConfigurationManager.GetServerByIndex(idx);

            Vector4 accent = UiSharedService.AccentColor;
            if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

            var labels = new[] { Loc.Get("Settings.Account.Tab.Characters"), Loc.Get("Settings.Account.Tab.SecretKeys") };
            var icons = new[] { FontAwesomeIcon.Users, FontAwesomeIcon.Key };
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

            for (int t = 0; t < labels.Length; t++)
            {
                if (t > 0) ImGui.SameLine(0, btnSpacing);

                var p = ImGui.GetCursorScreenPos();
                bool clicked = ImGui.InvisibleButton($"##accountTab_{t}", new Vector2(btnW, btnH));
                bool hovered = ImGui.IsItemHovered();
                bool isActive = _serverConfigTab == t;

                var bg = isActive ? accent : hovered ? hoverBg : bgColor;
                dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
                if (!isActive)
                    dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

                ImGui.PushFont(UiBuilder.IconFont);
                var iconStr = icons[t].ToIconString();
                var iconSz = ImGui.CalcTextSize(iconStr);
                ImGui.PopFont();

                var labelSz = ImGui.CalcTextSize(labels[t]);
                var totalW = iconSz.X + iconTextGap + labelSz.X;
                var startX = p.X + (btnW - totalW) / 2f;

                var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
                var textColorU32 = ImGui.GetColorU32(textColor);

                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new Vector2(startX, p.Y + (btnH - iconSz.Y) / 2f), textColorU32, iconStr);
                ImGui.PopFont();

                dl.AddText(new Vector2(startX + iconSz.X + iconTextGap, p.Y + (btnH - labelSz.Y) / 2f), textColorU32, labels[t]);

                if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (clicked) _serverConfigTab = t;
            }

            ImGuiHelpers.ScaledDummy(4f);

            if (_serverConfigTab == 0)
            {
                if (selectedServer.SecretKeys.Count > 0)
                {
                    int i = 0;
                    foreach (var item in selectedServer.Authentications.ToList())
                    {
                        bool thisIsYou = string.Equals(playerName, item.CharacterName, StringComparison.OrdinalIgnoreCase)
                            && playerWorldId == item.WorldId;

                        if (!worldData.TryGetValue((ushort)item.WorldId, out string? worldPreview))
                            worldPreview = worldData.First().Value;

                        UiSharedService.DrawCard($"chara-{i}", () =>
                        {
                            using var charaId = ImRaii.PushId("selectedChara" + i);
                            var availWidth = ImGui.GetContentRegionAvail().X;

                            // Character name row
                            _uiShared.IconText(thisIsYou ? FontAwesomeIcon.Star : FontAwesomeIcon.User);
                            if (thisIsYou)
                                UiSharedService.AttachToolTip(Loc.Get("Settings.Account.Characters.Current"));
                            ImGui.SameLine();
                            ImGui.TextUnformatted($"{item.CharacterName} @ {worldPreview}");

                            // Key selector
                            _uiShared.IconText(FontAwesomeIcon.Key);
                            ImGui.SameLine();
                            var comboWidth = availWidth - ImGui.GetCursorPosX() + ImGui.GetWindowContentRegionMin().X;
                            ImGui.SetNextItemWidth(comboWidth);

                            string selectedKeyName = string.Empty;
                            if (selectedServer.SecretKeys.TryGetValue(item.SecretKeyIdx, out var selectedKey))
                                selectedKeyName = selectedKey.FriendlyName;
                            if (ImGui.BeginCombo($"##combo{i}", selectedKeyName))
                            {
                                foreach (var key in selectedServer.SecretKeys)
                                {
                                    if (ImGui.Selectable($"{key.Value.FriendlyName}##{i}", key.Key == item.SecretKeyIdx)
                                        && key.Key != item.SecretKeyIdx)
                                    {
                                        item.SecretKeyIdx = key.Key;
                                        _serverConfigurationManager.Save();
                                    }
                                }
                                ImGui.EndCombo();
                            }

                            // Delete button
                            ImGuiHelpers.ScaledDummy(2f);
                            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Settings.Account.Characters.DeleteAssignment")))
                                _serverConfigurationManager.RemoveCharacterFromServer(idx, item);
                        });

                        ImGuiHelpers.ScaledDummy(2f);
                        i++;
                    }

                    using (_ = ImRaii.Disabled(selectedServer.Authentications.Exists(c =>
                            string.Equals(c.CharacterName, _uiShared.PlayerName, StringComparison.Ordinal)
                                && c.WorldId == _uiShared.WorldId
                    )))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.UserPlus, Loc.Get("Settings.Account.Characters.AddCurrent")))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer(idx);
                        }
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("Settings.Account.Characters.NeedKey"), ImGuiColors.DalamudYellow);
                }

            }

            if (_serverConfigTab == 1)
            {
                foreach (var item in selectedServer.SecretKeys.ToList())
                {
                    var keyInUse = selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key);

                    UiSharedService.DrawCard($"secret-key-{item.Key}", () =>
                    {
                        using var id = ImRaii.PushId("key" + item.Key);
                        var availWidth = ImGui.GetContentRegionAvail().X;

                        // Header: name + lock icon
                        var friendlyName = item.Value.FriendlyName;
                        _uiShared.IconText(keyInUse ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen);
                        if (keyInUse)
                            UiSharedService.AttachToolTip(Loc.Get("Settings.Account.Keys.InUseWarning"));
                        ImGui.SameLine();
                        var inputWidth = availWidth - ImGui.GetCursorPosX() + ImGui.GetWindowContentRegionMin().X;
                        ImGui.SetNextItemWidth(inputWidth);
                        if (ImGui.InputText("##name", ref friendlyName, 255))
                        {
                            item.Value.FriendlyName = friendlyName;
                            _serverConfigurationManager.Save();
                        }

                        // Secret key (revealed on hover)
                        var key = item.Value.Key;
                        bool isRevealed = _hoveredSecretKeys.Contains(item.Key);
                        _uiShared.IconText(FontAwesomeIcon.Key);
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(inputWidth);
                        var flags = isRevealed ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
                        if (keyInUse) flags |= ImGuiInputTextFlags.ReadOnly;
                        if (ImGui.InputText("##secret", ref key, 64, flags))
                        {
                            item.Value.Key = key;
                            _serverConfigurationManager.Save();
                        }
                        if (ImGui.IsItemHovered())
                            _hoveredSecretKeys.Add(item.Key);
                        else
                            _hoveredSecretKeys.Remove(item.Key);

                        // Action buttons row
                        ImGuiHelpers.ScaledDummy(2f);
                        bool thisIsYou = selectedServer.Authentications.Any(a =>
                            a.SecretKeyIdx == item.Key
                                && string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                                && a.WorldId == playerWorldId
                        );
                        bool disableAssignment = thisIsYou || item.Value.Key.IsNullOrEmpty();

                        using (_ = ImRaii.Disabled(disableAssignment))
                        {
                            if (_uiShared.IconTextButton(FontAwesomeIcon.User, Loc.Get("Settings.Account.Keys.AssignCurrent")))
                            {
                                var currentAssignment = selectedServer.Authentications.Find(a =>
                                    string.Equals(a.CharacterName, _uiShared.PlayerName, StringComparison.OrdinalIgnoreCase)
                                        && a.WorldId == playerWorldId
                                );
                                if (currentAssignment == null)
                                {
                                    selectedServer.Authentications.Add(new Authentication()
                                    {
                                        CharacterName = playerName,
                                        WorldId = playerWorldId,
                                        SecretKeyIdx = item.Key
                                    });
                                }
                                else
                                {
                                    currentAssignment.SecretKeyIdx = item.Key;
                                }
                            }
                            if (!disableAssignment)
                                UiSharedService.AttachToolTip(string.Format(Loc.Get("Settings.Account.Keys.UseKeyFor"), playerName, playerWorldName));
                            else if (thisIsYou)
                                UiSharedService.AttachToolTip(Loc.Get("Settings.Account.Characters.Current"));
                        }

                        ImGui.SameLine();
                        if (_uiShared.IconTextButton(FontAwesomeIcon.FileExport, Loc.Get("Settings.Account.Keys.ExportOne")))
                        {
                            var singleKey = item.Value;
                            var safeName = string.IsNullOrWhiteSpace(singleKey.FriendlyName) ? "key" : singleKey.FriendlyName.Replace(" ", "_");
                            _uiShared.FileDialogManager.SaveFileDialog(Loc.Get("Settings.Account.Keys.ExportOne.Dialog"), ".json",
                                $"umbra-key-{safeName}.json", ".json", (success, path) =>
                            {
                                if (!success) return;
                                var exportData = new[] { new { singleKey.FriendlyName, singleKey.Key } };
                                File.WriteAllText(path, JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true }));
                            });
                        }

                        ImGui.SameLine();
                        using (_ = ImRaii.Disabled(keyInUse))
                        {
                            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Settings.Account.Keys.Delete")) && UiSharedService.CtrlPressed())
                            {
                                selectedServer.SecretKeys.Remove(item.Key);
                                _serverConfigurationManager.Save();
                            }
                            if (!keyInUse)
                                UiSharedService.AttachToolTip(Loc.Get("Settings.Account.Keys.DeleteHelp"));
                        }
                    });

                    ImGuiHelpers.ScaledDummy(2f);
                }

                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("Settings.Account.Keys.AddNew")))
                {
                    selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                    {
                        FriendlyName = Loc.Get("Settings.Account.Keys.NewKeyName"),
                    });
                    _serverConfigurationManager.Save();
                }

                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("Settings.Account.Register")))
                {
                    _registrationInProgress = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var reply = await _registerService.RegisterAccount(CancellationToken.None).ConfigureAwait(false);
                            if (!reply.Success)
                            {
                                _logger.LogWarning("Registration failed: {err}", reply.ErrorMessage);
                                _registrationMessage = reply.ErrorMessage;
                                if (_registrationMessage.IsNullOrEmpty())
                                    _registrationMessage = Loc.Get("Settings.Account.Register.Error");
                                return;
                            }
                            _registrationMessage = Loc.Get("Settings.Account.Register.Success");
                            _registrationSuccess = true;
                            selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                            {
                                FriendlyName = reply.UID + $" (registered {DateTime.Now:yyyy-MM-dd})",
                                Key = reply.SecretKey
                            });
                            _serverConfigurationManager.Save();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Registration failed");
                            _registrationSuccess = false;
                            _registrationMessage = Loc.Get("Settings.Account.Register.Error");
                        }
                        finally
                        {
                            _registrationInProgress = false;
                        }
                    }, CancellationToken.None);
                }
                if (_registrationInProgress)
                {
                    ImGui.TextUnformatted(Loc.Get("Settings.Account.Register.Sending"));
                }
                else if (!_registrationMessage.IsNullOrEmpty())
                {
                    if (!_registrationSuccess)
                        ImGui.TextColored(ImGuiColors.DalamudYellow, _registrationMessage);
                    else
                        ImGui.TextWrapped(_registrationMessage);
                }

                ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(new Vector2(5, 5));

                if (_uiShared.IconTextButton(FontAwesomeIcon.FileExport, Loc.Get("Settings.Account.Keys.Export")))
                {
                    _uiShared.FileDialogManager.SaveFileDialog(Loc.Get("Settings.Account.Keys.Export.Dialog"), ".json",
                        "umbra-keys.json", ".json", (success, path) =>
                    {
                        if (!success) return;
                        var exportData = selectedServer.SecretKeys.Values.Select(k => new { k.FriendlyName, k.Key }).ToList();
                        File.WriteAllText(path, JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true }));
                    });
                }

                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.FileImport, Loc.Get("Settings.Account.Keys.Import")))
                {
                    _uiShared.FileDialogManager.OpenFileDialog(Loc.Get("Settings.Account.Keys.Import.Dialog"), ".json",
                        (success, paths) =>
                    {
                        if (!success) return;
                        if (paths.FirstOrDefault() is not string path) return;
                        try
                        {
                            var json = File.ReadAllText(path);
                            var parsed = JsonSerializer.Deserialize<List<SecretKey>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed == null || parsed.Count == 0)
                            {
                                _importResults = [(Loc.Get("Settings.Account.Keys.Import.InvalidFile"), false, "")];
                                _importResultPopupShown = true;
                                return;
                            }
                            _importResults = [];
                            foreach (var key in parsed)
                            {
                                if (string.IsNullOrWhiteSpace(key.Key))
                                {
                                    _importResults.Add((key.FriendlyName, false, Loc.Get("Settings.Account.Keys.Import.EmptyKey")));
                                    continue;
                                }
                                if (selectedServer.SecretKeys.Values.Any(k => string.Equals(k.Key, key.Key, StringComparison.Ordinal)))
                                {
                                    _importResults.Add((key.FriendlyName, false, Loc.Get("Settings.Account.Keys.Import.AlreadyExists")));
                                    continue;
                                }
                                var nextIdx = selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0;
                                selectedServer.SecretKeys.Add(nextIdx, new SecretKey { FriendlyName = key.FriendlyName, Key = key.Key });
                                _importResults.Add((key.FriendlyName, true, Loc.Get("Settings.Account.Keys.Import.Imported")));
                            }
                            _serverConfigurationManager.Save();
                            _importResultPopupShown = true;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to import secret keys");
                            _importResults = [(Loc.Get("Settings.Account.Keys.Import.InvalidFile"), false, "")];
                            _importResultPopupShown = true;
                        }
                    }, 1);
                }

                if (_importResultPopupShown)
                    ImGui.OpenPopup("###importResultPopup");

                if (ImGui.BeginPopupModal(Loc.Get("Settings.Account.Keys.Import.Result") + "###importResultPopup", ref _importResultPopupShown, UiSharedService.PopupWindowFlags))
                {
                    foreach (var (name, imported, reason) in _importResults)
                    {
                        var color = imported ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow;
                        ImGui.TextColored(color, imported ? "\uF00C" : "\uF00D");
                        ImGui.SameLine();
                        if (!string.IsNullOrEmpty(reason))
                            ImGui.TextUnformatted($"{name} — {reason}");
                        else
                            ImGui.TextUnformatted(name);
                    }

                    ImGui.Separator();
                    ImGui.Spacing();
                    if (ImGui.Button("OK", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                        _importResultPopupShown = false;

                    UiSharedService.SetScaledWindowSize(350);
                    ImGui.EndPopup();
                }

                if (ApiController.ServerAlive)
                {
                    ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(new Vector2(5, 5));

                    if (ImGui.Button(Loc.Get("Settings.Account.DeleteAccount")))
                    {
                        _deleteAccountPopupModalShown = true;
                        ImGui.OpenPopup("###deleteAccountPopup");
                    }

                    _uiShared.DrawHelpText(Loc.Get("Settings.Account.DeleteAccount.Help"));

                    if (ImGui.BeginPopupModal(Loc.Get("Settings.Account.DeleteAccount.Confirm") + "###deleteAccountPopup", ref _deleteAccountPopupModalShown, UiSharedService.PopupWindowFlags))
                    {
                        UiSharedService.TextWrapped(Loc.Get("Settings.Account.DeleteAccount.Warning1"));
                        UiSharedService.TextWrapped(Loc.Get("Settings.Account.DeleteAccount.Warning2"));
                        ImGui.TextUnformatted(Loc.Get("Settings.Account.DeleteAccount.ConfirmQuestion"));
                        ImGui.Separator();
                        ImGui.Spacing();

                        var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                          ImGui.GetStyle().ItemSpacing.X) / 2;

                        if (ImGui.Button(Loc.Get("Settings.Account.DeleteAccount"), new Vector2(buttonSize, 0)))
                        {
                            _ = Task.Run(ApiController.UserDelete);
                            _deleteAccountPopupModalShown = false;
                            Mediator.Publish(new SwitchToIntroUiMessage());
                        }

                        ImGui.SameLine();

                        if (ImGui.Button(Loc.Get("Common.Cancel") + "##cancelDelete", new Vector2(buttonSize, 0)))
                        {
                            _deleteAccountPopupModalShown = false;
                        }

                        UiSharedService.SetScaledWindowSize(325);
                        ImGui.EndPopup();
                    }
                }

            }
    }

    private string _uidToAddForIgnore = string.Empty;
    private int _selectedEntry = -1;

    private string _uidToAddForIgnoreBlacklist = string.Empty;
    private int _selectedEntryBlacklist = -1;

    private void DrawSettingsContent()
    {

        float sidebarWidth = SettingsSidebarWidth * ImGuiHelpers.GlobalScale;

        ImGui.BeginChild("settings-sidebar", new Vector2(sidebarWidth, 0), false, ImGuiWindowFlags.NoScrollbar);
        DrawSettingsSidebar();
        ImGui.EndChild();
        ImGui.SameLine();
        float separatorHeight = ImGui.GetContentRegionAvail().Y;
        float separatorX = ImGui.GetCursorPosX();
        float separatorY = ImGui.GetCursorPosY();
        var drawList = ImGui.GetWindowDrawList();
        var separatorStart = ImGui.GetCursorScreenPos();
        var separatorEnd = new Vector2(separatorStart.X, separatorStart.Y + separatorHeight);
        var separatorColor = UiSharedService.AccentColor with { W = 0.6f };
        drawList.AddLine(separatorStart, separatorEnd, ImGui.GetColorU32(separatorColor), 1f * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPos(new Vector2(separatorX + 6f * ImGuiHelpers.GlobalScale, separatorY));
        ImGui.BeginChild("settings-content", Vector2.Zero, false);
        switch (_activeSettingsTab)
        {
            case 0: DrawGeneral(); break;
            case 1: DrawPerformance(); break;
            case 2: DrawFileStorageSettings(); break;
            case 3: DrawCurrentTransfers(); break;
            case 4: DrawAutoDetect(); break;
            case 5: DrawChatConfig(); break;
            case 6:
                ImGui.BeginDisabled(_registrationInProgress);
                DrawServerConfiguration();
                ImGui.EndDisabled();
                break;
            case 7: DrawPrivacy(); break;
            case 8: DrawAdvanced(); break;
            case 9: DrawAbout(); break;
        }
        ImGui.EndChild();
    }

    private void DrawAbout()
    {
        _lastTab = "About";

        var availWidth = ImGui.GetContentRegionAvail().X;
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        string versionStr = $"v{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";

        ImGuiHelpers.ScaledDummy(20f);

        string moonIcon = FontAwesomeIcon.Moon.ToIconString();
        using (_uiShared.IconFont.Push())
        {
            var iconSz = ImGui.CalcTextSize(moonIcon);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - iconSz.X) / 2f);
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor))
                ImGui.TextUnformatted(moonIcon);
        }

        ImGuiHelpers.ScaledDummy(8f);

        using (_uiShared.UidFont.Push())
        {
            var titleSz = ImGui.CalcTextSize("UmbraSync");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor))
                ImGui.TextUnformatted("UmbraSync");
        }

        ImGuiHelpers.ScaledDummy(2f);

        string versionLine = $"{versionStr}  ·  {Loc.Get("Settings.About.ByAuthor")}";
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            var vSz = ImGui.CalcTextSize(versionLine);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - vSz.X) / 2f);
            ImGui.TextUnformatted(versionLine);
        }

        ImGuiHelpers.ScaledDummy(4f);

        string tagline = Loc.Get("Settings.About.Tagline");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            var tSz = ImGui.CalcTextSize(tagline);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - tSz.X) / 2f);
            ImGui.TextUnformatted(tagline);
        }

        ImGuiHelpers.ScaledDummy(24f);

        string linksLabel = Loc.Get("Settings.About.Links");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            var lSz = ImGui.CalcTextSize(linksLabel);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - lSz.X) / 2f);
            ImGui.TextUnformatted(linksLabel);
        }

        ImGuiHelpers.ScaledDummy(6f);

        float btnSpacing = 8f * ImGuiHelpers.GlobalScale;
        float margin = 20f * ImGuiHelpers.GlobalScale;
        float usableWidth = availWidth - margin * 2f;
        float btnWidth = (usableWidth - btnSpacing * 2f) / 3f;

        ImGui.SetCursorPosX(margin);
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0x2A / 255f, 0x1F / 255f, 0x3D / 255f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0x38 / 255f, 0x29 / 255f, 0x52 / 255f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0x4A / 255f, 0x36 / 255f, 0x68 / 255f, 1f)))
        {
            if (DrawAboutLinkButton(FontAwesomeIcon.Globe, "Discord", btnWidth))
                Dalamud.Utility.Util.OpenLink("https://discord.gg/2zJB7DjAs9");
            ImGui.SameLine(0, btnSpacing);
            if (DrawAboutLinkButton(FontAwesomeIcon.Code, "GitHub", btnWidth))
                Dalamud.Utility.Util.OpenLink("https://github.com/kedaewyn/UmbraClient/");
            ImGui.SameLine(0, btnSpacing);
            if (DrawAboutLinkButton(FontAwesomeIcon.FileAlt, Loc.Get("Settings.About.Changelog"), btnWidth))
                Mediator.Publish(new OpenChangelogUiMessage());
        }

        ImGuiHelpers.ScaledDummy(20f);

        if (_apiController.ServerState is ServerState.Connected)
        {
            string statusText = $"{Loc.Get("Settings.About.Service")} {_serverConfigurationManager.CurrentServer!.ServerName}:";
            string availableText = Loc.Get("Settings.About.Available");
            string usersText = _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture);
            string onlineText = Loc.Get("Settings.About.UsersOnline");
            string fullLine = $"{statusText} {availableText}  ( {usersText}  {onlineText} )";

            var lineSz = ImGui.CalcTextSize(fullLine);
            float lineStartX = (availWidth - lineSz.X) / 2f;
            ImGui.SetCursorPosX(lineStartX);

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextUnformatted($"{statusText} ");
            ImGui.SameLine(0, 0);
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor))
                ImGui.TextUnformatted(availableText);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextUnformatted("(");
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.AccentColor))
                ImGui.TextUnformatted(usersText);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextUnformatted($"{onlineText} )");
        }
        else
        {
            string offlineText = Loc.Get("Settings.About.ServerOffline");
            var offSz = ImGui.CalcTextSize(offlineText);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - offSz.X) / 2f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                ImGui.TextUnformatted(offlineText);
        }

        ImGuiHelpers.ScaledDummy(8f);

        string footer = Loc.Get("Settings.About.Footer");
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            var fSz = ImGui.CalcTextSize(footer);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - fSz.X) / 2f);
            ImGui.TextUnformatted(footer);
        }
    }

    private static bool DrawAboutLinkButton(FontAwesomeIcon icon, string label, float width)
    {
        string iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();
        var labelSz = ImGui.CalcTextSize(label);
        float totalW = iconSz.X + 6f * ImGuiHelpers.GlobalScale + labelSz.X;
        float btnH = 30f * ImGuiHelpers.GlobalScale;

        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button($"##{label}Link", new Vector2(width, btnH));

        var dl = ImGui.GetWindowDrawList();
        float startX = pos.X + (width - totalW) / 2f;
        float textY = pos.Y + (btnH - labelSz.Y) / 2f;

        dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), new Vector2(startX, pos.Y + (btnH - iconSz.Y) / 2f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), iconStr);
        dl.AddText(new Vector2(startX + iconSz.X + 6f * ImGuiHelpers.GlobalScale, textY), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), label);

        return clicked;
    }

    private void DrawSectionHeader(int tabIndex)
    {
        var icon = SettingsIcons[tabIndex];
        var title = SettingsLabels[tabIndex];
        var description = Loc.Get(SettingsDescriptionKeys[tabIndex]);
        var availWidth = ImGui.GetContentRegionAvail().X;

        ImGuiHelpers.ScaledDummy(6f);

        // Icon — large, centered
        string iconStr = icon.ToIconString();
        using (_uiShared.IconFont.Push())
        {
            var iconSz = ImGui.CalcTextSize(iconStr);
            float scale = 1.6f;
            var scaledSz = iconSz * scale;
            var pos = ImGui.GetCursorScreenPos();
            float iconX = pos.X + (availWidth - scaledSz.X) / 2f;
            float iconY = pos.Y;
            ImGui.Dummy(new Vector2(0, scaledSz.Y));
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(iconX, iconY), ImGui.GetColorU32(UiSharedService.AccentColor), iconStr);
        }

        ImGuiHelpers.ScaledDummy(4f);

        // Title — bold, centered
        using (_uiShared.UidFont.Push())
        {
            var titleSz = ImGui.CalcTextSize(title);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - titleSz.X) / 2f);
            ImGui.TextUnformatted(title);
        }

        ImGuiHelpers.ScaledDummy(2f);

        // Description — gray, centered wrap zone
        float margin = availWidth * 0.05f;
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + margin);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + availWidth - margin * 2f);
            ImGui.TextWrapped(description);
            ImGui.PopTextWrapPos();
        }

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawSettingsSidebar()
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        _settingsSidebarRects.Clear();

        ImGuiHelpers.ScaledDummy(4f);

        for (int i = 0; i < SettingsLabels.Length; i++)
        {
            DrawSettingsSidebarButton(i);
            ImGuiHelpers.ScaledDummy(1f);
        }

        drawList.ChannelsSetCurrent(0);
        DrawSettingsSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSettingsSidebarButton(int tabIndex)
    {
        using var id = ImRaii.PushId(tabIndex);

        const float btnH = 24f;
        const float iconTextGap = 6f;
        const float paddingX = 8f;
        float scaledBtnH = btnH * ImGuiHelpers.GlobalScale;
        float availWidth = ImGui.GetContentRegionAvail().X;

        bool isActive = _activeSettingsTab == tabIndex;

        var p = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton("##settingsSidebarBtn", new Vector2(availWidth, scaledBtnH));
        bool hovered = ImGui.IsItemHovered();

        _settingsSidebarRects[tabIndex] = (p, p + new Vector2(availWidth, scaledBtnH));

        // Background: only draw on hover (indicator handles active state)
        if (hovered && !isActive)
        {
            var hoverColor = new Vector4(0x30 / 255f, 0x19 / 255f, 0x46 / 255f, 1f);
            var dl = ImGui.GetWindowDrawList();
            float rounding = 6f * ImGuiHelpers.GlobalScale;
            float padding = 2f * ImGuiHelpers.GlobalScale;
            dl.AddRectFilled(p - new Vector2(padding), p + new Vector2(availWidth + padding, scaledBtnH + padding), ImGui.GetColorU32(hoverColor), rounding);
        }

        // Icon
        var dl2 = ImGui.GetWindowDrawList();
        string iconStr = SettingsIcons[tabIndex].ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSz = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
        var textColorU32 = ImGui.GetColorU32(textColor);

        float startX = p.X + paddingX * ImGuiHelpers.GlobalScale;

        ImGui.PushFont(UiBuilder.IconFont);
        dl2.AddText(new Vector2(startX, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, iconStr);
        ImGui.PopFont();

        dl2.AddText(new Vector2(startX + iconSz.X + iconTextGap * ImGuiHelpers.GlobalScale, p.Y + (scaledBtnH - iconSz.Y) / 2f), textColorU32, SettingsLabels[tabIndex]);

        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) _activeSettingsTab = tabIndex;
    }

    private void DrawSettingsSidebarIndicator(ImDrawListPtr drawList)
    {
        if (!_settingsSidebarRects.TryGetValue(_activeSettingsTab, out var rect))
            return;

        var windowPos = ImGui.GetWindowPos();
        var targetPos = rect.Min;
        var targetSize = rect.Max - rect.Min;

        if (!_settingsSidebarIndicatorInit || _settingsSidebarWindowPos != windowPos)
        {
            _settingsSidebarIndicatorPos = targetPos;
            _settingsSidebarIndicatorSize = targetSize;
            _settingsSidebarIndicatorInit = true;
            _settingsSidebarWindowPos = windowPos;
        }
        else
        {
            float dt = ImGui.GetIO().DeltaTime;
            float lerpT = 1f - MathF.Exp(-SettingsSidebarAnimSpeed * dt);
            _settingsSidebarIndicatorPos = Vector2.Lerp(_settingsSidebarIndicatorPos, targetPos, lerpT);
            _settingsSidebarIndicatorSize = Vector2.Lerp(_settingsSidebarIndicatorSize, targetSize, lerpT);
        }

        float padding = 2f * ImGuiHelpers.GlobalScale;
        var min = _settingsSidebarIndicatorPos - new Vector2(padding);
        var max = _settingsSidebarIndicatorPos + _settingsSidebarIndicatorSize + new Vector2(padding);
        float rounding = 6f * ImGuiHelpers.GlobalScale;
        var indicatorColor = _activeSettingsTab == 7
            ? new Vector4(0f, 0.2f, 0.6f, 1f) // EU Pantone blue #003399 for Privacy tab
            : UiSharedService.AccentColor;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(indicatorColor), rounding);
    }

    private void DrawAutoDetect()
    {
        _lastTab = "AutoDetect";
        DrawSectionHeader(4);


        bool isAutoDetectSuppressed = _autoDetectSuppressionService.IsSuppressed;
        bool enableDiscovery = _configService.Current.EnableAutoDetectDiscovery;

        using (ImRaii.Disabled(isAutoDetectSuppressed))
        {
            if (ImGui.Checkbox(Loc.Get("Settings.AutoDetect.Enable"), ref enableDiscovery))
            {
                _configService.Current.EnableAutoDetectDiscovery = enableDiscovery;
                _configService.Save();

                // notify services of toggle
                Mediator.Publish(new NearbyDetectionToggled(enableDiscovery));

                // if Nearby is turned OFF, force Allow Pair Requests OFF as well
                if (!enableDiscovery && _configService.Current.AllowAutoDetectPairRequests)
                {
                    _configService.Current.AllowAutoDetectPairRequests = false;
                    _configService.Save();
                    Mediator.Publish(new AllowPairRequestsToggled(false));
                }
            }
            if (isAutoDetectSuppressed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                UiSharedService.AttachToolTip(Loc.Get("Settings.AutoDetect.SuppressedTooltip"));
            }
        }

        // Allow Pair Requests is disabled when Nearby is OFF
        using (ImRaii.Disabled(isAutoDetectSuppressed || !enableDiscovery))
        {
            bool allowRequests = _configService.Current.AllowAutoDetectPairRequests;
            if (ImGui.Checkbox(Loc.Get("Settings.AutoDetect.AllowInvites"), ref allowRequests))
            {
                _configService.Current.AllowAutoDetectPairRequests = allowRequests;
                _configService.Save();

                // notify services of toggle
                Mediator.Publish(new AllowPairRequestsToggled(allowRequests));

                // user-facing info toast
                Mediator.Publish(new NotificationMessage(
                    "AutoDetect",
                    allowRequests ? Loc.Get("Settings.AutoDetect.InvitesEnabled") : Loc.Get("Settings.AutoDetect.InvitesDisabled"),
                    NotificationType.Info,
                    default));
            }
            if (isAutoDetectSuppressed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                UiSharedService.AttachToolTip(Loc.Get("Settings.AutoDetect.SuppressedTooltip"));
            }
        }

        // Interactive popup for pair requests is disabled when Nearby or Pair Requests are OFF
        using (ImRaii.Disabled(isAutoDetectSuppressed || !enableDiscovery || !_configService.Current.AllowAutoDetectPairRequests))
        {
            bool useInteractivePopup = _configService.Current.UseInteractivePairRequestPopup;
            if (ImGui.Checkbox(Loc.Get("Settings.AutoDetect.UseInteractivePopup"), ref useInteractivePopup))
            {
                _configService.Current.UseInteractivePairRequestPopup = useInteractivePopup;
                _configService.Save();
            }
            _uiShared.DrawHelpText(Loc.Get("Settings.AutoDetect.UseInteractivePopupHelp"));
        }

        var enableSlotNotifications = _configService.Current.EnableSlotNotifications;
        if (ImGui.Checkbox(Loc.Get("Settings.AutoDetect.EnableSlotNotifications"), ref enableSlotNotifications))
        {
            _configService.Current.EnableSlotNotifications = enableSlotNotifications;
            _configService.Save();
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.AutoDetect.EnableSlotNotificationsHelp"));

        if (isAutoDetectSuppressed)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Settings.AutoDetect.LockedInInstance"), ImGuiColors.DalamudYellow);
        }
    }

    private void DrawDtrStyleCombo()
    {
        var styleIndex = _configService.Current.DtrStyle;
        string previewText = styleIndex == 0 ? DtrDefaultPreviewText : DtrEntry.RenderDtrStyle(styleIndex, "123");

        ImGui.SetNextItemWidth(MathF.Min(250 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
        bool comboOpen = ImGui.BeginCombo("Server Info Bar style", previewText);

        if (comboOpen)
        {
            for (int i = 0; i < DtrEntry.NumStyles; i++)
            {
                string label = i == 0 ? DtrDefaultPreviewText : DtrEntry.RenderDtrStyle(i, "123");
                bool isSelected = i == styleIndex;
                if (ImGui.Selectable(label, isSelected))
                {
                    _configService.Current.DtrStyle = i;
                    _configService.Save();
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }

            }

            ImGui.EndCombo();
        }

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

    private void DrawTypingSettings()
    {
        var typingEnabled = _configService.Current.TypingIndicatorEnabled;
        var typingIndicatorNameplates = _configService.Current.TypingIndicatorShowOnNameplates;
        var typingIndicatorPartyList = _configService.Current.TypingIndicatorShowOnPartyList;
        var typingShowSelf = _configService.Current.TypingIndicatorShowSelf;

        if (ImGui.Checkbox(Loc.Get("Settings.Typing.EnableSystem"), ref typingEnabled))
        {
            _configService.Current.TypingIndicatorEnabled = typingEnabled;
            _configService.Save();
            _chatTypingDetectionService.SoftRestart();
        }
        _uiShared.DrawHelpText(Loc.Get("Settings.Typing.EnableSystemHelp"));

        using (ImRaii.Disabled(!typingEnabled))
        {
            if (ImGui.Checkbox(Loc.Get("Settings.Typing.ShowOnNameplates"), ref typingIndicatorNameplates))
            {
                _configService.Current.TypingIndicatorShowOnNameplates = typingIndicatorNameplates;
                _configService.Save();
            }
            _uiShared.DrawHelpText(Loc.Get("Settings.Typing.ShowOnNameplatesHelp"));

            if (typingIndicatorNameplates)
            {
                using var indentTyping = ImRaii.PushIndent();
                var bubbleSize = _configService.Current.TypingIndicatorBubbleSize;
                ImGui.SetNextItemWidth(MathF.Min(140 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 200 * ImGuiHelpers.GlobalScale));
                TypingIndicatorBubbleSize? selectedBubbleSize = _uiShared.DrawCombo($"{Loc.Get("Settings.Typing.BubbleSize")}##typingBubbleSize",
                    Enum.GetValues<TypingIndicatorBubbleSize>(),
                    size => size switch
                    {
                        TypingIndicatorBubbleSize.Small => Loc.Get("Settings.Typing.BubbleSizeSmall"),
                        TypingIndicatorBubbleSize.Medium => Loc.Get("Settings.Typing.BubbleSizeMedium"),
                        TypingIndicatorBubbleSize.Large => Loc.Get("Settings.Typing.BubbleSizeLarge"),
                        _ => size.ToString()
                    },
                    null,
                    bubbleSize);

                if (selectedBubbleSize.HasValue && selectedBubbleSize.Value != bubbleSize)
                {
                    _configService.Current.TypingIndicatorBubbleSize = selectedBubbleSize.Value;
                    _configService.Save();
                }

                if (ImGui.Checkbox(Loc.Get("Settings.Typing.LogPartyList"), ref typingIndicatorPartyList))
                {
                    _configService.Current.TypingIndicatorShowOnPartyList = typingIndicatorPartyList;
                    _configService.Save();
                }
                _uiShared.DrawHelpText(Loc.Get("Settings.Typing.LogPartyListHelp"));

                if (ImGui.Checkbox(Loc.Get("Settings.Typing.ShowSelf"), ref typingShowSelf))
                {
                    _configService.Current.TypingIndicatorShowSelf = typingShowSelf;
                    _configService.Save();
                }
                _uiShared.DrawHelpText(Loc.Get("Settings.Typing.ShowSelfHelp"));
            }
        }
    }

}