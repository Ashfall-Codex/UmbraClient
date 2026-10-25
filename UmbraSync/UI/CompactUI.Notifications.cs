using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.UI;

public partial class CompactUi
{
    private static readonly Vector4 MutedCardBackground = new(0.10f, 0.10f, 0.13f, 0.78f);
    private static readonly Vector4 MutedCardBorder = new(0.55f, 0.55f, 0.62f, 0.82f);
    private volatile bool _affiliationResponsePending;

    private void DrawNotificationsSection()
    {
        var notifications = _notificationTracker.GetEntries();
        if (notifications.Count == 0)
        {
            DrawEmptySectionCard(
                "notifications-empty",
                Loc.Get("CompactUi.Sidebar.Notifications"),
                Loc.Get("CompactUi.Notifications.Empty"),
                Loc.Get("CompactUi.Notifications.EmptyTooltip"));
            return;
        }

        if (ImGui.Button(Loc.Get("CompactUi.Notifications.ClearAll")))
        {
            _notificationTracker.Clear();
        }

        ImGuiHelpers.ScaledDummy(4f);

        foreach (var notification in notifications.OrderByDescending(n => n.CreatedAt))
        {
            switch (notification.Category)
            {
                case NotificationCategory.AutoDetect:
                    DrawAutoDetectNotification(notification);
                    break;
                case NotificationCategory.Syncshell:
                    DrawSyncshellNotification(notification);
                    break;
                case NotificationCategory.McdfShare:
                    DrawMcdfShareNotification(notification);
                    break;
                case NotificationCategory.Establishment when notification.Id.StartsWith(NotificationEntry.AffiliationRequestPrefix, StringComparison.Ordinal):
                    DrawAffiliationRequestNotification(notification);
                    break;
                default:
                    UiSharedService.DrawCard($"notification-{notification.Category}-{notification.Id}", () =>
                    {
                        ImGui.TextUnformatted(notification.Title);
                        if (!string.IsNullOrEmpty(notification.Description))
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                            ImGui.TextWrapped(notification.Description);
                            ImGui.PopStyleColor();
                        }

                        ImGuiHelpers.ScaledDummy(3f);
                        using (ImRaii.PushId($"notification-default-{notification.Id}"))
                        {
                            if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear") + "##" + notification.Id))
                            {
                                _notificationTracker.Remove(notification.Category, notification.Id);
                            }
                        }
                    }, stretchWidth: true);
                    break;
            }

            ImGuiHelpers.ScaledDummy(4f);
        }
    }

    private void DrawAutoDetectNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-autodetect-{notification.Id}", () =>
        {
            var displayName = ResolveRequesterDisplayName(notification.Id);
            var title = Loc.Get("AutoDetect.Notification.IncomingTitle");
            var body = string.Format(CultureInfo.CurrentCulture, Loc.Get("AutoDetect.Notification.IncomingBodyIdFirst"),
                BuildRequesterLabel(notification.Id, displayName));

            ImGui.TextUnformatted(title);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
            ImGui.TextWrapped(body);
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(3f);

            bool hasPending = _nearbyPending.Pending.ContainsKey(notification.Id);
            using (ImRaii.PushId(notification.Id))
            {
                using (ImRaii.Disabled(!hasPending))
                {
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Accept")))
                    {
                        TriggerAcceptAutoDetectNotification(notification.Id);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Decline")))
                    {
                        _nearbyPending.Decline(notification.Id);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("AutoDetect.Block")))
                    {
                        _nearbyPending.Block(notification.Id);
                    }
                    UiSharedService.AttachToolTip(Loc.Get("AutoDetect.Block.Tooltip"));
                }

                if (!hasPending)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                    {
                        _notificationTracker.Remove(NotificationCategory.AutoDetect, notification.Id);
                    }
                }
            }
        }, stretchWidth: true);
    }

    private void DrawAffiliationRequestNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-affiliation-{notification.Id}", () =>
        {
            ImGui.TextUnformatted(notification.Title);
            if (!string.IsNullOrEmpty(notification.Description))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                    ImGui.TextWrapped(notification.Description);
            }

            ImGuiHelpers.ScaledDummy(3f);
            var idText = notification.Id[NotificationEntry.AffiliationRequestPrefix.Length..];
            using (ImRaii.PushId(notification.Id))
            using (ImRaii.Disabled(_affiliationResponsePending || !Guid.TryParse(idText, out _)))
            {
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Accept")))
                    RespondToAffiliation(notification, Guid.Parse(idText), accept: true);
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Decline")))
                    RespondToAffiliation(notification, Guid.Parse(idText), accept: false);
                ImGui.SameLine();
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                    _notificationTracker.Remove(notification.Category, notification.Id);
            }
        }, stretchWidth: true);
    }

    private void RespondToAffiliation(NotificationEntry notification, Guid affiliationId, bool accept)
    {
        _affiliationResponsePending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _apiController.EstablishmentAffiliationRespond(affiliationId, accept).ConfigureAwait(false))
                {
                    _notificationTracker.Remove(notification.Category, notification.Id);
                    Mediator.Publish(new EstablishmentChangedMessage());
                }
                else
                {
                    Mediator.Publish(new NotificationMessage(Loc.Get("Establishment.Affiliation.RespondFailed.Title"),
                        Loc.Get("Establishment.Affiliation.RespondFailed.Body"), NotificationType.Warning));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to respond to affiliation {id}", affiliationId);
            }
            finally
            {
                _affiliationResponsePending = false;
            }
        });
    }

    private static void DrawEmptySectionCard(string id, string header, string description, string tooltip)
    {
        ImGuiHelpers.ScaledDummy(4f);
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.9f))
        {
            UiSharedService.DrawCard(id, () =>
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(header);

                ImGuiHelpers.ScaledDummy(3f);
                UiSharedService.ColorTextWrapped(description, ImGuiColors.DalamudGrey3);
            }, background: MutedCardBackground, border: MutedCardBorder, stretchWidth: true);
        }

        UiSharedService.AttachToolTip(tooltip);
    }

    private void DrawSyncshellNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-syncshell-{notification.Id}", () =>
        {
            ImGui.TextUnformatted(notification.Title);
            if (!string.IsNullOrEmpty(notification.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                ImGui.TextWrapped(notification.Description);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(3f);

            using (ImRaii.PushId($"syncshell-{notification.Id}"))
            {
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                {
                    _notificationTracker.Remove(NotificationCategory.Syncshell, notification.Id);
                }
            }
        }, stretchWidth: true);
    }

    private void DrawMcdfShareNotification(NotificationEntry notification)
    {
        UiSharedService.DrawCard($"notification-mcdf-{notification.Id}", () =>
        {
            ImGui.TextUnformatted(notification.Title);
            if (!string.IsNullOrEmpty(notification.Description))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3);
                ImGui.TextUnformatted(notification.Description);
                ImGui.PopStyleColor();
            }

            ImGuiHelpers.ScaledDummy(3f);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey2);
            ImGui.TextUnformatted(notification.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            ImGui.PopStyleColor();

            ImGuiHelpers.ScaledDummy(3f);
            using (ImRaii.PushId($"mcdf-{notification.Id}"))
            {
                if (ImGui.Button(Loc.Get("CompactUi.Notifications.Clear")))
                {
                    _notificationTracker.Remove(NotificationCategory.McdfShare, notification.Id);
                }
            }
        }, stretchWidth: true);
    }

    private void TriggerAcceptAutoDetectNotification(string uid)
    {
        _ = Task.Run(async () =>
        {
            bool accepted = await _nearbyPending.AcceptAsync(uid).ConfigureAwait(false);
            if (!accepted)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("CompactUi.Notifications.AutoDetectTitle"), string.Format(CultureInfo.CurrentCulture, Loc.Get("CompactUi.Notifications.AcceptFailed"), uid), NotificationType.Warning, TimeSpan.FromSeconds(5)));
                _notificationTracker.Upsert(NotificationEntry.AcceptPairRequestFailed(uid));
            }
        });
    }

    private static string BuildRequesterLabel(string uid, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return uid;
        if (string.Equals(displayName, uid, StringComparison.OrdinalIgnoreCase)) return uid;
        return displayName;
    }

    private string ResolveRequesterDisplayName(string uid)
    {
        var nearby = _nearbyEntries.FirstOrDefault(e => e.IsMatch
            && string.Equals(e.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (nearby != null && !string.IsNullOrWhiteSpace(nearby.Name))
            return nearby.Name;

        if (_nearbyPending.Pending.TryGetValue(uid, out var pendingEntry)
            && !string.IsNullOrWhiteSpace(pendingEntry.DisplayName)
            && !string.Equals(pendingEntry.DisplayName, _apiController.DisplayName, StringComparison.OrdinalIgnoreCase))
            return pendingEntry.DisplayName;

        return string.Empty;
    }
}
