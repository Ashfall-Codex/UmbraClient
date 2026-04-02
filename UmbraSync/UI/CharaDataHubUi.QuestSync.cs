using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.Localization;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private string _questSessionJoinId = string.Empty;
    private string _questSessionCurrentId = string.Empty;
    private readonly List<UserData> _questSessionParticipants = [];

    private void InitQuestSyncSubscriptions()
    {
        Mediator.Subscribe<QuestSessionJoinMessage>(this, (msg) =>
        {
            if (!_questSessionParticipants.Any(u => string.Equals(u.UID, msg.UserData.UID, StringComparison.Ordinal)))
                _questSessionParticipants.Add(msg.UserData);
        });

        Mediator.Subscribe<QuestSessionLeaveMessage>(this, (msg) =>
        {
            _questSessionParticipants.RemoveAll(u => string.Equals(u.UID, msg.UserData.UID, StringComparison.Ordinal));
        });
    }

    private void DrawQuestSync()
    {
        if (!_uiSharedService.ApiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            UiSharedService.DrawGroupedCenteredColorText(Loc.Get("QuestSync.ServerRequired"), UiSharedService.AccentColor);
            ImGuiHelpers.ScaledDummy(5);
        }

        _uiSharedService.BigText(Loc.Get("QuestSync.Title"));
        DrawHelpFoldout(Loc.Get("QuestSync.HelpText"));

        using var disabled = ImRaii.Disabled(!_uiSharedService.ApiController.IsConnected);

        UiSharedService.DistanceSeparator();
        _uiSharedService.BigText(Loc.Get("QuestSync.Controls"));

        if (string.IsNullOrEmpty(_questSessionCurrentId))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, Loc.Get("QuestSync.CreateSession")))
            {
                _ = CreateQuestSession();
            }

            ImGuiHelpers.ScaledDummy(5);
            ImGui.SetNextItemWidth(250);
            ImGui.InputTextWithHint("##questSessionId", Loc.Get("QuestSync.SessionCode"), ref _questSessionJoinId, 30);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, Loc.Get("QuestSync.JoinSession")))
            {
                _ = JoinQuestSession();
            }
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Loc.Get("QuestSync.Session"));
            ImGui.SameLine();
            UiSharedService.ColorTextWrapped(_questSessionCurrentId, ImGuiColors.ParsedGreen);
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Clipboard))
            {
                ImGui.SetClipboardText(_questSessionCurrentId);
            }
            UiSharedService.AttachToolTip(Loc.Get("QuestSync.CopySessionId"));

            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowLeft, Loc.Get("QuestSync.LeaveSession")))
                {
                    _ = LeaveQuestSession();
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("QuestSync.LeaveSessionHint"));
        }

        UiSharedService.DistanceSeparator();
        ImGui.TextUnformatted(Loc.Get("QuestSync.Participants"));
        ImGuiHelpers.ScaledDummy(3);

        if (string.IsNullOrEmpty(_questSessionCurrentId) || _questSessionParticipants.Count == 0)
        {
            UiSharedService.ColorTextWrapped(Loc.Get("QuestSync.NoActiveSession"), ImGuiColors.DalamudGrey3);
        }
        else
        {
            foreach (var user in _questSessionParticipants)
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextColored(ImGuiColors.ParsedGreen, FontAwesomeIcon.User.ToIconString());
                ImGui.SameLine();
                UiSharedService.ColorText(user.AliasOrUID, ImGuiColors.ParsedGreen);
            }
        }
    }

    private async Task CreateQuestSession()
    {
        try
        {
            var sessionId = await _uiSharedService.ApiController.QuestSessionCreate("default", "Quest Sync Session").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(sessionId))
            {
                _questSessionCurrentId = sessionId;
                _questSessionParticipants.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating quest session");
        }
    }

    private async Task JoinQuestSession()
    {
        if (string.IsNullOrWhiteSpace(_questSessionJoinId)) return;
        try
        {
            var participants = await _uiSharedService.ApiController.QuestSessionJoin(_questSessionJoinId.Trim()).ConfigureAwait(false);
            _questSessionCurrentId = _questSessionJoinId.Trim();
            _questSessionParticipants.Clear();
            _questSessionParticipants.AddRange(participants);
            _questSessionJoinId = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error joining quest session");
        }
    }

    private async Task LeaveQuestSession()
    {
        try
        {
            await _uiSharedService.ApiController.QuestSessionLeave().ConfigureAwait(false);
            _questSessionCurrentId = string.Empty;
            _questSessionParticipants.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error leaving quest session");
        }
    }
}
