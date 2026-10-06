using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.WildRp;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private EstablishmentListResponseDto? _annuaireResults;
    private List<EstablishmentDto>? _annuaireOwned;
    private bool _annuaireLoading;
    private string _annuaireSearch = string.Empty;
    private int _annuaireCategory = -1;
    private readonly string[] _annuaireLocalSearch = new string[5] { "", "", "", "", "" };
    private readonly int[] _annuaireLocalCategory = new int[5] { -1, -1, -1, -1, -1 };
    private int _annuairePage;
    private int _annuaireTab;
    private bool _annuaireNeedsRefresh = true;
    private sealed record UpcomingOccurrence(EstablishmentDto Establishment, EstablishmentEventDto Event, DateTime StartUtc, DateTime EndUtc);

    private List<UpcomingOccurrence>? _annuaireUpcoming;
    private bool _annuaireUpcomingLoading;
    private readonly Dictionary<Guid, IDalamudTextureWrap?> _annuaireLogoCache = new();
    private readonly Dictionary<Guid, Task<IDalamudTextureWrap>> _annuaireLogoTasks = new();
    private List<EstablishmentDto>? _annuaireBookmarkResults;
    private bool _annuaireBookmarksLoading;
    private WildRpAnnouncementDto? _annuaireWildRpOwn;
    private WildRpListResponseDto _annuaireWildRpResults = new();
    private bool _annuaireWildRpLoading;
    private string _annuaireWildRpMessage = string.Empty;
    private bool _annuaireWildRpFilterWorld;
    private int _annuaireWildRpPage;
    private List<RpProfileSummaryDto>? _annuaireWildRpProfiles;
    private readonly HashSet<int> _wildRpExpiryNotified = [];
    private Guid? _wildRpExpiryTrackingId;

    private static string[] AnnuaireCategoryNames =>
    [
        Loc.Get("Establishment.Category.Tavern"), Loc.Get("Establishment.Category.Shop"),
        Loc.Get("Establishment.Category.Temple"), Loc.Get("Establishment.Category.Academy"),
        Loc.Get("Establishment.Category.Guild"), Loc.Get("Establishment.Category.Residence"),
        Loc.Get("Establishment.Category.Workshop"), Loc.Get("Establishment.Category.Other")
    ];

    private static readonly FontAwesomeIcon[] AnnuaireCategoryIcons =
    [
        FontAwesomeIcon.Beer, FontAwesomeIcon.ShoppingBag, FontAwesomeIcon.Church, FontAwesomeIcon.GraduationCap,
        FontAwesomeIcon.Shield, FontAwesomeIcon.Home, FontAwesomeIcon.Hammer, FontAwesomeIcon.EllipsisH
    ];

    private static readonly string[] _dayNames = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];

    private void DrawAnnuaireSection(int tab, bool entering)
    {
        _annuaireTab = tab;
        if (entering) RefreshAnnuaireTab(tab);

        // Auto-refresh on first display
        if (_annuaireNeedsRefresh)
        {
            _annuaireNeedsRefresh = false;
            _ = AnnuaireRefreshOwned();
        }

        if (tab is 1 or 2 or 3)
            DrawAnnuaireToolbar(tab);
        else if (tab == 0)
            DrawAnnuaireRegisterButton();

        ImGuiHelpers.ScaledDummy(4f);

        switch (_annuaireTab)
        {
            case 0:
                DrawAnnuaireOwned();
                break;
            case 1:
                DrawAnnuaireBookmarks();
                break;
            case 2:
                DrawAnnuaireBrowse();
                break;
            case 3:
                DrawAnnuaireUpcoming();
                break;
            case 4:
                DrawAnnuaireWildRp();
                break;
        }
    }

    private void DrawAnnuaireToolbar(int tab)
    {
        bool serverSide = tab == 2;
        var scale = ImGuiHelpers.GlobalScale;
        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        var iconButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Search).X;
        int buttons = serverSide ? 2 : 1;
        // Les boutons ne doivent jamais sortir de la fenêtre : on répartit ce qui reste entre la
        // catégorie (au plus 130 px) et la recherche, qui prend le surplus.
        var remaining = MathF.Max(60f * scale, ImGui.GetContentRegionAvail().X - iconButtonWidth * buttons - itemSpacing * (buttons + 1));
        var categoryWidth = Math.Clamp(remaining * 0.38f, 56f * scale, 130f * scale);
        var searchWidth = MathF.Max(36f * scale, remaining - categoryWidth);

        ref string search = ref (serverSide ? ref _annuaireSearch : ref _annuaireLocalSearch[tab]);
        ref int category = ref (serverSide ? ref _annuaireCategory : ref _annuaireLocalCategory[tab]);

        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint($"##annSearch{tab}", "Rechercher...", ref search, 100, ImGuiInputTextFlags.EnterReturnsTrue) && serverSide)
        {
            _annuairePage = 0;
            _ = AnnuaireRefreshList();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(categoryWidth);
        var catPreview = category >= 0 && category < AnnuaireCategoryNames.Length
            ? AnnuaireCategoryNames[category] : "Toutes";
        using (var combo = ImRaii.Combo($"##annCat{tab}", catPreview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Toutes", category == -1))
                {
                    category = -1;
                    if (serverSide) { _annuairePage = 0; _ = AnnuaireRefreshList(); }
                }
                for (int i = 0; i < AnnuaireCategoryNames.Length; i++)
                {
                    if (ImGui.Selectable(AnnuaireCategoryNames[i], category == i))
                    {
                        category = i;
                        if (serverSide) { _annuairePage = 0; _ = AnnuaireRefreshList(); }
                    }
                }
            }
        }

        if (serverSide)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Search))
            {
                _annuairePage = 0;
                _ = AnnuaireRefreshList();
            }
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Search"));
        }

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
        {
            if (serverSide) AnnuaireRefreshAll();
            else RefreshAnnuaireTab(tab);
        }
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Refresh"));
    }

    private static bool MatchesAnnuaireFilter(EstablishmentDto establishment, string? eventTitle, string search, int category)
    {
        if (category >= 0 && establishment.Category != category) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        return establishment.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || (eventTitle != null && eventTitle.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    // L'enregistrement d'un établissement n'a de sens que dans « Mes lieux ».
    private void DrawAnnuaireRegisterButton()
    {
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("Establishment.Directory.Register")))
            Mediator.Publish(new UiToggleMessage(typeof(EstablishmentRegistrationUi)));
    }

    private void RefreshAnnuaireTab(int tab)
    {
        switch (tab)
        {
            case 0: _ = AnnuaireRefreshOwned(); break;
            case 1: _ = AnnuaireRefreshBookmarks(); break;
            case 2: _ = AnnuaireRefreshList(); break;
            case 3: _ = AnnuaireRefreshUpcoming(); break;
            case 4: _ = AnnuaireRefreshWildRp(); break;
        }
    }

    private void DrawAnnuaireBrowse()
    {
        if (_annuaireLoading)
        {
            ImGui.TextDisabled("Chargement...");
            return;
        }

        if (_annuaireResults == null)
        {
            ImGui.TextDisabled("Appuyez sur Rechercher pour charger l'annuaire.");
            return;
        }

        if (_annuaireResults.Establishments.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoResults"));
            return;
        }

        using (var scroll = ImRaii.Child("##annBrowseScroll", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 4)))
        {
            if (scroll)
            {
                // Server-side ordering is applied in EstablishmentList (alphabetical by Name)
                foreach (var e in _annuaireResults.Establishments)
                    DrawAnnuaireCard(e);
            }
        }

        DrawAnnuairePagination();
    }

    private void DrawAnnuaireBookmarks()
    {
        var bookmarks = _establishmentConfigService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0)
        {
            if (_uiSharedService.DrawEmptyState(FontAwesomeIcon.Star,
                    Loc.Get("EmptyState.Favorites.Title"), Loc.Get("EmptyState.Favorites.Hint"),
                    Loc.Get("EmptyState.Favorites.Button")))
                _socialSubSection = SocialSubSection.DirectoryBrowse;
            return;
        }

        if (!_annuaireBookmarksLoading && _annuaireBookmarkResults == null)
            _ = AnnuaireRefreshBookmarks();

        if (_annuaireBookmarksLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.Loading"));
            return;
        }

        if (_annuaireBookmarkResults != null)
        {
            var shown = _annuaireBookmarkResults
                .Where(x => MatchesAnnuaireFilter(x, null, _annuaireLocalSearch[1], _annuaireLocalCategory[1]))
                .OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase)
                .ToList();
            if (shown.Count == 0)
            {
                ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoResults"));
                return;
            }

            using var scroll = ImRaii.Child("##annBookmarksScroll", new Vector2(0, 0));
            if (scroll)
            {
                foreach (var establishment in shown)
                    DrawAnnuaireCard(establishment);
            }
        }
    }

    private async Task AnnuaireRefreshBookmarks()
    {
        if (_annuaireBookmarksLoading) return;
        _annuaireBookmarksLoading = true;
        try
        {
            var bookmarks = _establishmentConfigService.Current.BookmarkedEstablishments;
            var results = new List<EstablishmentDto>();
            foreach (var id in bookmarks.ToList())
            {
                var estab = await _apiController.EstablishmentGetById(id).ConfigureAwait(false);
                if (estab != null)
                    results.Add(estab);
                else
                {
                    bookmarks.Remove(id);
                    _establishmentConfigService.Save();
                }
            }
            _annuaireBookmarkResults = results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading bookmarked establishments");
        }
        finally
        {
            _annuaireBookmarksLoading = false;
        }
    }

    private void DrawAnnuaireOwned()
    {
        if (_annuaireOwned == null)
        {
            ImGui.TextDisabled("Chargement...");
            return;
        }

        if (_annuaireOwned.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.Home,
                Loc.Get("EmptyState.Owned.Title"), Loc.Get("EmptyState.Owned.Hint"));
            return;
        }

        using var scroll = ImRaii.Child("##annOwnedScroll", new Vector2(0, 0));
        if (scroll)
        {
            foreach (var e in _annuaireOwned.OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase))
                DrawAnnuaireCard(e);
        }
    }

    private void DrawAnnuaireCard(EstablishmentDto establishment)
    {
        ImGui.PushID(establishment.Id.ToString());

        var isBookmarked = _establishmentConfigService.Current.BookmarkedEstablishments.Contains(establishment.Id);
        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < AnnuaireCategoryIcons.Length ? AnnuaireCategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;
        var catName = catIndex >= 0 && catIndex < AnnuaireCategoryNames.Length ? AnnuaireCategoryNames[catIndex] : "?";

        var scale = ImGuiHelpers.GlobalScale;
        var logoSize = 56f * scale;
        var logoRounding = 6f * scale;
        var logoSpacing = 10f * scale;
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var cardHeight = 80f * scale;

        // Hover detection on the card area for a subtle highlight
        var cardStartScreen = ImGui.GetCursorScreenPos();
        var cardRectMax = cardStartScreen + new Vector2(cardWidth, cardHeight);
        var hovered = ImGui.IsMouseHoveringRect(cardStartScreen, cardRectMax);
        var bgColor = UiSharedService.AccentColor with { W = hovered ? 0.24f : 0.13f };
        using var pushBg = ImRaii.PushColor(ImGuiCol.ChildBg, bgColor);

        using (var card = ImRaii.Child($"##annCard_{establishment.Id}", new Vector2(cardWidth, cardHeight), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                var logoTex = GetAnnuaireLogo(establishment.Id, establishment.LogoImageBase64);

                var lineH = ImGui.GetTextLineHeight();
                var bigLineH = lineH * 1.45f;
                var spacingY = ImGui.GetStyle().ItemSpacing.Y;
                var contentH = bigLineH + spacingY + lineH;
                var innerH = ImGui.GetWindowHeight();
                var contentY = MathF.Max(0f, (innerH - contentH) / 2f);

                var cardInner = ImGui.GetCursorPos();
                ImGui.SetCursorPos(Vector2.Zero);
                bool cardClicked = ImGui.InvisibleButton("##openCard", new Vector2(cardWidth, cardHeight));
                ImGui.SetItemAllowOverlap();
                if (cardClicked)
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetCursorPos(cardInner);

                // Draw logo or placeholder (vertically centered against inner height)
                var logoScreenPos = ImGui.GetCursorScreenPos();
                var logoTopLeft = new Vector2(logoScreenPos.X, ImGui.GetWindowPos().Y + MathF.Max(0f, (innerH - logoSize) / 2f));
                if (logoTex != null)
                {
                    var logoDraw = ImGui.GetWindowDrawList();
                    logoDraw.AddImageRounded(logoTex.Handle, logoTopLeft,
                        logoTopLeft + new Vector2(logoSize, logoSize),
                        Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), logoRounding);
                    logoDraw.AddRect(logoTopLeft, logoTopLeft + new Vector2(logoSize, logoSize),
                        ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.45f }), logoRounding, ImDrawFlags.None, scale);
                }
                else
                {
                    UiSharedService.DrawLogoPlaceholder(logoTopLeft, logoSize, logoRounding, catIcon);
                }

                var starSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Star);
                var eyeSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Eye);
                var buttonsWidth = starSize.X + eyeSize.X + ImGui.GetStyle().ItemSpacing.X * 2;

                ImGui.SetCursorPos(new Vector2(logoSize + logoSpacing, contentY));
                var titleMax = MathF.Max(60f * scale, ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX() - buttonsWidth - ImGui.GetStyle().ItemSpacing.X * 2);
                string title = establishment.Name;
                using (_uiSharedService.UidFont.Push())
                    title = UiSharedService.TruncateToWidth(UiSharedService.SanitizeOneLine(establishment.Name), titleMax);
                _uiSharedService.BigText(title);
                if (!string.Equals(title, UiSharedService.SanitizeOneLine(establishment.Name), StringComparison.Ordinal))
                    UiSharedService.AttachToolTip(establishment.Name);
                var availX = ImGui.GetContentRegionAvail().X;
                if (availX > buttonsWidth)
                    ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttonsWidth);

                if (_uiSharedService.IconButton(isBookmarked ? FontAwesomeIcon.Star : FontAwesomeIcon.StarHalfAlt))
                {
                    if (isBookmarked)
                        _establishmentConfigService.Current.BookmarkedEstablishments.Remove(establishment.Id);
                    else
                        _establishmentConfigService.Current.BookmarkedEstablishments.Add(establishment.Id);
                    _establishmentConfigService.Save();
                }
                UiSharedService.AttachToolTip(isBookmarked
                    ? Loc.Get("Establishment.Directory.RemoveFavorite")
                    : Loc.Get("Establishment.Directory.AddFavorite"));

                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));

                // Row 2: pill + single-line description
                ImGui.SetCursorPosX(logoSize + logoSpacing);
                UiSharedService.DrawCategoryPill(catName);

                var desc = UiSharedService.SanitizeOneLine(establishment.Description);
                if (string.IsNullOrEmpty(desc))
                    desc = $"par {establishment.OwnerAlias ?? establishment.OwnerUID}";

                var descMaxX = ImGui.GetWindowContentRegionMax().X - 4f * scale;
                var descAvail = MathF.Max(40f, descMaxX - ImGui.GetCursorPosX());
                ImGui.TextDisabled(UiSharedService.TruncateToWidth(desc, descAvail));
            }
        }

        ImGui.PopID();
    }

    private void DrawAnnuairePagination()
    {
        if (_annuaireResults == null) return;
        var totalPages = Math.Max(1, (_annuaireResults.TotalCount + _annuaireResults.PageSize - 1) / Math.Max(_annuaireResults.PageSize, 1));

        if (_annuairePage > 0 && _uiSharedService.IconButton(FontAwesomeIcon.ChevronLeft))
        {
            _annuairePage--;
            _ = AnnuaireRefreshList();
        }

        ImGui.SameLine();
        ImGui.Text($"Page {_annuairePage + 1}/{totalPages} ({_annuaireResults.TotalCount})");

        if (_annuairePage < totalPages - 1)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.ChevronRight))
            {
                _annuairePage++;
                _ = AnnuaireRefreshList();
            }
        }
    }

    private async Task AnnuaireRefreshList()
    {
        if (_annuaireLoading) return;
        _annuaireLoading = true;
        try
        {
            var request = new EstablishmentListRequestDto
            {
                SearchText = string.IsNullOrWhiteSpace(_annuaireSearch) ? null : _annuaireSearch,
                Category = _annuaireCategory >= 0 ? _annuaireCategory : null,
                Page = _annuairePage,
                PageSize = 20
            };
            _annuaireResults = await _apiController.EstablishmentList(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing annuaire list");
        }
        finally
        {
            _annuaireLoading = false;
        }
    }

    private async Task AnnuaireRefreshOwned()
    {
        try
        {
            _annuaireOwned = await _apiController.EstablishmentGetByOwner().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading owned establishments");
        }
    }

    private IDalamudTextureWrap? GetAnnuaireLogo(Guid id, string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (_annuaireLogoCache.TryGetValue(id, out var cached)) return cached;

        if (!_annuaireLogoTasks.TryGetValue(id, out var task))
        {
            _annuaireLogoTasks[id] = Task.Run(() => _uiSharedService.LoadImageAsync(Convert.FromBase64String(base64)));
            return null;
        }

        if (!task.IsCompleted) return null;

        _annuaireLogoTasks.Remove(id);
        if (task.IsCompletedSuccessfully)
        {
            _annuaireLogoCache[id] = task.Result;
            return task.Result;
        }

        _logger.LogDebug(task.Exception?.GetBaseException(), "Logo d'établissement {id} illisible", id);
        _annuaireLogoCache[id] = null;
        return null;
    }

    private void AnnuaireRefreshAll()
    {
        _annuairePage = 0;
        foreach (var tex in _annuaireLogoCache.Values)
            tex?.Dispose();
        _annuaireLogoCache.Clear();
        foreach (var pending in _annuaireLogoTasks.Values)
        {
            _ = pending.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
            }, TaskScheduler.Default);
        }
        _annuaireLogoTasks.Clear();
        _annuaireBookmarkResults = null;
        _ = AnnuaireRefreshList();
        _ = AnnuaireRefreshOwned();
        _ = AnnuaireRefreshBookmarks();
        _ = AnnuaireRefreshUpcoming();
    }

    private async Task AnnuaireRefreshUpcoming()
    {
        if (_annuaireUpcomingLoading) return;
        _annuaireUpcomingLoading = true;
        try
        {
            var request = new EstablishmentListRequestDto { Page = 0, PageSize = 100 };
            var result = await _apiController.EstablishmentList(request).ConfigureAwait(false);
            if (result != null)
            {
                var now = DateTime.Now;
                var weekEnd = now.Date.AddDays(7);
                var upcoming = new List<UpcomingOccurrence>();

                foreach (var estab in result.Establishments)
                {
                    foreach (var evt in estab.Events)
                    {
                        var occurrence = EstablishmentReminderService.ComputeCurrentOrNextOccurrence(evt, DateTime.UtcNow);
                        if (occurrence is not { } occ) continue;

                        var localTime = occ.Start.ToLocalTime();
                        if (localTime >= now.AddHours(-1) && localTime.Date < weekEnd)
                            upcoming.Add(new UpcomingOccurrence(estab, evt, occ.Start, occ.End));
                    }
                }

                _annuaireUpcoming = upcoming.OrderBy(o => o.StartUtc).ToList();
                _logger.LogDebug("Found {count} upcoming events", _annuaireUpcoming.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading upcoming events");
        }
        finally
        {
            _annuaireUpcomingLoading = false;
        }
    }

    private void DrawAnnuaireUpcoming()
    {
        if (_annuaireUpcomingLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.UpcomingLoading"));
            return;
        }

        if (_annuaireUpcoming == null || _annuaireUpcoming.Count == 0)
        {
            _uiSharedService.DrawEmptyState(FontAwesomeIcon.CalendarAlt,
                Loc.Get("EmptyState.Upcoming.Title"), Loc.Get("EmptyState.Upcoming.Hint"));
            return;
        }

        var now = DateTime.Now;
        var today = now.Date;

        var upcomingSearch = _annuaireLocalSearch[3];
        var upcomingCategory = _annuaireLocalCategory[3];
        var filteredUpcoming = _annuaireUpcoming
            .Where(e => MatchesAnnuaireFilter(e.Establishment, e.Event.Title, upcomingSearch, upcomingCategory))
            .ToList();

        var tonightEvents = filteredUpcoming
            .Where(e => e.StartUtc.ToLocalTime().Date == today && e.StartUtc.ToLocalTime() > now.AddHours(-1))
            .OrderBy(e => e.StartUtc)
            .ToList();

        var weekEvents = filteredUpcoming
            .Where(e => e.StartUtc.ToLocalTime().Date > today)
            .OrderBy(e => e.StartUtc)
            .ToList();

        if (tonightEvents.Count == 0 && weekEvents.Count == 0)
        {
            bool filtering = !string.IsNullOrWhiteSpace(upcomingSearch) || upcomingCategory >= 0;
            ImGui.TextDisabled(Loc.Get(filtering ? "Establishment.Directory.NoResults" : "Establishment.Directory.NoUpcoming"));
            return;
        }

        using var scroll = ImRaii.Child("##annUpcomingScroll", new Vector2(0, 0));
        if (!scroll) return;

        if (tonightEvents.Count > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Fire.ToIconString());
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Establishment.Directory.Tonight"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(2f);
            foreach (var occurrence in tonightEvents)
                DrawAnnuaireUpcomingCard(occurrence);
            ImGuiHelpers.ScaledDummy(6f);
        }

        if (weekEvents.Count > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.CalendarAlt.ToIconString());
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Establishment.Directory.ThisWeek"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(2f);
            foreach (var occurrence in weekEvents)
                DrawAnnuaireUpcomingCard(occurrence);
        }
    }

    #region Wild RP

    private void DrawAnnuaireWildRp()
    {
        DrawAnnuaireWildRpAnnounce();
        ImGui.Separator();
        DrawAnnuaireWildRpList();
    }

    private void DrawAnnuaireWildRpAnnounce()
    {
        ImGui.Spacing();

        if (_annuaireWildRpOwn != null)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Compass.ToIconString());
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("WildRp.YourAnnouncement"), UiSharedService.AccentColor);

            var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)_annuaireWildRpOwn.WorldId, out string? wn) ? wn : _annuaireWildRpOwn.WorldId.ToString(CultureInfo.InvariantCulture);
            var territoryName = _dalamudUtilService.TerritoryData.Value.TryGetValue(_annuaireWildRpOwn.TerritoryId, out string? tn) ? tn : _annuaireWildRpOwn.TerritoryId.ToString(CultureInfo.InvariantCulture);
            var wardSuffix = _annuaireWildRpOwn.WardId is > 0 ? $" - {string.Format(CultureInfo.CurrentCulture, Loc.Get("WildRp.Ward"), _annuaireWildRpOwn.WardId)}" : string.Empty;

            ImGui.TextUnformatted($"{territoryName}{wardSuffix} | {worldName}");

            if (!string.IsNullOrWhiteSpace(_annuaireWildRpOwn.Message))
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"\"{_annuaireWildRpOwn.Message}\"");

            var remaining = _annuaireWildRpOwn.ExpiresAtUtc - DateTime.UtcNow;
            if (remaining.TotalMinutes > 0)
            {
                ImGui.TextDisabled(string.Format(CultureInfo.CurrentCulture, Loc.Get("WildRp.ExpiresIn"),
                    remaining.TotalMinutes >= 60
                        ? $"{(int)remaining.TotalHours}h{remaining.Minutes:D2}"
                        : $"{(int)remaining.TotalMinutes} min"));
            }

            ImGui.Spacing();
            if (ImGui.Button(Loc.Get("WildRp.Withdraw")))
                _ = AnnuaireWildRpWithdraw();
        }
        else
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Compass).X - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##wildRpMsg", Loc.Get("WildRp.MessageHint"), ref _annuaireWildRpMessage, 200);
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Compass))
                _ = AnnuaireWildRpAnnounce();
            UiSharedService.AttachToolTip(Loc.Get("WildRp.Announce"));
        }

        ImGui.Spacing();
    }

    private void DrawAnnuaireWildRpList()
    {
        if (ToggleSwitch.Draw(Loc.Get("WildRp.FilterWorld"), ref _annuaireWildRpFilterWorld))
        {
            _annuaireWildRpPage = 0;
            _ = AnnuaireRefreshWildRpList();
        }

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
        {
            _annuaireWildRpPage = 0;
            _ = AnnuaireRefreshWildRpList();
        }
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Refresh"));

        if (_annuaireWildRpLoading)
        {
            ImGui.TextDisabled(Loc.Get("WildRp.Loading"));
            return;
        }

        if (_annuaireWildRpResults.Announcements.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("WildRp.NoResults"));
            return;
        }

        using (var scroll = ImRaii.Child("##annWildRpScroll", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 4)))
        {
            if (scroll)
            {
                foreach (var announcement in _annuaireWildRpResults.Announcements)
                    DrawAnnuaireWildRpCard(announcement);
            }
        }

        if (_annuaireWildRpResults.Announcements.Count == 0) return;
        var totalPages = Math.Max(1, (_annuaireWildRpResults.TotalCount + _annuaireWildRpResults.PageSize - 1) / Math.Max(_annuaireWildRpResults.PageSize, 1));

        if (_annuaireWildRpPage > 0 && _uiSharedService.IconButton(FontAwesomeIcon.ChevronLeft))
        {
            _annuaireWildRpPage--;
            _ = AnnuaireRefreshWildRpList();
        }

        ImGui.SameLine();
        ImGui.Text($"Page {_annuaireWildRpPage + 1}/{totalPages} ({_annuaireWildRpResults.TotalCount})");

        if (_annuaireWildRpPage < totalPages - 1)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.ChevronRight))
            {
                _annuaireWildRpPage++;
                _ = AnnuaireRefreshWildRpList();
            }
        }
    }

    private void DrawAnnuaireWildRpCard(WildRpAnnouncementDto announcement)
        => Components.WildRpAnnouncementCard.Draw(announcement, _uiSharedService, _dalamudUtilService);


    private async Task AnnuaireRefreshWildRp()
    {
        try
        {
            _annuaireWildRpOwn = await _apiController.WildRpGetOwn().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading own wild RP announcement");
        }

        try
        {
            _annuaireWildRpProfiles = await _apiController.EstablishmentGetOwnRpProfiles().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading RP profiles for wild RP");
        }

        await AnnuaireRefreshWildRpList().ConfigureAwait(false);
    }

    private async Task AnnuaireRefreshWildRpList()
    {
        if (_annuaireWildRpLoading) return;
        _annuaireWildRpLoading = true;
        try
        {
            uint? worldFilter = null;
            if (_annuaireWildRpFilterWorld)
                worldFilter = await _dalamudUtilService.GetWorldIdAsync().ConfigureAwait(false);

            _annuaireWildRpResults = await _apiController.WildRpList(new WildRpListRequestDto
            {
                WorldId = worldFilter,
                Page = _annuaireWildRpPage,
                PageSize = 20
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing wild RP list");
        }
        finally
        {
            _annuaireWildRpLoading = false;
        }
    }

    private async Task AnnuaireWildRpAnnounce()
    {
        try
        {
            var mapData = await _dalamudUtilService.GetMapDataAsync().ConfigureAwait(false);
            var playerName = _uiSharedService.PlayerName;
            var matchingProfile = _annuaireWildRpProfiles?.FirstOrDefault(p =>
                string.Equals(p.CharacterName, playerName, StringComparison.OrdinalIgnoreCase));

            _annuaireWildRpOwn = await _apiController.WildRpAnnounce(new WildRpAnnounceRequestDto
            {
                WorldId = mapData.ServerId,
                TerritoryId = mapData.TerritoryId,
                WardId = mapData.WardId > 0 ? mapData.WardId : null,
                CharacterName = playerName,
                Message = string.IsNullOrWhiteSpace(_annuaireWildRpMessage) ? null : _annuaireWildRpMessage.Trim(),
                RpProfileId = matchingProfile?.Id
            }).ConfigureAwait(false);

            _annuaireWildRpMessage = string.Empty;
            await AnnuaireRefreshWildRpList().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error announcing wild RP");
        }
    }

    private async Task AnnuaireWildRpWithdraw()
    {
        try
        {
            await _apiController.WildRpWithdraw().ConfigureAwait(false);
            _annuaireWildRpOwn = null;
            _wildRpExpiryNotified.Clear();
            _wildRpExpiryTrackingId = null;
            await AnnuaireRefreshWildRpList().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error withdrawing wild RP");
        }
    }

    private void CheckWildRpExpiry()
    {
        if (_annuaireWildRpOwn == null) return;

        if (_wildRpExpiryTrackingId != _annuaireWildRpOwn.Id)
        {
            _wildRpExpiryTrackingId = _annuaireWildRpOwn.Id;
            _wildRpExpiryNotified.Clear();
        }

        var remaining = _annuaireWildRpOwn.ExpiresAtUtc - DateTime.UtcNow;
        var totalMinutes = remaining.TotalMinutes;

        if (totalMinutes <= 0 && _wildRpExpiryNotified.Add(0))
        {
            Mediator.Publish(new DualNotificationMessage(
                Loc.Get("WildRp.Tab.Title"),
                Loc.Get("WildRp.Expired"),
                MareConfiguration.Models.NotificationType.Warning,
                TimeSpan.FromSeconds(8)));
            _annuaireWildRpOwn = null;
        }
        else if (totalMinutes <= 2 && totalMinutes > 0 && _wildRpExpiryNotified.Add(2))
        {
            Mediator.Publish(new DualNotificationMessage(
                Loc.Get("WildRp.Tab.Title"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("WildRp.ExpiringSoon"), 2),
                MareConfiguration.Models.NotificationType.Warning,
                TimeSpan.FromSeconds(5)));
        }
        else if (totalMinutes <= 5 && totalMinutes > 2 && _wildRpExpiryNotified.Add(5))
        {
            Mediator.Publish(new DualNotificationMessage(
                Loc.Get("WildRp.Tab.Title"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("WildRp.ExpiringSoon"), 5),
                MareConfiguration.Models.NotificationType.Info,
                TimeSpan.FromSeconds(5)));
        }
    }

    #endregion

    private void DrawAnnuaireUpcomingCard(UpcomingOccurrence occurrence)
    {
        var establishment = occurrence.Establishment;
        var evt = occurrence.Event;
        ImGui.PushID($"upcoming_{establishment.Id}_{evt.Id}");

        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < AnnuaireCategoryIcons.Length ? AnnuaireCategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;

        var scale = ImGuiHelpers.GlobalScale;
        var logoSize = 56f * scale;
        var logoRounding = 6f * scale;
        var logoSpacing = 10f * scale;
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var cardHeight = 76f * scale;

        var localTime = occurrence.StartUtc.ToLocalTime();
        var dayOfWeek = _dayNames[(int)localTime.DayOfWeek == 0 ? 6 : (int)localTime.DayOfWeek - 1];
        bool tonight = localTime.Date == DateTime.Now.Date;
        var timeStr = tonight ? $"{localTime:HH}h{localTime:mm}" : $"{dayOfWeek} {localTime:HH}h{localTime:mm}";

        var cardStartScreen = ImGui.GetCursorScreenPos();
        var hovered = ImGui.IsMouseHoveringRect(cardStartScreen, cardStartScreen + new Vector2(cardWidth, cardHeight));
        using var pushBg = ImRaii.PushColor(ImGuiCol.ChildBg, UiSharedService.AccentColor with { W = hovered ? 0.24f : 0.13f });

        using (var card = ImRaii.Child($"##upcCard_{evt.Id}", new Vector2(cardWidth, cardHeight), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                var innerH = ImGui.GetWindowHeight();
                var spacing = ImGui.GetStyle().ItemSpacing;

                // Toute la carte ouvre le détail ; le bouton œil, posé après, garde la priorité.
                var cardInner = ImGui.GetCursorPos();
                ImGui.SetCursorPos(Vector2.Zero);
                bool cardClicked = ImGui.InvisibleButton("##openUpcoming", new Vector2(cardWidth, cardHeight));
                ImGui.SetItemAllowOverlap();
                if (cardClicked)
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetCursorPos(cardInner);

                // Logo : même rendu que les cartes de l'annuaire (liseré fin, ou pictogramme de catégorie).
                var logoTex = GetAnnuaireLogo(establishment.Id, establishment.LogoImageBase64);
                var logoScreen = ImGui.GetCursorScreenPos();
                // Centré sur la hauteur de la carte : le curseur inclut déjà le retrait du haut, qu'il ne faut pas compter deux fois.
                var logoTopLeft = new Vector2(logoScreen.X, ImGui.GetWindowPos().Y + MathF.Max(0f, (innerH - logoSize) / 2f));
                if (logoTex != null)
                {
                    var dl = ImGui.GetWindowDrawList();
                    dl.AddImageRounded(logoTex.Handle, logoTopLeft, logoTopLeft + new Vector2(logoSize),
                        Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), logoRounding);
                    dl.AddRect(logoTopLeft, logoTopLeft + new Vector2(logoSize),
                        ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.45f }), logoRounding, ImDrawFlags.None, scale);
                }
                else
                {
                    UiSharedService.DrawLogoPlaceholder(logoTopLeft, logoSize, logoRounding, catIcon);
                }

                var textX = logoSize + logoSpacing;
                var contentMaxX = ImGui.GetWindowContentRegionMax().X - 4f * scale;
                var eyeSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Eye);
                var eyeX = contentMaxX - eyeSize.X;

                // Pastille d'horaire : plus marquée pour une soirée du jour.
                var clockIcon = FontAwesomeIcon.Clock.ToIconString();
                Vector2 clockSize;
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    clockSize = ImGui.CalcTextSize(clockIcon);
                var timeSize = ImGui.CalcTextSize(timeStr);
                var chipPadX = 8f * scale;
                var chipGap = 5f * scale;
                var chipWidth = chipPadX * 2 + clockSize.X + chipGap + timeSize.X;
                var chipHeight = ImGui.GetTextLineHeight() + 6f * scale;

                var lineH = ImGui.GetTextLineHeight();
                var contentH = lineH * 2 + spacing.Y;
                var contentY = MathF.Max(0f, (innerH - contentH) / 2f);
                var windowPos = ImGui.GetWindowPos();

                var chipLeft = eyeX - spacing.X - chipWidth;
                // Centrée sur la même ligne que le bouton œil, au milieu de la carte.
                var chipTop = (innerH - chipHeight) / 2f;
                var chipMin = windowPos + new Vector2(chipLeft, chipTop);
                var chipDl = ImGui.GetWindowDrawList();
                var chipColor = UiSharedService.AccentColor;
                chipDl.AddRectFilled(chipMin, chipMin + new Vector2(chipWidth, chipHeight),
                    ImGui.GetColorU32(chipColor with { W = tonight ? 0.55f : 0.28f }), chipHeight / 2f);
                chipDl.AddRect(chipMin, chipMin + new Vector2(chipWidth, chipHeight),
                    ImGui.GetColorU32(chipColor with { W = tonight ? 0.9f : 0.5f }), chipHeight / 2f, ImDrawFlags.None, scale);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    chipDl.AddText(chipMin + new Vector2(chipPadX, (chipHeight - clockSize.Y) / 2f), ImGui.GetColorU32(Vector4.One), clockIcon);
                chipDl.AddText(chipMin + new Vector2(chipPadX + clockSize.X + chipGap, (chipHeight - timeSize.Y) / 2f),
                    ImGui.GetColorU32(Vector4.One), timeStr);

                ImGui.SetCursorPos(new Vector2(textX, contentY));
                var nameMax = MathF.Max(40f * scale, chipLeft - textX - spacing.X);
                var name = UiSharedService.SanitizeOneLine(establishment.Name);
                var shownName = UiSharedService.TruncateToWidth(name, nameMax);
                UiSharedService.ColorText(shownName, UiSharedService.ThemeNavTextActive);
                if (!string.Equals(shownName, name, StringComparison.Ordinal))
                    UiSharedService.AttachToolTip(name);

                ImGui.SetCursorPos(new Vector2(textX, contentY + lineH + spacing.Y));
                var title = UiSharedService.SanitizeOneLine(evt.Title);
                var titleMax = MathF.Max(40f * scale, eyeX - textX - spacing.X);
                UiSharedService.ColorText(UiSharedService.TruncateToWidth(title, titleMax), new Vector4(1f, 0.9f, 0.6f, 1f));

                // Bouton œil, centré verticalement à droite.
                ImGui.SetCursorPos(new Vector2(eyeX, MathF.Max(0f, (innerH - eyeSize.Y) / 2f)));
                if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                    Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
                UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));
            }
        }

        ImGui.PopID();
    }
}
