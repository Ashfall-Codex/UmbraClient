using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed class TargetProfileTooltipUi : WindowMediatorSubscriberBase
{
    private const int MaxDescriptionLength = 220;
    private static float TooltipWidth => 340f * ImGuiHelpers.GlobalScale;
    private static float PortraitSize => 84f * ImGuiHelpers.GlobalScale;

    private readonly ILogger<TargetProfileTooltipUi> _logger;
    private readonly ITargetManager _targetManager;
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _profileManager;
    private readonly MareConfigService _configService;
    private readonly UiSharedService _uiSharedService;

    private volatile TargetInfo? _target;
    private byte[] _lastPictureData = [];
    private Task<IDalamudTextureWrap>? _textureTask;

    private sealed record TargetInfo(Pair Pair, string CharName, uint WorldId);

    public TargetProfileTooltipUi(ILogger<TargetProfileTooltipUi> logger, MareMediator mediator,
        ITargetManager targetManager, PairManager pairManager, UmbraProfileManager profileManager,
        MareConfigService configService, UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "###UmbraSyncTargetProfileTooltip", performanceCollectorService)
    {
        _logger = logger;
        _targetManager = targetManager;
        _pairManager = pairManager;
        _profileManager = profileManager;
        _configService = configService;
        _uiSharedService = uiSharedService;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize;
        RespectCloseHotkey = false;
        IsOpen = false;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => RefreshTarget());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => _target = null);
    }

    private void RefreshTarget()
    {
        try
        {
            if (!_configService.Current.ShowTargetProfileTooltip)
            {
                _target = null;
                IsOpen = false;
                return;
            }

            if (_targetManager.Target is IPlayerCharacter pc)
            {
                var pair = _pairManager.GetVisiblePairByObjectId(pc.EntityId);
                if (pair != null)
                {
                    var name = pc.Name.TextValue;
                    var world = pc.HomeWorld.RowId;
                    var current = _target;
                    if (current == null || !ReferenceEquals(current.Pair, pair)
                        || !string.Equals(current.CharName, name, StringComparison.Ordinal) || current.WorldId != world)
                        _target = new TargetInfo(pair, name, world);
                    var profile = _profileManager.GetUmbraProfile(pair.UserData, name, world);
                    IsOpen = !string.IsNullOrWhiteSpace(profile.RpFirstName) || !string.IsNullOrWhiteSpace(profile.RpLastName);
                    return;
                }
            }

            _target = null;
            IsOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lecture de la cible impossible pour l'infobulle de profil");
            _target = null;
            IsOpen = false;
        }
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var viewport = ImGui.GetMainViewport();
        var margin = 30f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X - TooltipWidth - margin,
            viewport.WorkPos.Y + viewport.WorkSize.Y - 260f * ImGuiHelpers.GlobalScale - margin), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(TooltipWidth, 0), new Vector2(TooltipWidth, 600f * ImGuiHelpers.GlobalScale));
    }

    protected override void DrawInternal()
    {
        var target = _target;
        if (target == null) return;

        var profile = _profileManager.GetUmbraProfile(target.Pair.UserData, target.CharName, target.WorldId);
        var rpName = $"{profile.RpFirstName} {profile.RpLastName}".Trim();
        if (string.IsNullOrEmpty(rpName)) return;

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        var pictureData = profile.RpImageData.Value;
        if (pictureData.Length == 0) pictureData = profile.ImageData.Value;
        if (_textureTask == null || !ReferenceEquals(pictureData, _lastPictureData) && !pictureData.AsSpan().SequenceEqual(_lastPictureData))
        {
            var previous = _textureTask;
            previous?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            _lastPictureData = pictureData;
            _textureTask = pictureData.Length == 0
                ? Task.FromException<IDalamudTextureWrap>(new InvalidOperationException("Pas d'image"))
                : Task.Run(() => _uiSharedService.LoadImageAsync(pictureData));
        }

        ImGui.BeginGroup();
        var portraitMin = ImGui.GetCursorScreenPos();
        var portraitSize = new Vector2(PortraitSize);
        var portraitRounding = 10f * ImGuiHelpers.GlobalScale;
        ImGui.Dummy(portraitSize);
        var portraitDraw = ImGui.GetWindowDrawList();
        if (_textureTask is { IsCompletedSuccessfully: true })
            portraitDraw.AddImageRounded(_textureTask.Result.Handle, portraitMin, portraitMin + portraitSize,
                Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(Vector4.One), portraitRounding);
        else
            portraitDraw.AddRectFilled(portraitMin, portraitMin + portraitSize,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.06f)), portraitRounding);
        portraitDraw.AddRect(portraitMin, portraitMin + portraitSize,
            ImGui.GetColorU32(accent with { W = 0.5f }), portraitRounding, ImDrawFlags.None, 1f * ImGuiHelpers.GlobalScale);
        ImGui.EndGroup();
        ImGui.SameLine();

        ImGui.BeginGroup();
        // Les noms à rallonge passent à la ligne au lieu de déborder de la carte.
        var infoWidth = TooltipWidth - ImGui.GetStyle().WindowPadding.X * 2 - PortraitSize - ImGui.GetStyle().ItemSpacing.X;
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + infoWidth);
        var nameColor = _configService.Current.UseRpNameColors && !string.IsNullOrEmpty(profile.RpNameColor)
            ? UiSharedService.HexToVector4(profile.RpNameColor)
            : accent;
        using (_uiSharedService.UidFont.Push())
            UiSharedService.ColorText(rpName, nameColor);
        if (!string.IsNullOrEmpty(profile.RpTitle))
            ImGui.TextColored(ImGuiColors.DalamudGrey, profile.RpTitle);

        var identity = string.Join(" · ", new[] { profile.RpRace, profile.RpEthnicity, profile.RpAge }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (!string.IsNullOrEmpty(identity))
            ImGui.TextColored(ImGuiColors.DalamudGrey, identity);
        if (!string.IsNullOrWhiteSpace(profile.RpOccupation))
            ImGui.TextColored(ImGuiColors.DalamudGrey, profile.RpOccupation);
        if (!string.IsNullOrWhiteSpace(profile.RpAffiliation))
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"<{profile.RpAffiliation}>");
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var description = profile.RpDescription;
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.Separator();
            description = description.Trim();
            if (description.Length > MaxDescriptionLength)
                description = description[..MaxDescriptionLength].TrimEnd() + "…";
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TooltipWidth - ImGui.GetStyle().WindowPadding.X * 2);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
        }

        ImGuiHelpers.ScaledDummy(4f);
        if (DrawOpenProfileButton(accent))
            Mediator.Publish(new ProfileOpenStandaloneMessage(target.Pair, target.CharName, target.WorldId));
    }

    private static bool DrawOpenProfileButton(Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 22f * scale);
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton("##openTargetProfile", size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var dl = ImGui.GetWindowDrawList();
        var max = pos + size;
        var rounding = 6f * scale;

        // Style « fantôme » : fond à peine teinté, liseré fin, qui se remplit au survol.
        float fillAlpha = held ? 0.45f : hovered ? 0.32f : 0.12f;
        dl.AddRectFilled(pos, max, ImGui.GetColorU32(accent with { W = fillAlpha }), rounding);
        dl.AddRect(pos, max, ImGui.GetColorU32(accent with { W = hovered ? 0.85f : 0.4f }), rounding, ImDrawFlags.None, 1f * scale);

        var label = Loc.Get("Settings.ProfileBrowser.OpenProfile");
        var icon = FontAwesomeIcon.ExternalLinkAlt.ToIconString();
        Vector2 iconSize;
        using (Dalamud.Interface.Utility.Raii.ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(icon);
        var textSize = ImGui.CalcTextSize(label);
        float gap = 6f * scale;
        float total = iconSize.X + gap + textSize.X;
        float startX = pos.X + (size.X - total) / 2f;
        var textColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 1f : 0.75f));

        using (Dalamud.Interface.Utility.Raii.ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(startX, pos.Y + (size.Y - iconSize.Y) / 2f), textColor, icon);
        dl.AddText(new Vector2(startX + iconSize.X + gap, pos.Y + (size.Y - textSize.Y) / 2f), textColor, label);

        return clicked;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _textureTask?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            _textureTask = null;
        }

        base.Dispose(disposing);
    }
}
