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
    private Vector2 _lastMainPos = Vector2.Zero;
    private Vector2 _lastMainSize = Vector2.Zero;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastRpProfilePicture = [];
    private Pair? _pair;
    private IDalamudTextureWrap? _supporterTextureWrap;
    private IDalamudTextureWrap? _textureWrap;
    private IDalamudTextureWrap? _rpTextureWrap;
    private bool _isRpTab;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, MareMediator mediator, UiSharedService uiSharedService,
        ServerConfigurationManager serverManager, MareConfigService mareConfigService,
        UmbraProfileManager umbraProfileManager, PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "###UmbraSyncPopoutProfileUI", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _serverManager = serverManager;
        _configService = mareConfigService;
        _umbraProfileManager = umbraProfileManager;
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

            var umbraProfile = _umbraProfileManager.GetUmbraProfile(_pair.UserData);

            var accent = UiSharedService.AccentColor;
            if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;
            using (var _ = ImRaii.PushColor(ImGuiCol.TabHovered, accent))
            using (var __ = ImRaii.PushColor(ImGuiCol.TabActive, accent))
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
                // Données live = personnage connecté ; hors ligne, elles peuvent venir d'un autre perso du compte
                var moodlesJson = _pair.IsOnline ? _pair.LastReceivedCharacterData?.MoodlesData : null;
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
}