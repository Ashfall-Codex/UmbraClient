using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public partial class CompactUi
{
    // Annuaire state
    private EstablishmentListResponseDto? _annuaireResults;
    private List<EstablishmentDto>? _annuaireOwned;
    private bool _annuaireLoading;
    private string _annuaireSearch = string.Empty;
    private int _annuaireCategory = -1;
    private int _annuairePage;
    private int _annuaireTab;
    private bool _annuaireNeedsRefresh = true;
    private List<(EstablishmentDto Establishment, EstablishmentEventDto Event)>? _annuaireUpcoming;
    private bool _annuaireUpcomingLoading;
    private readonly Dictionary<Guid, IDalamudTextureWrap?> _annuaireLogoCache = new();
    private List<EstablishmentDto>? _annuaireBookmarkResults;
    private bool _annuaireBookmarksLoading;

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

    private void DrawAnnuaireSection()
    {
        // Auto-refresh on first display
        if (_annuaireNeedsRefresh)
        {
            _annuaireNeedsRefresh = false;
            _ = AnnuaireRefreshOwned();
        }

        // Toolbar: search + category + buttons
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputTextWithHint("##annSearch", "Rechercher...", ref _annuaireSearch, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _annuairePage = 0;
            _ = AnnuaireRefreshList();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        var catPreview = _annuaireCategory >= 0 && _annuaireCategory < AnnuaireCategoryNames.Length
            ? AnnuaireCategoryNames[_annuaireCategory] : "Toutes";
        using (var combo = ImRaii.Combo("##annCat", catPreview))
        {
            if (combo)
            {
                if (ImGui.Selectable("Toutes", _annuaireCategory == -1))
                {
                    _annuaireCategory = -1;
                    _annuairePage = 0;
                    _ = AnnuaireRefreshList();
                }
                for (int i = 0; i < AnnuaireCategoryNames.Length; i++)
                {
                    if (ImGui.Selectable(AnnuaireCategoryNames[i], _annuaireCategory == i))
                    {
                        _annuaireCategory = i;
                        _annuairePage = 0;
                        _ = AnnuaireRefreshList();
                    }
                }
            }
        }

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Search))
        {
            _annuairePage = 0;
            _ = AnnuaireRefreshList();
        }
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Search"));

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Sync))
            AnnuaireRefreshAll();
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Refresh"));

        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            Mediator.Publish(new UiToggleMessage(typeof(EstablishmentRegistrationUi)));
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.Register"));

        ImGui.Spacing();

        // Sub-tabs as styled buttons (hub pattern)
        DrawAnnuaireTabButtons();
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
        }
    }

    private void DrawAnnuaireTabButtons()
    {
        var icons = new[] { FontAwesomeIcon.Home, FontAwesomeIcon.Star, FontAwesomeIcon.Globe, FontAwesomeIcon.CalendarAlt };
        var labels = new[] {
            Loc.Get("Establishment.Directory.Tab.Mine"),
            Loc.Get("Establishment.Directory.Tab.Favorites"),
            Loc.Get("Establishment.Directory.Tab.Browse"),
            Loc.Get("Establishment.Directory.Tab.Upcoming")
        };

        const float btnH = 28f;
        const float btnSpacing = 6f;
        const float rounding = 4f;
        const float iconTextGap = 4f;
        const float btnPadX = 8f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var accent = UiSharedService.AccentColor;

        // Measure natural widths
        var iconStrings = new string[labels.Length];
        var iconSizes = new Vector2[labels.Length];
        var labelSizes = new Vector2[labels.Length];
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

        bool iconOnly = totalNatural > availWidth;

        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            float btnW = iconOnly ? (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length : naturalWidths[i];

            var p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##annTab_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool isActive = _annuaireTab == i;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f)
                : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f)
                : new Vector4(0.7f, 0.65f, 0.8f, 1f);
            var textColorU32 = ImGui.GetColorU32(textColor);

            if (iconOnly)
            {
                var ix = p.X + (btnW - iconSizes[i].X) / 2f;
                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new Vector2(ix, p.Y + (btnH - iconSizes[i].Y) / 2f), textColorU32, iconStrings[i]);
                ImGui.PopFont();
                if (hovered) UiSharedService.AttachToolTip(labels[i]);
            }
            else
            {
                var contentW = iconSizes[i].X + iconTextGap + labelSizes[i].X;
                var startX = p.X + (btnW - contentW) / 2f;

                ImGui.PushFont(UiBuilder.IconFont);
                dl.AddText(new Vector2(startX, p.Y + (btnH - iconSizes[i].Y) / 2f), textColorU32, iconStrings[i]);
                ImGui.PopFont();

                dl.AddText(new Vector2(startX + iconSizes[i].X + iconTextGap, p.Y + (btnH - labelSizes[i].Y) / 2f), textColorU32, labels[i]);
            }

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked)
            {
                _annuaireTab = i;
                if (i == 0) _ = AnnuaireRefreshOwned();
                if (i == 1) _ = AnnuaireRefreshBookmarks();
                if (i == 2) _ = AnnuaireRefreshList();
                if (i == 3) _ = AnnuaireRefreshUpcoming();
            }
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

        foreach (var e in _annuaireResults.Establishments)
            DrawAnnuaireCard(e);

        DrawAnnuairePagination();
    }

    private void DrawAnnuaireBookmarks()
    {
        var bookmarks = _establishmentConfigService.Current.BookmarkedEstablishments;
        if (bookmarks.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoFavorites"));
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
            foreach (var establishment in _annuaireBookmarkResults)
                DrawAnnuaireCard(establishment);
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
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoOwned"));
            return;
        }

        foreach (var e in _annuaireOwned)
            DrawAnnuaireCard(e);
    }

    private void DrawAnnuaireCard(EstablishmentDto establishment)
    {
        ImGui.PushID(establishment.Id.ToString());

        var isBookmarked = _establishmentConfigService.Current.BookmarkedEstablishments.Contains(establishment.Id);
        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < AnnuaireCategoryIcons.Length ? AnnuaireCategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;
        var catName = catIndex >= 0 && catIndex < AnnuaireCategoryNames.Length ? AnnuaireCategoryNames[catIndex] : "?";

        var cardWidth = ImGui.GetContentRegionAvail().X;
        var cardHeight = 58f;
        using (var card = ImRaii.Child($"##annCard_{establishment.Id}", new Vector2(cardWidth, cardHeight), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (card)
            {
                // Logo thumbnail if available
                if (establishment.LogoImageBase64 is { Length: > 0 })
                {
                    if (!_annuaireLogoCache.TryGetValue(establishment.Id, out var logoTex))
                    {
                        try
                        {
                            logoTex = _uiSharedService.LoadImage(Convert.FromBase64String(establishment.LogoImageBase64));
                            _annuaireLogoCache[establishment.Id] = logoTex;
                        }
                        catch { /* ignore */ }
                    }
                    if (logoTex != null)
                    {
                        float logoSize = 24f;
                        float logoRounding = 4f;
                        var dl = ImGui.GetWindowDrawList();
                        var p = ImGui.GetCursorScreenPos();
                        var textH = ImGui.GetTextLineHeight();
                        var logoY = p.Y + (textH - logoSize) / 2f;
                        var logoMin = new Vector2(p.X, logoY);
                        dl.AddImageRounded(logoTex.Handle, logoMin, logoMin + new Vector2(logoSize, logoSize),
                            Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), logoRounding);
                        ImGui.Dummy(new Vector2(logoSize, textH));
                        ImGui.SameLine();
                    }
                    else
                    {
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                            ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());
                        ImGui.SameLine();
                    }
                }
                else
                {
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());
                    ImGui.SameLine();
                }

                _uiSharedService.BigText(establishment.Name);

                // Right-aligned buttons — reserve space before placing
                var starSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Star);
                var eyeSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Eye);
                var buttonsWidth = starSize.X + eyeSize.X + ImGui.GetStyle().ItemSpacing.X * 2;
                var availX = ImGui.GetContentRegionAvail().X;
                if (availX > buttonsWidth)
                {
                    ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - buttonsWidth);
                }

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

                // Row 2: [Category] + Description or owner
                ImGui.TextColored(UiSharedService.AccentColor, $"[{catName}]");
                ImGui.SameLine();
                if (!string.IsNullOrEmpty(establishment.Description))
                {
                    var desc = establishment.Description.Length > 70
                        ? establishment.Description[..70] + "..."
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

    private void AnnuaireRefreshAll()
    {
        _annuairePage = 0;
        foreach (var tex in _annuaireLogoCache.Values)
            tex?.Dispose();
        _annuaireLogoCache.Clear();
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

                _annuaireUpcoming = upcoming.OrderBy(e => e.Item2.StartsAtUtc).ToList();
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
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoUpcoming"));
            return;
        }

        var now = DateTime.Now;
        var today = now.Date;

        var tonightEvents = _annuaireUpcoming
            .Where(e => e.Event.StartsAtUtc.ToLocalTime().Date == today && e.Event.StartsAtUtc.ToLocalTime() > now.AddHours(-1))
            .OrderBy(e => e.Event.StartsAtUtc)
            .ToList();

        var weekEvents = _annuaireUpcoming
            .Where(e => e.Event.StartsAtUtc.ToLocalTime().Date > today)
            .OrderBy(e => e.Event.StartsAtUtc)
            .ToList();

        if (tonightEvents.Count == 0 && weekEvents.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.NoUpcoming"));
            return;
        }

        if (tonightEvents.Count > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.Fire.ToIconString());
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Establishment.Directory.Tonight"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(2f);
            foreach (var (estab, evt) in tonightEvents)
                DrawAnnuaireUpcomingCard(estab, evt);
            ImGuiHelpers.ScaledDummy(6f);
        }

        if (weekEvents.Count > 0)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.CalendarAlt.ToIconString());
            ImGui.SameLine();
            UiSharedService.ColorText(Loc.Get("Establishment.Directory.ThisWeek"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(2f);
            foreach (var (estab, evt) in weekEvents)
                DrawAnnuaireUpcomingCard(estab, evt);
        }
    }

    private void DrawAnnuaireUpcomingCard(EstablishmentDto establishment, EstablishmentEventDto evt)
    {
        ImGui.PushID($"upcoming_{establishment.Id}_{evt.Id}");

        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < AnnuaireCategoryIcons.Length ? AnnuaireCategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;

        UiSharedService.DrawCard($"upc_{evt.Id}", () =>
        {
            // Logo or category icon
            var hasUpcomingLogo = false;
            if (establishment.LogoImageBase64 is { Length: > 0 })
            {
                if (!_annuaireLogoCache.TryGetValue(establishment.Id, out var logoTex))
                {
                    try
                    {
                        logoTex = _uiSharedService.LoadImage(Convert.FromBase64String(establishment.LogoImageBase64));
                        _annuaireLogoCache[establishment.Id] = logoTex;
                    }
                    catch { /* ignore */ }
                }
                if (logoTex != null)
                {
                    float logoSize = 24f;
                    float logoRounding = 4f;
                    var dl = ImGui.GetWindowDrawList();
                    var p = ImGui.GetCursorScreenPos();
                    var textH = ImGui.GetTextLineHeight();
                    var logoY = p.Y + (textH - logoSize) / 2f;
                    var logoMin = new Vector2(p.X, logoY);
                    dl.AddImageRounded(logoTex.Handle, logoMin, logoMin + new Vector2(logoSize, logoSize),
                        Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), logoRounding);
                    ImGui.Dummy(new Vector2(logoSize, textH));
                    ImGui.SameLine();
                    hasUpcomingLogo = true;
                }
            }
            if (!hasUpcomingLogo)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(UiSharedService.AccentColor, catIcon.ToIconString());
                ImGui.SameLine();
            }
            ImGui.TextUnformatted(establishment.Name);

            var localTime = evt.StartsAtUtc.ToLocalTime();
            var dayOfWeek = _dayNames[(int)localTime.DayOfWeek == 0 ? 6 : (int)localTime.DayOfWeek - 1];
            var timeStr = localTime.Date == DateTime.Now.Date
                ? $"{localTime:HH}h{localTime:mm}"
                : $"{dayOfWeek} {localTime:HH}h{localTime:mm}";

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.Clock.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, timeStr);

            var eyeSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Eye);
            var availX = ImGui.GetContentRegionAvail().X;
            if (availX > eyeSize.X)
                ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - eyeSize.X - ImGui.GetStyle().ItemSpacing.X * 3);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Eye))
                Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Directory.ViewDetail"));

            UiSharedService.ColorText(evt.Title, new Vector4(1f, 0.9f, 0.6f, 1f));
        }, stretchWidth: true);

        ImGui.PopID();
    }
}
