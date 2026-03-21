using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.MareConfiguration;
using UmbraSync.Localization;
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

    // Upcoming events cache
    private List<(EstablishmentDto Establishment, EstablishmentEventDto Event)>? _upcomingEvents;
    private bool _upcomingLoading;

    private static string[] CategoryNames =>
    [
        Loc.Get("Establishment.Category.Tavern"), Loc.Get("Establishment.Category.Shop"),
        Loc.Get("Establishment.Category.Temple"), Loc.Get("Establishment.Category.Academy"),
        Loc.Get("Establishment.Category.Guild"), Loc.Get("Establishment.Category.Residence"),
        Loc.Get("Establishment.Category.Workshop"), Loc.Get("Establishment.Category.Other")
    ];

    private static readonly FontAwesomeIcon[] CategoryIcons =
    [
        FontAwesomeIcon.Beer, FontAwesomeIcon.ShoppingBag, FontAwesomeIcon.Church, FontAwesomeIcon.GraduationCap,
        FontAwesomeIcon.Shield, FontAwesomeIcon.Home, FontAwesomeIcon.Hammer, FontAwesomeIcon.EllipsisH
    ];

    private static readonly string[] DayNames = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];

    public EstablishmentDirectoryUi(ILogger<EstablishmentDirectoryUi> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService,
        UiSharedService uiSharedService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Annuaire des \u00e9tablissements###EstablishmentDirectory", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;

        SizeConstraints = new()
        {
            MinimumSize = new(620, 450),
            MaximumSize = new(900, 800)
        };

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            _ = RefreshList();
            _ = RefreshUpcoming();
        });
    }

    protected override void DrawInternal()
    {
        if (!_apiController.IsConnected)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Establishment.Registration.NotConnected"), ImGuiColors.DalamudRed);
            return;
        }

        DrawToolbar();
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("##estabTabs");
        if (!tabBar) return;

        using (var mineTab = ImRaii.TabItem(Loc.Get("Establishment.Directory.Tab.Mine")))
        {
            if (mineTab)
            {
                if (_activeTab != 2) { _activeTab = 2; _ = RefreshOwned(); }
                DrawOwnedTab();
            }
        }

        using (var bookmarkTab = ImRaii.TabItem(Loc.Get("Establishment.Directory.Tab.Favorites")))
        {
            if (bookmarkTab)
            {
                if (_activeTab != 1) { _activeTab = 1; _ = RefreshBookmarks(); }
                DrawBookmarksTab();
            }
        }

        using (var browseTab = ImRaii.TabItem(Loc.Get("Establishment.Directory.Tab.Browse")))
        {
            if (browseTab)
            {
                if (_activeTab != 0) { _activeTab = 0; _ = RefreshList(); }
                DrawBrowseTab();
            }
        }

        using (var upcomingTab = ImRaii.TabItem(Loc.Get("Establishment.Directory.Tab.Upcoming")))
        {
            if (upcomingTab)
            {
                if (_activeTab != 3)
                {
                    _activeTab = 3;
                    _ = RefreshUpcoming();
                }
                DrawUpcomingTab();
            }
        }
    }

    private void DrawToolbar()
    {
        // Search
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputTextWithHint("##search", Loc.Get("Establishment.Directory.SearchHint"), ref _searchText, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _currentPage = 0;
            _ = RefreshList();
        }

        // Category filter
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        var categoryPreview = _selectedCategory >= 0 && _selectedCategory < CategoryNames.Length
            ? CategoryNames[_selectedCategory] : Loc.Get("Establishment.Directory.AllCategories");
        using (var combo = ImRaii.Combo("##category", categoryPreview))
        {
            if (combo)
            {
                if (ImGui.Selectable(Loc.Get("Establishment.Directory.AllCategories"), _selectedCategory == -1))
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
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Search"));

        // Refresh button
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
        {
            _currentPage = 0;
            _ = RefreshList();
            _ = RefreshUpcoming();
        }
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Refresh"));

        // Create button
        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X;
        var btnSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - btnSize.X);
        if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            Mediator.Publish(new UiToggleMessage(typeof(EstablishmentRegistrationUi)));
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Register"));
    }

    private void DrawBrowseTab()
    {
        if (_isLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.Loading"));
            return;
        }

        if (_currentResults == null)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.PressSearch"));
            return;
        }

        if (_currentResults.Establishments.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoResults"));
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

    private void DrawUpcomingTab()
    {
        if (_upcomingLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.UpcomingLoading"));
            return;
        }

        if (_upcomingEvents == null || _upcomingEvents.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoUpcoming"));
            return;
        }

        var now = DateTime.Now;
        var today = now.Date;
        var weekEnd = today.AddDays(7);

        var tonightEvents = _upcomingEvents
            .Where(e => e.Event.StartsAtUtc.ToLocalTime().Date == today && e.Event.StartsAtUtc.ToLocalTime() > now.AddHours(-1))
            .OrderBy(e => e.Event.StartsAtUtc)
            .ToList();

        var weekEvents = _upcomingEvents
            .Where(e =>
            {
                var local = e.Event.StartsAtUtc.ToLocalTime();
                return local.Date > today && local.Date < weekEnd;
            })
            .OrderBy(e => e.Event.StartsAtUtc)
            .ToList();

        if (tonightEvents.Count == 0 && weekEvents.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoUpcoming"));
            return;
        }

        using var child = ImRaii.Child("##upcomingList", new Vector2(0, 0));
        if (!child) return;

        if (tonightEvents.Count > 0)
        {
            DrawUpcomingSectionHeader(FontAwesomeIcon.Fire, Loc.Get("Establishment.Directory.Tonight"));
            foreach (var (estab, evt) in tonightEvents)
                DrawUpcomingEventCard(estab, evt);
            ImGuiHelpers.ScaledDummy(6f);
        }

        if (weekEvents.Count > 0)
        {
            DrawUpcomingSectionHeader(FontAwesomeIcon.CalendarAlt, Loc.Get("Establishment.Directory.ThisWeek"));
            foreach (var (estab, evt) in weekEvents)
                DrawUpcomingEventCard(estab, evt);
        }
    }

    private static void DrawUpcomingSectionHeader(FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, icon.ToIconString());
        ImGui.SameLine();
        UiSharedService.ColorText(title, UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawUpcomingEventCard(EstablishmentDto establishment, EstablishmentEventDto evt)
    {
        ImGui.PushID($"upcoming_{establishment.Id}_{evt.Id}");

        var isBookmarked = _configService.Current.BookmarkedEstablishments.Contains(establishment.Id);
        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < CategoryIcons.Length ? CategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;

        UiSharedService.DrawCard($"upcoming_card_{evt.Id}", () =>
        {
            // Row 1: Category icon + Establishment name + Event time + buttons
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());

            ImGui.SameLine();
            ImGui.TextUnformatted(establishment.Name);

            // Format time
            var localTime = evt.StartsAtUtc.ToLocalTime();
            var dayOfWeek = DayNames[(int)localTime.DayOfWeek == 0 ? 6 : (int)localTime.DayOfWeek - 1];
            var timeStr = localTime.Date == DateTime.Now.Date
                ? $"{localTime:HH}h{localTime:mm}"
                : $"{dayOfWeek} {localTime:HH}h{localTime:mm}";

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.Clock.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, timeStr);

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
            UiSharedService.AttachToolTip(isBookmarked ? Loc.Get("Establishment.Directory.RemoveFavorite") : Loc.Get("Establishment.Directory.AddFavorite"));

            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));

            // Row 2: Event title
            UiSharedService.ColorText(evt.Title, new Vector4(1f, 0.9f, 0.6f, 1f));

            if (evt.EndsAtUtc.HasValue)
            {
                ImGui.SameLine();
                var endLocal = evt.EndsAtUtc.Value.ToLocalTime();
                ImGui.TextDisabled($"{Loc.Get("Establishment.Directory.Until")} {endLocal:HH}h{endLocal:mm}");
            }
        }, stretchWidth: true);

        ImGui.PopID();
    }

    private void DrawBookmarksTab()
    {
        var bookmarks = _configService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoFavorites"));
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
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
            {
                bookmarks.Remove(id);
                _configService.Save();
            }
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.RemoveFavorite"));
            ImGui.PopID();
        }
    }

    private void DrawOwnedTab()
    {
        if (_ownedEstablishments == null)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.Loading"));
            return;
        }

        if (_ownedEstablishments.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoOwned"));
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

                // Next event badge
                var nextEvent = establishment.Events
                    .Where(e => e.StartsAtUtc.ToLocalTime() > DateTime.Now.AddHours(-1))
                    .OrderBy(e => e.StartsAtUtc)
                    .FirstOrDefault();
                if (nextEvent != null)
                {
                    ImGui.SameLine();
                    var evtLocal = nextEvent.StartsAtUtc.ToLocalTime();
                    var dayOfWeek = DayNames[(int)evtLocal.DayOfWeek == 0 ? 6 : (int)evtLocal.DayOfWeek - 1];
                    var badge = evtLocal.Date == DateTime.Now.Date
                        ? $"{evtLocal:HH}h{evtLocal:mm}"
                        : $"{dayOfWeek} {evtLocal:HH}h{evtLocal:mm}";
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), FontAwesomeIcon.Calendar.ToIconString());
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), badge);
                }

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
                UiSharedService.AttachToolTip(isBookmarked ? Loc.Get("Establishment.Directory.RemoveFavorite") : Loc.Get("Establishment.Directory.AddFavorite"));

                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));

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
        ImGui.Text(string.Format(Loc.Get("Establishment.Directory.PageInfo"), _currentPage + 1, totalPages, _currentResults.TotalCount));

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
            _logger.LogDebug("Loaded {count} establishments (page {page})", _currentResults?.Establishments.Count ?? 0, _currentPage);
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

    private async Task RefreshUpcoming()
    {
        if (_upcomingLoading) return;
        _upcomingLoading = true;
        try
        {
            var request = new EstablishmentListRequestDto
            {
                Page = 0,
                PageSize = 100
            };
            var result = await _apiController.EstablishmentList(request).ConfigureAwait(false);
            if (result != null)
            {
                var now = DateTime.Now;
                var weekEnd = now.Date.AddDays(7);
                var upcoming = new List<(EstablishmentDto, EstablishmentEventDto)>();

                foreach (var estab in result.Establishments)
                {
                    foreach (var evt in estab.Events)
                    {
                        var localTime = evt.StartsAtUtc.ToLocalTime();
                        if (localTime >= now.AddHours(-1) && localTime.Date < weekEnd)
                            upcoming.Add((estab, evt));
                    }
                }

                _upcomingEvents = upcoming.OrderBy(e => e.Item2.StartsAtUtc).ToList();
                _logger.LogDebug("Found {count} upcoming events from {total} establishments", _upcomingEvents.Count, result.Establishments.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading upcoming events");
        }
        finally
        {
            _upcomingLoading = false;
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
