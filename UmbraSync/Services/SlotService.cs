using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.Slot;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Services;

public class SlotService : MediatorSubscriberBase, IDisposable
{
    private readonly ApiController _apiController;
    private readonly MareConfigService _configService;
    private readonly TransientConfigService _transientConfigService;
    private readonly PairManager _pairManager;
    private readonly ChatService _chatService;
    private readonly NotificationTracker _notificationTracker;
    private SlotSyncshellDto? _currentSlotSyncshell;
    private bool _joinedViaSlot = false;
    private CancellationTokenSource? _leaveTimerCts;
    private readonly HashSet<Guid> _declinedSlots = [];

    public SlotService(ILogger<SlotService> logger, MareMediator mediator, ApiController apiController,
        SyncshellDiscoveryService syncshellDiscoveryService, MareConfigService configService, TransientConfigService transientConfigService,
        PairManager pairManager, ChatService chatService, NotificationTracker notificationTracker)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;
        _transientConfigService = transientConfigService;
        _pairManager = pairManager;
        _chatService = chatService;
        _notificationTracker = notificationTracker;

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            var uid = msg.Connection.User.UID;
            if (_transientConfigService.Current.LastJoinedSlotSyncshellPerUid.TryGetValue(uid, out var lastJoined))
            {
                _currentSlotSyncshell = lastJoined;
                _joinedViaSlot = true;
                Logger.LogInformation("Loaded last joined slot syncshell {name} for UID {uid}", _currentSlotSyncshell.Name, uid);
            }
            else
            {
                _currentSlotSyncshell = null;
                _joinedViaSlot = false;
            }
        });

        Mediator.Subscribe<HousingPlotEnteredMessage>(this, (msg) => _ = OnHousingPlotEntered(msg.LocationInfo));
        Mediator.Subscribe<HousingPlotLeftMessage>(this, (msg) => OnHousingPlotLeft());
        Mediator.Subscribe<HousingPositionUpdateMessage>(this, (msg) => OnHousingPositionUpdate(msg.ServerId, msg.TerritoryId, msg.DivisionId, msg.WardId, msg.Position));
        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            _ = ClearState();
        });
        Mediator.Subscribe<GroupLeftMessage>(this, (msg) =>
        { 
            if (_currentSlotSyncshell != null && string.Equals(msg.Gid, _currentSlotSyncshell.Gid, StringComparison.Ordinal))
            {
                Logger.LogInformation("Leaving slot syncshell {name} because user left the group", _currentSlotSyncshell.Name);
                _currentSlotSyncshell = null;
                _joinedViaSlot = false;
                var uid = _apiController.UID;
                if (!string.IsNullOrEmpty(uid))
                {
                    _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.Remove(uid);
                    _transientConfigService.Save();
                }
            }
            else
            {
                var uid = _apiController.UID;
                if (!string.IsNullOrEmpty(uid) &&
                    _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.TryGetValue(uid, out var savedSlot) &&
                    string.Equals(msg.Gid, savedSlot.Gid, StringComparison.Ordinal))
                {
                    Logger.LogInformation("Clearing saved slot syncshell {name} because user left the group", savedSlot.Name);
                    _currentSlotSyncshell = null;
                    _joinedViaSlot = false;
                    _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.Remove(uid);
                    _transientConfigService.Save();
                }
            }
        });
    }

    private SlotInfoResponseDto? _detectedSlotByDistance;
    private SlotInfoResponseDto? _lastNotifiedSlot;
    private Vector3 _lastQueryPosition = Vector3.Zero;
    private LocationInfo? _currentPlot;

    private async void OnHousingPositionUpdate(uint serverId, uint territoryId, uint divisionId, uint wardId, Vector3 position)
    {
        if (!_configService.Current.EnableSlotNotifications)
        {
            Logger.LogTrace("Slot: notifications désactivées");
            return;
        }
        // Utiliser wardId > 0 pour détecter toute zone résidentielle (extérieur, intérieur, lobby d'appartements)
        // au lieu d'une liste hardcodée de territoryId qui peut devenir obsolète
        if (wardId == 0)
        {
            Logger.LogTrace("Slot: wardId=0, pas en zone résidentielle (territory={territory})", territoryId);
            return;
        }
        if (Vector3.Distance(position, _lastQueryPosition) < 2.0f) return;
        if (!_apiController.IsConnected)
        {
            Logger.LogTrace("Slot: non connecté au serveur");
            return;
        }

        _lastQueryPosition = position;
        Logger.LogDebug("Slot: requête SlotGetNearby - Server={server} Territory={territory} Division={division} Ward={ward} Pos=({x},{y},{z})",
            serverId, territoryId, divisionId, wardId, position.X, position.Y, position.Z);
        try
        {
            var slotInfo = await _apiController.SlotGetNearby(serverId, territoryId, divisionId, wardId, position.X, position.Y, position.Z).ConfigureAwait(false);

            // Vérification côté client: s'assurer que le slot correspond à notre ward/division actuel
            if (slotInfo?.Location != null && wardId > 0 &&
                (slotInfo.Location.WardId != wardId || slotInfo.Location.DivisionId != divisionId))
            {
                Logger.LogDebug("SlotGetNearby returned slot for Ward {slotWard}/Div {slotDiv}, but we are in Ward {currentWard}/Div {currentDiv}. Ignoring.",
                    slotInfo.Location.WardId, slotInfo.Location.DivisionId, wardId, divisionId);
                slotInfo = null;
            }
            if (slotInfo != null)
            {
                Logger.LogDebug("SlotGetNearby result: {name} (ID: {id})", slotInfo.SlotName, slotInfo.SlotId);
            }

            // Si on détecte un slot à proximité
            if (slotInfo != null)
            {
                // Si un timer de sortie était en cours, on l'annule puisqu'on est de nouveau à proximité
                if (_leaveTimerCts != null)
                {
                    Logger.LogInformation("Player back in range of a Slot, canceling leave timer");
                    await _leaveTimerCts.CancelAsync().ConfigureAwait(false);
                    _leaveTimerCts.Dispose();
                    _leaveTimerCts = null;
                }

                // Si on a déjà rejoint via Slot, on ne fait rien de plus
                if (_joinedViaSlot) return;

                // Si on n'avait pas encore détecté ce slot par distance
                if (_detectedSlotByDistance == null)
                {
                    if (_declinedSlots.Contains(slotInfo.SlotId))
                    {
                        Logger.LogDebug("Slot {id} is in declined list, ignoring", slotInfo.SlotId);
                        return;
                    }

                    _detectedSlotByDistance = slotInfo;
                    if (slotInfo.AssociatedSyncshell != null)
                    {
                        _currentSlotSyncshell = slotInfo.AssociatedSyncshell;
                        var isMember = _pairManager.Groups.Any(g => string.Equals(g.Key.GID, slotInfo.AssociatedSyncshell.Gid, StringComparison.Ordinal));
                        Logger.LogDebug("Slot {name} member status: {isMember}", slotInfo.SlotName, isMember);
                        if (isMember)
                        {
                            if (_lastNotifiedSlot?.SlotId != slotInfo.SlotId)
                            {
                                _lastNotifiedSlot = slotInfo;
                                Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), string.Format(Loc.Get("Slot.Toast.Welcome"), slotInfo.SlotName), MareConfiguration.Models.NotificationType.Info));
                            }
                        }
                        else
                        {
                            Logger.LogDebug("Publishing OpenSlotPromptMessage for slot {name} (not a member)", slotInfo.SlotName);
                            _lastNotifiedSlot = slotInfo;
                            Mediator.Publish(new OpenSlotPromptMessage(slotInfo));
                        }
                    }
                }
            }
            else if (slotInfo == null)
            {
                // Si on était dans un slot via le système de Slot, on lance le timer de 5 minutes si on s'éloigne
                if (_joinedViaSlot && _currentSlotSyncshell != null && _leaveTimerCts == null && _currentPlot == null)
                {
                    StartLeaveTimer();
                }

                if (_detectedSlotByDistance != null && _lastNotifiedSlot?.SlotId == _detectedSlotByDistance.SlotId)
                {
                    var isMember = _pairManager.Groups.Any(g => _detectedSlotByDistance.AssociatedSyncshell != null && string.Equals(g.Key.GID, _detectedSlotByDistance.AssociatedSyncshell.Gid, StringComparison.Ordinal));
                    if (isMember && !_joinedViaSlot)
                    {
                        Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), string.Format(Loc.Get("Slot.Toast.Leaving"), _detectedSlotByDistance.SlotName), MareConfiguration.Models.NotificationType.Info));
                    }
                    _lastNotifiedSlot = null;
                }
                _detectedSlotByDistance = null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error in OnHousingPositionUpdate");
        }
    }

    private async Task ClearState()
    {
        if (_leaveTimerCts != null)
        {
            await _leaveTimerCts.CancelAsync().ConfigureAwait(false);
            _leaveTimerCts.Dispose();
            _leaveTimerCts = null;
        }
        _declinedSlots.Clear();
        _detectedSlotByDistance = null;
        _lastNotifiedSlot = null;
        _lastQueryPosition = Vector3.Zero;
        _currentPlot = null;
        _currentSlotSyncshell = null;
        _joinedViaSlot = false;
    }

    public void MarkJoinedViaSlot(SlotSyncshellDto syncshell)
    {
        _currentSlotSyncshell = syncshell;
        _joinedViaSlot = true;
        var uid = _apiController.UID;
        if (!string.IsNullOrEmpty(uid))
        {
            _transientConfigService.Current.LastJoinedSlotSyncshellPerUid[uid] = syncshell;
            _transientConfigService.Save();
        }
        Logger.LogInformation("Syncshell {name} marked as joined via Slot", syncshell.Name);
    }

    private async Task OnHousingPlotEntered(LocationInfo location)
    {
        if (!_configService.Current.EnableSlotNotifications) return;
        // Utiliser WardId > 0 pour couvrir toutes les zones résidentielles
        if (location.WardId == 0) return;

        Logger.LogInformation("Entered housing plot: {location}", location);
        _currentPlot = location;
        if (_leaveTimerCts != null)
        {
            await _leaveTimerCts.CancelAsync().ConfigureAwait(false);
            _leaveTimerCts.Dispose();
            _leaveTimerCts = null;
        }

        var slotLocation = new SlotLocationDto
        {
            ServerId = location.ServerId,
            TerritoryId = location.TerritoryId,
            DivisionId = location.DivisionId,
            WardId = location.WardId,
            PlotId = location.HouseId
        };

        var slotInfo = await _apiController.SlotGetInfo(slotLocation).ConfigureAwait(false);
        if (slotInfo != null)
        {
            Logger.LogDebug("SlotGetInfo (plot) result: {name} (ID: {id})", slotInfo.SlotName, slotInfo.SlotId);
        }
        else
        {
            Logger.LogTrace("SlotGetInfo (plot): No slot found for plot {id}", slotLocation.PlotId);
        }

        if (slotInfo != null)
        {
            if (_declinedSlots.Contains(slotInfo.SlotId))
            {
                Logger.LogDebug("Slot {id} is in declined list, ignoring", slotInfo.SlotId);
                return;
            }

            Logger.LogInformation("Slot detected: {name}", slotInfo.SlotName);
            if (slotInfo.AssociatedSyncshell != null)
            {
                _currentSlotSyncshell = slotInfo.AssociatedSyncshell;
                var isMember = _pairManager.Groups.Any(g => string.Equals(g.Key.GID, slotInfo.AssociatedSyncshell.Gid, StringComparison.Ordinal));
                Logger.LogDebug("Slot {name} member status: {isMember}", slotInfo.SlotName, isMember);
                if (isMember)
                {
                    if (_lastNotifiedSlot?.SlotId != slotInfo.SlotId)
                    {
                        _lastNotifiedSlot = slotInfo;
                        Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), string.Format(Loc.Get("Slot.Toast.Welcome"), slotInfo.SlotName), MareConfiguration.Models.NotificationType.Info));
                    }
                }
                else
                {
                    Logger.LogInformation("Publishing OpenSlotPromptMessage for slot {name} (not a member)", slotInfo.SlotName);
                    _lastNotifiedSlot = slotInfo;
                    Mediator.Publish(new OpenSlotPromptMessage(slotInfo));
                }
            }
        }
    }

    private void StartLeaveTimer()
    {
        if (_currentSlotSyncshell != null && _joinedViaSlot)
        {
            if (_leaveTimerCts != null) return;
            var gid = _currentSlotSyncshell.Gid;
            var group = _pairManager.Groups.Values.FirstOrDefault(g => string.Equals(g.Group.GID, gid, StringComparison.Ordinal));
            if (group == null)
            {
                Logger.LogInformation("User is no longer a member of syncshell {name}, clearing slot state", _currentSlotSyncshell.Name);
                _currentSlotSyncshell = null;
                _joinedViaSlot = false;
                var uid = _apiController.UID;
                if (!string.IsNullOrEmpty(uid))
                {
                    _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.Remove(uid);
                    _transientConfigService.Save();
                }
                return;
            }
            
            // Si l'utilisateur est le propriétaire de la Syncshell, on ne l'éjecte pas
            if (string.Equals(group.Owner.UID, _apiController.UID, StringComparison.Ordinal))
            {
                Logger.LogInformation("User is owner of syncshell {name}, skipping leave timer", _currentSlotSyncshell.Name);
                return;
            }

            Logger.LogInformation("Starting 5 minute leave timer for syncshell {name}", _currentSlotSyncshell.Name);
            var title = Loc.Get("SlotPopup.Title");
            var message = string.Format(Loc.Get("Slot.Toast.AutoLeaveStarting"), _currentSlotSyncshell.Name);
            Logger.LogDebug("Publishing leave notification: {title} - {message}", title, message);

            Mediator.Publish(new DualNotificationMessage(title, message, MareConfiguration.Models.NotificationType.Warning));
            _notificationTracker.Upsert(NotificationEntry.SlotConflict(_currentSlotSyncshell.Name));

            _leaveTimerCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), _leaveTimerCts.Token).ConfigureAwait(false);
                    await LeaveSlotSyncshell().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInformation("Leave timer canceled");
                }
            }, _leaveTimerCts.Token);
        }
    }

    private void OnHousingPlotLeft()
    {
        Logger.LogInformation("Left housing plot");
        _currentPlot = null;

        if (_lastNotifiedSlot != null)
        {
            var isMember = _pairManager.Groups.Any(g => _lastNotifiedSlot.AssociatedSyncshell != null && string.Equals(g.Key.GID, _lastNotifiedSlot.AssociatedSyncshell.Gid, StringComparison.Ordinal));
            if (isMember && !_joinedViaSlot)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), string.Format(Loc.Get("Slot.Toast.Leaving"), _lastNotifiedSlot.SlotName), MareConfiguration.Models.NotificationType.Info));
            }
            _lastNotifiedSlot = null;
        }

        StartLeaveTimer();
    }

    private async Task LeaveSlotSyncshell()
    {
        if (_currentSlotSyncshell != null)
        {
            var syncshellName = _currentSlotSyncshell.Name;
            Logger.LogInformation("Leaving slot syncshell {name} due to inactivity", syncshellName);
            await _apiController.GroupLeave(new GroupDto(new GroupData(_currentSlotSyncshell.Gid))).ConfigureAwait(false);
            _currentSlotSyncshell = null;
            _joinedViaSlot = false;
            var uid = _apiController.UID;
            if (!string.IsNullOrEmpty(uid))
            {
                _transientConfigService.Current.LastJoinedSlotSyncshellPerUid.Remove(uid);
                _transientConfigService.Save();
            }

            // Notification toast et centre de notifications
            var msg = string.Format(Loc.Get("Slot.Toast.AutoLeft"), syncshellName);
            Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), msg, MareConfiguration.Models.NotificationType.Info));
            _chatService.Print(msg);
        }
    }

    public void DeclineSlot(SlotInfoResponseDto slotInfo)
    {
        _declinedSlots.Add(slotInfo.SlotId);
        var msg = string.Format(Loc.Get("Slot.Decline.Notification"), slotInfo.SlotName);
        Mediator.Publish(new NotificationMessage(Loc.Get("SlotPopup.Title"), msg, MareConfiguration.Models.NotificationType.Info));
        _chatService.Print(msg);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_leaveTimerCts != null)
            {
                _ = _leaveTimerCts.CancelAsync().ConfigureAwait(false);
                _leaveTimerCts.Dispose();
            }
            UnsubscribeAll();
        }
    }
}