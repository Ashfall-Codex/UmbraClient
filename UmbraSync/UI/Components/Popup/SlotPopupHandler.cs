using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.Slot;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI.Components.Popup;

public class SlotPopupHandler : IPopupHandler
{
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly SlotService _slotService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly MareMediator _mediator;
    private SlotInfoResponseDto? _slotInfo;
    private bool _joinPermanently = false;

    public SlotPopupHandler(ApiController apiController, UiSharedService uiSharedService, SlotService slotService, DalamudUtilService dalamudUtilService, MareMediator mediator)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _slotService = slotService;
        _dalamudUtilService = dalamudUtilService;
        _mediator = mediator;
    }

    public Vector2 PopupSize => new(550, 450);
    public bool ShowClose => false;

    public void Open(OpenSlotPromptMessage msg)
    {
        _slotInfo = msg.SlotInfo;
        _joinPermanently = false;
    }

    public void DrawContent()
    {
        if (_slotInfo == null) return;

        var titleColor = new Vector4(0.75f, 0.5f, 1.0f, 1.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 8));
        var originalFontScale = ImGui.GetFont().Scale;
        ImGui.SetWindowFontScale(1.3f);

        var titleTextSize = ImGui.CalcTextSize(_slotInfo.SlotName);
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(16, 8);
        var rectSize = new Vector2(titleTextSize.X + padding.X * 2, titleTextSize.Y + padding.Y * 2);
        var rectPosX = (windowWidth - rectSize.X) * 0.5f;
        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var rectTopLeft = new Vector2(cursorScreenPos.X + rectPosX, cursorScreenPos.Y);
        var rectBottomRight = new Vector2(rectTopLeft.X + rectSize.X, rectTopLeft.Y + rectSize.Y);
        var rectColor = ImGui.GetColorU32(new Vector4(0.75f, 0.5f, 1.0f, 1.0f)); // Violet opaque

        ImGui.GetWindowDrawList().AddRect(rectTopLeft, rectBottomRight, rectColor, 8.0f, ImDrawFlags.None, 2.5f);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + rectPosX + padding.X);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + padding.Y);

        ImGui.TextColored(titleColor, _slotInfo.SlotName);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + padding.Y);

        ImGui.SetWindowFontScale(originalFontScale);
        ImGui.PopStyleVar();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawInfoSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawAboutSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSyncshellSection();
    }

    private void DrawInfoSection()
    {
        if (_slotInfo == null) return;

        var iconColor = new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
        var textColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        if (_slotInfo.Location != null)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(iconColor, FontAwesomeIcon.MapMarkerAlt.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1.0f), Loc.Get("SlotPopup.SlotLocation"));
            ImGui.SameLine();

            string worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)_slotInfo.Location.ServerId, out var world) ? world : _slotInfo.Location.ServerId.ToString();
            string territoryName = _dalamudUtilService.TerritoryData.Value.TryGetValue(_slotInfo.Location.TerritoryId, out var territory) ? territory : _slotInfo.Location.TerritoryId.ToString();
            string locationText = $"{worldName}, {territoryName}, Secteur {_slotInfo.Location.WardId} - Emplacement {_slotInfo.Location.PlotId}";
            ImGui.TextColored(textColor, locationText);
        }

        if (_slotInfo.AssociatedSyncshell != null)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(iconColor, FontAwesomeIcon.User.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1.0f), Loc.Get("SlotPopup.Host"));
            ImGui.SameLine();
            ImGui.TextColored(textColor, _slotInfo.AssociatedSyncshell.Name);
        }
    }

    private void DrawAboutSection()
    {
        if (_slotInfo == null) return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
        ImGui.Text(Loc.Get("SlotPopup.AboutSlot"));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        if (!string.IsNullOrEmpty(_slotInfo.SlotDescription))
        {
            var descColor = new Vector4(0.85f, 0.85f, 0.85f, 1.0f);

            var cursorPos = ImGui.GetCursorScreenPos();
            var windowSize = ImGui.GetContentRegionAvail();
            var boxHeight = 80f;
            var bgColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.18f, 0.9f));

            ImGui.GetWindowDrawList().AddRectFilled(
                cursorPos,
                new Vector2(cursorPos.X + windowSize.X, cursorPos.Y + boxHeight),
                bgColor,
                4.0f
            );

            ImGui.PushStyleColor(ImGuiCol.Text, descColor);
            ImGui.BeginChild("DescriptionBox", new Vector2(windowSize.X, boxHeight), false);
            ImGui.PushTextWrapPos();
            ImGui.Text(_slotInfo.SlotDescription);
            ImGui.PopTextWrapPos();
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }
    }

    private void DrawSyncshellSection()
    {
        if (_slotInfo == null) return;

        if (_slotInfo.AssociatedSyncshell != null)
        {
            var infoColor = new Vector4(0.9f, 0.9f, 0.7f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Text, infoColor);
            ImGui.PushTextWrapPos();
            ImGui.Text(Loc.Get("SlotPopup.SlotRegistered"));
            ImGui.Spacing();

            if (!_joinPermanently)
            {
                ImGui.Text(Loc.Get("SlotPopup.AutoLeaveNotice"));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 1.0f, 0.5f, 1.0f));
                ImGui.Text(Loc.Get("SlotPopup.PermanentJoinNotice"));
                ImGui.PopStyleColor();
            }

            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();

            ImGui.Spacing();
            ImGui.Spacing();

            var checkboxColor = new Vector4(0.9f, 0.9f, 0.9f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Text, checkboxColor);
            ImGui.Checkbox(Loc.Get("SlotPopup.JoinPermanentlyCheckbox"), ref _joinPermanently);
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.Get("SlotPopup.JoinPermanentlyTooltip"));
            }

            ImGui.Spacing();
            ImGui.Spacing();

            var buttonWidth = 200f;
            var buttonHeight = 45f;
            var spacing = 10f;
            var totalWidth = (buttonWidth * 2) + spacing;
            var cursorPosX = (ImGui.GetContentRegionAvail().X - totalWidth) * 0.5f;

            if (cursorPosX > 0)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorPosX);

            var joinText = Loc.Get("SlotPopup.JoinButton");
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, joinText, buttonWidth, isInPopup: true, buttonColor: new Vector4(0.2f, 0.6f, 0.2f, 1.0f), height: buttonHeight))
            {
                var slotName = _slotInfo.SlotName;
                _ = Task.Run(async () =>
                {
                    if (_joinPermanently)
                    {
                        // Join permanent : rejoindre directement la syncshell sans auto-leave
                        var groupPasswordDto = new GroupPasswordDto(new GroupData(_slotInfo.AssociatedSyncshell.Gid), string.Empty);
                        await _apiController.GroupJoin(groupPasswordDto).ConfigureAwait(false);
                    }
                    else
                    {
                        // Join temporaire : système actuel avec auto-leave
                        _slotService.MarkJoinedViaSlot(_slotInfo.AssociatedSyncshell);
                        await _apiController.SlotJoin(_slotInfo.SlotId).ConfigureAwait(false);
                    }
                    
                    // Notification toast de confirmation
                    _mediator.Publish(new NotificationMessage(
                        Loc.Get("SlotPopup.Title"),
                        string.Format(Loc.Get("Slot.Toast.Joined"), slotName),
                        NotificationType.Success));
                });
                ImGui.CloseCurrentPopup();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.Get("SlotPopup.JoinToolTip"));
            }

            ImGui.SameLine(0, spacing);
            var closeText = Loc.Get("SlotPopup.CloseButton");
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, closeText, buttonWidth, isInPopup: true, buttonColor: new Vector4(0.6f, 0.2f, 0.2f, 1.0f), height: buttonHeight))
            {
                _slotService.DeclineSlot(_slotInfo);
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.6f, 0.4f, 1.0f));
            ImGui.TextWrapped(Loc.Get("SlotPopup.NoSyncshell"));
            ImGui.PopStyleColor();
        }
    }
}