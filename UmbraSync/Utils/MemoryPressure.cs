using Dalamud.Plugin.Services;
using UmbraSync.Localization;
using DalamudNotification = Dalamud.Interface.ImGuiNotification.Notification;
using DalamudNotificationType = Dalamud.Interface.ImGuiNotification.NotificationType;

namespace UmbraSync.Utils;

public static class MemoryPressure
{
    public static bool IsOutOfMemory(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is OutOfMemoryException or InsufficientMemoryException) return true;

            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (IsOutOfMemory(inner)) return true;
                }
                return false;
            }

            ex = ex.InnerException;
        }

        return false;
    }

    public static void NotifyStartupFailure(INotificationManager notificationManager)
    {
        try
        {
            notificationManager.AddNotification(new DalamudNotification()
            {
                Title = Loc.Get("Startup.OutOfMemory.Title"),
                Content = Loc.Get("Startup.OutOfMemory.Message"),
                Type = DalamudNotificationType.Error,
                Minimized = false,
                InitialDuration = TimeSpan.FromSeconds(30)
            });
        }
        catch
        {
            // best-effort : l'échec d'une notification ne doit jamais masquer l'erreur d'origine
        }
    }
}
