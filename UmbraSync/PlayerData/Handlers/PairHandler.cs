using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Events;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.Utils;
using UmbraSync.WebAPI.Files;
using ObjectKind = UmbraSync.API.Data.Enum.ObjectKind;
using PlayerChanges = UmbraSync.PlayerData.Data.PlayerChanges;

namespace UmbraSync.PlayerData.Handlers;

public sealed class PairHandler : DisposableMediatorSubscriberBase, IPairHandlerAdapter
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileDownloadManager _downloadManager;
    private readonly FileCacheManager _fileDbManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly VisibilityService _visibilityService;
    private readonly ApplicationSemaphoreService _applicationSemaphoreService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private CharacterData? _cachedData = null;
    private CharacterData? _lastAppliedData = null;
    private bool _pendingModReapply;
    private GameObjectHandler? _charaHandler;
    private readonly Dictionary<ObjectKind, Guid?> _customizeIds = [];
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private Task? _downloadTask;
    private bool _forceApplyMods = false;
    private bool _isVisible;
    private Guid _deferred = Guid.Empty;
    private Guid _penumbraCollection = Guid.Empty;
    private bool _redrawOnNextApplication = false;
    private readonly Lock _pauseLock = new();
    private Task _pauseTransitionTask = Task.CompletedTask;
    private bool _pauseRequested = false;
    private readonly Lock _visibilityGraceGate = new();
    private CancellationTokenSource? _visibilityGraceCts;
    private static readonly TimeSpan VisibilityEvictionGrace = TimeSpan.FromMinutes(5);
    private DateTime? _invisibleSinceUtc;
    private DateTime? _visibilityEvictionDueAtUtc;
    private DateTime? _lastDataReceivedAt;
    private DateTime? _lastApplyAttemptAt;
    private DateTime? _lastSuccessfulApplyAt;
    private string? _lastFailureReason;
    private IReadOnlyList<string> _lastBlockingConditions = Array.Empty<string>();
    public bool ScheduledForDeletion { get; set; }
    public DateTime? InvisibleSinceUtc => _invisibleSinceUtc;
    public DateTime? VisibilityEvictionDueAtUtc => _visibilityEvictionDueAtUtc;
    public DateTime? LastDataReceivedAt => _lastDataReceivedAt;
    public DateTime? LastApplyAttemptAt => _lastApplyAttemptAt;
    public DateTime? LastSuccessfulApplyAt => _lastSuccessfulApplyAt;
    public string? LastFailureReason => _lastFailureReason;
    public IReadOnlyList<string> LastBlockingConditions => _lastBlockingConditions;
    public string Ident => Pair.Ident;
    public bool Initialized => _charaHandler != null;
    public bool IsApplyingOrDownloading =>
        (_applicationTask != null && !_applicationTask.IsCompleted) ||
        (_downloadTask != null && !_downloadTask.IsCompleted);
    public CharacterData? LastReceivedCharacterData => _cachedData;

    public PairHandler(ILogger<PairHandler> logger, Pair pair, PairAnalyzer pairAnalyzer,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, MareMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        MareConfigService configService, VisibilityService visibilityService,
        ApplicationSemaphoreService applicationSemaphoreService, ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        Pair = pair;
        PairAnalyzer = pairAnalyzer;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _fileDbManager = fileDbManager;
        _playerPerformanceService = playerPerformanceService;
        _configService = configService;
        _visibilityService = visibilityService;
        _applicationSemaphoreService = applicationSemaphoreService;
        _serverConfigurationManager = serverConfigurationManager;

        _visibilityService.StartTracking(Pair.Ident);

        Mediator.SubscribeKeyed<PlayerVisibilityMessage>(this, Pair.Ident, (msg) => UpdateVisibility(msg.IsVisible, msg.Invalidate));

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            IsVisible = false;
        });
        Mediator.Subscribe<CutsceneStartMessage>(this, _ => DisableSync());
        Mediator.Subscribe<CutsceneEndMessage>(this, _ =>
        {
            if (_deferred != Guid.Empty && _cachedData != null)
            {
                ApplyCharacterData(_deferred, _cachedData, forceApplyCustomization: true);
            }
            EnableSync();
        });
        Mediator.Subscribe<GposeStartMessage>(this, _ => DisableSync());
        Mediator.Subscribe<GposeEndMessage>(this, _ =>
        {
            if (_deferred != Guid.Empty && _cachedData != null)
            {
                ApplyCharacterData(_deferred, _cachedData, forceApplyCustomization: true);
            }
            EnableSync();
        });
        Mediator.Subscribe<InstanceOrDutyStartMessage>(this, _ => DisableSync());
        Mediator.Subscribe<InstanceOrDutyEndMessage>(this, _ => EnableSync());
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            _penumbraCollection = Guid.Empty;
            if (_deferred != Guid.Empty && _cachedData != null)
            {
                ApplyCharacterData(_deferred, _cachedData, forceApplyCustomization: true);
            }

            if (!IsVisible && _charaHandler != null)
            {
                PlayerName = string.Empty;
                _charaHandler.Dispose();
                _charaHandler = null;
            }
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _redrawOnNextApplication = true;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceEndMessage>(this, _ => EnableSync());
        Mediator.Subscribe<CombatOrPerformanceStartMessage>(this, _ =>
        {
            if (_configService.Current.HoldCombatApplication)
            {
                _dataReceivedInDowntime = null;
                DisableSync();
            }
        });
        Mediator.Subscribe<RecalculatePerformanceMessage>(this, (msg) =>
        {
            if (msg.UID != null && !msg.UID.Equals(Pair.UserData.UID, StringComparison.Ordinal)) return;
            Logger.LogDebug("Recalculating performance for {uid}", Pair.UserData.UID);
            pair.ApplyLastReceivedData(forced: true);
        });
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => TryReapplyPendingData());

        LastAppliedDataBytes = -1;
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, text)));

                if (_isVisible)
                {
                    CancelVisibilityGraceTask();
                }
                else
                {
                    StartVisibilityGraceTask();
                }
            }
        }
    }

    public long LastAppliedDataBytes { get; private set; }
    public Pair Pair { get; private init; }
    public PairAnalyzer PairAnalyzer { get; private init; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;
    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;
    public string? PlayerName { get; private set; }
    public string PlayerNameHash => Pair.Ident;
    public bool IsInitialized => _charaHandler != null;
    
    // Enregistre un échec d'application avec sa raison et les conditions bloquantes.
    private void RecordFailure(string reason, params string[] conditions)
    {
        _lastFailureReason = reason;
        _lastBlockingConditions = conditions.Length == 0 ? Array.Empty<string>() : conditions.ToArray();
    }
    // Efface l'état d'échec précédent.
    private void ClearFailureState()
    {
        _lastFailureReason = null;
        _lastBlockingConditions = Array.Empty<string>();
    }
    
    // Appeles des données reçues pour ce handler.
    public void OnDataReceived()
    {
        _lastDataReceivedAt = DateTime.UtcNow;
    }

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        _lastApplyAttemptAt = DateTime.UtcNow;
        ClearFailureState();

        if (_configService.Current.HoldCombatApplication && _dalamudUtil.IsInCombatOrPerforming)
        {
            RecordFailure("En combat ou en train de jouer de la musique", "Combat", "Performing");
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player is in combat or performing", applicationBase);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            RecordFailure("Joueur dans un état invalide", "CharaHandlerNull", "PlayerPointerNull");
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}",
                applicationBase, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            _isVisible = false;
            _deferred = applicationBase;
            return;
        }

        _deferred = Guid.Empty;

        SetUploading(isUploading: false);

        if (Pair.IsDownloadBlocked)
        {
            var reasons = string.Join(", ", Pair.HoldDownloadReasons);
            RecordFailure($"Téléchargement bloqué: {reasons}", Pair.HoldDownloadReasons.ToArray());
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                $"Not applying character data: {reasons}")));
            Logger.LogDebug("[BASE-{appBase}] Not applying due to hold: {reasons}", applicationBase, reasons);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            return;
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _forceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase, characterData.DataHash.Value, _cachedData?.DataHash.Value ?? "NODATA");

        var hasMissingFiles = false;
        if (string.Equals(characterData.DataHash.Value, _cachedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
            && !forceApplyCustomization
            && !_forceApplyMods
            && !_pendingModReapply)
        {
            hasMissingFiles = HasMissingFiles(characterData);
            if (!hasMissingFiles)
                return;

            Logger.LogDebug("[BASE-{appbase}] Same hash {hash} but missing files detected, forcing reapply", applicationBase, characterData.DataHash.Value);
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            var conditions = new List<string>();
            if (_dalamudUtil.IsInCutscene) conditions.Add("Cutscene");
            if (_dalamudUtil.IsInGpose) conditions.Add("GPose");
            if (!_ipcManager.Penumbra.APIAvailable) conditions.Add("PenumbraUnavailable");
            if (!_ipcManager.Glamourer.APIAvailable) conditions.Add("GlamourerUnavailable");
            RecordFailure("GPose, Cutscene ou Penumbra/Glamourer indisponible", conditions.ToArray());

            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available. Deferring application.")));
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation("[BASE-{appbase}] Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, deferring", applicationBase, this);
            _forceApplyMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            _deferred = applicationBase;
            _isVisible = false;
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        _forceApplyMods |= forceApplyCustomization || hasMissingFiles;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _cachedData?.DeepClone() ?? new(), Logger, this, forceApplyCustomization, _forceApplyMods);

        if (_charaHandler != null && _forceApplyMods)
        {
            _forceApplyMods = false;
        }

        if (_redrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _redrawOnNextApplication = false;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate);
    }

    public override string ToString()
    {
        return Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    public void SetUploading(bool isUploading)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("Setting {pairHandler} uploading {uploading}", this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

 
    public void Invalidate()
    {
        Logger.LogDebug("Invalidating handler for {uid}", Pair.UserData.UID);
        _charaHandler?.Invalidate();
        _forceApplyMods = true;
        _pendingModReapply = true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _visibilityService.StopTracking(Pair.Ident);

        SetUploading(isUploading: false);
        var name = PlayerName;
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Disposing {name} ({user})", name, Pair.UserData.AliasOrUID);
        try
        {
            Guid applicationId = Guid.NewGuid();

            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, "Disposing User")));
            }

            UndoApplicationAsync(applicationId).GetAwaiter().GetResult();

            PlayerName = null;
            _applicationCancellationTokenSource?.Dispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            lock (_visibilityGraceGate)
            {
                _visibilityGraceCts?.Cancel();
                _visibilityGraceCts?.Dispose();
                _visibilityGraceCts = null;
            }
            _charaHandler?.Dispose();
            _charaHandler = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            _cachedData = null;
            _lastAppliedData = null;
            _pendingModReapply = false;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));
            Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    public void UndoApplication(Guid applicationId = default)
    {
        _ = Task.Run(async () =>
        {
            await UndoApplicationAsync(applicationId).ConfigureAwait(false);
        });
    }

    private async Task UndoApplicationAsync(Guid applicationId = default)
    {
        var name = PlayerName;
        Logger.LogDebug("Undoing application of {pair} (Name: {name})", Pair.UserData.UID, name);
        _lastAppliedData = null;
        _pendingModReapply = false;
        try
        {
            if (applicationId == Guid.Empty)
                applicationId = Guid.NewGuid();
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
            _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();

            Logger.LogDebug("[{applicationId}] Removing Temp Collection for {name} ({user})", applicationId, name, Pair.UserData.UID);
            if (_penumbraCollection != Guid.Empty)
            {
                var col = _penumbraCollection;
                try
                {
                    await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, col).ConfigureAwait(false);
                    _penumbraCollection = Guid.Empty;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to remove temporary collection {col}, likely already removed", col);
                }
            }

            if (!string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("[{applicationId}] Restoring state for {name} ({OnlineUser})", applicationId, name, Pair.UserData.UID);
                if (!IsVisible)
                {
                    Logger.LogDebug("[{applicationId}] Restoring Glamourer for {name} ({user})", applicationId, name, Pair.UserData.UID);
                    await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    Logger.LogInformation("[{applicationId}] CachedData is null {isNull}, contains things: {contains}", applicationId, _cachedData == null, (_cachedData?.FileReplacements.Values.Count ?? 0) > 0);

                    if (_cachedData != null && _cachedData.FileReplacements.Values.Count > 0)
                    {
                        foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in _cachedData.FileReplacements)
                        {
                            try
                            {
                                await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException ex)
                            {
                                Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                                break;
                            }
                        }
                    }
                    else
                    {
                        Logger.LogDebug("[{applicationId}] Restoring Glamourer (fallback) for {name} ({user})", applicationId, name, Pair.UserData.UID);
                        await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                Logger.LogTrace("[{applicationId}] Not restoring state, PlayerName is null or empty", applicationId);
            }

            _cachedData = null;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));
            Logger.LogDebug("Undo Application [{applicationId}] complete", applicationId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on undoing application of {name}", name);
        }
    }
    
    private async Task RevertToRestoredAsync(Guid applicationId)
    {
        var name = PlayerName;
        Logger.LogDebug("[{applicationId}] Reverting to restored state for {name} ({user})", applicationId, name, Pair.UserData.UID);

        if (_charaHandler is null || _charaHandler.Address == nint.Zero)
        {
            Logger.LogDebug("[{applicationId}] Character handler is null or invalid, skipping revert", applicationId);
            return;
        }

        try
        {
            var gameObject = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler.GetGameObject()).ConfigureAwait(false);
            if (gameObject is not Dalamud.Game.ClientState.Objects.Types.ICharacter character)
            {
                Logger.LogDebug("[{applicationId}] Game object is not a character, skipping revert", applicationId);
                return;
            }
            if (_ipcManager.Penumbra.APIAvailable && _penumbraCollection != Guid.Empty)
            {
                Logger.LogDebug("[{applicationId}] Clearing Penumbra mods for {name}", applicationId, name);
                try
                {
                    await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, character.ObjectIndex).ConfigureAwait(false);
                    await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, applicationId, _penumbraCollection, new Dictionary<string, string>(StringComparer.Ordinal)).ConfigureAwait(false);
                    await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, applicationId, _penumbraCollection, string.Empty).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[{applicationId}] Failed to clear Penumbra mods for {name}", applicationId, name);
                }
            }
            var kinds = new HashSet<ObjectKind>(_customizeIds.Keys);
            if (_cachedData is not null)
            {
                foreach (var kind in _cachedData.FileReplacements.Keys)
                {
                    kinds.Add(kind);
                }
            }
            kinds.Add(ObjectKind.Player);
            var characterName = character.Name.TextValue;
            if (string.IsNullOrEmpty(characterName))
            {
                characterName = character.Name.ToString();
            }
            if (string.IsNullOrEmpty(characterName))
            {
                Logger.LogWarning("[{applicationId}] Failed to determine character name for {handler}, using fallback", applicationId, name);
                characterName = name ?? Pair.UserData.UID;
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            Logger.LogDebug("[{applicationId}] Reverting {count} ObjectKinds for {name}", applicationId, kinds.Count, characterName);
            foreach (var kind in kinds)
            {
                try
                {
                    await RevertCustomizationDataAsync(kind, characterName, applicationId, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("[{applicationId}] Revert operation timed out for {kind} on {name}", applicationId, kind, characterName);
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[{applicationId}] Failed to revert {kind} for {name}", applicationId, kind, characterName);
                }
            }

            _cachedData = null;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));

            Logger.LogInformation("[{applicationId}] Revert to restored state complete for {name}", applicationId, characterName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{applicationId}] Failed to revert handler {name} during pause", applicationId, name);
        }
    }
    
    private void DisableSync()
    {
        Logger.LogDebug("Disabling sync for {name} ({user})", PlayerName, Pair.UserData.UID);
        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
        _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
    }
    
    private void EnableSync()
    {
        Logger.LogDebug("Enabling sync for {name} ({user})", PlayerName, Pair.UserData.UID);
        if (_dataReceivedInDowntime is not null && IsVisible)
        {
            var pending = _dataReceivedInDowntime;
            _dataReceivedInDowntime = null;

            Logger.LogDebug("Applying queued data for {name} ({user})", PlayerName, Pair.UserData.UID);
            _ = Task.Run(() =>
            {
                try
                {
                    Task.Delay(100).Wait(); // Small delay to ensure state is ready
                    ApplyCharacterData(pending.ApplicationId, pending.CharacterData, pending.Forced);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed applying queued data for {name} ({user})", PlayerName, Pair.UserData.UID);
                }
            });
        }
    }

    private void StartVisibilityGraceTask()
    {
        CancellationToken token;
        lock (_visibilityGraceGate)
        {
            _visibilityGraceCts = _visibilityGraceCts?.CancelRecreate() ?? new CancellationTokenSource();
            token = _visibilityGraceCts.Token;
            _invisibleSinceUtc = DateTime.UtcNow;
            _visibilityEvictionDueAtUtc = _invisibleSinceUtc.Value + VisibilityEvictionGrace;
        }

        Logger.LogDebug("Starting visibility grace period for {name} ({user}), eviction due at {time}",
            PlayerName, Pair.UserData.UID, _visibilityEvictionDueAtUtc);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(VisibilityEvictionGrace, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (IsVisible) return;

                Logger.LogInformation("Visibility grace period expired for {name} ({user}), scheduling for deletion",
                    PlayerName, Pair.UserData.UID);
                ScheduledForDeletion = true;

                // Clean up Penumbra collection when the grace period expires
                if (_penumbraCollection != Guid.Empty)
                {
                    var applicationId = Guid.NewGuid();
                    try
                    {
                        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _penumbraCollection).ConfigureAwait(false);
                        _penumbraCollection = Guid.Empty;
                        Logger.LogDebug("[{applicationId}] Removed temporary collection after visibility grace timeout", applicationId);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "[{applicationId}] Failed to remove temporary collection after visibility grace timeout", applicationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Grace period was cancelled (player became visible again)
            }
        }, CancellationToken.None);
    }

    private void CancelVisibilityGraceTask()
    {
        lock (_visibilityGraceGate)
        {
            if (_visibilityGraceCts != null)
            {
                Logger.LogDebug("Cancelling visibility grace period for {name} ({user})", PlayerName, Pair.UserData.UID);
                _visibilityGraceCts.Cancel();
                _visibilityGraceCts.Dispose();
                _visibilityGraceCts = null;
            }

            _invisibleSinceUtc = null;
            _visibilityEvictionDueAtUtc = null;
            ScheduledForDeletion = false;
        }
    }

    private async Task PauseInternalAsync()
    {
        try
        {
            Logger.LogInformation("Pausing handler for {name} ({user})", PlayerName, Pair.UserData.UID);
            DisableSync();
            if (_charaHandler is not null && _charaHandler.Address != nint.Zero)
            {
                var applicationId = Guid.NewGuid();
                await RevertToRestoredAsync(applicationId).ConfigureAwait(false);
            }
            Mediator.Publish(new PlayerVisibilityMessage(Pair.Ident, IsVisible: false, Invalidate: true));

            Logger.LogInformation("Pause complete for {name} ({user})", PlayerName, Pair.UserData.UID);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to pause handler for {name} ({user})", PlayerName, Pair.UserData.UID);
        }
    }
    
    private async Task ResumeInternalAsync()
    {
        try
        {
            Logger.LogInformation("Resuming handler for {name} ({user})", PlayerName, Pair.UserData.UID);

            if (_charaHandler is null || _charaHandler.Address == nint.Zero)
            {
                Logger.LogDebug("Character handler is null or invalid, skipping resume");
                return;
            }

            if (!IsVisible)
            {
                Mediator.Publish(new PlayerVisibilityMessage(Pair.Ident, IsVisible: true, Invalidate: false));
            }

            EnableSync();

            // Toujours appeler ApplyLastReceivedData - les données sont dans Pair.LastReceivedCharacterData ou le cache
            Logger.LogDebug("Applying last received data for {name} ({user})", PlayerName, Pair.UserData.UID);
            Pair.ApplyLastReceivedData(forced: true);

            Logger.LogInformation("Resume complete for {name} ({user})", PlayerName, Pair.UserData.UID);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resume handler for {name} ({user})", PlayerName, Pair.UserData.UID);
        }
    }
    
    public void SetPaused(bool paused)
    {
        lock (_pauseLock)
        {
            if (_pauseRequested == paused)
            {
                Logger.LogTrace("Pause state already {state} for {name} ({user}), skipping", paused ? "paused" : "unpaused", PlayerName, Pair.UserData.UID);
                return;
            }

            _pauseRequested = paused;
            Logger.LogDebug("Queueing pause transition to {state} for {name} ({user})", paused ? "paused" : "unpaused", PlayerName, Pair.UserData.UID);

            _pauseTransitionTask = _pauseTransitionTask
                .ContinueWith(_ => paused ? PauseInternalAsync() : ResumeInternalAsync(), TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
    {
        if (PlayerCharacter == nint.Zero) return;
        var ptr = PlayerCharacter;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _charaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetCompanion(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetMinionOrMount(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetPet(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };
        var handlerToDispose = handler == _charaHandler ? null : handler;

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_configService.Current.SerialApplication)
            {
                var orderedChanges = changes.Value.OrderBy(p => (int)p).ToList();
                var serialChangeList = orderedChanges.Where(p => p <= PlayerChanges.ForcedRedraw).ToList();
                var asyncChangeList = orderedChanges.Where(p => p > PlayerChanges.ForcedRedraw).ToList();
                await _dalamudUtil.RunOnFrameworkThread(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, serialChangeList, charaData, token).ConfigureAwait(false)).ConfigureAwait(false);
                await Task.Run(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, asyncChangeList, charaData, token).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var orderedChanges = changes.Value.OrderBy(p => (int)p).ToList();
                await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, orderedChanges, charaData, token).ConfigureAwait(false);
            }
        }
        finally
        {
            handlerToDispose?.Dispose();
        }
    }

    private async Task ProcessCustomizationChangesAsync(GameObjectHandler handler, Guid applicationId, ObjectKind objectKind,
        IEnumerable<PlayerChanges> changeList, CharacterData charaData, CancellationToken token)
    {
        foreach (var change in changeList)
        {
            Logger.LogDebug("[{applicationId}{ft}] Processing {change} for {handler}", applicationId, _dalamudUtil.IsOnFrameworkThread ? "*" : string.Empty, change, handler);
            switch (change)
            {
                case PlayerChanges.Customize:
                    if (charaData.CustomizePlusData.TryGetValue(objectKind, out var customizePlusData))
                    {
                        _customizeIds[objectKind] = await _ipcManager.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                    }
                    else if (_customizeIds.TryGetValue(objectKind, out var customizeId))
                    {
                        await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                        _customizeIds.Remove(objectKind);
                    }
                    break;

                case PlayerChanges.Heels:
                    await _ipcManager.Heels.SetOffsetForPlayerAsync(handler.Address, charaData.HeelsData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Honorific:
                    await _ipcManager.Honorific.SetTitleAsync(handler.Address, charaData.HonorificData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Glamourer:
                    if (charaData.GlamourerData.TryGetValue(objectKind, out var glamourerData))
                    {
                        await _ipcManager.Glamourer.ApplyAllAsync(Logger, handler, glamourerData, applicationId, token, allowImmediate: true).ConfigureAwait(false);
                    }
                    break;

                case PlayerChanges.PetNames:
                    await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Moodles:
                    await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.ForcedRedraw:
                    await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, token).ConfigureAwait(false);
                    break;

            }

            token.ThrowIfCancellationRequested();
        }
    }

    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        if (updatedData.Count == 0)
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            return;
        }

        if (string.Equals(charaData.DataHash.Value, _lastAppliedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
            && !updatedData.Values.Any(v => v.Contains(PlayerChanges.ForcedRedraw))
            && !_pendingModReapply)
        {
            Logger.LogDebug("[BASE-{appBase}] Already applied hash {hash} and no pending reapply, ignoring", applicationBase, charaData.DataHash.Value);
            return;
        }

        _pendingModReapply = false;

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));
        var hasOtherChanges = updatedData.Values.Any(v => v.Any(p => p != PlayerChanges.ModFiles && p != PlayerChanges.ModManip && p != PlayerChanges.ForcedRedraw));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _downloadTask = Task.Run(async () =>
        {
#pragma warning disable MA0004 // ConfigureAwait on await using requires different syntax
            await using var semaphoreLease = await _applicationSemaphoreService
                .AcquireAsync(downloadToken, highPriority: IsVisible)
                .ConfigureAwait(false);
#pragma warning restore MA0004
            if ((updateModdedPaths || updateManip) && !hasOtherChanges && !_forceApplyMods)
            {
                Logger.LogDebug("[BASE-{appBase}] Applying mod changes only - skipping full redraw", applicationBase);
                await ApplyModChangesOnlyAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pendingModReapply = true;
                RecordFailure("Téléchargement annulé", "Cancellation");
            }
            catch (Exception ex)
            {
                _pendingModReapply = true;
                RecordFailure($"Échec de l'application: {ex.Message}", "Exception");
                Logger.LogWarning(ex, "[BASE-{appBase}] DownloadAndApplyCharacterAsync failed, marking for reapply", applicationBase);
            }
        }, downloadToken);
    }
    
    private async Task ApplyModChangesOnlyAsync(Guid applicationBase, CharacterData charaData,
        Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip, CancellationToken token)
    {
        Logger.LogDebug("[BASE-{applicationBase}] Applying mod changes only", applicationBase);

        try
        {
            var modOnlyUpdatedData = new Dictionary<ObjectKind, HashSet<PlayerChanges>>();

            foreach (var kvp in updatedData)
            {
                var modChanges = new HashSet<PlayerChanges>();
                if (updateModdedPaths && kvp.Value.Contains(PlayerChanges.ModFiles))
                {
                    modChanges.Add(PlayerChanges.ModFiles);
                }
                if (updateManip && kvp.Value.Contains(PlayerChanges.ModManip))
                {
                    modChanges.Add(PlayerChanges.ModManip);
                }

                if (modChanges.Count > 0)
                {
                    modOnlyUpdatedData[kvp.Key] = modChanges;
                }
            }

            if (modOnlyUpdatedData.Count == 0)
            {
                Logger.LogDebug("[BASE-{applicationBase}] No mod changes to apply", applicationBase);
                return;
            }
            
            foreach (var changes in modOnlyUpdatedData.Values)
            {
                changes.Remove(PlayerChanges.ForcedRedraw);
            }

            Logger.LogDebug("[BASE-{applicationBase}] Applying mod changes using simplified mechanism", applicationBase);
            await DownloadAndApplyCharacterAsync(applicationBase, charaData, modOnlyUpdatedData, updateModdedPaths, updateManip, token).ConfigureAwait(false);

            Logger.LogDebug("[BASE-{applicationBase}] Mod changes applied without forced redraw", applicationBase);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[BASE-{applicationBase}] Failed to apply mod changes only, falling back to full apply", applicationBase);
            await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, token).ConfigureAwait(false);
        }
    }

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync", applicationBase);
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];

        if (updateModdedPaths)
        {
            Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync > updateModdedPaths", applicationBase);
            int attempts = 0;
            List<FileReplacementData> toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_pairDownloadTask != null && !_pairDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for player {name}, {kind}", applicationBase, PlayerName, updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for player {name}, {kind}", applicationBase, PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files")));
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(_charaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(this, charaData, toDownloadFiles))
                {
                    Pair.HoldApplication("IndividualPerformanceThreshold", maxValue: 1);
                    _downloadManager.ClearDownload();
                    _pendingModReapply = true;
                    RecordFailure("Seuil VRAM dépassé", "VRAMThreshold");
                    return;
                }

                var downloadBatch = toDownloadReplacements.ToList();
                _pairDownloadTask = Task.Run(async () => await _downloadManager.DownloadFiles(_charaHandler!, downloadBatch, downloadToken).ConfigureAwait(false), downloadToken);

                await _pairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    _pendingModReapply = true;
                    RecordFailure("Téléchargement annulé", "Cancellation");
                    return;
                }

                toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

                if (toDownloadReplacements.TrueForAll(c =>
                    _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                    || _downloadManager.IsHashOnCooldown(c.Hash)))
                {
                    Logger.LogDebug("[BASE-{appBase}] All {count} remaining files are forbidden or on cooldown, stopping download loop", applicationBase, toDownloadReplacements.Count);
                    break;
                }

                var backoffSeconds = Math.Min(2 * Math.Pow(2, attempts - 1), 30);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), downloadToken).ConfigureAwait(false);
            }

            try
            {
                Mediator.Publish(new HaltScanMessage(nameof(PlayerPerformanceService.ShrinkTextures)));
                if (await _playerPerformanceService.ShrinkTextures(this, charaData, downloadToken).ConfigureAwait(false))
                    _ = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);
            }
            finally
            {
                Mediator.Publish(new ResumeScanMessage(nameof(PlayerPerformanceService.ShrinkTextures)));
            }

            bool exceedsThreshold = !await _playerPerformanceService.CheckBothThresholds(this, charaData).ConfigureAwait(false);

            if (exceedsThreshold)
                Pair.HoldApplication("IndividualPerformanceThreshold", maxValue: 1);
            else
                Pair.UnholdApplication("IndividualPerformanceThreshold");

            if (exceedsThreshold)
            {
                Logger.LogTrace("[BASE-{appBase}] Not applying due to performance thresholds", applicationBase);
                _pendingModReapply = true;
                RecordFailure("Seuils de performance dépassés", "PerformanceThreshold");
                return;
            }
        }

        if (Pair.IsApplicationBlocked)
        {
            var reasons = string.Join(", ", Pair.HoldApplicationReasons);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                $"Not applying character data: {reasons}")));
            Logger.LogTrace("[BASE-{appBase}] Not applying due to hold: {reasons}", applicationBase, reasons);
            _pendingModReapply = true;
            RecordFailure($"Application bloquée: {reasons}", Pair.HoldApplicationReasons.ToArray());
            return;
        }

        downloadToken.ThrowIfCancellationRequested();

        if (_applicationTask != null && !_applicationTask.IsCompleted)
        {
            Logger.LogDebug("[BASE-{appBase}] Cancelling current data application (Id: {id}) for player ({handler})", applicationBase, _applicationId, PlayerName);
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(downloadToken, timeoutCts.Token);
            try
            {
                await _applicationTask.WaitAsync(combinedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogWarning("[BASE-{appBase}] Timeout waiting for application task {id} to complete, proceeding anyway", applicationBase, _applicationId);
            }
        }
        else
        {
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        }

        if (downloadToken.IsCancellationRequested)
        {
            _pendingModReapply = true;
            RecordFailure("Application annulée", "Cancellation");
            return;
        }

        var token = _applicationCancellationTokenSource.Token;

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, token);
        await _applicationTask.ConfigureAwait(false);
    }

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, CancellationToken token)
    {
        ushort objIndex = ushort.MaxValue;
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            if (_penumbraCollection == Guid.Empty)
            {
                objIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(Logger, Pair.UserData.UID).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex).ConfigureAwait(false);
            }

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token).ConfigureAwait(false);
            if (_charaHandler!.Address != nint.Zero)
            {
                await _dalamudUtil.WaitForFullyLoadedAsync(_charaHandler!, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths)
            {
                // ensure collection is set
                if (objIndex == ushort.MaxValue)
                    objIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex).ConfigureAwait(false);

                await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, _applicationId, _penumbraCollection,
                    moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                {
                    if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                    LastAppliedDataBytes += path.Length;
                }
            }

            if (updateManip)
            {
                await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, _applicationId, _penumbraCollection, charaData.ManipulationData).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _cachedData = charaData;
            _lastAppliedData = charaData;
            _pendingModReapply = false;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
            _lastSuccessfulApplyAt = DateTime.UtcNow;
            ClearFailureState();
            IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("[{applicationId}] Application cancelled for {handler}", _applicationId, this);
            _pendingModReapply = true;
            RecordFailure("Application annulée", "Cancellation");
            _cachedData = charaData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
        }
        catch (Exception ex)
        {
            _pendingModReapply = true;
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _forceApplyMods = true;
                _cachedData = charaData;
                Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
                RecordFailure("Joueur devenu null pendant l'application", "PlayerNull");
                Logger.LogDebug("[{applicationId}] Cancelled, player turned null during application", _applicationId);
            }
            else
            {
                RecordFailure($"Échec de l'application: {ex.Message}", "Exception");
                Logger.LogWarning(ex, "[{applicationId}] Application failed", _applicationId);
            }
        }
    }

    private bool HasMissingFiles(CharacterData data)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in data.FileReplacements.SelectMany(k => k.Value))
        {
            if (!string.IsNullOrEmpty(replacement.FileSwapPath))
                continue;

            var hash = replacement.Hash;
            if (string.IsNullOrWhiteSpace(hash) || !seen.Add(hash))
                continue;

            var fileCache = _fileDbManager.GetFileCacheByHash(hash);
            if (fileCache is null || !File.Exists(fileCache.ResolvedFilepath))
            {
                if (fileCache is not null)
                    _fileDbManager.RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);

                if (!_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, hash, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    private void TryReapplyPendingData()
    {
        if (!_pendingModReapply || !IsVisible
            || (_applicationTask != null && !_applicationTask.IsCompleted)
            || (_downloadTask != null && !_downloadTask.IsCompleted))
            return;

        var now = DateTime.UtcNow;
        if (_lastApplyAttemptAt.HasValue && now - _lastApplyAttemptAt.Value < TimeSpan.FromSeconds(5))
            return;

        var dataToApply = _cachedData ?? Pair.LastReceivedCharacterData;
        if (dataToApply == null)
            return;

        Logger.LogDebug("Auto-retry: reapplying pending data for {handler} (pendingModReapply=true)", this);
        _ = Task.Run(() =>
        {
            try
            {
                ApplyCharacterData(Guid.NewGuid(), dataToApply, forceApplyCustomization: true);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reapply pending data for {handler}", this);
            }
        });
    }

    private void UpdateVisibility(bool nowVisible, bool invalidate = false)
    {
        if (string.IsNullOrEmpty(PlayerName))
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            if (pc.ObjectId == 0) return;
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("One-Time Initializing {pairHandler}", this);
            Initialize(pc.Name);
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("One-Time Initialized {pairHandler}", this);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                $"Initializing User For Character {pc.Name}")));
        }

        // This was triggered by the character becoming handled by Mare, so unapply everything
        // There seems to be a good chance that this races Mare and then crashes
        if (!nowVisible && invalidate)
        {
            bool wasVisible = IsVisible;
            IsVisible = false;
            _charaHandler?.Invalidate();
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            if (wasVisible && Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("{pairHandler} visibility changed, now: {visi}", this, IsVisible);

            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("Invalidating {pairHandler}", this);
            UndoApplication();
            return;
        }

        if (!IsVisible && nowVisible)
        {
            // This is deferred application attempt, avoid any log output
            if (_deferred != Guid.Empty)
            {
                _isVisible = true;
                _ = Task.Run(() =>
                {
                    ApplyCharacterData(_deferred, _cachedData!, forceApplyCustomization: true);
                });
            }

            IsVisible = true;
            Mediator.Publish(new PairHandlerVisibleMessage(this));
            if (_cachedData != null)
            {
                Guid appData = Guid.NewGuid();
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace("[BASE-{appBase}] {pairHandler} visibility changed, now: {visi}, cached data exists", appData, this, IsVisible);

                _ = Task.Run(() =>
                {
                    ApplyCharacterData(appData, _cachedData!, forceApplyCustomization: true);
                });
            }
            else if (Pair.LastReceivedCharacterData != null)
            {
                Guid appData = Guid.NewGuid();
                Logger.LogDebug("[BASE-{appBase}] {pairHandler} visibility changed, now: {visi}, using LastReceivedCharacterData fallback", appData, this, IsVisible);

                _ = Task.Run(() =>
                {
                    Pair.ApplyLastReceivedData(forced: true);
                });
            }
            else
            {
                Logger.LogTrace("{this} visibility changed, now: {visi}, no cached data exists", this, IsVisible);
            }

            // Retry automatique si une application précédente a échoué
            TryReapplyPendingData();
        }
        else if (IsVisible && !nowVisible)
        {
            IsVisible = false;
            _charaHandler?.Invalidate();
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace("{pairHandler} visibility changed, now: {visi}", this, IsVisible);
        }
    }

    public void Initialize(string name)
    {
        PlayerName = name;
        _charaHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident), isWatched: false).GetAwaiter().GetResult();

        if (_dalamudUtil.TryGetWorldIdByIdent(Pair.Ident, out var worldId))
        {
            Pair.SetWorldId(worldId);
            // Sauvegarder le nom et le WorldId pour utilisation ultérieure (pour les profils RP quand offline)
            _serverConfigurationManager.SetWorldIdForUid(Pair.UserData.UID, worldId);
        }

        if (!string.IsNullOrEmpty(name))
        {
            _serverConfigurationManager.SetNameForUid(Pair.UserData.UID, name);
        }

        Mediator.Subscribe<HonorificReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false), CancellationToken.None);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _cachedData.PetNamesData).ConfigureAwait(false), CancellationToken.None);
        });

        Mediator.Subscribe<MoodlesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.MoodlesData)) return;
            Logger.LogTrace("Reapplying Moodles data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.Moodles.SetStatusAsync(PlayerCharacter, _cachedData.MoodlesData).ConfigureAwait(false), CancellationToken.None);
        });
    }

    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
        if (address == nint.Zero) return;

        Logger.LogDebug("[{applicationId}] Reverting all Customization for {alias}/{name} {objectKind}", applicationId, Pair.UserData.AliasOrUID, name, objectKind);

        if (_customizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _customizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Heels for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring C+ for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }

    private List<FileReplacementData> TryCalculateModdedDictionary(Guid applicationBase, CharacterData charaData, out Dictionary<(string GamePath, string? Hash), string> moddedDictionary, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        moddedDictionary = [];
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        bool hasMigrationChanges = false;

        try
        {
            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();
                var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash, preferSubst: true);
                if (fileCache != null)
                {
                    if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, item.GamePaths[0].Split(".")[^1]);
                    }

                    foreach (var gamePath in item.GamePaths)
                    {
                        outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                    }
                }
                else
                {
                    Logger.LogTrace("Missing file: {hash}", item.Hash);
                    missingFiles.Add(item);
                }
            });

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList())
            {
                foreach (var gamePath in item.GamePaths)
                {
                    Logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase, gamePath, item.FileSwapPath);
                    moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements", applicationBase);
        }
        if (hasMigrationChanges) _fileDbManager.WriteOutFullCsv();
        st.Stop();
        Logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return [.. missingFiles];
    }
}
