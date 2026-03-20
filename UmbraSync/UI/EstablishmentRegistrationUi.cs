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
    private int _locationType; // 0=Housing, 1=Zone
    private bool _isSubmitting;

    // Housing fields
    private ushort _selectedWorldId;
    private string _worldSearchFilter = string.Empty;
    private int _selectedDistrictIndex;
    private int _ward = 1;
    private int _plot = 1;
    private bool _isSubdivision;
    private bool _isApartment;
    private int _roomNumber;

    // Zone fields
    private float _x, _y, _z;
    private float _radius = 30f;

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

    private static readonly string[] CategoryNames =
    [
        "Taverne", "Boutique", "Temple", "Academie",
        "Guilde", "Residence", "Atelier", "Autre"
    ];

    private static readonly string[] LocationTypeNames = ["Housing", "Zone ouverte"];

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
        : base(logger, mediator, "Enregistrer un etablissement###EstablishmentRegistration", performanceCollectorService)
    {
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _establishmentConfigService = establishmentConfigService;
        _fileDialogManager = fileDialogManager;

        SizeConstraints = new()
        {
            MinimumSize = new(480, 500),
            MaximumSize = new(600, 700)
        };
    }

    protected override void DrawInternal()
    {
        if (!_apiController.IsConnected)
        {
            UiSharedService.ColorTextWrapped("Non connecte au serveur.", ImGuiColors.DalamudRed);
            return;
        }

        // Header
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.PlusCircle.ToIconString());
        ImGui.SameLine();
        _uiSharedService.BigText("Nouvel etablissement");
        ImGui.Separator();
        ImGui.Spacing();

        // General info section
        DrawSectionHeader(FontAwesomeIcon.InfoCircle, "Informations generales");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##name", "Nom de l'etablissement *", ref _name, 100);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextMultiline("##desc", ref _description, 2000, new Vector2(ImGui.GetContentRegionAvail().X, 60));

        ImGui.SetNextItemWidth(200);
        ImGui.Combo("Categorie", ref _category, CategoryNames, CategoryNames.Length);

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##schedule", "Horaires (ex: Ven-Sam 21h-1h)", ref _schedule, 200);

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##faction", "Faction / Organisation", ref _factionTag, 50);

        ImGui.Checkbox("Visible dans l'annuaire public", ref _isPublic);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Images section
        DrawImageSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Location section
        DrawSectionHeader(FontAwesomeIcon.MapMarkerAlt, "Localisation");

        ImGui.SetNextItemWidth(200);
        ImGui.Combo("Type de lieu", ref _locationType, LocationTypeNames, LocationTypeNames.Length);

        ImGui.Spacing();

        if (_locationType == 0)
            DrawHousingFields();
        else
            DrawZoneFields();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // SyncSlot link section
        DrawSyncSlotLink();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Submit
        if (_isSubmitting)
        {
            ImGui.TextDisabled("Creation en cours...");
        }
        else
        {
            var canSubmit = !string.IsNullOrWhiteSpace(_name);
            if (!canSubmit) ImGui.BeginDisabled();

            using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
            if (ImGui.Button("Creer l'etablissement", new Vector2(ImGui.GetContentRegionAvail().X, 32)))
                _ = Submit();

            if (!canSubmit) ImGui.EndDisabled();

            if (!canSubmit)
                ImGui.TextColored(ImGuiColors.DalamudOrange, "Le nom est obligatoire.");
        }
    }

    private static void DrawSectionHeader(FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, icon.ToIconString());
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.Spacing();
    }

    private void DrawHousingFields()
    {
        // Serveur
        var previewName = _selectedWorldId != 0 && _uiSharedService.WorldData.TryGetValue(_selectedWorldId, out var wn)
            ? wn : "Choisir un serveur...";
        ImGui.SetNextItemWidth(280);
        using (var combo = ImRaii.Combo("Serveur", previewName))
        {
            if (combo)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.InputTextWithHint("##worldSearch", "Rechercher...", ref _worldSearchFilter, 50);

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

        // Quartier résidentiel
        ImGui.SetNextItemWidth(280);
        ImGui.Combo("Quartier", ref _selectedDistrictIndex, DistrictNames, DistrictNames.Length);

        // Ward / Plot
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("Secteur (Ward)", ref _ward, 1, 1);
        _ward = Math.Clamp(_ward, 1, 30);

        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("Parcelle (Plot)", ref _plot, 1, 1);
        _plot = Math.Clamp(_plot, 1, 60);

        // Subdivision
        ImGui.Checkbox("Subdivision", ref _isSubdivision);
        UiSharedService.AttachToolTip("Cocher si le plot est dans la subdivision du quartier");

        // Appartement
        ImGui.Checkbox("Appartement", ref _isApartment);
        if (_isApartment)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("Chambre", ref _roomNumber, 1, 1);
            _roomNumber = Math.Max(_roomNumber, 0);
        }

        // Auto-fill
        ImGui.Spacing();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Crosshairs))
            AutoFillHousing();
        UiSharedService.AttachToolTip("Remplir depuis votre position actuelle");
    }

    private void DrawZoneFields()
    {
        ImGui.TextDisabled("Definissez la zone a l'aide de coordonnees et d'un rayon.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("X##zone", ref _x);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("Y##zone", ref _y);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("Z##zone", ref _z);

        ImGui.SetNextItemWidth(200);
        ImGui.SliderFloat("Rayon de detection", ref _radius, 5f, 200f, "%.0f");

        ImGui.Spacing();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Crosshairs))
            DetectCurrentPosition();
        UiSharedService.AttachToolTip("Detecter votre position actuelle");
    }

    private void DrawImageSection()
    {
        DrawSectionHeader(FontAwesomeIcon.Image, "Images");

        if (!string.IsNullOrEmpty(_imageMessage))
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, _imageMessage);
            ImGui.Spacing();
        }

        // Logo
        ImGui.TextUnformatted("Logo");
        if (_logoTexture != null && _logoImageBytes.Length > 0)
        {
            float imgSize = 100f * ImGuiHelpers.GlobalScale;
            float imgRounding = 8f * ImGuiHelpers.GlobalScale;
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
            ImGui.TextDisabled("Aucun logo.");
        }
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, "Charger un logo"))
        {
            _fileDialogManager.OpenFileDialog(
                "Choisir un logo",
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                        if (bytes.Length > 2 * 1024 * 1024)
                        {
                            _imageMessage = "Image trop volumineuse (max 2 Mo).";
                            return;
                        }
                        _imageMessage = null;
                        _logoImageBytes = bytes;
                        _logoTexture?.Dispose();
                        _logoTexture = _uiSharedService.LoadImage(bytes);
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_logoImageBytes.Length == 0))
        {
            using (ImRaii.PushId("clearLogo"))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Retirer"))
                {
                    _logoImageBytes = [];
                    _logoTexture?.Dispose();
                    _logoTexture = null;
                }
            }
        }

        ImGui.Spacing();

        // Banner
        ImGui.TextUnformatted("Banniere");
        if (_bannerTexture != null && _bannerImageBytes.Length > 0)
        {
            float availWidth = ImGui.GetContentRegionAvail().X;
            float bannerHeight = availWidth * (260f / 840f);
            float bannerRounding = 8f * ImGuiHelpers.GlobalScale;
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
            ImGui.TextDisabled("Aucune banniere.");
        }
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, "Charger une banniere"))
        {
            _fileDialogManager.OpenFileDialog(
                "Choisir une banniere",
                "Image files{.png,.jpg,.jpeg}",
                (success, name) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                        if (bytes.Length > 2 * 1024 * 1024)
                        {
                            _imageMessage = "Image trop volumineuse (max 2 Mo).";
                            return;
                        }
                        _imageMessage = null;
                        _bannerImageBytes = bytes;
                        _bannerTexture?.Dispose();
                        _bannerTexture = _uiSharedService.LoadImage(bytes);
                    });
                });
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_bannerImageBytes.Length == 0))
        {
            using (ImRaii.PushId("clearBanner"))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Retirer"))
                {
                    _bannerImageBytes = [];
                    _bannerTexture?.Dispose();
                    _bannerTexture = null;
                }
            }
        }
    }

    private void DrawSyncSlotLink()
    {
        DrawSectionHeader(FontAwesomeIcon.Link, "Liaison Syncshell");

        ImGui.TextDisabled("Seules les syncshells dont vous etes proprietaire et qui possedent un SyncSlot sont disponibles.");
        ImGui.Spacing();

        if (!_eligibleGroupsLoaded && !_eligibleGroupsLoading)
            _ = LoadEligibleGroups();

        if (_eligibleGroupsLoading)
        {
            ImGui.TextDisabled("Chargement des syncshells eligibles...");
            return;
        }

        if (_eligibleGroups.Count == 0)
        {
            ImGui.TextDisabled("Aucune syncshell eligible (vous devez etre proprietaire d'une syncshell avec un SyncSlot).");
            return;
        }

        var preview = _linkedSyncshellGid != null ? _linkedSyncshellDisplay : "Aucune";
        ImGui.SetNextItemWidth(280);
        using (var combo = ImRaii.Combo("##syncslotLink", preview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Aucune", _linkedSyncshellGid == null))
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
            UiSharedService.AttachToolTip("Retirer la liaison");
        }

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
            _ = LoadEligibleGroups();
        UiSharedService.AttachToolTip("Rafraichir la liste");
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
            _locationType = 0;
            _selectedDistrictIndex = ResolveDistrictIndex(slotLoc.TerritoryId);
            _ward = (int)slotLoc.WardId;
            _plot = slotLoc.PlotId > 0 ? (int)slotLoc.PlotId : 1;
            _isSubdivision = slotLoc.DivisionId > 1;
            _isApartment = false;
            _roomNumber = 0;
        }
        else
        {
            _locationType = 1;
            _x = slotLoc.X;
            _y = slotLoc.Y;
            _z = slotLoc.Z;
            _radius = slotLoc.Radius;
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
            _plot = loc.HouseId > 0 ? (int)loc.HouseId : 1;
            _isSubdivision = loc.DivisionId > 1;
            _roomNumber = loc.RoomId > 0 ? (int)loc.RoomId : 0;
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

    private void DetectCurrentPosition()
    {
        try
        {
            var player = _dalamudUtilService.GetPlayerCharacter();
            if (player != null)
            {
                _x = player.Position.X;
                _y = player.Position.Y;
                _z = player.Position.Z;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error detecting current position");
        }
    }

    private async Task Submit()
    {
        _isSubmitting = true;
        try
        {
            var location = new EstablishmentLocationDto
            {
                LocationType = _locationType,
                TerritoryId = _locationType == 0 ? ResidentialDistricts[_selectedDistrictIndex].TerritoryId : _dalamudUtilService.GetMapDataAsync().GetAwaiter().GetResult().TerritoryId,
                ServerId = _selectedWorldId != 0 ? _selectedWorldId : null,
                WardId = _locationType == 0 ? (uint)_ward : null,
                PlotId = _locationType == 0 ? (uint)_plot : null,
                DivisionId = _locationType == 0 ? (_isSubdivision ? 2u : 1u) : null,
                IsApartment = _isApartment ? true : null,
                RoomId = _isApartment && _roomNumber > 0 ? (uint)_roomNumber : null,
                X = _locationType == 1 ? _x : null,
                Y = _locationType == 1 ? _y : null,
                Z = _locationType == 1 ? _z : null,
                Radius = _locationType == 1 ? _radius : null
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

            var result = await _apiController.EstablishmentCreate(request).ConfigureAwait(false);
            if (result != null)
            {
                // Save syncshell binding if linked
                if (!string.IsNullOrEmpty(_linkedSyncshellGid))
                {
                    _establishmentConfigService.Current.EstablishmentSyncSlotBindings[result.Id] = _linkedSyncshellGid;
                    _establishmentConfigService.Save();
                }

                Mediator.Publish(new NotificationMessage("Etablissement", $"'{result.Name}' cree avec succes !", NotificationType.Info));
                Mediator.Publish(new OpenEstablishmentDetailMessage(result.Id));
                IsOpen = false;
                ResetFields();
            }
            else
            {
                Mediator.Publish(new NotificationMessage("Etablissement", "Erreur lors de la creation", NotificationType.Error));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating establishment");
            Mediator.Publish(new NotificationMessage("Etablissement", "Erreur inattendue", NotificationType.Error));
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
        _selectedWorldId = 0;
        _worldSearchFilter = string.Empty;
        _selectedDistrictIndex = 0;
        _ward = 1;
        _plot = 1;
        _isSubdivision = false;
        _isApartment = false;
        _roomNumber = 0;
        _x = _y = _z = 0;
        _radius = 30f;
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
