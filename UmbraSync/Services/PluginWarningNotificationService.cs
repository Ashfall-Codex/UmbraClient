using System.Collections.Concurrent;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Comparer;
using UmbraSync.Interop.Ipc;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using PlayerChanges = UmbraSync.PlayerData.Data.PlayerChanges;
using UmbraSync.Localization;

namespace UmbraSync.Services;

public class PluginWarningNotificationService
{
    private readonly ConcurrentDictionary<UserData, OptionalPluginWarning> _cachedOptionalPluginWarnings = new(UserDataComparer.Instance);
    private readonly IpcManager _ipcManager;
    private readonly MareConfigService _mareConfigService;
    private readonly MareMediator _mediator;
    private readonly NotificationTracker _notificationTracker;

    public PluginWarningNotificationService(MareConfigService mareConfigService, IpcManager ipcManager, MareMediator mediator,
        NotificationTracker notificationTracker)
    {
        _mareConfigService = mareConfigService;
        _ipcManager = ipcManager;
        _mediator = mediator;
        _notificationTracker = notificationTracker;
    }

    public void NotifyForMissingPlugins(UserData user, string playerName, HashSet<PlayerChanges> changes)
    {
        if (!_cachedOptionalPluginWarnings.TryGetValue(user, out var warning))
        {
            _cachedOptionalPluginWarnings[user] = warning = new()
            {
                ShownCustomizePlusWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownHeelsWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownHonorificWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShowPetNicknamesWarning = _mareConfigService.Current.DisableOptionalPluginWarnings,
                ShownMoodlesWarning = _mareConfigService.Current.DisableOptionalPluginWarnings
            };
        }

        List<string> missingPluginsForData = [];
        if (changes.Contains(PlayerChanges.Heels) && !warning.ShownHeelsWarning && !_ipcManager.Heels.APIAvailable)
        {
            missingPluginsForData.Add("SimpleHeels");
            warning.ShownHeelsWarning = true;
        }
        if (changes.Contains(PlayerChanges.Customize) && !warning.ShownCustomizePlusWarning && !_ipcManager.CustomizePlus.APIAvailable)
        {
            missingPluginsForData.Add("Customize+");
            warning.ShownCustomizePlusWarning = true;
        }

        if (changes.Contains(PlayerChanges.Honorific) && !warning.ShownHonorificWarning && !_ipcManager.Honorific.APIAvailable)
        {
            missingPluginsForData.Add("Honorific");
            warning.ShownHonorificWarning = true;
        }

        if (changes.Contains(PlayerChanges.PetNames) && !warning.ShowPetNicknamesWarning && !_ipcManager.PetNames.APIAvailable)
        {
            missingPluginsForData.Add("PetNicknames");
            warning.ShowPetNicknamesWarning = true;
        }

        if (changes.Contains(PlayerChanges.Moodles) && !warning.ShownMoodlesWarning && !_ipcManager.Moodles.APIAvailable)
        {
            missingPluginsForData.Add("Moodles");
            warning.ShownMoodlesWarning = true;
        }

        if (missingPluginsForData.Any())
        {
            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.PluginIntegration.MissingPlugins.Title", playerName),
                Loc.Get("Notification.PluginIntegration.MissingPlugins.Body", string.Join(", ", missingPluginsForData)),
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
            _notificationTracker.Upsert(NotificationEntry.MissingPlugins(playerName, missingPluginsForData));
        }
    }
}