using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.UI;

public sealed class PairRequestToastUi : WindowMediatorSubscriberBase
{
    private const int MaxVisibleToasts = 3;
    private const float ToastWidth = 360f;
    private const float ToastPadding = 12f;
    private const float ToastMargin = 16f;
    private const float ToastSpacing = 8f;
    private const float TitleMessageSpacing = 6f;
    private const float ButtonSpacing = 6f;
    private const float ToastRounding = 10f;
    private const float AccentWidth = 3f;
    private const float MinToastHeight = 88f;

    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ApiController _apiController;
    private readonly NearbyPendingService _nearbyPending;
    private readonly NearbyDiscoveryService _nearbyDiscoveryService;
    private readonly NotificationTracker _notificationTracker;
    private readonly MareConfigService _configService;

    public PairRequestToastUi(ILogger<PairRequestToastUi> logger, MareMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        DalamudUtilService dalamudUtilService, ApiController apiController, NearbyPendingService nearbyPending,
        NearbyDiscoveryService nearbyDiscoveryService, NotificationTracker notificationTracker, MareConfigService configService)
        : base(logger, mediator, "UmbraSync Pair Requests Toasts", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _apiController = apiController;
        _nearbyPending = nearbyPending;
        _nearbyDiscoveryService = nearbyDiscoveryService;
        _notificationTracker = notificationTracker;
        _configService = configService;

        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoSavedSettings;
        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;
        Flags |= ImGuiWindowFlags.NoBackground;

        DisableWindowSounds = true;
        ForceMainWindow = true;
        IsOpen = false;
        Mediator.Subscribe<NotificationStateChanged>(this, OnNotificationStateChanged);
    }

    private void OnNotificationStateChanged(NotificationStateChanged msg)
    {
        if (!_dalamudUtilService.IsLoggedIn) return;
        if (!_configService.Current.UseInteractivePairRequestPopup) return;
        if (_nearbyPending.Pending.Count == 0) return;

        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        if (!_dalamudUtilService.IsLoggedIn)
        {
            IsOpen = false;
            return;
        }

        if (!_configService.Current.UseInteractivePairRequestPopup)
        {
            IsOpen = false;
            return;
        }

        var pendingEntries = GetPendingEntries();
        if (pendingEntries.Count == 0)
        {
            IsOpen = false;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var toastWidth = ToastWidth * scale;
        var margin = ToastMargin * scale;
        var spacing = ToastSpacing * scale;
        var paddingX = ToastPadding * scale;
        var paddingY = ToastPadding * scale;
        var titleMessageGap = TitleMessageSpacing * scale;
        var buttonGap = ButtonSpacing * scale;
        var rounding = ToastRounding * scale;
        var accentWidth = AccentWidth * scale;
        var minHeight = MinToastHeight * scale;

        var incomingTitle = Loc.Get("AutoDetect.Notification.IncomingTitle");
        var incomingBody = Loc.Get("AutoDetect.Notification.IncomingBodyIdFirst");

        var layouts = pendingEntries
            .Take(MaxVisibleToasts)
            .Select(entry =>
            {
                var displayName = ResolveDisplayName(entry.Id);
                var label = BuildRequesterLabel(entry.Id, displayName);
                var title = incomingTitle;
                var message = string.Format(CultureInfo.CurrentCulture, incomingBody, label);
                var contentWidth = toastWidth - (paddingX * 2f);
                var titleHeight = ImGui.CalcTextSize(title, false, contentWidth).Y;
                var messageHeight = string.IsNullOrWhiteSpace(message)
                    ? 0f
                    : ImGui.CalcTextSize(message, false, contentWidth).Y;
                var buttonHeight = ImGui.GetFrameHeight();
                var height = paddingY + titleHeight
                    + (messageHeight > 0f ? titleMessageGap + messageHeight : 0f)
                    + buttonGap + buttonHeight + paddingY;
                if (height < minHeight) height = minHeight;
                return (entry.Id, Title: title, Message: message, Height: height);
            })
            .ToList();

        if (layouts.Count == 0) return;

        var totalHeight = layouts.Sum(l => l.Height) + spacing * (layouts.Count - 1);
        var viewport = ImGui.GetMainViewport();
        var windowPos = viewport.Pos + viewport.Size - new Vector2(toastWidth + margin, totalHeight + margin);
        Size = new Vector2(toastWidth, totalHeight);
        Position = windowPos;
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f);

        var yOffset = 0f;
        foreach (var layout in layouts)
        {
            var y = totalHeight - yOffset - layout.Height;
            ImGui.SetCursorPos(new Vector2(0f, y));

            using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, rounding))
            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.08f, 0.92f)))
            using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.2f, 0.2f, 0.2f, 0.85f)))
            using (var child = ImRaii.Child($"pair-request-toast-{layout.Id}", new Vector2(toastWidth, layout.Height), true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (child)
                {
                    DrawToastAccent(accentWidth, rounding);
                    DrawToastContent(layout.Id, layout.Title, layout.Message, toastWidth - (paddingX * 2f), paddingX, paddingY, titleMessageGap, buttonGap);
                }
            }

            yOffset += layout.Height + spacing;
        }
    }

    private static void DrawToastAccent(float accentWidth, float rounding)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var accentColor = ImGui.GetColorU32(ImGuiColors.DalamudOrange);
        drawList.AddRectFilled(pos, new Vector2(pos.X + accentWidth, pos.Y + size.Y), accentColor, rounding, ImDrawFlags.RoundCornersLeft);
    }

    private void DrawToastContent(string uid, string title, string message, float contentWidth,
        float paddingX, float paddingY, float titleMessageGap, float buttonGap)
    {
        ImGui.PushID(uid);
        ImGui.SetCursorPos(new Vector2(paddingX, paddingY));
        DrawCenteredText(title, contentWidth, paddingX);

        if (!string.IsNullOrWhiteSpace(message))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + titleMessageGap);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey2))
            {
                DrawCenteredText(message, contentWidth, paddingX);
            }
        }

        var buttonHeight = ImGui.GetFrameHeight();
        ImGui.SetCursorPosY(ImGui.GetWindowSize().Y - paddingY - buttonHeight);
        ImGui.SetCursorPosX(paddingX);

        var buttonWidth = (contentWidth - buttonGap) / 2f;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.HealerGreen))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGuiColors.ParsedGreen))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ImGuiColors.ParsedGreen))
        {
            if (ImGui.Button(Loc.Get("CompactUi.Notifications.Accept"), new Vector2(buttonWidth, 0)))
            {
                AcceptRequest(uid);
            }
        }

        ImGui.SameLine(0f, buttonGap);
        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DalamudRed))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed))
        {
            var declineBlockWidth = (buttonWidth - buttonGap) * 0.7f;
            var blockWidth = (buttonWidth - buttonGap) * 0.3f;
            if (ImGui.Button(Loc.Get("CompactUi.Notifications.Decline"), new Vector2(declineBlockWidth, 0)))
            {
                _nearbyPending.Decline(uid);
            }
            ImGui.SameLine(0f, buttonGap);
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.4f, 0.1f, 0.1f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.5f, 0.15f, 0.15f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.2f, 0.2f, 1f)))
            {
                if (ImGui.Button(Loc.Get("AutoDetect.Block"), new Vector2(blockWidth, 0)))
                {
                    _nearbyPending.Block(uid);
                }
            }
            UiSharedService.AttachToolTip(Loc.Get("AutoDetect.Block.Tooltip"));
        }
        ImGui.PopID();
    }

    private void AcceptRequest(string uid)
    {
        _ = Task.Run(async () =>
        {
            var accepted = await _nearbyPending.AcceptAsync(uid).ConfigureAwait(false);
            if (!accepted)
            {
                Mediator.Publish(new NotificationMessage(
                    Loc.Get("CompactUi.Notifications.AutoDetectTitle"),
                    string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Notifications.AcceptFailed"), uid),
                    NotificationType.Warning,
                    TimeSpan.FromSeconds(5)));
            }
        });
    }

    private string ResolveDisplayName(string uid)
    {
        try
        {
            var nearby = _nearbyDiscoveryService.SnapshotEntries()
                .FirstOrDefault(e => e.IsMatch && string.Equals(e.Uid, uid, StringComparison.OrdinalIgnoreCase));
            if (nearby != null && !string.IsNullOrWhiteSpace(nearby.Name))
                return nearby.Name;
        }
        catch
        {
            // ignore lookup errors and fall back to pending data
        }

        if (_nearbyPending.Pending.TryGetValue(uid, out var pendingEntry)
            && !string.IsNullOrWhiteSpace(pendingEntry.DisplayName)
            && !string.Equals(pendingEntry.DisplayName, _apiController.DisplayName, StringComparison.OrdinalIgnoreCase))
            return pendingEntry.DisplayName;

        return string.Empty;
    }

    private static string BuildRequesterLabel(string uid, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return uid;
        if (string.Equals(displayName, uid, StringComparison.OrdinalIgnoreCase)) return uid;
        return displayName;
    }

    private static void DrawCenteredText(string text, float contentWidth, float paddingX)
    {
        if (string.IsNullOrEmpty(text)) return;

        var textSize = ImGui.CalcTextSize(text, false, contentWidth);
        if (textSize.X <= contentWidth)
        {
            ImGui.SetCursorPosX(paddingX + (contentWidth - textSize.X) / 2f);
            ImGui.TextUnformatted(text);
            return;
        }

        ImGui.SetCursorPosX(paddingX);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
    }

    private IReadOnlyList<NotificationEntry> GetPendingEntries()
    {
        if (_nearbyPending.Pending.Count == 0) return Array.Empty<NotificationEntry>();

        var entries = _notificationTracker.GetEntries()
            .Where(e => e.Category == NotificationCategory.AutoDetect && _nearbyPending.Pending.ContainsKey(e.Id))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        if (entries.Count > 0) return entries;

        return _nearbyPending.Pending
            .Select(kv => NotificationEntry.AutoDetect(kv.Key, kv.Value.DisplayName))
            .ToList();
    }
}