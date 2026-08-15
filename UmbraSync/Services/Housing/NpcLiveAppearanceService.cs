using System.Diagnostics;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.Interop.Ipc.Penumbra;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

public sealed class NpcLiveAppearanceService : DisposableMediatorSubscriberBase
{
    private readonly IpcManager _ipc;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly FileCacheManager _fileCacheManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly CharaDataFileHandler _fileHandler;
    private readonly NativeNpcSpawner _spawner;
    private readonly PenumbraCollectionBinder _collectionBinder;
    private const int GPosePlayerIndex = 200;

    private volatile CharacterData? _lastSelfData;

    public NpcLiveAppearanceService(ILogger<NpcLiveAppearanceService> logger, MareMediator mediator, IpcManager ipc,
        GameObjectHandlerFactory gameObjectHandlerFactory, FileCacheManager fileCacheManager,
        DalamudUtilService dalamudUtil, CharaDataFileHandler fileHandler, NativeNpcSpawner spawner)
        : base(logger, mediator)
    {
        _spawner = spawner;
        _collectionBinder = new PenumbraCollectionBinder(ipc);
        // Snapshot live complet de notre perso : c'est CELUI envoyé aux pairs.
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, msg => _lastSelfData = msg.CharacterData);
        _ipc = ipc;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _fileCacheManager = fileCacheManager;
        _dalamudUtil = dalamudUtil;
        _fileHandler = fileHandler;
    }
    
    public Task<List<(Guid Id, string Name)>> GetDesignsAsync() => _ipc.Glamourer.GetDesignsAsync();
    
    public async Task<(CharacterData? Data, NpcAppearance? Appearance)> CaptureDesignOnSelfAsync(Guid designId)
    {
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return (null, null);
        int index = player.ObjectIndex;
        var playerAddr = player.Address;
        var savedState = await _ipc.Glamourer.GetCharacterCustomizationAsync(playerAddr).ConfigureAwait(false);
        try
        {
            await _ipc.Glamourer.ApplyDesignToSelfAsync(designId, index).ConfigureAwait(false);
            await Task.Delay(600).ConfigureAwait(false);
            var beforeRebuild = _lastSelfData;
            Mediator.Publish(new ForcePlayerCacheRecreationMessage());

            const int maxWaitMs = 15000, stepMs = 150, settleMs = 700;
            CharacterData? captured = null;
            int waited = 0, sinceChange = 0;
            while (waited < maxWaitMs)
            {
                await Task.Delay(stepMs).ConfigureAwait(false);
                waited += stepMs;
                var current = _lastSelfData;
                if (current == null || ReferenceEquals(current, beforeRebuild)) continue;

                if (!ReferenceEquals(current, captured)) { captured = current; sinceChange = 0; }
                else if ((sinceChange += stepMs) >= settleMs) break;
            }

            if (captured == null)
                Logger.LogWarning("Capture depuis design : aucun recalcul détecté après {Timeout}ms, repli sur le dernier cache", maxWaitMs);

            var replacementCount = captured?.FileReplacements.TryGetValue(ObjectKind.Player, out var caps) == true ? caps.Count : 0;
            Logger.LogInformation("Capture depuis design : {Count} remplacement(s) de fichier retenus", replacementCount);

            var appearance = await _dalamudUtil.RunOnFrameworkThread(
                () => NativeNpcSpawner.ReadAppearance(playerAddr)).ConfigureAwait(false);
            Logger.LogDebug("Capture depuis design : casque masqué={Hat}, arme masquée={Weapon}, visière={Visor}",
                appearance.HideHeadgear, appearance.HideWeapon, appearance.VisorToggled);

            return (captured ?? _lastSelfData, appearance);
        }
        finally
        {
            if (!string.IsNullOrEmpty(savedState))
                await _ipc.Glamourer.ApplyStateToSelfAsync(savedState, index).ConfigureAwait(false);
        }
    }

    public async Task<CharacterData?> CaptureSelfAsync()
    {
        if (_lastSelfData != null)
        {
            Logger.LogInformation("Capture live : cache live utilisé ({Count} remplacement(s))",
                _lastSelfData.FileReplacements.TryGetValue(ObjectKind.Player, out var r) ? r.Count : 0);
            return _lastSelfData;
        }

        Logger.LogWarning("Capture live : cache live indisponible, repli sur l'export MCDF (apparence potentiellement incomplète)");
        return await _fileHandler.CreatePlayerData().ConfigureAwait(false);
    }
    
    public sealed record Preparation(NpcLiveHandle? Handle, bool SafeToDraw)
    {
        public static Preparation VanillaIsSafe { get; } = new(null, SafeToDraw: true);
        public static Preparation Unsafe { get; } = new(null, SafeToDraw: false);
    }

    public async Task<Preparation> PrepareCollectionAsync(nint address, CharacterData data)
    {
        if (!_ipc.Initialized || address == nint.Zero) return Preparation.VanillaIsSafe;

        int idx = await _dalamudUtil.RunOnFrameworkThread(() => _spawner.GetObjectTableIndex(address)).ConfigureAwait(false);
        if (idx < 0) return Preparation.VanillaIsSafe;
        if (idx == GPosePlayerIndex)
        {
            Logger.LogWarning("Live PNJ : acteur sur le slot {Index} (joueur GPose), l'apparence risque de ne pas s'appliquer", GPosePlayerIndex);
        }
        var handler = await _gameObjectHandlerFactory.Create(
            ObjectKind.Player, () => _spawner.ResolveIfAlive(idx, address), isWatched: false).ConfigureAwait(false);
        try
        {
            var appId = Guid.NewGuid();
            var modPaths = BuildModPaths(data);
            
            int totalReplacements = data.FileReplacements.TryGetValue(ObjectKind.Player, out var reps) ? reps.Count : 0;
            Logger.LogInformation("Live PNJ : {Reps} remplacement(s) reçus, {Mods} résolus depuis le cache local",
                totalReplacements, modPaths.Count);
            if (totalReplacements > modPaths.Count)
            {
                Logger.LogWarning("Live PNJ : {Missing} fichier(s) de mod absents du cache local ({Reps} attendus, {Mods} résolus)",
                    totalReplacements - modPaths.Count, totalReplacements, modPaths.Count);
            }

            // Même chemin que les pairs : création, assignation et pose de l'état passent par le
            // binder, qui vérifie chaque réponse de Penumbra. Ici l'acteur est encore en deferDraw,
            // donc la collection est bien en place avant qu'il ne charge la moindre ressource.
            var binding = new PenumbraCollectionBinding();
            var result = await _collectionBinder.BindAndApplyAsync(Logger, appId, binding,
                "UmbraNpc-" + appId.ToString("N"), () => Task.FromResult((ushort)idx),
                modPaths, data.ManipulationData ?? string.Empty).ConfigureAwait(false);

            if (!result.Success)
            {
                Logger.LogWarning("Live PNJ : {Reason} ({Failure}), abandon", result.Reason, result.Failure);
                await _collectionBinder.RemoveAsync(Logger, appId, binding).ConfigureAwait(false);
                handler.Dispose();
                return Preparation.Unsafe;
            }

            Logger.LogInformation("Live PNJ : collection {Collection} liée à l'index {Index}", binding.Collection, idx);

            if (!await WaitForCollectionReadyAsync(idx, binding.Collection).ConfigureAwait(false))
            {
                await _collectionBinder.RemoveAsync(Logger, appId, binding).ConfigureAwait(false);
                handler.Dispose();
                return Preparation.Unsafe;
            }

            return new Preparation(new NpcLiveHandle
            {
                ApplicationId = appId,
                Collection = binding.Collection,
                ObjectIndex = idx,
                Handler = handler,
            }, SafeToDraw: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Préparation de la collection PNJ échouée");
            handler.Dispose();
            return Preparation.Unsafe;
        }
    }
    
    private async Task<bool> WaitForCollectionReadyAsync(int objectIndex, Guid expected)
    {
        const int timeoutMs = 15000;
        const int stepMs = 25;
        const int settleMs = 150;
        const int slowWarnMs = 1500;

        var sw = Stopwatch.StartNew();
        bool warned = false;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var effective = await _ipc.Penumbra.GetCollectionForObjectAsync(objectIndex).ConfigureAwait(false);
            if (effective.CollectionId == expected)
            {
                Logger.LogDebug("Live PNJ : collection {Collection} effective sur l'index {Index} après {Elapsed} ms",
                    expected, objectIndex, sw.ElapsedMilliseconds);
                await Task.Delay(settleMs).ConfigureAwait(false);
                return true;
            }
            
            if (!warned && sw.ElapsedMilliseconds >= slowWarnMs)
            {
                warned = true;
                Logger.LogInformation("Live PNJ : Penumbra met plus de {Slow} ms à activer la collection sur l'index {Index}, attente prolongée",
                    slowWarnMs, objectIndex);
            }

            await Task.Delay(stepMs).ConfigureAwait(false);
        }

        Logger.LogWarning("Live PNJ : collection {Collection} toujours pas effective sur l'index {Index} après {Timeout} ms, spawn abandonné "
            + "(dessiner ici afficherait l'apparence d'un autre personnage)", expected, objectIndex, timeoutMs);
        return false;
    }

    /// <summary>
    /// Deuxième temps, une fois l'acteur dessiné : état Glamourer, redraw et plugins d'apparence.
    /// </summary>
    public async Task FinalizeAsync(NpcLiveHandle handle, CharacterData data, CancellationToken token)
    {
        try
        {
            data.GlamourerData.TryGetValue(ObjectKind.Player, out var glamourer);
            data.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizePlus);

            // Relevé de l'état avant application : sert de témoin pour savoir si Glamourer a
            // réellement touché CET acteur (cf. ApplyGlamourerWithRetryAsync).
            var stateBefore = string.IsNullOrEmpty(glamourer)
                ? string.Empty
                : await _ipc.Glamourer.GetCharacterCustomizationAsync(handle.Handler.Address).ConfigureAwait(false);

            await ApplyGlamourerAsync(handle, glamourer, token).ConfigureAwait(false);
            if (handle.Handler.Address == nint.Zero)
            {
                Logger.LogWarning("Live PNJ : acteur de l'index {Index} disparu pendant le redraw, finalisation abandonnée", handle.ObjectIndex);
                return;
            }

            await VerifyGlamourerAppliedAsync(handle, glamourer, stateBefore, token).ConfigureAwait(false);
            if (handle.Handler.Address == nint.Zero) return;

            if (!string.IsNullOrEmpty(customizePlus))
                handle.CustomizeProfile = await _ipc.CustomizePlus.SetBodyScaleAsync(handle.Handler.Address, customizePlus).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.HeelsData))
                await _ipc.Heels.SetOffsetForPlayerAsync(handle.Handler.Address, data.HeelsData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.MoodlesData))
                await _ipc.Moodles.SetStatusAsync(handle.Handler.Address, data.MoodlesData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.PetNamesData))
                await _ipc.PetNames.SetPlayerData(handle.Handler.Address, data.PetNamesData).ConfigureAwait(false);

            var effective = await _ipc.Penumbra.GetCollectionForObjectAsync(handle.ObjectIndex).ConfigureAwait(false);
            Logger.LogInformation("Live PNJ : index {Index} → ObjectValid={Valid}, collection effective {EffId}, attendue {Expected}",
                handle.ObjectIndex, effective.ObjectValid, effective.CollectionId, handle.Collection);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Finalisation du live data PNJ échouée");
        }
    }

    private async Task ApplyGlamourerAsync(NpcLiveHandle handle, string? glamourer, CancellationToken token)
    {
        await _ipc.Glamourer.ApplyAllAsync(Logger, handle.Handler, glamourer, handle.ApplicationId, token).ConfigureAwait(false);
        await _ipc.Penumbra.RedrawAsync(Logger, handle.Handler, handle.ApplicationId, token).ConfigureAwait(false);
        await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handle.Handler, handle.ApplicationId, 30000, token).ConfigureAwait(false);
    }
    
    private async Task VerifyGlamourerAppliedAsync(NpcLiveHandle handle, string? glamourer, string stateBefore, CancellationToken token)
    {
        if (string.IsNullOrEmpty(glamourer) || string.IsNullOrEmpty(stateBefore)) return;

        var stateAfter = await _ipc.Glamourer.GetCharacterCustomizationAsync(handle.Handler.Address).ConfigureAwait(false);
        if (string.IsNullOrEmpty(stateAfter) || !string.Equals(stateAfter, stateBefore, StringComparison.Ordinal)) return;

        Logger.LogWarning("Live PNJ : l'état Glamourer de l'index {Index} n'a pas bougé après application, nouvelle tentative", handle.ObjectIndex);
        await ApplyGlamourerAsync(handle, glamourer, token).ConfigureAwait(false);
        if (handle.Handler.Address == nint.Zero)
        {
            Logger.LogWarning("Live PNJ : acteur de l'index {Index} disparu pendant la seconde tentative", handle.ObjectIndex);
            return;
        }

        var stateRetried = await _ipc.Glamourer.GetCharacterCustomizationAsync(handle.Handler.Address).ConfigureAwait(false);
        if (string.Equals(stateRetried, stateBefore, StringComparison.Ordinal))
        {
            Logger.LogWarning("Live PNJ : l'index {Index} refuse toujours l'apparence Glamourer, il restera en apparence brute", handle.ObjectIndex);
        }
        else
        {
            Logger.LogInformation("Live PNJ : apparence Glamourer appliquée à l'index {Index} à la seconde tentative", handle.ObjectIndex);
        }
    }

    public async Task RevertAsync(NpcLiveHandle handle)
    {
        try
        {
            if (handle.CustomizeProfile != null)
                await _ipc.CustomizePlus.RevertByIdAsync(handle.CustomizeProfile).ConfigureAwait(false);
            await _ipc.Penumbra.RemoveTemporaryCollectionAsync(Logger, handle.ApplicationId, handle.Collection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Nettoyage du live data PNJ échoué");
        }
        finally
        {
            handle.DisposeHandler();
        }
    }

    private Dictionary<string, string> BuildModPaths(CharacterData data)
    {
        var modPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!data.FileReplacements.TryGetValue(ObjectKind.Player, out var replacements)) return modPaths;

        foreach (var rep in replacements)
        {
            if (!string.IsNullOrEmpty(rep.FileSwapPath))
            {
                foreach (var gamePath in rep.GamePaths) modPaths[gamePath] = rep.FileSwapPath;
                continue;
            }
            var cacheFile = _fileCacheManager.GetFileCacheByHash(rep.Hash);
            if (cacheFile == null) continue; // fichier absent du cache local (rare pour soi-même)
            foreach (var gamePath in rep.GamePaths) modPaths[gamePath] = cacheFile.ResolvedFilepath;
        }
        return modPaths;
    }
}

public sealed class NpcLiveHandle
{
    private int _handlerDisposed;

    public Guid ApplicationId { get; set; }
    public Guid Collection { get; set; }
    public int ObjectIndex { get; set; }
    public Guid? CustomizeProfile { get; set; }
    public GameObjectHandler Handler { get; set; } = null!;
    public void DisposeHandler()
    {
        if (Interlocked.Exchange(ref _handlerDisposed, 1) != 0) return;
        Handler.Dispose();
    }
}
