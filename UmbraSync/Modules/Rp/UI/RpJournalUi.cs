using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.Modules.Rp.Services;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;
using UmbraSync.UI.Components;

namespace UmbraSync.Modules.Rp.UI;

public sealed class RpJournalUi : WindowMediatorSubscriberBase
{
    private const int TrustFilterAll = -1;

    private readonly RpJournalService _journal;
    private readonly PairManager _pairManager;
    private readonly UiSharedService _uiSharedService;
    private readonly JournalSubjectEditor _editor;

    private string _search = string.Empty;
    private int _trustFilter = TrustFilterAll;
    private string? _selectedKey;
    private Task<(string Name, uint WorldId, string? Uid)?>? _targetTask;
    private string? _statusMessage;

    public RpJournalUi(ILogger<RpJournalUi> logger, MareMediator mediator, RpJournalService journal, PairManager pairManager,
        UiSharedService uiSharedService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, Loc.Get("Journal.Title") + "###UmbraSyncRpJournal", performanceCollectorService)
    {
        _journal = journal;
        _pairManager = pairManager;
        _uiSharedService = uiSharedService;
        _editor = new JournalSubjectEditor(journal, uiSharedService, logger, "journalWindow");

        SizeConstraints = new()
        {
            MinimumSize = new(720, 460),
            MaximumSize = new(1600, 2000),
        };

        Mediator.Subscribe<OpenJournalMessage>(this, OnOpenRequested);
    }

    private void OnOpenRequested(OpenJournalMessage msg)
    {
        IsOpen = true;
        if (string.IsNullOrEmpty(msg.CharName) || msg.WorldId is not > 0) return;

        var subject = _journal.GetOrCreateSubject(msg.CharName, msg.WorldId.Value, msg.Uid, msg.RpName);
        if (subject == null)
        {
            _statusMessage = Loc.Get("Journal.NotLoggedIn");
            return;
        }

        Select(subject);
        _search = string.Empty;
        _trustFilter = TrustFilterAll;
    }

    private void Select(JournalSubject subject)
    {
        var key = RpJournalQuery.SubjectKey(subject.TargetName, subject.TargetWorldId);
        if (string.Equals(key, _selectedKey, StringComparison.Ordinal)) return;
        _selectedKey = key;
    }

    protected override void DrawInternal()
    {
        if (!_journal.HasOwner)
        {
            ImGuiHelpers.ScaledDummy(8f);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("Journal.NotLoggedIn"), ImGuiColors.DalamudGrey);
            return;
        }

        ConsumeTargetTask();
        DrawHeader();
        ImGuiHelpers.ScaledDummy(4f);

        float scale = ImGuiHelpers.GlobalScale;
        float listWidth = MathF.Min(280f * scale, ImGui.GetContentRegionAvail().X * 0.38f);

        using (var list = ImRaii.Child("##journalList", new Vector2(listWidth, -1), false))
        {
            if (list) DrawSubjectList();
        }

        ImGui.SameLine(0, 10f * scale);

        using (var detail = ImRaii.Child("##journalDetail", new Vector2(-1, -1), false))
        {
            if (detail) DrawDetail();
        }
    }

    private void DrawHeader()
    {
        _uiSharedService.BigText(string.Format(CultureInfo.CurrentCulture, Loc.Get("Journal.Header"), _journal.OwnerName));
        int count = _journal.SubjectCount;
        UiSharedService.ColorText(string.Format(CultureInfo.CurrentCulture,
            Loc.Get(count == 1 ? "Journal.Header.CountOne" : "Journal.Header.Count"), count), ImGuiColors.DalamudGrey);
        ImGui.SameLine();
        UiSharedService.ColorText("·", ImGuiColors.DalamudGrey3);
        ImGui.SameLine();
        UiSharedService.ColorText(Loc.Get("Journal.LocalOnly"), ImGuiColors.DalamudGrey3);
        UiSharedService.AttachToolTip(Loc.Get("Journal.LocalOnly.Tooltip"));

        if (!string.IsNullOrEmpty(_statusMessage))
            UiSharedService.ColorTextWrapped(_statusMessage!, ImGuiColors.DalamudYellow);
    }

    private void DrawSubjectList()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##journalSearch", Loc.Get("Journal.SearchHint"), ref _search, 100);

        ImGui.SetNextItemWidth(-1);
        var filterLabel = _trustFilter == TrustFilterAll ? Loc.Get("Journal.Filter.All") : JournalTrustStyle.Label((JournalTrust)_trustFilter);
        using (var combo = ImRaii.Combo("##journalTrustFilter", filterLabel))
        {
            if (combo)
            {
                if (ImGui.Selectable(Loc.Get("Journal.Filter.All"), _trustFilter == TrustFilterAll)) _trustFilter = TrustFilterAll;
                foreach (var level in JournalTrustStyle.Levels)
                {
                    using var id = ImRaii.PushId((int)level);
                    if (ImGui.Selectable(JournalTrustStyle.Label(level), _trustFilter == (int)level)) _trustFilter = (int)level;
                }
            }
        }

        using (ImRaii.Disabled(_targetTask != null))
        {
            if (CapsuleButton.Draw(Loc.Get("Journal.AddTarget"), FontAwesomeIcon.Crosshairs))
            {
                _statusMessage = null;
                _targetTask = _journal.GetTargetPlayerAsync();
            }
        }
        UiSharedService.AttachToolTip(Loc.Get("Journal.AddTarget.Tooltip"));

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        var trust = _trustFilter == TrustFilterAll ? (JournalTrust?)null : (JournalTrust)_trustFilter;
        var subjects = _journal.Subjects(_search, trust);
        if (subjects.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(6f);
            UiSharedService.ColorTextWrapped(
                _journal.SubjectCount == 0 ? Loc.Get("Journal.Empty") : Loc.Get("Journal.NoResult"), ImGuiColors.DalamudGrey);
            return;
        }

        using var rows = ImRaii.Child("##journalRows", new Vector2(-1, -1), false);
        if (!rows) return;

        foreach (var subject in subjects)
        {
            var key = RpJournalQuery.SubjectKey(subject.TargetName, subject.TargetWorldId);
            using var id = ImRaii.PushId(key);
            DrawSubjectRow(subject, string.Equals(key, _selectedKey, StringComparison.Ordinal));
        }
    }

    private void DrawSubjectRow(JournalSubject subject, bool selected)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float lineHeight = ImGui.GetTextLineHeight();
        float rowHeight = lineHeight * 2f + 8f * scale;
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;

        if (ImGui.InvisibleButton("##row", new Vector2(width, rowHeight)))
            Select(subject);
        bool hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        if (selected || hovered)
        {
            var bg = selected ? UiSharedService.WithAlpha(UiSharedService.AccentColor, 0.22f) : UiSharedService.ThemeCapsuleHovered;
            drawList.AddRectFilled(start, start + new Vector2(width, rowHeight), ImGui.GetColorU32(bg), UiSharedService.RadiusControl * 2f * scale);
        }

        float pad = 8f * scale;
        float dotRadius = 4.5f * scale;
        var dotCenter = new Vector2(start.X + pad + dotRadius, start.Y + 4f * scale + lineHeight / 2f);
        var trustColor = ImGui.GetColorU32(JournalTrustStyle.Color(subject.Trust));
        if (subject.Trust == JournalTrust.Unknown)
            drawList.AddCircle(dotCenter, dotRadius, trustColor, 16, 1.5f * scale);
        else
            drawList.AddCircleFilled(dotCenter, dotRadius, trustColor, 16);

        float textX = start.X + pad + dotRadius * 2f + 8f * scale;
        float maxTextWidth = start.X + width - pad - textX;
        var title = RpJournalQuery.DisplayName(subject);
        drawList.PushClipRect(new Vector2(textX, start.Y), new Vector2(start.X + width - pad, start.Y + rowHeight), true);
        drawList.AddText(new Vector2(textX, start.Y + 4f * scale), ImGui.GetColorU32(ImGuiColors.DalamudWhite), title);

        var subtitle = string.IsNullOrWhiteSpace(subject.Relation)
            ? _journal.WorldName(subject.TargetWorldId)
            : subject.Relation + " · " + _journal.WorldName(subject.TargetWorldId);
        drawList.AddText(new Vector2(textX, start.Y + 4f * scale + lineHeight + 2f * scale), ImGui.GetColorU32(ImGuiColors.DalamudGrey), subtitle);
        drawList.PopClipRect();

        if (subject.Pinned && maxTextWidth > 40f * scale)
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var icon = FontAwesomeIcon.Thumbtack.ToIconString();
                var size = ImGui.CalcTextSize(icon);
                drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 0.8f,
                    new Vector2(start.X + width - pad - size.X * 0.8f, start.Y + 5f * scale),
                    ImGui.GetColorU32(ImGuiColors.DalamudGrey), icon);
            }
        }

        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGuiHelpers.ScaledDummy(1f);
    }

    private JournalSubject? SelectedSubject()
    {
        var journal = _journal.CurrentJournal;
        if (journal == null || _selectedKey == null) return null;
        return journal.Subjects.TryGetValue(_selectedKey, out var subject) ? subject : null;
    }

    private void DrawDetail()
    {
        var subject = SelectedSubject();
        if (subject == null)
        {
            ImGuiHelpers.ScaledDummy(30f);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("Journal.SelectHint"), ImGuiColors.DalamudGrey);
            return;
        }

        DrawIdentity(subject);
        ImGuiHelpers.ScaledDummy(6f);
        _editor.Draw(subject);
    }

    private void DrawIdentity(JournalSubject subject)
    {
        _uiSharedService.BigText(RpJournalQuery.DisplayName(subject));

        var identity = subject.TargetName + " · " + _journal.WorldName(subject.TargetWorldId);
        UiSharedService.ColorText(identity, ImGuiColors.DalamudGrey);
        if (subject.LastSeenUtc is { } seen)
        {
            var seenText = string.Format(CultureInfo.CurrentCulture, Loc.Get("Journal.LastSeen"),
                seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            if (!string.IsNullOrEmpty(subject.LastSeenPlace)) seenText += " · " + subject.LastSeenPlace;
            UiSharedService.ColorText(seenText, ImGuiColors.DalamudGrey3);
        }

        ImGuiHelpers.ScaledDummy(2f);
        var pair = string.IsNullOrEmpty(subject.LastKnownUid) ? null : _pairManager.GetPairByUID(subject.LastKnownUid);
        if (pair != null)
        {
            if (CapsuleButton.Draw(Loc.Get("Journal.OpenProfile"), FontAwesomeIcon.IdCard))
                Mediator.Publish(new ProfileOpenStandaloneMessage(pair, subject.TargetName, subject.TargetWorldId));
            ImGui.SameLine();
        }

        if (CapsuleButton.Draw(Loc.Get(subject.Pinned ? "Journal.Unpin" : "Journal.Pin"), FontAwesomeIcon.Thumbtack))
        {
            subject.Pinned = !subject.Pinned;
            _journal.Touch(subject);
        }
        ImGui.SameLine();

        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (CapsuleButton.Draw(Loc.Get("Journal.Delete"), FontAwesomeIcon.Trash, CapsuleButtonRole.Destructive))
            {
                _journal.DeleteSubject(subject);
                _selectedKey = null;
            }
        }
        UiSharedService.AttachToolTip(Loc.Get("Journal.Delete.Tooltip"));
    }

    private void ConsumeTargetTask()
    {
        if (!UiSharedService.TryConsumeUiTask(ref _targetTask, _logger, out var target)) return;
        if (target is not { } t)
        {
            _statusMessage = Loc.Get("Journal.AddTarget.NoTarget");
            return;
        }

        var subject = _journal.GetOrCreateSubject(t.Name, t.WorldId, t.Uid);
        if (subject == null) return;
        Select(subject);
        _search = string.Empty;
        _trustFilter = TrustFilterAll;
    }
}
