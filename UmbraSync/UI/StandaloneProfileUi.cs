using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using System.Numerics;
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
    private readonly PairManager _pairManager;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastRpProfilePicture = [];
    private IDalamudTextureWrap? _textureWrap;
    private IDalamudTextureWrap? _rpTextureWrap;
    private bool _isRpTab = true;
    private bool _windowSizeInitialized = false;
    private string _localMoodlesJson = string.Empty;
    private bool _moodlesFetching;
    private DateTime _lastMoodlesFetch = DateTime.MinValue;
    private string? _selectedAltCharName;
    private uint? _selectedAltWorldId;

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, MareMediator mediator, UiSharedService uiBuilder,
        ServerConfigurationManager serverManager, MareConfigService configService, UmbraProfileManager umbraProfileManager, ApiController apiController, Pair pair,
        PerformanceCollectorService performanceCollector, IpcManager ipcManager, DalamudUtilService dalamudUtil, PairManager pairManager)
        : base(logger, mediator, string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("StandaloneProfile.WindowTitle"), pair.UserData.AliasOrUID) + "##UmbraSyncStandaloneProfileUI" + pair.UserData.AliasOrUID, performanceCollector)
    {
        _uiSharedService = uiBuilder;
        _serverManager = serverManager;
        _configService = configService;
        _umbraProfileManager = umbraProfileManager;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        Pair = pair;
        Flags = ImGuiWindowFlags.None;

        SizeConstraints = new()
        {
            MinimumSize = new(650, 400),
            MaximumSize = new(1200, 2000)
        };

        bool isSelf = string.Equals(pair.UserData.UID, apiController.UID, StringComparison.Ordinal);
        if (isSelf)
        {
            Mediator.Subscribe<MoodlesMessage>(this, (msg) => _ = Task.Run(RefreshLocalMoodlesAsync));
        }

        IsOpen = true;
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

    protected override void DrawInternal()
    {
        try
        {
            if (!_windowSizeInitialized)
            {
                ImGui.SetWindowSize(new Vector2(800, 600));
                _windowSizeInitialized = true;
            }

            var umbraProfile = (_selectedAltCharName != null && _selectedAltWorldId != null)
                ? _umbraProfileManager.GetUmbraProfile(Pair.UserData, _selectedAltCharName, _selectedAltWorldId)
                : _umbraProfileManager.GetUmbraProfile(Pair.UserData);

            var accent = UiSharedService.AccentColor;
            if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

            // RP / HRP toggle buttons
            DrawTabButtons(accent);

            // Alt switcher (not self)
            if (!string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal))
                DrawAltSwitcher(accent);

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

            var currentTexture = _isRpTab ? _rpTextureWrap : _textureWrap;

            // Report button (not self)
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
                    DrawRpProfile(umbraProfile, currentTexture, accent);
                else
                    DrawHrpProfile(umbraProfile, currentTexture, accent);
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

        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

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

            var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
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

    private void DrawAltSwitcher(Vector4 accent)
    {
        var alts = _umbraProfileManager.GetEncounteredAlts(Pair.UserData.UID);
        if (alts.Count <= 1) return;

        var dl = ImGui.GetWindowDrawList();
        const float btnH = 24f;
        const float btnSpacing = 4f;
        const float rounding = 3f;
        const float padH = 8f;

        var borderColor = new Vector4(0.29f, 0.21f, 0.41f, 0.7f);
        var bgColor = new Vector4(0.11f, 0.11f, 0.11f, 0.9f);
        var hoverBg = new Vector4(0.17f, 0.13f, 0.22f, 1f);

        UiSharedService.ColorText(Loc.Get("AltSwitcher.Label"), ImGuiColors.DalamudGrey);
        ImGuiHelpers.ScaledDummy(2f);

        for (int i = 0; i < alts.Count; i++)
        {
            var (charName, worldId) = alts[i];
            bool isSelected = (_selectedAltCharName == null && i == 0)
                ? IsCurrentCharacter(charName, worldId)
                : string.Equals(_selectedAltCharName, charName, StringComparison.Ordinal) && _selectedAltWorldId == worldId;

            // Try to get display name from cached profile
            var cachedProfile = _umbraProfileManager.GetUmbraProfile(Pair.UserData, charName, worldId);
            var displayName = GetAltDisplayName(cachedProfile, charName);

            var textSize = ImGui.CalcTextSize(displayName);
            var btnW = textSize.X + padH * 2;

            if (i > 0) ImGui.SameLine(0, btnSpacing);

            var p = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton($"##alt_{i}", new Vector2(btnW, btnH));
            bool hovered = ImGui.IsItemHovered();

            var bg = isSelected ? accent : hovered ? hoverBg : bgColor;
            dl.AddRectFilled(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(bg), rounding);
            if (!isSelected)
                dl.AddRect(p, p + new Vector2(btnW, btnH), ImGui.GetColorU32(borderColor with { W = hovered ? 0.9f : 0.5f }), rounding);

            var textColor = isSelected ? new Vector4(1f, 1f, 1f, 1f) : hovered ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.7f, 0.65f, 0.8f, 1f);
            dl.AddText(new Vector2(p.X + padH, p.Y + (btnH - textSize.Y) / 2f), ImGui.GetColorU32(textColor), displayName);

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var worldName = _dalamudUtil.WorldData.Value.TryGetValue((ushort)worldId, out var wn) ? wn : worldId.ToString();
                ImGui.SetTooltip($"{charName} @ {worldName}");
            }

            if (clicked)
            {
                _selectedAltCharName = charName;
                _selectedAltWorldId = worldId;
                _lastProfilePicture = [];
                _lastRpProfilePicture = [];
                _textureWrap?.Dispose();
                _textureWrap = null;
                _rpTextureWrap?.Dispose();
                _rpTextureWrap = null;
            }
        }

        ImGuiHelpers.ScaledDummy(4f);
    }

    private bool IsCurrentCharacter(string charName, uint worldId)
    {
        var pair = _pairManager.GetPairByUID(Pair.UserData.UID);
        if (pair != null)
            return string.Equals(pair.PlayerName, charName, StringComparison.Ordinal) && pair.WorldId == worldId;
        var lastName = _serverManager.GetNameForUid(Pair.UserData.UID);
        var lastWorld = _serverManager.GetWorldIdForUid(Pair.UserData.UID);
        return string.Equals(lastName, charName, StringComparison.Ordinal) && lastWorld == worldId;
    }

    private static string GetAltDisplayName(UmbraProfileData profile, string charName)
    {
        var first = profile.RpFirstName ?? string.Empty;
        var last = profile.RpLastName ?? string.Empty;
        var rpName = $"{first} {last}".Trim();
        return !string.IsNullOrEmpty(rpName) ? rpName : charName;
    }

    private void DrawRpProfile(UmbraProfileData profile, IDalamudTextureWrap? texture, Vector4 accent)
    {
        var cardSpacing = 8f * ImGuiHelpers.GlobalScale;

        DrawHeroCard(profile, texture, accent);
        ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);

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

                var availW = ImGui.GetContentRegionAvail().X;
                int fieldCount = (hasAge ? 1 : 0) + (hasHeight ? 1 : 0) + (hasBuild ? 1 : 0);
                var colWidth = availW / Math.Max(fieldCount, 1);

                bool first = true;
                if (hasAge)
                {
                    DrawVerticalField(Loc.Get("UserProfile.RpAge"), profile.RpAge!, colWidth);
                    first = false;
                }
                if (hasHeight)
                {
                    if (!first) ImGui.SameLine();
                    DrawVerticalField(Loc.Get("UserProfile.RpHeight"), profile.RpHeight!, colWidth);
                    first = false;
                }
                if (hasBuild)
                {
                    if (!first) ImGui.SameLine();
                    DrawVerticalField(Loc.Get("UserProfile.RpBuild"), profile.RpBuild!, colWidth);
                }
            }, stretchWidth: true);
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

                var availW = ImGui.GetContentRegionAvail().X;
                var halfW = availW / 2f;
                var col1X = ImGui.GetCursorPosX();
                var col2X = col1X + halfW;

                // Row 1
                bool row1HasContent = hasResidence || hasOccupation;
                if (row1HasContent)
                {
                    if (hasResidence)
                        DrawVerticalField(Loc.Get("UserProfile.RpResidence"), profile.RpResidence!, halfW);
                    if (hasOccupation)
                    {
                        if (hasResidence) { ImGui.SameLine(); ImGui.SetCursorPosX(col2X); }
                        DrawVerticalField(Loc.Get("UserProfile.RpOccupation"), profile.RpOccupation!, halfW);
                    }
                }

                // Row 2
                bool row2HasContent = hasAffiliation || hasAlignment;
                if (row2HasContent)
                {
                    if (row1HasContent) ImGuiHelpers.ScaledDummy(4f);

                    if (hasAffiliation)
                        DrawVerticalField(Loc.Get("UserProfile.RpAffiliation"), profile.RpAffiliation!, halfW);
                    if (hasAlignment)
                    {
                        if (hasAffiliation) { ImGui.SameLine(); ImGui.SetCursorPosX(col2X); }
                        DrawVerticalField(Loc.Get("UserProfile.RpAlignment"), profile.RpAlignment!, halfW);
                    }
                }
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        if (profile.RpCustomFields is { Count: > 0 })
        {
            UiSharedService.DrawCard("rp-custom-fields-card", () =>
            {
                DrawSectionTitle(Loc.Get("UserProfile.RpCustomFields"));
                var availW = ImGui.GetContentRegionAvail().X;
                var halfW = availW / 2f;
                var col1X = ImGui.GetCursorPosX();
                var col2X = col1X + halfW;
                var sorted = profile.RpCustomFields.OrderBy(f => f.Order).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    var field = sorted[i];
                    if (string.IsNullOrEmpty(field.Name) && string.IsNullOrEmpty(field.Value)) continue;
                    bool isEven = (i % 2 == 0);
                    if (isEven && i > 0) ImGuiHelpers.ScaledDummy(4f);
                    if (!isEven) { ImGui.SameLine(); ImGui.SetCursorPosX(col2X); }
                    DrawVerticalField(field.Name, field.Value, halfW);
                }
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        var description = profile.RpDescription;
        if (!string.IsNullOrEmpty(description))
        {
            UiSharedService.DrawCard("rp-description-card", () =>
            {
                var wrapWidth = ImGui.GetContentRegionAvail().X;
                DrawSectionTitle(Loc.Get("UserProfile.RpDescription"));
                using var _ = _uiSharedService.GameFont.Push();
                BbCodeRenderer.Render(description, wrapWidth);
            }, stretchWidth: true);
            ImGuiHelpers.ScaledDummy(cardSpacing / ImGuiHelpers.GlobalScale);
        }

        if (!string.IsNullOrEmpty(profile.RpAdditionalInfo))
        {
            UiSharedService.DrawCard("rp-additional-card", () =>
            {
                var wrapWidth = ImGui.GetContentRegionAvail().X;
                DrawSectionTitle(Loc.Get("UserProfile.RpAdditionalInfo"));
                using var _ = _uiSharedService.GameFont.Push();
                BbCodeRenderer.Render(profile.RpAdditionalInfo, wrapWidth);
            }, stretchWidth: true);
        }
    }

    private void DrawHeroCard(UmbraProfileData profile, IDalamudTextureWrap? texture, Vector4 accent)
    {
        // Captured inside card callback for moodle positioning after card
        Vector2 nameLineScreen = Vector2.Zero;
        float cardContentWidth = 0f;

        UiSharedService.DrawCard("rp-hero-card", () =>
        {
            var portraitSize = 120f * ImGuiHelpers.GlobalScale;
            var drawList = ImGui.GetWindowDrawList();
            cardContentWidth = ImGui.GetContentRegionAvail().X;

            // Portrait
            var portraitStart = ImGui.GetCursorScreenPos();
            if (texture != null)
            {
                bool tallerThanWide = texture.Height >= texture.Width;
                var stretchFactor = tallerThanWide ? portraitSize / texture.Height : portraitSize / texture.Width;
                var newWidth = texture.Width * stretchFactor;
                var newHeight = texture.Height * stretchFactor;
                var offsetX = (portraitSize - newWidth) / 2f;
                var offsetY = (portraitSize - newHeight) / 2f;

                var pMin = new Vector2(portraitStart.X + offsetX, portraitStart.Y + offsetY);
                var pMax = new Vector2(pMin.X + newWidth, pMin.Y + newHeight);
                var rounding = 10f * ImGuiHelpers.GlobalScale;

                drawList.AddImageRounded(texture.Handle, pMin, pMax,
                    Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), rounding);
            }
            else
            {
                var rounding = 10f * ImGuiHelpers.GlobalScale;
                drawList.AddRectFilled(portraitStart,
                    new Vector2(portraitStart.X + portraitSize, portraitStart.Y + portraitSize),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), rounding);
            }

            // Reserve space for portrait
            ImGui.Dummy(new Vector2(portraitSize, portraitSize));
            ImGui.SameLine();

            // Right side: name, title, race, status
            ImGui.BeginGroup();

            // Full name (violet)
            var firstName = profile.RpFirstName ?? string.Empty;
            var lastName = profile.RpLastName ?? string.Empty;
            var fullName = $"{firstName} {lastName}".Trim();
            if (string.IsNullOrEmpty(fullName))
                fullName = Pair.UserData.AliasOrUID;

            // Capture screen position of name line for moodles
            nameLineScreen = ImGui.GetCursorScreenPos();

            var nameColor = _configService.Current.UseRpNameColors && !string.IsNullOrEmpty(profile.RpNameColor) ? UiSharedService.HexToVector4(profile.RpNameColor) : accent;

            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(fullName, nameColor);

            // Title
            if (!string.IsNullOrEmpty(profile.RpTitle))
            {
                using var _ = _uiSharedService.GameFont.Push();
                UiSharedService.ColorText(profile.RpTitle, nameColor);
            }

            // Race · Ethnicity
            var race = profile.RpRace ?? string.Empty;
            var ethnicity = profile.RpEthnicity ?? string.Empty;
            var raceEthnicity = !string.IsNullOrEmpty(race) && !string.IsNullOrEmpty(ethnicity)
                ? $"{race} · {ethnicity}"
                : !string.IsNullOrEmpty(race) ? race : ethnicity;
            if (!string.IsNullOrEmpty(raceEthnicity))
                ImGui.TextColored(ImGuiColors.DalamudGrey, raceEthnicity);

            // Status
            ImGuiHelpers.ScaledDummy(2f);
            bool isSelf = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);
            string statusText;
            Vector4 statusColor;
            bool isOnline;

            if (isSelf)
            {
                isOnline = _apiController.IsConnected;
                statusText = isOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline");
                statusColor = isOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            }
            else
            {
                isOnline = Pair.IsVisible || Pair.IsOnline;
                statusText = Pair.IsVisible
                    ? Loc.Get("StandaloneProfile.Status.Visible")
                    : (Pair.IsOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline"));
                statusColor = isOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            }

            // Status dot + text
            var dotPos = ImGui.GetCursorScreenPos();
            var dotRadius = 4f * ImGuiHelpers.GlobalScale;
            var textHeight = ImGui.GetTextLineHeight();
            drawList.AddCircleFilled(
                new Vector2(dotPos.X + dotRadius, dotPos.Y + textHeight / 2f),
                dotRadius,
                ImGui.GetColorU32(statusColor));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + dotRadius * 2f + 6f * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(statusColor, statusText);

            // Note
            var note = _serverManager.GetNoteForUid(Pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
                ImGui.TextColored(ImGuiColors.DalamudGrey, note);

            ImGui.EndGroup();
        }, stretchWidth: true);

        // Draw moodles AFTER card (on top), right-aligned on same line as name
        DrawMoodlesOnNameLine(nameLineScreen);
    }

    private void DrawMoodlesOnNameLine(Vector2 nameLineScreen)
    {
        var moodlesJson = GetMoodlesJson();
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

            // Portrait
            var portraitStart = ImGui.GetCursorScreenPos();
            if (texture != null)
            {
                bool tallerThanWide = texture.Height >= texture.Width;
                var stretchFactor = tallerThanWide ? portraitSize / texture.Height : portraitSize / texture.Width;
                var newWidth = texture.Width * stretchFactor;
                var newHeight = texture.Height * stretchFactor;
                var offsetX = (portraitSize - newWidth) / 2f;
                var offsetY = (portraitSize - newHeight) / 2f;

                var pMin = new Vector2(portraitStart.X + offsetX, portraitStart.Y + offsetY);
                var pMax = new Vector2(pMin.X + newWidth, pMin.Y + newHeight);
                var rounding = 10f * ImGuiHelpers.GlobalScale;

                drawList.AddImageRounded(texture.Handle, pMin, pMax,
                    Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), rounding);
            }
            else
            {
                var rounding = 10f * ImGuiHelpers.GlobalScale;
                drawList.AddRectFilled(portraitStart,
                    new Vector2(portraitStart.X + portraitSize, portraitStart.Y + portraitSize),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)), rounding);
            }

            ImGui.Dummy(new Vector2(portraitSize, portraitSize));
            ImGui.SameLine();

            // Right side: name, status, description
            var rightWidth = wrapWidth - portraitSize - ImGui.GetStyle().ItemSpacing.X;

            ImGui.BeginGroup();

            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(Pair.UserData.AliasOrUID, accent);

            bool isSelf = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);
            string statusText;
            Vector4 statusColor;

            if (isSelf)
            {
                bool isOnline = _apiController.IsConnected;
                statusText = isOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline");
                statusColor = isOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            }
            else
            {
                bool isOnline = Pair.IsVisible || Pair.IsOnline;
                statusText = Pair.IsVisible
                    ? Loc.Get("StandaloneProfile.Status.Visible")
                    : (Pair.IsOnline ? Loc.Get("StandaloneProfile.Status.Online") : Loc.Get("StandaloneProfile.Status.Offline"));
                statusColor = isOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            }

            ImGuiHelpers.ScaledDummy(2f);
            var dotPos = ImGui.GetCursorScreenPos();
            var dotRadius = 4f * ImGuiHelpers.GlobalScale;
            var textHeight = ImGui.GetTextLineHeight();
            drawList.AddCircleFilled(
                new Vector2(dotPos.X + dotRadius, dotPos.Y + textHeight / 2f),
                dotRadius,
                ImGui.GetColorU32(statusColor));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + dotRadius * 2f + 6f * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(statusColor, statusText);

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

            ImGui.EndGroup();
        }, stretchWidth: true);
    }

    private string? GetMoodlesJson()
    {
        bool isSelfProfile = string.Equals(Pair.UserData.UID, _apiController.UID, StringComparison.Ordinal);
        if (isSelfProfile)
        {
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
            var realtimeMoodles = Pair.LastReceivedCharacterData?.MoodlesData;
            if (!string.IsNullOrEmpty(realtimeMoodles))
                return realtimeMoodles;

            return _umbraProfileManager.GetUmbraProfile(Pair.UserData).MoodlesData;
        }
    }

    private static void DrawSectionTitle(string text)
    {
        UiSharedService.ColorText(text, UiSharedService.AccentColor);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private static void DrawVerticalField(string label, string value, float width)
    {
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 8f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}