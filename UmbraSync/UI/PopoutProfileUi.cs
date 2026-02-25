using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text;
using UmbraSync.API.Data.Extensions;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI.Components;

namespace UmbraSync.UI;

public class PopoutProfileUi : WindowMediatorSubscriberBase
{
    private readonly UmbraProfileManager _umbraProfileManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly MareConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private Vector2 _lastMainPos = Vector2.Zero;
    private Vector2 _lastMainSize = Vector2.Zero;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastRpProfilePicture = [];
    private Pair? _pair;
    private IDalamudTextureWrap? _supporterTextureWrap;
    private IDalamudTextureWrap? _textureWrap;
    private IDalamudTextureWrap? _rpTextureWrap;
    private bool _isRpTab;
    private string? _selectedAltCharName;
    private uint? _selectedAltWorldId;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, MareMediator mediator, UiSharedService uiSharedService,
        ServerConfigurationManager serverManager, MareConfigService mareConfigService,
        UmbraProfileManager umbraProfileManager, PerformanceCollectorService performanceCollectorService,
        DalamudUtilService dalamudUtil, PairManager pairManager) : base(logger, mediator, "###UmbraSyncPopoutProfileUI", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _serverManager = serverManager;
        _configService = mareConfigService;
        _umbraProfileManager = umbraProfileManager;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        Flags = ImGuiWindowFlags.NoDecoration;

        Mediator.Subscribe<ProfilePopoutToggle>(this, (msg) =>
        {
            IsOpen = msg.Pair != null;
            _pair = msg.Pair;
            _lastProfilePicture = [];
            _lastRpProfilePicture = [];
            _textureWrap?.Dispose();
            _textureWrap = null;
            _rpTextureWrap?.Dispose();
            _rpTextureWrap = null;
            _supporterTextureWrap?.Dispose();
            _supporterTextureWrap = null;
            _isRpTab = false;
            _selectedAltCharName = null;
            _selectedAltWorldId = null;
        });

        Mediator.Subscribe<CompactUiChange>(this, (msg) =>
        {
            if (msg.Size != Vector2.Zero)
            {
                var border = ImGui.GetStyle().WindowBorderSize;
                var padding = ImGui.GetStyle().WindowPadding;
                Size = new(256 + (padding.X * 2) + border, msg.Size.Y / ImGuiHelpers.GlobalScale);
                _lastMainSize = msg.Size;
            }
            var mainPos = msg.Position == Vector2.Zero ? _lastMainPos : msg.Position;
            if (mareConfigService.Current.ProfilePopoutRight)
            {
                Position = new(mainPos.X + _lastMainSize.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }
            else
            {
                Position = new(mainPos.X - Size!.Value.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }

            if (msg.Position != Vector2.Zero)
            {
                _lastMainPos = msg.Position;
            }
        });

        IsOpen = false;
    }

    protected override void DrawInternal()
    {
        if (_pair == null) return;

        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            var umbraProfile = (_selectedAltCharName != null && _selectedAltWorldId != null)
                ? _umbraProfileManager.GetUmbraProfile(_pair.UserData, _selectedAltCharName, _selectedAltWorldId)
                : _umbraProfileManager.GetUmbraProfile(_pair.UserData);

            var accent = UiSharedService.AccentColor;
            if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;
            using (var topTabHoverColor = ImRaii.PushColor(ImGuiCol.TabHovered, accent))
            using (var topTabActiveColor = ImRaii.PushColor(ImGuiCol.TabActive, accent))
            {
                using var tabBar = ImRaii.TabBar("PopoutProfileTabBarV2");
                if (tabBar)
                {
                    using (var tabItem = ImRaii.TabItem("RP"))
                    {
                        if (tabItem) _isRpTab = true;
                    }
                    using (var tabItem = ImRaii.TabItem("HRP"))
                    {
                        if (tabItem) _isRpTab = false;
                    }
                }
            }

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

            var drawList = ImGui.GetWindowDrawList();
            var rectMin = drawList.GetClipRectMin();
            var rectMax = drawList.GetClipRectMax();

            var nameColor = _isRpTab && _configService.Current.UseRpNameColors && !string.IsNullOrEmpty(umbraProfile.RpNameColor) ? UiSharedService.HexToVector4(umbraProfile.RpNameColor) : UiSharedService.AccentColor;
            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(_pair.UserData.AliasOrUID + (_isRpTab ? " (RP)" : " (HRP)"), nameColor);

            DrawAltPopup();

            ImGuiHelpers.ScaledDummy(spacing.Y, spacing.Y);
            var textPos = ImGui.GetCursorPosY();
            ImGui.Separator();
            var imagePos = ImGui.GetCursorPos();
            ImGuiHelpers.ScaledDummy(256, 256 * ImGuiHelpers.GlobalScale + spacing.Y);
            var note = _serverManager.GetNoteForUid(_pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                UiSharedService.ColorText(note, ImGuiColors.DalamudGrey);
            }
            string status = _pair.IsVisible ? Loc.Get("PopoutProfile.Status.Visible") : (_pair.IsOnline ? Loc.Get("PopoutProfile.Status.Online") : Loc.Get("PopoutProfile.Status.Offline"));
            UiSharedService.ColorText(status, (_pair.IsVisible || _pair.IsOnline) ? ImGuiColors.HealerGreen : UiSharedService.AccentColor);
            if (_pair.IsVisible)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"({_pair.PlayerName})");
            }
            if (_pair.UserPair != null)
            {
                ImGui.TextUnformatted(Loc.Get("PopoutProfile.PairStatus.Direct"));
                if (_pair.UserPair.OwnPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText(Loc.Get("PopoutProfile.PairStatus.YouPaused"), ImGuiColors.DalamudYellow);
                }
                if (_pair.UserPair.OtherPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText(Loc.Get("PopoutProfile.PairStatus.TheyPaused"), ImGuiColors.DalamudYellow);
                }
            }
            if (_pair.GroupPair.Any())
            {
                ImGui.TextUnformatted(Loc.Get("PopoutProfile.PairStatus.SyncshellHeader"));
                foreach (var groupPair in _pair.GroupPair.Select(k => k.Key))
                {
                    var groupNote = _serverManager.GetNoteForGid(groupPair.GID);
                    var groupName = groupPair.GroupAliasOrGID;
                    var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                    ImGui.TextUnformatted("- " + groupString);
                }
            }

            ImGui.Separator();

            if (_isRpTab)
            {
                var moodlesJson = _pair.LastReceivedCharacterData?.MoodlesData;
                if (string.IsNullOrEmpty(moodlesJson))
                    moodlesJson = umbraProfile.MoodlesData;
                if (!string.IsNullOrEmpty(moodlesJson))
                {
                    _uiSharedService.DrawMoodlesAtAGlance(moodlesJson, 36f);
                    ImGui.Spacing();
                }
            }

            _uiSharedService.GameFont.Push();
            var remaining = ImGui.GetWindowContentRegionMax().Y - ImGui.GetCursorPosY();
            var descText = _isRpTab ? (umbraProfile.RpDescription ?? Loc.Get("UserProfile.NoRpDescription")) : umbraProfile.Description;

            if (_isRpTab)
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(umbraProfile.RpFirstName) || !string.IsNullOrEmpty(umbraProfile.RpLastName))
                    sb.Append(umbraProfile.RpFirstName).Append(' ').Append(umbraProfile.RpLastName).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpTitle))
                    sb.Append(Loc.Get("UserProfile.RpTitle")).Append(" : ").Append(umbraProfile.RpTitle).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpAge))
                    sb.Append(Loc.Get("UserProfile.RpAge")).Append(" : ").Append(umbraProfile.RpAge).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpRace))
                    sb.Append(Loc.Get("UserProfile.RpRace")).Append(" : ").Append(umbraProfile.RpRace).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpEthnicity))
                    sb.Append(Loc.Get("UserProfile.RpEthnicity")).Append(" : ").Append(umbraProfile.RpEthnicity).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpHeight))
                    sb.Append(Loc.Get("UserProfile.RpHeight")).Append(" : ").Append(umbraProfile.RpHeight).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpBuild))
                    sb.Append(Loc.Get("UserProfile.RpBuild")).Append(" : ").Append(umbraProfile.RpBuild).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpResidence))
                    sb.Append(Loc.Get("UserProfile.RpResidence")).Append(" : ").Append(umbraProfile.RpResidence).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpOccupation))
                    sb.Append(Loc.Get("UserProfile.RpOccupation")).Append(" : ").Append(umbraProfile.RpOccupation).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpAffiliation))
                    sb.Append(Loc.Get("UserProfile.RpAffiliation")).Append(" : ").Append(umbraProfile.RpAffiliation).Append('\n');
                if (!string.IsNullOrEmpty(umbraProfile.RpAlignment))
                    sb.Append(Loc.Get("UserProfile.RpAlignment")).Append(" : ").Append(umbraProfile.RpAlignment).Append('\n');

                if (umbraProfile.RpCustomFields is { Count: > 0 })
                {
                    foreach (var field in umbraProfile.RpCustomFields.OrderBy(f => f.Order))
                    {
                        if (!string.IsNullOrEmpty(field.Name) || !string.IsNullOrEmpty(field.Value))
                            sb.Append(field.Name).Append(" : ").Append(field.Value).Append('\n');
                    }
                }

                if (sb.Length > 0)
                {
                    descText = sb.Append("----------\n").Append(descText).ToString();
                }
            }

            var cleanDesc = BbCodeRenderer.StripTags(descText);
            var textSize = ImGui.CalcTextSize(cleanDesc, hideTextAfterDoubleHash: false, 256f * ImGuiHelpers.GlobalScale);
            bool trimmed = textSize.Y > remaining;
            while (textSize.Y > remaining && descText.Contains(' '))
            {
                descText = descText[..descText.LastIndexOf(' ')].TrimEnd();
                cleanDesc = BbCodeRenderer.StripTags(descText);
                textSize = ImGui.CalcTextSize(cleanDesc + $"...{Environment.NewLine}{Loc.Get("PopoutProfile.ReadMoreHint")}", hideTextAfterDoubleHash: false, 256f * ImGuiHelpers.GlobalScale);
            }
            var wrapWidth = 256f * ImGuiHelpers.GlobalScale;
            BbCodeRenderer.Render(trimmed ? descText + $"...{Environment.NewLine}{Loc.Get("PopoutProfile.ReadMoreHint")}" : descText, wrapWidth);

            _uiSharedService.GameFont.Pop();

            var padding = ImGui.GetStyle().WindowPadding.X / 2;
            if (currentTexture != null)
            {
                bool tallerThanWide = currentTexture.Height >= currentTexture.Width;
                var stretchFactor = tallerThanWide ? 256f * ImGuiHelpers.GlobalScale / currentTexture.Height : 256f * ImGuiHelpers.GlobalScale / currentTexture.Width;
                var newWidth = currentTexture.Width * stretchFactor;
                var newHeight = currentTexture.Height * stretchFactor;
                var remainingWidth = (256f * ImGuiHelpers.GlobalScale - newWidth) / 2f;
                var remainingHeight = (256f * ImGuiHelpers.GlobalScale - newHeight) / 2f;
                drawList.AddImage(currentTexture.Handle, new Vector2(rectMin.X + padding + remainingWidth, rectMin.Y + spacing.Y + imagePos.Y + remainingHeight),
                    new Vector2(rectMin.X + padding + remainingWidth + newWidth, rectMin.Y + spacing.Y + imagePos.Y + remainingHeight + newHeight));
            }
            if (_supporterTextureWrap != null)
            {
                const float iconSize = 38;
                drawList.AddImage(_supporterTextureWrap.Handle,
                    new Vector2(rectMax.X - iconSize - spacing.X, rectMin.Y + (textPos / 2) - (iconSize / 2)),
                    new Vector2(rectMax.X - spacing.X, rectMin.Y + iconSize + (textPos / 2) - (iconSize / 2)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw tooltip");
        }
    }

    private void DrawAltPopup()
    {
        if (_pair == null) return;
        var alts = _umbraProfileManager.GetEncounteredAlts(_pair.UserData.UID);
        if (alts.Count <= 1) return;

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(FontAwesomeIcon.Users.ToIconString());
        ImGui.PopFont();
        var countSize = ImGui.CalcTextSize($" {alts.Count}");
        var btnW = iconSize.X + countSize.X + ImGui.GetStyle().FramePadding.X * 2 + 4f;

        if (ImGui.Button($"##altPopupBtn", new Vector2(btnW, ImGui.GetFrameHeight())))
            ImGui.OpenPopup("##altPopup");

        // Draw icon + text manually over the button
        var btnMin = ImGui.GetItemRectMin();
        var btnMax = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        var centerY = (btnMin.Y + btnMax.Y) / 2f;
        ImGui.PushFont(UiBuilder.IconFont);
        dl.AddText(new Vector2(btnMin.X + ImGui.GetStyle().FramePadding.X, centerY - iconSize.Y / 2f),
            ImGui.GetColorU32(ImGuiColors.DalamudGrey), FontAwesomeIcon.Users.ToIconString());
        ImGui.PopFont();
        dl.AddText(new Vector2(btnMin.X + ImGui.GetStyle().FramePadding.X + iconSize.X + 2f, centerY - countSize.Y / 2f),
            ImGui.GetColorU32(ImGuiColors.DalamudGrey), $" {alts.Count}");

        if (ImGui.BeginPopup("##altPopup"))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("AltSwitcher.Label"));
            ImGui.Separator();

            for (int i = 0; i < alts.Count; i++)
            {
                var (charName, worldId) = alts[i];
                var cachedProfile = _umbraProfileManager.GetUmbraProfile(_pair.UserData, charName, worldId);
                var first = cachedProfile.RpFirstName ?? string.Empty;
                var last = cachedProfile.RpLastName ?? string.Empty;
                var rpName = $"{first} {last}".Trim();
                var worldName = _dalamudUtil.WorldData.Value.TryGetValue((ushort)worldId, out var wn) ? wn : worldId.ToString();
                var displayName = !string.IsNullOrEmpty(rpName) ? $"{rpName}  ({charName} @ {worldName})" : $"{charName} @ {worldName}";

                bool isSelected = string.Equals(_selectedAltCharName, charName, StringComparison.Ordinal) && _selectedAltWorldId == worldId;
                if (_selectedAltCharName == null)
                {
                    var pair = _pairManager.GetPairByUID(_pair.UserData.UID);
                    if (pair != null)
                        isSelected = string.Equals(pair.PlayerName, charName, StringComparison.Ordinal) && pair.WorldId == worldId;
                    else
                    {
                        var lastName = _serverManager.GetNameForUid(_pair.UserData.UID);
                        var lastWorld = _serverManager.GetWorldIdForUid(_pair.UserData.UID);
                        isSelected = string.Equals(lastName, charName, StringComparison.Ordinal) && lastWorld == worldId;
                    }
                }

                if (ImGui.Selectable(displayName, isSelected))
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

            ImGui.EndPopup();
        }
    }
}