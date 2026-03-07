using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using DalamudNotification = Dalamud.Interface.ImGuiNotification.Notification;
using DalamudNotificationType = Dalamud.Interface.ImGuiNotification.NotificationType;
using NotificationType = UmbraSync.MareConfiguration.Models.NotificationType;

namespace UmbraSync.Services;

public class NotificationService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly INotificationManager _notificationManager;
    private readonly IChatGui _chatGui;
    private readonly MareConfigService _configurationService;
    private readonly Services.Notification.NotificationTracker _notificationTracker;
    private readonly PlayerData.Pairs.PairManager _pairManager;
    private readonly IDalamudPluginInterface _pluginInterface;

    public NotificationService(ILogger<NotificationService> logger, MareMediator mediator,
        DalamudUtilService dalamudUtilService,
        INotificationManager notificationManager,
        IChatGui chatGui, MareConfigService configurationService,
        Services.Notification.NotificationTracker notificationTracker,
        PlayerData.Pairs.PairManager pairManager,
        IDalamudPluginInterface pluginInterface) : base(logger, mediator)
    {
        _dalamudUtilService = dalamudUtilService;
        _notificationManager = notificationManager;
        _chatGui = chatGui;
        _configurationService = configurationService;
        _notificationTracker = notificationTracker;
        _pairManager = pairManager;
        _pluginInterface = pluginInterface;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Mediator.Subscribe<NotificationMessage>(this, ShowNotification);
        Mediator.Subscribe<DualNotificationMessage>(this, ShowDualNotification);
        Mediator.Subscribe<Services.Mediator.SyncshellAutoDetectStateChanged>(this, OnSyncshellAutoDetectStateChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void PrintErrorChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[UmbraSync] Error: " + message);
        _chatGui.PrintError(se.BuiltString);
    }

    private void PrintInfoChat(string? message)
    {
        var se = new SeStringBuilder().AddText("[UmbraSync] Info: ");
        var chatTwoActive = PluginWatcherService.GetInitialPluginState(_pluginInterface, "ChatTwo")?.IsLoaded == true;
        if (chatTwoActive)
            se.AddText(message ?? string.Empty);
        else
            se.AddItalics(message ?? string.Empty);
        _chatGui.Print(se.BuiltString);
    }

    private void PrintWarnChat(string? message)
    {
        SeStringBuilder se = new SeStringBuilder().AddText("[UmbraSync] ").AddUiForeground("Warning: " + (message ?? string.Empty), 31).AddUiForegroundOff();
        _chatGui.Print(se.BuiltString);
    }

    private void ShowChat(NotificationMessage msg)
    {
        switch (msg.Type)
        {
            case NotificationType.Info:
            case NotificationType.Success:
                PrintInfoChat(msg.Message);
                break;

            case NotificationType.Warning:
                PrintWarnChat(msg.Message);
                break;

            case NotificationType.Error:
                PrintErrorChat(msg.Message);
                break;
        }
    }

    private void ShowNotification(NotificationMessage msg)
    {
        Logger.LogInformation("{msg}", msg.ToString());

        if (!_dalamudUtilService.IsLoggedIn) return;

        bool appendInstruction;
        bool forceChat = ShouldForceChat(msg, out appendInstruction);
        var effectiveMessage = forceChat && appendInstruction ? AppendAutoDetectInstruction(msg.Message) : msg.Message;
        var adjustedMsg = forceChat && appendInstruction ? msg with { Message = effectiveMessage } : msg;

        var suppressToast = IsAutoDetectPairRequest(adjustedMsg);

        switch (adjustedMsg.Type)
        {
            case NotificationType.Info:
            case NotificationType.Success:
                ShowNotificationLocationBased(adjustedMsg, _configurationService.Current.InfoNotification, forceChat, suppressToast);
                break;

            case NotificationType.Warning:
                ShowNotificationLocationBased(adjustedMsg, _configurationService.Current.WarningNotification, forceChat, suppressToast);
                break;

            case NotificationType.Error:
                ShowNotificationLocationBased(adjustedMsg, _configurationService.Current.ErrorNotification, forceChat, suppressToast);
                break;
        }
    }

    private void ShowDualNotification(DualNotificationMessage message)
    {
        if (!_dalamudUtilService.IsLoggedIn) return;

        var baseMsg = new NotificationMessage(message.Title, message.Message, message.Type, message.ToastDuration);
        ShowToast(baseMsg);
        ShowChat(baseMsg);
    }

    private void OnSyncshellAutoDetectStateChanged(SyncshellAutoDetectStateChanged msg)
    {
        try
        {
            var gid = msg.Gid;
            // Try to resolve alias from PairManager snapshot; fallback to gid
            var alias = _pairManager.Groups.Values.FirstOrDefault(g => string.Equals(g.GID, gid, StringComparison.OrdinalIgnoreCase))?.GroupAliasOrGID ?? gid;

            string title;
            string message;
            Services.Notification.NotificationEntry entry;

            if (msg.Visible)
            {
                title = $"Syncshell publique: {alias}";
                message = "La Syncshell est de nouveau visible via AutoDetect.";
                entry = Services.Notification.NotificationEntry.SyncshellPublic(gid, alias);
            }
            else
            {
                title = $"Syncshell non publique: {alias}";
                message = "La Syncshell n'est plus visible via AutoDetect.";
                entry = Services.Notification.NotificationEntry.SyncshellNotPublic(gid, alias);
            }

            // Show toast + chat
            ShowDualNotification(new DualNotificationMessage(title, message, NotificationType.Info, TimeSpan.FromSeconds(4)));

            // Persist into notification center
            _notificationTracker.Upsert(entry);
        }
        catch
        {
            // ignore failures
        }
    }

    private static bool ShouldForceChat(NotificationMessage msg, out bool appendInstruction)
    {
        appendInstruction = false;

        bool isAccept = ContainsNearbyAccept(msg.Title) || ContainsNearbyAccept(msg.Message);
        if (isAccept)
            return false;

        bool isRequest = ContainsNearbyRequest(msg.Title) || ContainsNearbyRequest(msg.Message);
        if (isRequest)
        {
            appendInstruction = !IsRequestSentConfirmation(msg);
            return true;
        }

        return false;
    }

    private static bool ContainsNearbyAccept(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("Nearby Accept", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequestSentConfirmation(NotificationMessage msg)
    {
        var sentTitle = Loc.Get("Notification.Nearby.Sent.Title");
        if (string.Equals(msg.Title, sentTitle, StringComparison.Ordinal))
            return true;

        var sentBody = Loc.Get("Notification.Nearby.Sent.Body");
        if (!string.IsNullOrEmpty(msg.Message) && msg.Message.Contains(sentBody, StringComparison.Ordinal))
            return true;

        return false;
    }

    private static string AppendAutoDetectInstruction(string? message)
    {
        return message ?? string.Empty;
    }

    private void ShowNotificationLocationBased(NotificationMessage msg, NotificationLocation location, bool forceChat, bool suppressToast)
    {
        bool showToast = !suppressToast && location is NotificationLocation.Toast or NotificationLocation.Both;
        bool showChat = forceChat || location is NotificationLocation.Chat or NotificationLocation.Both;

        if (showToast)
        {
            ShowToast(msg);
        }

        if (showChat)
        {
            ShowChat(msg);
        }
    }

    private void ShowToast(NotificationMessage msg)
    {
        DalamudNotificationType dalamudType = msg.Type switch
        {
            NotificationType.Error => DalamudNotificationType.Error,
            NotificationType.Warning => DalamudNotificationType.Warning,
            NotificationType.Success => DalamudNotificationType.Success,
            NotificationType.Info => DalamudNotificationType.Info,
            _ => DalamudNotificationType.Info,
        };

        _notificationManager.AddNotification(new DalamudNotification()
        {
            Content = msg.Message,
            Title = msg.Title,
            Type = dalamudType,
            Minimized = false,
            InitialDuration = msg.TimeShownOnScreen ?? TimeSpan.FromSeconds(3)
        });
    }

    private bool IsAutoDetectPairRequest(NotificationMessage msg)
    {
        if (msg.Type != NotificationType.Info) return false;

        // Ne supprimer le toast que si les popups interactives sont activées
        if (!_configurationService.Current.UseInteractivePairRequestPopup) return false;

        // Vérifier le titre localisé (notification créée par NearbyPendingService)
        var incomingTitle = Loc.Get("AutoDetect.Notification.IncomingTitle");
        if (string.Equals(msg.Title, incomingTitle, StringComparison.Ordinal)) return true;

        // Vérifier aussi le pattern "Nearby Request" dans le message (pour les messages du serveur)
        // mais exclure les confirmations d'envoi
        bool isNearbyRequest = ContainsNearbyRequest(msg.Title) || ContainsNearbyRequest(msg.Message);
        if (isNearbyRequest && !IsRequestSentConfirmation(msg)) return true;

        return false;
    }

    private static bool ContainsNearbyRequest(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var nearbyTitle = Loc.Get("Notification.NearbyDetection.Title");
        return text.Contains("Nearby request", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Nearby Request", StringComparison.Ordinal)
            || text.Contains(nearbyTitle, StringComparison.Ordinal);
    }
}