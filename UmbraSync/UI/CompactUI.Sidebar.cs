using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private const float SidebarWidth = 53f;
    private const float SidebarIconSize = 25f;
    private static readonly Vector4 SidebarButtonColor = new(0f, 0f, 0f, 0f);
    private static readonly Vector4 SidebarButtonHoverColor = new(0x30 / 255f, 0x19 / 255f, 0x46 / 255f, 1f);
    private static readonly Vector4 SidebarButtonActiveColor = new(0x50 / 255f, 0x17 / 255f, 0x83 / 255f, 1f);
    private const float SidebarIndicatorAnimSpeed = 18f;
    private readonly Dictionary<CompactUiSection, (Vector2 Min, Vector2 Max)> _sidebarButtonRects = new();
    private Vector2 _sidebarIndicatorPos;
    private Vector2 _sidebarIndicatorSize;
    private bool _sidebarIndicatorInitialized;
    private Vector2 _sidebarWindowPos;

    private void DrawSidebar()
    {
        bool isConnected = _apiController.ServerState is ServerState.Connected;
        bool hasNotifications = _notificationCount > 0;

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        _sidebarButtonRects.Clear();

        ImGuiHelpers.ScaledDummy(6f);
        DrawConnectionIcon();
        ImGuiHelpers.ScaledDummy(4f);
        DrawSidebarUid();
        ImGuiHelpers.ScaledDummy(8f);
        string notificationsTooltip = hasNotifications
            ? Loc.Get("CompactUi.Sidebar.Notifications")
            : Loc.Get("CompactUi.Sidebar.NotificationsEmpty");
        DrawSidebarButton(FontAwesomeIcon.Bell, notificationsTooltip, CompactUiSection.Notifications, hasNotifications, hasNotifications, _notificationCount, null, ImGuiColors.DalamudOrange);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.GlobeEurope, "Social", CompactUiSection.Social, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        ImGuiHelpers.ScaledDummy(3f);
        int pendingInvites = _nearbyPending.Pending.Count;
        bool highlightAutoDetect = pendingInvites > 0;
        string autoDetectTooltip = highlightAutoDetect
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Sidebar.AutoDetectPending"), pendingInvites)
            : Loc.Get("CompactUi.Sidebar.AutoDetect");
        DrawSidebarButton(FontAwesomeIcon.BroadcastTower, autoDetectTooltip, CompactUiSection.AutoDetect, isConnected, highlightAutoDetect, pendingInvites);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("CompactUi.Sidebar.CharacterAnalysis"), CompactUiSection.CharacterAnalysis, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.Running, Loc.Get("CompactUi.Sidebar.CharacterDataHub"), CompactUiSection.CharacterDataHub, isConnected);
        ImGuiHelpers.ScaledDummy(12f);
        DrawSidebarButton(FontAwesomeIcon.UserCircle, Loc.Get("CompactUi.Sidebar.EditProfile"), CompactUiSection.EditProfile, isConnected);
        ImGuiHelpers.ScaledDummy(3f);
        DrawSidebarButton(FontAwesomeIcon.Cog, Loc.Get("CompactUi.Sidebar.Settings"), CompactUiSection.Settings);

        drawList.ChannelsSetCurrent(0);
        DrawSidebarIndicator(drawList);
        drawList.ChannelsMerge();
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, string tooltip, CompactUiSection section, bool enabled = true, bool highlight = false, int badgeCount = 0, Action? onClick = null, Vector4? highlightColor = null)
    {
        using var id = ImRaii.PushId((int)section);
        float regionWidth = ImGui.GetContentRegionAvail().X;
        float buttonWidth = SidebarIconSize * ImGuiHelpers.GlobalScale;
        float offset = System.Math.Max(0f, (regionWidth - buttonWidth) / 2f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        bool isActive = _activeSection == section;

        if (DrawSidebarSquareButton(icon, isActive, highlight, enabled, badgeCount, highlightColor))
        {
            if (onClick != null)
            {
                onClick.Invoke();
            }
            else
            {
                _activeSection = section;
            }
        }

        _sidebarButtonRects[section] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawConnectionIcon()
    {
        var state = _apiController.ServerState;
        var hasServer = _serverManager.HasServers;
        var currentServer = hasServer ? _serverManager.CurrentServer : null;
        bool isLinked = currentServer != null && !currentServer.FullPause;
        var icon = isLinked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;

        using var id = ImRaii.PushId("connection-icon");
        float childWidth = SidebarWidth * ImGuiHelpers.GlobalScale;
        float buttonWidth = SidebarIconSize * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX((childWidth - buttonWidth) / 2f);

        bool isTogglingDisabled = !hasServer || state is ServerState.Reconnecting or ServerState.Disconnecting;

        var connectionColor = state is ServerState.Connected
            ? new Vector4(0.25f, 0.85f, 0.45f, 1f)
            : new Vector4(0.90f, 0.25f, 0.25f, 1f);
        if (DrawSidebarSquareButton(icon, false, false, !isTogglingDisabled, 0, null, connectionColor) && !isTogglingDisabled)
        {
            ToggleConnection();
        }

        var tooltip = hasServer
            ? (isLinked
                ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.DisconnectTooltip"), currentServer!.ServerName)
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.ConnectTooltip"), currentServer!.ServerName))
            : Loc.Get("CompactUi.Connection.NoServer");
        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawSidebarUid()
    {
        var uidText = GetUidText();
        var uidColor = GetUidColor();
        bool isConnected = _apiController.ServerState is ServerState.Connected;

        float regionWidth = ImGui.GetContentRegionAvail().X;
        float padding = 4f * ImGuiHelpers.GlobalScale;
        float maxTextWidth = regionWidth - padding * 2f;

        var textSize = ImGui.CalcTextSize(uidText);
        float fontScale = 1f;
        if (textSize.X > maxTextWidth && maxTextWidth > 0)
        {
            fontScale = maxTextWidth / textSize.X;
            fontScale = MathF.Max(fontScale, 0.55f); // minimum readability
        }

        using var scalePush = UiSharedService.PushFontScale(fontScale);
        textSize = ImGui.CalcTextSize(uidText);

        float textX = ImGui.GetCursorPosX() + (regionWidth - textSize.X) / 2f;
        ImGui.SetCursorPosX(textX);

        if (isConnected)
        {
            var screenPos = ImGui.GetCursorScreenPos();
            float btnHeight = textSize.Y;
            ImGui.InvisibleButton("##sidebarUid", new Vector2(textSize.X, btnHeight));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();

            var font = ImGui.GetFont();
            float fontSize = ImGui.GetFontSize();
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(font, fontSize, screenPos, ImGui.GetColorU32(uidColor), uidText);

            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked)
                ImGui.SetClipboardText(_apiController.DisplayName);

            UiSharedService.AttachToolTip(Loc.Get("CompactUi.Uid.CopyTooltip"));
        }
        else
        {
            ImGui.TextColored(uidColor, uidText);
        }
    }

    private bool DrawSidebarSquareButton(FontAwesomeIcon icon, bool isActive, bool highlight, bool enabled, int badgeCount, Vector4? highlightColor, Vector4? iconColorOverride = null)
    {
        float size = SidebarIconSize * ImGuiHelpers.GlobalScale;

        bool useIndicator = isActive;
        bool useAccent = highlight && enabled;
        var buttonColor = useAccent ? UiSharedService.AccentColor : SidebarButtonColor;
        var hoverColor = useAccent ? UiSharedService.AccentHoverColor : SidebarButtonHoverColor;
        var activeColor = useAccent ? UiSharedService.AccentActiveColor : SidebarButtonActiveColor;
        if (useIndicator)
        {
            buttonColor = new Vector4(0f, 0f, 0f, 0f);
            hoverColor = buttonColor;
            activeColor = buttonColor;
        }

        string iconText = icon.ToIconString();
        Vector2 iconSize;
        using (_uiSharedService.IconFont.Push())
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var start = ImGui.GetCursorScreenPos();
        bool clicked;

        using var disabled = ImRaii.Disabled(!enabled);
        using var buttonColorPush = ImRaii.PushColor(ImGuiCol.Button, buttonColor);
        using var hoverColorPush = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColor);
        using var activeColorPush = ImRaii.PushColor(ImGuiCol.ButtonActive, activeColor);

        clicked = ImGui.Button("##sidebar-icon", new Vector2(size, size));

        using (_uiSharedService.IconFont.Push())
        {
            var textPos = new Vector2(
                MathF.Round(start.X + (size - iconSize.X) / 2f),
                MathF.Round(start.Y + (size - iconSize.Y) / 2f));
            uint iconColor = !enabled
                ? ImGui.GetColorU32(ImGuiCol.TextDisabled)
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.9f, 1f));
            if (enabled)
            {
                if (iconColorOverride.HasValue)
                {
                    iconColor = ImGui.ColorConvertFloat4ToU32(iconColorOverride.Value);
                }
                if (highlight)
                {
                    var color = highlightColor ?? new Vector4(0.45f, 0.85f, 0.45f, 1f);
                    iconColor = ImGui.ColorConvertFloat4ToU32(color);
                }
                else if (isActive)
                {
                    iconColor = ImGui.GetColorU32(ImGuiCol.Text);
                }
            }
            ImGui.GetWindowDrawList().AddText(textPos, iconColor, iconText);
        }

        if (badgeCount > 0)
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            float radius = 6f * ImGuiHelpers.GlobalScale;
            var center = new Vector2(max.X - radius * 0.8f, min.Y + radius * 0.8f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(UiSharedService.AccentColor));
            string badgeText = badgeCount > 9 ? "9+" : badgeCount.ToString(CultureInfo.CurrentCulture);
            var textSize = ImGui.CalcTextSize(badgeText);
            drawList.AddText(center - textSize / 2f, ImGui.GetColorU32(ImGuiCol.Text), badgeText);
        }

        return clicked && enabled;
    }

    private void DrawSidebarIndicator(ImDrawListPtr drawList)
    {
        if (!_sidebarButtonRects.TryGetValue(_activeSection, out var rect))
        {
            return;
        }

        var windowPos = ImGui.GetWindowPos();
        var targetPos = rect.Min;
        var targetSize = rect.Max - rect.Min;

        if (!_sidebarIndicatorInitialized || _sidebarWindowPos != windowPos)
        {
            _sidebarIndicatorPos = targetPos;
            _sidebarIndicatorSize = targetSize;
            _sidebarIndicatorInitialized = true;
            _sidebarWindowPos = windowPos;
        }
        else
        {
            float dt = ImGui.GetIO().DeltaTime;
            float lerpT = 1f - MathF.Exp(-SidebarIndicatorAnimSpeed * dt);
            _sidebarIndicatorPos = Vector2.Lerp(_sidebarIndicatorPos, targetPos, lerpT);
            _sidebarIndicatorSize = Vector2.Lerp(_sidebarIndicatorSize, targetSize, lerpT);
        }

        float padding = 2f * ImGuiHelpers.GlobalScale;
        var min = _sidebarIndicatorPos - new Vector2(padding);
        var max = _sidebarIndicatorPos + _sidebarIndicatorSize + new Vector2(padding);
        float rounding = 6f * ImGuiHelpers.GlobalScale;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(UiSharedService.AccentColor), rounding);
    }


    private void ToggleConnection()
    {
        if (!_serverManager.HasServers) return;

        _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
        _serverManager.Save();
        _ = _apiController.CreateConnections();
    }
}
