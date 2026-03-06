using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.UI.Handlers;

namespace UmbraSync.UI.Components;

public abstract class DrawPairBase
{
    protected readonly ApiController _apiController;
    protected readonly UidDisplayHandler _displayHandler;
    protected readonly UiSharedService _uiSharedService;
    protected Pair _pair;
    private readonly string _id;

    protected DrawPairBase(string id, Pair entry, ApiController apiController, UidDisplayHandler uIDDisplayHandler, UiSharedService uiSharedService)
    {
        _id = id;
        _pair = entry;
        _apiController = apiController;
        _displayHandler = uIDDisplayHandler;
        _uiSharedService = uiSharedService;
    }

    public string ImGuiID => _id;
    public Pair Pair => _pair;
    public string UID => _pair.UserData.UID;

    public void DrawPairedClient()
    {
        var style = ImGui.GetStyle();
        var padding = style.FramePadding;
        var spacing = style.ItemSpacing;
        var rowStartCursor = ImGui.GetCursorPos();
        var rowStartScreen = ImGui.GetCursorScreenPos();

        var pauseButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause);
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);
        var menuButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars);

        float pauseClusterWidth = Math.Max(pauseButtonSize.X, playButtonSize.X);
        float pauseClusterHeight = Math.Max(Math.Max(pauseButtonSize.Y, playButtonSize.Y), ImGui.GetFrameHeight());
        float reservedSpacing = style.ItemSpacing.X * 3f + style.FramePadding.X;
        float rightButtonWidth =
            menuButtonSize.X +
            pauseClusterWidth +
            reservedSpacing +
            GetRightSideExtraWidth();

        float availableWidth = Math.Max(ImGui.GetContentRegionAvail().X - rightButtonWidth, 1f);
        float textHeight = ImGui.GetFontSize();
        var presenceIconSize = UiSharedService.GetIconSize(FontAwesomeIcon.Moon);
        float iconHeight = presenceIconSize.Y;
        float contentHeight = Math.Max(textHeight, Math.Max(iconHeight, pauseClusterHeight));
        float rowHeight = contentHeight + padding.Y * 2f;
        float totalHeight = rowHeight + spacing.Y;

        var origin = ImGui.GetCursorStartPos();
        var top = origin.Y + rowStartCursor.Y;
        var bottom = top + totalHeight;
        var visibleHeight = UiSharedService.GetWindowContentRegionHeight();
        if (bottom < 0 || top > visibleHeight)
        {
            ImGui.SetCursorPos(new Vector2(rowStartCursor.X, rowStartCursor.Y + totalHeight));
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var backgroundColor = new Vector4(0x1C / 255f, 0x1C / 255f, 0x1C / 255f, 1f);
        var borderColor = new Vector4(0f, 0f, 0f, 0f);
        float rounding = Math.Max(style.FrameRounding, 7f * ImGuiHelpers.GlobalScale);

        var panelMin = rowStartScreen + new Vector2(0f, spacing.Y * 0.15f);
        var panelMax = panelMin + new Vector2(availableWidth, rowHeight - spacing.Y * 0.3f);
        drawList.AddRectFilled(panelMin, panelMax, ImGui.ColorConvertFloat4ToU32(backgroundColor), rounding);
        drawList.AddRect(panelMin, panelMax, ImGui.ColorConvertFloat4ToU32(borderColor), rounding);

        float iconTop = rowStartCursor.Y + (rowHeight - iconHeight) / 2f;
        // Nudge text slightly up to sit visually centered with the icon row.
        float textNudge = ImGui.GetFontSize() * 0.22f;
        float textTop = iconTop - textNudge;
        float buttonTop = rowStartCursor.Y + (rowHeight - pauseClusterHeight) / 2f;

        ImGui.SetCursorPos(new Vector2(rowStartCursor.X + padding.X, iconTop));
        DrawLeftSide(iconTop, iconTop);

        float leftReserved = GetLeftSideReservedWidth();
        float nameStartX = rowStartCursor.X + padding.X + leftReserved;

        var rightSide = DrawRightSide(buttonTop, buttonTop);

        ImGui.SameLine(nameStartX);
        ImGui.SetCursorPosY(textTop);
        // Draw the name/UID on the same vertical line as the icons
        DrawName(textTop, nameStartX, rightSide);

        ImGui.SetCursorPos(new Vector2(rowStartCursor.X, rowStartCursor.Y + totalHeight));
        ImGui.SetCursorPosX(rowStartCursor.X);
    }

    protected abstract void DrawLeftSide(float textPosY, float originalY);

    protected abstract float DrawRightSide(float textPosY, float originalY);

    protected virtual float GetRightSideExtraWidth() => 0f;

    protected virtual float GetLeftSideReservedWidth() => UiSharedService.GetIconSize(FontAwesomeIcon.Moon).X * 2f + ImGui.GetStyle().ItemSpacing.X * 1.5f;

    private void DrawName(float originalY, float leftSide, float rightSide)
    {
        _displayHandler.DrawPairText(_id, _pair, leftSide, originalY, () => rightSide - leftSide);
    }
}