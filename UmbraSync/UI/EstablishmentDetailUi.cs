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

internal class EstablishmentDetailUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly FileDialogManager _fileDialogManager;

    private EstablishmentDto? _establishment;
    private Guid _currentId;
    private bool _isLoading;
    private bool _isEditing;

    // Edit fields
    private string _editName = string.Empty;
    private string _editDescription = string.Empty;
    private int _editCategory;
    private string _editSchedule = string.Empty;
    private string _editFactionTag = string.Empty;
    private bool _editIsPublic = true;

    // Event creation
    private string _newEventTitle = string.Empty;
    private string _newEventDescription = string.Empty;

    // SyncSlot binding
    private string _syncSlotGid = string.Empty;

    // Images (view + edit)
    private IDalamudTextureWrap? _logoTexture;
    private IDalamudTextureWrap? _bannerTexture;
    private byte[] _editLogoBytes = [];
    private byte[] _editBannerBytes = [];
    private IDalamudTextureWrap? _editLogoTexture;
    private IDalamudTextureWrap? _editBannerTexture;
    private string? _imageMessage;

    // Eligible groups cache (owned + has SyncSlot)
    private List<(string Gid, string Display)> _eligibleGroups = [];
    private bool _eligibleGroupsLoading;
    private bool _eligibleGroupsLoaded;

    private static readonly string[] CategoryNames =
    [
        "Taverne", "Boutique", "Temple", "Academie",
        "Guilde", "Residence", "Atelier", "Autre"
    ];

    private static readonly Dictionary<uint, string> DistrictNamesByTerritory = new()
    {
        { 339, "Brum\u00e9e" }, { 340, "Lavandi\u00e8re" }, { 341, "La Coupe" },
        { 641, "Shirogane" }, { 979, "Empyr\u00e9e" },
    };

    private static string ResolveDistrictName(uint territoryId)
        => DistrictNamesByTerritory.TryGetValue(territoryId, out var name) ? name : $"Zone {territoryId}";

    private static readonly FontAwesomeIcon[] CategoryIcons =
    [
        FontAwesomeIcon.Beer, FontAwesomeIcon.ShoppingBag, FontAwesomeIcon.Church, FontAwesomeIcon.GraduationCap,
        FontAwesomeIcon.Shield, FontAwesomeIcon.Home, FontAwesomeIcon.Hammer, FontAwesomeIcon.EllipsisH
    ];

    public EstablishmentDetailUi(ILogger<EstablishmentDetailUi> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService,
        UiSharedService uiSharedService, PairManager pairManager,
        FileDialogManager fileDialogManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Detail etablissement###EstablishmentDetail", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _fileDialogManager = fileDialogManager;

        SizeConstraints = new()
        {
            MinimumSize = new(520, 450),
            MaximumSize = new(800, 750)
        };

        Mediator.Subscribe<OpenEstablishmentDetailMessage>(this, msg =>
        {
            _currentId = msg.EstablishmentId;
            _isEditing = false;
            _eligibleGroupsLoaded = false;
            _eligibleGroups = [];
            IsOpen = true;
            _ = LoadEstablishment();
        });
    }

    protected override void DrawInternal()
    {
        if (_isLoading)
        {
            ImGui.TextDisabled("Chargement...");
            return;
        }

        if (_establishment == null)
        {
            UiSharedService.ColorTextWrapped("Etablissement introuvable ou supprime.", ImGuiColors.DalamudRed);
            return;
        }

        var isOwner = string.Equals(_establishment.OwnerUID, _apiController.UID, StringComparison.Ordinal);

        DrawHeader(isOwner);
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("##detailTabs");
        if (!tabBar) return;

        using (var infoTab = ImRaii.TabItem("Informations"))
        {
            if (infoTab)
            {
                if (_isEditing && isOwner)
                    DrawEditMode();
                else
                    DrawViewMode();
            }
        }

        using (var eventsTab = ImRaii.TabItem($"Evenements ({_establishment.Events.Count})"))
        {
            if (eventsTab)
                DrawEventsTab(isOwner);
        }

        using (var imagesTab = ImRaii.TabItem("Images"))
        {
            if (imagesTab)
                DrawImagesTab(isOwner);
        }

        using (var syncTab = ImRaii.TabItem("Liaison Sync"))
        {
            if (syncTab)
                DrawSyncSlotTab();
        }
    }

    private void DrawHeader(bool isOwner)
    {
        var catIndex = _establishment!.Category;
        var catIcon = catIndex >= 0 && catIndex < CategoryIcons.Length ? CategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;
        var catName = catIndex >= 0 && catIndex < CategoryNames.Length ? CategoryNames[catIndex] : "?";

        // Category icon
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());
        ImGui.SameLine();

        // Title
        _uiSharedService.BigText(_establishment.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"[{catName}]");

        // Right-aligned buttons
        var rightX = ImGui.GetContentRegionAvail().X;
        var iconSizes = 0f;
        if (isOwner) iconSizes += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Edit).X + ImGui.GetStyle().ItemSpacing.X;
        iconSizes += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Star).X + ImGui.GetStyle().ItemSpacing.X;
        if (isOwner) iconSizes += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Trash).X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(ImGui.GetCursorPosX() + rightX - iconSizes);

        // Bookmark
        var isBookmarked = _configService.Current.BookmarkedEstablishments.Contains(_establishment.Id);
        if (_uiSharedService.IconButton(isBookmarked ? FontAwesomeIcon.Star : FontAwesomeIcon.StarHalfAlt))
        {
            if (isBookmarked)
                _configService.Current.BookmarkedEstablishments.Remove(_establishment.Id);
            else
                _configService.Current.BookmarkedEstablishments.Add(_establishment.Id);
            _configService.Save();
        }
        UiSharedService.AttachToolTip(isBookmarked ? "Retirer des favoris" : "Ajouter aux favoris");

        if (isOwner)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(_isEditing ? FontAwesomeIcon.Times : FontAwesomeIcon.Edit))
            {
                if (!_isEditing) PopulateEditFields();
                _isEditing = !_isEditing;
            }
            UiSharedService.AttachToolTip(_isEditing ? "Annuler les modifications" : "Modifier");

            ImGui.SameLine();
            using var red = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                _ = DeleteEstablishment();
            UiSharedService.AttachToolTip("Supprimer cet etablissement");
        }

        // Owner line
        var owner = _establishment.OwnerAlias ?? _establishment.OwnerUID;
        ImGui.TextDisabled($"Proprietaire: {owner}  |  Derniere MAJ: {_establishment.UpdatedUtc:g}");
    }

    private void DrawViewMode()
    {
        ImGui.Spacing();

        if (!string.IsNullOrEmpty(_establishment!.Description))
        {
            ImGui.TextWrapped(_establishment.Description);
            ImGui.Spacing();
        }

        // Info grid
        if (!string.IsNullOrEmpty(_establishment.Schedule))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Clock.ToIconString());
            ImGui.SameLine();
            ImGui.Text(_establishment.Schedule);
        }

        if (!string.IsNullOrEmpty(_establishment.FactionTag))
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Flag.ToIconString());
            ImGui.SameLine();
            ImGui.Text(_establishment.FactionTag);
        }

        if (_establishment.Languages.Length > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Globe.ToIconString());
            ImGui.SameLine();
            ImGui.Text(string.Join(", ", _establishment.Languages));
        }

        if (_establishment.Tags.Length > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Tags.ToIconString());
            ImGui.SameLine();
            ImGui.Text(string.Join(", ", _establishment.Tags));
        }

        // Location info
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var loc = _establishment.Location;
        if (loc != null)
        {
            var locType = (EstablishmentLocationType)loc.LocationType;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.MapMarkerAlt.ToIconString());
            ImGui.SameLine();

            if (locType == EstablishmentLocationType.Housing)
            {
                var serverName = loc.ServerId.HasValue && _uiSharedService.WorldData.TryGetValue((ushort)loc.ServerId.Value, out var sn)
                    ? sn : loc.ServerId?.ToString() ?? "?";
                var districtName = ResolveDistrictName(loc.TerritoryId);
                var subdivText = loc.DivisionId > 1 ? " (Subdivision)" : string.Empty;
                ImGui.Text($"{districtName} — {serverName}, Secteur {loc.WardId}, Parcelle {loc.PlotId}{subdivText}");
            }
            else
            {
                ImGui.Text($"Zone — Position ({loc.X:F0}, {loc.Y:F0}, {loc.Z:F0}), Rayon {loc.Radius:F0}");
            }
        }
    }

    private void DrawEditMode()
    {
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##editName", ref _editName, 100);
        UiSharedService.AttachToolTip("Nom de l'etablissement");

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextMultiline("##editDesc", ref _editDescription, 2000, new Vector2(ImGui.GetContentRegionAvail().X, 80));

        ImGui.SetNextItemWidth(200);
        ImGui.Combo("Categorie##edit", ref _editCategory, CategoryNames, CategoryNames.Length);

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Horaires##edit", ref _editSchedule, 200);

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Faction##edit", ref _editFactionTag, 50);

        ImGui.Checkbox("Public##edit", ref _editIsPublic);

        ImGui.Spacing();
        using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
        if (ImGui.Button("Sauvegarder", new Vector2(120, 0)))
            _ = SaveChanges();
    }

    private void DrawEventsTab(bool isOwner)
    {
        ImGui.Spacing();

        if (_establishment!.Events.Count == 0)
        {
            ImGui.TextDisabled("Aucun evenement programme.");
        }
        else
        {
            foreach (var evt in _establishment.Events.OrderBy(e => e.StartsAtUtc))
            {
                ImGui.PushID(evt.Id.ToString());

                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Calendar.ToIconString());
                ImGui.SameLine();
                UiSharedService.ColorText(evt.Title, new Vector4(1f, 0.9f, 0.6f, 1f));
                ImGui.SameLine();
                ImGui.TextDisabled($"— {evt.StartsAtUtc:g}");

                if (evt.EndsAtUtc.HasValue)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"a {evt.EndsAtUtc.Value:t}");
                }

                if (isOwner)
                {
                    ImGui.SameLine();
                    using var red = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.5f, 0.1f, 0.1f, 1f));
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                        _ = DeleteEvent(evt.Id);
                    UiSharedService.AttachToolTip("Supprimer cet evenement");
                }

                if (!string.IsNullOrEmpty(evt.Description))
                {
                    ImGui.Indent(24);
                    ImGui.TextDisabled(evt.Description);
                    ImGui.Unindent(24);
                }

                ImGui.PopID();
                ImGui.Spacing();
            }
        }

        if (isOwner)
        {
            ImGui.Separator();
            ImGui.Spacing();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.PlusCircle.ToIconString());
            ImGui.SameLine();
            ImGui.Text("Nouvel evenement");

            ImGui.SetNextItemWidth(250);
            ImGui.InputText("Titre##newevent", ref _newEventTitle, 100);

            ImGui.SetNextItemWidth(250);
            ImGui.InputText("Description##newevent", ref _newEventDescription, 500);

            using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
            if (ImGui.Button("Ajouter##newevent") && !string.IsNullOrWhiteSpace(_newEventTitle))
                _ = CreateEvent();
        }
    }

    private void DrawSyncSlotTab()
    {
        ImGui.Spacing();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Link.ToIconString());
        ImGui.SameLine();
        ImGui.Text("Liaison avec une Syncshell");
        ImGui.Spacing();

        ImGui.TextWrapped("Quand vous entrez dans cet etablissement, vous rejoindrez automatiquement la syncshell liee.");
        ImGui.TextDisabled("Seules les syncshells dont vous etes proprietaire et qui possedent un SyncSlot sont disponibles.");
        ImGui.Spacing();

        if (!_eligibleGroupsLoaded && !_eligibleGroupsLoading)
            _ = LoadEligibleGroups();

        if (_eligibleGroupsLoading)
        {
            ImGui.TextDisabled("Chargement des syncshells eligibles...");
            return;
        }

        var bindings = _configService.Current.EstablishmentSyncSlotBindings;
        bindings.TryGetValue(_establishment!.Id, out var currentGid);
        _syncSlotGid = currentGid ?? string.Empty;

        // Resolve display name for current binding
        var currentDisplay = "Aucune";
        if (!string.IsNullOrEmpty(_syncSlotGid))
        {
            var match = _eligibleGroups.FirstOrDefault(g => string.Equals(g.Gid, _syncSlotGid, StringComparison.Ordinal));
            currentDisplay = match.Display ?? _syncSlotGid;
        }

        ImGui.SetNextItemWidth(280);
        using (var combo = ImRaii.Combo("##syncslotBinding", currentDisplay))
        {
            if (combo)
            {
                if (ImGui.Selectable("Aucune", string.IsNullOrEmpty(_syncSlotGid)))
                {
                    bindings.Remove(_establishment.Id);
                    _syncSlotGid = string.Empty;
                    _configService.Save();
                }
                foreach (var (gid, display) in _eligibleGroups)
                {
                    if (ImGui.Selectable(display, string.Equals(_syncSlotGid, gid, StringComparison.Ordinal)))
                    {
                        bindings[_establishment.Id] = gid;
                        _syncSlotGid = gid;
                        _configService.Save();
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(_syncSlotGid))
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Unlink))
            {
                bindings.Remove(_establishment.Id);
                _syncSlotGid = string.Empty;
                _configService.Save();
            }
            UiSharedService.AttachToolTip("Supprimer la liaison");
        }

        if (_eligibleGroups.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Aucune syncshell eligible (vous devez etre proprietaire d'une syncshell avec un SyncSlot).");
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
            var result = new List<(string Gid, string Display)>();
            var ownedGroups = _pairManager.GroupPairs
                .Where(g => string.Equals(g.Key.OwnerUID, _apiController.UID, StringComparison.Ordinal))
                .OrderBy(g => g.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var (info, _) in ownedGroups)
            {
                var slots = await _apiController.SlotGetInfoForGroup(new API.Dto.Group.GroupDto(info.Group)).ConfigureAwait(false);
                if (slots.Count > 0)
                    result.Add((info.GID, info.Group.AliasOrGID));
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

    private void DrawImagesTab(bool isOwner)
    {
        ImGui.Spacing();

        if (!string.IsNullOrEmpty(_imageMessage))
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, _imageMessage);
            ImGui.Spacing();
        }

        // Banner display
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Image.ToIconString());
        ImGui.SameLine();
        ImGui.Text("Banniere");

        var bannerTex = isOwner && _isEditing && _editBannerTexture != null ? _editBannerTexture : _bannerTexture;
        var bannerBytes = isOwner && _isEditing ? _editBannerBytes : null;
        var hasBanner = bannerTex != null && (bannerBytes == null ? _establishment!.BannerImageBase64 is { Length: > 0 } : bannerBytes.Length > 0);

        if (hasBanner && bannerTex != null)
        {
            float availWidth = ImGui.GetContentRegionAvail().X;
            float bannerHeight = availWidth * (260f / 840f);
            float bannerRounding = 8f * ImGuiHelpers.GlobalScale;
            var bannerDrawList = ImGui.GetWindowDrawList();
            var bannerMin = ImGui.GetCursorScreenPos();
            var bannerMax = new Vector2(bannerMin.X + availWidth, bannerMin.Y + bannerHeight);
            bannerDrawList.AddImageRounded(
                bannerTex.Handle, bannerMin, bannerMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), bannerRounding);
            ImGui.Dummy(new Vector2(availWidth, bannerHeight));
        }
        else
        {
            ImGui.TextDisabled("Aucune banniere.");
        }

        if (isOwner)
        {
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
                            _editBannerBytes = bytes;
                            _editBannerTexture?.Dispose();
                            _editBannerTexture = _uiSharedService.LoadImage(bytes);
                        });
                    });
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(_editBannerBytes.Length == 0 && _establishment!.BannerImageBase64 is not { Length: > 0 }))
            {
                using (ImRaii.PushId("clearBanner"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Retirer"))
                    {
                        _editBannerBytes = [];
                        _editBannerTexture?.Dispose();
                        _editBannerTexture = null;
                        _bannerTexture?.Dispose();
                        _bannerTexture = null;
                        _ = SaveImages(clearBanner: true);
                    }
                }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(_editBannerBytes.Length == 0))
            {
                using (ImRaii.PushId("saveBanner"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Sauvegarder"))
                        _ = SaveImages();
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Logo display
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Portrait.ToIconString());
        ImGui.SameLine();
        ImGui.Text("Logo");

        var logoTex = isOwner && _editLogoTexture != null ? _editLogoTexture : _logoTexture;
        var logoBytes = isOwner ? _editLogoBytes : null;
        var hasLogo = logoTex != null && (logoBytes == null ? _establishment!.LogoImageBase64 is { Length: > 0 } : logoBytes.Length > 0);

        if (hasLogo && logoTex != null)
        {
            float imgSize = 120f * ImGuiHelpers.GlobalScale;
            float imgRounding = 8f * ImGuiHelpers.GlobalScale;
            var drawList = ImGui.GetWindowDrawList();
            var imgMin = ImGui.GetCursorScreenPos();
            var imgMax = new Vector2(imgMin.X + imgSize, imgMin.Y + imgSize);
            drawList.AddImageRounded(
                logoTex.Handle, imgMin, imgMax,
                Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(Vector4.One), imgRounding);
            ImGui.Dummy(new Vector2(imgSize, imgSize));
        }
        else
        {
            ImGui.TextDisabled("Aucun logo.");
        }

        if (isOwner)
        {
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
                            _editLogoBytes = bytes;
                            _editLogoTexture?.Dispose();
                            _editLogoTexture = _uiSharedService.LoadImage(bytes);
                        });
                    });
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(_editLogoBytes.Length == 0 && _establishment!.LogoImageBase64 is not { Length: > 0 }))
            {
                using (ImRaii.PushId("clearLogo"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Retirer"))
                    {
                        _editLogoBytes = [];
                        _editLogoTexture?.Dispose();
                        _editLogoTexture = null;
                        _logoTexture?.Dispose();
                        _logoTexture = null;
                        _ = SaveImages(clearLogo: true);
                    }
                }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(_editLogoBytes.Length == 0))
            {
                using (ImRaii.PushId("saveLogo"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Sauvegarder"))
                        _ = SaveImages();
                }
            }
        }
    }

    private async Task SaveImages(bool clearLogo = false, bool clearBanner = false)
    {
        var request = new EstablishmentUpdateRequestDto
        {
            Id = _establishment!.Id,
            Name = _establishment.Name,
            Description = _establishment.Description,
            Category = _establishment.Category,
            Schedule = _establishment.Schedule,
            FactionTag = _establishment.FactionTag,
            IsPublic = _establishment.IsPublic,
            LogoImageBase64 = clearLogo ? null : (_editLogoBytes.Length > 0 ? Convert.ToBase64String(_editLogoBytes) : _establishment.LogoImageBase64),
            BannerImageBase64 = clearBanner ? null : (_editBannerBytes.Length > 0 ? Convert.ToBase64String(_editBannerBytes) : _establishment.BannerImageBase64),
            Location = _establishment.Location
        };

        var success = await _apiController.EstablishmentUpdate(request).ConfigureAwait(false);
        if (success)
        {
            Mediator.Publish(new NotificationMessage("Etablissement", "Images sauvegardees", NotificationType.Info));
            _editLogoBytes = [];
            _editBannerBytes = [];
            await LoadEstablishment().ConfigureAwait(false);
        }
    }

    private void PopulateEditFields()
    {
        _editName = _establishment!.Name;
        _editDescription = _establishment.Description ?? string.Empty;
        _editCategory = _establishment.Category;
        _editSchedule = _establishment.Schedule ?? string.Empty;
        _editFactionTag = _establishment.FactionTag ?? string.Empty;
        _editIsPublic = _establishment.IsPublic;
    }

    private async Task SaveChanges()
    {
        var request = new EstablishmentUpdateRequestDto
        {
            Id = _establishment!.Id,
            Name = _editName,
            Description = string.IsNullOrWhiteSpace(_editDescription) ? null : _editDescription,
            Category = _editCategory,
            Schedule = string.IsNullOrWhiteSpace(_editSchedule) ? null : _editSchedule,
            FactionTag = string.IsNullOrWhiteSpace(_editFactionTag) ? null : _editFactionTag,
            IsPublic = _editIsPublic,
            LogoImageBase64 = _establishment.LogoImageBase64,
            BannerImageBase64 = _establishment.BannerImageBase64,
            Location = _establishment.Location
        };

        var success = await _apiController.EstablishmentUpdate(request).ConfigureAwait(false);
        if (success)
        {
            _isEditing = false;
            Mediator.Publish(new NotificationMessage("Etablissement", "Modifications sauvegardees", NotificationType.Info));
            await LoadEstablishment().ConfigureAwait(false);
        }
    }

    private async Task DeleteEstablishment()
    {
        var success = await _apiController.EstablishmentDelete(_establishment!.Id).ConfigureAwait(false);
        if (success)
        {
            Mediator.Publish(new NotificationMessage("Etablissement", "Etablissement supprime", NotificationType.Info));
            _establishment = null;
            IsOpen = false;
        }
    }

    private async Task CreateEvent()
    {
        var request = new EstablishmentEventUpsertRequestDto
        {
            EstablishmentId = _establishment!.Id,
            Title = _newEventTitle.Trim(),
            Description = string.IsNullOrWhiteSpace(_newEventDescription) ? null : _newEventDescription,
            StartsAtUtc = DateTime.UtcNow.AddHours(1)
        };

        var result = await _apiController.EstablishmentEventUpsert(request).ConfigureAwait(false);
        if (result != null)
        {
            _newEventTitle = string.Empty;
            _newEventDescription = string.Empty;
            await LoadEstablishment().ConfigureAwait(false);
        }
    }

    private async Task DeleteEvent(Guid eventId)
    {
        var success = await _apiController.EstablishmentEventDelete(eventId).ConfigureAwait(false);
        if (success)
            await LoadEstablishment().ConfigureAwait(false);
    }

    private async Task LoadEstablishment()
    {
        _isLoading = true;
        try
        {
            _establishment = await _apiController.EstablishmentGetById(_currentId).ConfigureAwait(false);

            // Load textures from DTO
            _logoTexture?.Dispose();
            _logoTexture = null;
            _bannerTexture?.Dispose();
            _bannerTexture = null;
            _editLogoTexture?.Dispose();
            _editLogoTexture = null;
            _editBannerTexture?.Dispose();
            _editBannerTexture = null;
            _editLogoBytes = [];
            _editBannerBytes = [];
            _imageMessage = null;

            if (_establishment?.LogoImageBase64 is { Length: > 0 } logoB64)
            {
                try { _logoTexture = _uiSharedService.LoadImage(Convert.FromBase64String(logoB64)); }
                catch { /* ignore invalid image */ }
            }
            if (_establishment?.BannerImageBase64 is { Length: > 0 } bannerB64)
            {
                try { _bannerTexture = _uiSharedService.LoadImage(Convert.FromBase64String(bannerB64)); }
                catch { /* ignore invalid image */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading establishment {id}", _currentId);
        }
        finally
        {
            _isLoading = false;
        }
    }
}
