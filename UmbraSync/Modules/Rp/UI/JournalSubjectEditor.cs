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
using UmbraSync.UI;
using UmbraSync.UI.Components;

namespace UmbraSync.Modules.Rp.UI;

public sealed class JournalSubjectEditor
{
    private readonly RpJournalService _journal;
    private readonly UiSharedService _uiSharedService;
    private readonly ILogger _logger;
    private readonly string _idPrefix;

    private string? _subjectKey;
    private string _newEntryText = string.Empty;
    private Guid? _editingEntryId;
    private string _editingEntryText = string.Empty;
    private string _newFieldLabel = string.Empty;
    private Task? _addEntryTask;

    public JournalSubjectEditor(RpJournalService journal, UiSharedService uiSharedService, ILogger logger, string idPrefix)
    {
        _journal = journal;
        _uiSharedService = uiSharedService;
        _logger = logger;
        _idPrefix = idPrefix;
    }

    public void Draw(JournalSubject subject)
    {
        var key = RpJournalQuery.SubjectKey(subject.TargetName, subject.TargetWorldId);
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _newEntryText = string.Empty;
            _editingEntryId = null;
            _newFieldLabel = string.Empty;
        }

        if (_addEntryTask is { IsCompleted: true })
        {
            if (_addEntryTask.IsFaulted) _logger.LogWarning(_addEntryTask.Exception, "Journal: ajout d'entrée impossible");
            _addEntryTask = null;
        }

        using var id = ImRaii.PushId(_idPrefix);
        DrawRelationCard(subject);
        ImGuiHelpers.ScaledDummy(6f);
        DrawSummaryCard(subject);
        ImGuiHelpers.ScaledDummy(6f);
        DrawFieldsCard(subject);
        ImGuiHelpers.ScaledDummy(6f);
        DrawChronicleCard(subject);
    }

    private static void CardBody(Action draw)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float rightInset = ImGui.GetStyle().FramePadding.X + 4f * scale + MathF.Max(1f, scale);
        UiSharedService.ShiftContentRight(-rightInset);
        try
        {
            draw();
        }
        finally
        {
            UiSharedService.ShiftContentRight(rightInset);
        }
    }

    private void DrawRelationCard(JournalSubject subject)
    {
        UiSharedService.DrawCard("journalRelation", () => CardBody(() =>
        {
            UiSharedService.DrawCardTitle(FontAwesomeIcon.Handshake, Loc.Get("Journal.Card.Relation"));
            ImGuiHelpers.ScaledDummy(2f);
            DrawTrustSelector(subject);
            ImGuiHelpers.ScaledDummy(4f);

            var relation = subject.Relation;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##journalRelationText", Loc.Get("Journal.Relation.Hint"), ref relation, RpJournalQuery.MaxRelationLength))
                subject.Relation = relation;
            if (ImGui.IsItemDeactivatedAfterEdit()) _journal.Touch(subject);
        }), stretchWidth: true);
    }

    private void DrawTrustSelector(JournalSubject subject)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float spacing = 4f * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        float segmentWidth = (avail - spacing * (JournalTrustStyle.Levels.Length - 1)) / JournalTrustStyle.Levels.Length;
        float height = MathF.Max(28f * scale, ImGui.GetFrameHeight());
        var drawList = ImGui.GetWindowDrawList();

        for (int i = 0; i < JournalTrustStyle.Levels.Length; i++)
        {
            var level = JournalTrustStyle.Levels[i];
            using var id = ImRaii.PushId(i);
            if (i > 0) ImGui.SameLine(0, spacing);

            var pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton("##trust", new Vector2(segmentWidth, height));
            bool hovered = ImGui.IsItemHovered();
            bool active = subject.Trust == level;
            var color = JournalTrustStyle.Color(level);

            var bg = active ? UiSharedService.WithAlpha(color, 0.30f) : hovered ? UiSharedService.ThemeCapsuleHovered : UiSharedService.ThemeCapsule;
            float rounding = height / 2f;
            drawList.AddRectFilled(pos, pos + new Vector2(segmentWidth, height), ImGui.GetColorU32(bg), rounding);
            drawList.AddRect(pos, pos + new Vector2(segmentWidth, height),
                ImGui.GetColorU32(active ? color : UiSharedService.ThemeCapsuleBorder), rounding, ImDrawFlags.None, scale);

            var label = JournalTrustStyle.Label(level);
            var labelSize = ImGui.CalcTextSize(label);
            var textColor = active ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey;
            drawList.PushClipRect(pos, pos + new Vector2(segmentWidth, height), true);
            drawList.AddText(new Vector2(pos.X + MathF.Max(4f * scale, (segmentWidth - labelSize.X) / 2f), pos.Y + (height - labelSize.Y) / 2f),
                ImGui.GetColorU32(textColor), label);
            drawList.PopClipRect();

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                UiSharedService.AttachToolTip(JournalTrustStyle.Tooltip(level));
            }

            if (clicked && !active)
            {
                subject.Trust = level;
                _journal.Touch(subject);
            }
        }
    }

    private void DrawSummaryCard(JournalSubject subject)
    {
        UiSharedService.DrawCard("journalSummary", () => CardBody(() =>
        {
            UiSharedService.DrawCardTitle(FontAwesomeIcon.BookOpen, Loc.Get("Journal.Card.Summary"));
            ImGuiHelpers.ScaledDummy(2f);
            var summary = subject.Summary;
            if (ResizableTextArea.Draw("##journalSummaryText", ref summary, RpJournalQuery.MaxSummaryLength, 120f, out bool committed))
                subject.Summary = summary;
            if (committed) _journal.Touch(subject);
            if (string.IsNullOrEmpty(subject.Summary))
                UiSharedService.ColorTextWrapped(Loc.Get("Journal.Summary.Hint"), ImGuiColors.DalamudGrey3);
        }), stretchWidth: true);
    }

    private void DrawFieldsCard(JournalSubject subject)
    {
        UiSharedService.DrawCard("journalFields", () => CardBody(() =>
        {
            UiSharedService.DrawCardTitle(FontAwesomeIcon.ListUl, Loc.Get("Journal.Card.Fields"));
            ImGuiHelpers.ScaledDummy(2f);

            float scale = ImGuiHelpers.GlobalScale;
            float labelWidth = MathF.Min(160f * scale, ImGui.GetContentRegionAvail().X * 0.35f);
            int? removeIndex = null;

            for (int i = 0; i < subject.CustomFields.Count; i++)
            {
                var field = subject.CustomFields[i];
                using var id = ImRaii.PushId(i);

                var label = field.Label;
                ImGui.SetNextItemWidth(labelWidth);
                if (ImGui.InputText("##label", ref label, RpJournalQuery.MaxFieldLabelLength)) field.Label = label;
                if (ImGui.IsItemDeactivatedAfterEdit()) _journal.Touch(subject);

                ImGui.SameLine();
                var value = field.Value;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
                if (ImGui.InputText("##value", ref value, RpJournalQuery.MaxFieldValueLength)) field.Value = value;
                if (ImGui.IsItemDeactivatedAfterEdit()) _journal.Touch(subject);

                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Times)) removeIndex = i;
                UiSharedService.AttachToolTip(Loc.Get("Journal.Field.Remove"));
            }

            if (removeIndex is { } index)
            {
                subject.CustomFields.RemoveAt(index);
                _journal.Touch(subject);
            }

            if (subject.CustomFields.Count >= RpJournalQuery.MaxCustomFields) return;

            float rowY = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(rowY + MathF.Max(0f, (CapsuleButton.HitRegionHeight - ImGui.GetFrameHeight()) / 2f));
            ImGui.SetNextItemWidth(labelWidth);
            bool submit = ImGui.InputTextWithHint("##newFieldLabel", Loc.Get("Journal.Field.NewHint"), ref _newFieldLabel,
                RpJournalQuery.MaxFieldLabelLength, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_newFieldLabel)))
            {
                if (CapsuleButton.Draw(Loc.Get("Journal.Field.Add"), FontAwesomeIcon.Plus)) submit = true;
            }

            if (submit && !string.IsNullOrWhiteSpace(_newFieldLabel))
            {
                subject.CustomFields.Add(new JournalField { Label = _newFieldLabel.Trim() });
                _newFieldLabel = string.Empty;
                _journal.Touch(subject);
            }
        }), stretchWidth: true);
    }

    private void DrawChronicleCard(JournalSubject subject)
    {
        UiSharedService.DrawCard("journalChronicle", () => CardBody(() =>
        {
            int count = subject.Entries.Count;
            UiSharedService.DrawCardTitle(FontAwesomeIcon.Feather, Loc.Get("Journal.Card.Chronicle"),
                trailing: count > 0 ? count.ToString(CultureInfo.CurrentCulture) : null);
            ImGuiHelpers.ScaledDummy(2f);

            ResizableTextArea.Draw("##journalNewEntry", ref _newEntryText, RpJournalQuery.MaxEntryLength, 70f, 50f, 300f);
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_newEntryText) || _addEntryTask != null))
            {
                if (CapsuleButton.Draw(Loc.Get("Journal.Entry.Add"), FontAwesomeIcon.Plus))
                {
                    _addEntryTask = _journal.AddEntryAsync(subject, _newEntryText);
                    _newEntryText = string.Empty;
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("Journal.Entry.Add.Tooltip"));

            if (count == 0)
            {
                ImGuiHelpers.ScaledDummy(2f);
                UiSharedService.ColorTextWrapped(Loc.Get("Journal.Entry.Empty"), ImGuiColors.DalamudGrey3);
                return;
            }

            Guid? removeId = null;
            for (int i = count - 1; i >= 0; i--)
            {
                var entry = subject.Entries[i];
                using var id = ImRaii.PushId(entry.Id.ToString());
                ImGuiHelpers.ScaledDummy(4f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2f);

                var meta = entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                if (!string.IsNullOrEmpty(entry.Place)) meta += " · " + entry.Place;
                UiSharedService.ColorText(meta, ImGuiColors.DalamudGrey);

                bool editing = _editingEntryId == entry.Id;
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X
                    - (ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.X)));
                if (_uiSharedService.IconButton(editing ? FontAwesomeIcon.Check : FontAwesomeIcon.Pen))
                {
                    if (editing)
                    {
                        var text = RpJournalQuery.Clip(_editingEntryText.Trim(), RpJournalQuery.MaxEntryLength);
                        if (text.Length > 0)
                        {
                            entry.Text = text;
                            _journal.Touch(subject);
                        }
                        _editingEntryId = null;
                    }
                    else
                    {
                        _editingEntryId = entry.Id;
                        _editingEntryText = entry.Text;
                    }
                }
                UiSharedService.AttachToolTip(Loc.Get(editing ? "Journal.Entry.Save" : "Journal.Entry.Edit"));

                ImGui.SameLine();
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                {
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash)) removeId = entry.Id;
                }
                UiSharedService.AttachToolTip(Loc.Get("Journal.Entry.Delete.Tooltip"));

                if (editing)
                    ResizableTextArea.Draw("##editEntry", ref _editingEntryText, RpJournalQuery.MaxEntryLength, 90f, 50f, 400f);
                else
                    UiSharedService.TextWrapped(entry.Text);
            }

            if (removeId is { } toRemove)
            {
                if (_editingEntryId == toRemove) _editingEntryId = null;
                _journal.DeleteEntry(subject, toRemove);
            }
        }), stretchWidth: true);
    }
}
