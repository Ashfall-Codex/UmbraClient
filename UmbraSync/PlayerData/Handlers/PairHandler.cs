using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.PlayerData.Redraw;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
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
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly VisibilityService _visibilityService;
    private readonly ApplicationSemaphoreService _applicationSemaphoreService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PairRedrawCoordinator _pairRedrawCoordinator;
    private readonly PairAssetResolver _assetResolver;
    private readonly PairAppliedState _state = new();
    private readonly PairCharacterReverter _reverter;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private GameObjectHandler? _charaHandler;
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private Task? _downloadTask;
    private bool _isVisible;
    private readonly Lock _pauseLock = new();
    private Task _pauseTransitionTask = Task.CompletedTask;
    private bool _pauseRequested = false;
    private const int VisibilityApplyJitterMaxMs = 600;
    private readonly TimeSpan _reapplyJitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5000));
    private readonly PairVisibilityGrace _visibilityGrace;
    public bool ScheduledForDeletion
    {
        get => _visibilityGrace.ScheduledForDeletion;
        set => _visibilityGrace.ScheduledForDeletion = value;
    }
    public DateTime? InvisibleSinceUtc => _visibilityGrace.InvisibleSinceUtc;
    public DateTime? VisibilityEvictionDueAtUtc => _visibilityGrace.EvictionDueAtUtc;
    public DateTime? LastDataReceivedAt => _state.LastDataReceivedAt;
    public DateTime? LastApplyAttemptAt => _state.LastApplyAttemptAt;
    public DateTime? LastSuccessfulApplyAt => _state.LastSuccessfulApplyAt;
    public string? LastFailureReason => _state.LastFailureReason;
    public IReadOnlyList<string> LastBlockingConditions => _state.LastBlockingConditions;
    public string Ident => Pair.Ident;
    public bool Initialized => _charaHandler != null;
    public bool IsApplyingOrDownloading =>
        (_applicationTask != null && !_applicationTask.IsCompleted) ||
        (_downloadTask != null && !_downloadTask.IsCompleted);
    public CharacterData? LastReceivedCharacterData => _state.CachedData;

    public PairHandler(ILogger<PairHandler> logger, Pair pair, PairAnalyzer pairAnalyzer,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, MareMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        MareConfigService configService, VisibilityService visibilityService,
        ApplicationSemaphoreService applicationSemaphoreService, ServerConfigurationManager serverConfigurationManager,
        PairRedrawCoordinator pairRedrawCoordinator, CompressedAlternateManager compressedAlternateManager,
        PlayerPerformanceConfigService playerPerformanceConfigService) : base(logger, mediator)
    {
        Pair = pair;
        PairAnalyzer = pairAnalyzer;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _playerPerformanceService = playerPerformanceService;
        _configService = configService;
        _visibilityService = visibilityService;
        _applicationSemaphoreService = applicationSemaphoreService;
        _serverConfigurationManager = serverConfigurationManager;
        _pairRedrawCoordinator = pairRedrawCoordinator;
        _assetResolver = new PairAssetResolver(logger, pair.UserData, fileDbManager, compressedAlternateManager,
            transferManager, playerPerformanceConfigService);
        _visibilityGrace = new PairVisibilityGrace(logger, pair, _state, ipcManager,
            getPlayerName: () => PlayerName, isVisible: () => IsVisible);
        _reverter = new PairCharacterReverter(logger, pair, _state, ipcManager, dalamudUtil,
            gameObjectHandlerFactory, pairRedrawCoordinator, mediator,
            new PairCharacterReverter.Context(
                GetPlayerName: () => PlayerName,
                IsVisible: () => IsVisible,
                GetCharaHandler: () => _charaHandler,
                CancelInFlightWork: () =>
                {
                    _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
                    _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
                }));

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
            if (_state.Deferred != Guid.Empty && _state.CachedData != null)
            {
                ApplyCharacterData(_state.Deferred, _state.CachedData, forceApplyCustomization: true);
            }
            EnableSync();
        });
        Mediator.Subscribe<GposeStartMessage>(this, _ => DisableSync());
        Mediator.Subscribe<GposeEndMessage>(this, _ =>
        {
            if (_state.Deferred != Guid.Empty && _state.CachedData != null)
            {
                ApplyCharacterData(_state.Deferred, _state.CachedData, forceApplyCustomization: true);
            }
            EnableSync();
        });
        Mediator.Subscribe<InstanceOrDutyStartMessage>(this, _ => DisableSync());
        Mediator.Subscribe<InstanceOrDutyEndMessage>(this, _ => EnableSync());
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            _state.PenumbraCollection = Guid.Empty;
            _state.PenumbraAssignedObjectIndex = -1;
            if (_state.Deferred != Guid.Empty && _state.CachedData != null)
            {
                ApplyCharacterData(_state.Deferred, _state.CachedData, forceApplyCustomization: true);
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
                _state.RedrawOnNextApplication = true;
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
                    _visibilityGrace.Cancel();
                }
                else
                {
                    _visibilityGrace.Start();
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
        _state.LastFailureReason = reason;
        _state.LastBlockingConditions = conditions.Length == 0 ? Array.Empty<string>() : conditions.ToArray();
    }
    // Efface l'état d'échec précédent.
    private void ClearFailureState() => _state.ClearFailure();
    
    // Appeles des données reçues pour ce handler.
    public void OnDataReceived()
    {
        _state.LastDataReceivedAt = DateTime.UtcNow;
    }
    
    public void ResetDownloadFailures() => _downloadManager.ResetFailureState();
    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        _state.LastApplyAttemptAt = DateTime.UtcNow;
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
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _state.CachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = hasDiffMods || _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _state.CachedData.DataHash.Value, _state.ForceApplyMods);
            _isVisible = false;
            _state.Deferred = applicationBase;
            return;
        }

        _state.Deferred = Guid.Empty;

        SetUploading(isUploading: false);

        if (Pair.IsDownloadBlocked)
        {
            var reasons = string.Join(", ", Pair.HoldDownloadReasons);
            RecordFailure($"Téléchargement bloqué: {reasons}", Pair.HoldDownloadReasons.ToArray());
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                $"Not applying character data: {reasons}")));
            Logger.LogDebug("[BASE-{appBase}] Not applying due to hold: {reasons}", applicationBase, reasons);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _state.CachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = hasDiffMods || _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _state.CachedData.DataHash.Value, _state.ForceApplyMods);
            return;
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _state.ForceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase, characterData.DataHash.Value, _state.CachedData?.DataHash.Value ?? "NODATA");

        var hasMissingFiles = false;
        if (string.Equals(characterData.DataHash.Value, _state.CachedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
            && !forceApplyCustomization
            && !_state.ForceApplyMods
            && !_state.PendingModReapply)
        {
            hasMissingFiles = _assetResolver.HasMissingFiles(characterData);
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
            _state.ForceApplyMods = characterData.CheckUpdatedData(applicationBase, _state.CachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            _state.Deferred = applicationBase;
            _isVisible = false;
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        _state.ForceApplyMods |= forceApplyCustomization || hasMissingFiles;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _state.CachedData?.DeepClone() ?? new(), Logger, this, forceApplyCustomization, _state.ForceApplyMods);

        if (_charaHandler != null && _state.ForceApplyMods)
        {
            _state.ForceApplyMods = false;
        }

        bool redrawForcedExternally = false;
        if (_state.RedrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _state.RedrawOnNextApplication = false;
            redrawForcedExternally = true;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        // Décision de redraw (soft/hard) calculée à partir du même diff que les PlayerChanges,
        // uniquement si la feature est activée. OFF -> null -> HardRedraw (comportement actuel).
        // Elle voyage avec l'application : un second push pour la même paire ne doit pas réécrire
        // la décision d'une application encore en vol (elle s'appliquerait à un diff différent).
        var redrawDecisions = _configService.Current.EnableSoftRedraw
            ? characterData.ComputeRedrawDecisions(_state.CachedData, charaDataToUpdate)
            : null;

        // Un redraw imposé de l'extérieur (changement de job) ne se déduit pas du diff de fichiers :
        // sans ça, un changement de job simultané à un diff texture seule tombait en soft reapply
        // et la paire restait affichée avec l'équipement du job précédent.
        if (redrawForcedExternally && redrawDecisions != null)
            redrawDecisions[ObjectKind.Player] = PairRedrawDecision.HardRedraw;

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate, redrawDecisions);
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
        _state.ForceApplyMods = true;
        _state.PendingModReapply = true;
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

            _reverter.UndoApplicationAsync(applicationId).GetAwaiter().GetResult();

            PlayerName = null;
            _applicationCancellationTokenSource?.Dispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            _visibilityGrace.Cancel();
            _charaHandler?.Dispose();
            _charaHandler = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {user}", Pair.UserData.UID);
        }
        finally
        {
            _state.CachedData = null;
            _state.LastAppliedData = null;
            _state.PendingModReapply = false;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));
            Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    public void UndoApplication(Guid applicationId = default)
    {
        _ = Task.Run(async () =>
        {
            await _reverter.UndoApplicationAsync(applicationId).ConfigureAwait(false);
        });
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
                    Logger.LogError(ex, "Failed applying queued data for {user}", Pair.UserData.UID);
                }
            });
        }
    }

    private async Task PauseInternalAsync()
    {
        try
        {
            Logger.LogInformation("Pausing handler for {user}", Pair.UserData.UID);
            DisableSync();
            if (_charaHandler is not null && _charaHandler.Address != nint.Zero)
            {
                var applicationId = Guid.NewGuid();
                await _reverter.RevertToRestoredAsync(applicationId).ConfigureAwait(false);
            }
            Mediator.Publish(new PlayerVisibilityMessage(Pair.Ident, IsVisible: false, Invalidate: true));

            Logger.LogInformation("Pause complete for {user}", Pair.UserData.UID);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to pause handler for {user}", Pair.UserData.UID);
        }
    }
    
    private async Task ResumeInternalAsync()
    {
        try
        {
            Logger.LogInformation("Resuming handler for {user}", Pair.UserData.UID);

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

            Logger.LogInformation("Resume complete for {user}", Pair.UserData.UID);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to resume handler for {user}", Pair.UserData.UID);
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

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken token)
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
                await _dalamudUtil.RunOnFrameworkThread(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, serialChangeList, charaData, redrawDecisions, token).ConfigureAwait(false)).ConfigureAwait(false);
                await Task.Run(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, asyncChangeList, charaData, redrawDecisions, token).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var orderedChanges = changes.Value.OrderBy(p => (int)p).ToList();
                await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, orderedChanges, charaData, redrawDecisions, token).ConfigureAwait(false);
            }
        }
        finally
        {
            handlerToDispose?.Dispose();
        }
    }

    private async Task ProcessCustomizationChangesAsync(GameObjectHandler handler, Guid applicationId, ObjectKind objectKind,
        IEnumerable<PlayerChanges> changeList, CharacterData charaData,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken token)
    {
        foreach (var change in changeList)
        {
            Logger.LogDebug("[{applicationId}{ft}] Processing {change} for {handler}", applicationId, _dalamudUtil.IsOnFrameworkThread ? "*" : string.Empty, change, handler);
            switch (change)
            {
                case PlayerChanges.Customize:
                    if (charaData.CustomizePlusData.TryGetValue(objectKind, out var customizePlusData))
                    {
                        _state.CustomizeIds[objectKind] = await _ipcManager.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                    }
                    else if (_state.CustomizeIds.TryGetValue(objectKind, out var customizeId))
                    {
                        await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                        _state.CustomizeIds.Remove(objectKind);
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
                    // Décision soft/hard quand la feature est active et qu'une décision existe pour ce
                    // kind, sinon HardRedraw (comportement historique, redraw Penumbra complet)
                    var redrawDecision = (_configService.Current.EnableSoftRedraw
                            && redrawDecisions != null
                            && redrawDecisions.TryGetValue(objectKind, out var d))
                        ? d
                        : PairRedrawDecision.HardRedraw;
                    await _pairRedrawCoordinator.ExecuteDecisionAsync(redrawDecision, Logger, handler, applicationId, token).ConfigureAwait(false);
                    break;

            }

            token.ThrowIfCancellationRequested();
        }
    }

    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions)
    {
        if (updatedData.Count == 0)
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            return;
        }

        if (string.Equals(charaData.DataHash.Value, _state.LastAppliedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
            && !updatedData.Values.Any(v => v.Contains(PlayerChanges.ForcedRedraw))
            && !_state.PendingModReapply)
        {
            Logger.LogDebug("[BASE-{appBase}] Already applied hash {hash} and no pending reapply, ignoring", applicationBase, charaData.DataHash.Value);
            return;
        }

        _state.PendingModReapply = false;

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));
        var hasOtherChanges = updatedData.Values.Any(v => v.Any(p => p != PlayerChanges.ModFiles && p != PlayerChanges.ModManip && p != PlayerChanges.ForcedRedraw));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _downloadTask = Task.Run(async () =>
        {
            // Note: the global GPU-heavy semaphore is acquired later, just before the
            // Penumbra apply stage — not here — so file downloads (network/CPU bound)
            // can run in parallel without holding GPU slots. See DownloadAndApplyCharacterAsync.
            if ((updateModdedPaths || updateManip) && !hasOtherChanges && !_state.ForceApplyMods)
            {
                Logger.LogDebug("[BASE-{appBase}] Applying mod changes only - skipping full redraw", applicationBase);
                await ApplyModChangesOnlyAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, redrawDecisions, downloadToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, redrawDecisions, downloadToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _state.PendingModReapply = true;
                RecordFailure("Téléchargement annulé", "Cancellation");
            }
            catch (Exception ex)
            {
                _state.PendingModReapply = true;
                RecordFailure($"Échec de l'application: {ex.Message}", "Exception");
                Logger.LogWarning(ex, "[BASE-{appBase}] DownloadAndApplyCharacterAsync failed, marking for reapply", applicationBase);
            }
        }, downloadToken);
    }
    
    private async Task ApplyModChangesOnlyAsync(Guid applicationBase, CharacterData charaData,
        Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken token)
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
            await DownloadAndApplyCharacterAsync(applicationBase, charaData, modOnlyUpdatedData, updateModdedPaths, updateManip, redrawDecisions, token).ConfigureAwait(false);

            Logger.LogDebug("[BASE-{applicationBase}] Mod changes applied without forced redraw", applicationBase);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[BASE-{applicationBase}] Failed to apply mod changes only, falling back to full apply", applicationBase);
            await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, redrawDecisions, token).ConfigureAwait(false);
        }
    }

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken downloadToken)
    {
        Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync", applicationBase);
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];

        if (updateModdedPaths)
        {
            Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync > updateModdedPaths", applicationBase);
            int attempts = 0;
            var compressedUsage = _assetResolver.ComputeCompressedAlternateUsage();
            var resolution = _assetResolver.Resolve(applicationBase, charaData, compressedUsage, downloadToken);
            List<FileReplacementData> toDownloadReplacements = resolution.MissingFiles;
            var locallyPresentFiles = resolution.LocallyPresentFiles;
            // moddedPaths n'est pas repris ici : la résolution finale, après la boucle de download,
            // écrase de toute façon le dictionnaire avant qu'il ne soit lu.

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
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(_charaHandler!, toDownloadReplacements, compressedUsage, locallyPresentFiles, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(this, charaData, toDownloadFiles))
                {
                    Pair.HoldApplication("IndividualPerformanceThreshold", maxValue: 1);
                    _downloadManager.ClearDownload();
                    _state.PendingModReapply = true;
                    RecordFailure("Seuil VRAM dépassé", "VRAMThreshold");
                    return;
                }

                var downloadBatch = toDownloadReplacements.ToList();
                _pairDownloadTask = Task.Run(async () => await _downloadManager.DownloadFiles(_charaHandler!, downloadBatch, downloadToken).ConfigureAwait(false), downloadToken);

                await _pairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    _state.PendingModReapply = true;
                    RecordFailure("Téléchargement annulé", "Cancellation");
                    return;
                }

                resolution = _assetResolver.Resolve(applicationBase, charaData, compressedUsage, downloadToken);
                toDownloadReplacements = resolution.MissingFiles;
                locallyPresentFiles = resolution.LocallyPresentFiles;

                var forbiddenOnly = toDownloadReplacements.Where(c =>
                    _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))).ToList();
                var missingOnServerOnly = toDownloadReplacements.Where(c =>
                    !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                    && _downloadManager.IsHashMissingOnServer(c.Hash)).ToList();
                var onCooldownOnly = toDownloadReplacements.Where(c =>
                    !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                    && !_downloadManager.IsHashMissingOnServer(c.Hash)
                    && _downloadManager.IsHashOnCooldown(c.Hash)).ToList();
                var retriableNow = toDownloadReplacements.Count - forbiddenOnly.Count - missingOnServerOnly.Count - onCooldownOnly.Count;

                if (retriableNow == 0)
                {
                    if (onCooldownOnly.Count > 0)
                    {
                        Logger.LogWarning("[BASE-{appBase}] {cooldown} fichiers en cooldown, {missing} absents du serveur et {forbidden} non accessible sur {total}. Reapply.",
                            applicationBase, onCooldownOnly.Count, missingOnServerOnly.Count, forbiddenOnly.Count, toDownloadReplacements.Count);
                        _state.PendingModReapply = true;
                    }
                    else if (missingOnServerOnly.Count > 0)
                    {
                        Logger.LogWarning("[BASE-{appBase}] {missing} fichiers absents du serveur sur {total} : application partielle sans reapply (le pair doit repousser ses données)",
                            applicationBase, missingOnServerOnly.Count, toDownloadReplacements.Count);
                    }
                    else
                    {
                        Logger.LogDebug("[BASE-{appBase}] All {count} remaining files are permanently forbidden, stopping download loop", applicationBase, forbiddenOnly.Count);
                    }
                    break;
                }

                var backoffSeconds = Math.Min(2 * Math.Pow(2, attempts - 1), 30);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), downloadToken).ConfigureAwait(false);
            }

            var finalResolution = _assetResolver.Resolve(applicationBase, charaData, compressedUsage, downloadToken);
            var finalMissing = finalResolution.MissingFiles;
            moddedPaths = finalResolution.ModdedPaths;
            if (finalMissing.Count > 0)
            {
                var retriableMissing = finalMissing.Count(c =>
                    !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                    && !_downloadManager.IsHashMissingOnServer(c.Hash));
                if (retriableMissing > 0)
                {
                    Logger.LogWarning("[BASE-{appBase}] Applying with {missing} missing files ({retriable} retriable) — reapply scheduled",
                        applicationBase, finalMissing.Count, retriableMissing);
                    _state.PendingModReapply = true;
                }
                else
                {
                    Logger.LogDebug("[BASE-{appBase}] {count} missing files are all forbidden or absent server-side, no reapply", applicationBase, finalMissing.Count);
                }
            }

            try
            {
                Mediator.Publish(new HaltScanMessage(nameof(PlayerPerformanceService.ShrinkTextures)));
                if (await _playerPerformanceService.ShrinkTextures(this, charaData, downloadToken).ConfigureAwait(false))
                    moddedPaths = _assetResolver
                        .Resolve(applicationBase, charaData, _assetResolver.ComputeCompressedAlternateUsage(), downloadToken)
                        .ModdedPaths;
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
                _state.PendingModReapply = true;
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
            _state.PendingModReapply = true;
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
            _state.PendingModReapply = true;
            RecordFailure("Application annulée", "Cancellation");
            return;
        }

        var token = _applicationCancellationTokenSource.Token;
        var hadMissingFiles = _state.PendingModReapply;
        
#pragma warning disable MA0004 // ConfigureAwait on await using
        await using var applyLease = await _applicationSemaphoreService
            .AcquireAsync(token, highPriority: IsVisible, gpuHeavy: updateModdedPaths || updateManip)
            .ConfigureAwait(false);
#pragma warning restore MA0004

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, redrawDecisions, token);
        await _applicationTask.ConfigureAwait(false);
        if (hadMissingFiles && !_state.PendingModReapply)
        {
            Logger.LogDebug("[BASE-{appBase}] Restoring pendingModReapply: applied with missing files", applicationBase);
            _state.PendingModReapply = true;
        }
    }

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken token)
    {
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            if (_state.PenumbraCollection == Guid.Empty)
            {
                var initialIndex = await TryResolveObjectIndexAsync().ConfigureAwait(false);
                if (initialIndex == ushort.MaxValue)
                {
                    AbortApplication(charaData, "Index d'objet introuvable avant la pose des mods", "ObjectIndexUnavailable");
                    return;
                }

                _state.PenumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(Logger, Pair.UserData.UID).ConfigureAwait(false);
                _state.PenumbraAssignedObjectIndex = -1;
                if (_state.PenumbraCollection == Guid.Empty)
                {
                    AbortApplication(charaData, "Création de la collection temporaire refusée par Penumbra", "PenumbraCollectionUnavailable");
                    return;
                }

                if (!await TryAssignCollectionAsync(charaData, initialIndex).ConfigureAwait(false))
                    return;
            }

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token).ConfigureAwait(false);
            if (_charaHandler!.Address != nint.Zero)
            {
                await _dalamudUtil.WaitForFullyLoadedAsync(_charaHandler!, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths || updateManip)
            {
                // L'attente ci-dessus peut durer jusqu'à 30 s : on re-résout l'index, l'acteur a pu
                // changer de place dans la table entre-temps.
                var objIndex = await TryResolveObjectIndexAsync().ConfigureAwait(false);
                if (objIndex == ushort.MaxValue)
                {
                    AbortApplication(charaData, "Index d'objet introuvable avant la pose des mods", "ObjectIndexUnavailable");
                    return;
                }

                if (_state.PenumbraAssignedObjectIndex != objIndex
                    && !await TryAssignCollectionAsync(charaData, objIndex).ConfigureAwait(false))
                {
                    return;
                }
                
                var applied = await _ipcManager.Penumbra.ApplyTemporaryStateAsync(Logger, _applicationId, _state.PenumbraCollection,
                    updateModdedPaths ? moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal) : null,
                    updateManip ? charaData.ManipulationData : null).ConfigureAwait(false);

                if (!applied)
                {
                    AbortApplication(charaData, "Pose de l'état Penumbra refusée", "PenumbraApplyFailed");
                    return;
                }

                if (updateModdedPaths)
                {
                    LastAppliedDataBytes = -1;
                    foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                    {
                        if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                        LastAppliedDataBytes += path.Length;
                    }
                }
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, redrawDecisions, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _state.CachedData = charaData;
            _state.LastAppliedData = charaData;
            _state.PendingModReapply = false;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
            _state.LastSuccessfulApplyAt = DateTime.UtcNow;
            ClearFailureState();
            IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("[{applicationId}] Application cancelled for {handler}", _applicationId, this);
            _state.PendingModReapply = true;
            RecordFailure("Application annulée", "Cancellation");
            _state.CachedData = charaData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
        }
        catch (Exception ex)
        {
            _state.PendingModReapply = true;
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _state.ForceApplyMods = true;
                _state.CachedData = charaData;
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

    private async Task<ushort> TryResolveObjectIndexAsync()
    {
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                var handler = _charaHandler;
                if (handler is null || handler.Address == nint.Zero) return ushort.MaxValue;
                return handler.GetGameObject()?.ObjectIndex ?? ushort.MaxValue;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[{applicationId}] Échec de la résolution de l'index d'objet pour {handler}", _applicationId, this);
            return ushort.MaxValue;
        }
    }

    private async Task<bool> TryAssignCollectionAsync(CharacterData charaData, ushort objIndex)
    {
        var assign = await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _state.PenumbraCollection, objIndex).ConfigureAwait(false);
        if (assign != global::Penumbra.Api.Enums.PenumbraApiEc.Success)
        {
            _state.PenumbraAssignedObjectIndex = -1;
            AbortApplication(charaData, $"Assignation de la collection refusée par Penumbra ({assign})", "PenumbraAssignFailed");
            return false;
        }

        _state.PenumbraAssignedObjectIndex = objIndex;
        return true;
    }

    private void AbortApplication(CharacterData charaData, string reason, params string[] conditions)
    {
        Logger.LogWarning("[{applicationId}] Application interrompue pour {handler} : {reason}", _applicationId, this, reason);
        _state.PendingModReapply = true;
        RecordFailure(reason, conditions);
        _state.CachedData = charaData;
        Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
    }

    private void TryReapplyPendingData()
    {
        if (!_state.PendingModReapply || !IsVisible
            || (_applicationTask != null && !_applicationTask.IsCompleted)
            || (_downloadTask != null && !_downloadTask.IsCompleted))
            return;

        var now = DateTime.UtcNow;
        // Intervalle déphasé par-handler (5s + offset stable 0-5s) : sans ça, tous les pairs ayant
        // posé _state.PendingModReapply au même moment (cache froid à 24 pairs) relancent un apply complet
        // au MÊME tick toutes les 5s, en lockstep -> burst périodique. Le déphasage les étale.
        if (_state.LastApplyAttemptAt.HasValue && now - _state.LastApplyAttemptAt.Value < TimeSpan.FromSeconds(5) + _reapplyJitter)
            return;

        var dataToApply = _state.CachedData ?? Pair.LastReceivedCharacterData;
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
            IsVisible = true;
            Mediator.Publish(new PairHandlerVisibleMessage(this));
            int applyJitterMs = Random.Shared.Next(0, VisibilityApplyJitterMaxMs);

            if (_state.Deferred != Guid.Empty && _state.CachedData != null)
            {
                // application différée : pas de log (déjà tracé à la réception)
                Guid deferredId = _state.Deferred;
                CharacterData deferredData = _state.CachedData;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(applyJitterMs).ConfigureAwait(false);
                    ApplyCharacterData(deferredId, deferredData, forceApplyCustomization: true);
                });
            }
            else if (_state.CachedData != null)
            {
                Guid appData = Guid.NewGuid();
                CharacterData cached = _state.CachedData;
                if (Logger.IsEnabled(LogLevel.Trace))
                    Logger.LogTrace("[BASE-{appBase}] {pairHandler} visibility changed, now: {visi}, cached data exists", appData, this, IsVisible);

                _ = Task.Run(async () =>
                {
                    await Task.Delay(applyJitterMs).ConfigureAwait(false);
                    ApplyCharacterData(appData, cached, forceApplyCustomization: true);
                });
            }
            else if (Pair.LastReceivedCharacterData != null)
            {
                Guid appData = Guid.NewGuid();
                Logger.LogDebug("[BASE-{appBase}] {pairHandler} visibility changed, now: {visi}, using LastReceivedCharacterData fallback", appData, this, IsVisible);

                _ = Task.Run(async () =>
                {
                    await Task.Delay(applyJitterMs).ConfigureAwait(false);
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
            if (string.IsNullOrEmpty(_state.CachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _state.CachedData.HonorificData).ConfigureAwait(false), CancellationToken.None);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_state.CachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _state.CachedData.PetNamesData).ConfigureAwait(false), CancellationToken.None);
        });

        Mediator.Subscribe<MoodlesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_state.CachedData?.MoodlesData)) return;
            Logger.LogTrace("Reapplying Moodles data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.Moodles.SetStatusAsync(PlayerCharacter, _state.CachedData.MoodlesData).ConfigureAwait(false), CancellationToken.None);
        });
    }

}
