using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Modules.Rp.Services;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;
using UmbraSync.Utils;

namespace UmbraSync.Modules.Rp.UI;

public sealed class TargetProfileTooltipUi : WindowMediatorSubscriberBase
{
    private const int MaxDescriptionLength = 220;
    private const int JournalPreviewLength = 140;
    private static float TooltipWidth => 340f * ImGuiHelpers.GlobalScale;
    private static float PortraitSize => 84f * ImGuiHelpers.GlobalScale;

    private readonly ITargetManager _targetManager;
    private readonly PairManager _pairManager;
    private readonly UmbraProfileManager _profileManager;
    private readonly MareConfigService _configService;
    private readonly UiSharedService _uiSharedService;
    private readonly ServerConfigurationManager _serverManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IGameGui _gameGui;
    private readonly RpJournalService _journal;

    private static readonly string[] TargetBarAddons = ["_TargetInfoMainTarget", "_TargetInfo"];
    private string? _dismissedTargetKey;

    private volatile TargetInfo? _target;
    private volatile UmbraProfileData? _preview;

    private sealed record TargetInfo(Pair? Pair, string CharName, uint WorldId)
    {
        public string Key => $"{Pair?.UserData.UID}|{CharName}|{WorldId}";
    }

    public TargetProfileTooltipUi(ILogger<TargetProfileTooltipUi> logger, MareMediator mediator,
        ITargetManager targetManager, PairManager pairManager, UmbraProfileManager profileManager,
        MareConfigService configService, UiSharedService uiSharedService,
        ServerConfigurationManager serverManager, DalamudUtilService dalamudUtil, IGameGui gameGui,
        RpJournalService journal, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "###UmbraSyncTargetProfileTooltip", performanceCollectorService)
    {
        _targetManager = targetManager;
        _pairManager = pairManager;
        _profileManager = profileManager;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _serverManager = serverManager;
        _dalamudUtil = dalamudUtil;
        _gameGui = gameGui;
        _journal = journal;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.AlwaysAutoResize;
        RespectCloseHotkey = false;
        IsOpen = false;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => RefreshTarget());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => _target = null);
        Mediator.Subscribe<GposeStartMessage>(this, _ =>
        {
            _target = null;
            IsOpen = false;
        });
        Mediator.Subscribe<TargetProfilePreviewMessage>(this, msg =>
        {
            _preview = msg.Profile;
            IsOpen = msg.Profile != null;
        });
    }

    private void RefreshTarget()
    {
        try
        {
            if (_dalamudUtil.IsInGpose)
            {
                _target = null;
                IsOpen = false;
                return;
            }

            NoteJournalSighting();

            if (_preview != null)
            {
                IsOpen = true;
                return;
            }

            // Désactivée par l'utilisateur, ou en combat / cinématique où elle ne ferait que gêner.
            if (!_configService.Current.ShowTargetProfileTooltip || _dalamudUtil.IsInCutscene || _dalamudUtil.IsInCombatOrPerforming
                || (_configService.Current.HideTargetProfileTooltipInDuty && _dalamudUtil.IsInDuty))
            {
                _target = null;
                IsOpen = false;
                return;
            }

            if (_targetManager.Target is IPlayerCharacter pc)
            {
                var pair = _pairManager.GetVisiblePairByObjectId(pc.EntityId);
                var name = pc.Name.TextValue;
                var world = pc.HomeWorld.RowId;
                var subject = _configService.Current.ShowJournalInTargetTooltip ? _journal.TryGetSubject(name, world) : null;
                if (pair != null || subject != null)
                {
                    var current = _target;
                    if (current == null || !ReferenceEquals(current.Pair, pair)
                        || !string.Equals(current.CharName, name, StringComparison.Ordinal) || current.WorldId != world)
                        _target = new TargetInfo(pair, name, world);

                    if (_dismissedTargetKey != null)
                    {
                        if (string.Equals(_dismissedTargetKey, _target!.Key, StringComparison.Ordinal))
                        {
                            IsOpen = false;
                            return;
                        }
                        _dismissedTargetKey = null;
                    }

                    bool hasRpName = false;
                    string? rpName = null;
                    if (pair != null)
                    {
                        var profile = _profileManager.GetUmbraProfile(pair.UserData, name, world);
                        rpName = $"{profile.RpFirstName} {profile.RpLastName}".Trim();
                        hasRpName = rpName.Length > 0;
                    }

                    IsOpen = hasRpName || subject != null;
                    return;
                }
            }

            _target = null;
            _dismissedTargetKey = null;
            IsOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lecture de la cible impossible pour l'infobulle de profil");
            _target = null;
            IsOpen = false;
        }
    }

    private void NoteJournalSighting()
    {
        if (_targetManager.Target is not IPlayerCharacter pc) return;
        var name = pc.Name.TextValue;
        var world = pc.HomeWorld.RowId;
        if (_journal.TryGetSubject(name, world) == null) return;

        var pair = _pairManager.GetVisiblePairByObjectId(pc.EntityId);
        string? rpName = null;
        if (pair != null)
        {
            var profile = _profileManager.GetUmbraProfile(pair.UserData, name, world);
            rpName = $"{profile.RpFirstName} {profile.RpLastName}".Trim();
        }
        _journal.NoteSeen(name, world, pair?.UserData.UID, rpName);
    }

    public override void PreDraw()
    {
        UiSharedService.BeginProfileAccent(ResolveProfile()?.CardStyle.Value.AccentColor);
        base.PreDraw();
        var viewport = ImGui.GetMainViewport();
        var margin = 30f * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X - TooltipWidth - margin,
            viewport.WorkPos.Y + viewport.WorkSize.Y - 260f * ImGuiHelpers.GlobalScale - margin), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(TooltipWidth, 0), new Vector2(TooltipWidth, 600f * ImGuiHelpers.GlobalScale));
    }

    public override void PostDraw()
    {
        base.PostDraw();
        UiSharedService.EndProfileAccent();
    }

    public override bool DrawConditions()
        => _preview != null
           || !IsTargetBarHoverMode
           || IsMouseOverTargetBar();

    private bool IsTargetBarHoverMode
        => _configService.Current.TargetProfileTooltipTrigger == TargetProfileTooltipTrigger.OnTargetBarHover;

    private unsafe bool IsMouseOverTargetBar()
    {
        foreach (var addonName in TargetBarAddons)
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addonName, 1).Address;
            if (unit == null || !unit->IsVisible || unit->RootNode == null) continue;

            var min = new Vector2(unit->X, unit->Y);
            var max = min + new Vector2(unit->RootNode->Width * unit->Scale, unit->RootNode->Height * unit->Scale);
            if (IsMouseInside(min, max)) return true;
        }
        return false;
    }

    private static bool IsMouseInside(Vector2 min, Vector2 max)
    {
        var mouse = ImGui.GetIO().MousePos;
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    private UmbraProfileData? ResolveProfile()
    {
        var preview = _preview;
        if (preview != null) return preview;
        var target = _target;
        return target?.Pair == null ? null : _profileManager.GetUmbraProfile(target.Pair.UserData, target.CharName, target.WorldId);
    }

    protected override void DrawInternal()
    {
        var preview = _preview;
        var target = _target;
        if (preview == null && target == null) return;

        var subject = preview == null && _configService.Current.ShowJournalInTargetTooltip
            ? _journal.TryGetSubject(target!.CharName, target.WorldId) : null;
        if (preview == null && target!.Pair == null)
        {
            if (subject != null) DrawJournalOnly(target, subject);
            return;
        }

        var profile = preview ?? _profileManager.GetUmbraProfile(target!.Pair!.UserData, target.CharName, target.WorldId);
        var rpName = $"{profile.RpFirstName} {profile.RpLastName}".Trim();
        if (string.IsNullOrEmpty(rpName))
        {
            if (subject != null) DrawJournalOnly(target!, subject);
            return;
        }

        var accent = UiSharedService.AccentColor;
        if (accent.W <= 0f) accent = ImGuiColors.ParsedPurple;

        var portrait = _uiSharedService.ProfileTextures.GetTexture(profile.RpImageData.HasImage ? profile.RpImageData : profile.ImageData);

        var windowDraw = ImGui.GetWindowDrawList();
        windowDraw.ChannelsSplit(2);
        windowDraw.ChannelsSetCurrent(1);

        ImGui.BeginGroup();
        var portraitMin = ImGui.GetCursorScreenPos();
        var portraitSize = new Vector2(PortraitSize);
        var portraitRounding = 10f * ImGuiHelpers.GlobalScale;
        ImGui.Dummy(portraitSize);
        var portraitDraw = ImGui.GetWindowDrawList();
        if (portrait != null)
        {
            var (uv0, uv1) = UmbraSync.UI.Components.ProfileBanner.CoverUv(portrait.Width, portrait.Height, portraitSize.X, portraitSize.Y);
            portraitDraw.AddImageRounded(portrait.Handle, portraitMin, portraitMin + portraitSize,
                uv0, uv1, ImGui.ColorConvertFloat4ToU32(Vector4.One), portraitRounding);
        }
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
        var cardStyle = profile.CardStyle.Value;
        var infoColor = UiSharedService.ProfileCardTextColor(cardStyle.TextColor);
        if (!string.IsNullOrEmpty(profile.RpTitle))
            ImGui.TextColored(infoColor, profile.RpTitle);

        var pronouns = profile.RpCustomFields?.FirstOrDefault(f =>
                f.Name.Contains("pronom", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("pronoun", StringComparison.OrdinalIgnoreCase))?.Value;
        var identity = string.Join(" · ", new[] { profile.RpRace, profile.RpEthnicity, profile.RpAge, pronouns }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (!string.IsNullOrEmpty(identity))
            ImGui.TextColored(infoColor, identity);
        if (!string.IsNullOrWhiteSpace(profile.RpOccupation))
            ImGui.TextColored(infoColor, profile.RpOccupation);
        if (!string.IsNullOrWhiteSpace(profile.RpAffiliation))
            ImGui.TextColored(infoColor, $"<{profile.RpAffiliation}>");
        if (!string.IsNullOrWhiteSpace(profile.RpResidence))
        {
            _uiSharedService.IconText(FontAwesomeIcon.Home, infoColor);
            ImGui.SameLine();
            ImGui.TextColored(infoColor, profile.RpResidence);
        }

        var privateNote = target?.Pair == null ? null : _serverManager.GetNoteForUid(target.Pair.UserData.UID);
        if (!string.IsNullOrWhiteSpace(privateNote))
            ImGui.TextColored(ImGuiColors.DalamudGrey2, privateNote);
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

        if (subject != null)
        {
            ImGui.Separator();
            DrawJournalLines(subject);
        }

        if (preview != null)
        {
            ImGuiHelpers.ScaledDummy(4f);
            if (DrawGhostButton(accent, Loc.Get("EditProfile.CardPreview.Close"), FontAwesomeIcon.Times, "##closeCardPreview"))
                Mediator.Publish(new TargetProfilePreviewMessage(null));
        }
        else if (!IsTargetBarHoverMode)
        {
            ImGuiHelpers.ScaledDummy(4f);
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
            if (DrawGhostButton(accent, Loc.Get("Settings.ProfileBrowser.OpenProfile"), FontAwesomeIcon.ExternalLinkAlt, "##openTargetProfile", half))
                Mediator.Publish(new ProfileOpenStandaloneMessage(target!.Pair!, target.CharName, target.WorldId));
            ImGui.SameLine();
            if (DrawGhostButton(accent, Loc.Get("TargetCard.Close"), FontAwesomeIcon.Times, "##closeTargetCard", half))
            {
                _dismissedTargetKey = target!.Key;
                IsOpen = false;
            }
        }

        DrawBanner(windowDraw, profile.RpBannerData, cardStyle);
        windowDraw.ChannelsMerge();
    }

    private void DrawJournalOnly(TargetInfo target, JournalSubject subject)
    {
        var accent = UiSharedService.AccentColor;
        using (_uiSharedService.UidFont.Push())
            UiSharedService.ColorText(RpJournalQuery.DisplayName(subject), accent);
        ImGui.TextColored(ImGuiColors.DalamudGrey, target.CharName + " · " + _journal.WorldName(target.WorldId));
        ImGui.Separator();
        DrawJournalLines(subject);

        if (IsTargetBarHoverMode) return;
        ImGuiHelpers.ScaledDummy(4f);
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (DrawGhostButton(accent, Loc.Get("Journal.Tooltip.Open"), FontAwesomeIcon.Feather, "##openJournal", half))
            Mediator.Publish(new OpenJournalMessage(target.CharName, target.WorldId, target.Pair?.UserData.UID));
        ImGui.SameLine();
        if (DrawGhostButton(accent, Loc.Get("TargetCard.Close"), FontAwesomeIcon.Times, "##closeJournalCard", half))
        {
            _dismissedTargetKey = target.Key;
            IsOpen = false;
        }
    }

    private static void DrawJournalLines(JournalSubject subject)
    {
        JournalTrustStyle.DrawDot(subject.Trust);
        ImGui.SameLine(0, 6f * ImGuiHelpers.GlobalScale);
        var line = JournalTrustStyle.Label(subject.Trust);
        if (!string.IsNullOrWhiteSpace(subject.Relation)) line += " · " + subject.Relation;
        ImGui.TextColored(JournalTrustStyle.Color(subject.Trust), line);

        var last = subject.Entries.Count > 0 ? subject.Entries[^1].Text : subject.Summary;
        if (string.IsNullOrWhiteSpace(last)) return;
        last = last.Trim().ReplaceLineEndings(" ");
        if (last.Length > JournalPreviewLength) last = last[..JournalPreviewLength].TrimEnd() + "…";
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + TooltipWidth - ImGui.GetStyle().WindowPadding.X * 2);
        ImGui.TextColored(ImGuiColors.DalamudGrey2, last);
        ImGui.PopTextWrapPos();
    }

    // Bannière derrière tout le contenu, estompée sur les bords comme la carte héros du profil.
    private void DrawBanner(ImDrawListPtr drawList, ProfileImage bannerImage, ProfileCardStyle style)
    {
        var banner = _uiSharedService.ProfileTextures.GetTexture(bannerImage);
        if (banner == null) return;

        var scale = ImGuiHelpers.GlobalScale;
        var inset = 1f * scale;
        var min = ImGui.GetWindowPos() + new Vector2(inset);
        var max = ImGui.GetWindowPos() + ImGui.GetWindowSize() - new Vector2(inset);
        var background = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.WindowBg));

        drawList.ChannelsSetCurrent(0);
        UmbraSync.UI.Components.ProfileBanner.Draw(drawList, banner, min, max, background,
            MathF.Max(ImGui.GetStyle().WindowRounding - inset, 0f), style);
        drawList.ChannelsSetCurrent(1);
    }

    private static bool DrawGhostButton(Vector4 accent, string label, FontAwesomeIcon fontIcon, string id, float? width = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(width ?? ImGui.GetContentRegionAvail().X, 22f * scale);
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
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

        var icon = fontIcon.ToIconString();
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
}
