using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Interop.Ipc.Penumbra;
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

public sealed partial class PairHandler : DisposableMediatorSubscriberBase, IPairHandlerAdapter
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
    private readonly Lock _applyGate = new();
    private readonly PenumbraCollectionBinder _collectionBinder;
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
        _assetResolver = new PairAssetResolver(logger, pair.UserData, new FileCacheLookupAdapter(fileDbManager),
            compressedAlternateManager, new ForbiddenTransferRegistryAdapter(transferManager),
            new TextureCompressionSettingsAdapter(playerPerformanceConfigService));
        _collectionBinder = new PenumbraCollectionBinder(ipcManager);
        _visibilityGrace = new PairVisibilityGrace(logger, pair, _state, ipcManager,
            describeForLog: ToString, isVisible: () => IsVisible);
        _reverter = new PairCharacterReverter(logger, pair, _state, ipcManager, dalamudUtil,
            gameObjectHandlerFactory, pairRedrawCoordinator, mediator,
            new PairCharacterReverter.Context(
                GetPlayerName: () => PlayerName,
                DescribeForLog: ToString,
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
            _state.Penumbra.Collection = Guid.Empty;
            _state.Penumbra.AssignedObjectIndex = -1;
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
    /// <summary>
    /// Identité du handler telle qu'elle apparaît dans les logs. Point de passage unique : les 22
    /// appels qui journalisent <c>{this}</c> ou <c>{handler}</c> passent tous par ici, donc c'est le
    /// seul endroit à garder pour que le nom du personnage ne fuite pas dans un log partagé.
    /// </summary>
    public override string ToString()
    {
        var presence = PlayerCharacter != nint.Zero ? "HasChar" : "NoChar";
        return _configService.Current.LogPlayerNames
            ? Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + presence
            : Pair.UserData.AliasOrUID + ":" + presence;
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
            Logger.LogDebug("Disposing {pair}", Pair.UserData.AliasOrUID);
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
            Logger.LogDebug("Disposing {pair} complete", Pair.UserData.AliasOrUID);
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
        Logger.LogDebug("Disabling sync for {pair}", ToString());
        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
        _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
    }
    
    private void EnableSync()
    {
        Logger.LogDebug("Enabling sync for {pair}", ToString());
        if (_dataReceivedInDowntime is not null && IsVisible)
        {
            var pending = _dataReceivedInDowntime;
            _dataReceivedInDowntime = null;

            Logger.LogDebug("Applying queued data for {pair}", ToString());
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
            Logger.LogDebug("Applying last received data for {pair}", ToString());
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
                Logger.LogTrace("Pause state already {state} for {pair}, skipping", paused ? "paused" : "unpaused", ToString());
                return;
            }

            _pauseRequested = paused;
            Logger.LogDebug("Queueing pause transition to {state} for {pair}", paused ? "paused" : "unpaused", ToString());

            _pauseTransitionTask = _pauseTransitionTask
                .ContinueWith(_ => paused ? PauseInternalAsync() : ResumeInternalAsync(), TaskScheduler.Default)
                .Unwrap();
        }
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
