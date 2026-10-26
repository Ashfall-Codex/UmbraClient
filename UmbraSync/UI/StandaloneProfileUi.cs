using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.API.Dto.User;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Models;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public class StandaloneProfileUi : WindowMediatorSubscriberBase
{
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly MareConfigService _configService;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastRpProfilePicture = [];
    private IDalamudTextureWrap? _textureWrap;
    private IDalamudTextureWrap? _rpTextureWrap;
    private IDalamudTextureWrap? _rpBannerTexture;
    private byte[]? _lastRpBanner;
    private SheetPalette? _sheetPalette;
    private ProfileCardStyle _rpCardStyle = ProfileCardStyle.Default;
    private bool _isRpTab = true;
    private bool _windowSizeInitialized = false;
    private string _localMoodlesJson = string.Empty;
    private bool _moodlesFetching;
    private DateTime _lastMoodlesFetch = DateTime.MinValue;
    private string? _selectedAltCharName;
    private uint? _selectedAltWorldId;
    private volatile bool _resetTexturesPending;
    private string _characterNoteDraft = string.Empty;
    private string? _characterNoteKey;
    private const float FixedWindowWidth = 700f;
    public void SelectCharacter(string? charName, uint? worldId)
    {
        if (string.IsNullOrEmpty(charName) || worldId is not > 0) return;
        _selectedAltCharName = charName;
        _selectedAltWorldId = worldId;
        _resetTexturesPending = true;
    }

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, MareMediator mediator, UiSharedService uiBuilder,
        ServerConfigurationManager serverManager, MareConfigService configService, UmbraProfileManager umbraProfileManager, ApiController apiController, Pair pair,
        PerformanceCollectorService performanceCollector, IpcManager ipcManager, DalamudUtilService dalamudUtil)
        : base(logger, mediator, string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("StandaloneProfile.WindowTitle"), pair.UserData.AliasOrUID) + "###UmbraSyncStandaloneProfileUI" + pair.UserData.AliasOrUID, performanceCollector)
    {
        _uiSharedService = uiBuilder;
        _serverManager = serverManager;
        _configService = configService;
        _umbraProfileManager = umbraProfileManager;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        Pair = pair;
        Flags = ImGuiWindowFlags.None;

        SizeConstraints = new()
        {
            MinimumSize = new(FixedWindowWidth, 400),
            MaximumSize = new(FixedWindowWidth, 2000)
        };

        bool isSelf = string.Equals(pair.UserData.UID, apiController.UID, StringComparison.Ordinal);
        if (isSelf)
        {
            Mediator.Subscribe<MoodlesMessage>(this, (msg) => _ = Task.Run(RefreshLocalMoodlesAsync));
        }
        // Une affiliation acceptée, quittée ou masquée change les établissements affichés : rechargement au prochain dessin.
        Mediator.Subscribe<EstablishmentChangedMessage>(this, (_) => _profileEstablishmentsKey = null);

        IsOpen = true;
    }

    private (string Text, Vector4 Color, bool IsOnline, bool IsSelf) ResolvePresence()
    {
        bool isSelf = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);

        bool isOnline = isSelf ? _apiController.IsConnected : Pair.IsVisible || Pair.IsOnline;
        var color = isOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;

        if (isSelf)
            return (isOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline"), color, isOnline, true);

        var text = Pair.IsVisible
            ? Loc.Get("StandaloneProfile.Status.Visible")
            : Pair.IsOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline");

        return (text, color, isOnline, false);
    }

    private static void DrawPortrait(ImDrawListPtr drawList, IDalamudTextureWrap? texture, float portraitSize)
    {
        var portraitStart = ImGui.GetCursorScreenPos();
        var rounding = 10f * ImGuiHelpers.GlobalScale;

        if (texture == null)
        {
            drawList.AddRectFilled(portraitStart,
                new Vector2(portraitStart.X + portraitSize, portraitStart.Y + portraitSize),
                ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), rounding);
            return;
        }

        bool tallerThanWide = texture.Height >= texture.Width;
        var stretchFactor = tallerThanWide ? portraitSize / texture.Height : portraitSize / texture.Width;
        var newWidth = texture.Width * stretchFactor;
        var newHeight = texture.Height * stretchFactor;

        var pMin = new Vector2(portraitStart.X + (portraitSize - newWidth) / 2f,
            portraitStart.Y + (portraitSize - newHeight) / 2f);
        var pMax = new Vector2(pMin.X + newWidth, pMin.Y + newHeight);

        drawList.AddImageRounded(texture.Handle, pMin, pMax,
            Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), rounding);
    }

    private async Task RefreshLocalMoodlesAsync()
    {
        if (_moodlesFetching) return;
        _moodlesFetching = true;
        try
        {
            if (!_ipcManager.Moodles.APIAvailable) return;
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;
            _localMoodlesJson = await _ipcManager.Moodles.GetStatusAsync(ptr).ConfigureAwait(false) ?? string.Empty;
            _lastMoodlesFetch = DateTime.UtcNow;
        }
        finally
        {
            _moodlesFetching = false;
        }
    }

    public Pair Pair { get; init; }

    private UmbraProfileData ResolveProfile()
        => (_selectedAltCharName != null && _selectedAltWorldId != null)
            ? _umbraProfileManager.GetUmbraProfile(Pair.UserData, _selectedAltCharName, _selectedAltWorldId)
            : _umbraProfileManager.GetUmbraProfile(Pair.UserData);

    public override void PreDraw()
    {
        UiSharedService.BeginProfileAccent(ResolveProfile().CardStyle.Value.AccentColor);
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        UiSharedService.EndProfileAccent();
    }

    protected override void DrawInternal()
    {
        try
        {
            if (!_windowSizeInitialized)
            {
                ImGui.SetWindowSize(new Vector2(FixedWindowWidth * ImGuiHelpers.GlobalScale, 600f * ImGuiHelpers.GlobalScale));
                _windowSizeInitialized = true;
            }

            if (_resetTexturesPending)
            {
                _resetTexturesPending = false;
                _lastProfilePicture = [];
                _lastRpProfilePicture = [];
                _textureWrap?.Dispose();
                _textureWrap = null;
                _rpTextureWrap?.Dispose();
                _rpTextureWrap = null;
                _rpBannerTexture?.Dispose();
                _rpBannerTexture = null;
                _lastRpBanner = null;
            }

            var umbraProfile = ResolveProfile();

            var accent = UiSharedService.AccentColor;
            if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

            // RP / HRP toggle buttons
            DrawTabButtons(accent);

            // Load textures
            var pfpData = _isRpTab ? umbraProfile.RpImageData.Value : umbraProfile.ImageData.Value;

            if (_isRpTab)
            {
                if (_rpTextureWrap == null || !pfpData.SequenceEqual(_lastRpProfilePicture))
                {
                    _rpTextureWrap?.Dispose();
                    _lastRpProfilePicture = pfpData;
                    _rpTextureWrap = _uiSharedService.LoadImage(_lastRpProfilePicture);
                }
            }
            else
            {
                if (_textureWrap == null || !pfpData.SequenceEqual(_lastProfilePicture))
                {
                    _textureWrap?.Dispose();
                    _lastProfilePicture = pfpData;
                    _textureWrap = _uiSharedService.LoadImage(_lastProfilePicture);
                }
            }

            if (_isRpTab)
            {
                var bannerData = umbraProfile.RpBannerData.Value;
                if (!ReferenceEquals(bannerData, _lastRpBanner))
                {
                    // Une prévisualisation rafraîchie recrée le tableau : ne recharger la texture que si l'image change.
                    if (!bannerData.AsSpan().SequenceEqual(_lastRpBanner))
                    {
                        _rpBannerTexture?.Dispose();
                        _rpBannerTexture = bannerData.Length > 0 ? _uiSharedService.LoadImage(bannerData) : null;
                    }
                    _lastRpBanner = bannerData;
                }
            }

            var currentTexture = _isRpTab ? _rpTextureWrap : _textureWrap;

            // Profil enrichi (onglet RP) à gauche, signalement (pas soi-même) à droite, sur la même ligne
            if (_isRpTab)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, "Voir le profil enrichi"))
                    OpenEnrichedProfile();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("S'ouvre dans votre navigateur web. Le profil doit être en mode \"Public\" sur Connect pour être accessible.");
            }

            if (!string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                var reportButtonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.ExclamationTriangle, Loc.Get("StandaloneProfile.ReportButton"));
                ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - reportButtonSize);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExclamationTriangle, Loc.Get("StandaloneProfile.ReportButton")))
                    Mediator.Publish(new OpenReportPopupMessage(Pair));
            }

            ImGuiHelpers.ScaledDummy(2f);


            if (ImGui.BeginChild("ProfileScrollArea", ImGui.GetContentRegionAvail(), false))
            {
                if (_isRpTab)
                {
                    var sheetMin = ImGui.GetWindowPos();
                    var sheetMax = sheetMin + ImGui.GetWindowSize();
                    if (ImGui.GetScrollMaxY() > 0f)
                        sheetMax.X -= ImGui.GetStyle().ScrollbarSize;
                    _rpCardStyle = umbraProfile.CardStyle.Value;
                    var sheetRounding = UiSharedService.RadiusCard * ImGuiHelpers.GlobalScale;
                    _sheetPalette = ProfileSheetBackground.Draw(_uiSharedService.TextureProvider, ImGui.GetWindowDrawList(),
                            _rpCardStyle, sheetMin, sheetMax, sheetRounding)
                        ?? ProfileSheetBackground.DrawPlain(ImGui.GetWindowDrawList(), _rpCardStyle, sheetMin, sheetMax, sheetRounding);
                    var contentColor = _sheetPalette?.Value ?? ProfileSheetBackground.CustomColor(_rpCardStyle.SheetTextColor);
                    using (ImRaii.PushColor(ImGuiCol.Text, contentColor ?? Vector4.Zero, contentColor != null))
                        DrawRpProfile(umbraProfile, currentTexture, accent);
                }
                else
                {
                    _sheetPalette = null;
                    _rpCardStyle = ProfileCardStyle.Default;
                    DrawHrpProfile(umbraProfile, currentTexture, accent);
                }
            }
            ImGui.EndChild();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw standalone profile");
        }
    }

    private void DrawTabButtons(Vector4 accent)
    {
        var labels = new[] { "RP", "HRP" };
        var icons = new[] { FontAwesomeIcon.Scroll, FontAwesomeIcon.User };
        const float btnH = 32f;
        const float btnSpacing = 8f;
        const float rounding = 4f;
        const float iconTextGap = 6f;

        var dl = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var btnW = (availWidth - btnSpacing * (labels.Length - 1)) / labels.Length;

        var borderColor = UiSharedService.ThemeButtonActive;
        var bgColor = UiSharedService.WithAlpha(UiSharedService.ThemeWindowBg, 0.9f);
        var hoverBg = UiSharedService.ThemeButtonBg;

        for (int i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, btnSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##profileTab_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();
            bool isActive = _isRpTab ? i == 0 : i == 1;

            var bg = isActive ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isActive)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            ImGui.PushFont(UiBuilder.IconFont);
            var iconStr = icons[i].ToIconString();
            var iconSz = ImGui.CalcTextSize(iconStr);
            ImGui.PopFont();

            var labelSz = ImGui.CalcTextSize(labels[i]);
            var totalW = iconSz.X + iconTextGap + labelSz.X;
            var startX = p.X + (btnW - totalW) / 2f;

            var textColor = isActive ? UiSharedService.ThemeNavTextActive : hovered ? UiSharedService.ThemeNavTextHovered : UiSharedService.ThemeNavText;
            var textColorU32 = ImGui.GetColorU32(textColor);

            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(new Vector2(startX, p.Y + (btnH - iconSz.Y) / 2f), textColorU32, iconStr);
            ImGui.PopFont();

            dl.AddText(new Vector2(startX + iconSz.X + iconTextGap, p.Y + (btnH - labelSz.Y) / 2f), textColorU32, labels[i]);

            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked) _isRpTab = i == 0;
        }

        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawCharacterNote()
    {
        if (string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal)) return;

        var (charName, worldId) = (_selectedAltCharName != null && _selectedAltWorldId != null)
            ? (_selectedAltCharName, _selectedAltWorldId)
            : _umbraProfileManager.ResolveCharacter(Pair.UserData);
        if (string.IsNullOrEmpty(charName) || worldId is not > 0) return;

        // Le brouillon suit le personnage affiché : changer de personnage recharge sa propre note.
        var key = $"{Pair.UserData.UID}|{charName}|{worldId}";
        if (!string.Equals(_characterNoteKey, key, StringComparison.Ordinal))
        {
            _characterNoteKey = key;
            _characterNoteDraft = _serverManager.GetNoteForCharacter(Pair.UserData.UID, charName, worldId) ?? string.Empty;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##characterNote", Loc.Get("StandaloneProfile.CharacterNote.Hint"), ref _characterNoteDraft, 200);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _serverManager.SetNoteForCharacter(Pair.UserData.UID, charName, worldId.Value, _characterNoteDraft);
        UiSharedService.AttachToolTip(Loc.Get("StandaloneProfile.CharacterNote.Tooltip"));
        ImGuiHelpers.ScaledDummy(4f);
    }

    private void DrawRpProfile(UmbraProfileData profile, IDalamudTextureWrap? texture, Vector4 accent)
    {
        var cardSpacing = 8f * ImGuiHelpers.GlobalScale;

        DrawHeroCard(profile, texture, accent);
        ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);

        DrawCharacterNote();

        bool hasAnyRpContent = !string.IsNullOrEmpty(profile.RpAge)
            || !string.IsNullOrEmpty(profile.RpHeight)
            || !string.IsNullOrEmpty(profile.RpBuild)
            || !string.IsNullOrEmpty(profile.RpResidence)
            || !string.IsNullOrEmpty(profile.RpOccupation)
            || !string.IsNullOrEmpty(profile.RpAffiliation)
            || !string.IsNullOrEmpty(profile.RpAlignment)
            || !string.IsNullOrEmpty(profile.RpDescription)
            || !string.IsNullOrEmpty(profile.RpAdditionalInfo)
            || profile.RpCustomFields is { Count: > 0 }
            || profile.RpLevel != 0;

        if (!hasAnyRpContent)
        {
            ImGuiHelpers.ScaledDummy(12f);
            var noProfileText = Loc.Get("UserProfile.NoRpProfile");
            var noProfileSz = ImGui.CalcTextSize(noProfileText);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - noProfileSz.X) / 2f);
            ImGui.TextColored(ImGuiColors.DalamudGrey, noProfileText);
            return;
        }

        bool hasAge = !string.IsNullOrEmpty(profile.RpAge);
        bool hasHeight = !string.IsNullOrEmpty(profile.RpHeight);
        bool hasBuild = !string.IsNullOrEmpty(profile.RpBuild);
        if (hasAge || hasHeight || hasBuild)
        {
            UiSharedService.DrawCard("rp-characteristics-card", () =>
            {
                DrawSectionTitle(string.Equals(Loc.Get("UserProfile.RpAge").Split(' ')[0], "Âge", StringComparison.Ordinal)
                    ? "Caractéristiques"
                    : "Characteristics");

                var fields = new List<(string Label, string Value)>(3);
                if (hasAge) fields.Add((Loc.Get("UserProfile.RpAge"), profile.RpAge!));
                if (hasHeight) fields.Add((Loc.Get("UserProfile.RpHeight"), profile.RpHeight!));
                if (hasBuild) fields.Add((Loc.Get("UserProfile.RpBuild"), profile.RpBuild!));

                // Colonnes égales sur la largeur du titre, champ centré dans la sienne.
                var startX = ImGui.GetCursorPosX();
                var colWidth = SectionContentWidth / fields.Count;
                for (int i = 0; i < fields.Count; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    ImGui.BeginGroup();
                    DrawCenteredField(fields[i].Label, fields[i].Value, startX + (i * colWidth), colWidth);
                    ImGui.EndGroup();
                }
            }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        bool hasResidence = !string.IsNullOrEmpty(profile.RpResidence);
        bool hasOccupation = !string.IsNullOrEmpty(profile.RpOccupation);
        bool hasAffiliation = !string.IsNullOrEmpty(profile.RpAffiliation);
        bool hasAlignment = !string.IsNullOrEmpty(profile.RpAlignment);
        if (hasResidence || hasOccupation || hasAffiliation || hasAlignment)
        {
            UiSharedService.DrawCard("rp-society-card", () =>
            {
                DrawSectionTitle("Société");

                // Deux colonnes sur la largeur du titre, champ centré dans la sienne, comme les Caractéristiques.
                var col1X = ImGui.GetCursorPosX();
                var halfW = SectionContentWidth / 2f;
                var col2X = col1X + halfW;

                bool row1HasContent = hasResidence || hasOccupation;
                if (row1HasContent)
                    DrawCenteredRow(hasResidence ? (Loc.Get("UserProfile.RpResidence"), profile.RpResidence!) : null,
                        hasOccupation ? (Loc.Get("UserProfile.RpOccupation"), profile.RpOccupation!) : null, col1X, col2X, halfW);

                if (hasAffiliation || hasAlignment)
                {
                    if (row1HasContent) ImGuiHelpers.ScaledDummy(4f);
                    DrawCenteredRow(hasAffiliation ? (Loc.Get("UserProfile.RpAffiliation"), profile.RpAffiliation!) : null,
                        hasAlignment ? (Loc.Get("UserProfile.RpAlignment"), profile.RpAlignment!) : null, col1X, col2X, halfW);
                }
            }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        var customFields = profile.RpCustomFields?
            .Where(f => !string.IsNullOrEmpty(f.Name) || !string.IsNullOrEmpty(f.Value))
            .OrderBy(f => f.Order)
            .ToList() ?? [];
        if (customFields.Count > 0)
        {
            UiSharedService.DrawCard("rp-custom-fields-card", () =>
            {
                DrawSectionTitle(Loc.Get("UserProfile.RpCustomFields"));

                var col1X = ImGui.GetCursorPosX();
                var fullW = SectionContentWidth;
                var halfW = fullW / 2f;
                for (int i = 0; i < customFields.Count; i += 2)
                {
                    if (i > 0) ImGuiHelpers.ScaledDummy(4f);
                    var left = (customFields[i].Name ?? string.Empty, customFields[i].Value ?? string.Empty);
                    if (i + 1 < customFields.Count)
                    {
                        var right = (customFields[i + 1].Name ?? string.Empty, customFields[i + 1].Value ?? string.Empty);
                        DrawCenteredRow(left, right, col1X, col1X + halfW, halfW, _rpCardStyle.JustifyCustomFields);
                    }
                    else
                    {
                        ImGui.BeginGroup();
                        DrawCenteredField(left.Item1, left.Item2, col1X, fullW, _rpCardStyle.JustifyCustomFields);
                        ImGui.EndGroup();
                    }
                }
            }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        var description = profile.RpDescription;
        if (!string.IsNullOrEmpty(description))
        {
            UiSharedService.DrawCard("rp-description-card", () =>
            {
                var rightPad = ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale;
                var wrapWidth = ImGui.GetContentRegionAvail().X - rightPad;
                DrawSectionTitle(Loc.Get("UserProfile.RpDescription"));
                using var _ = _uiSharedService.GameFont.Push();
                DrawSectionText(description, wrapWidth);
            }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        if (!string.IsNullOrEmpty(profile.RpAdditionalInfo))
        {
            UiSharedService.DrawCard("rp-additional-card", () =>
            {
                var rightPad = ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale;
                var wrapWidth = ImGui.GetContentRegionAvail().X - rightPad;
                DrawSectionTitle(Loc.Get("UserProfile.RpAdditionalInfo"));
                using var _ = _uiSharedService.GameFont.Push();
                DrawSectionText(profile.RpAdditionalInfo, wrapWidth);
            }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
        }

        // Establishment managed by this user
        DrawLinkedEstablishment(cardSpacing);
    }

    private void DrawHonorificInHero()
    {
        // Get Honorific data
        string? honorificB64 = null;
        bool isSelf = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);

        if (isSelf)
        {
            if (!IsDisplayingOwnLiveCharacter()) return;
            if (_ipcManager.Honorific.APIAvailable && string.IsNullOrEmpty(_localHonorificB64))
                _ = RefreshLocalHonorificAsync();
            honorificB64 = _localHonorificB64;
        }
        else if (IsDisplayingLiveCharacter())
        {
            honorificB64 = Pair.LastReceivedCharacterData?.HonorificData;
        }

        if (string.IsNullOrEmpty(honorificB64)) return;

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(honorificB64));
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = root.TryGetProperty("Title", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(title)) return;

            var color = Vector4.One;
            if (root.TryGetProperty("Color", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                color = new Vector4(
                    (c.TryGetProperty("X", out var cx) || c.TryGetProperty("x", out cx)) ? cx.GetSingle() : 1f,
                    (c.TryGetProperty("Y", out var cy) || c.TryGetProperty("y", out cy)) ? cy.GetSingle() : 1f,
                    (c.TryGetProperty("Z", out var cz) || c.TryGetProperty("z", out cz)) ? cz.GetSingle() : 1f,
                    1f);
            }

            using var _ = _uiSharedService.GameFont.Push();
            ImGui.TextColored(color, title);
        }
        catch { /* ignore parse errors */ }
    }

    private string _localHonorificB64 = string.Empty;

    private async Task RefreshLocalHonorificAsync()
    {
        if (!_ipcManager.Honorific.APIAvailable) return;
        try
        {
            _localHonorificB64 = await _ipcManager.Honorific.GetTitle().ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    private static string[] CategoryNames =>
    [
        Loc.Get("Establishment.Category.Tavern"), Loc.Get("Establishment.Category.Shop"),
        Loc.Get("Establishment.Category.Temple"), Loc.Get("Establishment.Category.Academy"),
        Loc.Get("Establishment.Category.Guild"), Loc.Get("Establishment.Category.Residence"),
        Loc.Get("Establishment.Category.Workshop"), Loc.Get("Establishment.Category.Other")
    ];

    private List<(EstablishmentProfileEntryDto Entry, IDalamudTextureWrap? Logo)> _profileEstablishments = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<List<(EstablishmentProfileEntryDto Entry, IDalamudTextureWrap? Logo)>> _staleProfileEstablishments = new();
    private string? _profileEstablishmentsKey;
    private int _profileEstablishmentsRequest;

    // Établissements gérés par le perso affiché ou auxquels il est affilié, trois au plus, filtrés par le serveur
    // selon la visibilité de la fiche.
    private void DrawLinkedEstablishment(float cardSpacing)
    {
        // Les logos remplacés sont libérés ici, sur le thread de rendu, jamais pendant qu'une frame les dessine.
        while (_staleProfileEstablishments.TryDequeue(out var stale))
            DisposeLogos(stale);

        var (charName, worldId) = (_selectedAltCharName != null && _selectedAltWorldId != null)
            ? (_selectedAltCharName, _selectedAltWorldId)
            : _umbraProfileManager.ResolveCharacter(Pair.UserData);
        var key = $"{charName}@{worldId}";
        if (!string.Equals(_profileEstablishmentsKey, key, StringComparison.Ordinal))
        {
            _profileEstablishmentsKey = key;
            var requestId = Interlocked.Increment(ref _profileEstablishmentsRequest);
            _ = LoadProfileEstablishmentsAsync(charName, worldId, requestId);
        }

        var entries = _profileEstablishments;
        if (entries.Count == 0) return;

        ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        UiSharedService.DrawCard("rp-establishment-card", () =>
        {
            DrawSectionTitle(Loc.Get(entries.Count > 1 ? "Profile.Establishment.TitlePlural" : "Profile.Establishment.Title"));
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) ImGuiHelpers.ScaledDummy(6f);
                using var id = ImRaii.PushId(i);
                DrawProfileEstablishment(entries[i].Entry, entries[i].Logo);
            }
        }, stretchWidth: true, background: SheetCardFill, border: SheetCardFill);
    }

    private void DrawProfileEstablishment(EstablishmentProfileEntryDto establishment, IDalamudTextureWrap? logo)
    {
        var catNames = CategoryNames;
        var catIndex = establishment.Category;
        var catName = catIndex >= 0 && catIndex < catNames.Length ? catNames[catIndex] : "?";

        // Logo dessiné en absolu (substitut s'il manque), le contenu s'écoule à sa droite.
        var scale = ImGuiHelpers.GlobalScale;
        var logoSize = 44f * scale;
        var logoRounding = 6f * scale;
        var logoSpacing = 10f * scale;
        var linkedScreenPos = ImGui.GetCursorScreenPos();
        var linkedLocalPos = ImGui.GetCursorPos();
        if (logo != null)
        {
            ImGui.GetWindowDrawList().AddImageRounded(logo.Handle,
                linkedScreenPos, linkedScreenPos + new Vector2(logoSize, logoSize),
                Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), logoRounding);
        }
        else
        {
            UiSharedService.DrawLogoPlaceholder(linkedScreenPos, logoSize, logoRounding, FontAwesomeIcon.Building);
        }

        var contentX = linkedLocalPos.X + logoSize + logoSpacing;
        ImGui.SetCursorPos(new Vector2(contentX, linkedLocalPos.Y));
        _uiSharedService.BigText(UiSharedService.SanitizeOneLine(establishment.Name));

        // DrawCategoryPill laisse le curseur sur sa ligne, calé sur son texte : la seconde pastille repart du haut
        // de la première, et la ligne est terminée avant la description.
        ImGui.SetCursorPosX(contentX);
        var pillsY = ImGui.GetCursorPosY();
        UiSharedService.DrawCategoryPill(catName);
        if (establishment.IsManager)
        {
            ImGui.SetCursorPosY(pillsY);
            UiSharedService.DrawCategoryPill(Loc.Get("Profile.Establishment.Manager"));
        }
        ImGui.NewLine();

        var desc = UiSharedService.SanitizeOneLine(establishment.Description);
        if (!string.IsNullOrEmpty(desc))
        {
            ImGui.SetCursorPosX(contentX);
            var descMaxX = ImGui.GetWindowContentRegionMax().X - 4f * scale;
            var descAvail = MathF.Max(40f, descMaxX - ImGui.GetCursorPosX());
            ImGui.TextDisabled(UiSharedService.TruncateToWidth(desc, descAvail));
        }

        ImGui.SetCursorPosX(contentX);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, Loc.Get("Profile.Establishment.View")))
            Mediator.Publish(new OpenEstablishmentDetailMessage(establishment.Id));
    }

    private async Task LoadProfileEstablishmentsAsync(string? charName, uint? worldId, int requestId)
    {
        var loaded = new List<(EstablishmentProfileEntryDto Entry, IDalamudTextureWrap? Logo)>();
        try
        {
            var entries = string.IsNullOrEmpty(charName) || worldId is not > 0
                ? []
                : await _apiController.EstablishmentGetForCharacter(new UserDto(Pair.UserData, charName, worldId)).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                IDalamudTextureWrap? logo = null;
                if (entry.LogoImageBase64 is { Length: > 0 } logoB64)
                {
                    try { logo = await _uiSharedService.LoadImageOrEmptyAsync(Convert.FromBase64String(logoB64)).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to load an establishment logo for {uid}", Pair.UserData.UID); }
                }
                loaded.Add((entry, logo));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading establishments for a character of {uid}", Pair.UserData.UID);
        }

        if (requestId != Volatile.Read(ref _profileEstablishmentsRequest))
        {
            DisposeLogos(loaded);
            return;
        }

        _staleProfileEstablishments.Enqueue(Interlocked.Exchange(ref _profileEstablishments, loaded));
    }

    private static void DisposeLogos(List<(EstablishmentProfileEntryDto Entry, IDalamudTextureWrap? Logo)> entries)
    {
        foreach (var (_, logo) in entries)
            logo?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeLogos(Interlocked.Exchange(ref _profileEstablishments, []));
            while (_staleProfileEstablishments.TryDequeue(out var stale))
                DisposeLogos(stale);
        }

        base.Dispose(disposing);
    }

    private void DrawHeroCard(UmbraProfileData profile, IDalamudTextureWrap? texture, Vector4 accent)
    {
        // Captured inside card callback for moodle positioning after card
        Vector2 nameLineScreen = Vector2.Zero;

        UiSharedService.DrawCard("rp-hero-card", () =>
        {
            var portraitSize = 120f * ImGuiHelpers.GlobalScale;
            var drawList = ImGui.GetWindowDrawList();

            DrawPortrait(drawList, texture, portraitSize);

            // Reserve space for portrait
            ImGui.Dummy(new Vector2(portraitSize, portraitSize));
            ImGui.SameLine();

            // Right side: name, title, race, status
            ImGui.BeginGroup();

            // Full name
            var firstName = profile.RpFirstName ?? string.Empty;
            var lastName = profile.RpLastName ?? string.Empty;
            var fullName = $"{firstName} {lastName}".Trim();
            if (string.IsNullOrEmpty(fullName))
                fullName = Pair.UserData.AliasOrUID;

            // Capture screen position of name line for moodles
            nameLineScreen = ImGui.GetCursorScreenPos();

            var nameColor = _configService.Current.UseRpNameColors && !string.IsNullOrEmpty(profile.RpNameColor) ? UiSharedService.HexToVector4(profile.RpNameColor) : accent;

            // Name with title as prefix
            var displayName = !string.IsNullOrEmpty(profile.RpTitle)
                ? $"{profile.RpTitle} {fullName}"
                : fullName;

            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(displayName, nameColor);

            // Honorific title
            DrawHonorificInHero();

            // Race · Ethnicity
            var race = profile.RpRace ?? string.Empty;
            var ethnicity = profile.RpEthnicity ?? string.Empty;
            var raceEthnicity = !string.IsNullOrEmpty(race) && !string.IsNullOrEmpty(ethnicity)
                ? $"{race} · {ethnicity}"
                : !string.IsNullOrEmpty(race) ? race : ethnicity;
            if (!string.IsNullOrEmpty(raceEthnicity))
                ImGui.TextColored(UiSharedService.ProfileCardTextColor(profile.CardStyle.Value.TextColor), raceEthnicity);

            // Status
            ImGuiHelpers.ScaledDummy(2f);
            var (statusText, statusColor, isOnline, isSelf) = ResolvePresence();

            // Status pill (online / offline / visible) + RP level pill on the same row
            var statusIcon = !isOnline ? FontAwesomeIcon.CircleNotch
                : (Pair.IsVisible && !isSelf ? FontAwesomeIcon.Eye : FontAwesomeIcon.Circle);
            DrawColoredPill(statusIcon, statusText, statusColor);

            var rpStyle = GetRpLevelStyle(profile.RpLevel);
            if (rpStyle.HasValue)
                DrawColoredPill(rpStyle.Value.Icon, rpStyle.Value.Label, rpStyle.Value.Color, rpStyle.Value.Tooltip);

            // End the row of pills
            ImGui.NewLine();

            // Note
            var note = _serverManager.GetNoteForUid(Pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
                ImGui.TextColored(ImGuiColors.DalamudGrey, note);

            ImGui.EndGroup();
        }, stretchWidth: true, background: HeroCardFill, border: HeroCardBorder, backgroundOverlay: _rpBannerTexture is { } banner
            ? (min, max, rounding, background) => ProfileBanner.Draw(ImGui.GetWindowDrawList(), banner, min, max, background, rounding, profile.CardStyle.Value)
            : null);

        // Draw moodles AFTER card (on top), right-aligned on same line as name
        DrawMoodlesOnNameLine(nameLineScreen, profile);
    }

    private void DrawMoodlesOnNameLine(Vector2 nameLineScreen, UmbraProfileData profile)
    {
        var moodlesJson = GetMoodlesJson(profile);
        if (string.IsNullOrEmpty(moodlesJson)) return;

        var moodles = MoodleStatusInfo.ParseMoodles(moodlesJson);
        if (moodles.Count == 0) return;

        const float iconHeight = 40f;
        var scaledHeight = iconHeight * ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var textureProvider = _uiSharedService.TextureProvider;
        var drawList = ImGui.GetWindowDrawList();

        var items = new List<(MoodleStatusInfo moodle, ImTextureID handle, Vector2 size)>();
        float totalWidth = 0f;
        foreach (var moodle in moodles)
        {
            if (moodle.IconID <= 0) continue;
            var wrap = textureProvider.GetFromGameIcon(new GameIconLookup((uint)moodle.IconID)).GetWrapOrEmpty();
            if (wrap.Handle == IntPtr.Zero) continue;
            var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
            var displaySize = new Vector2(scaledHeight * aspect, scaledHeight);
            items.Add((moodle, wrap.Handle, displaySize));
            totalWidth += displaySize.X;
        }
        if (items.Count == 0) return;

        totalWidth += (items.Count - 1) * spacing;

        // Right-aligned, vertically centered on name line
        var windowPos = ImGui.GetWindowPos();
        var regionMax = ImGui.GetWindowContentRegionMax();
        var scrollX = ImGui.GetScrollX();
        var cardPad = ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale;
        float rightEdge = windowPos.X + regionMax.X + scrollX - cardPad;
        float curX = rightEdge - totalWidth;
        float curY = nameLineScreen.Y;

        for (int i = 0; i < items.Count; i++)
        {
            var (moodle, handle, size) = items[i];
            var pMin = new Vector2(curX, curY);
            var pMax = new Vector2(curX + size.X, curY + size.Y);

            drawList.AddImage(handle, pMin, pMax);

            if (ImGui.IsMouseHoveringRect(pMin, pMax))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20f);
                var title = moodle.CleanTitle;
                if (!string.IsNullOrEmpty(title))
                {
                    var typeColor = moodle.Type switch
                    {
                        0 => new Vector4(0.4f, 0.9f, 0.4f, 1f),
                        1 => new Vector4(0.9f, 0.4f, 0.4f, 1f),
                        _ => new Vector4(0.5f, 0.6f, 1f, 1f),
                    };
                    ImGui.TextColored(typeColor, title);
                }
                var desc = moodle.CleanDescription;
                if (!string.IsNullOrEmpty(desc))
                    ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            curX += size.X + spacing;
        }
    }

    private void DrawHrpProfile(UmbraProfileData profile, IDalamudTextureWrap? texture, Vector4 accent)
    {
        UiSharedService.DrawCard("hrp-card", () =>
        {
            var portraitSize = 120f * ImGuiHelpers.GlobalScale;
            var drawList = ImGui.GetWindowDrawList();
            var wrapWidth = ImGui.GetContentRegionAvail().X;

            DrawPortrait(drawList, texture, portraitSize);

            ImGui.Dummy(new Vector2(portraitSize, portraitSize));
            ImGui.SameLine();

            // Right side: name, status, description
            var rightWidth = wrapWidth - portraitSize - ImGui.GetStyle().ItemSpacing.X;

            ImGui.BeginGroup();

            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(Pair.UserData.AliasOrUID, accent);

            var (statusText, statusColor, isOnline, isSelf) = ResolvePresence();

            ImGuiHelpers.ScaledDummy(2f);
            var statusIconAlt = !isOnline ? FontAwesomeIcon.CircleNotch
                : (Pair.IsVisible && !isSelf ? FontAwesomeIcon.Eye : FontAwesomeIcon.Circle);
            DrawColoredPill(statusIconAlt, statusText, statusColor);

            var rpStyleAlt = GetRpLevelStyle(profile.RpLevel);
            if (rpStyleAlt.HasValue)
                DrawColoredPill(rpStyleAlt.Value.Icon, rpStyleAlt.Value.Label, rpStyleAlt.Value.Color, rpStyleAlt.Value.Tooltip);

            ImGui.NewLine();

            var note = _serverManager.GetNoteForUid(Pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
                ImGui.TextColored(ImGuiColors.DalamudGrey, note);

            // Description on the right
            var description = profile.Description;
            if (!string.IsNullOrEmpty(description))
            {
                ImGuiHelpers.ScaledDummy(2f);
                using var _ = _uiSharedService.GameFont.Push();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + rightWidth);
                ImGui.TextUnformatted(description);
                ImGui.PopTextWrapPos();
            }
            else
            {
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("UserProfile.NoHrpProfile"));
            }

            ImGui.EndGroup();
        }, stretchWidth: true);
    }

    private string? GetMoodlesJson(UmbraProfileData profile)
    {
        bool isSelfProfile = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);
        if (isSelfProfile)
        {
            if (!IsDisplayingOwnLiveCharacter()) return profile.MoodlesData;
            if (string.IsNullOrEmpty(_localMoodlesJson) && !_moodlesFetching
                && (DateTime.UtcNow - _lastMoodlesFetch).TotalSeconds > 3)
            {
#pragma warning disable MA0134
                Task.Run(RefreshLocalMoodlesAsync);
#pragma warning restore MA0134
            }
            return _localMoodlesJson;
        }
        else
        {
            if (IsDisplayingLiveCharacter())
            {
                var realtimeMoodles = Pair.LastReceivedCharacterData?.MoodlesData;
                if (!string.IsNullOrEmpty(realtimeMoodles))
                    return realtimeMoodles;
            }

            return profile.MoodlesData;
        }
    }

    // Les données reçues en direct (Honorific, Moodles) appartiennent au personnage connecté :
    // ne les afficher que si c'est bien lui que montre la fiche, jamais sur un autre perso du compte.
    private bool IsDisplayingLiveCharacter()
    {
        if (!Pair.IsOnline) return false;

        var liveName = Pair.PlayerName;
        var liveWorld = Pair.WorldId;
        if (string.IsNullOrEmpty(liveName) || liveWorld == 0) return false;

        var (charName, worldId) = (_selectedAltCharName != null && _selectedAltWorldId != null)
            ? (_selectedAltCharName, _selectedAltWorldId)
            : _umbraProfileManager.ResolveCharacter(Pair.UserData);

        return string.Equals(charName, liveName, StringComparison.Ordinal) && worldId == liveWorld;
    }

    // Même règle pour sa propre fiche : Honorific et Moodles locaux sont ceux du perso joué.
    private bool IsDisplayingOwnLiveCharacter()
    {
        if (_selectedAltCharName == null || _selectedAltWorldId == null) return true;
        var (liveName, liveWorld) = _umbraProfileManager.ResolveCharacter(Pair.UserData);
        return string.Equals(_selectedAltCharName, liveName, StringComparison.Ordinal) && _selectedAltWorldId == liveWorld;
    }

    private Vector4? SheetCardFill => _sheetPalette != null ? Vector4.Zero : null;

    private Vector4? HeroCardFill => _sheetPalette is { } palette
        ? (_rpBannerTexture == null ? Vector4.Zero : palette.Veil)
        : null;

    private Vector4? HeroCardBorder => _sheetPalette is { } palette ? palette.Accent with { W = ProfileSheetBackground.BorderOpacity } : null;

    private void DrawSectionTitle(string text)
    {
        if (_sheetPalette is { } palette)
        {
            ProfileSheetBackground.DrawHeading(text, palette, SectionContentWidth);
            return;
        }
        UiSharedService.ColorText(text, ProfileSheetBackground.CustomColor(_rpCardStyle.SheetTitleColor) ?? UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawVerticalField(string label, string value, float width)
    {
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 8f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(FieldLabelColor, label);
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
    }

    private Vector4 FieldLabelColor
        => _sheetPalette?.Label ?? ProfileSheetBackground.CustomColor(_rpCardStyle.SheetLabelColor) ?? ImGuiColors.DalamudGrey;

    // Texte libre en retrait des bords, dans l'axe des filets du titre orné.
    private static void DrawSectionText(string bbcode, float width)
    {
        var inset = MathF.Round(width * 0.04f);
        ImGui.Indent(inset);
        BbCodeRenderer.Render(bbcode, width - (inset * 2f));
        ImGui.Unindent(inset);
    }

    private static float SectionContentWidth
        => ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale);

    private void DrawCenteredRow((string Label, string Value)? left, (string Label, string Value)? right, float col1X, float col2X, float width, bool justifyValues = false)
    {
        if (left is { } l)
        {
            ImGui.BeginGroup();
            DrawCenteredField(l.Label, l.Value, col1X, width, justifyValues);
            ImGui.EndGroup();
        }
        if (right is not { } r) return;
        if (left != null) ImGui.SameLine();
        ImGui.BeginGroup();
        DrawCenteredField(r.Label, r.Value, col2X, width, justifyValues);
        ImGui.EndGroup();
    }

    private void DrawCenteredField(string label, string value, float columnX, float width, bool justifyValue = false)
    {
        var pad = 4f * ImGuiHelpers.GlobalScale;
        var innerWidth = width - (pad * 2f);
        DrawCenteredLine(label, FieldLabelColor, columnX + pad, innerWidth);

        // Une valeur courte reste centrée sous son libellé ; plus longue, elle se lit comme un paragraphe justifié.
        if (justifyValue && ImGui.CalcTextSize(value).X > innerWidth)
        {
            ImGui.SetCursorPosX(columnX + pad);
            BbCodeRenderer.RenderJustified(value, innerWidth);
            return;
        }
        DrawCenteredLine(value, null, columnX + pad, innerWidth);
    }

    private static void DrawCenteredLine(string text, Vector4? color, float columnX, float width)
    {
        ImGui.SetCursorPosX(columnX + MathF.Max((width - ImGui.CalcTextSize(text).X) * 0.5f, 0f));
        ImGui.PushTextWrapPos(columnX + width);
        if (color is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static (string Label, Vector4 Color, FontAwesomeIcon Icon, string Tooltip)? GetRpLevelStyle(byte level) => level switch
    {
        1 => (Loc.Get("UserProfile.RpLevel.Beginner"), new Vector4(0.55f, 0.85f, 0.55f, 1f), FontAwesomeIcon.Seedling, Loc.Get("UserProfile.RpLevel.Beginner.Tooltip")),
        2 => (Loc.Get("UserProfile.RpLevel.Regular"), new Vector4(0.55f, 0.75f, 1f, 1f), FontAwesomeIcon.Tree, Loc.Get("UserProfile.RpLevel.Regular.Tooltip")),
        3 => (Loc.Get("UserProfile.RpLevel.Mentor"), new Vector4(1f, 0.75f, 0.4f, 1f), FontAwesomeIcon.Crown, Loc.Get("UserProfile.RpLevel.Mentor.Tooltip")),
        _ => null,
    };

    private static void DrawColoredPill(FontAwesomeIcon icon, string label, Vector4 color, string? tooltip = null)
    {
        const float iconFontScale = 0.75f;

        float iconWidth;
        float iconHeight;
        ImGui.SetWindowFontScale(iconFontScale);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconSize = ImGui.CalcTextSize(icon.ToIconString());
            iconWidth = iconSize.X;
            iconHeight = iconSize.Y;
        }
        ImGui.SetWindowFontScale(1f);

        var labelSize = ImGui.CalcTextSize(label);
        var padX = 5f * ImGuiHelpers.GlobalScale;
        var padY = 1f * ImGuiHelpers.GlobalScale;
        var iconLabelSpacing = 3f * ImGuiHelpers.GlobalScale;
        var rounding = 3f * ImGuiHelpers.GlobalScale;
        var totalSize = new Vector2(iconWidth + iconLabelSpacing + labelSize.X + padX * 2,
            MathF.Max(iconHeight, labelSize.Y) + padY * 2);

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + totalSize;
        var bg = color; bg.W = 0.18f;
        var border = color; border.W = 0.55f;
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), rounding);
        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(border), rounding);

        // Vertically center the (smaller) icon against the label.
        var iconY = min.Y + padY + (labelSize.Y - iconHeight) / 2f;
        ImGui.SetCursorScreenPos(new Vector2(min.X + padX, iconY));
        ImGui.SetWindowFontScale(iconFontScale);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SetWindowFontScale(1f);

        ImGui.SetCursorScreenPos(new Vector2(min.X + padX + iconWidth + iconLabelSpacing, min.Y + padY));
        ImGui.TextColored(color, label);

        // Tooltip on hover (manual hit-test since the pill is drawn via DrawList, not as an ImGui item)
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsMouseHoveringRect(min, max))
            ImGui.SetTooltip(tooltip);

        // Advance cursor to the right of the pill so callers can chain another pill via implicit SameLine layout.
        ImGui.SetCursorScreenPos(new Vector2(max.X + ImGui.GetStyle().ItemSpacing.X, min.Y));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    private void OpenEnrichedProfile()
    {
        var charName = !string.IsNullOrEmpty(_selectedAltCharName) ? _selectedAltCharName : Pair.PlayerName;
        uint worldId = _selectedAltWorldId is { } w && w != 0 ? w : Pair.WorldId;

        // Paire hors zone : PlayerName/WorldId ne sont pas connus localement. On retombe sur
        // le dernier personnage vu pour cet UID, jamais sur un autre personnage du compte.
        if (string.IsNullOrEmpty(charName) || worldId == 0)
        {
            charName = _serverManager.GetNameForUid(Pair.UserData.UID);
            worldId = _serverManager.GetWorldIdForUid(Pair.UserData.UID) ?? 0;
        }

        if (string.IsNullOrEmpty(charName) || worldId == 0)
        {
            Mediator.Publish(new NotificationMessage("Profil enrichi",
                "Impossible de déterminer le personnage ou le monde pour construire l'URL.",
                MareConfiguration.Models.NotificationType.Warning, TimeSpan.FromSeconds(4)));
            return;
        }

        var worldData = _dalamudUtil.WorldData.Value;
        var worldName = worldData.GetValueOrDefault((ushort)worldId, string.Empty);
        if (string.IsNullOrEmpty(worldName))
        {
            Mediator.Publish(new NotificationMessage("Profil enrichi",
                "Monde inconnu — impossible de construire l'URL.",
                MareConfiguration.Models.NotificationType.Warning, TimeSpan.FromSeconds(4)));
            return;
        }

        var url = $"https://hub.ashfall-codex.dev/{Slugify(worldName)}/{Slugify(charName)}";
        Dalamud.Utility.Util.OpenLink(url);
        Mediator.Publish(new NotificationMessage("Profil enrichi",
            "Ouverture dans votre navigateur web…",
            MareConfiguration.Models.NotificationType.Info, TimeSpan.FromSeconds(3)));
    }

    /// Slugify aligné sur celui de Connect (PublicProfileEndpoints.Slugify) : NFD-decompose,
    /// strip diacritiques, lowercase ; espaces / tirets / apostrophes → '-', tout le reste = drop.
    private static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '\'') sb.Append('-');
        }
        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}