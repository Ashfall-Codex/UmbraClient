using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.Interop.Ipc.Penumbra;
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
                ToString(), forceApplyCustomization, forceApplyMods: false)
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

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _state.CachedData?.DeepClone() ?? new(), Logger, ToString(), forceApplyCustomization, _state.ForceApplyMods);

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
            ? characterData.ComputeRedrawDecisions(_state.CachedData, charaDataToUpdate)
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
        bool appliedWithRetriableMissingFiles = false;

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
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for {pair}, {kind}", applicationBase, ToString(), updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
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

        var token = _applicationCancellationTokenSource.Token;

#pragma warning disable MA0004 // ConfigureAwait on await using
        await using var applyLease = await _applicationSemaphoreService
            .AcquireAsync(token, highPriority: IsVisible, gpuHeavy: updateModdedPaths || updateManip)
            .ConfigureAwait(false);
#pragma warning restore MA0004

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, redrawDecisions, token);
        await _applicationTask.ConfigureAwait(false);
        if (appliedWithRetriableMissingFiles && !_state.PendingModReapply)
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
            if (_charaHandler!.Address != nint.Zero)
            {
                await _dalamudUtil.WaitForFullyLoadedAsync(_charaHandler!, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths || updateManip)
            {
                // L'attente ci-dessus peut durer jusqu'à 30 s
                var applied = await _collectionBinder.BindAndApplyAsync(Logger, _applicationId, _state.Penumbra,
                    Pair.UserData.UID, TryResolveObjectIndexAsync,
                    updateModdedPaths ? moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal) : null,
                    updateManip ? charaData.ManipulationData : null).ConfigureAwait(false);

                if (!applied.Success)
                {
                    AbortApplication(charaData, applied.Reason, applied.Failure.ToString());
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
}
