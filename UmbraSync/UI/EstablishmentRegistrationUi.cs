using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

internal class EstablishmentRegistrationUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly EstablishmentConfigService _establishmentConfigService;
    private readonly FileDialogManager _fileDialogManager;

    private string _name = string.Empty;
    private string _description = string.Empty;
    private int _category;
    private string _schedule = string.Empty;
    private string _factionTag = string.Empty;
    private bool _isPublic = true;
    private int _housingType; // 0=Maison, 1=Appartement
    private bool _isSubmitting;

    // Housing fields
    private ushort _selectedWorldId;
    private string _worldSearchFilter = string.Empty;
    private int _selectedDistrictIndex;
    private int _ward = 1;
    private int _plot = 1;
    private bool _isSubdivision;
    private int _roomNumber = 1;

    // SyncSlot link
    private string? _linkedSyncshellGid;
    private string _linkedSyncshellDisplay = string.Empty;

    // Images
    private byte[] _logoImageBytes = [];
    private byte[] _bannerImageBytes = [];
    private IDalamudTextureWrap? _logoTexture;
    private IDalamudTextureWrap? _bannerTexture;
    private string? _imageMessage;

    // Eligible groups cache (owned + has SyncSlot)
    private List<(string Gid, string Display, API.Dto.Slot.SlotLocationDto? SlotLocation)> _eligibleGroups = [];
    private bool _eligibleGroupsLoading;
    private bool _eligibleGroupsLoaded;

    private static string[] CategoryNames =>
    [
        Loc.Get("Establishment.Category.Tavern"), Loc.Get("Establishment.Category.Shop"),
        Loc.Get("Establishment.Category.Temple"), Loc.Get("Establishment.Category.Academy"),
        Loc.Get("Establishment.Category.Guild"), Loc.Get("Establishment.Category.Residence"),
        Loc.Get("Establishment.Category.Workshop"), Loc.Get("Establishment.Category.Other")
    ];

    private static readonly (string NameFr, string NameEn, uint TerritoryId)[] ResidentialDistricts =
    [
        ("Brum\u00e9e", "Mist", 339),
        ("Lavandi\u00e8re", "The Lavender Beds", 340),
        ("La Coupe", "The Goblet", 341),
        ("Shirogane", "Shirogane", 641),
        ("Empyr\u00e9e", "Empyreum", 979),
    ];

    private static readonly string[] DistrictNames = ResidentialDistricts.Select(d => d.NameFr).ToArray();

    public EstablishmentRegistrationUi(ILogger<EstablishmentRegistrationUi> logger, MareMediator mediator,
        ApiController apiController, DalamudUtilService dalamudUtilService, UiSharedService uiSharedService,
        PairManager pairManager, EstablishmentConfigService establishmentConfigService,
        FileDialogManager fileDialogManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Enregistrer un \u00e9tablissement###EstablishmentRegistration", performanceCollectorService)
    {
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _establishmentConfigService = establishmentConfigService;
        _fileDialogManager = fileDialogManager;

        SizeConstraints = new()
        {
            MinimumSize = new(480, 520),
            MaximumSize = new(600, 800)
        };
    }

    protected override void DrawInternal()
    {
        if (!_apiController.IsConnected)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Establishment.Registration.NotConnected"), ImGuiColors.DalamudRed);
            return;
        }

        DrawHeader();
        ImGuiHelpers.ScaledDummy(6f);

        UiSharedService.DrawCard("reg-general", () =>
        {
            DrawCardSectionHeader(FontAwesomeIcon.InfoCircle, Loc.Get("Establishment.Section.GeneralInfo"));
            DrawGeneralFields();
        }, stretchWidth: true);
        ImGuiHelpers.ScaledDummy(6f);

        UiSharedService.DrawCard("reg-images", () =>
        {
            DrawCardSectionHeader(FontAwesomeIcon.Image, Loc.Get("Establishment.Section.Images"));
            DrawImageFields();
        }, stretchWidth: true);
        ImGuiHelpers.ScaledDummy(6f);

        UiSharedService.DrawCard("reg-location", () =>
        {
            DrawCardSectionHeader(FontAwesomeIcon.MapMarkerAlt, Loc.Get("Establishment.Section.Location"));
            DrawLocationFields();
        }, stretchWidth: true);
        ImGuiHelpers.ScaledDummy(6f);

        UiSharedService.DrawCard("reg-syncshell", () =>
        {
            DrawCardSectionHeader(FontAwesomeIcon.Link, Loc.Get("Establishment.Section.SyncshellLink"));
            DrawSyncSlotFields();
        }, stretchWidth: true);
        ImGuiHelpers.ScaledDummy(8f);

        DrawSubmitArea();
    }

    private void DrawHeader()
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.PlusCircle.ToIconString());
        ImGui.SameLine();
        _uiSharedService.BigText(Loc.Get("Establishment.Registration.Header"));
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Registration.Subtitle"));
    }

    private static void DrawCardSectionHeader(FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, icon.ToIconString());
        ImGui.SameLine();
        UiSharedService.ColorText(title, UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawGeneralFields()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Field.Name"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##name", Loc.Get("Establishment.Field.NameHint"), ref _name, 100);
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Field.Description"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##desc", ref _description, 2000,
            new Vector2(-1, 60 * ImGuiHelpers.GlobalScale));
        ImGuiHelpers.ScaledDummy(2f);

        var availW = ImGui.GetContentRegionAvail().X;
        var halfW = (availW - ImGui.GetStyle().ItemSpacing.X) / 2f;

        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Field.Category"));
        ImGui.SetNextItemWidth(halfW);
        ImGui.Combo("##category", ref _category, CategoryNames, CategoryNames.Length);
        ImGui.EndGroup();

        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Field.Schedule"));
        ImGui.SetNextItemWidth(halfW);
        ImGui.InputTextWithHint("##schedule", Loc.Get("Establishment.Field.ScheduleHint"), ref _schedule, 200);
        ImGui.EndGroup();

        ImGuiHelpers.ScaledDummy(2f);

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Field.Faction"));
        ImGui.SetNextItemWidth(availW * 0.6f);
        ImGui.InputTextWithHint("##faction", Loc.Get("Establishment.Field.Optional"), ref _factionTag, 50);
        ImGuiHelpers.ScaledDummy(2f);

        ImGui.Checkbox(Loc.Get("Establishment.Field.PublicDirectory"), ref _isPublic);
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Field.PublicDirectoryTooltip"));
    }

    private void DrawImageFields()
    {
        if (!string.IsNullOrEmpty(_imageMessage))
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, _imageMessage);
            ImGuiHelpers.ScaledDummy(2f);
        }

        // Logo
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Image.Logo"));
        ImGui.TextDisabled(Loc.Get("Establishment.Image.LogoHint"));
        float imgSize = 80f * ImGuiHelpers.GlobalScale;
        float imgRounding = 8f * ImGuiHelpers.GlobalScale;

        if (_logoTexture != null && _logoImageBytes.Length > 0)
        {
            var drawList = ImGui.GetWindowDrawList();
            var imgMin = ImGui.GetCursorScreenPos();
            var imgMax = new Vector2(imgMin.X + imgSize, imgMin.Y + imgSize);
            drawList.AddImageRounded(
                _logoTexture.Handle, imgMin, imgMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), imgRounding);
            ImGui.Dummy(new Vector2(imgSize, imgSize));
        }
        else
        {
            DrawImagePlaceholder(imgSize, imgSize, imgRounding, FontAwesomeIcon.UserCircle);
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("Establishment.Image.Upload")))
        {
            _fileDialogManager.OpenFileDialog(
                Loc.Get("Establishment.Image.ChooseLogo"),
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                            if (bytes.Length > 2 * 1024 * 1024)
                            {
                                _imageMessage = Loc.Get("Establishment.Image.TooLarge");
                                return;
                            }
                            _imageMessage = null;
                            _logoImageBytes = bytes;
                            _logoTexture?.Dispose();
                            _logoTexture = _uiSharedService.LoadImage(bytes);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error loading logo image");
                            _imageMessage = Loc.Get("Establishment.Image.LoadError");
                        }
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_logoImageBytes.Length == 0))
        {
            using (ImRaii.PushId("clearLogo"))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Establishment.Image.Remove")))
                {
                    _logoImageBytes = [];
                    _logoTexture?.Dispose();
                    _logoTexture = null;
                }
            }
        }

        ImGuiHelpers.ScaledDummy(4f);

        // Banner
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Image.Banner"));
        ImGui.TextDisabled(Loc.Get("Establishment.Image.BannerHint"));
        float availWidth = ImGui.GetContentRegionAvail().X;
        float bannerRounding = 8f * ImGuiHelpers.GlobalScale;

        if (_bannerTexture != null && _bannerImageBytes.Length > 0)
        {
            float bannerHeight = availWidth * (260f / 840f);
            var bannerDrawList = ImGui.GetWindowDrawList();
            var bannerMin = ImGui.GetCursorScreenPos();
            var bannerMax = new Vector2(bannerMin.X + availWidth, bannerMin.Y + bannerHeight);
            bannerDrawList.AddImageRounded(
                _bannerTexture.Handle, bannerMin, bannerMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), bannerRounding);
            ImGui.Dummy(new Vector2(availWidth, bannerHeight));
        }
        else
        {
            DrawImagePlaceholder(availWidth, 60f * ImGuiHelpers.GlobalScale, bannerRounding, FontAwesomeIcon.Image);
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("Establishment.Image.Upload")))
        {
            _fileDialogManager.OpenFileDialog(
                Loc.Get("Establishment.Image.ChooseBanner"),
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                            if (bytes.Length > 2 * 1024 * 1024)
                            {
                                _imageMessage = Loc.Get("Establishment.Image.TooLarge");
                                return;
                            }
                            _imageMessage = null;
                            _bannerImageBytes = bytes;
                            _bannerTexture?.Dispose();
                            _bannerTexture = _uiSharedService.LoadImage(bytes);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error loading banner image");
                            _imageMessage = Loc.Get("Establishment.Image.LoadError");
                        }
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_bannerImageBytes.Length == 0))
        {
            using (ImRaii.PushId("clearBanner"))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Establishment.Image.Remove")))
                {
                    _bannerImageBytes = [];
                    _bannerTexture?.Dispose();
                    _bannerTexture = null;
                }
            }
        }
    }

    private static void DrawImagePlaceholder(float width, float height, float rounding, FontAwesomeIcon icon)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + width, min.Y + height);

        drawList.AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 0.5f)), rounding);
        drawList.AddRect(min, max,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f)), rounding);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconStr = icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconStr);
            var center = new Vector2((min.X + max.X) / 2f, (min.Y + max.Y) / 2f);
            ImGui.SetCursorScreenPos(new Vector2(center.X - iconSize.X / 2f, center.Y - iconSize.Y / 2f));
            ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 0.6f), iconStr);
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawLocationFields()
    {
        // Housing type toggle
        var availW = ImGui.GetContentRegionAvail().X;
        var btnW = (availW - ImGui.GetStyle().ItemSpacing.X) / 2f;
        var btnH = 28f * ImGuiHelpers.GlobalScale;

        bool isMaison = _housingType == 0;
        using (ImRaii.PushColor(ImGuiCol.Button, isMaison ? UiSharedService.AccentColor : UiSharedService.ThemeButtonBg))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, isMaison ? UiSharedService.AccentColor : UiSharedService.ThemeButtonHovered))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, UiSharedService.AccentColor))
        {
            if (ImGui.Button(Loc.Get("Establishment.Location.House"), new Vector2(btnW, btnH)))
                _housingType = 0;
        }

        ImGui.SameLine();

        bool isAppt = _housingType == 1;
        using (ImRaii.PushColor(ImGuiCol.Button, isAppt ? UiSharedService.AccentColor : UiSharedService.ThemeButtonBg))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, isAppt ? UiSharedService.AccentColor : UiSharedService.ThemeButtonHovered))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, UiSharedService.AccentColor))
        {
            if (ImGui.Button(Loc.Get("Establishment.Location.Apartment"), new Vector2(btnW, btnH)))
                _housingType = 1;
        }

        ImGuiHelpers.ScaledDummy(4f);

        // Auto-fill button
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crosshairs,
            Loc.Get("Establishment.Location.AutoFill"), ImGui.GetContentRegionAvail().X))
        {
            AutoFillHousing();
        }
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Location.AutoFillTooltip"));

        ImGuiHelpers.ScaledDummy(4f);

        // Shared housing fields
        DrawSharedHousingFields();

        // Type-specific fields
        if (_housingType == 0)
            DrawMaisonFields();
        else
            DrawAppartementFields();
    }

    private void DrawSharedHousingFields()
    {
        var availW = ImGui.GetContentRegionAvail().X;
        var halfW = (availW - ImGui.GetStyle().ItemSpacing.X) / 2f;

        // Serveur + Quartier
        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Server"));
        var previewName = _selectedWorldId != 0 && _uiSharedService.WorldData.TryGetValue(_selectedWorldId, out var wn)
            ? wn : Loc.Get("Establishment.Location.ServerChoose");
        ImGui.SetNextItemWidth(halfW);
        using (var combo = ImRaii.Combo("##server", previewName))
        {
            if (combo)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.InputTextWithHint("##worldSearch", Loc.Get("Establishment.Location.SearchHint"), ref _worldSearchFilter, 50);

                var filtered = _uiSharedService.WorldData
                    .Where(w => string.IsNullOrEmpty(_worldSearchFilter)
                        || w.Value.Contains(_worldSearchFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(w => w.Value, StringComparer.OrdinalIgnoreCase);

                foreach (var (id, name) in filtered)
                {
                    if (ImGui.Selectable(name, _selectedWorldId == id))
                    {
                        _selectedWorldId = id;
                        _worldSearchFilter = string.Empty;
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            else
            {
                _worldSearchFilter = string.Empty;
            }
        }
        ImGui.EndGroup();

        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.District"));
        ImGui.SetNextItemWidth(halfW);
        ImGui.Combo("##district", ref _selectedDistrictIndex, DistrictNames, DistrictNames.Length);
        ImGui.EndGroup();

        ImGuiHelpers.ScaledDummy(2f);

        // Secteur
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Ward"));
        ImGui.SetNextItemWidth(halfW);
        ImGui.InputInt("##ward", ref _ward, 1, 1);
        _ward = Math.Clamp(_ward, 1, 30);
    }

    private void DrawMaisonFields()
    {
        ImGuiHelpers.ScaledDummy(2f);
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Plot"));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
        ImGui.InputInt("##plot", ref _plot, 1, 1);
        _plot = Math.Clamp(_plot, 1, 60);

        _isSubdivision = _plot > 30;
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            _isSubdivision ? Loc.Get("Establishment.Location.Subdivision") : Loc.Get("Establishment.Location.Main"));
    }

    private void DrawAppartementFields()
    {
        ImGuiHelpers.ScaledDummy(2f);

        var availW = ImGui.GetContentRegionAvail().X;
        var halfW = (availW - ImGui.GetStyle().ItemSpacing.X) / 2f;

        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.RoomNumber"));
        ImGui.SetNextItemWidth(halfW);
        ImGui.InputInt("##roomNumber", ref _roomNumber, 1, 1);
        _roomNumber = Math.Max(_roomNumber, 1);
        ImGui.EndGroup();

        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Annex"));
        ImGui.Checkbox("##annexe", ref _isSubdivision);
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Location.AnnexTooltip"));
        ImGui.EndGroup();
    }

    private void DrawSyncSlotFields()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            Loc.Get("Establishment.Syncshell.Description"));
        ImGuiHelpers.ScaledDummy(2f);

        if (!_eligibleGroupsLoaded && !_eligibleGroupsLoading)
            _ = LoadEligibleGroups();

        if (_eligibleGroupsLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Syncshell.Loading"));
            return;
        }

        if (_eligibleGroups.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Syncshell.NoEligible"));
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Syncshell.NoEligibleTooltip"));
            return;
        }

        var preview = _linkedSyncshellGid != null ? _linkedSyncshellDisplay : Loc.Get("Establishment.Syncshell.None");
        ImGui.SetNextItemWidth(-1);
        using (var combo = ImRaii.Combo("##syncslotLink", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable(Loc.Get("Establishment.Syncshell.None"), _linkedSyncshellGid == null))
                {
                    _linkedSyncshellGid = null;
                    _linkedSyncshellDisplay = string.Empty;
                }
                foreach (var (gid, display, slotLoc) in _eligibleGroups)
                {
                    if (ImGui.Selectable(display, string.Equals(_linkedSyncshellGid, gid, StringComparison.Ordinal)))
                    {
                        _linkedSyncshellGid = gid;
                        _linkedSyncshellDisplay = display;
                        AutoFillFromSlotLocation(slotLoc);
                    }
                }
            }
        }

        if (_linkedSyncshellGid != null)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Unlink))
            {
                _linkedSyncshellGid = null;
                _linkedSyncshellDisplay = string.Empty;
            }
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Syncshell.Unlink"));
        }

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
            _ = LoadEligibleGroups();
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Syncshell.Refresh"));
    }

    private void DrawSubmitArea()
    {
        var canSubmit = !string.IsNullOrWhiteSpace(_name);

        if (_isSubmitting)
        {
            var text = Loc.Get("Establishment.Submit.Creating");
            var textW = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - textW) / 2f);
            ImGui.TextDisabled(text);
            return;
        }

        if (!canSubmit)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, Loc.Get("Establishment.Submit.NameRequired"));
            ImGuiHelpers.ScaledDummy(2f);
        }

        using (ImRaii.Disabled(!canSubmit))
        {
            using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
            using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered,
                new Vector4(
                    Math.Min(UiSharedService.AccentColor.X * 1.15f, 1f),
                    Math.Min(UiSharedService.AccentColor.Y * 1.15f, 1f),
                    Math.Min(UiSharedService.AccentColor.Z * 1.15f, 1f), 1f));
            if (ImGui.Button(Loc.Get("Establishment.Submit.Create"),
                new Vector2(ImGui.GetContentRegionAvail().X, 36f * ImGuiHelpers.GlobalScale)))
            {
                _ = Submit();
            }
        }
    }

    private async Task LoadEligibleGroups()
    {
        _eligibleGroupsLoading = true;
        try
        {
            var result = new List<(string Gid, string Display, API.Dto.Slot.SlotLocationDto? SlotLocation)>();
            var ownedGroups = _pairManager.GroupPairs
                .Where(g => string.Equals(g.Key.OwnerUID, _apiController.UID, StringComparison.Ordinal))
                .OrderBy(g => g.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var (info, _) in ownedGroups)
            {
                var slots = await _apiController.SlotGetInfoForGroup(new API.Dto.Group.GroupDto(info.Group)).ConfigureAwait(false);
                if (slots.Count > 0)
                    result.Add((info.GID, info.Group.AliasOrGID, slots[0].Location));
            }

            _eligibleGroups = result;
            _eligibleGroupsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading eligible groups for establishment sync slot");
        }
        finally
        {
            _eligibleGroupsLoading = false;
        }
    }

    private void AutoFillFromSlotLocation(API.Dto.Slot.SlotLocationDto? slotLoc)
    {
        if (slotLoc == null) return;

        _selectedWorldId = (ushort)slotLoc.ServerId;

        if (slotLoc.WardId > 0)
        {
            _housingType = 0;
            _selectedDistrictIndex = ResolveDistrictIndex(slotLoc.TerritoryId);
            _ward = (int)slotLoc.WardId;
            _plot = slotLoc.PlotId > 0 ? (int)slotLoc.PlotId : 1;
            _isSubdivision = slotLoc.DivisionId > 1;
        }
    }

    private void AutoFillHousing()
    {
        try
        {
            var loc = _dalamudUtilService.GetMapData();
            _selectedWorldId = (ushort)loc.ServerId;
            _selectedDistrictIndex = ResolveDistrictIndex(loc.TerritoryId);
            _ward = loc.WardId > 0 ? (int)loc.WardId : 1;
            _isSubdivision = loc.DivisionId > 1;

            if (loc.RoomId > 0)
            {
                _housingType = 1;
                _roomNumber = (int)loc.RoomId;
            }
            else
            {
                _housingType = 0;
                _plot = loc.HouseId > 0 ? (int)loc.HouseId : 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error auto-filling housing data");
        }
    }

    private static int ResolveDistrictIndex(uint territoryId)
    {
        for (int i = 0; i < ResidentialDistricts.Length; i++)
        {
            if (ResidentialDistricts[i].TerritoryId == territoryId)
                return i;
        }
        return 0;
    }

    private async Task Submit()
    {
        _isSubmitting = true;
        _logger.LogInformation("Creating establishment '{name}', type={type}", _name, _housingType == 0 ? "Maison" : "Appartement");
        try
        {
            var isApt = _housingType == 1;
            var location = new EstablishmentLocationDto
            {
                LocationType = 0,
                TerritoryId = ResidentialDistricts[_selectedDistrictIndex].TerritoryId,
                ServerId = _selectedWorldId != 0 ? _selectedWorldId : null,
                WardId = (uint)_ward,
                PlotId = isApt ? null : (uint)_plot,
                DivisionId = _isSubdivision ? 2u : 1u,
                IsApartment = isApt ? true : null,
                RoomId = isApt && _roomNumber > 0 ? (uint)_roomNumber : null,
            };

            var request = new EstablishmentCreateRequestDto
            {
                Name = _name.Trim(),
                Description = string.IsNullOrWhiteSpace(_description) ? null : _description,
                Category = _category,
                Schedule = string.IsNullOrWhiteSpace(_schedule) ? null : _schedule,
                FactionTag = string.IsNullOrWhiteSpace(_factionTag) ? null : _factionTag,
                IsPublic = _isPublic,
                LogoImageBase64 = _logoImageBytes.Length > 0 ? Convert.ToBase64String(_logoImageBytes) : null,
                BannerImageBase64 = _bannerImageBytes.Length > 0 ? Convert.ToBase64String(_bannerImageBytes) : null,
                Location = location
            };

            _logger.LogDebug("Submitting establishment creation request: {name}, category={cat}, location=Ward{ward}", request.Name, request.Category, _ward);
            var result = await _apiController.EstablishmentCreate(request).ConfigureAwait(false);
            if (result != null)
            {
                _logger.LogInformation("Establishment '{name}' created with id {id}", result.Name, result.Id);
                if (!string.IsNullOrEmpty(_linkedSyncshellGid))
                {
                    _establishmentConfigService.Current.EstablishmentSyncSlotBindings[result.Id] = _linkedSyncshellGid;
                    _establishmentConfigService.Save();
                }

                Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), $"'{result.Name}' {Loc.Get("Establishment.Notification.Created")}", NotificationType.Info));
                Mediator.Publish(new EstablishmentChangedMessage());
                Mediator.Publish(new OpenEstablishmentDetailMessage(result.Id));
                IsOpen = false;
                ResetFields();
            }
            else
            {
                _logger.LogWarning("Establishment creation returned null for '{name}'", _name);
                Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), Loc.Get("Establishment.Notification.CreateError"), NotificationType.Error));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating establishment");
            Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), Loc.Get("Establishment.Notification.UnexpectedError"), NotificationType.Error));
        }
        finally
        {
            _isSubmitting = false;
        }
    }

    private void ResetFields()
    {
        _name = string.Empty;
        _description = string.Empty;
        _category = 0;
        _schedule = string.Empty;
        _factionTag = string.Empty;
        _isPublic = true;
        _housingType = 0;
        _selectedWorldId = 0;
        _worldSearchFilter = string.Empty;
        _selectedDistrictIndex = 0;
        _ward = 1;
        _plot = 1;
        _isSubdivision = false;
        _roomNumber = 1;
        _linkedSyncshellGid = null;
        _linkedSyncshellDisplay = string.Empty;
        _eligibleGroupsLoaded = false;
        _eligibleGroups = [];
        _logoImageBytes = [];
        _bannerImageBytes = [];
        _logoTexture?.Dispose();
        _logoTexture = null;
        _bannerTexture?.Dispose();
        _bannerTexture = null;
        _imageMessage = null;
    }
}
