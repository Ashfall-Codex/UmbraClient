using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
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

    // Bord droit imposé par un conteneur plus étroit que la fenêtre (tiroir des favoris).
    [ThreadStatic] private static float? _rowRightLimit;

    public static IDisposable PushRowRightLimit(float screenX)
    {
        var previous = _rowRightLimit;
        _rowRightLimit = screenX;
        return new RowLimitScope(() => _rowRightLimit = previous);
    }

    private sealed class RowLimitScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public string ImGuiID => _id;
    public Pair Pair => _pair;
    public string UID => _pair.UserData.UID;

    public float GetRowTotalHeight()
    {
        var style = ImGui.GetStyle();
        var pauseButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause);
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);
        float pauseClusterHeight = Math.Max(Math.Max(pauseButtonSize.Y, playButtonSize.Y), ImGui.GetFrameHeight());
        float iconHeight = UiSharedService.GetIconSize(FontAwesomeIcon.Moon).Y;
        float contentHeight = Math.Max(ImGui.GetFontSize(), Math.Max(iconHeight, pauseClusterHeight));
        if (_displayHandler.WantsTallRow(_pair))
            contentHeight = Math.Max(contentHeight, UidDisplayHandler.AvatarSize);
        return contentHeight + style.FramePadding.Y * 2f + style.ItemSpacing.Y;
    }

    public void DrawPairedClient()
    {
        var style = ImGui.GetStyle();
        var padding = style.FramePadding;
        var spacing = style.ItemSpacing;
        var rowStartCursor = ImGui.GetCursorPos();
        var rowStartScreen = ImGui.GetCursorScreenPos();

        var pauseButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause);
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);

        float pauseClusterHeight = Math.Max(Math.Max(pauseButtonSize.Y, playButtonSize.Y), ImGui.GetFrameHeight());
        float textHeight = ImGui.GetFontSize();
        var presenceIconSize = UiSharedService.GetIconSize(FontAwesomeIcon.Moon);
        float iconHeight = presenceIconSize.Y;
        float contentHeight = Math.Max(textHeight, Math.Max(iconHeight, pauseClusterHeight));
        if (_displayHandler.WantsTallRow(_pair))
            contentHeight = Math.Max(contentHeight, UidDisplayHandler.AvatarSize);
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
        float rounding = Math.Max(style.FrameRounding, 7f * ImGuiHelpers.GlobalScale);

        // Le fond couvre toute la ligne, boutons compris : même longueur pour toutes les lignes,
        // qu'elles aient un bouton supplémentaire ou non. Il s'éclaircit au survol.
        var panelMin = rowStartScreen + new Vector2(0f, spacing.Y * 0.15f);
        // Bord droit pris sur la fenêtre et non sur l'espace restant : toutes les lignes s'arrêtent au même X.
        float rowRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (_rowRightLimit is { } limit)
            rowRight = MathF.Min(rowRight, limit);
        var panelMax = new Vector2(MathF.Max(rowRight, panelMin.X + 1f), panelMin.Y + rowHeight - spacing.Y * 0.3f);
        bool rowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && ImGui.IsMouseHoveringRect(panelMin, panelMax);
        drawList.AddRectFilled(panelMin, panelMax,
            ImGui.GetColorU32(rowHovered ? UiSharedService.ThemeFrameBgHovered with { W = 0.45f } : UiSharedService.ThemeHeaderBg), rounding);

        float iconTop = rowStartCursor.Y + (rowHeight - iconHeight) / 2f;
        // Nudge text slightly up to sit visually centered with the icon row.
        float textNudge = ImGui.GetFontSize() * 0.22f;
        float textTop = iconTop - textNudge;
        float buttonTop = rowStartCursor.Y + (rowHeight - pauseClusterHeight) / 2f;

        ImGui.SetCursorPos(new Vector2(rowStartCursor.X + padding.X, iconTop));
        DrawLeftSide(iconTop, iconTop);

        float leftReserved = GetLeftSideReservedWidth();
        float nameStartX = rowStartCursor.X + padding.X + leftReserved;
        float rightSide;
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.2f, 0.25f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.35f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.3f, 1f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale))
        {
            rightSide = DrawRightSide(buttonTop, buttonTop);
        }

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