using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.CharaData.Models;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi : WindowMediatorSubscriberBase
{
    private const int maxPoses = 10;
    private readonly CharaDataManager _charaDataManager;
    private readonly CharaDataNearbyManager _charaDataNearbyManager;
    private readonly CharaDataConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly PairManager _pairManager;
    private readonly CharaDataGposeTogetherManager _charaDataGposeTogetherManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiSharedService;
    private readonly McdfShareManager _mcdfShareManager;
    private readonly MareConfigService _mareConfigService;
    private readonly HousingShareManager? _housingShareManager_housing;
    private readonly HousingFurnitureScanner? _housingScanner;
    private readonly HousingNpcScenarioService? _housingNpcScenarioService;
    private readonly HousingOwnershipService _housingOwnershipService;
    private readonly HousingScenarioManager? _housingScenarioManager;
    private CancellationTokenSource? _closalCts = new();
    private bool _disableUI = false;
    private CancellationTokenSource? _disposalCts = new();
    private string _exportDescription = string.Empty;
    private string _filterCodeNote = string.Empty;
    private string _filterDescription = string.Empty;
    private Dictionary<string, (CharaDataFavorite Favorite, CharaDataMetaInfoExtendedDto? MetaInfo, bool DownloadedMetaInfo)> _filteredFavorites = [];
    private bool _filterPoseOnly = false;
    private bool _filterWorldOnly = false;
    private string _gposeTarget = string.Empty;
    private bool _hasValidGposeTarget;
    private readonly PolledUiValue<(bool CanApply, string TargetName)> _gposeTargetPoll;
    private readonly PolledUiValue<nint> _gposeTargetAddressPoll;
    private bool _isHandlingSelf = false;
    private DateTime _lastFavoriteUpdateTime = DateTime.UtcNow;
    private PoseEntryExtended? _nearbyHovered;
    private bool _openMcdOnlineOnNextRun = false;
    private bool _readExport;
    private string _selectedDtoId = string.Empty;
    private string SelectedDtoId
    {
        get => _selectedDtoId;
        set
        {
            if (!string.Equals(_selectedDtoId, value, StringComparison.Ordinal))
            {
                _charaDataManager.UploadTask = null;
                _selectedDtoId = value;
            }

        }
    }

    private static string SanitizeFileName(string? candidate, string fallback)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        if (string.IsNullOrWhiteSpace(candidate)) return fallback;

        var sanitized = new string(candidate.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
    private string _selectedSpecificUserIndividual = string.Empty;
    private string _selectedSpecificGroupIndividual = string.Empty;
    private string _specificIndividualAdd = string.Empty;
    private string _specificGroupAdd = string.Empty;
    private bool _liveGroupMode;

    private string? _openComboHybridId = null;
    private (string Id, string? Alias, string AliasOrId, string? Note)[]? _openComboHybridEntries = null;
    private bool _comboHybridUsedLastFrame = false;
    private enum HubPage
    {
        GposeTogether,
        QuestSync,
        GposeActors,
        NearbyPoses,
        ApplyData,
        McdfData,
        LiveData,
        HousingFurniture,
        HousingNpc,
        Profiles,
    }

    private readonly SideRail _hubRail = new();
    private HubPage _hubPage = HubPage.GposeTogether;
    private int _dataApplicationSubTab;
    private bool _mcdfShareInitialized;
    private string _mcdfSnapshotName = string.Empty;
    private List<LocalMcdfEntry> _localMcdfFiles = [];
    private DateTime _localMcdfScanTime = DateTime.MinValue;
    private readonly HashSet<string> _collapsedMcdfFolders = [];
    private string _mcdfLocalSearch = string.Empty;
    private string _mcdfOnlineSearch = string.Empty;
    private const int LibraryAll = 0;
    private const int LibraryFavorites = 1;
    private const int LibraryOwn = 2;
    private const int LibraryImported = 3;
    private const int LibraryShared = 4;
    private string _librarySearch = string.Empty;
    private List<FileInfo> _importedFiles = [];
    private DateTime _importedFilesScanTime = DateTime.MinValue;
    private enum McdfPanel { None, Export, Import }
    private McdfPanel _mcdfPanel = McdfPanel.None;
    private string _mcdfShareTargetName = string.Empty;
    private string _mcdfNewFolderName = string.Empty;
    private bool _mcdfShowNewFolderInput;
    private string _mcdfFolderToDelete = string.Empty;
    private bool _mcdfDeleteFolderModalOpen = true;
    private UmbraSync.API.Dto.McdfShare.McdfShareEntryDto? _mcdfDownloadEntry;
    private string _mcdfDownloadFolder = string.Empty;
    private string _mcdfDownloadNewFolder = string.Empty;
    private Task? _mcdfDownloadTask;
    private bool _mcdfOpenDownloadPopup;

    private sealed record LocalMcdfEntry(string FilePath, string FileName, string Description, long FileSize, DateTime LastModified, string SubFolder);
    private string _mcdfShareDescription = string.Empty;
    private readonly List<string> _mcdfShareAllowedIndividuals = new();
    private readonly List<string> _mcdfShareAllowedSyncshells = new();
    private string _mcdfShareIndividualDropdownSelection = string.Empty;
    private string _mcdfShareIndividualInput = string.Empty;
    private string _mcdfShareSyncshellDropdownSelection = string.Empty;
    private string _mcdfShareSyncshellInput = string.Empty;
    private int _mcdfShareExpireDays;
    private string _mcdfShareSourceId = string.Empty;
    private bool _mcdfShareSourceIsLocal;
    private readonly UmbraProfileManager _umbraProfileManager;
    private string _profileBrowserSearch = string.Empty;
    private readonly Dictionary<string, (byte[] Data, Task<IDalamudTextureWrap> Task)> _profileBrowserTextureTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _profileCardHeights = new(StringComparer.Ordinal);

    public CharaDataHubUi(ILogger<CharaDataHubUi> logger, MareMediator mediator, PerformanceCollectorService performanceCollectorService,
                         CharaDataManager charaDataManager, CharaDataNearbyManager charaDataNearbyManager, CharaDataConfigService configService,
                         UiSharedService uiSharedService, ServerConfigurationManager serverConfigurationManager,
                         DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, PairManager pairManager,
                         CharaDataGposeTogetherManager charaDataGposeTogetherManager, McdfShareManager mcdfShareManager,
                         HousingShareManager housingShareManager, HousingFurnitureScanner housingScanner,
                         HousingNpcScenarioService housingNpcScenarioService,
                         HousingScenarioManager housingScenarioManager,
                         HousingOwnershipService housingOwnershipService,
                         UmbraProfileManager umbraProfileManager, MareConfigService mareConfigService)
        : base(logger, mediator, $"{Loc.Get("CharaDataHub.WindowTitle")}###UmbraCharaDataUI", performanceCollectorService)
    {
        SetWindowSizeConstraints();

        _charaDataManager = charaDataManager;
        _charaDataNearbyManager = charaDataNearbyManager;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;

        // La cible GPose était relue en bloquant le thread de rendu à chaque frame.
        _gposeTargetPoll = new PolledUiValue<(bool, string)>(
            () => _charaDataManager.CanApplyInGpose(),
            TimeSpan.FromMilliseconds(200),
            (false, string.Empty));
        // Celui-ci était relu une fois par acteur GPose et par frame.
        _gposeTargetAddressPoll = new PolledUiValue<nint>(
            async () => (await _dalamudUtilService.GetGposeTargetGameObjectAsync().ConfigureAwait(false))?.Address ?? nint.Zero,
            TimeSpan.FromMilliseconds(200),
            nint.Zero);
        _housingLocationPoll = new PolledUiValue<LocationInfo>(
            () => _dalamudUtilService.GetMapDataAsync(),
            TimeSpan.FromMilliseconds(500),
            default);
        _fileDialogManager = fileDialogManager;
        _pairManager = pairManager;
        _charaDataGposeTogetherManager = charaDataGposeTogetherManager;
        _mcdfShareManager = mcdfShareManager;
        _mareConfigService = mareConfigService;
        _housingShareManager_housing = housingShareManager;
        _housingScanner = housingScanner;
        _housingNpcScenarioService = housingNpcScenarioService;
        _housingScenarioManager = housingScenarioManager;
        _housingOwnershipService = housingOwnershipService;
        _umbraProfileManager = umbraProfileManager;
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen |= _configService.Current.OpenMareHubOnGposeStart);
        Mediator.Subscribe<OpenCharaDataHubWithFilterMessage>(this, (msg) =>
        {
            IsOpen = true;
            _openDataApplicationShared = true;
        });
        Mediator.Subscribe<ConnectedMessage>(this, (_) =>
        {
            _mcdfShareManager.EnsureSubscribed();

            if (_mareConfigService.Current.AutoFetchMcdfOnConnect)
                _mcdfShareManager.StartBackgroundPolling();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => _mcdfShareManager.StopBackgroundPolling());
        InitQuestSyncSubscriptions();
    }

    private bool _openDataApplicationShared = false;

    public string CharaName(string name)
    {
        if (_configService.Current.AbbreviateCharaNames)
        {
            var split = name.Split(" ");
            return split[0].First() + ". " + split[1].First() + ".";
        }

        return name;
    }

    public override void OnClose()
    {
        if (_disableUI)
        {
            IsOpen = true;
            return;
        }

        try
        {
            _closalCts?.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "Attempted to cancel CharaDataHubUi close token after disposal");
        }
        EnsureFreshCts(ref _closalCts);
        SelectedDtoId = string.Empty;
        _charaDataNearbyManager.ComputeNearbyData = false;
        _openComboHybridId = null;
        _openComboHybridEntries = null;
    }

    public override void OnOpen()
    {
        EnsureFreshCts(ref _closalCts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mcdfShareManager.StopBackgroundPolling();
            CancelAndDispose(ref _closalCts);
            CancelAndDispose(ref _disposalCts);
        }

        base.Dispose(disposing);
    }

    protected override void DrawInternal()
    {
        DrawHubContent();
    }

    public void DrawInline()
    {
        using (ImRaii.PushId("CharaDataHubInline"))
        {
            DrawHubContent();
        }
    }

    private void DrawHubContent()
    {
        if (!_comboHybridUsedLastFrame)
        {
            _openComboHybridId = null;
            _openComboHybridEntries = null;
        }
        _comboHybridUsedLastFrame = false;

        _disableUI = !(_charaDataManager.UiBlockingComputation?.IsCompleted ?? true);
        if (DateTime.UtcNow.Subtract(_lastFavoriteUpdateTime).TotalSeconds > 2)
        {
            _lastFavoriteUpdateTime = DateTime.UtcNow;
            UpdateFilteredFavorites();
        }

        (_hasValidGposeTarget, _gposeTarget) = _gposeTargetPoll.Poll(_logger);

        if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(3);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.BrioRequired"), UiSharedService.AccentColor);
            UiSharedService.DistanceSeparator();
        }

        using var disabled = ImRaii.Disabled(_disableUI);

        DisableDisabled(() =>
        {
            if (_charaDataManager.DataApplicationTask != null)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Loc.Get("CharaDataHub.ApplyingData"));
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Ban, Loc.Get("CharaDataHub.CancelApplication")))
                {
                    _charaDataManager.CancelDataApplication();
                }
            }
            if (!string.IsNullOrEmpty(_charaDataManager.DataApplicationProgress))
            {
                UiSharedService.ColorTextWrapped(_charaDataManager.DataApplicationProgress, UiSharedService.AccentColor);
            }
            if (_charaDataManager.DataApplicationTask != null)
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.ApplicationWarning"), UiSharedService.AccentColor);
                ImGuiHelpers.ScaledDummy(5);
                ImGui.Separator();
            }
        });

        _isHandlingSelf = _charaDataManager.HandledCharaData.Any(c => c.Value.IsSelf);
        if (_isHandlingSelf) _openMcdOnlineOnNextRun = false;

        if (_openDataApplicationShared)
        {
            _hubPage = HubPage.ApplyData;
            _dataApplicationSubTab = LibraryShared;
            _openDataApplicationShared = false;
        }
        if (_openMcdOnlineOnNextRun)
        {
            _hubPage = HubPage.LiveData;
            _openMcdOnlineOnNextRun = false;
        }

        bool inGpose = _uiSharedService.IsInGpose;
        if (inGpose && !IsGposePage(_hubPage)) _hubPage = HubPage.ApplyData;
        if (_isHandlingSelf && IsCreationPage(_hubPage)) _hubPage = HubPage.ApplyData;

        _onlineDataSubTab = _hubPage == HubPage.LiveData ? 1 : 0;
        _charaDataNearbyManager.ComputeNearbyData = _hubPage == HubPage.NearbyPoses;

        float railWidth = SideRail.WidthFor(ImGui.GetContentRegionAvail().X);
        bool railCollapsed = railWidth < SideRail.ExpandedWidth * ImGuiHelpers.GlobalScale;

        using (var rail = ImRaii.Child("hub-rail", new Vector2(railWidth, 0), false, ImGuiWindowFlags.NoScrollbar))
        {
            if (rail)
            {
                int active = (int)_hubPage;
                _hubRail.Draw(BuildHubRail(inGpose), ref active, railCollapsed);
                _hubPage = (HubPage)active;
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

        using (var content = ImRaii.Child("hub-content", Vector2.Zero, false))
        {
            if (content)
            {
                DrawHubPage(_hubPage);
                UiSharedService.EndSectionCard();
            }
        }

        SetWindowSizeConstraints(IsGposePage(_hubPage));
    }

    private static bool IsGposePage(HubPage page)
        => page is HubPage.GposeTogether or HubPage.QuestSync or HubPage.GposeActors or HubPage.NearbyPoses or HubPage.ApplyData;

    private static bool IsCreationPage(HubPage page)
        => page is HubPage.McdfData or HubPage.LiveData or HubPage.HousingFurniture or HubPage.HousingNpc;

    private List<SideRailEntry> BuildHubRail(bool inGpose)
    {
        var entries = new List<SideRailEntry>(15)
        {
            SideRailEntry.Group(Loc.Get("CharaDataHub.Nav.Group.Together")),
            Page(HubPage.GposeTogether, "CharaDataHub.Lobby.GposeTogether", FontAwesomeIcon.Camera),
            Page(HubPage.QuestSync, "CharaDataHub.Lobby.QuestSync", FontAwesomeIcon.Scroll),
            SideRailEntry.Group(Loc.Get("CharaDataHub.Nav.Group.Apply")),
            Page(HubPage.GposeActors, "CharaDataHub.Tab.GposeActors", FontAwesomeIcon.UserFriends),
            Page(HubPage.NearbyPoses, "CharaDataHub.Tab.PosesNearby", FontAwesomeIcon.MapMarkerAlt),
            Page(HubPage.ApplyData, "CharaDataHub.Nav.Library", FontAwesomeIcon.BookOpen),
        };

        // En GPose, la création n'a pas lieu d'être : le rail se limite à ce qui sert sur place.
        if (inGpose) return entries;

        entries.Add(SideRailEntry.Group(Loc.Get("CharaDataHub.Nav.Group.Data")));
        entries.Add(Page(HubPage.McdfData, "CharaDataHub.Mcd.Online.TabMcdf", FontAwesomeIcon.FileArchive, creation: true));
        entries.Add(Page(HubPage.LiveData, "CharaDataHub.Nav.LiveData", FontAwesomeIcon.Edit, creation: true));
        entries.Add(SideRailEntry.Group(Loc.Get("CharaDataHub.Tab.HousingShare")));
        entries.Add(Page(HubPage.HousingFurniture, "CharaDataHub.Nav.Furniture", FontAwesomeIcon.Couch, creation: true));
        entries.Add(Page(HubPage.HousingNpc, "CharaDataHub.Nav.Npc", FontAwesomeIcon.Users, creation: true));
        entries.Add(SideRailEntry.Group(Loc.Get("CharaDataHub.Nav.Group.Community")));
        entries.Add(Page(HubPage.Profiles, "CharaDataHub.Tab.Profiles", FontAwesomeIcon.AddressBook));
        return entries;
    }

    private SideRailEntry Page(HubPage page, string labelKey, FontAwesomeIcon icon, bool creation = false)
        => new((int)page, Loc.Get(labelKey), icon, !(creation && _isHandlingSelf),
            creation ? Loc.Get("CharaDataHub.CreationDisabledTooltip") : null);

    private void DrawHubPage(HubPage page)
    {
        var accent = UiSharedService.AccentColor;
        using var disabled = ImRaii.Disabled(IsCreationPage(page) && _isHandlingSelf);

        switch (page)
        {
            case HubPage.GposeTogether:
                DrawGposeTogether();
                break;
            case HubPage.QuestSync:
                DrawQuestSync();
                break;
            case HubPage.GposeActors:
                if (_uiSharedService.IsInGpose)
                {
                    using var id = ImRaii.PushId("gposeControls");
                    DrawGposeControls();
                }
                else
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.GposeOnlyTooltip"), UiSharedService.AccentColor);
                }
                break;
            case HubPage.NearbyPoses:
                using (ImRaii.PushId("nearbyPoseControls"))
                    DrawNearbyPoses();
                break;
            case HubPage.ApplyData:
                using (ImRaii.PushId("applyData"))
                    DrawDataApplication(accent);
                break;
            case HubPage.McdfData:
            case HubPage.LiveData:
                using (ImRaii.PushId("mcdf"))
                using (ImRaii.PushId("mcdOnline"))
                    DrawMcdOnline();
                break;
            case HubPage.HousingFurniture:
            case HubPage.HousingNpc:
                using (ImRaii.PushId("housingShare"))
                    DrawHousingShare(page);
                break;
            case HubPage.Profiles:
                DrawProfileBrowser(accent);
                break;
        }
    }

    private void DrawAddOrRemoveFavorite(CharaDataFullDto dto)
    {
        DrawFavorite(dto.Uploader.UID + ":" + dto.Id);
    }

    private void DrawAddOrRemoveFavorite(CharaDataMetaInfoExtendedDto? dto)
    {
        if (dto == null) return;
        DrawFavorite(dto.FullId);
    }

    private void DrawFavorite(string id, string? defaultDescription = null)
    {
        bool isFavorite = _configService.Current.FavoriteCodes.TryGetValue(id, out var favorite);
        if (_configService.Current.FavoriteCodes.ContainsKey(id))
        {
            _uiSharedService.IconText(FontAwesomeIcon.Star, ImGuiColors.ParsedGold);
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Favorite.CustomDesc"), favorite?.CustomDescription ?? string.Empty) + UiSharedService.TooltipSeparator
                + Loc.Get("CharaDataHub.Apply.Favorite.RemoveTooltip"));
        }
        else
        {
            _uiSharedService.IconText(FontAwesomeIcon.Star, ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorite.AddTooltip"));
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (isFavorite) _configService.Current.FavoriteCodes.Remove(id);
            else _configService.Current.FavoriteCodes[id] = new() { CustomDescription = defaultDescription ?? string.Empty };
            _configService.Save();
        }
    }

    private void DrawGposeControls()
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Apply.GposeActors.Title"), FontAwesomeIcon.UserFriends);
        ImGuiHelpers.ScaledDummy(5);
        using var indent = ImRaii.PushIndent(10f);

        foreach (var actor in _dalamudUtilService.GetGposeCharactersFromObjectTable())
        {
            if (actor == null) continue;
            using var actorId = ImRaii.PushId(actor.Name.TextValue);
            UiSharedService.DrawGrouped(() =>
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Crosshairs))
                {
                    unsafe
                    {
                        _dalamudUtilService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
                    }
                }
                ImGui.SameLine();
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.GposeActors.TargetTooltip"), CharaName(actor.Name.TextValue)));
                ImGui.AlignTextToFramePadding();
                var pos = ImGui.GetCursorPosX();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, actor.Address == _gposeTargetAddressPoll.Poll(_logger)))
                {
                    ImGui.TextUnformatted(CharaName(actor.Name.TextValue));
                }
                ImGui.SameLine(250);
                var handled = _charaDataManager.HandledCharaData.GetValueOrDefault(actor.Name.TextValue);
                using (ImRaii.Disabled(handled == null))
                {
                    _uiSharedService.IconText(FontAwesomeIcon.InfoCircle);
                    var id = string.IsNullOrEmpty(handled?.MetaInfo.Uploader.UID) ? handled?.MetaInfo.Id : handled.MetaInfo.FullId;
                    UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.GposeActors.AppliedDataTooltip"), id ?? Loc.Get("CharaDataHub.Apply.GposeActors.NoData")));

                    ImGui.SameLine();
                    // maybe do this better, check with brio for handled charas or sth
                    using (ImRaii.Disabled(!actor.Name.TextValue.StartsWith("Brio ", StringComparison.Ordinal)))
                    {
                        if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                        {
                            _charaDataManager.RemoveChara(actor.Name.TextValue);
                        }
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.GposeActors.RemoveTooltip"), CharaName(actor.Name.TextValue)));
                    }
                    ImGui.SameLine();
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Undo))
                    {
                        _charaDataManager.RevertChara(handled);
                    }
                    UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.GposeActors.RevertTooltip"), CharaName(actor.Name.TextValue)));
                    ImGui.SetCursorPosX(pos);
                    DrawPoseData(handled?.MetaInfo, actor.Name.TextValue, true);
                }
            });

            ImGuiHelpers.ScaledDummy(2);
        }
    }

    private void DrawDataApplication(System.Numerics.Vector4 accent)
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Apply.Title"), FontAwesomeIcon.FileImport);

        ImGuiHelpers.ScaledDummy(5);

        if (_uiSharedService.IsInGpose)
        {
            ImGui.TextUnformatted(Loc.Get("CharaDataHub.Apply.TargetLabel"));
            ImGui.SameLine(200);
            UiSharedService.ColorText(CharaName(_gposeTarget), UiSharedService.GetSuccessColor(_hasValidGposeTarget));
        }

        if (!_hasValidGposeTarget)
        {
            ImGuiHelpers.ScaledDummy(3);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Apply.TargetWarning"), UiSharedService.AccentColor, 350);
        }

        UiSharedService.EndSectionCard();

        if (!_mcdfShareInitialized && !_mcdfShareManager.IsBusy)
        {
            _mcdfShareInitialized = true;
            var cts = EnsureFreshCts(ref _disposalCts);
            _ = _charaDataManager.GetAllData(cts.Token);
            _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
        }

        using (ImRaii.Disabled((!_charaDataManager.GetAllDataTask?.IsCompleted ?? false)
            || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
            || _mcdfShareManager.IsBusy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowsSpin, Loc.Get("CharaDataHub.Mcd.Online.Refresh")))
            {
                var cts = EnsureFreshCts(ref _disposalCts);
                _ = _charaDataManager.GetAllData(cts.Token);
                _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
                _importedFilesScanTime = DateTime.MinValue;
            }
        }
        if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
        {
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.YourOwn.Cooldown"));
        }

        ImGui.SameLine();
        _uiSharedService.IconText(FontAwesomeIcon.Search, ImGuiColors.DalamudGrey);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Min(250f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##librarySearch", Loc.Get("CharaDataHub.Mcd.Online.Search"), ref _librarySearch, 128);

        ImGuiHelpers.ScaledDummy(3);

        var originLabels = new[]
        {
            Loc.Get("CharaDataHub.Library.All"),
            Loc.Get("CharaDataHub.Apply.Tabs.Favorites"),
            Loc.Get("CharaDataHub.Apply.Tabs.YourOwn"),
            Loc.Get("CharaDataHub.Apply.Tabs.Imported"),
            Loc.Get("CharaDataHub.Apply.Tabs.SharedWithYou"),
        };
        var originIcons = new[]
        {
            FontAwesomeIcon.LayerGroup,
            FontAwesomeIcon.Star,
            FontAwesomeIcon.User,
            FontAwesomeIcon.FileImport,
            FontAwesomeIcon.ShareAlt,
        };
        DrawSubTabButtons(originLabels, originIcons, ref _dataApplicationSubTab, accent);

        ImGuiHelpers.ScaledDummy(5);

        int origin = _dataApplicationSubTab;
        bool all = origin == LibraryAll;

        if (all || origin == LibraryFavorites)
        {
            UiSharedService.DrawTree(Loc.Get("CharaDataHub.Apply.Filters.Title"), () =>
            {
                var maxIndent = ImGui.GetWindowContentRegionMax();
                ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                ImGui.InputTextWithHint("##ownFilter", Loc.Get("CharaDataHub.Apply.Filters.CodeOwner"), ref _filterCodeNote, 100);
                ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                ImGui.InputTextWithHint("##descFilter", Loc.Get("CharaDataHub.Apply.Filters.Description"), ref _filterDescription, 100);
                ToggleSwitch.Draw(Loc.Get("CharaDataHub.Apply.Filters.OnlyPose"), ref _filterPoseOnly);
                ToggleSwitch.Draw(Loc.Get("CharaDataHub.Apply.Filters.OnlyWorld"), ref _filterWorldOnly);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Ban, Loc.Get("CharaDataHub.Apply.Filters.Reset")))
                {
                    _filterCodeNote = string.Empty;
                    _filterDescription = string.Empty;
                    _filterPoseOnly = false;
                    _filterWorldOnly = false;
                }
            });
        }

        if (origin == LibraryOwn)
        {
            DrawHelpFoldout(Loc.Get("CharaDataHub.Apply.YourOwn.Help"));
        }

        ImGuiHelpers.ScaledDummy(3);
        ImGui.Separator();

        using var list = ImRaii.Child("libraryList");
        if (!list) return;

        ImGuiHelpers.ScaledDummy(5);
        using var listIndent = ImRaii.PushIndent(5f);

        int shown = 0;
        if (all || origin == LibraryFavorites) shown += DrawLibraryFavorites(_librarySearch, all);
        if (all || origin == LibraryOwn) shown += DrawLibraryOwn(_librarySearch, all);
        if (all || origin == LibraryImported) shown += DrawLibraryImported(_librarySearch, all);
        if (all || origin == LibraryShared) shown += DrawLibraryShared(_librarySearch, all);

        if (shown == 0)
        {
            var emptyKey = origin switch
            {
                LibraryFavorites => "CharaDataHub.Apply.NoFavorites",
                LibraryImported => string.IsNullOrEmpty(_configService.Current.McdfLocalFolder)
                    ? "CharaDataHub.Mcdf.Import.NoFolderConfigured"
                    : "CharaDataHub.Apply.Imported.Empty",
                LibraryShared => "CharaDataHub.Apply.SharedWithYou.None",
                _ => "CharaDataHub.Library.Empty",
            };
            UiSharedService.ColorTextWrapped(Loc.Get(emptyKey), ImGuiColors.DalamudGrey);
        }
    }

    private static bool MatchesSearch(string search, string? value)
        => string.IsNullOrWhiteSpace(search) || (value?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    // Vue « Tout » : l'origine s'annonce au premier élément affiché, jamais pour un groupe vide.
    private static void LibraryItemStart(ref int count, bool grouped, string headingKey)
    {
        if (count == 0 && grouped)
        {
            ImGuiHelpers.ScaledDummy(3f);
            UiSharedService.ColorText(Loc.Get(headingKey), UiSharedService.ThemeTextAccent);
            ImGuiHelpers.ScaledDummy(2f);
        }

        count++;
    }

    private int DrawLibraryFavorites(string search, bool grouped)
    {
        int count = 0;
        var cursorPos = ImGui.GetCursorPos();
        var max = ImGui.GetWindowContentRegionMax();

        // MCDF favorites
        foreach (var favorite in _configService.Current.FavoriteCodes
            .Where(f => f.Key.StartsWith("mcdf:", StringComparison.Ordinal))
            .OrderByDescending(f => f.Value.LastDownloaded))
        {
            var mcdfGuidStr = favorite.Key["mcdf:".Length..];
            if (!Guid.TryParse(mcdfGuidStr, out var mcdfGuid)) continue;
            var shareEntry = _mcdfShareManager.OwnShares.FirstOrDefault(s => s.Id == mcdfGuid)
                ?? _mcdfShareManager.SharedShares.FirstOrDefault(s => s.Id == mcdfGuid);
            var favEntry = _configService.Current.FavoriteCodes.TryGetValue(favorite.Key, out var fav) ? fav : null;

            // Auto-update stored description from share if empty
            if (favEntry != null && string.IsNullOrEmpty(favEntry.CustomDescription) && shareEntry != null && !string.IsNullOrEmpty(shareEntry.Description))
            {
                favEntry.CustomDescription = shareEntry.Description;
                _configService.Save();
            }

            var displayName = shareEntry != null && !string.IsNullOrEmpty(shareEntry.Description)
                ? shareEntry.Description
                : !string.IsNullOrEmpty(favEntry?.CustomDescription) ? favEntry.CustomDescription : mcdfGuidStr;

            if (!MatchesSearch(search, displayName)) continue;
            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.Favorites");

            UiSharedService.DrawGrouped(() =>
            {
                using var tableid = ImRaii.PushId(favorite.Key);
                ImGui.AlignTextToFramePadding();
                DrawFavorite(favorite.Key);
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.FileArchive, ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                ImGui.TextUnformatted(displayName);

                ImGui.SameLine();
                using (ImRaii.Disabled(!_hasValidGposeTarget || shareEntry == null))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                    {
                        _ = _mcdfShareManager.ApplyShareAsync(mcdfGuid, CancellationToken.None);
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.ApplyTooltip"));

                UiSharedService.ColorText($"[{Loc.Get("CharaDataHub.Mcd.Online.TypeMcdf")}]", ImGuiColors.DalamudGrey);

                ImGui.TextUnformatted(Loc.Get("CharaDataHub.Apply.LastUse"));
                ImGui.SameLine();
                ImGui.TextUnformatted(favorite.Value.LastDownloaded == DateTime.MaxValue
                    ? Loc.Get("CharaDataHub.Apply.Never")
                    : favorite.Value.LastDownloaded.ToString(CultureInfo.CurrentCulture));

                var desc = favorite.Value.CustomDescription;
                ImGui.SetNextItemWidth(max.X - cursorPos.X - 25f);
                if (ImGui.InputTextWithHint("##desc", Loc.Get("CharaDataHub.Apply.FavoriteDescPlaceholder"), ref desc, 100))
                {
                    favorite.Value.CustomDescription = desc;
                    _configService.Save();
                }
            });
            ImGuiHelpers.ScaledDummy(5);
        }

        // CharaData favorites
        foreach (var favorite in _filteredFavorites
            .Where(f => !f.Key.StartsWith("mcdf:", StringComparison.Ordinal))
            .OrderByDescending(k => k.Value.Favorite.LastDownloaded))
        {
            if (!MatchesSearch(search, favorite.Value.MetaInfo?.Description) && !MatchesSearch(search, favorite.Key)
                && !MatchesSearch(search, favorite.Value.Favorite.CustomDescription)) continue;
            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.Favorites");

            UiSharedService.DrawGrouped(() =>
            {
                using var tableid = ImRaii.PushId(favorite.Key);
                ImGui.AlignTextToFramePadding();
                DrawFavorite(favorite.Key);
                using var innerIndent = ImRaii.PushIndent(25f);
                ImGui.SameLine();
                var xPos = ImGui.GetCursorPosX();
                var maxPos = (max.X - cursorPos.X);

                bool metaInfoDownloaded = favorite.Value.DownloadedMetaInfo;
                var metaInfo = favorite.Value.MetaInfo;

                ImGui.AlignTextToFramePadding();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey, !metaInfoDownloaded))
                using (ImRaii.PushColor(ImGuiCol.Text, UiSharedService.GetBoolColor(metaInfo != null), metaInfoDownloaded))
                    ImGui.TextUnformatted(string.IsNullOrEmpty(metaInfo?.Description) ? favorite.Key : metaInfo.Description);

                var iconSize = _uiSharedService.GetIconData(FontAwesomeIcon.Check);
                var refreshButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowsSpin);
                var applyButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowRight);
                var addButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
                var offsetFromRight = maxPos - (iconSize.X + refreshButtonSize.X + applyButtonSize.X + addButtonSize.X + (ImGui.GetStyle().ItemSpacing.X * 3.5f));

                ImGui.SameLine();
                ImGui.SetCursorPosX(offsetFromRight);
                if (metaInfoDownloaded)
                {
                    _uiSharedService.BooleanToColoredIcon(metaInfo != null, false);
                    if (metaInfo != null)
                    {
                        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.MetaInfoPresent") + UiSharedService.TooltipSeparator
                            + string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Favorites.MetaUpdated"), metaInfo.UpdatedDate) + Environment.NewLine
                            + string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Favorites.MetaDescription"), metaInfo.Description) + Environment.NewLine
                            + string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Favorites.MetaPoses"), metaInfo.PoseData.Count));
                    }
                    else
                    {
                        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.MetaNotDownloaded") + UiSharedService.TooltipSeparator
                            + Loc.Get("CharaDataHub.Apply.Favorites.MetaNotAccessible"));
                    }
                }
                else
                {
                    _uiSharedService.IconText(FontAwesomeIcon.QuestionCircle, ImGuiColors.DalamudGrey);
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.MetaUnknown"));
                }

                ImGui.SameLine();
                bool isInTimeout = _charaDataManager.IsInTimeout(favorite.Key);
                using (ImRaii.Disabled(isInTimeout))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowsSpin))
                    {
                        _charaDataManager.DownloadMetaInfo(favorite.Key, false);
                    }
                }
                UiSharedService.AttachToolTip(isInTimeout ? Loc.Get("CharaDataHub.Apply.Favorites.RefreshTimeout")
                    : Loc.Get("CharaDataHub.Apply.Favorites.RefreshTooltip"));

                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight) && meta != null)
                    {
                        _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta);
                    }
                }, Loc.Get("CharaDataHub.Apply.Favorites.ApplyTooltip"), metaInfo, _hasValidGposeTarget, false);
                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Plus) && meta != null)
                    {
                        _ = _charaDataManager.SpawnAndApplyData(meta);
                    }
                }, Loc.Get("CharaDataHub.Apply.Favorites.SpawnTooltip"), metaInfo, _hasValidGposeTarget, true);

                var uid = favorite.Key.Split(":")[0];
                string uidText;
                if (metaInfo != null)
                {
                    uidText = metaInfo.Uploader.AliasOrUID;
                }
                else
                {
                    uidText = uid;
                }

                var uidNote = _serverConfigurationManager.GetNoteForUid(uid);
                if (uidNote != null)
                {
                    uidText = $"{uidNote} ({uidText})";
                }
                UiSharedService.ColorText($"[{Loc.Get("CharaDataHub.Mcd.Online.TypeCharaData")}] {uidText}", ImGuiColors.HealerGreen);

                ImGui.TextUnformatted(Loc.Get("CharaDataHub.Apply.LastUse"));
                ImGui.SameLine();
                ImGui.TextUnformatted(favorite.Value.Favorite.LastDownloaded == DateTime.MaxValue
                    ? Loc.Get("CharaDataHub.Apply.Never")
                    : favorite.Value.Favorite.LastDownloaded.ToString(CultureInfo.CurrentCulture));

                var desc = favorite.Value.Favorite.CustomDescription;
                ImGui.SetNextItemWidth(maxPos - xPos);
                if (ImGui.InputTextWithHint("##desc", Loc.Get("CharaDataHub.Apply.FavoriteDescPlaceholder"), ref desc, 100))
                {
                    favorite.Value.Favorite.CustomDescription = desc;
                    _configService.Save();
                }

                DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
            });

            ImGuiHelpers.ScaledDummy(5);
        }

        return count;
    }

    private int DrawLibraryOwn(string search, bool grouped)
    {
        int count = 0;

        // Live entries
        foreach (var data in _charaDataManager.OwnCharaData.Values)
        {
            var hasMetaInfo = _charaDataManager.TryGetMetaInfo(data.FullId, out var metaInfo);
            if (!hasMetaInfo || metaInfo == null) continue;
            if (!string.IsNullOrWhiteSpace(search)
                && !(data.Description ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                && !data.FullId.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;
            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.YourOwn");
            DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, metaInfo, true);
        }

        var localDescriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localMcdfFolder = _configService.Current.McdfLocalFolder;
        if (!string.IsNullOrEmpty(localMcdfFolder) && Directory.Exists(localMcdfFolder))
        {
            foreach (var file in _localMcdfFiles.Where(f => !string.IsNullOrEmpty(f.FilePath)
                && !string.Equals(f.SubFolder, "Import", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(search) || f.Description.Contains(search, StringComparison.OrdinalIgnoreCase))))
            {
                localDescriptions.Add(file.Description);

                LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.YourOwn");
                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DrawCard("localOwn" + file.FilePath, () =>
                {
                    ImGui.AlignTextToFramePadding();
                    _uiSharedService.IconText(FontAwesomeIcon.FileArchive, ImGuiColors.DalamudGrey);
                    ImGui.SameLine();
                    var folderTag = string.IsNullOrEmpty(file.SubFolder) ? "" : $"{file.SubFolder}/";
                    UiSharedService.ColorText("[Local]", ImGuiColors.DalamudGrey);
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{folderTag}{file.Description}");

                    ImGui.SameLine();
                    using (ImRaii.Disabled(!_hasValidGposeTarget))
                    {
                        if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                        {
                            _charaDataManager.LoadMcdf(file.FilePath);
                            _ = _charaDataManager.McdfApplyToGposeTarget();
                        }
                    }
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.ApplyTooltip"));

                    UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Meta.LastUpdate"), file.LastModified.ToString("dd/MM/yyyy HH:mm")), ImGuiColors.DalamudGrey);
                }, stretchWidth: true);
            }
        }

        // MCDF cloud entries (skip if a local copy exists)
        foreach (var entry in _mcdfShareManager.OwnShares)
        {
            var displayName = string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description;
            if (localDescriptions.Contains(displayName)) continue;
            if (!string.IsNullOrWhiteSpace(search) && !displayName.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;

            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.YourOwn");
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawCard("mcdfOwn" + entry.Id, () =>
            {
                ImGui.AlignTextToFramePadding();
                var mcdfFavId = $"mcdf:{entry.Id:D}";
                DrawFavorite(mcdfFavId, displayName);
                ImGui.SameLine();
                _uiSharedService.IconText(FontAwesomeIcon.CloudUploadAlt, UiSharedService.AccentColor);
                ImGui.SameLine();
                UiSharedService.ColorText($"[{Loc.Get("CharaDataHub.Mcd.Online.TypeMcdf")}]", UiSharedService.AccentColor);
                ImGui.SameLine();
                ImGui.TextUnformatted(displayName);

                ImGui.SameLine();
                using (ImRaii.Disabled(!_hasValidGposeTarget))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                    {
                        _ = _mcdfShareManager.ApplyShareAsync(entry.Id, CancellationToken.None);
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Favorites.ApplyTooltip"));

                UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Meta.LastUpdate"), entry.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")), ImGuiColors.DalamudGrey);
            }, stretchWidth: true);
        }

        return count;
    }

    private int DrawLibraryImported(string search, bool grouped)
    {
        int count = 0;
        var mcdfFolder = _configService.Current.McdfLocalFolder;
        if (string.IsNullOrEmpty(mcdfFolder)) return 0;

        if ((DateTime.UtcNow - _importedFilesScanTime).TotalSeconds > 2)
        {
            _importedFilesScanTime = DateTime.UtcNow;
            var importDir = Path.Combine(mcdfFolder, "Import");
            try
            {
                _importedFiles = Directory.Exists(importDir)
                    ? Directory.EnumerateFiles(importDir, "*.mcdf")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList()
                    : [];
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Lecture du dossier d'import MCDF impossible");
                _importedFiles = [];
            }
        }

        foreach (var fi in _importedFiles)
        {
            var desc = fi.Name.Replace(".mcdf", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (!MatchesSearch(search, desc)) continue;
            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.Imported");

            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawCard("imported" + fi.FullName, () =>
            {
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(FontAwesomeIcon.FileImport, ImGuiColors.DalamudGrey);
                ImGui.SameLine();
                ImGui.TextUnformatted(desc);

                ImGui.SameLine();
                using (ImRaii.Disabled(!_hasValidGposeTarget))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                    {
                        _charaDataManager.LoadMcdf(fi.FullName);
                        _ = _charaDataManager.McdfApplyToGposeTarget();
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Imported.ApplyTooltip"));
                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                {
                    try { File.Delete(fi.FullName); } catch (IOException) { /* best-effort delete */ }
                    _importedFilesScanTime = DateTime.MinValue;
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Local.DeleteTooltip"));

                UiSharedService.ColorTextWrapped($"{FormatFileSize(fi.Length)} - {fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm")}", ImGuiColors.DalamudGrey);
            }, stretchWidth: true);
        }

        return count;
    }

    private int DrawLibraryShared(string search, bool grouped)
    {
        int count = 0;

        foreach (var entry in _mcdfShareManager.SharedShares)
        {
            var name = string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description;
            var owner = string.IsNullOrEmpty(entry.OwnerAlias) ? entry.OwnerUid : entry.OwnerAlias;
            if (!MatchesSearch(search, name) && !MatchesSearch(search, owner)) continue;
            LibraryItemStart(ref count, grouped, "CharaDataHub.Apply.Tabs.SharedWithYou");

            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawCard("sharedShare" + entry.Id, () =>
            {
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(FontAwesomeIcon.ShareAlt, UiSharedService.AccentColor);
                ImGui.SameLine();
                ImGui.TextUnformatted(name);

                ImGui.SameLine();
                using (ImRaii.Disabled(!_hasValidGposeTarget))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight))
                    {
                        _ = _mcdfShareManager.ApplyShareAsync(entry.Id, CancellationToken.None);
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.SharedWithYou.ApplyTarget"));
                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Save))
                {
                    var baseName = SanitizeFileName(entry.Description, entry.Id.ToString("D", CultureInfo.InvariantCulture));
                    _fileDialogManager.SaveFileDialog(Loc.Get("CharaDataHub.Apply.SharedWithYou.SaveDialog"), ".mcdf", baseName + ".mcdf", ".mcdf", (success, path) =>
                    {
                        if (!success || string.IsNullOrEmpty(path)) return;
                        _ = _mcdfShareManager.ExportShareAsync(entry.Id, path, CancellationToken.None);
                    });
                }
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.SharedWithYou.Save"));

                var expires = entry.ExpiresAtUtc.HasValue
                    ? entry.ExpiresAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : Loc.Get("CharaDataHub.Apply.Never");
                UiSharedService.ColorTextWrapped(
                    $"{owner} - {Loc.Get("CharaDataHub.Apply.SharedWithYou.Expires")} : {expires}",
                    ImGuiColors.DalamudGrey);
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.SharedWithYou.OwnerUid"), entry.OwnerUid));
            }, stretchWidth: true);
        }

        return count;
    }

    private void DrawMcdfImport()
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcdf.Import.Title"), FontAwesomeIcon.FileImport);

        DrawHelpFoldout(Loc.Get("CharaDataHub.Mcdf.Import.Help"));

        ImGuiHelpers.ScaledDummy(5);

        if (_charaDataManager.LoadedMcdfHeader == null || _charaDataManager.LoadedMcdfHeader.IsCompleted)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, Loc.Get("CharaDataHub.Mcdf.Import.Load")))
            {
                _fileDialogManager.OpenFileDialog(Loc.Get("CharaDataHub.Mcdf.Import.PickFile"), ".mcdf", (success, paths) =>
                {
                    if (!success) return;
                    if (paths.FirstOrDefault() is not { } path) return;

                    _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                    _configService.Save();

                    _charaDataManager.LoadMcdf(path);
                }, 1, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
            }
            UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Import.LoadTooltip"));
            if ((_charaDataManager.LoadedMcdfHeader?.IsCompleted ?? false))
            {
                ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Import.LoadedFile"));
                ImGui.SameLine(200);
                UiSharedService.TextWrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.FilePath);
                ImGui.TextUnformatted(Loc.Get("CharaDataHub.Apply.Description"));
                ImGui.SameLine(200);
                UiSharedService.TextWrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.CharaFileData.Description);

                ImGuiHelpers.ScaledDummy(5);

                var mcdfLocalFolder = _configService.Current.McdfLocalFolder;
                if (!string.IsNullOrEmpty(mcdfLocalFolder))
                {
                    var importDir = Path.Combine(mcdfLocalFolder, "Import");
                    var sourcePath = _charaDataManager.LoadedMcdfHeader.Result.LoadedFile.FilePath;
                    var fileName = Path.GetFileName(sourcePath);
                    var destPath = Path.Combine(importDir, fileName);
                    bool alreadyExists = File.Exists(destPath);

                    using (ImRaii.Disabled(alreadyExists))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("CharaDataHub.Mcdf.Import.SaveToImport")))
                        {
                            try
                            {
                                Directory.CreateDirectory(importDir);
                                File.Copy(sourcePath, destPath, false);
                                _localMcdfScanTime = DateTime.MinValue;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to copy MCDF to import folder");
                            }
                        }
                    }
                    if (alreadyExists)
                        UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Mcdf.Import.AlreadyExists"));
                    else
                        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Import.SaveToImportTooltip"), importDir));
                }
                else
                {
                    UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.NoFolderConfigured"), ImGuiColors.DalamudGrey);
                }

                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ApplyHint"), ImGuiColors.DalamudGrey);
            }
            if ((_charaDataManager.LoadedMcdfHeader?.IsFaulted ?? false) || (_charaDataManager.McdfApplicationTask?.IsFaulted ?? false))
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ReadError"),
                    UiSharedService.AccentColor);
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.ReadErrorNote"), UiSharedService.AccentColor);
            }
        }
        else
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Import.Loading"), UiSharedService.AccentColor);
        }
    }

    private void DrawMcdfExport()
    {
        UiSharedService.BeginSectionCard("Export de fichier MCDF", FontAwesomeIcon.FileExport);

        DrawHelpFoldout("Cette fonctionnalité vous permet de compresser votre personnage dans un fichier MCDF et de l'envoyer manuellement à d'autres personnes. Les fichiers MCDF peuvent être importés pendant le GPose. " +
            "Sachez qu'il est possible que des personnes créent des exporteurs non officiels pour extraire les données contenues.");

        ImGuiHelpers.ScaledDummy(5);

        ImGui.Checkbox("##readExport", ref _readExport);
        ImGui.SameLine();
        UiSharedService.TextWrapped("Je comprends qu'en exportant les données de mon personnage dans un fichier et en les envoyant à d'autres personnes, je cède irrévocablement l'apparence actuelle de mon personnage. Les personnes avec qui je partage mes données ont la possibilité de les partager avec d'autres sans aucune limitation.");

        if (_readExport)
        {
            ImGui.Indent();

            ImGui.InputTextWithHint("Description de l'export", "Cette description sera affichée lors du chargement des données", ref _exportDescription, 255);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Exporter le personnage en MCDF"))
            {
                string defaultFileName = string.IsNullOrEmpty(_exportDescription)
                    ? "export.mcdf"
                    : SanitizeFileName(_exportDescription, "export") + ".mcdf";
                _uiSharedService.FileDialogManager.SaveFileDialog("Export Character to file", ".mcdf", defaultFileName, ".mcdf", (success, path) =>
                {
                    if (!success) return;

                    _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                    _configService.Save();

                    _charaDataManager.SaveMareCharaFile(_exportDescription, path);
                    _exportDescription = string.Empty;
                }, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
            }
            UiSharedService.ColorTextWrapped("Note: For best results make sure you have everything you want to be shared as well as the correct character appearance" +
                " equipped and redraw your character before exporting.", UiSharedService.AccentColor);

            ImGui.Unindent();
        }
    }

    private void BeginMcdfShare(string sourceId, bool isLocal, string name,
        IEnumerable<string>? individuals = null, IEnumerable<string>? syncshells = null, DateTime? expiresAtUtc = null)
    {
        ResetMcdfShare();
        _mcdfShareSourceId = sourceId;
        _mcdfShareSourceIsLocal = isLocal;
        _mcdfShareTargetName = name;
        _mcdfShareDescription = name;
        if (individuals != null) _mcdfShareAllowedIndividuals.AddRange(individuals);
        if (syncshells != null) _mcdfShareAllowedSyncshells.AddRange(syncshells);
        _mcdfShareExpireDays = expiresAtUtc.HasValue
            ? Math.Max(1, (int)Math.Ceiling((expiresAtUtc.Value - DateTime.UtcNow).TotalDays))
            : 0;
    }

    private void ResetMcdfShare()
    {
        _mcdfShareDescription = string.Empty;
        _mcdfShareTargetName = string.Empty;
        _mcdfShareAllowedIndividuals.Clear();
        _mcdfShareAllowedSyncshells.Clear();
        _mcdfShareIndividualInput = string.Empty;
        _mcdfShareIndividualDropdownSelection = string.Empty;
        _mcdfShareSyncshellInput = string.Empty;
        _mcdfShareSyncshellDropdownSelection = string.Empty;
        _mcdfShareExpireDays = 0;
        _mcdfShareSourceId = string.Empty;
        _mcdfShareSourceIsLocal = false;
    }

    // Ouvert depuis la ligne d'une entrée : la source est déjà connue, il ne reste qu'à dire à qui.
    private void DrawMcdfShareForm()
    {
        if (string.IsNullOrEmpty(_mcdfShareSourceId)) return;

        UiSharedService.BeginSectionCard(
            string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Share.TitleFor"), _mcdfShareTargetName),
            FontAwesomeIcon.ShareAlt);

        if (_mcdfShareSourceIsLocal)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Share.LocalWarning"), ImGuiColors.DalamudYellow);
        }

        ImGuiHelpers.ScaledDummy(3);
        ImGui.InputTextWithHint("##mcdfShareDescription", "Description", ref _mcdfShareDescription, 128);
        ImGui.InputInt(Loc.Get("CharaDataHub.Mcdf.Share.Expiration"), ref _mcdfShareExpireDays);

        DrawMcdfShareIndividualDropdown();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint("##mcdfShareUidInput", Loc.Get("CharaDataHub.Mcdf.Share.PairPlaceholder"), ref _mcdfShareIndividualInput, 32))
        {
            _mcdfShareIndividualDropdownSelection = string.Empty;
        }
        ImGui.SameLine();
        var normalizedUid = NormalizeUidCandidate(_mcdfShareIndividualInput);
        using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedUid)
            || _mcdfShareAllowedIndividuals.Any(p => string.Equals(p, normalizedUid, StringComparison.OrdinalIgnoreCase))))
        {
            if (ImGui.SmallButton("Ajouter"))
            {
                _mcdfShareAllowedIndividuals.Add(normalizedUid);
                _mcdfShareIndividualInput = string.Empty;
                _mcdfShareIndividualDropdownSelection = string.Empty;
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Share.PairLabel"));
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcdf.Share.PairHelp"));

        foreach (var uid in _mcdfShareAllowedIndividuals.ToArray())
        {
            using (ImRaii.PushId("mcdfShareUid" + uid))
            {
                ImGui.BulletText(FormatPairLabel(uid));
                ImGui.SameLine();
                if (ImGui.SmallButton("Retirer"))
                {
                    _mcdfShareAllowedIndividuals.Remove(uid);
                }
            }
        }

        DrawMcdfShareSyncshellDropdown();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint("##mcdfShareSyncshellInput", Loc.Get("CharaDataHub.Mcdf.Share.SyncshellPlaceholder"), ref _mcdfShareSyncshellInput, 32))
        {
            _mcdfShareSyncshellDropdownSelection = string.Empty;
        }
        ImGui.SameLine();
        var normalizedSyncshell = NormalizeSyncshellCandidate(_mcdfShareSyncshellInput);
        using (ImRaii.Disabled(string.IsNullOrEmpty(normalizedSyncshell)
            || _mcdfShareAllowedSyncshells.Any(p => string.Equals(p, normalizedSyncshell, StringComparison.OrdinalIgnoreCase))))
        {
            if (ImGui.SmallButton("Ajouter"))
            {
                _mcdfShareAllowedSyncshells.Add(normalizedSyncshell);
                _mcdfShareSyncshellInput = string.Empty;
                _mcdfShareSyncshellDropdownSelection = string.Empty;
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Share.SyncshellLabel"));
        _uiSharedService.DrawHelpText(Loc.Get("CharaDataHub.Mcdf.Share.SyncshellHelp"));

        foreach (var shell in _mcdfShareAllowedSyncshells.ToArray())
        {
            using (ImRaii.PushId("mcdfShareShell" + shell))
            {
                ImGui.BulletText(FormatSyncshellLabel(shell));
                ImGui.SameLine();
                if (ImGui.SmallButton("Retirer"))
                {
                    _mcdfShareAllowedSyncshells.Remove(shell);
                }
            }
        }

        using (ImRaii.Disabled(_mcdfShareManager.IsBusy
            || (_mcdfShareAllowedIndividuals.Count == 0 && _mcdfShareAllowedSyncshells.Count == 0)))
        {
            if (ImGui.Button(Loc.Get("CharaDataHub.Mcdf.Share.Create")))
            {
                DateTime? expiresAt = _mcdfShareExpireDays <= 0 ? null : DateTime.UtcNow.AddDays(_mcdfShareExpireDays);
                if (_mcdfShareSourceIsLocal)
                {
                    _ = _mcdfShareManager.CreateShareFromFileAsync(_mcdfShareDescription, _mcdfShareSourceId, _mcdfShareAllowedIndividuals.ToList(), _mcdfShareAllowedSyncshells.ToList(), expiresAt, CancellationToken.None);
                }
                else
                {
                    // Server share: update access lists on existing share
                    var shareGuid = Guid.Parse(_mcdfShareSourceId);
                    _ = _mcdfShareManager.UpdateShareAsync(new UmbraSync.API.Dto.McdfShare.McdfShareUpdateRequestDto
                    {
                        ShareId = shareGuid,
                        Description = _mcdfShareDescription,
                        AllowedIndividuals = _mcdfShareAllowedIndividuals.ToList(),
                        AllowedSyncshells = _mcdfShareAllowedSyncshells.ToList(),
                        ExpiresAtUtc = expiresAt,
                    });
                }
                ResetMcdfShare();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.Get("CharaDataHub.Mcdf.Share.Cancel")))
        {
            ResetMcdfShare();
        }
    }

    private void DrawMcdfMyShares()
    {
        UiSharedService.BeginSectionCard(Loc.Get("CharaDataHub.Mcdf.Share.MyShares"), FontAwesomeIcon.List);

        if (_mcdfShareManager.IsBusy)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Local.Uploading"), ImGuiColors.DalamudYellow);
        }
        else if (!string.IsNullOrEmpty(_mcdfShareManager.LastError))
        {
            UiSharedService.ColorTextWrapped(_mcdfShareManager.LastError!, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(_mcdfShareManager.LastSuccess))
        {
            UiSharedService.ColorTextWrapped(_mcdfShareManager.LastSuccess!, ImGuiColors.HealerGreen);
        }

        var sharedEntries = _mcdfShareManager.OwnShares.Where(s => s.AllowedIndividuals.Count > 0 || s.AllowedSyncshells.Count > 0).ToList();

        if (sharedEntries.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Mcdf.Share.NoShares"), ImGuiColors.DalamudGrey);
        }
        else if (ImGui.BeginTable("mcdf-share-list", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcdf.Local.ColDate"), ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Apply.SharedWithYou.Expires"), ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcdf.Share.Access"), ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn(Loc.Get("CharaDataHub.Mcdf.OwnShares.Downloads"), ImGuiTableColumnFlags.WidthFixed, 40);
            var style2 = ImGui.GetStyle();
            float Bw(string l) => ImGui.CalcTextSize(l).X + style2.FramePadding.X * 2f;
            float actW2 = Bw(Loc.Get("CharaDataHub.Mcdf.Local.Delete")) + 2f;
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actW2);
            ImGui.TableHeadersRow();

            foreach (var entry in sharedEntries)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 26f);
                using var rowId = ImRaii.PushId("shareList" + entry.Id);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(entry.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(entry.ExpiresAtUtc.HasValue ? entry.ExpiresAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy") : Loc.Get("CharaDataHub.Apply.Never"));

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Mcdf.Share.AccessSummary"), entry.AllowedIndividuals.Count, entry.AllowedSyncshells.Count));
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    if (entry.AllowedIndividuals.Count > 0)
                    {
                        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Share.AllowedUids"));
                        foreach (var uid in entry.AllowedIndividuals)
                            ImGui.BulletText(FormatUidWithName(uid));
                    }
                    if (entry.AllowedSyncshells.Count > 0)
                    {
                        if (entry.AllowedIndividuals.Count > 0) ImGui.Separator();
                        ImGui.TextUnformatted(Loc.Get("CharaDataHub.Mcdf.Share.AllowedSyncshells"));
                        foreach (var gid in entry.AllowedSyncshells)
                            ImGui.BulletText(FormatSyncshellLabel(gid));
                    }
                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(entry.DownloadCount.ToString(CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                if (ImGui.SmallButton(Loc.Get("CharaDataHub.Mcdf.Share.Revoke")))
                {
                    _ = _mcdfShareManager.UpdateShareAsync(new UmbraSync.API.Dto.McdfShare.McdfShareUpdateRequestDto
                    {
                        ShareId = entry.Id,
                        Description = entry.Description,
                        AllowedIndividuals = [],
                        AllowedSyncshells = [],
                        ExpiresAtUtc = entry.ExpiresAtUtc,
                    });
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawMcdfShareIndividualDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_mcdfShareIndividualDropdownSelection)
            ? _mcdfShareIndividualInput
            : _mcdfShareIndividualDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "Sélectionner un pair synchronisé..."
            : FormatPairLabel(previewSource);

        using var combo = ImRaii.Combo("##mcdfShareUidDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo)
        {
            return;
        }

        foreach (var pair in _pairManager.DirectPairs
            .OrderBy(p => p.GetNoteOrName() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = pair.UserData.UID;
            var display = FormatPairLabel(normalized);
            bool selected = string.Equals(normalized, _mcdfShareIndividualDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _mcdfShareIndividualDropdownSelection = normalized;
                _mcdfShareIndividualInput = normalized;
            }
        }
    }

    private void DrawMcdfShareSyncshellDropdown()
    {
        ImGui.SetNextItemWidth(220f);
        var previewSource = string.IsNullOrEmpty(_mcdfShareSyncshellDropdownSelection)
            ? _mcdfShareSyncshellInput
            : _mcdfShareSyncshellDropdownSelection;
        var previewLabel = string.IsNullOrEmpty(previewSource)
            ? "Sélectionner une syncshell..."
            : FormatSyncshellLabel(previewSource);

        using var combo = ImRaii.Combo("##mcdfShareSyncshellDropdown", previewLabel, ImGuiComboFlags.None);
        if (!combo)
        {
            return;
        }

        foreach (var group in _pairManager.Groups.Values
            .OrderBy(g => _serverConfigurationManager.GetNoteForGid(g.GID) ?? g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var gid = group.GID;
            var display = FormatSyncshellLabel(gid);
            bool selected = string.Equals(gid, _mcdfShareSyncshellDropdownSelection, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(display, selected))
            {
                _mcdfShareSyncshellDropdownSelection = gid;
                _mcdfShareSyncshellInput = gid;
            }
        }
    }

    private string NormalizeUidCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var trimmed = candidate.Trim();

        foreach (var pair in _pairManager.DirectPairs)
        {
            var alias = pair.UserData.Alias;
            var aliasOrUid = pair.UserData.AliasOrUID;
            var note = pair.GetNoteOrName();

            if (string.Equals(pair.UserData.UID, trimmed, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(alias) && string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
                || string.Equals(aliasOrUid, trimmed, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(note) && string.Equals(note, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.UserData.UID;
            }
        }

        return trimmed;
    }

    private string NormalizeSyncshellCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        var trimmed = candidate.Trim();

        foreach (var group in _pairManager.Groups.Values)
        {
            var alias = group.GroupAlias;
            var aliasOrGid = group.GroupAliasOrGID;
            var note = _serverConfigurationManager.GetNoteForGid(group.GID);

            if (string.Equals(group.GID, trimmed, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(alias) && string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
                || string.Equals(aliasOrGid, trimmed, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(note) && string.Equals(note, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return group.GID;
            }
        }

        return trimmed;
    }

    private string FormatUidWithName(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
        var note = _serverConfigurationManager.GetNoteForUid(uid);
        if (!string.IsNullOrEmpty(note)) return note;
        var pair = _pairManager.DirectPairs.FirstOrDefault(p => string.Equals(p.UserData.UID, uid, StringComparison.OrdinalIgnoreCase));
        if (pair != null)
        {
            var alias = pair.UserData.Alias;
            if (!string.IsNullOrEmpty(alias)) return alias;
            return pair.UserData.AliasOrUID;
        }
        return uid;
    }

    private string FormatPairLabel(string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return string.Empty;
        }

        foreach (var pair in _pairManager.DirectPairs)
        {
            var alias = pair.UserData.Alias;
            var aliasOrUid = pair.UserData.AliasOrUID;
            var note = pair.GetNoteOrName();

            if (string.Equals(pair.UserData.UID, candidate, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(alias) && string.Equals(alias, candidate, StringComparison.OrdinalIgnoreCase))
                || string.Equals(aliasOrUid, candidate, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(note) && string.Equals(note, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrEmpty(note) ? aliasOrUid : $"{note} ({aliasOrUid})";
            }
        }

        return candidate;
    }

    private string FormatSyncshellLabel(string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return string.Empty;
        }

        foreach (var group in _pairManager.Groups.Values)
        {
            var alias = group.GroupAlias;
            var aliasOrGid = group.GroupAliasOrGID;
            var note = _serverConfigurationManager.GetNoteForGid(group.GID);

            if (string.Equals(group.GID, candidate, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(alias) && string.Equals(alias, candidate, StringComparison.OrdinalIgnoreCase))
                || string.Equals(aliasOrGid, candidate, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(note) && string.Equals(note, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return string.IsNullOrEmpty(note) ? aliasOrGid : $"{note} ({aliasOrGid})";
            }
        }

        return candidate;
    }

    private void DrawMetaInfoData(string selectedGposeActor, bool hasValidGposeTarget, CharaDataMetaInfoExtendedDto data, bool canOpen = false)
    {
        ImGuiHelpers.ScaledDummy(5);
        using var entryId = ImRaii.PushId(data.FullId);

        var startPos = ImGui.GetCursorPosX();
        var maxPos = ImGui.GetWindowContentRegionMax().X;
        var availableWidth = maxPos - startPos;
        UiSharedService.DrawGrouped(() =>
        {
            ImGui.AlignTextToFramePadding();
            DrawAddOrRemoveFavorite(data);

            ImGui.SameLine();
            var favPos = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            UiSharedService.ColorText(data.FullId, UiSharedService.GetSuccessColor(data.CanBeDownloaded));
            if (!data.CanBeDownloaded)
            {
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Meta.Incomplete"));
            }

            var offsetFromRight = availableWidth - _uiSharedService.GetIconData(FontAwesomeIcon.Calendar).X - _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X
                - _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X - ImGui.GetStyle().ItemSpacing.X * 2;

            ImGui.SameLine();
            ImGui.SetCursorPosX(offsetFromRight);
            _uiSharedService.IconText(FontAwesomeIcon.Calendar);
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Meta.LastUpdate"), data.UpdatedDate));

            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight) && meta != null)
                {
                    _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta);
                }
            }, string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Meta.ApplyToTarget"), CharaName(selectedGposeActor)), data, hasValidGposeTarget, false);
            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Plus) && meta != null)
                {
                    _ = _charaDataManager.SpawnAndApplyData(meta);
                }
            }, Loc.Get("CharaDataHub.Apply.Meta.SpawnAndApply"), data, hasValidGposeTarget, true);

            using var indent = ImRaii.PushIndent(favPos - startPos);

            if (canOpen)
            {
                using (ImRaii.Disabled(_isHandlingSelf))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Edit, Loc.Get("CharaDataHub.Apply.Meta.OpenEditor")))
                    {
                        SelectedDtoId = data.Id;
                        _openMcdOnlineOnNextRun = true;
                    }
                }
                if (_isHandlingSelf)
                {
                    UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.Apply.Meta.CannotWhileApplied"));
                }
            }

            if (string.IsNullOrEmpty(data.Description))
            {
                UiSharedService.ColorTextWrapped(Loc.Get("CharaDataHub.Apply.Meta.NoDescription"), ImGuiColors.DalamudGrey, availableWidth);
            }
            else
            {
                UiSharedService.TextWrapped(data.Description, availableWidth);
            }

            DrawPoseData(data, selectedGposeActor, hasValidGposeTarget);
        });
    }


    private void DrawPoseData(CharaDataMetaInfoExtendedDto? metaInfo, string actor, bool hasValidGposeTarget)
    {
        if (metaInfo == null || !metaInfo.HasPoses) return;

        bool isInGpose = _uiSharedService.IsInGpose;
        var start = ImGui.GetCursorPosX();
        foreach (var item in metaInfo.PoseExtended)
        {
            if (!item.HasPoseData) continue;

            float DrawIcon(float s)
            {
                ImGui.SetCursorPosX(s);
                var posX = ImGui.GetCursorPosX();
                _uiSharedService.IconText(item.HasWorldData ? FontAwesomeIcon.Circle : FontAwesomeIcon.Running);
                if (item.HasWorldData)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    using var col = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.WindowBg));
                    _uiSharedService.IconText(FontAwesomeIcon.Running);
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    _uiSharedService.IconText(FontAwesomeIcon.Running);
                }
                ImGui.SameLine();
                return ImGui.GetCursorPosX();
            }

            string tooltip = string.IsNullOrEmpty(item.Description) ? Loc.Get("CharaDataHub.Apply.Pose.NoDescription") : string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Pose.Description"), item.Description);
            if (!isInGpose)
            {
                start = DrawIcon(start);
                UiSharedService.AttachToolTip(tooltip + UiSharedService.TooltipSeparator + (item.HasWorldData ? GetWorldDataTooltipText(item) + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.Apply.Pose.ShowOnMap") : string.Empty));
                if (item.HasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(item.Position, item.Map);
                }
            }
            else
            {
                tooltip += UiSharedService.TooltipSeparator + string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Pose.ApplyToTarget"), CharaName(actor));
                if (item.HasWorldData) tooltip += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, Loc.Get("CharaDataHub.Apply.Pose.ApplyWorldPos"), CharaName(actor))
                        + UiSharedService.TooltipSeparator + Loc.Get("CharaDataHub.Apply.Pose.WorldPosCaution");
                start = GposePoseAction(currentStart =>
                {
                    var newStart = DrawIcon(currentStart);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        _ = _charaDataManager.ApplyPoseData(item, actor);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && UiSharedService.CtrlPressed())
                    {
                        _ = _charaDataManager.ApplyWorldDataToTarget(item, actor);
                    }

                    return newStart;
                }, tooltip, hasValidGposeTarget, start);
                ImGui.SameLine();
            }
        }
        if (metaInfo.PoseExtended.Any()) ImGui.NewLine();
    }



    private void DrawProfileBrowser(Vector4 accent)
    {
        var cachedProfiles = _umbraProfileManager.GetCachedProfiles();

        var currentUid = _umbraProfileManager.CurrentUid;
        var search = _profileBrowserSearch;
        var filtered = cachedProfiles.Where(p =>
        {
            if (currentUid != null && string.Equals(p.Key.User.UID, currentUid, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(p.Profile.RpFirstName) && string.IsNullOrWhiteSpace(p.Profile.RpLastName))
                return false;
            if (string.IsNullOrEmpty(search)) return true;

            // Comparaison insensible à la casse plutôt qu'une mise en minuscules : le filtre est
            // évalué à chaque frame pour chaque profil en cache, une chaîne allouée par champ.
            return Matches(p.Key.User.AliasOrUID) || Matches(p.Key.CharName)
                   || Matches(p.Profile.RpFirstName) || Matches(p.Profile.RpLastName)
                   || Matches(_serverConfigurationManager.GetNoteForUid(p.Key.User.UID));

            bool Matches(string? value)
                => value != null && value.Contains(search, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            string.Format(CultureInfo.InvariantCulture, Loc.Get("Settings.ProfileBrowser.CachedProfiles"), filtered.Count));
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Loc.Get("Settings.ProfileBrowser.ClearCache")).X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Settings.ProfileBrowser.ClearCache")))
        {
            _umbraProfileManager.ClearPersistedProfileCache();
            foreach (var pending in _profileBrowserTextureTasks.Values)
                pending.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            _profileBrowserTextureTasks.Clear();
            _profileCardHeights.Clear();
        }
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##profileBrowserSearch", Loc.Get("Settings.ProfileBrowser.SearchHint"), ref _profileBrowserSearch, 100);

        ImGuiHelpers.ScaledDummy(4f);

        if (filtered.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(10f);
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Settings.ProfileBrowser.NoResults"));
            return;
        }

        if (!ImGui.BeginChild("##profileBrowserScroll", ImGui.GetContentRegionAvail(), false))
            return;

        var cardSpacing = 6f * ImGuiHelpers.GlobalScale;
        var clipMin = ImGui.GetWindowPos().Y;
        var clipMax = clipMin + ImGui.GetWindowHeight();
        float defaultHeight = _profileCardHeights.Count > 0 ? _profileCardHeights.Values.Average() : 90f * ImGuiHelpers.GlobalScale;
        foreach (var entry in filtered)
        {
            var heightKey = $"{entry.Key.User.UID}_{entry.Key.CharName}_{entry.Key.WorldId}";
            var height = _profileCardHeights.TryGetValue(heightKey, out var knownHeight) ? knownHeight : defaultHeight;
            var top = ImGui.GetCursorScreenPos().Y;
            if (top + height < clipMin || top > clipMax)
            {
                ImGui.Dummy(new Vector2(1f, height + cardSpacing));
                continue;
            }

            var startY = ImGui.GetCursorPosY();
            DrawProfileCard(entry.Key, entry.Profile, accent);
            _profileCardHeights[heightKey] = ImGui.GetCursorPosY() - startY;
            ImGui.Dummy(new Vector2(1f, cardSpacing));
        }

        ImGui.EndChild();
    }

    private void DrawProfileCard(
        (API.Data.UserData User, string? CharName, uint? WorldId) key,
        UmbraProfileData profile, Vector4 accent)
    {
        var portraitSize = 64f * ImGuiHelpers.GlobalScale;

        // Resolve display data
        var firstName = profile.RpFirstName ?? string.Empty;
        var lastName = profile.RpLastName ?? string.Empty;
        var rpName = $"{firstName} {lastName}".Trim();
        var title = profile.RpTitle ?? string.Empty;
        var charName = key.CharName ?? "?";
        var worldId = key.WorldId ?? 0;
        var worldName = worldId > 0 && _dalamudUtilService.WorldData.Value.TryGetValue((ushort)worldId, out var wn) ? wn : string.Empty;
        var uid = key.User.AliasOrUID;
        var note = _serverConfigurationManager.GetNoteForUid(key.User.UID);

        // Name color
        var nameColor = !string.IsNullOrEmpty(profile.RpNameColor)
            ? UiSharedService.HexToVector4(profile.RpNameColor)
            : accent;

        // Texture cache key
        var texKey = $"{key.User.UID}_{key.CharName}_{key.WorldId}";
        var imgData = profile.RpImageData.Value;
        if (!_profileBrowserTextureTasks.TryGetValue(texKey, out var cachedTask)
            || (!ReferenceEquals(imgData, cachedTask.Data) && !imgData.AsSpan().SequenceEqual(cachedTask.Data)))
        {
            if (cachedTask.Task != null)
                cachedTask.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            cachedTask = (imgData, imgData.Length == 0
                ? Task.FromException<IDalamudTextureWrap>(new InvalidOperationException("Aucune image"))
                : Task.Run(() => _uiSharedService.LoadImageAsync(imgData)));
            _profileBrowserTextureTasks[texKey] = cachedTask;
        }
        IDalamudTextureWrap? profileTexture = cachedTask.Task.IsCompletedSuccessfully ? cachedTask.Task.Result : null;
        var cached = (Data: imgData, Texture: profileTexture);

        UiSharedService.DrawCard($"profileCard_{texKey}", () =>
        {
            var dl = ImGui.GetWindowDrawList();

            // Portrait (left side)
            var portraitStart = ImGui.GetCursorScreenPos();
            if (cached.Texture != null && cached.Texture.Handle != IntPtr.Zero && imgData.Length > 0)
            {
                bool tallerThanWide = cached.Texture.Height >= cached.Texture.Width;
                var stretchFactor = tallerThanWide ? portraitSize / cached.Texture.Height : portraitSize / cached.Texture.Width;
                var newW = cached.Texture.Width * stretchFactor;
                var newH = cached.Texture.Height * stretchFactor;
                var offX = (portraitSize - newW) / 2f;
                var offY = (portraitSize - newH) / 2f;

                var pMin = new Vector2(portraitStart.X + offX, portraitStart.Y + offY);
                var pMax = new Vector2(pMin.X + newW, pMin.Y + newH);
                dl.AddImageRounded(cached.Texture.Handle, pMin, pMax,
                    Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), 6f * ImGuiHelpers.GlobalScale);
            }
            else
            {
                dl.AddRectFilled(portraitStart,
                    new Vector2(portraitStart.X + portraitSize, portraitStart.Y + portraitSize),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), 6f * ImGuiHelpers.GlobalScale);
            }

            ImGui.Dummy(new Vector2(portraitSize, portraitSize));
            ImGui.SameLine();

            // Right side: info
            ImGui.BeginGroup();

            // RP Name (colored)
            var displayName = !string.IsNullOrEmpty(rpName) ? rpName : charName;
            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(displayName, nameColor);

            // Title (if any)
            if (!string.IsNullOrEmpty(title))
            {
                using var _ = _uiSharedService.GameFont.Push();
                UiSharedService.ColorText(title, nameColor);
            }

            // Character name @ World  [UID]
            var subLine = charName;
            if (!string.IsNullOrEmpty(worldName))
                subLine += $" @ {worldName}";
            subLine += $"  [{uid}]";
            ImGui.TextColored(ImGuiColors.DalamudGrey, subLine);

            // Note (if any)
            if (!string.IsNullOrEmpty(note))
                ImGui.TextColored(ImGuiColors.DalamudGrey2, note);

            ImGui.EndGroup();

            // Open button — right-aligned
            var pair = _pairManager.GetPairByUID(key.User.UID);
            if (pair != null)
            {
                var btnSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.ExternalLinkAlt, Loc.Get("Settings.ProfileBrowser.OpenProfile"));
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - btnSize - ImGui.GetStyle().ItemSpacing.X * 3 + ImGui.GetCursorPosX());
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, Loc.Get("Settings.ProfileBrowser.OpenProfile")))
                {
                    Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
                }
            }
        }, stretchWidth: true);
    }

    private static void DrawSubTabButtons(string[] subLabels, FontAwesomeIcon[] subIcons, ref int activeSubTab, System.Numerics.Vector4 accent)
    {
        const float btnH = 26f;
        const float btnSpacing = 5f;
        const float rounding = 4f;
        const float iconTextGap = 5f;
        const float btnPadX = 12f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        var iconStrs = new string[subLabels.Length];
        var iconSzs = new System.Numerics.Vector2[subLabels.Length];
        var labelSzs = new System.Numerics.Vector2[subLabels.Length];
        var naturalW = new float[subLabels.Length];
        float totalW = btnSpacing * (subLabels.Length - 1);

        for (int i = 0; i < subLabels.Length; i++)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            iconStrs[i] = subIcons[i].ToIconString();
            iconSzs[i] = ImGui.CalcTextSize(iconStrs[i]);
            ImGui.PopFont();
            labelSzs[i] = ImGui.CalcTextSize(subLabels[i]);
            naturalW[i] = iconSzs[i].X + iconTextGap + labelSzs[i].X + btnPadX;
            totalW += naturalW[i];
        }

        bool iconOnly = totalW > availWidth;

        var borderColor = new System.Numerics.Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new System.Numerics.Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new System.Numerics.Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < subLabels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            float w = iconOnly ? (availWidth - btnSpacing * (subLabels.Length - 1)) / subLabels.Length : naturalW[i];
            var p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##subTab_{i}", new System.Numerics.Vector2(w, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool isActive = activeSubTab == i;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new System.Numerics.Vector2(w, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new System.Numerics.Vector2(w, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            var textColor = isActive ? new System.Numerics.Vector4(1f, 1f, 1f, 1f)
                : hovered ? new System.Numerics.Vector4(0.9f, 0.85f, 1f, 1f)
                : new System.Numerics.Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            if (iconOnly)
            {
                var ix = p.X + (w - iconSzs[i].X) / 2f;
                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new System.Numerics.Vector2(ix, p.Y + (btnH - iconSzs[i].Y) / 2f), textColorU32, iconStrs[i]);
                ImGui.PopFont();
                if (hovered) UiSharedService.AttachToolTip(subLabels[i]);
            }
            else
            {
                var contentW = iconSzs[i].X + iconTextGap + labelSzs[i].X;
                var startX = p.X + (w - contentW) / 2f;
                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new System.Numerics.Vector2(startX, p.Y + (btnH - iconSzs[i].Y) / 2f), textColorU32, iconStrs[i]);
                ImGui.PopFont();
                dl.AddText(new System.Numerics.Vector2(startX + iconSzs[i].X + iconTextGap, p.Y + (btnH - labelSzs[i].Y) / 2f), textColorU32, subLabels[i]);
            }

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) activeSubTab = i;
        }
    }

    private void DrawHelpFoldout(string text)
    {
        if (_configService.Current.ShowHelpTexts)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawTree("Qu'est-ce que c'est ? (Explication / Aide)", () =>
            {
                UiSharedService.TextWrapped(text);
            });
        }
    }

    private void DisableDisabled(Action drawAction)
    {
        if (_disableUI) ImGui.EndDisabled();
        drawAction();
        if (_disableUI) ImGui.BeginDisabled();
    }

    private CancellationTokenSource EnsureFreshCts(ref CancellationTokenSource? cts)
    {
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        return cts;
    }

    private void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "Attempted to cancel CharaDataHubUi token after disposal");
        }

        cts.Dispose();
        cts = null;
    }
}