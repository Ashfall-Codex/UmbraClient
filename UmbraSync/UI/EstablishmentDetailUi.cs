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
using UmbraSync.API.Dto.User;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;

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
    private int? _editManagerRpProfileId;
    private bool _editShowManagerOnProfile = true;
    private int _editHousingType; // 0=Maison, 1=Appartement
    private bool _editLocationDirty;
    private int _editDistrictIndex;
    private int _editWard = 1;
    private int _editPlot = 1;
    private int _editRoomNumber = 1;
    private bool _editIsSubdivision;
    private ulong? _editServerId;

    private static readonly (string NameFr, string NameEn, uint TerritoryId)[] ResidentialDistricts =
    [
        ("Brumée", "Mist", 339),
        ("Lavandière", "The Lavender Beds", 340),
        ("La Coupe", "The Goblet", 341),
        ("Shirogane", "Shirogane", 641),
        ("Empyrée", "Empyreum", 979),
    ];
    private static readonly string[] DistrictNames = ResidentialDistricts.Select(d => d.NameFr).ToArray();
    private static string[] HousingTypeNames => [Loc.Get("Establishment.Location.House"), Loc.Get("Establishment.Location.Apartment")];

    private static int ResolveDistrictIndex(uint territoryId)
    {
        for (int i = 0; i < ResidentialDistricts.Length; i++)
            if (ResidentialDistricts[i].TerritoryId == territoryId) return i;
        return 0;
    }

    // Manager profile cache
    private IDalamudTextureWrap? _managerProfileTexture;
    private int? _lastManagerRpProfileId;

    // Own RP profiles for combo
    private List<RpProfileSummaryDto>? _ownRpProfiles;
    private bool _ownRpProfilesLoading;

    // Event creation
    private string _newEventTitle = string.Empty;
    private string _newEventDescription = string.Empty;
    private int _newEventDay = DateTime.Now.Day;
    private int _newEventMonth = DateTime.Now.Month;
    private int _newEventYear = DateTime.Now.Year;
    private int _newEventHour = 21;
    private int _newEventMinute;
    private bool _newEventHasEndTime;
    private int _newEventEndHour = 23;
    private int _newEventEndMinute;
    private int _newEventRecurrence; // 0=Unique, 1=Quotidien, 2=Hebdomadaire, 3=Mensuel, 4=Bihebdomadaire, 5=Bimestriel, 6=Trimestriel, 7=Annuel

    // SyncSlot binding
    private string _syncSlotGid = string.Empty;

    // Images (view + edit)
    private IDalamudTextureWrap? _logoTexture;
    private IDalamudTextureWrap? _bannerTexture;
    private DateTime _addressCopiedAt = DateTime.MinValue;
    private float? _fitHeight;
    private bool _fitCapped;
    private Vector2 _lastWindowSize;
    private byte[] _editLogoBytes = [];
    private byte[] _editBannerBytes = [];
    private IDalamudTextureWrap? _editLogoTexture;
    private IDalamudTextureWrap? _editBannerTexture;
    private string? _imageMessage;

    // Eligible groups cache (owned + has SyncSlot)
    private List<(string Gid, string Display)> _eligibleGroups = [];
    private bool _eligibleGroupsLoading;
    private bool _eligibleGroupsLoaded;

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

    public EstablishmentDetailUi(ILogger<EstablishmentDetailUi> logger, MareMediator mediator,
        ApiController apiController, EstablishmentConfigService configService,
        UiSharedService uiSharedService, PairManager pairManager,
        FileDialogManager fileDialogManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "D\u00e9tail \u00e9tablissement###EstablishmentDetail", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _fileDialogManager = fileDialogManager;

        SizeConstraints = new()
        {
            MinimumSize = new(520, 450),
            MaximumSize = new(800, 2000)
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

    // La hauteur suit le contenu : mesurée en fin de frame, appliquée au début de la suivante,
    // bornée par l'écran au-delà duquel la barre de défilement reprend le relais.
    // Tant que tout tient, la barre est coupée : sinon son apparition rétrécit le texte, qui
    // s'allonge, agrandit la fenêtre, fait disparaître la barre… et la fenêtre tremble.
    public override void PreDraw()
    {
        base.PreDraw();
        if (_fitHeight is not { } height) return;

        if (_fitCapped)
            Flags &= ~ImGuiWindowFlags.NoScrollbar;
        else
            Flags |= ImGuiWindowFlags.NoScrollbar;

        float delta = height - _lastWindowSize.Y;
        // Agrandir tout de suite, ne réduire qu'au-delà d'un seuil pour absorber les arrondis.
        if (delta > 1f || delta < -6f * ImGuiHelpers.GlobalScale)
            ImGui.SetNextWindowSize(new Vector2(_lastWindowSize.X, height));
    }

    protected override void DrawInternal()
    {
        DrawContent();

        var style = ImGui.GetStyle();
        float needed = ImGui.GetCursorPosY() + ImGui.GetScrollY() - style.ItemSpacing.Y + style.WindowPadding.Y;
        float minHeight = 450f * ImGuiHelpers.GlobalScale;
        float maxHeight = MathF.Max(minHeight, ImGui.GetMainViewport().WorkSize.Y * 0.85f);
        _lastWindowSize = ImGui.GetWindowSize();
        // Hystérésis : la barre retire de la largeur au texte, qui s'allonge d'autant.
        _fitCapped = _fitCapped
            ? needed > maxHeight - 24f * ImGuiHelpers.GlobalScale
            : needed > maxHeight;
        _fitHeight = Math.Clamp(needed + 2f * ImGuiHelpers.GlobalScale, minHeight, maxHeight);
    }

    private void DrawContent()
    {
        if (_isLoading)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Detail.Loading"));
            return;
        }

        if (_establishment == null)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("Establishment.Detail.NotFound"), ImGuiColors.DalamudRed);
            return;
        }

        var isOwner = string.Equals(_establishment.OwnerUID, _apiController.UID, StringComparison.Ordinal);

        DrawHeader(isOwner);
        ImGui.Separator();

        using var tabBar = ImRaii.TabBar("##detailTabs");
        if (!tabBar) return;

        using (var infoTab = ImRaii.TabItem(Loc.Get("Establishment.Detail.Tab.Info")))
        {
            if (infoTab)
            {
                if (_isEditing && isOwner)
                    DrawEditMode();
                else
                    DrawViewMode();
            }
        }

        using (var eventsTab = ImRaii.TabItem($"{Loc.Get("Establishment.Detail.Tab.Events")} ({_establishment.Events.Count})"))
        {
            if (eventsTab)
                DrawEventsTab(isOwner);
        }

        if (isOwner)
        {
            using (var imagesTab = ImRaii.TabItem(Loc.Get("Establishment.Detail.Tab.Images")))
            {
                if (imagesTab)
                    DrawImagesTab(isOwner);
            }

            using (var syncTab = ImRaii.TabItem(Loc.Get("Establishment.Detail.Tab.Sync")))
            {
                if (syncTab)
                    DrawSyncSlotTab();
            }
        }
    }

    private void DrawHeader(bool isOwner)
    {
        var establishment = _establishment!;
        var catIndex = establishment.Category;
        var catIcon = catIndex >= 0 && catIndex < CategoryIcons.Length ? CategoryIcons[catIndex] : FontAwesomeIcon.QuestionCircle;
        var catName = catIndex >= 0 && catIndex < CategoryNames.Length ? CategoryNames[catIndex] : "?";

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        float logoSize = 44f * scale;
        float logoRounding = UiSharedService.RadiusCard * scale;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        if (_logoTexture != null)
        {
            dl.AddImageRounded(_logoTexture.Handle, origin, origin + new Vector2(logoSize),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), logoRounding);
            dl.AddRect(origin, origin + new Vector2(logoSize),
                ImGui.GetColorU32(UiSharedService.ThemeCardBorder), logoRounding, ImDrawFlags.None, scale);
        }
        else
        {
            UiSharedService.DrawLogoPlaceholder(origin, logoSize, logoRounding, catIcon);
        }

        var buttonsWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Star).X;
        if (isOwner)
        {
            buttonsWidth += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Edit).X + style.ItemSpacing.X;
            buttonsWidth += _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Trash).X + style.ItemSpacing.X;
        }
        var buttonsX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X - buttonsWidth;

        ImGui.SetCursorScreenPos(new Vector2(origin.X + logoSize + 10f * scale, origin.Y));
        ImGui.BeginGroup();
        _uiSharedService.BigText(UiSharedService.SanitizeOneLine(establishment.Name));
        UiSharedService.DrawCategoryPill(catName);
        var owner = establishment.OwnerAlias ?? establishment.OwnerUID;
        ImGui.TextDisabled(string.Format(Loc.CurrentCulture, Loc.Get("Establishment.Detail.Owner"), owner,
            establishment.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Loc.CurrentCulture)));
        ImGui.EndGroup();
        var infoBottom = ImGui.GetItemRectMax().Y;

        ImGui.SetCursorScreenPos(new Vector2(buttonsX, origin.Y));
        var isBookmarked = _configService.Current.BookmarkedEstablishments.Contains(establishment.Id);
        if (_uiSharedService.IconButton(isBookmarked ? FontAwesomeIcon.Star : FontAwesomeIcon.StarHalfAlt))
        {
            if (isBookmarked)
                _configService.Current.BookmarkedEstablishments.Remove(establishment.Id);
            else
                _configService.Current.BookmarkedEstablishments.Add(establishment.Id);
            _configService.Save();
        }
        UiSharedService.AttachToolTip(isBookmarked ? Loc.Get("Establishment.Directory.RemoveFavorite") : Loc.Get("Establishment.Directory.AddFavorite"));

        if (isOwner)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(_isEditing ? FontAwesomeIcon.Times : FontAwesomeIcon.Edit))
            {
                if (!_isEditing) PopulateEditFields();
                _isEditing = !_isEditing;
            }
            UiSharedService.AttachToolTip(_isEditing ? Loc.Get("Establishment.Detail.CancelEdit") : Loc.Get("Establishment.Detail.Edit"));

            ImGui.SameLine();
            using var red = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
            if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                _ = DeleteEstablishment();
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Detail.Delete"));
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, MathF.Max(origin.Y + logoSize, infoBottom) + style.ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawViewMode()
    {
        var establishment = _establishment!;
        var scale = ImGuiHelpers.GlobalScale;
        ImGuiHelpers.ScaledDummy(2f);

        if (_bannerTexture != null)
        {
            float availWidth = ImGui.GetContentRegionAvail().X;
            float bannerHeight = availWidth * (260f / 840f);
            float bannerRounding = UiSharedService.RadiusCard * scale;
            var dl = ImGui.GetWindowDrawList();
            var bannerMin = ImGui.GetCursorScreenPos();
            var bannerMax = bannerMin + new Vector2(availWidth, bannerHeight);
            dl.AddImageRounded(_bannerTexture.Handle, bannerMin, bannerMax,
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), bannerRounding);
            dl.AddRect(bannerMin, bannerMax, ImGui.GetColorU32(UiSharedService.ThemeCardBorder), bannerRounding, ImDrawFlags.None, scale);
            ImGui.Dummy(new Vector2(availWidth, bannerHeight));
            ImGuiHelpers.ScaledDummy(4f);
        }

        DrawPracticalInfo(establishment);

        if (establishment.Tags.Length > 0)
        {
            ImGuiHelpers.ScaledDummy(2f);
            DrawTagChips(establishment.Tags);
        }

        if (establishment.ManagerRpProfileId.HasValue)
        {
            DrawSectionTitle(FontAwesomeIcon.UserTie, Loc.Get("Establishment.Detail.Manager"));
            DrawManagerSection();
        }

        if (!string.IsNullOrWhiteSpace(establishment.Description))
        {
            DrawSectionTitle(FontAwesomeIcon.BookOpen, Loc.Get("Establishment.Detail.Section.About"));
            var padding = new Vector2(12f, 10f) * scale;
            float wrapX = ImGui.GetWindowContentRegionMax().X - padding.X;
            UiSharedService.DrawCard("##description", () =>
            {
                using (ImRaii.TextWrapPos(wrapX))
                    ImGui.TextWrapped(establishment.Description);
            }, padding: padding, stretchWidth: true);
        }
    }

    private void DrawPracticalInfo(EstablishmentDto establishment)
    {
        var facts = new List<(FontAwesomeIcon Icon, string Label, string Value, bool CopyOnClick)>(5);

        var loc = establishment.Location;
        if (loc != null)
        {
            var address = (EstablishmentLocationType)loc.LocationType == EstablishmentLocationType.Housing
                ? EstablishmentLocationText.FormatHousing(loc, _uiSharedService.WorldData)
                : $"Zone — Position ({loc.X:F0}, {loc.Y:F0}, {loc.Z:F0}), Rayon {loc.Radius:F0}";
            if (!string.IsNullOrEmpty(address))
                facts.Add((FontAwesomeIcon.MapMarkerAlt, Loc.Get("Establishment.Detail.Fact.Address"), address, true));
        }

        if (!string.IsNullOrWhiteSpace(establishment.Schedule))
            facts.Add((FontAwesomeIcon.Clock, Loc.Get("Establishment.Field.Schedule"), establishment.Schedule, false));

        var nextEvent = FormatNextEvent(establishment);
        if (nextEvent != null)
            facts.Add((FontAwesomeIcon.CalendarAlt, Loc.Get("Establishment.Detail.Fact.NextEvent"), nextEvent, false));

        if (!string.IsNullOrWhiteSpace(establishment.FactionTag))
            facts.Add((FontAwesomeIcon.Flag, Loc.Get("Establishment.Detail.Fact.Faction"), establishment.FactionTag, false));

        if (establishment.Languages.Length > 0)
            facts.Add((FontAwesomeIcon.Language, Loc.Get("Establishment.Detail.Fact.Languages"), string.Join(" • ", establishment.Languages), false));

        if (facts.Count == 0) return;

        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f, 8f) * scale;
        float iconColumn = 22f * scale;
        float labelWidth = 0f;
        foreach (var fact in facts)
            labelWidth = MathF.Max(labelWidth, ImGui.CalcTextSize(fact.Label).X);
        float valueX = iconColumn + labelWidth + 14f * scale;
        float wrapX = ImGui.GetWindowContentRegionMax().X - padding.X;

        UiSharedService.DrawCard("##practicalInfo", () =>
        {
            foreach (var fact in facts)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(UiSharedService.AccentColor, fact.Icon.ToIconString());
                ImGui.SameLine(iconColumn);
                ImGui.TextColored(ImGuiColors.DalamudGrey, fact.Label);
                ImGui.SameLine(valueX);
                using (ImRaii.TextWrapPos(wrapX))
                    ImGui.TextWrapped(fact.Value);

                if (fact.CopyOnClick && ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    bool justCopied = (DateTime.UtcNow - _addressCopiedAt).TotalSeconds < 2;
                    UiSharedService.AttachToolTip(Loc.Get(justCopied ? "Establishment.Detail.AddressCopied" : "Establishment.Detail.CopyAddress"));
                    if (ImGui.IsItemClicked())
                    {
                        ImGui.SetClipboardText(fact.Value);
                        _addressCopiedAt = DateTime.UtcNow;
                    }
                }
            }
        }, padding: padding, stretchWidth: true);
    }

    private static string? FormatNextEvent(EstablishmentDto establishment)
    {
        var now = DateTime.UtcNow;
        EstablishmentEventDto? nextEvent = null;
        (DateTime Start, DateTime End) next = default;
        foreach (var evt in establishment.Events)
        {
            if (EstablishmentReminderService.ComputeCurrentOrNextOccurrence(evt, now) is not { } occurrence) continue;
            if (nextEvent == null || occurrence.Start < next.Start)
            {
                nextEvent = evt;
                next = occurrence;
            }
        }
        if (nextEvent == null) return null;

        var title = UiSharedService.SanitizeOneLine(nextEvent.Title);
        if (next.Start <= now)
            return $"{Loc.Get("Establishment.Detail.Fact.HappeningNow")} • {title}";

        var culture = Loc.CurrentCulture;
        var local = next.Start.ToLocalTime();
        var when = $"{local.ToString("dddd d MMMM", culture)}, {local.ToString("t", culture)}";
        when = string.Concat(when[..1].ToUpper(culture), when[1..]);
        return $"{when} • {title}";
    }

    private static void DrawSectionTitle(FontAwesomeIcon icon, string title)
    {
        ImGuiHelpers.ScaledDummy(6f);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, icon.ToIconString());
        ImGui.SameLine();
        UiSharedService.ColorText(title.ToUpper(Loc.CurrentCulture), UiSharedService.ThemeTextAccent);
        var lineStart = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(lineStart, lineStart + new Vector2(ImGui.GetContentRegionAvail().X, 0),
            ImGui.GetColorU32(UiSharedService.ThemeSeparator), ImGuiHelpers.GlobalScale);
        ImGuiHelpers.ScaledDummy(4f);
    }

    private static void DrawTagChips(IEnumerable<string> tags)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(7f, 2f) * scale;
        float rounding = UiSharedService.RadiusControl * scale;
        float maxX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var dl = ImGui.GetWindowDrawList();
        bool first = true;

        foreach (var raw in tags)
        {
            var tag = UiSharedService.SanitizeOneLine(raw);
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var size = ImGui.CalcTextSize(tag) + padding * 2;
            if (!first)
            {
                ImGui.SameLine(0, 4f * scale);
                if (ImGui.GetCursorScreenPos().X + size.X > maxX)
                    ImGui.NewLine();
            }
            first = false;

            var min = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(min, min + size, ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.18f }), rounding);
            dl.AddRect(min, min + size, ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.55f }), rounding, ImDrawFlags.None, scale);
            dl.AddText(min + padding, ImGui.GetColorU32(UiSharedService.ThemeNavTextHovered), tag);
            ImGui.Dummy(size);
        }
    }

    private void DrawManagerSection()
    {
        var establishment = _establishment!;
        var profileId = establishment.ManagerRpProfileId;
        if (profileId != _lastManagerRpProfileId)
        {
            _lastManagerRpProfileId = profileId;
            _managerProfileTexture?.Dispose();
            _managerProfileTexture = null;
            if (establishment.ManagerRpProfilePictureBase64 is { Length: > 0 } picB64)
            {
                try { _managerProfileTexture = _uiSharedService.LoadImage(Convert.FromBase64String(picB64)); }
                catch { /* image invalide : on garde le pictogramme */ }
            }
        }

        var rpName = $"{establishment.ManagerRpFirstName} {establishment.ManagerRpLastName}".Trim();
        var characterName = establishment.ManagerCharacterName;
        if (string.IsNullOrEmpty(rpName))
            rpName = characterName ?? establishment.OwnerAlias ?? establishment.OwnerUID;
        bool showCharacterName = !string.IsNullOrEmpty(characterName)
            && !string.Equals(characterName, rpName, StringComparison.Ordinal);

        // Le profil RP du gérant appartient au propriétaire : la fiche n'est consultable que s'il est en paire.
        var ownerPair = _pairManager.GetPairByUID(establishment.OwnerUID);
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f, 8f) * scale;
        UiSharedService.DrawCard("##manager", () =>
        {
            float picSize = 40f * scale;
            float rowTop = ImGui.GetCursorPosY();
            var picMin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            if (_managerProfileTexture != null)
            {
                dl.AddImageRounded(_managerProfileTexture.Handle, picMin, picMin + new Vector2(picSize),
                    Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), picSize / 2f);
                dl.AddCircle(picMin + new Vector2(picSize / 2f), picSize / 2f,
                    ImGui.GetColorU32(UiSharedService.ThemeCardBorder), 0, 1.5f * scale);
            }
            else
            {
                UiSharedService.DrawLogoPlaceholder(picMin, picSize, picSize / 2f, FontAwesomeIcon.User);
            }
            ImGui.Dummy(new Vector2(picSize));

            ImGui.SameLine(0, 10f * scale);
            ImGui.BeginGroup();
            float lineH = ImGui.GetTextLineHeight();
            float textH = showCharacterName ? lineH * 2 + ImGui.GetStyle().ItemSpacing.Y : lineH;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (picSize - textH) / 2f));
            UiSharedService.ColorText(rpName, UiSharedService.ThemeNavTextActive);
            if (showCharacterName)
                ImGui.TextDisabled(characterName);
            ImGui.EndGroup();

            if (ownerPair != null)
            {
                var label = Loc.Get("Establishment.Detail.ViewRpProfile");
                float buttonWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.IdCard, label);
                float rightEdge = ImGui.GetWindowContentRegionMax().X - padding.X;
                ImGui.SameLine();
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), rightEdge - buttonWidth));
                ImGui.SetCursorPosY(rowTop + MathF.Max(0f, (picSize - ImGui.GetFrameHeight()) / 2f));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.IdCard, label))
                {
                    // Le monde n'est connu que si le gérant est le personnage actuellement joué par la paire.
                    uint? worldId = string.Equals(ownerPair.PlayerName, characterName, StringComparison.Ordinal) && ownerPair.WorldId != 0
                        ? ownerPair.WorldId : null;
                    Mediator.Publish(new ProfileOpenStandaloneMessage(ownerPair, characterName, worldId));
                }
            }
        }, padding: padding, stretchWidth: true);
    }

    private async Task LoadOwnRpProfiles()
    {
        if (_ownRpProfilesLoading) return;
        _ownRpProfilesLoading = true;
        try
        {
            _ownRpProfiles = await _apiController.EstablishmentGetOwnRpProfiles().ConfigureAwait(false);
            _logger.LogDebug("Loaded {count} own RP profiles", _ownRpProfiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading own RP profiles");
            _ownRpProfiles = [];
        }
        finally
        {
            _ownRpProfilesLoading = false;
        }
    }

    private void DrawEditMode()
    {
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##editName", ref _editName, 100);
        UiSharedService.AttachToolTip(Loc.Get("Establishment.Detail.NameTooltip"));

        ResizableTextArea.Draw("##editDesc", ref _editDescription, 2000, defaultHeight: 180f);

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        UiSharedService.Combo($"{Loc.Get("Establishment.Field.Category")}##edit", ref _editCategory, CategoryNames, CategoryNames.Length);

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"{Loc.Get("Establishment.Field.Schedule")}##edit", Loc.Get("Establishment.Field.ScheduleHint"), ref _editSchedule, 200);

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint($"{Loc.Get("Establishment.Field.Faction")}##edit", Loc.Get("Establishment.Field.Optional"), ref _editFactionTag, 50);

        ToggleSwitch.Draw($"{Loc.Get("Establishment.Field.PublicDirectory")}##edit", ref _editIsPublic);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        
        ImGui.TextColored(UiSharedService.AccentColor, Loc.Get("Establishment.Section.Location"));
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.District"));
        ImGui.SetNextItemWidth(220);
        if (UiSharedService.Combo("##editDistrict", ref _editDistrictIndex, DistrictNames, DistrictNames.Length))
            _editLocationDirty = true;

        ImGui.SetNextItemWidth(220);
        if (UiSharedService.Combo("##editHousingType", ref _editHousingType, HousingTypeNames, HousingTypeNames.Length))
            _editLocationDirty = true;

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Ward"));
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputInt("##editWard", ref _editWard, 1, 1))
            _editLocationDirty = true;
        _editWard = Math.Clamp(_editWard, 1, 30);

        if (_editHousingType == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Plot"));
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputInt("##editPlot", ref _editPlot, 1, 1))
                _editLocationDirty = true;
            _editPlot = Math.Clamp(_editPlot, 1, 60);
            if (_editPlot > 30) _editIsSubdivision = true;

            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.Annex"));
            if (ToggleSwitch.Draw("##editAnnexe", ref _editIsSubdivision))
                _editLocationDirty = true;
            UiSharedService.AttachToolTip(Loc.Get("Establishment.Location.AnnexTooltip"));
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Location.RoomNumber"));
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputInt("##editRoom", ref _editRoomNumber, 1, 1))
                _editLocationDirty = true;
            _editRoomNumber = Math.Max(_editRoomNumber, 1);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Manager RP profile selection
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Detail.Manager.Choose"));

        if (_ownRpProfiles == null && !_ownRpProfilesLoading)
            _ = LoadOwnRpProfiles();

        if (_ownRpProfiles == null)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Directory.Loading"));
        }
        else
        {
            var currentProfile = _ownRpProfiles.FirstOrDefault(p => p.Id == _editManagerRpProfileId);
            var managerPreview = currentProfile != null
                ? $"{currentProfile.RpFirstName} {currentProfile.RpLastName}".Trim()
                : Loc.Get("Establishment.Syncshell.None");
            if (string.IsNullOrEmpty(managerPreview))
                managerPreview = currentProfile?.CharacterName ?? Loc.Get("Establishment.Syncshell.None");

            ImGui.SetNextItemWidth(250);
            using (var combo = UiSharedService.Combo("##editManager", managerPreview))
            {
                if (combo)
                {
                    if (ImGui.Selectable(Loc.Get("Establishment.Syncshell.None"), _editManagerRpProfileId == null))
                        _editManagerRpProfileId = null;

                    foreach (var profile in _ownRpProfiles)
                    {
                        var rpName = $"{profile.RpFirstName} {profile.RpLastName}".Trim();
                        var display = string.IsNullOrEmpty(rpName)
                            ? profile.CharacterName
                            : $"{rpName} ({profile.CharacterName})";
                        if (ImGui.Selectable(display, _editManagerRpProfileId == profile.Id))
                            _editManagerRpProfileId = profile.Id;
                    }
                }
            }
        }

        ToggleSwitch.Draw(Loc.Get("Establishment.Detail.ShowOnProfile"), ref _editShowManagerOnProfile);

        ImGui.Spacing();
        using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
        if (ImGui.Button(Loc.Get("Establishment.Detail.Save"), new Vector2(120, 0)))
            _ = SaveChanges();
    }

    private static readonly string[] DayNamesShort = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];

    private static string FormatEventTime(EstablishmentEventDto evt)
    {
        var startUtc = evt.StartsAtUtc;
        var endUtc = evt.EndsAtUtc;
        if (evt.Recurrence > 0 && EstablishmentReminderService.ComputeCurrentOrNextOccurrence(evt, DateTime.UtcNow) is { } next)
        {
            startUtc = next.Start;
            endUtc = evt.EndsAtUtc.HasValue ? next.End : null;
        }

        var local = startUtc.ToLocalTime();
        var dayName = DayNamesShort[(int)local.DayOfWeek == 0 ? 6 : (int)local.DayOfWeek - 1];
        var date = $"{dayName} {local:dd/MM} - {local:HH}h{local:mm}";
        if (endUtc.HasValue)
        {
            var endLocal = endUtc.Value.ToLocalTime();
            date += $" > {endLocal:HH}h{endLocal:mm}";
        }
        return date;
    }

    private static string GetRecurrenceLabel(int recurrence) => recurrence switch
    {
        1 => Loc.Get("Establishment.Event.Recurrence.Daily"),
        2 => Loc.Get("Establishment.Event.Recurrence.Weekly"),
        3 => Loc.Get("Establishment.Event.Recurrence.Monthly"),
        4 => Loc.Get("Establishment.Event.Recurrence.Biweekly"),
        5 => Loc.Get("Establishment.Event.Recurrence.Bimonthly"),
        6 => Loc.Get("Establishment.Event.Recurrence.Quarterly"),
        7 => Loc.Get("Establishment.Event.Recurrence.Yearly"),
        _ => Loc.Get("Establishment.Event.Recurrence.Unique")
    };

    private static FontAwesomeIcon GetRecurrenceIcon(int recurrence) => recurrence switch
    {
        1 => FontAwesomeIcon.Redo,
        2 => FontAwesomeIcon.CalendarWeek,
        3 => FontAwesomeIcon.CalendarAlt,
        4 => FontAwesomeIcon.CalendarWeek,
        5 => FontAwesomeIcon.CalendarAlt,
        6 => FontAwesomeIcon.CalendarAlt,
        7 => FontAwesomeIcon.CalendarCheck,
        _ => FontAwesomeIcon.Calendar
    };

    private void DrawEventsTab(bool isOwner)
    {
        ImGui.Spacing();

        if (_establishment!.Events.Count == 0)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Event.NoEvents"));
        }
        else
        {
            var now = DateTime.UtcNow;
            var futureEvents = _establishment.Events
                .Where(e => e.StartsAtUtc >= now || (e.EndsAtUtc.HasValue && e.EndsAtUtc.Value >= now) || e.Recurrence > 0)
                .OrderBy(e => e.Recurrence > 0
                    ? EstablishmentReminderService.ComputeCurrentOrNextOccurrence(e, now)?.Start ?? e.StartsAtUtc
                    : e.StartsAtUtc)
                .ToList();
            var pastEvents = _establishment.Events
                .Where(e => e.StartsAtUtc < now && (!e.EndsAtUtc.HasValue || e.EndsAtUtc.Value < now) && e.Recurrence == 0)
                .OrderByDescending(e => e.StartsAtUtc)
                .ToList();

            foreach (var evt in futureEvents)
                DrawEventCard(evt, isOwner, false);

            if (pastEvents.Count > 0)
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextDisabled($"--- {Loc.Get("Establishment.Event.Past")} ({pastEvents.Count}) ---");
                ImGuiHelpers.ScaledDummy(2f);
                foreach (var evt in pastEvents)
                    DrawEventCard(evt, isOwner, true);
            }
        }

        if (isOwner)
        {
            ImGui.Separator();
            ImGui.Spacing();
            DrawEventCreationForm();
        }
    }

    private void DrawEventCard(EstablishmentEventDto evt, bool isOwner, bool isPast)
    {
        ImGui.PushID(evt.Id.ToString());

        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f, 7f) * scale;
        // Bord droit du contenu de la carte, en coordonnées locales : rien ne doit le dépasser.
        float contentRight = ImGui.GetWindowContentRegionMax().X - padding.X;

        UiSharedService.DrawCard($"evt_{evt.Id}", () =>
        {
            var style = ImGui.GetStyle();
            var trashSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Trash);

            // Row 1: Icon + Title + Delete button
            var recIcon = GetRecurrenceIcon(evt.Recurrence);
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(isPast ? ImGuiColors.DalamudGrey : UiSharedService.AccentColor, recIcon.ToIconString());
            ImGui.SameLine();
            var titleColor = isPast ? ImGuiColors.DalamudGrey : new Vector4(1f, 0.9f, 0.6f, 1f);
            var title = UiSharedService.SanitizeOneLine(evt.Title);
            float titleMax = contentRight - ImGui.GetCursorPosX() - (isOwner ? trashSize.X + style.ItemSpacing.X : 0f);
            var shownTitle = UiSharedService.TruncateToWidth(title, MathF.Max(40f * scale, titleMax));
            UiSharedService.ColorText(shownTitle, titleColor);
            if (!string.Equals(shownTitle, title, StringComparison.Ordinal))
                UiSharedService.AttachToolTip(title);

            if (isOwner)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosX(contentRight - trashSize.X);
                using var red = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.5f, 0.1f, 0.1f, 1f));
                if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    _ = DeleteEvent(evt.Id);
                UiSharedService.AttachToolTip(Loc.Get("Establishment.Event.Delete"));
            }

            // Row 2: Date/time + recurrence badge
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudGrey, FontAwesomeIcon.Clock.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, FormatEventTime(evt));

            if (evt.Recurrence > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(UiSharedService.AccentColor, $"[{GetRecurrenceLabel(evt.Recurrence)}]");
            }

            // Row 3: Description
            if (!string.IsNullOrEmpty(evt.Description))
            {
                ImGuiHelpers.ScaledDummy(1f);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                using (ImRaii.TextWrapPos(contentRight))
                    ImGui.TextWrapped(evt.Description);
            }
        }, padding: padding, stretchWidth: true);

        ImGui.PopID();
    }

    private void DrawEventCreationForm()
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(UiSharedService.AccentColor, FontAwesomeIcon.PlusCircle.ToIconString());
        ImGui.SameLine();
        ImGui.Text(Loc.Get("Establishment.Event.New"));
        ImGui.Spacing();

        // Title + Description
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##evtTitle", Loc.Get("Establishment.Event.TitleHint"), ref _newEventTitle, 100);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##evtDesc", Loc.Get("Establishment.Event.DescriptionHint"), ref _newEventDescription, 500);

        ImGui.Spacing();

        // Date row
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Event.DateTimeLabel"));

        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("##evtDay", ref _newEventDay, 0, 0);
        var maxDay = DateTime.DaysInMonth(Math.Max(_newEventYear, 1), Math.Clamp(_newEventMonth, 1, 12));
        _newEventDay = Math.Clamp(_newEventDay, 1, maxDay);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        var monthNames = new[] { "Janvier", "Fevrier", "Mars", "Avril", "Mai", "Juin",
            "Juillet", "Aout", "Septembre", "Octobre", "Novembre", "Decembre" };
        var monthIdx = _newEventMonth - 1;
        if (UiSharedService.Combo("##evtMonth", ref monthIdx, monthNames, monthNames.Length))
            _newEventMonth = monthIdx + 1;
        _newEventMonth = Math.Clamp(_newEventMonth, 1, 12);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("##evtYear", ref _newEventYear, 0, 0);
        _newEventYear = Math.Clamp(_newEventYear, DateTime.Now.Year, DateTime.Now.Year + 2);

        // Time row
        ImGui.SetNextItemWidth(50);
        ImGui.InputInt("##evtHour", ref _newEventHour, 0, 0);
        _newEventHour = Math.Clamp(_newEventHour, 0, 23);
        ImGui.SameLine();
        ImGui.Text(":");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50);
        ImGui.InputInt("##evtMin", ref _newEventMinute, 0, 0);
        _newEventMinute = Math.Clamp(_newEventMinute, 0, 59);

        // End time
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(10f, 0);
        ImGui.SameLine();
        ToggleSwitch.Draw(Loc.Get("Establishment.Event.EndTime"), ref _newEventHasEndTime);
        if (_newEventHasEndTime)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##evtEndHour", ref _newEventEndHour, 0, 0);
            _newEventEndHour = Math.Clamp(_newEventEndHour, 0, 23);
            ImGui.SameLine();
            ImGui.Text(":");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##evtEndMin", ref _newEventEndMinute, 0, 0);
            _newEventEndMinute = Math.Clamp(_newEventEndMinute, 0, 59);
        }

        // Recurrence
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Establishment.Event.Recurrence"));
        var recLabels = new[]
        {
            Loc.Get("Establishment.Event.Recurrence.Unique"),
            Loc.Get("Establishment.Event.Recurrence.Daily"),
            Loc.Get("Establishment.Event.Recurrence.Weekly"),
            Loc.Get("Establishment.Event.Recurrence.Monthly"),
            Loc.Get("Establishment.Event.Recurrence.Biweekly"),
            Loc.Get("Establishment.Event.Recurrence.Bimonthly"),
            Loc.Get("Establishment.Event.Recurrence.Quarterly"),
            Loc.Get("Establishment.Event.Recurrence.Yearly")
        };
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        UiSharedService.Combo("##evtRecurrence", ref _newEventRecurrence, recLabels, recLabels.Length);

        // Validation
        var canCreate = !string.IsNullOrWhiteSpace(_newEventTitle);
        try
        {
            var localStart = new DateTime(_newEventYear, _newEventMonth, _newEventDay,
                _newEventHour, _newEventMinute, 0, DateTimeKind.Local);
            if (localStart < DateTime.Now.AddMinutes(-5))
            {
                canCreate = false;
                ImGui.TextColored(ImGuiColors.DalamudOrange, Loc.Get("Establishment.Event.FutureRequired"));
            }
        }
        catch
        {
            canCreate = false;
            ImGui.TextColored(ImGuiColors.DalamudOrange, Loc.Get("Establishment.Event.InvalidDate"));
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(!canCreate))
        {
            using var accent = ImRaii.PushColor(ImGuiCol.Button, UiSharedService.AccentColor);
            if (ImGui.Button(Loc.Get("Establishment.Event.Add"), new Vector2(ImGui.GetContentRegionAvail().X, 0)))
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
        using (var combo = UiSharedService.Combo("##syncslotBinding", currentDisplay))
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
        ImGui.Text(Loc.Get("Establishment.Image.Banner"));
        ImGui.TextDisabled(Loc.Get("Establishment.Image.BannerHint"));

        var bannerTex = _editBannerTexture ?? _bannerTexture;
        var hasBanner = bannerTex != null || _editBannerBytes.Length > 0;

        if (bannerTex == null && _editBannerBytes.Length > 0)
        {
            _editBannerTexture = _uiSharedService.LoadImage(_editBannerBytes);
            bannerTex = _editBannerTexture;
            hasBanner = bannerTex != null;
        }

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
            ImGui.TextDisabled(Loc.Get("Establishment.Image.NoBanner"));
        }

        if (isOwner)
        {
            using var _bannerUploadId = ImRaii.PushId("uploadBanner");
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("Establishment.Image.Upload")))
            {
                _logger.LogInformation("Opening file dialog for banner upload");
                _fileDialogManager.OpenFileDialog(
                    Loc.Get("Establishment.Image.ChooseBanner"),
                    "Image files{.png,.jpg,.jpeg}",
                    (success, name) =>
                    {
                        _logger.LogInformation("Banner file dialog callback: success={success}, path={path}", success, name);
                        if (!success) return;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                                _logger.LogInformation("Banner file read: {size} bytes", bytes.Length);
                                if (bytes.Length > 2 * 1024 * 1024)
                                {
                                    _imageMessage = Loc.Get("Establishment.Image.TooLarge");
                                    return;
                                }
                                _imageMessage = null;
                                _editBannerBytes = bytes;
                                _editBannerTexture?.Dispose();
                                _editBannerTexture = _uiSharedService.LoadImage(bytes);
                                _logger.LogInformation("Banner texture loaded: {ok}, bytes={len}", _editBannerTexture != null, bytes.Length);
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
            using (ImRaii.Disabled(_editBannerBytes.Length == 0 && _establishment!.BannerImageBase64 is not { Length: > 0 }))
            {
                using (ImRaii.PushId("clearBanner"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Establishment.Image.Remove")))
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
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("Establishment.Detail.Save")))
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
        ImGui.Text(Loc.Get("Establishment.Image.Logo"));
        ImGui.TextDisabled(Loc.Get("Establishment.Image.LogoHint"));

        // Use edit texture if available, otherwise server texture
        var logoTex = _editLogoTexture ?? _logoTexture;
        var hasLogo = logoTex != null || _editLogoBytes.Length > 0;

        // Lazy-create texture on main thread if bytes exist but texture doesn't
        if (logoTex == null && _editLogoBytes.Length > 0)
        {
            _editLogoTexture = _uiSharedService.LoadImage(_editLogoBytes);
            logoTex = _editLogoTexture;
            hasLogo = logoTex != null;
        }

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
            ImGui.TextDisabled(Loc.Get("Establishment.Image.NoLogo"));
        }

        if (isOwner)
        {
            using var _logoUploadId = ImRaii.PushId("uploadLogo");
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, Loc.Get("Establishment.Image.Upload")))
            {
                _logger.LogInformation("Opening file dialog for logo upload");
                _fileDialogManager.OpenFileDialog(
                    Loc.Get("Establishment.Image.ChooseLogo"),
                    "Image files{.png,.jpg,.jpeg}",
                    (success, name) =>
                    {
                        _logger.LogInformation("Logo file dialog callback: success={success}, path={path}", success, name);
                        if (!success) return;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var bytes = await File.ReadAllBytesAsync(name).ConfigureAwait(false);
                                _logger.LogInformation("Logo file read: {size} bytes", bytes.Length);
                                if (bytes.Length > 2 * 1024 * 1024)
                                {
                                    _imageMessage = Loc.Get("Establishment.Image.TooLarge");
                                    return;
                                }
                                _imageMessage = null;
                                _editLogoBytes = bytes;
                                _editLogoTexture?.Dispose();
                                _editLogoTexture = _uiSharedService.LoadImage(bytes);
                                _logger.LogInformation("Logo texture loaded: {ok}, bytes={len}", _editLogoTexture != null, bytes.Length);
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
            using (ImRaii.Disabled(_editLogoBytes.Length == 0 && _establishment!.LogoImageBase64 is not { Length: > 0 }))
            {
                using (ImRaii.PushId("clearLogo"))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, Loc.Get("Establishment.Image.Remove")))
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
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, Loc.Get("Establishment.Detail.Save")))
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
            ManagerRpProfileId = _establishment.ManagerRpProfileId,
            ShowManagerOnProfile = _establishment.ShowManagerOnProfile,
            Location = _establishment.Location
        };

        var success = await _apiController.EstablishmentUpdate(request).ConfigureAwait(false);
        if (success)
        {
            Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), Loc.Get("Establishment.Detail.ImagesSaved"), NotificationType.Info));
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
        _editManagerRpProfileId = _establishment.ManagerRpProfileId;
        _editShowManagerOnProfile = _establishment.ShowManagerOnProfile;

        var loc = _establishment.Location;
        _editLocationDirty = false;
        _editServerId = loc?.ServerId;
        _editDistrictIndex = loc != null ? ResolveDistrictIndex(loc.TerritoryId) : 0;
        _editHousingType = (loc?.IsApartment ?? false) ? 1 : 0;
        _editWard = (int)Math.Clamp(loc?.WardId ?? 1u, 1u, 30u);
        _editPlot = (int)Math.Clamp(loc?.PlotId ?? 1u, 1u, 60u);
        _editRoomNumber = (int)Math.Max(loc?.RoomId ?? 1u, 1u);
        _editIsSubdivision = (loc?.DivisionId ?? 1u) > 1u;

        _ownRpProfiles = null;
        _ownRpProfilesLoading = false;
    }

    private EstablishmentLocationDto BuildEditedLocation()
    {
        bool isApt = _editHousingType == 1;
        return new EstablishmentLocationDto
        {
            LocationType = 0,
            TerritoryId = ResidentialDistricts[_editDistrictIndex].TerritoryId,
            ServerId = _editServerId,
            WardId = (uint)_editWard,
            PlotId = isApt ? null : (uint)_editPlot,
            DivisionId = _editIsSubdivision ? 2u : 1u,
            IsApartment = isApt ? true : null,
            RoomId = isApt && _editRoomNumber > 0 ? (uint)_editRoomNumber : null,
        };
    }

    private async Task SaveChanges()
    {
        _logger.LogInformation("Saving establishment changes for {id}", _establishment!.Id);
        try
        {
            var request = new EstablishmentUpdateRequestDto
            {
                Id = _establishment.Id,
                Name = _editName,
                Description = string.IsNullOrWhiteSpace(_editDescription) ? null : _editDescription,
                Category = _editCategory,
                Schedule = string.IsNullOrWhiteSpace(_editSchedule) ? null : _editSchedule,
                FactionTag = string.IsNullOrWhiteSpace(_editFactionTag) ? null : _editFactionTag,
                IsPublic = _editIsPublic,
                LogoImageBase64 = _establishment.LogoImageBase64,
                BannerImageBase64 = _establishment.BannerImageBase64,
                ManagerRpProfileId = _editManagerRpProfileId,
                ShowManagerOnProfile = _editShowManagerOnProfile,
                Location = _editLocationDirty ? BuildEditedLocation() : _establishment.Location
            };

            var success = await _apiController.EstablishmentUpdate(request).ConfigureAwait(false);
            if (success)
            {
                _logger.LogInformation("Establishment {id} updated successfully", _establishment.Id);
                _isEditing = false;
                Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), Loc.Get("Establishment.Detail.ChangesSaved"), NotificationType.Info));
                Mediator.Publish(new EstablishmentChangedMessage());
                await LoadEstablishment().ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Failed to update establishment {id}", _establishment.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving establishment changes for {id}", _establishment.Id);
        }
    }

    private async Task DeleteEstablishment()
    {
        _logger.LogInformation("Deleting establishment {id}", _establishment!.Id);
        try
        {
            var success = await _apiController.EstablishmentDelete(_establishment.Id).ConfigureAwait(false);
            if (success)
            {
                _logger.LogInformation("Establishment {id} deleted", _establishment.Id);
                Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Detail.Title"), Loc.Get("Establishment.Detail.Deleted"), NotificationType.Info));
                Mediator.Publish(new EstablishmentChangedMessage());
                _establishment = null;
                IsOpen = false;
            }
            else
            {
                _logger.LogWarning("Failed to delete establishment {id}", _establishment.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting establishment {id}", _establishment?.Id);
        }
    }

    private async Task CreateEvent()
    {
        _logger.LogInformation("Creating event for establishment {id}", _establishment!.Id);
        try
        {
            var localStart = new DateTime(_newEventYear, _newEventMonth, _newEventDay,
                _newEventHour, _newEventMinute, 0, DateTimeKind.Local);

            DateTime? localEnd = null;
            if (_newEventHasEndTime)
            {
                localEnd = new DateTime(_newEventYear, _newEventMonth, _newEventDay,
                    _newEventEndHour, _newEventEndMinute, 0, DateTimeKind.Local);
                if (localEnd <= localStart)
                    localEnd = localStart.AddHours(1);
            }

            var request = new EstablishmentEventUpsertRequestDto
            {
                EstablishmentId = _establishment.Id,
                Title = _newEventTitle.Trim(),
                Description = string.IsNullOrWhiteSpace(_newEventDescription) ? null : _newEventDescription,
                StartsAtUtc = localStart.ToUniversalTime(),
                EndsAtUtc = localEnd?.ToUniversalTime(),
                Recurrence = _newEventRecurrence
            };

            _logger.LogDebug("Event: {title}, starts {start}, ends {end}", request.Title, request.StartsAtUtc, request.EndsAtUtc);
            var result = await _apiController.EstablishmentEventUpsert(request).ConfigureAwait(false);
            if (result != null)
            {
                _logger.LogInformation("Event '{title}' created for establishment {id}", result.Title, _establishment.Id);
                _newEventTitle = string.Empty;
                _newEventDescription = string.Empty;
                ResetEventDateFields();
                Mediator.Publish(new EstablishmentChangedMessage());
                await LoadEstablishment().ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Failed to create event for establishment {id}", _establishment.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event for establishment {id}", _establishment.Id);
        }
    }

    private void ResetEventDateFields()
    {
        var now = DateTime.Now;
        _newEventDay = now.Day;
        _newEventMonth = now.Month;
        _newEventYear = now.Year;
        _newEventHour = 21;
        _newEventMinute = 0;
        _newEventHasEndTime = false;
        _newEventEndHour = 23;
        _newEventEndMinute = 0;
        _newEventRecurrence = 0;
    }

    private async Task DeleteEvent(Guid eventId)
    {
        _logger.LogInformation("Deleting event {eventId}", eventId);
        try
        {
            var success = await _apiController.EstablishmentEventDelete(eventId).ConfigureAwait(false);
            if (success)
            {
                _logger.LogInformation("Event {eventId} deleted", eventId);
                Mediator.Publish(new EstablishmentChangedMessage());
                await LoadEstablishment().ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Failed to delete event {eventId}", eventId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting event {eventId}", eventId);
        }
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

            var hasLogoB64 = _establishment?.LogoImageBase64 is { Length: > 0 };
            var hasBannerB64 = _establishment?.BannerImageBase64 is { Length: > 0 };
            _logger.LogInformation("LoadEstablishment: hasLogo={hasLogo}, hasBanner={hasBanner}", hasLogoB64, hasBannerB64);

            if (_establishment?.LogoImageBase64 is { Length: > 0 } logoB64)
            {
                try
                {
                    var logoBytes = Convert.FromBase64String(logoB64);
                    _logoTexture = await _uiSharedService.LoadImageOrEmptyAsync(logoBytes).ConfigureAwait(false);
                    _logger.LogInformation("Server logo texture: {ok}, b64Len={len}", _logoTexture != null, logoB64.Length);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to load logo texture for establishment {id}", _currentId); }
            }
            if (_establishment?.BannerImageBase64 is { Length: > 0 } bannerB64)
            {
                try
                {
                    var bannerBytes = Convert.FromBase64String(bannerB64);
                    _bannerTexture = await _uiSharedService.LoadImageOrEmptyAsync(bannerBytes).ConfigureAwait(false);
                    _logger.LogInformation("Server banner texture: {ok}, b64Len={len}", _bannerTexture != null, bannerB64.Length);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to load banner texture for establishment {id}", _currentId); }
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
