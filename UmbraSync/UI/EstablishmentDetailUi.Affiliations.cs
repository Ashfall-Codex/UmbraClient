using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.Localization;
using UmbraSync.Services.Mediator;
using UmbraSync.UI.Components;
using UmbraSync.Utils;

namespace UmbraSync.UI;

internal partial class EstablishmentDetailUi
{
    private List<EstablishmentAffiliationDto> _myAffiliations = [];
    private List<EstablishmentAffiliationDto> _establishmentAffiliations = [];
    private string _affiliationMessage = string.Empty;
    private string? _affiliationStatus;
    private volatile bool _affiliationBusy;
    private byte[] _portraitData = [];
    private Task<IDalamudTextureWrap>? _portraitTask;
    private readonly Dictionary<Guid, (string Base64, Task<IDalamudTextureWrap> Task)> _memberPortraits = [];

    private void RefreshAffiliationsFor(Guid establishmentId)
    {
        if (IsOpen && establishmentId == _currentId)
            _ = LoadAffiliationsAsync();
    }

    private async Task LoadAffiliationsAsync()
    {
        try
        {
            var establishmentId = _currentId;
            var isOwner = _establishment != null && string.Equals(_establishment.OwnerUID, _apiController.UID, StringComparison.Ordinal);
            if (_ownRpProfiles == null) await LoadOwnRpProfiles().ConfigureAwait(false);

            var mine = await _apiController.EstablishmentAffiliationGetMine().ConfigureAwait(false);
            var members = isOwner
                ? await _apiController.EstablishmentAffiliationGetForEstablishment(establishmentId).ConfigureAwait(false)
                : [];
            if (establishmentId != _currentId) return;

            _myAffiliations = mine.Where(a => a.EstablishmentId == establishmentId).ToList();
            _establishmentAffiliations = members;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading affiliations for establishment {id}", _currentId);
        }
    }

    private void RunAffiliationAction(Func<Task<string?>> action)
    {
        if (_affiliationBusy) return;
        _affiliationBusy = true;
        _affiliationStatus = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var status = await action().ConfigureAwait(false);
                if (status != null) _affiliationStatus = status;
                await LoadAffiliationsAsync().ConfigureAwait(false);
                Mediator.Publish(new EstablishmentChangedMessage());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Affiliation action failed for establishment {id}", _currentId);
            }
            finally
            {
                _affiliationBusy = false;
            }
        });
    }

    private string MembersTabLabel()
    {
        var pending = _establishmentAffiliations.Count(a => a.Status == EstablishmentAffiliationStatus.Pending);
        var label = Loc.Get("Establishment.Affiliation.MembersTab");
        return (pending > 0 ? $"{label} ({pending})" : label) + "###establishmentMembers";
    }

    // Seul le personnage connecté est concerné : ses autres persos se gèrent en se connectant dessus.
    private RpProfileSummaryDto? CurrentCharacterProfile()
    {
        if (_ownRpProfiles == null) return null;
        var name = _dalamudUtil.GetPlayerName();
        var world = _dalamudUtil.GetHomeWorldId();
        return _ownRpProfiles.FirstOrDefault(p => string.Equals(p.CharacterName, name, StringComparison.Ordinal) && p.WorldId == world);
    }

    private void DrawAffiliationTab(bool isOwner)
    {
        ImGuiHelpers.ScaledDummy(4f);
        if (_ownRpProfiles == null)
        {
            ImGui.TextDisabled(Loc.Get("Establishment.Detail.Loading"));
            return;
        }

        DrawSectionHeader(Loc.Get("Establishment.Affiliation.CurrentCharacterHeader"));
        var profile = CurrentCharacterProfile();
        if (profile == null)
        {
            UiSharedService.DrawCard("affiliation-no-profile", () =>
                UiSharedService.ColorTextWrapped(Loc.Get("Establishment.Affiliation.NoRpProfile"), ImGuiColors.DalamudGrey), stretchWidth: true);
            return;
        }

        var affiliation = _myAffiliations.FirstOrDefault(a => a.RpProfileId == profile.Id);
        bool isManager = _establishment?.ManagerRpProfileId == profile.Id;
        var (badge, badgeColor) = isManager
            ? (Loc.Get("Establishment.Affiliation.Status.Manager"), UiSharedService.AccentColor)
            : affiliation?.Status switch
            {
                EstablishmentAffiliationStatus.Pending => (Loc.Get("Establishment.Affiliation.Status.Pending"), ImGuiColors.DalamudYellow),
                EstablishmentAffiliationStatus.Accepted => (Loc.Get("Establishment.Affiliation.Status.Member"), ImGuiColors.HealerGreen),
                _ => ((string?)null, Vector4.Zero),
            };

        using (ImRaii.Disabled(_affiliationBusy))
        {
            var portrait = CurrentCharacterPortrait(profile);
            UiSharedService.DrawCard("affiliation-character", () =>
            {
                var rowWidth = CardRowWidth();
                DrawListRow(ProfileTitle(profile.RpFirstName, profile.RpLastName, profile.CharacterName),
                    CharacterLine(profile.CharacterName, profile.WorldId), rowWidth,
                    badge == null ? 0f : BadgeWidth(badge), badge == null ? null : () => DrawBadge(badge, badgeColor), portrait: portrait);

                if (affiliation is { Status: EstablishmentAffiliationStatus.Accepted } member)
                {
                    DrawRowSeparator(0f, rowWidth);
                    bool show = member.ShowOnProfile;
                    if (DrawToggleRow(Loc.Get("Establishment.Affiliation.ShowOnProfile"), ref show, rowWidth))
                    {
                        var affiliationId = member.Id;
                        RunAffiliationAction(async () =>
                            await _apiController.EstablishmentAffiliationSetVisibility(affiliationId, show).ConfigureAwait(false)
                                ? null
                                : Loc.Get("Establishment.Affiliation.ActionFailed"));
                    }
                }
            }, stretchWidth: true);

            DrawSectionFooter(Loc.Get(isManager ? "Establishment.Affiliation.ManagerFooter"
                : affiliation == null ? (isOwner ? "Establishment.Affiliation.IntroOwner" : "Establishment.Affiliation.Intro")
                : affiliation.Status == EstablishmentAffiliationStatus.Pending ? "Establishment.Affiliation.PendingFooter"
                : affiliation.ShowOnProfile ? "Establishment.Affiliation.MemberFooter" : "Establishment.Affiliation.MemberHiddenFooter"));

            if (!isManager)
                DrawAffiliationActions(profile, affiliation, isOwner);
        }

        DrawAffiliationNotice();
    }

    private IDalamudTextureWrap? CurrentCharacterPortrait(RpProfileSummaryDto profile)
    {
        var data = _umbraProfileManager.GetUmbraProfile(new UserData(_apiController.UID), profile.CharacterName, profile.WorldId).RpImageData.Value;
        if (!ReferenceEquals(data, _portraitData) && !data.AsSpan().SequenceEqual(_portraitData))
        {
            _portraitTask.DisposeResultWhenCompleted();
            _portraitData = data;
            _portraitTask = data.Length == 0 ? null : Task.Run(() => _uiSharedService.LoadImageAsync(data));
        }
        return _portraitTask is { IsCompletedSuccessfully: true } ? _portraitTask.Result : null;
    }

    private IDalamudTextureWrap? MemberPortrait(EstablishmentAffiliationDto member)
    {
        var base64 = member.RpProfilePictureBase64;
        if (string.IsNullOrEmpty(base64)) return null;

        if (_memberPortraits.TryGetValue(member.Id, out var cached) && !ReferenceEquals(cached.Base64, base64))
        {
            if (string.Equals(cached.Base64, base64, StringComparison.Ordinal))
            {
                cached = (base64, cached.Task);
                _memberPortraits[member.Id] = cached;
            }
            else
            {
                cached.Task.DisposeResultWhenCompleted();
                _memberPortraits.Remove(member.Id);
                cached = default;
            }
        }

        if (cached.Task == null)
        {
            cached = (base64, Task.Run(() => _uiSharedService.LoadImageAsync(Convert.FromBase64String(base64))));
            _memberPortraits[member.Id] = cached;
        }
        return cached.Task.IsCompletedSuccessfully ? cached.Task.Result : null;
    }

    private void PruneMemberPortraits(List<EstablishmentAffiliationDto> current)
    {
        foreach (var id in _memberPortraits.Keys.Where(id => current.TrueForAll(a => a.Id != id)).ToList())
        {
            _memberPortraits[id].Task.DisposeResultWhenCompleted();
            _memberPortraits.Remove(id);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _portraitTask.DisposeResultWhenCompleted();
            _portraitTask = null;
            foreach (var (_, task) in _memberPortraits.Values)
                task.DisposeResultWhenCompleted();
            _memberPortraits.Clear();
        }
        base.Dispose(disposing);
    }

    private void DrawAffiliationActions(RpProfileSummaryDto profile, EstablishmentAffiliationDto? affiliation, bool isOwner)
    {
        ImGuiHelpers.ScaledDummy(6f);
        if (affiliation == null)
        {
            if (!isOwner)
            {
                DrawSectionHeader(Loc.Get("Establishment.Affiliation.MessageHeader"));
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("##affiliationMessage", Loc.Get("Establishment.Affiliation.MessageHint"),
                    ref _affiliationMessage, EstablishmentAffiliationLimits.MaxMessageLength);
                ImGuiHelpers.ScaledDummy(4f);
            }

            var label = Loc.Get(isOwner ? "Establishment.Affiliation.Affiliate" : "Establishment.Affiliation.Request");
            if (DrawTrailingButton(FontAwesomeIcon.Handshake, label, primary: true))
            {
                var request = new EstablishmentAffiliationRequestDto
                {
                    EstablishmentId = _currentId,
                    RpProfileId = profile.Id,
                    Message = isOwner ? null : _affiliationMessage
                };
                RunAffiliationAction(async () =>
                {
                    var result = await _apiController.EstablishmentAffiliationRequest(request).ConfigureAwait(false);
                    if (result == EstablishmentAffiliationRequestResult.Requested) _affiliationMessage = string.Empty;
                    return RequestResultText(result);
                });
            }
            return;
        }

        var pending = affiliation.Status == EstablishmentAffiliationStatus.Pending;
        if (DrawTrailingButton(pending ? FontAwesomeIcon.Times : FontAwesomeIcon.SignOutAlt,
                Loc.Get(pending ? "Establishment.Affiliation.Cancel" : "Establishment.Affiliation.Leave"), destructive: true))
            RunAffiliationAction(() => RemoveAffiliation(affiliation.Id));
    }

    private void DrawMembersTab()
    {
        ImGuiHelpers.ScaledDummy(4f);
        PruneMemberPortraits(_establishmentAffiliations);
        var pending = _establishmentAffiliations.Where(a => a.Status == EstablishmentAffiliationStatus.Pending).ToList();
        var members = _establishmentAffiliations.Where(a => a.Status == EstablishmentAffiliationStatus.Accepted).ToList();

        if (pending.Count == 0 && members.Count == 0)
        {
            UiSharedService.DrawCard("affiliation-members-empty", () =>
                UiSharedService.ColorTextWrapped(Loc.Get("Establishment.Affiliation.NoMembers"), ImGuiColors.DalamudGrey), stretchWidth: true);
            return;
        }

        using (ImRaii.Disabled(_affiliationBusy))
        {
            if (pending.Count > 0)
            {
                DrawSectionHeader(Loc.Get("Establishment.Affiliation.PendingHeader"));
                UiSharedService.DrawCard("affiliation-pending", () => DrawRows(pending, DrawPendingRequestRow), stretchWidth: true);
                ImGuiHelpers.ScaledDummy(6f);
            }

            if (members.Count > 0)
            {
                DrawSectionHeader(Loc.Get("Establishment.Affiliation.MembersHeader"));
                UiSharedService.DrawCard("affiliation-members", () => DrawRows(members, DrawMemberRow), stretchWidth: true);
                DrawSectionFooter(Loc.Get("Establishment.Affiliation.MembersFooter"));
            }
        }

        DrawAffiliationNotice();
    }

    private static void DrawRows(List<EstablishmentAffiliationDto> rows, Action<EstablishmentAffiliationDto, float> drawRow)
    {
        var rowWidth = CardRowWidth();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) DrawRowSeparator(AvatarSize + AvatarGap, rowWidth);
            using var id = ImRaii.PushId(rows[i].Id.ToString());
            drawRow(rows[i], rowWidth);
        }
    }

    private void DrawPendingRequestRow(EstablishmentAffiliationDto request, float rowWidth)
    {
        var accept = Loc.Get("CompactUi.Notifications.Accept");
        var decline = Loc.Get("CompactUi.Notifications.Decline");
        var acceptWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Check, accept);
        var declineWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Times, decline);
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : $"« {request.Message.Trim()} »";

        DrawListRow(ProfileTitle(request.RpFirstName, request.RpLastName, request.CharacterName),
            message ?? CharacterLine(request.CharacterName, request.WorldId), rowWidth,
            acceptWidth + ImGui.GetStyle().ItemSpacing.X + declineWidth, () =>
            {
                using (PushPrimaryButtonColors())
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, accept))
                        RunAffiliationAction(() => RespondToAffiliation(request.Id, accept: true));
                }
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, decline))
                    RunAffiliationAction(() => RespondToAffiliation(request.Id, accept: false));
            }, subtitleTooltip: message, portrait: MemberPortrait(request));
    }

    private void DrawMemberRow(EstablishmentAffiliationDto member, float rowWidth)
    {
        var remove = Loc.Get("Establishment.Affiliation.Remove");
        var removeWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.UserMinus, remove);
        var hidden = Loc.Get("Establishment.Affiliation.Status.Hidden");
        var trailingWidth = removeWidth + (member.ShowOnProfile ? 0f : BadgeWidth(hidden) + ImGui.GetStyle().ItemSpacing.X);

        DrawListRow(ProfileTitle(member.RpFirstName, member.RpLastName, member.CharacterName),
            CharacterLine(member.CharacterName, member.WorldId), rowWidth, trailingWidth, () =>
            {
                if (!member.ShowOnProfile)
                {
                    DrawBadge(hidden, ImGuiColors.DalamudGrey);
                    UiSharedService.AttachToolTip(Loc.Get("Establishment.Affiliation.Status.HiddenTooltip"));
                    ImGui.SameLine();
                }
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserMinus, remove))
                        RunAffiliationAction(() => RemoveAffiliation(member.Id));
                }
            }, portrait: MemberPortrait(member));
    }

    private async Task<string?> RespondToAffiliation(Guid affiliationId, bool accept)
        => await _apiController.EstablishmentAffiliationRespond(affiliationId, accept).ConfigureAwait(false)
            ? null
            : Loc.Get("Establishment.Affiliation.RespondFailed.Body");

    private async Task<string?> RemoveAffiliation(Guid affiliationId)
        => await _apiController.EstablishmentAffiliationRemove(affiliationId).ConfigureAwait(false)
            ? null
            : Loc.Get("Establishment.Affiliation.ActionFailed");

    private static string RequestResultText(EstablishmentAffiliationRequestResult result) => result switch
    {
        EstablishmentAffiliationRequestResult.Requested => Loc.Get("Establishment.Affiliation.Result.Requested"),
        EstablishmentAffiliationRequestResult.Accepted => Loc.Get("Establishment.Affiliation.Result.Accepted"),
        EstablishmentAffiliationRequestResult.AlreadyExists => Loc.Get("Establishment.Affiliation.Result.AlreadyExists"),
        EstablishmentAffiliationRequestResult.LimitReached
            => Loc.Get("Establishment.Affiliation.Result.LimitReached", EstablishmentAffiliationLimits.MaxDisplayedPerCharacter),
        EstablishmentAffiliationRequestResult.PendingLimitReached
            => Loc.Get("Establishment.Affiliation.Result.PendingLimitReached", EstablishmentAffiliationLimits.MaxPendingPerCharacter),
        EstablishmentAffiliationRequestResult.RateLimited => Loc.Get("Establishment.Affiliation.Result.RateLimited"),
        _ => Loc.Get("Establishment.Affiliation.Result.NotFound"),
    };

    private static string ProfileTitle(string? firstName, string? lastName, string characterName)
    {
        var rpName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrEmpty(rpName) ? characterName : rpName;
    }

    private string CharacterLine(string characterName, uint worldId)
        => worldId > 0 && _dalamudUtil.WorldData.Value.TryGetValue((ushort)worldId, out var worldName)
            ? $"{characterName} @ {worldName}"
            : characterName;

    private void DrawAffiliationNotice()
    {
        if (string.IsNullOrEmpty(_affiliationStatus)) return;
        ImGuiHelpers.ScaledDummy(6f);
        _uiSharedService.IconText(FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);
        ImGui.SameLine();
        UiSharedService.ColorTextWrapped(_affiliationStatus, ImGuiColors.DalamudYellow);
    }

    // Rangée de liste : avatar à initiales, titre et sous-titre, accessoire calé à droite et centré verticalement.
    private static float AvatarSize => 34f * ImGuiHelpers.GlobalScale;
    private static float AvatarGap => 10f * ImGuiHelpers.GlobalScale;

    private static float CardRowWidth()
        => ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().FramePadding.X + 4f * ImGuiHelpers.GlobalScale);

    private static void DrawListRow(string title, string? subtitle, float rowWidth, float trailingWidth, Action? drawTrailing,
        string? subtitleTooltip = null, IDalamudTextureWrap? portrait = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var lineHeight = ImGui.GetTextLineHeight();
        var textHeight = subtitle != null ? (lineHeight * 2f) + (2f * scale) : lineHeight;
        var rowHeight = MathF.Max(AvatarSize, MathF.Max(textHeight, ImGui.GetFrameHeight())) + (6f * scale);
        var start = ImGui.GetCursorPos();
        var screen = ImGui.GetCursorScreenPos();

        var avatarMin = screen + new Vector2(0f, (rowHeight - AvatarSize) * 0.5f);
        if (portrait != null)
        {
            var (uv0, uv1) = ProfileBanner.CoverUv(portrait.Width, portrait.Height, AvatarSize, AvatarSize);
            ImGui.GetWindowDrawList().AddImageRounded(portrait.Handle, avatarMin, avatarMin + new Vector2(AvatarSize),
                uv0, uv1, ImGui.GetColorU32(Vector4.One), AvatarSize * 0.5f);
        }
        else
        {
            DrawMonogram(ImGui.GetWindowDrawList(), avatarMin, AvatarSize, title);
        }

        var textX = start.X + AvatarSize + AvatarGap;
        var textWidth = MathF.Max(40f * scale, rowWidth - AvatarSize - AvatarGap - trailingWidth - AvatarGap);
        ImGui.SetCursorPos(new Vector2(textX, start.Y + ((rowHeight - textHeight) * 0.5f)));
        ImGui.TextUnformatted(UiSharedService.TruncateToWidth(title, textWidth));
        if (subtitle != null)
        {
            ImGui.SetCursorPosX(textX);
            var shown = UiSharedService.TruncateToWidth(subtitle, textWidth);
            ImGui.TextColored(ImGuiColors.DalamudGrey, shown);
            if (subtitleTooltip != null && !string.Equals(shown, subtitle, StringComparison.Ordinal))
                UiSharedService.AttachToolTip(subtitleTooltip);
        }

        if (drawTrailing != null)
        {
            ImGui.SetCursorPos(new Vector2(start.X + rowWidth - trailingWidth, start.Y + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f)));
            drawTrailing();
        }

        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowHeight));
        ImGui.Dummy(new Vector2(rowWidth, 0f));
    }

    private static bool DrawToggleRow(string label, ref bool value, float rowWidth)
    {
        var start = ImGui.GetCursorPos();
        var toggleWidth = MathF.Round(MathF.Round(ImGui.GetFrameHeight() * 0.82f) * 1.8f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(start.X + rowWidth - toggleWidth);
        return ToggleSwitch.Draw("##affiliationToggle", ref value);
    }

    private static void DrawRowSeparator(float inset, float rowWidth)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var pos = ImGui.GetCursorScreenPos() - new Vector2(0f, spacing * 0.5f);
        ImGui.GetWindowDrawList().AddLine(pos + new Vector2(inset, 0f), pos + new Vector2(rowWidth, 0f),
            ImGui.GetColorU32(UiSharedService.ThemeSeparator), MathF.Max(1f, ImGuiHelpers.GlobalScale));
    }

    private static void DrawMonogram(ImDrawListPtr drawList, Vector2 min, float size, string name)
    {
        var center = min + new Vector2(size * 0.5f);
        drawList.AddCircleFilled(center, size * 0.5f, ImGui.GetColorU32(UiSharedService.AccentColor with { W = 0.35f }));
        var initials = UiSharedService.GetInitials(name);
        if (initials.Length == 0) return;
        var textSize = ImGui.CalcTextSize(initials);
        drawList.AddText(center - (textSize * 0.5f), ImGui.GetColorU32(UiSharedService.ThemeNavTextActive), initials);
    }

    private static float BadgeWidth(string text) => ImGui.CalcTextSize(text).X + (16f * ImGuiHelpers.GlobalScale);

    private static void DrawBadge(string text, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var textSize = ImGui.CalcTextSize(text);
        var size = new Vector2(textSize.X + (16f * scale), textSize.Y + (4f * scale));
        var frameHeight = ImGui.GetFrameHeight();
        var min = ImGui.GetCursorScreenPos() + new Vector2(0f, (frameHeight - size.Y) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(color with { W = 0.18f }), size.Y * 0.5f);
        drawList.AddText(min + new Vector2(8f * scale, 2f * scale), ImGui.GetColorU32(color), text);
        ImGui.Dummy(new Vector2(size.X, frameHeight));
    }

    private static void DrawSectionHeader(string text)
    {
        using (UiSharedService.PushFontScale(UiSharedService.ContentFontScale))
            ImGui.TextColored(ImGuiColors.DalamudGrey, text);
    }

    private static void DrawSectionFooter(string text)
    {
        using (UiSharedService.PushFontScale(UiSharedService.ContentFontScale))
            UiSharedService.ColorTextWrapped(text, ImGuiColors.DalamudGrey);
    }

    private static ImRaii.ColorDisposable PushPrimaryButtonColors(bool condition = true)
    {
        var accent = UiSharedService.AccentColor;
        return ImRaii.PushColor(ImGuiCol.Button, accent, condition)
            .Push(ImGuiCol.ButtonHovered, Vector4.Lerp(accent, UiSharedService.ThemeNavTextActive, 0.15f), condition)
            .Push(ImGuiCol.ButtonActive, Vector4.Lerp(accent, UiSharedService.ThemeWindowBg, 0.2f), condition)
            .Push(ImGuiCol.Text, UiSharedService.ThemeNavTextActive, condition);
    }

    private bool DrawTrailingButton(FontAwesomeIcon icon, string label, bool primary = false, bool destructive = false)
    {
        var width = _uiSharedService.GetIconTextButtonSize(icon, label);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - width));
        using var primaryColors = PushPrimaryButtonColors(primary);
        using var destructiveColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, destructive);
        return _uiSharedService.IconTextButton(icon, label);
    }
}
