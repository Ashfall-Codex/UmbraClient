using Glamourer.Api.Enums;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.PlayerData.Redraw;
using UmbraSync.Services;
using UmbraSync.Services.Events;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using ObjectKind = UmbraSync.API.Data.Enum.ObjectKind;
using PlayerChanges = UmbraSync.PlayerData.Data.PlayerChanges;

namespace UmbraSync.PlayerData.Handlers;

public sealed partial class PairHandler
{

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        lock (_applyGate)
        {
            ApplyCharacterDataCore(applicationBase, characterData, forceApplyCustomization);
        }
    }

    private void ApplyCharacterDataCore(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization)
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
                ToString(), forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = hasDiffMods || _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _state.CachedData.DataHash.Value, _state.ForceApplyMods);
            // Pas de _isVisible = false ici : VisibilityService ne republierait jamais la transition.
            // La reprise passe par TryReapplyPendingData dès que l'acteur a de nouveau une adresse.
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
                ToString(), forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = hasDiffMods || _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _state.CachedData.DataHash.Value, _state.ForceApplyMods);
            return;
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _state.ForceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, last applied hash is {oldHash}", applicationBase, characterData.DataHash.Value, _state.LastAppliedData?.DataHash.Value ?? "NODATA");

        if (!forceApplyCustomization && !_state.ForceApplyMods && IsApplyingOrDownloading
            && string.Equals(characterData.DataHash.Value, _inFlightDataHash, StringComparison.Ordinal))
        {
            Logger.LogDebug("[BASE-{appbase}] Hash {hash} is already being applied, ignoring", applicationBase, characterData.DataHash.Value);
            return;
        }

        var hasMissingFiles = false;
        if (string.Equals(characterData.DataHash.Value, _state.LastAppliedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
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
                ToString(), forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _state.ForceApplyMods = _state.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _state.CachedData == null);
            _state.CachedData = characterData;
            _state.Deferred = applicationBase;
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        _state.ForceApplyMods |= forceApplyCustomization || hasMissingFiles;

        // Diff contre la dernière donnée réellement appliquée : CachedData peut contenir une donnée
        // différée ou annulée, dont les mods n'ont jamais été posés.
        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _state.LastAppliedData?.DeepClone() ?? new(), Logger, ToString(), forceApplyCustomization, _state.ForceApplyMods);

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
            Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {pair}", applicationBase, this);

        // Décision de redraw (soft/hard) calculée à partir du même diff que les PlayerChanges,
        // uniquement si la feature est activée. OFF -> null -> HardRedraw (comportement actuel).
        // Elle voyage avec l'application : un second push pour la même paire ne doit pas réécrire
        // la décision d'une application encore en vol (elle s'appliquerait à un diff différent).
        var redrawDecisions = _configService.Current.EnableSoftRedraw
            ? characterData.ComputeRedrawDecisions(_state.LastAppliedData, charaDataToUpdate)
            : null;

        // Un redraw imposé de l'extérieur (changement de job) ne se déduit pas du diff de fichiers :
        // sans ça, un changement de job simultané à un diff texture seule tombait en soft reapply
        // et la paire restait affichée avec l'équipement du job précédent.
        if (redrawForcedExternally && redrawDecisions != null)
            redrawDecisions[ObjectKind.Player] = PairRedrawDecision.HardRedraw;

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate, redrawDecisions);
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, CancellationToken token)
    {
        // Joueur disparu : c'est un échec, pas un succès silencieux (sinon l'application est comptée
        // comme faite et rien ne la relance au retour du joueur).
        var ptr = PlayerCharacter;
        if (ptr == nint.Zero)
            throw new InvalidOperationException("Player pointer is zero, cannot apply customization data");

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
                if (changes.Key == ObjectKind.Player)
                    throw new InvalidOperationException("Player pointer is zero, cannot apply customization data");

                // Monture/familier/compagnon pas encore sorti : réappliqué à son apparition.
                lock (_state.PendingOwnedObjects)
                    _state.PendingOwnedObjects.Add(changes.Key);
                Logger.LogDebug("[{applicationId}] {kind} not present for {handler}, deferring until it spawns", applicationId, changes.Key, this);
                return;
            }

            if (changes.Key != ObjectKind.Player)
            {
                lock (_state.PendingOwnedObjects)
                    _state.PendingOwnedObjects.Remove(changes.Key);
            }

            // Joueur : référence prise après la pose des mods. Objets possédés : les mods sont déjà posés à ce
            // stade, donc tout redraw Penumbra postérieur les a chargés et rend le nôtre inutile.
            var redrawBaseline = changes.Key == ObjectKind.Player
                ? _redrawBaseline
                : _pairRedrawCoordinator.CaptureBaseline(handler.Address);

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_configService.Current.SerialApplication)
            {
                var orderedChanges = changes.Value.OrderBy(p => (int)p).ToList();
                var serialChangeList = orderedChanges.Where(p => p <= PlayerChanges.ForcedRedraw).ToList();
                var asyncChangeList = orderedChanges.Where(p => p > PlayerChanges.ForcedRedraw).ToList();
                await _dalamudUtil.RunOnFrameworkThread(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, serialChangeList, charaData, redrawDecisions, redrawBaseline, token).ConfigureAwait(false)).ConfigureAwait(false);
                await Task.Run(async () => await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, asyncChangeList, charaData, redrawDecisions, redrawBaseline, token).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var orderedChanges = changes.Value.OrderBy(p => (int)p).ToList();
                await ProcessCustomizationChangesAsync(handler, applicationId, changes.Key, orderedChanges, charaData, redrawDecisions, redrawBaseline, token).ConfigureAwait(false);
            }
        }
        finally
        {
            handlerToDispose?.Dispose();
        }
    }

    private async Task ProcessCustomizationChangesAsync(GameObjectHandler handler, Guid applicationId, ObjectKind objectKind,
        IEnumerable<PlayerChanges> changeList, CharacterData charaData,
        IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, PairRedrawBaseline? redrawBaseline, CancellationToken token)
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
                        LastOwnGlamourerCallUtc = DateTime.UtcNow;
                        var glamourerResult = await _ipcManager.Glamourer.ApplyAllAsync(Logger, handler, glamourerData, applicationId, token, allowImmediate: true).ConfigureAwait(false);
                        LastOwnGlamourerCallUtc = DateTime.UtcNow;
                        if (glamourerResult == GlamourerApiEc.InvalidKey)
                            _glamourerLockRefused = true;
                    }
                    break;

                case PlayerChanges.PetNames:
                    await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Moodles:
                    await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.ForcedRedraw:
                    var redrawDecision = (_configService.Current.EnableSoftRedraw
                            && redrawDecisions != null
                            && redrawDecisions.TryGetValue(objectKind, out var d))
                        ? d
                        : PairRedrawDecision.HardRedraw;
                    await _pairRedrawCoordinator.ExecuteDecisionAsync(redrawDecision, Logger, handler, applicationId, token,
                        redrawBaseline).ConfigureAwait(false);
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

        // PendingModReapply n'est plus effacé ici : seule une application menée à terme l'efface, et
        // seulement si aucune nouvelle demande n'est arrivée entre-temps (génération).
        var reapplyGeneration = _state.ModReapplyGeneration;

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;
        var dataHash = charaData.DataHash.Value;
        _inFlightDataHash = dataHash;

        // Un changement de mods seul passe par le même chemin que le reste : le ForcedRedraw issu du diff
        // est exécuté via PairRedrawCoordinator (soft/hard selon la décision, redraw sauté si Penumbra
        // a déjà redessiné l'acteur après la pose des mods).
        // CancellationToken.None : le délégué doit toujours tourner pour marquer la reprise s'il est annulé.
        _downloadTask = Task.Run(async () =>
        {
            // Bail propre à cette passe : protège de l'éviction les fichiers téléchargés mais pas encore appliqués
            object pendingLeaseOwner = new();
            try
            {
                await DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, redrawDecisions, reapplyGeneration, pendingLeaseOwner, downloadToken).ConfigureAwait(false);
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
            finally
            {
                _cacheLeases.ReleaseLeases(pendingLeaseOwner);
                if (string.Equals(_inFlightDataHash, dataHash, StringComparison.Ordinal))
                    _inFlightDataHash = null;
            }
        }, CancellationToken.None);
    }

    private string? _inFlightDataHash;

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, int reapplyGeneration,
        object pendingLeaseOwner, CancellationToken downloadToken)
    {
        Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync", applicationBase);
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];
        bool appliedWithRetriableMissingFiles = false;

        if (updateModdedPaths)
        {
            Logger.LogTrace("[BASE-{appBase}] DownloadAndApplyCharacterAsync > updateModdedPaths", applicationBase);
            int attempts = 0;
            var compressedUsage = _assetResolver.ComputeCompressedAlternateUsage();
            var resolution = _assetResolver.Resolve(applicationBase, charaData, compressedUsage, downloadToken);
            List<FileReplacementData> toDownloadReplacements = resolution.MissingFiles;
            var locallyPresentFiles = resolution.LocallyPresentFiles;
            LeasePendingFiles(pendingLeaseOwner, charaData, resolution.ModdedPaths.Values);
            // moddedPaths n'est pas repris ici : la résolution finale, après la boucle de download,
            // écrase de toute façon le dictionnaire avant qu'il ne soit lu.

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                var priorDownloadTask = _pairDownloadTask;
                if (priorDownloadTask != null && !priorDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for {pair}, {kind}", applicationBase, ToString(), updatedData);
                    try
                    {
                        await priorDownloadTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!downloadToken.IsCancellationRequested)
                    {
                        // Le téléchargement précédent a été annulé par cette application même : on continue.
                        Logger.LogDebug("[BASE-{appBase}] Prior download task was cancelled, continuing", applicationBase);
                    }
                    catch (Exception ex) when (!downloadToken.IsCancellationRequested)
                    {
                        Logger.LogDebug(ex, "[BASE-{appBase}] Prior download task failed, continuing", applicationBase);
                    }
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for {pair}, {kind}", applicationBase, ToString(), updatedData);

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
                LeasePendingFiles(pendingLeaseOwner, charaData, resolution.ModdedPaths.Values);

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
                    else if (forbiddenOnly.Count > 0)
                    {
                        Logger.LogDebug("[BASE-{appBase}] All {count} remaining files are permanently forbidden, stopping download loop", applicationBase, forbiddenOnly.Count);
                    }
                    else
                    {
                        Logger.LogDebug("[BASE-{appBase}] Tous les fichiers ont été récupérés, fin de la boucle de téléchargement", applicationBase);
                    }
                    break;
                }

                var backoffSeconds = Math.Min(2 * Math.Pow(2, attempts - 1), 30);
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), downloadToken).ConfigureAwait(false);
            }

            var finalResolution = _assetResolver.Resolve(applicationBase, charaData, compressedUsage, downloadToken);
            var finalMissing = finalResolution.MissingFiles;
            moddedPaths = finalResolution.ModdedPaths;
            LeasePendingFiles(pendingLeaseOwner, charaData, moddedPaths.Values);
            if (finalMissing.Count > 0)
            {
                var retriableMissing = finalMissing.Count(c =>
                    !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                    && !_downloadManager.IsHashMissingOnServer(c.Hash));
                if (retriableMissing > 0)
                {
                    Logger.LogWarning("[BASE-{appBase}] Applying with {missing} missing files ({retriable} retriable) — reapply scheduled",
                        applicationBase, finalMissing.Count, retriableMissing);
                    appliedWithRetriableMissingFiles = true;
                    _state.PendingModReapply = true;
                }
                else
                {
                    Logger.LogDebug("[BASE-{appBase}] {count} missing files are all forbidden or absent server-side, no reapply", applicationBase, finalMissing.Count);
                }

                // Fichiers que le serveur n'a plus : on lui demande de les faire ré-uploader par le pair
                var missingOnServer = finalMissing
                    .Where(c => !string.IsNullOrEmpty(c.Hash)
                        && !_downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))
                        && _downloadManager.IsHashMissingOnServer(c.Hash))
                    .Select(c => c.Hash)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missingOnServer.Count > 0)
                    Mediator.Publish(new ReportMissingFilesMessage(Pair.UserData, missingOnServer, charaData.DataHash.Value));
            }

            try
            {
                Mediator.Publish(new HaltScanMessage(nameof(PlayerPerformanceService.ShrinkTextures)));
                if (await _playerPerformanceService.ShrinkTextures(this, charaData, downloadToken).ConfigureAwait(false))
                {
                    moddedPaths = _assetResolver
                        .Resolve(applicationBase, charaData, _assetResolver.ComputeCompressedAlternateUsage(), downloadToken)
                        .ModdedPaths;
                    LeasePendingFiles(pendingLeaseOwner, charaData, moddedPaths.Values);
                }
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

        var applicationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token;
        await _applicationStartGate.WaitAsync(downloadToken).ConfigureAwait(false);
        try
        {
            if (_applicationTask != null && !_applicationTask.IsCompleted)
            {
                Logger.LogDebug("[BASE-{appBase}] Cancelling current data application (Id: {id}) for {pair}", applicationBase, _applicationId, ToString());
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

            token = _applicationCancellationTokenSource.Token;
            _applicationTask = applicationDone.Task;
        }
        finally
        {
            _applicationStartGate.Release();
        }

        try
        {
            // Attente du draw hors du slot d'application : un pair coupé par les limites d'affichage, ou
            // parti en cours de route, ne doit pas bloquer les applications des autres pairs.
            var readyHandler = _charaHandler;
            var ready = readyHandler == null
                ? DalamudUtilService.ActorLoadWaitResult.AddressLost
                : await _dalamudUtil.WaitForFullyLoadedAsync(readyHandler, ActorReadyTimeout, token).ConfigureAwait(false);
            if (ready is DalamudUtilService.ActorLoadWaitResult.AddressLost or DalamudUtilService.ActorLoadWaitResult.NotDrawn)
            {
                Logger.LogDebug("[BASE-{appBase}] {pair} not ready for application ({state}), deferring", applicationBase, this, ready);
                _state.PendingModReapply = true;
                RecordFailure(ready == DalamudUtilService.ActorLoadWaitResult.NotDrawn
                    ? "Personnage non affiché (limites d'affichage ?)"
                    : "Personnage introuvable", "ActorNotReady");
                return;
            }

            if (ready == DalamudUtilService.ActorLoadWaitResult.TimedOut)
                Logger.LogDebug("[BASE-{appBase}] {pair} still loading after {timeout}s, applying anyway", applicationBase, this, ActorReadyTimeout.TotalSeconds);

#pragma warning disable MA0004 // ConfigureAwait on await using
            await using var applyLease = await _applicationSemaphoreService
                .AcquireAsync(token, highPriority: IsVisible, gpuHeavy: updateModdedPaths || updateManip)
                .ConfigureAwait(false);
#pragma warning restore MA0004

            await ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, redrawDecisions, reapplyGeneration, token).ConfigureAwait(false);
        }
        finally
        {
            applicationDone.TrySetResult();
        }

        if (appliedWithRetriableMissingFiles && !_state.PendingModReapply)
        {
            Logger.LogDebug("[BASE-{appBase}] Restoring pendingModReapply: applied with missing files", applicationBase);
            _state.PendingModReapply = true;
        }
    }

    private void LeasePendingFiles(object pendingLeaseOwner, CharacterData charaData, IEnumerable<string> resolvedPaths)
    {
        _cacheLeases.SetLeases(pendingLeaseOwner, charaData.FileReplacements.Values
            .SelectMany(v => v)
            .Select(f => f.Hash)
            .Concat(resolvedPaths));
    }

    private static readonly TimeSpan ActorReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InLeaseLoadTimeout = TimeSpan.FromSeconds(15);

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, IReadOnlyDictionary<ObjectKind, PairRedrawDecision>? redrawDecisions, int reapplyGeneration,
        CancellationToken token)
    {
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            if (_state.Penumbra.Collection == Guid.Empty)
            {
                var bound = await _collectionBinder
                    .EnsureBoundAsync(Logger, _state.Penumbra, Pair.UserData.UID, TryResolveObjectIndexAsync)
                    .ConfigureAwait(false);
                if (!bound.Success)
                {
                    AbortApplication(charaData, bound.Reason, bound.Failure.ToString());
                    return;
                }
            }

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token).ConfigureAwait(false);
            // L'acteur était prêt avant la prise du slot : cette attente ne couvre que le redraw de
            // l'assignation initiale, elle est donc courte et bornée.
            var loaded = await _dalamudUtil.WaitForFullyLoadedAsync(_charaHandler!, InLeaseLoadTimeout, token).ConfigureAwait(false);
            if (loaded == DalamudUtilService.ActorLoadWaitResult.AddressLost)
                throw new InvalidOperationException("Player pointer became zero while waiting for draw");

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths || updateManip)
            {
                if (_state.Penumbra.Collection == Guid.Empty)
                {
                    var created = await _collectionBinder
                        .EnsureBoundAsync(Logger, _state.Penumbra, Pair.UserData.UID, TryResolveObjectIndexAsync)
                        .ConfigureAwait(false);
                    if (!created.Success)
                    {
                        AbortApplication(charaData, created.Reason, created.Failure.ToString());
                        return;
                    }
                }

                // Les mods sont posés dans la collection AVANT une éventuelle (ré)assignation : le redraw que
                // Penumbra lance à l'assignation charge alors l'acteur avec les mods. La baseline est prise
                // entre les deux : seul un redraw postérieur à la pose des mods dispense du HardRedraw.
                var applied = await _collectionBinder.ApplyStateAsync(Logger, _applicationId, _state.Penumbra,
                    updateModdedPaths ? moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal) : null,
                    updateManip ? charaData.ManipulationData : null).ConfigureAwait(false);

                if (!applied.Success)
                {
                    AbortApplication(charaData, applied.Reason, applied.Failure.ToString());
                    return;
                }

                _redrawBaseline = _pairRedrawCoordinator.CaptureBaseline(_charaHandler!.Address);

                var ensured = await _collectionBinder
                    .EnsureBoundAsync(Logger, _state.Penumbra, Pair.UserData.UID, TryResolveObjectIndexAsync)
                    .ConfigureAwait(false);
                if (!ensured.Success)
                {
                    AbortApplication(charaData, ensured.Reason, ensured.Failure.ToString());
                    return;
                }

                if (updateModdedPaths)
                {
                    // Les fichiers posés dans la collection ne doivent pas être évincés du cache tant qu'ils sont appliqués
                    _cacheLeases.SetLeases(this, moddedPaths.Values);
                    LastAppliedDataBytes = -1;
                    foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                    {
                        if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                        LastAppliedDataBytes += path.Length;
                    }
                }
            }

            else
            {
                _redrawBaseline = _pairRedrawCoordinator.CaptureBaseline(_charaHandler!.Address);
            }

            token.ThrowIfCancellationRequested();

            _glamourerLockRefused = false;
            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, redrawDecisions, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _state.CachedData = charaData;
            _state.LastAppliedData = charaData;
            _state.Deferred = Guid.Empty;
            _state.ClearModReapply(reapplyGeneration);
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
            _state.LastSuccessfulApplyAt = DateTime.UtcNow;
            ClearFailureState();
            if (_glamourerLockRefused)
                RecordFailure("Apparence Glamourer verrouillée par un autre plugin", "GlamourerLocked");
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
            var handler = _charaHandler;
            if (handler == null || handler.Address == nint.Zero)
            {
                // Joueur disparu en cours d'application : il sera redétecté par VisibilityService.
                _visibilityService.RearmTracking(Pair.Ident);
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

    private void AbortApplication(CharacterData charaData, string reason, params string[] conditions)
    {
        Logger.LogWarning("[{applicationId}] Application interrompue pour {handler} : {reason}", _applicationId, this, reason);
        _state.PendingModReapply = true;
        RecordFailure(reason, conditions);
        _state.CachedData = charaData;
        Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
    }

    // Relance après une demande de ré-upload : la donnée doit être celle pour laquelle on a signalé
    private void OnRetryMissingFiles(RetryMissingFilesMessage msg)
    {
        if (!string.Equals(msg.OwnerUid, Pair.UserData.UID, StringComparison.Ordinal)) return;

        var data = _state.CachedData;
        if (data == null || !string.Equals(data.DataHash.Value, msg.DataHash, StringComparison.Ordinal)) return;
        if (!_assetResolver.HasMissingFiles(data)) return;

        Logger.LogDebug("Relance après demande de ré-upload pour {pair} ({count} fichier(s))", this, msg.Hashes.Count);
        _downloadManager.ForgetServerMissing(msg.Hashes);
        _state.PendingModReapply = true;
    }

    // Appelé sur le framework thread (DelayedFrameworkUpdateMessage)
    private void TryReapplyPendingData()
    {
        if ((!_state.PendingModReapply && _state.Deferred == Guid.Empty) || !IsVisible
            || PlayerCharacter == nint.Zero
            || IsApplyingOrDownloading
            || !CanApplyNow())
            return;

        var now = DateTime.UtcNow;
        if (_state.LastApplyAttemptAt.HasValue && now - _state.LastApplyAttemptAt.Value < TimeSpan.FromSeconds(5) + _reapplyJitter)
            return;

        var fallback = _state.CachedData;
        if (Pair.LastReceivedCharacterData == null && fallback == null)
            return;

        // Posé ici aussi : si la paire refuse l'application en amont, on ne retente pas à chaque tick.
        _state.LastApplyAttemptAt = now;
        Logger.LogDebug("Auto-retry: reapplying last received data for {handler} (pendingModReapply={pending}, deferred={deferred})",
            this, _state.PendingModReapply, _state.Deferred != Guid.Empty);
        _ = Task.Run(() =>
        {
            try
            {
                ReapplyLatestData(Guid.NewGuid(), fallback);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to reapply pending data for {handler}", this);
            }
        });
    }

    private bool CanApplyNow()
        => !_dalamudUtil.IsInGpose
           && !_dalamudUtil.IsInCutscene
           && !_dalamudUtil.IsZoning
           && !(_configService.Current.HoldCombatApplication && _dalamudUtil.IsInCombatOrPerforming)
           && _ipcManager.Penumbra.APIAvailable
           && _ipcManager.Glamourer.APIAvailable
           && !Pair.IsApplicationBlocked
           && !Pair.IsPaused;

    /// <summary>
    /// Relance depuis la dernière donnée reçue du serveur (filtrée par la paire), et non depuis
    /// CachedData qui peut être une donnée plus ancienne restée en plan.
    /// </summary>
    private void ReapplyLatestData(Guid applicationBase, CharacterData? fallback)
    {
        if (Pair.LastReceivedCharacterData != null)
            Pair.ApplyLastReceivedData(forced: true);
        else if (fallback != null)
            ApplyCharacterData(applicationBase, fallback, forceApplyCustomization: true);
    }

    private static readonly TimeSpan IpcReadyReapplyCooldown = TimeSpan.FromSeconds(10);
    private DateTime _lastIpcReadyReapplyUtc = DateTime.MinValue;

    // Glamourer, Customize+ ou Heels vient de (re)devenir disponible : ce qui a été reçu ou posé
    // pendant son absence est perdu ou en attente.
    private void OnAppearanceIpcReady(string plugin)
    {
        var applied = _state.LastAppliedData;
        bool deferred = _state.Deferred != Guid.Empty;
        bool concerned = deferred || (applied != null && plugin switch
        {
            "Glamourer" => applied.GlamourerData.Count > 0,
            "CustomizePlus" => applied.CustomizePlusData.Values.Any(v => !string.IsNullOrEmpty(v)),
            "Heels" => !string.IsNullOrEmpty(applied.HeelsData),
            _ => false,
        });
        if (!concerned || !IsVisible) return;

        var now = DateTime.UtcNow;
        if (now - _lastIpcReadyReapplyUtc < IpcReadyReapplyCooldown) return;
        _lastIpcReadyReapplyUtc = now;

        Logger.LogDebug("{plugin} available again, reapplying data for {handler}", plugin, this);
        _state.RequestModReapply();
        var fallback = _state.CachedData;
        int jitterMs = Random.Shared.Next(500, 3000);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(jitterMs).ConfigureAwait(false);
                if (IsVisible && !IsApplyingOrDownloading)
                    ReapplyLatestData(Guid.NewGuid(), fallback);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to reapply data after {plugin} became available for {user}", plugin, Pair.UserData.UID);
            }
        });
    }

    private static readonly TimeSpan OwnedObjectCheckInterval = TimeSpan.FromSeconds(2);
    private DateTime _nextOwnedObjectCheckUtc = DateTime.MinValue;

    // Appelé sur le framework thread (DelayedFrameworkUpdateMessage) : monture, familier ou compagnon
    // absent lors de l'application, on lui pose Glamourer et Customize+ dès qu'il apparaît.
    private void TryApplyPendingOwnedObjects()
    {
        lock (_state.PendingOwnedObjects)
        {
            if (_state.PendingOwnedObjects.Count == 0) return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextOwnedObjectCheckUtc) return;
        _nextOwnedObjectCheckUtc = now + OwnedObjectCheckInterval;

        var data = _state.LastAppliedData;
        var ptr = PlayerCharacter;
        if (data == null || ptr == nint.Zero)
        {
            if (data == null)
            {
                lock (_state.PendingOwnedObjects)
                    _state.PendingOwnedObjects.Clear();
            }
            return;
        }

        if (!IsVisible || IsApplyingOrDownloading || !CanApplyNow()) return;

        List<KeyValuePair<ObjectKind, HashSet<PlayerChanges>>> spawned = [];
        lock (_state.PendingOwnedObjects)
        {
            foreach (var kind in _state.PendingOwnedObjects.ToList())
            {
                var address = kind switch
                {
                    ObjectKind.MinionOrMount => _dalamudUtil.GetMinionOrMount(ptr),
                    ObjectKind.Pet => _dalamudUtil.GetPet(ptr),
                    ObjectKind.Companion => _dalamudUtil.GetCompanion(ptr),
                    _ => nint.Zero,
                };
                if (address == nint.Zero) continue;

                _state.PendingOwnedObjects.Remove(kind);
                HashSet<PlayerChanges> changes = [];
                if (data.GlamourerData.ContainsKey(kind)) changes.Add(PlayerChanges.Glamourer);
                if (data.CustomizePlusData.TryGetValue(kind, out var cplus) && !string.IsNullOrEmpty(cplus)) changes.Add(PlayerChanges.Customize);
                if (changes.Count > 0)
                    spawned.Add(new(kind, changes));
            }
        }

        if (spawned.Count == 0) return;

        CancellationToken token;
        try
        {
            token = _applicationCancellationTokenSource?.Token ?? CancellationToken.None;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var applicationId = Guid.NewGuid();
            try
            {
                foreach (var changes in spawned)
                {
                    Logger.LogDebug("[{applicationId}] {kind} spawned for {handler}, applying its appearance", applicationId, changes.Key, this);
                    await ApplyCustomizationDataAsync(applicationId, changes, data, null, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("[{applicationId}] Owned object application cancelled for {handler}", applicationId, this);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{applicationId}] Failed to apply owned object appearance for {user}", applicationId, Pair.UserData.UID);
            }
        }, CancellationToken.None);
    }
}
