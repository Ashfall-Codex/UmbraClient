using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Internal;
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
    private readonly HousingShareManager? _housingShareManager_housing;
    private readonly HousingFurnitureScanner? _housingScanner;
    private CancellationTokenSource? _closalCts = new();
    private bool _disableUI = false;
    private CancellationTokenSource? _disposalCts = new();
    private string _exportDescription = string.Empty;
    private string _filterCodeNote = string.Empty;
    private string _filterDescription = string.Empty;
    private Dictionary<string, List<CharaDataMetaInfoExtendedDto>>? _filteredDict;
    private Dictionary<string, (CharaDataFavorite Favorite, CharaDataMetaInfoExtendedDto? MetaInfo, bool DownloadedMetaInfo)> _filteredFavorites = [];
    private bool _filterPoseOnly = false;
    private bool _filterWorldOnly = false;
    private string _gposeTarget = string.Empty;
    private bool _hasValidGposeTarget;
    private string _importCode = string.Empty;
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
    private string _sharedWithYouDescriptionFilter = string.Empty;
    private bool _sharedWithYouDownloadableFilter = false;
    private string _sharedWithYouOwnerFilter = string.Empty;
    private string _specificIndividualAdd = string.Empty;
    private string _specificGroupAdd = string.Empty;
    private bool _abbreviateCharaName = false;
    private string? _openComboHybridId = null;
    private (string Id, string? Alias, string AliasOrId, string? Note)[]? _openComboHybridEntries = null;
    private bool _comboHybridUsedLastFrame = false;
    private int _hubActiveTab;
    private int _creationSubTab;
    private bool _mcdfShareInitialized;
    private string _mcdfShareDescription = string.Empty;
    private readonly List<string> _mcdfShareAllowedIndividuals = new();
    private readonly List<string> _mcdfShareAllowedSyncshells = new();
    private string _mcdfShareIndividualDropdownSelection = string.Empty;
    private string _mcdfShareIndividualInput = string.Empty;
    private string _mcdfShareSyncshellDropdownSelection = string.Empty;
    private string _mcdfShareSyncshellInput = string.Empty;
    private int _mcdfShareExpireDays;
    private readonly UmbraProfileManager _umbraProfileManager;
    private string _profileBrowserSearch = string.Empty;
    private readonly Dictionary<string, (byte[] Data, IDalamudTextureWrap? Texture)> _profileBrowserTextures = new(StringComparer.Ordinal);

    public CharaDataHubUi(ILogger<CharaDataHubUi> logger, MareMediator mediator, PerformanceCollectorService performanceCollectorService,
                         CharaDataManager charaDataManager, CharaDataNearbyManager charaDataNearbyManager, CharaDataConfigService configService,
                         UiSharedService uiSharedService, ServerConfigurationManager serverConfigurationManager,
                         DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, PairManager pairManager,
                         CharaDataGposeTogetherManager charaDataGposeTogetherManager, McdfShareManager mcdfShareManager,
                         HousingShareManager housingShareManager, HousingFurnitureScanner housingScanner,
                         UmbraProfileManager umbraProfileManager)
        : base(logger, mediator, $"{Loc.Get("CharaDataHub.WindowTitle")}###UmbraCharaDataUI", performanceCollectorService)
    {
        SetWindowSizeConstraints();

        _charaDataManager = charaDataManager;
        _charaDataNearbyManager = charaDataNearbyManager;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _pairManager = pairManager;
        _charaDataGposeTogetherManager = charaDataGposeTogetherManager;
        _mcdfShareManager = mcdfShareManager;
        _housingShareManager_housing = housingShareManager;
        _housingScanner = housingScanner;
        _umbraProfileManager = umbraProfileManager;
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen |= _configService.Current.OpenMareHubOnGposeStart);
        Mediator.Subscribe<OpenCharaDataHubWithFilterMessage>(this, (msg) =>
        {
            IsOpen = true;
            _openDataApplicationShared = true;
            _sharedWithYouOwnerFilter = msg.UserData.AliasOrUID;
            UpdateFilteredItems();
        });
    }

    private bool _openDataApplicationShared = false;

    public string CharaName(string name)
    {
        if (_abbreviateCharaName)
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
        _filteredDict = null;
        _sharedWithYouOwnerFilter = string.Empty;
        _importCode = string.Empty;
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

        (_hasValidGposeTarget, _gposeTarget) = _charaDataManager.CanApplyInGpose().GetAwaiter().GetResult();

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

        if (_openDataApplicationShared) _hubActiveTab = 2;
        if (_openMcdOnlineOnNextRun) { _hubActiveTab = 3; _openMcdOnlineOnNextRun = false; }

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        var labels = new[]
        {
            Loc.Get("CharaDataHub.Tab.GposeTogether"),
            "Quest Sync",
            Loc.Get("CharaDataHub.Tab.Application"),
            Loc.Get("CharaDataHub.Tab.DataCreation"),
            Loc.Get("CharaDataHub.Tab.Profiles"),
            Loc.Get("CharaDataHub.Tab.Settings") is { Length: > 0 } s ? s : "Settings",
        };
        var icons = new[]
        {
            FontAwesomeIcon.Camera,
            FontAwesomeIcon.Scroll,
            FontAwesomeIcon.FileImport,
            FontAwesomeIcon.PaintBrush,
            FontAwesomeIcon.AddressBook,
            FontAwesomeIcon.Cog,
        };

        const float btnH = 26f;
        const float btnSpacing = 3f;
        const float rounding = 4f;
        const float iconTextGap = 4f;
        const float btnPadX = 8f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Measure natural widths (icon + gap + text + padding)
        var iconStrings = new string[labels.Length];
        var iconSizes = new System.Numerics.Vector2[labels.Length];
        var labelSizes = new System.Numerics.Vector2[labels.Length];
        var naturalWidths = new float[labels.Length];
        float totalNatural = btnSpacing * (labels.Length - 1);

        for (int i = 0; i < labels.Length; i++)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            iconStrings[i] = icons[i].ToIconString();
            iconSizes[i] = ImGui.CalcTextSize(iconStrings[i]);
            ImGui.PopFont();
            labelSizes[i] = ImGui.CalcTextSize(labels[i]);
            naturalWidths[i] = iconSizes[i].X + iconTextGap + labelSizes[i].X + btnPadX;
            totalNatural += naturalWidths[i];
        }

        // Determine display mode: icon+text if fits, icon-only otherwise
        bool iconOnly = totalNatural > availWidth;

        var borderColor = new System.Numerics.Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new System.Numerics.Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new System.Numerics.Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            float btnW = iconOnly ? (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length : naturalWidths[i];
            bool isDisabled = (i == 3 && _isHandlingSelf); // Data Creation disabled when handling self

            var p = ImGui.GetCursorScreenPos();
            using (ImRaii.Disabled(isDisabled))
                ImGui.InvisibleButton($"##hubTab_{i}", new System.Numerics.Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool isActive = _hubActiveTab == i;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new System.Numerics.Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new System.Numerics.Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            var textColor = isActive ? new System.Numerics.Vector4(1f, 1f, 1f, 1f)
                : hovered ? new System.Numerics.Vector4(0.9f, 0.85f, 1f, 1f)
                : new System.Numerics.Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            if (iconOnly)
            {
                // Icon centered
                var ix = p.X + (btnW - iconSizes[i].X) / 2f;
                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new System.Numerics.Vector2(ix, p.Y + (btnH - iconSizes[i].Y) / 2f), textColorU32, iconStrings[i]);
                ImGui.PopFont();

                if (hovered) UiSharedService.AttachToolTip(labels[i]);
            }
            else
            {
                // Icon + text centered
                var contentW = iconSizes[i].X + iconTextGap + labelSizes[i].X;
                var startX = p.X + (btnW - contentW) / 2f;

                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new System.Numerics.Vector2(startX, p.Y + (btnH - iconSizes[i].Y) / 2f), textColorU32, iconStrings[i]);
                ImGui.PopFont();

                dl.AddText(new System.Numerics.Vector2(startX + iconSizes[i].X + iconTextGap, p.Y + (btnH - labelSizes[i].Y) / 2f), textColorU32, labels[i]);
            }

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked && !isDisabled) _hubActiveTab = i;

            if (isDisabled)
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.CreationDisabledTooltip"));
        }

        ImGuiHelpers.ScaledDummy(4f);

        bool smallUi = false;
        switch (_hubActiveTab)
        {
            case 0:
                smallUi = true;
                DrawGposeTogether();
                break;
            case 1:
                smallUi = true;
                DrawQuestSync();
                break;
            case 2:
                smallUi = true;
                DrawDataApplicationTab(accent);
                break;
            case 3:
                DrawDataCreationTab(accent);
                break;
            case 4:
                DrawProfileBrowser(accent);
                break;
            case 5:
                using (var id = ImRaii.PushId("settings"))
                    DrawSettings();
                break;
        }

        SetWindowSizeConstraints(smallUi);
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

    private void DrawFavorite(string id)
    {
        bool isFavorite = _configService.Current.FavoriteCodes.TryGetValue(id, out var favorite);
        if (_configService.Current.FavoriteCodes.ContainsKey(id))
        {
            _uiSharedService.IconText(FontAwesomeIcon.Star, ImGuiColors.ParsedGold);
            UiSharedService.AttachToolTip($"Custom Description: {favorite?.CustomDescription ?? string.Empty}" + UiSharedService.TooltipSeparator
                + "Click to remove from Favorites");
        }
        else
        {
            _uiSharedService.IconText(FontAwesomeIcon.Star, ImGuiColors.DalamudGrey);
            UiSharedService.AttachToolTip("Click to add to Favorites");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (isFavorite) _configService.Current.FavoriteCodes.Remove(id);
            else _configService.Current.FavoriteCodes[id] = new();
            _configService.Save();
        }
    }

    private void DrawGposeControls()
    {
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Apply.GposeActors.Title"));
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
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, actor.Address == (_dalamudUtilService.GetGposeTargetGameObjectAsync().GetAwaiter().GetResult()?.Address ?? nint.Zero)))
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

    private void DrawDataApplication()
    {
        _uiSharedService.BigText(Loc.Get("CharaDataHub.Apply.Title"));

        ImGuiHelpers.ScaledDummy(5);

        if (_uiSharedService.IsInGpose)
        {
            ImGui.TextUnformatted(Loc.Get("CharaDataHub.Apply.TargetLabel"));
            ImGui.SameLine(200);
            UiSharedService.ColorText(CharaName(_gposeTarget), UiSharedService.GetBoolColor(_hasValidGposeTarget));
        }

        if (!_hasValidGposeTarget)
        {
            ImGuiHelpers.ScaledDummy(3);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("CharaDataHub.Apply.TargetWarning"), UiSharedService.AccentColor, 350);
        }

        ImGuiHelpers.ScaledDummy(10);

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;
        using (var applyTabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent))
        using (var applyTabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent))
        {
            using var tabs = ImRaii.TabBar("Tabs");

            using (var byFavoriteTabItem = ImRaii.TabItem(Loc.Get("CharaDataHub.Apply.Tabs.Favorites")))
            {
                if (byFavoriteTabItem)
                {
                    using var id = ImRaii.PushId("byFavorite");

                    ImGuiHelpers.ScaledDummy(5);

                    var max = ImGui.GetWindowContentRegionMax();
                    UiSharedService.DrawTree(Loc.Get("CharaDataHub.Apply.Filters.Title"), () =>
                    {
                        var maxIndent = ImGui.GetWindowContentRegionMax();
                        ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                        ImGui.InputTextWithHint("##ownFilter", Loc.Get("CharaDataHub.Apply.Filters.CodeOwner"), ref _filterCodeNote, 100);
                        ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                        ImGui.InputTextWithHint("##descFilter", Loc.Get("CharaDataHub.Apply.Filters.Description"), ref _filterDescription, 100);
                        ImGui.Checkbox(Loc.Get("CharaDataHub.Apply.Filters.OnlyPose"), ref _filterPoseOnly);
                        ImGui.Checkbox(Loc.Get("CharaDataHub.Apply.Filters.OnlyWorld"), ref _filterWorldOnly);
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Ban, Loc.Get("CharaDataHub.Apply.Filters.Reset")))
                        {
                            _filterCodeNote = string.Empty;
                            _filterDescription = string.Empty;
                            _filterPoseOnly = false;
                            _filterWorldOnly = false;
                        }
                    });

                    ImGuiHelpers.ScaledDummy(5);
                    ImGui.Separator();
                    using var scrollableChild = ImRaii.Child("favorite");
                    ImGuiHelpers.ScaledDummy(5);
                    using var totalIndent = ImRaii.PushIndent(5f);
                    var cursorPos = ImGui.GetCursorPos();
                    max = ImGui.GetWindowContentRegionMax();
                    foreach (var favorite in _filteredFavorites.OrderByDescending(k => k.Value.Favorite.LastDownloaded))
                    {
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
                                ImGui.TextUnformatted(favorite.Key);

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
                                    UpdateFilteredItems();
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
                            }, "Apply Character Data to GPose Target", metaInfo, _hasValidGposeTarget, false);
                            ImGui.SameLine();
                            GposeMetaInfoAction((meta) =>
                            {
                                if (_uiSharedService.IconButton(FontAwesomeIcon.Plus) && meta != null)
                                {
                                    _ = _charaDataManager.SpawnAndApplyData(meta);
                                }
                            }, "Spawn Actor with Brio and apply Character Data", metaInfo, _hasValidGposeTarget, true);

                            string uidText = string.Empty;
                            var uid = favorite.Key.Split(":")[0];
                            if (metaInfo != null)
                            {
                                uidText = metaInfo.Uploader.AliasOrUID;
                            }
                            else
                            {
                                uidText = uid;
                            }

                            var note = _serverConfigurationManager.GetNoteForUid(uid);
                            if (note != null)
                            {
                                uidText = $"{note} ({uidText})";
                            }
                            ImGui.TextUnformatted(uidText);

                            ImGui.TextUnformatted("Last Use: ");
                            ImGui.SameLine();
                            ImGui.TextUnformatted(favorite.Value.Favorite.LastDownloaded == DateTime.MaxValue
                                ? "Never"
                                : favorite.Value.Favorite.LastDownloaded.ToString(CultureInfo.CurrentCulture));

                            var desc = favorite.Value.Favorite.CustomDescription;
                            ImGui.SetNextItemWidth(maxPos - xPos);
                            if (ImGui.InputTextWithHint("##desc", "Custom Description for Favorite", ref desc, 100))
                            {
                                favorite.Value.Favorite.CustomDescription = desc;
                                _configService.Save();
                            }

                            DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
                        });

                        ImGuiHelpers.ScaledDummy(5);
                    }

                    if (_configService.Current.FavoriteCodes.Count == 0)
                    {
                        UiSharedService.ColorTextWrapped("You have no favorites added. Add Favorites through the other tabs before you can use this tab.", UiSharedService.AccentColor);
                    }
                }
            }

            using (var byCodeTabItem = ImRaii.TabItem("Code"))
            {
                using var id = ImRaii.PushId("byCodeTab");
                if (byCodeTabItem)
                {
                    using var child = ImRaii.Child("sharedWithYouByCode", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                    DrawHelpFoldout("You can apply character data you have a code for in this tab. Provide the code in it's given format \"OwnerUID:DataId\" into the field below and click on " +
                                    "\"Get Info from Code\". This will provide you basic information about the data behind the code. Afterwards select an actor in GPose and press on \"Download and apply to <actor>\"." + Environment.NewLine + Environment.NewLine
                                    + "Description: as set by the owner of the code to give you more or additional information of what this code may contain." + Environment.NewLine
                                    + "Last Update: the date and time the owner of the code has last updated the data." + Environment.NewLine
                                    + "Is Downloadable: whether or not the code is downloadable and applicable. If the code is not downloadable, contact the owner so they can attempt to fix it." + Environment.NewLine + Environment.NewLine
                                    + "To download a code the code requires correct access permissions to be set by the owner. If getting info from the code fails, contact the owner to make sure they set their Access Permissions for the code correctly.");

                    ImGuiHelpers.ScaledDummy(5);
                    ImGui.InputTextWithHint("##importCode", "Enter Data Code", ref _importCode, 100);
                    using (ImRaii.Disabled(string.IsNullOrEmpty(_importCode)))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleDown, "Get Info from Code"))
                        {
                            _charaDataManager.DownloadMetaInfo(_importCode);
                        }
                    }
                    GposeMetaInfoAction((meta) =>
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, $"Download and Apply") && meta != null)
                        {
                            _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta);
                        }
                    }, "Apply this Character Data to the current GPose actor", _charaDataManager.LastDownloadedMetaInfo, _hasValidGposeTarget, false);
                    ImGui.SameLine();
                    GposeMetaInfoAction((meta) =>
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, $"Download and Spawn") && meta != null)
                        {
                            _ = _charaDataManager.SpawnAndApplyData(meta);
                        }
                    }, "Spawn a new Brio actor and apply this Character Data", _charaDataManager.LastDownloadedMetaInfo, _hasValidGposeTarget, true);
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    DrawAddOrRemoveFavorite(_charaDataManager.LastDownloadedMetaInfo);

                    ImGui.NewLine();
                    if (!_charaDataManager.DownloadMetaInfoTask?.IsCompleted ?? false)
                    {
                        UiSharedService.ColorTextWrapped("Downloading meta info. Please wait.", UiSharedService.AccentColor);
                    }
                    if ((_charaDataManager.DownloadMetaInfoTask?.IsCompleted ?? false) && !_charaDataManager.DownloadMetaInfoTask.Result.Success)
                    {
                        UiSharedService.ColorTextWrapped(_charaDataManager.DownloadMetaInfoTask.Result.Result, UiSharedService.AccentColor);
                    }

                    using (ImRaii.Disabled(_charaDataManager.LastDownloadedMetaInfo == null))
                    {
                        ImGuiHelpers.ScaledDummy(5);
                        var metaInfo = _charaDataManager.LastDownloadedMetaInfo;
                        ImGui.TextUnformatted("Description");
                        ImGui.SameLine(150);
                        UiSharedService.TextWrapped(string.IsNullOrEmpty(metaInfo?.Description) ? "-" : metaInfo.Description);
                        ImGui.TextUnformatted("Last Update");
                        ImGui.SameLine(150);
                        ImGui.TextUnformatted(metaInfo?.UpdatedDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-");
                        ImGui.TextUnformatted("Is Downloadable");
                        ImGui.SameLine(150);
                        _uiSharedService.BooleanToColoredIcon(metaInfo?.CanBeDownloaded ?? false, inline: false);
                        ImGui.TextUnformatted("Poses");
                        ImGui.SameLine(150);
                        if (metaInfo?.HasPoses ?? false)
                            DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
                        else
                            _uiSharedService.BooleanToColoredIcon(false, false);
                    }
                }
            }

            using (var yourOwnTabItem = ImRaii.TabItem("Your Own"))
            {
                using var id = ImRaii.PushId("yourOwnTab");
                if (yourOwnTabItem)
                {
                    DrawHelpFoldout("You can apply character data you created yourself in this tab. If the list is not populated press on \"Download your Character Data\"." + Environment.NewLine + Environment.NewLine
                                     + "To create new and edit your existing character data use the \"Online Data\" tab.");

                    ImGuiHelpers.ScaledDummy(5);

                    using (ImRaii.Disabled(_charaDataManager.GetAllDataTask != null
                        || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowCircleDown, "Download your Character Data"))
                        {
                            var cts = EnsureFreshCts(ref _disposalCts);
                            _ = _charaDataManager.GetAllData(cts.Token);
                        }
                    }
                    if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
                    {
                        UiSharedService.AttachToolTip("You can only refresh all character data from server every minute. Please wait.");
                    }

                    ImGuiHelpers.ScaledDummy(5);
                    ImGui.Separator();

                    using var child = ImRaii.Child("ownDataChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                    using var indent = ImRaii.PushIndent(10f);
                    foreach (var data in _charaDataManager.OwnCharaData.Values)
                    {
                        var hasMetaInfo = _charaDataManager.TryGetMetaInfo(data.FullId, out var metaInfo);
                        if (!hasMetaInfo || metaInfo == null) continue;
                        DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, metaInfo, true);
                    }

                    ImGuiHelpers.ScaledDummy(5);
                }
            }

            using (var sharedWithYouTabItem = ImRaii.TabItem("Shared With You", _openDataApplicationShared ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                using var id = ImRaii.PushId("sharedWithYouTab");
                if (sharedWithYouTabItem)
                {
                    DrawHelpFoldout("You can apply character data shared with you implicitly in this tab. Shared Character Data are Character Data entries that have \"Sharing\" set to \"Shared\" and you have access through those by meeting the access restrictions, " +
                                    "i.e. you were specified by your UID to gain access or are paired with the other user according to the Access Restrictions setting." + Environment.NewLine + Environment.NewLine
                                    + "Filter if needed to find a specific entry, then just press on \"Apply to <actor>\" and it will download and apply the Character Data to the currently targeted GPose actor." + Environment.NewLine + Environment.NewLine
                                    + "Note: Shared Data of Pairs you have paused will not be shown here.");

                    ImGuiHelpers.ScaledDummy(5);

                    DrawUpdateSharedDataButton();

                    int activeFilters = 0;
                    if (!string.IsNullOrEmpty(_sharedWithYouOwnerFilter)) activeFilters++;
                    if (!string.IsNullOrEmpty(_sharedWithYouDescriptionFilter)) activeFilters++;
                    if (_sharedWithYouDownloadableFilter) activeFilters++;
                    string filtersText = activeFilters == 0 ? "Filters" : $"Filters ({activeFilters} active)";
                    UiSharedService.DrawTree($"{filtersText}##filters", () =>
                    {
                        var filterWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                        ImGui.SetNextItemWidth(filterWidth);
                        if (ImGui.InputTextWithHint("##filter", "Filter by UID/Note", ref _sharedWithYouOwnerFilter, 30))
                        {
                            UpdateFilteredItems();
                        }
                        ImGui.SetNextItemWidth(filterWidth);
                        if (ImGui.InputTextWithHint("##filterDesc", "Filter by Description", ref _sharedWithYouDescriptionFilter, 50))
                        {
                            UpdateFilteredItems();
                        }
                        if (ImGui.Checkbox("Only show downloadable", ref _sharedWithYouDownloadableFilter))
                        {
                            UpdateFilteredItems();
                        }
                    });

                    if (_filteredDict == null && _charaDataManager.GetSharedWithYouTask == null)
                    {
                        _filteredDict = _charaDataManager.SharedWithYouData
                            .ToDictionary(k =>
                            {
                                var note = _serverConfigurationManager.GetNoteForUid(k.Key.UID);
                                return string.IsNullOrEmpty(note) ? k.Key.AliasOrUID : $"{note} ({k.Key.AliasOrUID})";
                            }, k => k.Value, StringComparer.OrdinalIgnoreCase)
                            .Where(k => string.IsNullOrEmpty(_sharedWithYouOwnerFilter) || k.Key.Contains(_sharedWithYouOwnerFilter, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
                    }

                    ImGuiHelpers.ScaledDummy(5);
                    ImGui.Separator();
                    using var child = ImRaii.Child("sharedWithYouChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);

                    ImGuiHelpers.ScaledDummy(5);
                    foreach (var entry in _filteredDict ?? [])
                    {
                        bool isFilteredAndHasToBeOpened = entry.Key.Contains(_sharedWithYouOwnerFilter) && _openDataApplicationShared;
                        if (isFilteredAndHasToBeOpened)
                            ImGui.SetNextItemOpen(isFilteredAndHasToBeOpened);
                        UiSharedService.DrawTree($"{entry.Key} - [{entry.Value.Count} Character Data Sets]##{entry.Key}", () =>
                        {
                            foreach (var data in entry.Value)
                            {
                                DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, data);
                            }
                            ImGuiHelpers.ScaledDummy(5);
                        });
                        if (isFilteredAndHasToBeOpened)
                            _openDataApplicationShared = false;
                    }
                }
            }

            using (var mcdfTabItem = ImRaii.TabItem("From MCDF"))
            {
                using var id = ImRaii.PushId("applyMcdfTab");
                if (mcdfTabItem)
                {
                    using var child = ImRaii.Child("applyMcdf", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                    DrawHelpFoldout("You can apply character data shared with you using a MCDF file in this tab." + Environment.NewLine + Environment.NewLine
                                    + "Load the MCDF first via the \"Load MCDF\" button which will give you the basic description that the owner has set during export." + Environment.NewLine
                                    + "You can then apply it to any handled GPose actor." + Environment.NewLine + Environment.NewLine
                                    + "MCDF to share with others can be generated using the \"MCDF Export\" tab at the top.");

                    ImGuiHelpers.ScaledDummy(5);

                    if (_charaDataManager.LoadedMcdfHeader == null || _charaDataManager.LoadedMcdfHeader.IsCompleted)
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Load MCDF"))
                        {
                            _fileDialogManager.OpenFileDialog("Pick MCDF file", ".mcdf", (success, paths) =>
                            {
                                if (!success) return;
                                if (paths.FirstOrDefault() is not string path) return;

                                _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                                _configService.Save();

                                _charaDataManager.LoadMcdf(path);
                            }, 1, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
                        }
                        UiSharedService.AttachToolTip("Load MCDF Metadata into memory");
                        if ((_charaDataManager.LoadedMcdfHeader?.IsCompleted ?? false))
                        {
                            ImGui.TextUnformatted("Loaded file");
                            ImGui.SameLine(200);
                            UiSharedService.TextWrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.FilePath);
                            ImGui.Text("Description");
                            ImGui.SameLine(200);
                            UiSharedService.TextWrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.CharaFileData.Description);

                            ImGuiHelpers.ScaledDummy(5);

                            using (ImRaii.Disabled(!_hasValidGposeTarget))
                            {
                                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, "Apply"))
                                {
                                    _ = _charaDataManager.McdfApplyToGposeTarget();
                                }
                                UiSharedService.AttachToolTip($"Apply to {_gposeTarget}");
                                ImGui.SameLine();
                                using (ImRaii.Disabled(!_charaDataManager.BrioAvailable))
                                {
                                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Spawn Actor and Apply"))
                                    {
                                        _charaDataManager.McdfSpawnApplyToGposeTarget();
                                    }
                                }
                            }
                        }
                        if ((_charaDataManager.LoadedMcdfHeader?.IsFaulted ?? false) || (_charaDataManager.McdfApplicationTask?.IsFaulted ?? false))
                        {
                            UiSharedService.ColorTextWrapped("Failure to read MCDF file. MCDF file is possibly corrupt. Re-export the MCDF file and try again.",
                                UiSharedService.AccentColor);
                            UiSharedService.ColorTextWrapped("Note: if this is your MCDF, try redrawing yourself, wait and re-export the file. " +
                                "If you received it from someone else have them do the same.", UiSharedService.AccentColor);
                        }
                    }
                    else
                    {
                        UiSharedService.ColorTextWrapped("Loading Character...", UiSharedService.AccentColor);
                    }
                }
            }
        }
    }

    private void DrawMcdf()
    {
        DrawMcdfExport();
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);
        DrawMcdfShare();
    }

    private void DrawMcdfExport()
    {
        _uiSharedService.BigText("Export de fichier MCDF");

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

    private void DrawMcdfShare()
    {
        if (!_mcdfShareInitialized && !_mcdfShareManager.IsBusy)
        {
            _mcdfShareInitialized = true;
            _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
        }

        if (_mcdfShareManager.IsBusy)
        {
            UiSharedService.ColorTextWrapped("Traitement en cours...", ImGuiColors.DalamudYellow);
        }

        if (!string.IsNullOrEmpty(_mcdfShareManager.LastError))
        {
            UiSharedService.ColorTextWrapped(_mcdfShareManager.LastError!, ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(_mcdfShareManager.LastSuccess))
        {
            UiSharedService.ColorTextWrapped(_mcdfShareManager.LastSuccess!, ImGuiColors.HealerGreen);
        }

        if (ImGui.Button("Actualiser les partages"))
        {
            _ = _mcdfShareManager.RefreshAsync(CancellationToken.None);
        }

        ImGui.Separator();
        _uiSharedService.BigText("Créer un partage MCDF");

        ImGui.InputTextWithHint("##mcdfShareDescription", "Description", ref _mcdfShareDescription, 128);
        ImGui.InputInt("Expiration (jours, 0 = jamais)", ref _mcdfShareExpireDays);

        DrawMcdfShareIndividualDropdown();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputTextWithHint("##mcdfShareUidInput", "UID ou vanity", ref _mcdfShareIndividualInput, 32))
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
        ImGui.TextUnformatted("UID synchronisé à ajouter");
        _uiSharedService.DrawHelpText("Choisissez un pair synchronisé dans la liste ou saisissez un UID. Les utilisateurs listés pourront récupérer ce partage MCDF.");

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
        if (ImGui.InputTextWithHint("##mcdfShareSyncshellInput", "GID ou alias", ref _mcdfShareSyncshellInput, 32))
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
        ImGui.TextUnformatted("Syncshell à ajouter");
        _uiSharedService.DrawHelpText("Sélectionnez une syncshell synchronisée ou saisissez un identifiant. Les syncshells listées auront accès au partage.");

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

        using (ImRaii.Disabled(_mcdfShareManager.IsBusy))
        {
            if (ImGui.Button("Créer"))
            {
                DateTime? expiresAt = _mcdfShareExpireDays <= 0 ? null : DateTime.UtcNow.AddDays(_mcdfShareExpireDays);
                _ = _mcdfShareManager.CreateShareAsync(_mcdfShareDescription, _mcdfShareAllowedIndividuals.ToList(), _mcdfShareAllowedSyncshells.ToList(), expiresAt, CancellationToken.None);
                _mcdfShareDescription = string.Empty;
                _mcdfShareAllowedIndividuals.Clear();
                _mcdfShareAllowedSyncshells.Clear();
                _mcdfShareIndividualInput = string.Empty;
                _mcdfShareIndividualDropdownSelection = string.Empty;
                _mcdfShareSyncshellInput = string.Empty;
                _mcdfShareSyncshellDropdownSelection = string.Empty;
                _mcdfShareExpireDays = 0;
            }
        }

        ImGui.Separator();
        _uiSharedService.BigText("Mes partages : ");

        if (_mcdfShareManager.OwnShares.Count == 0)
        {
            ImGui.TextDisabled("Aucun partage MCDF créé.");
        }
        else if (ImGui.BeginTable("mcdf-own-shares", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter))
        {
            ImGui.TableSetupColumn("Description");
            ImGui.TableSetupColumn("Créé le");
            ImGui.TableSetupColumn("Expire");
            ImGui.TableSetupColumn("Téléchargements");
            ImGui.TableSetupColumn("Accès");
            var style = ImGui.GetStyle();
            float BtnWidth(string label) => ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f;
            float ownActionsWidth = BtnWidth("Appliquer en GPose") + style.ItemSpacing.X + BtnWidth("Enregistrer") + style.ItemSpacing.X + BtnWidth("Supprimer") + 2f; // small margin
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, ownActionsWidth);
            ImGui.TableHeadersRow();

            foreach (var entry in _mcdfShareManager.OwnShares)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.ExpiresAtUtc.HasValue ? entry.ExpiresAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Jamais");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.DownloadCount.ToString(CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"UID : {entry.AllowedIndividuals.Count}, Syncshells : {entry.AllowedSyncshells.Count}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    if (entry.AllowedIndividuals.Count > 0)
                    {
                        ImGui.TextUnformatted("UID autorisés:");
                        foreach (var uid in entry.AllowedIndividuals)
                            ImGui.BulletText(FormatUidWithName(uid));
                    }
                    else
                    {
                        ImGui.TextDisabled("Aucun UID autorisé");
                    }
                    ImGui.Separator();
                    if (entry.AllowedSyncshells.Count > 0)
                    {
                        ImGui.TextUnformatted("Syncshells autorisées:");
                        foreach (var gid in entry.AllowedSyncshells)
                            ImGui.BulletText(FormatSyncshellLabel(gid));
                    }
                    else
                    {
                        ImGui.TextDisabled("Aucune syncshell autorisée");
                    }
                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();
                using (ImRaii.PushId("ownShare" + entry.Id))
                {
                    if (ImGui.SmallButton("Appliquer en GPose"))
                    {
                        _ = _mcdfShareManager.ApplyShareAsync(entry.Id, CancellationToken.None);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Enregistrer"))
                    {
                        var baseName = SanitizeFileName(entry.Description, entry.Id.ToString("D", CultureInfo.InvariantCulture));
                        var defaultName = baseName + ".mcdf";
                        _fileDialogManager.SaveFileDialog("Enregistrer le partage MCDF", ".mcdf", defaultName, ".mcdf", (success, path) =>
                        {
                            if (!success || string.IsNullOrEmpty(path)) return;
                            _ = _mcdfShareManager.ExportShareAsync(entry.Id, path, CancellationToken.None);
                        });
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Supprimer"))
                    {
                        _ = _mcdfShareManager.DeleteShareAsync(entry.Id);
                    }
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        _uiSharedService.BigText("Partagés avec moi : ");

        if (_mcdfShareManager.SharedShares.Count == 0)
        {
            ImGui.TextDisabled("Aucun partage MCDF reçu.");
        }
        else if (ImGui.BeginTable("mcdf-shared-shares", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter))
        {
            ImGui.TableSetupColumn("Description");
            ImGui.TableSetupColumn("Propriétaire");
            ImGui.TableSetupColumn("Expire");
            var style2 = ImGui.GetStyle();
            float BtnWidth2(string label) => ImGui.CalcTextSize(label).X + style2.FramePadding.X * 2f;
            float sharedActionsWidth = BtnWidth2("Appliquer sur la cible") + style2.ItemSpacing.X + BtnWidth2("Enregistrer") + 2f;
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, sharedActionsWidth);
            ImGui.TableHeadersRow();

            foreach (var entry in _mcdfShareManager.SharedShares)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Description) ? entry.Id.ToString("D", CultureInfo.InvariantCulture) : entry.Description);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.OwnerAlias) ? entry.OwnerUid : entry.OwnerAlias);
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"UID propriétaire: {entry.OwnerUid}");
                    if (!string.IsNullOrEmpty(entry.OwnerAlias))
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted($"Alias: {entry.OwnerAlias}");
                    }
                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.ExpiresAtUtc.HasValue ? entry.ExpiresAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Jamais");

                ImGui.TableNextColumn();
                using (ImRaii.PushId("sharedShare" + entry.Id))
                {
                    if (ImGui.SmallButton("Appliquer sur la cible"))
                    {
                        _ = _mcdfShareManager.ApplyShareAsync(entry.Id, CancellationToken.None);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Enregistrer"))
                    {
                        var baseName = SanitizeFileName(entry.Description, entry.Id.ToString("D", CultureInfo.InvariantCulture));
                        var defaultName = baseName + ".mcdf";
                        _fileDialogManager.SaveFileDialog("Enregistrer le partage MCDF", ".mcdf", defaultName, ".mcdf", (success, path) =>
                        {
                            if (!success || string.IsNullOrEmpty(path)) return;
                            _ = _mcdfShareManager.ExportShareAsync(entry.Id, path, CancellationToken.None);
                        });
                    }
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
        if (!string.IsNullOrEmpty(note)) return $"{uid} ({note})";
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
            UiSharedService.ColorText(data.FullId, UiSharedService.GetBoolColor(data.CanBeDownloaded));
            if (!data.CanBeDownloaded)
            {
                UiSharedService.AttachToolTip("This data is incomplete on the server and cannot be downloaded. Contact the owner so they can fix it. If you are the owner, review the data in the Online Data tab.");
            }

            var offsetFromRight = availableWidth - _uiSharedService.GetIconData(FontAwesomeIcon.Calendar).X - _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X
                - _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X - ImGui.GetStyle().ItemSpacing.X * 2;

            ImGui.SameLine();
            ImGui.SetCursorPosX(offsetFromRight);
            _uiSharedService.IconText(FontAwesomeIcon.Calendar);
            UiSharedService.AttachToolTip($"Last Update: {data.UpdatedDate}");

            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowRight) && meta != null)
                {
                    _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta);
                }
            }, $"Apply Character data to {CharaName(selectedGposeActor)}", data, hasValidGposeTarget, false);
            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Plus) && meta != null)
                {
                    _ = _charaDataManager.SpawnAndApplyData(meta);
                }
            }, "Spawn and Apply Character data", data, hasValidGposeTarget, true);

            using var indent = ImRaii.PushIndent(favPos - startPos);

            if (canOpen)
            {
                using (ImRaii.Disabled(_isHandlingSelf))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Edit, "Open in Online Data Editor"))
                    {
                        SelectedDtoId = data.Id;
                        _openMcdOnlineOnNextRun = true;
                    }
                }
                if (_isHandlingSelf)
                {
                    UiSharedService.AttachToolTip("Cannot use Online Data while having Character Data applied to self.");
                }
            }

            if (string.IsNullOrEmpty(data.Description))
            {
                UiSharedService.ColorTextWrapped("No description set", ImGuiColors.DalamudGrey, availableWidth);
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

            string tooltip = string.IsNullOrEmpty(item.Description) ? "No description set" : "Pose Description: " + item.Description;
            if (!isInGpose)
            {
                start = DrawIcon(start);
                UiSharedService.AttachToolTip(tooltip + UiSharedService.TooltipSeparator + (item.HasWorldData ? GetWorldDataTooltipText(item) + UiSharedService.TooltipSeparator + "Click to show on Map" : string.Empty));
                if (item.HasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(item.Position, item.Map);
                }
            }
            else
            {
                tooltip += UiSharedService.TooltipSeparator + $"Left Click: Apply this pose to {CharaName(actor)}";
                if (item.HasWorldData) tooltip += Environment.NewLine + $"CTRL+Right Click: Apply world position to {CharaName(actor)}."
                        + UiSharedService.TooltipSeparator + "!!! CAUTION: Applying world position will likely yeet this actor into nirvana. Use at your own risk !!!";
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

    private void DrawSettings()
    {
        ImGuiHelpers.ScaledDummy(5);
        _uiSharedService.BigText("Settings");
        ImGuiHelpers.ScaledDummy(5);
        bool openInGpose = _configService.Current.OpenMareHubOnGposeStart;
        if (ImGui.Checkbox("Open Character Data Hub when GPose loads", ref openInGpose))
        {
            _configService.Current.OpenMareHubOnGposeStart = openInGpose;
            _configService.Save();
        }
        _uiSharedService.DrawHelpText("This will automatically open the import menu when loading into Gpose. If unchecked you can open the menu manually with /sync gpose");
        bool downloadDataOnConnection = _configService.Current.DownloadMcdDataOnConnection;
        if (ImGui.Checkbox("Download Online Character Data on connecting", ref downloadDataOnConnection))
        {
            _configService.Current.DownloadMcdDataOnConnection = downloadDataOnConnection;
            _configService.Save();
        }
        _uiSharedService.DrawHelpText("This will automatically download Online Character Data data (Your Own and Shared with You) once a connection is established to the server.");

        bool showHelpTexts = _configService.Current.ShowHelpTexts;
        if (ImGui.Checkbox("Afficher les volets \"Qu'est-ce que c'est ? (Explication / Aide)\"", ref showHelpTexts))
        {
            _configService.Current.ShowHelpTexts = showHelpTexts;
            _configService.Save();
        }

        ImGui.Checkbox("Abbreviate Chara Names", ref _abbreviateCharaName);
        _uiSharedService.DrawHelpText("This setting will abbreviate displayed names. This setting is not persistent and will reset between restarts.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Last Export Folder");
        ImGui.SameLine(300);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.IsNullOrEmpty(_configService.Current.LastSavedCharaDataLocation) ? "Not set" : _configService.Current.LastSavedCharaDataLocation);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Ban, "Clear Last Export Folder"))
        {
            _configService.Current.LastSavedCharaDataLocation = string.Empty;
            _configService.Save();
        }
        _uiSharedService.DrawHelpText("Use this if the Load or Save MCDF file dialog does not open");
    }

    private void DrawProfileBrowser(Vector4 accent)
    {
        var cachedProfiles = _umbraProfileManager.GetCachedProfiles();

        var currentUid = _umbraProfileManager.CurrentUid;
        var searchLower = _profileBrowserSearch.ToLowerInvariant();
        var filtered = cachedProfiles.Where(p =>
        {
            if (currentUid != null && string.Equals(p.Key.User.UID, currentUid, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(p.Profile.RpFirstName) && string.IsNullOrWhiteSpace(p.Profile.RpLastName))
                return false;
            if (string.IsNullOrEmpty(searchLower)) return true;
            var uid = p.Key.User.AliasOrUID.ToLowerInvariant();
            var charName = (p.Key.CharName ?? string.Empty).ToLowerInvariant();
            var rpFirst = (p.Profile.RpFirstName ?? string.Empty).ToLowerInvariant();
            var rpLast = (p.Profile.RpLastName ?? string.Empty).ToLowerInvariant();
            var note = (_serverConfigurationManager.GetNoteForUid(p.Key.User.UID) ?? string.Empty).ToLowerInvariant();
            return uid.Contains(searchLower) || charName.Contains(searchLower) ||
                   rpFirst.Contains(searchLower) || rpLast.Contains(searchLower) || note.Contains(searchLower);
        }).ToList();

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            string.Format(CultureInfo.InvariantCulture, Loc.Get("Settings.ProfileBrowser.CachedProfiles"), filtered.Count));
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(Loc.Get("Settings.ProfileBrowser.ClearCache")).X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Settings.ProfileBrowser.ClearCache")))
        {
            _umbraProfileManager.ClearPersistedProfileCache();
            foreach (var tex in _profileBrowserTextures.Values)
                tex.Texture?.Dispose();
            _profileBrowserTextures.Clear();
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
        foreach (var entry in filtered)
        {
            DrawProfileCard(entry.Key, entry.Profile, accent);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
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
        if (!_profileBrowserTextures.TryGetValue(texKey, out var cached) || !imgData.SequenceEqual(cached.Data))
        {
            cached.Texture?.Dispose();
            var tex = _uiSharedService.LoadImage(imgData);
            _profileBrowserTextures[texKey] = (imgData, tex);
            cached = (imgData, tex);
        }

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

    private void DrawDataApplicationTab(System.Numerics.Vector4 accent)
    {
        using (var appTabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent))
        using (var appTabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent))
        {
            using var appTabs = ImRaii.TabBar("TabsApplicationLevel");

            using (ImRaii.Disabled(!_uiSharedService.IsInGpose))
            {
                using (var gposeTabItem = ImRaii.TabItem(Loc.Get("CharaDataHub.Tab.GposeActors")))
                {
                    if (gposeTabItem)
                    {
                        using var id = ImRaii.PushId("gposeControls");
                        DrawGposeControls();
                    }
                }
            }
            if (!_uiSharedService.IsInGpose)
                UiSharedService.AttachToolTip(Loc.Get("CharaDataHub.GposeOnlyTooltip"));

            using (var nearbyPosesTabItem = ImRaii.TabItem(Loc.Get("CharaDataHub.Tab.PosesNearby")))
            {
                if (nearbyPosesTabItem)
                {
                    using var id = ImRaii.PushId("nearbyPoseControls");
                    _charaDataNearbyManager.ComputeNearbyData = true;
                    DrawNearbyPoses();
                }
                else
                {
                    _charaDataNearbyManager.ComputeNearbyData = false;
                }
            }

            using (var applyDataTabItem = ImRaii.TabItem(Loc.Get("CharaDataHub.Tab.ApplyData"), _openDataApplicationShared ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                if (applyDataTabItem)
                {
                    using var id = ImRaii.PushId("applyData");
                    DrawDataApplication();
                }
            }
        }

        if (_hubActiveTab != 1)
            _charaDataNearbyManager.ComputeNearbyData = false;
    }

    private void DrawDataCreationTab(System.Numerics.Vector4 accent)
    {
        if (_openMcdOnlineOnNextRun)
        {
            _creationSubTab = 0;
            _openMcdOnlineOnNextRun = false;
        }

        var subLabels = new[]
        {
            Loc.Get("CharaDataHub.Tab.OnlineData"),
            Loc.Get("CharaDataHub.Tab.Mcdf"),
            Loc.Get("CharaDataHub.Tab.HousingShare"),
        };
        var subIcons = new[]
        {
            FontAwesomeIcon.Globe,
            FontAwesomeIcon.FileExport,
            FontAwesomeIcon.Home,
        };

        DrawSubTabButtons(subLabels, subIcons, ref _creationSubTab, accent);

        ImGuiHelpers.ScaledDummy(4f);

        using (ImRaii.Disabled(_isHandlingSelf))
        {
            switch (_creationSubTab)
            {
                case 0:
                    using (var id = ImRaii.PushId("mcdOnline"))
                        DrawMcdOnline();
                    break;
                case 1:
                    using (var id = ImRaii.PushId("mcdf"))
                        DrawMcdf();
                    break;
                case 2:
                    using (var id = ImRaii.PushId("housingShare"))
                        DrawHousingShare();
                    break;
            }
        }
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
        var bgColor = new System.Numerics.Vector4(0f, 0f, 0f, 0f);
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
            if (!isActive && hovered)
                dl.AddRect(p, p + new System.Numerics.Vector2(w, btnH), ImGui.GetColorU32(borderColor), rounding);

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