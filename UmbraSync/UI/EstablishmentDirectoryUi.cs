using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

internal class EstablishmentDirectoryUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly EstablishmentConfigService _configService;
    private readonly UiSharedService _uiSharedService;

    private EstablishmentListResponseDto? _currentResults;
    private List<EstablishmentDto>? _ownedEstablishments;
    private bool _isLoading;
    private string _searchText = string.Empty;
    private int _selectedCategory = -1;
    private int _currentPage;
    private int _activeTab; // 0=browse, 1=bookmarks, 2=mine

    private static readonly string[] CategoryNames =
    [
        "Taverne", "Boutique", "Temple", "Academie",
        "Guilde", "Residence", "Atelier", "Autre"
    ];

    private static readonly FontAwesomeIcon[] CategoryIcons =
    [
        FontAwesomeIcon.Beer, FontAwesomeIcon.ShoppingBag, FontAwesomeIcon.Church, FontAwesomeIcon.GraduationCap,
        FontAwesomeIcon.Shield, FontAwesomeIcon.Home, FontAwesomeIcon.Hammer, FontAwesomeIcon.EllipsisH
    ];

    public EstablishmentDirectoryUi(ILogger<EstablishmentDirectoryUi> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService,
        UiSharedService uiSharedService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Annuaire des etablissements###EstablishmentDirectory", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;

        SizeConstraints = new()
        {
            MinimumSize = new(620, 450),
            MaximumSize = new(900, 800)
        };

        Mediator.Subscribe<ConnectedMessage>(this, (msg) => _ = RefreshList());
    }

    protected override void DrawInternal()
    {
        if (!_apiController.IsConnected)
        {
            UiSharedService.ColorTextWrapped("Non connecte au serveur.", ImGuiColors.DalamudRed);
            return;
        }

        DrawToolbar();
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("##estabTabs");
        if (!tabBar) return;

        using (var browseTab = ImRaii.TabItem("Parcourir"))
        {
            if (browseTab)
            {
                if (_activeTab != 0) { _activeTab = 0; _ = RefreshList(); }
                DrawBrowseTab();
            }
        }

        using (var bookmarkTab = ImRaii.TabItem("Favoris"))
        {
            if (bookmarkTab)
            {
                if (_activeTab != 1) { _activeTab = 1; _ = RefreshBookmarks(); }
                DrawBookmarksTab();
            }
        }

        using (var mineTab = ImRaii.TabItem("Mes etablissements"))
        {
            if (mineTab)
            {
                if (_activeTab != 2) { _activeTab = 2; _ = RefreshOwned(); }
                DrawOwnedTab();
            }
        }
    }

    private void DrawToolbar()
    {
        // Search
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputTextWithHint("##search", "Rechercher...", ref _searchText, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _currentPage = 0;
            _ = RefreshList();
        }

        // Category filter
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        var categoryPreview = _selectedCategory >= 0 && _selectedCategory < CategoryNames.Length
            ? CategoryNames[_selectedCategory] : "Toutes categories";
        using (var combo = ImRaii.Combo("##category", categoryPreview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Toutes categories", _selectedCategory == -1))
                {
                    _selectedCategory = -1;
                    _currentPage = 0;
                    _ = RefreshList();
                }
                for (int i = 0; i < CategoryNames.Length; i++)
                {
                    if (ImGui.Selectable(CategoryNames[i], _selectedCategory == i))
                    {
                        _selectedCategory = i;
                        _currentPage = 0;
                        _ = RefreshList();
                    }
                }
            }
        }

        // Search button
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Search))
        {
            _currentPage = 0;
            _ = RefreshList();
        }
        UiSharedService.AttachToolTip("Rechercher");

        // Refresh button
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
        {
            _currentPage = 0;
            _ = RefreshList();
        }
        UiSharedService.AttachToolTip("Rafraichir");

        // Create button
        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X;
        var btnSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - btnSize.X);
        if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            Mediator.Publish(new UiToggleMessage(typeof(EstablishmentRegistrationUi)));
        UiSharedService.AttachToolTip("Enregistrer un nouvel etablissement");
    }

    private void DrawBrowseTab()
    {
        if (_isLoading)
        {
            ImGui.TextDisabled("Chargement...");
            return;
        }

        if (_currentResults == null)
        {
            ImGui.TextDisabled("Appuyez sur Rechercher pour charger l'annuaire.");
            return;
        }

        if (_currentResults.Establishments.Count == 0)
        {
            ImGui.TextDisabled("Aucun etablissement trouve.");
            return;
        }

        using var child = ImRaii.Child("##browseList", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 4));
        if (child)
        {
            foreach (var establishment in _currentResults.Establishments)
            {
                DrawEstablishmentCard(establishment);
            }
        }

        DrawPagination();
    }

    private void DrawBookmarksTab()
    {
        var bookmarks = _configService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0)
        {
            ImGui.TextDisabled("Aucun favori. Ajoutez des etablissements en cliquant sur l'etoile.");
            return;
        }

        ImGui.TextDisabled($"{bookmarks.Count} favori(s)");
        foreach (var id in bookmarks.ToList())
        {
            ImGui.PushID(id.ToString());
            ImGui.BulletText(id.ToString()[..8] + "...");
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                Mediator.Publish(new OpenEstablishmentDetailMessage(id));
            UiSharedService.AttachToolTip("Voir le detail");
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
            {
                bookmarks.Remove(id);
                _configService.Save();
            }
            UiSharedService.AttachToolTip("Retirer des favoris");
            ImGui.PopID();
        }
    }

    private void DrawOwnedTab()
    {
        if (_ownedEstablishments == null)
        {
            ImGui.TextDisabled("Chargement...");
            return;
        }

        if (_ownedEstablishments.Count == 0)
        {
            ImGui.TextDisabled("Vous n'avez aucun etablissement enregistre.");
            return;
        }

        foreach (var establishment in _ownedEstablishments)
        {
            DrawEstablishmentCard(establishment);
        }
    }

    private void DrawEstablishmentCard(EstablishmentDto establishment)
    {
        ImGui.PushID(establishment.Id.ToString());

        var isBookmarked = _configService.Current.BookmarkedEstablishments.Contains(establishment.Id);
        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < CategoryIcons.Length ? CategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;
        var catName = catIndex >= 0 && catIndex < CategoryNames.Length ? CategoryNames[catIndex] : "?";

        using (var card = ImRaii.Child($"##card_{establishment.Id}", new Vector2(ImGui.GetContentRegionAvail().X, 68), true))
        {
            if (card)
            {
                // Row 1: Icon + Name + Category + Bookmark + Open
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());

                ImGui.SameLine();
                _uiSharedService.BigText(establishment.Name);

                ImGui.SameLine();
                ImGui.TextDisabled($"[{catName}]");

                // Right-aligned buttons
                var rightOffset = ImGui.GetContentRegionAvail().X;
                var starSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Star);
                var eyeSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Eye);
                ImGui.SameLine(ImGui.GetCursorPosX() + rightOffset - starSize.X - eyeSize.X - ImGui.GetStyle().ItemSpacing.X);

                if (_uiSharedService.IconButton(isBookmarked ? FontAwesomeIcon.Star : FontAwesomeIcon.StarHalfAlt))
                {
                    if (isBookmarked)
                        _configService.Current.BookmarkedEstablishments.Remove(establishment.Id);
                    else
                        _configService.Current.BookmarkedEstablishments.Add(establishment.Id);
                    _configService.Save();
                }
                UiSharedService.AttachToolTip(isBookmarked ? "Retirer des favoris" : "Ajouter aux favoris");

                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                UiSharedService.AttachToolTip("Voir le detail");

                // Row 2: Description + owner
                if (!string.IsNullOrEmpty(establishment.Description))
                {
                    var desc = establishment.Description.Length > 90
                        ? establishment.Description[..90] + "..."
                        : establishment.Description;
                    ImGui.TextDisabled(desc);
                }
                else
                {
                    var owner = establishment.OwnerAlias ?? establishment.OwnerUID;
                    ImGui.TextDisabled($"par {owner}");
                }
            }
        }

        ImGui.PopID();
    }

    private void DrawPagination()
    {
        if (_currentResults == null) return;
        var totalPages = Math.Max(1, (_currentResults.TotalCount + _currentResults.PageSize - 1) / Math.Max(_currentResults.PageSize, 1));

        if (_currentPage > 0 && _uiSharedService.IconButton(FontAwesomeIcon.ChevronLeft))
        {
            _currentPage--;
            _ = RefreshList();
        }

        ImGui.SameLine();
        ImGui.Text($"Page {_currentPage + 1} / {totalPages}  ({_currentResults.TotalCount} resultats)");

        if (_currentPage < totalPages - 1)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.ChevronRight))
            {
                _currentPage++;
                _ = RefreshList();
            }
        }
    }

    private async Task RefreshList()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            var request = new EstablishmentListRequestDto
            {
                SearchText = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
                Category = _selectedCategory >= 0 ? _selectedCategory : null,
                Page = _currentPage,
                PageSize = 20
            };
            _currentResults = await _apiController.EstablishmentList(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing establishment list");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static Task RefreshBookmarks()
    {
        // Bookmarks are local, no server call needed for the tab display
        return Task.CompletedTask;
    }

    private async Task RefreshOwned()
    {
        try
        {
            _ownedEstablishments = await _apiController.EstablishmentGetByOwner().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading owned establishments");
        }
    }
}
