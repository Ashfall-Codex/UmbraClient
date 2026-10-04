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
using UmbraSync.UI.Components;
using UmbraSync.WebAPI.SignalR.Utils;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private const float SidebarWidth = 48f;
    private const float SidebarButtonSize = 34f;

    private void DrawSidebar()
    {
        bool isConnected = _apiController.ServerState is ServerState.Connected;
        bool hasNotifications = _notificationCount > 0;

        ImGuiHelpers.ScaledDummy(6f);
        DrawConnectionIcon();
        ImGuiHelpers.ScaledDummy(4f);
        DrawSidebarUid();
        ImGuiHelpers.ScaledDummy(8f);
        string notificationsTooltip = hasNotifications
            ? Loc.Get("CompactUi.Sidebar.Notifications")
            : Loc.Get("CompactUi.Sidebar.NotificationsEmpty");
        DrawSidebarButton(FontAwesomeIcon.Bell, notificationsTooltip, CompactUiSection.Notifications, hasNotifications, hasNotifications, _notificationCount, null, ImGuiColors.DalamudOrange);
        DrawSidebarButton(FontAwesomeIcon.GlobeEurope, "Social", CompactUiSection.Social, isConnected);
        int pendingInvites = _nearbyPending.Pending.Count;
        bool highlightAutoDetect = pendingInvites > 0;
        string autoDetectTooltip = highlightAutoDetect
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Sidebar.AutoDetectPending"), pendingInvites)
            : Loc.Get("CompactUi.Sidebar.AutoDetect");
        DrawSidebarButton(FontAwesomeIcon.BroadcastTower, autoDetectTooltip, CompactUiSection.AutoDetect, isConnected, highlightAutoDetect, pendingInvites);
        DrawSidebarButton(FontAwesomeIcon.PersonCircleQuestion, Loc.Get("CompactUi.Sidebar.CharacterAnalysis"), CompactUiSection.CharacterAnalysis, isConnected);
        DrawSidebarButton(FontAwesomeIcon.CircleNodes, Loc.Get("CompactUi.Sidebar.CharacterDataHub"), CompactUiSection.CharacterDataHub, isConnected);

        SidebarControls.DrawSeparator(SidebarButtonSize);

        DrawSidebarButton(FontAwesomeIcon.UserCircle, Loc.Get("CompactUi.Sidebar.EditProfile"), CompactUiSection.EditProfile, isConnected);
        DrawSidebarButton(FontAwesomeIcon.Cog, Loc.Get("CompactUi.Sidebar.Settings"), CompactUiSection.Settings);
    }

    private void DrawSidebarButton(FontAwesomeIcon icon, string tooltip, CompactUiSection section, bool enabled = true, bool highlight = false, int badgeCount = 0, Action? onClick = null, Vector4? highlightColor = null)
    {
        Vector4? iconColor = highlight && enabled ? highlightColor ?? new Vector4(0.45f, 0.85f, 0.45f, 1f) : null;

        if (SidebarControls.DrawButton(icon, "##sidebar_" + (int)section, _activeSection == section, tooltip,
                SidebarButtonSize, badgeCount, enabled, iconColor: iconColor))
        {
            if (onClick != null)
            {
                onClick.Invoke();
            }
            else
            {
                var previousSection = _activeSection;
                _activeSection = section;
                if (section == CompactUiSection.EditProfile && previousSection != CompactUiSection.EditProfile)
                {
                    _editProfileUi.RefreshFromServer();
                }
            }

            RegisterSidebarAccent(section);
        }
    }

    private void DrawConnectionIcon()
    {
        var state = _apiController.ServerState;
        var hasServer = _serverManager.HasServers;
        var currentServer = hasServer ? _serverManager.CurrentServer : null;
        bool isLinked = currentServer != null && !currentServer.FullPause;
        var icon = isLinked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;

        bool isTogglingDisabled = !hasServer || state is ServerState.Reconnecting or ServerState.Disconnecting;

        var connectionColor = state is ServerState.Connected
            ? new Vector4(0.25f, 0.85f, 0.45f, 1f)
            : new Vector4(0.90f, 0.25f, 0.25f, 1f);
        var tooltip = hasServer
            ? (isLinked
                ? string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.DisconnectTooltip"), currentServer!.ServerName)
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Connection.ConnectTooltip"), currentServer!.ServerName))
            : Loc.Get("CompactUi.Connection.NoServer");

        if (SidebarControls.DrawButton(icon, "##sidebar_connection", false, tooltip, SidebarButtonSize,
                enabled: !isTogglingDisabled, iconColor: connectionColor) && !isTogglingDisabled)
        {
            ToggleConnection();
        }
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

    private void ToggleConnection()
    {
        if (!_serverManager.HasServers) return;

        _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
        _serverManager.Save();
        _ = _apiController.CreateConnections();
    }
}
