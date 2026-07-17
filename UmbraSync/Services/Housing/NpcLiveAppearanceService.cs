using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
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

    private volatile CharacterData? _lastSelfData;

    public NpcLiveAppearanceService(ILogger<NpcLiveAppearanceService> logger, MareMediator mediator, IpcManager ipc,
        GameObjectHandlerFactory gameObjectHandlerFactory, FileCacheManager fileCacheManager,
        DalamudUtilService dalamudUtil, CharaDataFileHandler fileHandler)
        : base(logger, mediator)
    {
        // Snapshot live complet de notre perso : c'est CELUI envoyé aux pairs.
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, msg => _lastSelfData = msg.CharacterData);
        _ipc = ipc;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _fileCacheManager = fileCacheManager;
        _dalamudUtil = dalamudUtil;
        _fileHandler = fileHandler;
    }
    
    public Task<List<(Guid Id, string Name)>> GetDesignsAsync() => _ipc.Glamourer.GetDesignsAsync();
    
    public async Task<CharacterData?> CaptureDesignOnSelfAsync(Guid designId)
    {
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return null;
        int index = player.ObjectIndex;
        var playerAddr = player.Address;
        var savedState = await _ipc.Glamourer.GetCharacterCustomizationAsync(playerAddr).ConfigureAwait(false);
        var before = _lastSelfData;
        try
        {
            await _ipc.Glamourer.ApplyDesignToSelfAsync(designId, index).ConfigureAwait(false);
            const int maxWaitMs = 8000, stepMs = 150, settleMs = 700;
            CharacterData? captured = null;
            int waited = 0, sinceChange = 0;
            while (waited < maxWaitMs)
            {
                await Task.Delay(stepMs).ConfigureAwait(false);
                waited += stepMs;
                var current = _lastSelfData;
                if (current == null || ReferenceEquals(current, before)) continue;

                if (!ReferenceEquals(current, captured)) { captured = current; sinceChange = 0; }
                else if ((sinceChange += stepMs) >= settleMs) break;
            }

            if (captured == null)
                Logger.LogWarning("Capture depuis design : aucun recalcul détecté après {Timeout}ms, repli sur le dernier cache", maxWaitMs);
            return captured ?? _lastSelfData;
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
    
    public async Task<NpcLiveHandle?> ApplyAsync(nint address, CharacterData data, CancellationToken token)
    {
        if (!_ipc.Initialized || address == nint.Zero) return null;

        var handler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
        try
        {
            var appId = Guid.NewGuid();
            int idx = await _dalamudUtil.RunOnFrameworkThread(() => handler.GetGameObject()?.ObjectIndex ?? -1).ConfigureAwait(false);
            if (idx < 0) { handler.Dispose(); return null; }

            var modPaths = BuildModPaths(data);
            data.GlamourerData.TryGetValue(ObjectKind.Player, out var glamourer);
            data.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizePlus);

            // Un écart entre replacements et modPaths signale des fichiers de mods absents du cache
            // local (apparence incomplète) — c'est le symptôme qui avait révélé la capture amputée.
            int totalReplacements = data.FileReplacements.TryGetValue(ObjectKind.Player, out var reps) ? reps.Count : -1;
            if (totalReplacements != modPaths.Count)
            {
                Logger.LogWarning("Live PNJ : {Missing} fichier(s) de mod absents du cache local ({Reps} attendus, {Mods} résolus)",
                    totalReplacements - modPaths.Count, totalReplacements, modPaths.Count);
            }

            var collection = await _ipc.Penumbra.CreateTemporaryCollectionAsync(Logger, "UmbraNpc-" + appId.ToString("N")).ConfigureAwait(false);
            await _ipc.Penumbra.AssignTemporaryCollectionAsync(Logger, collection, idx).ConfigureAwait(false);
            await _ipc.Penumbra.SetTemporaryModsAsync(Logger, appId, collection, modPaths).ConfigureAwait(false);
            await _ipc.Penumbra.SetManipulationDataAsync(Logger, appId, collection, data.ManipulationData ?? string.Empty).ConfigureAwait(false);

            await _ipc.Glamourer.ApplyAllAsync(Logger, handler, glamourer, appId, token).ConfigureAwait(false);
            await _ipc.Penumbra.RedrawAsync(Logger, handler, appId, token).ConfigureAwait(false);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, appId, 30000, token).ConfigureAwait(false);

            Guid? customizeProfile = null;
            if (!string.IsNullOrEmpty(customizePlus))
                customizeProfile = await _ipc.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlus).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.HeelsData))
                await _ipc.Heels.SetOffsetForPlayerAsync(handler.Address, data.HeelsData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.MoodlesData))
                await _ipc.Moodles.SetStatusAsync(handler.Address, data.MoodlesData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.PetNamesData))
                await _ipc.PetNames.SetPlayerData(handler.Address, data.PetNamesData).ConfigureAwait(false);

            Logger.LogInformation("Live data appliqué au PNJ (index {Index}, {Mods} fichier(s) mod)", idx, modPaths.Count);
            return new NpcLiveHandle
            {
                ApplicationId = appId,
                Collection = collection,
                ObjectIndex = idx,
                CustomizeProfile = customizeProfile,
                Handler = handler,
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Application du live data au PNJ échouée");
            handler.Dispose();
            return null;
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
            handle.Handler.Dispose();
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
    public Guid ApplicationId { get; set; }
    public Guid Collection { get; set; }
    public int ObjectIndex { get; set; }
    public Guid? CustomizeProfile { get; set; }
    public GameObjectHandler Handler { get; set; } = null!;
}
