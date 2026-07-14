using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.CharaData;

namespace UmbraSync.Services.Housing;

public sealed class NpcLiveAppearanceService
{
    private readonly ILogger<NpcLiveAppearanceService> _logger;
    private readonly IpcManager _ipc;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly FileCacheManager _fileCacheManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly CharaDataFileHandler _fileHandler;

    public NpcLiveAppearanceService(ILogger<NpcLiveAppearanceService> logger, IpcManager ipc,
        GameObjectHandlerFactory gameObjectHandlerFactory, FileCacheManager fileCacheManager,
        DalamudUtilService dalamudUtil, CharaDataFileHandler fileHandler)
    {
        _logger = logger;
        _ipc = ipc;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _fileCacheManager = fileCacheManager;
        _dalamudUtil = dalamudUtil;
        _fileHandler = fileHandler;
    }

    public Task<CharacterData?> CaptureSelfAsync() => _fileHandler.CreatePlayerData();
    
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

            var collection = await _ipc.Penumbra.CreateTemporaryCollectionAsync(_logger, "UmbraNpc-" + appId.ToString("N")).ConfigureAwait(false);
            await _ipc.Penumbra.AssignTemporaryCollectionAsync(_logger, collection, idx).ConfigureAwait(false);
            await _ipc.Penumbra.SetTemporaryModsAsync(_logger, appId, collection, modPaths).ConfigureAwait(false);
            await _ipc.Penumbra.SetManipulationDataAsync(_logger, appId, collection, data.ManipulationData ?? string.Empty).ConfigureAwait(false);

            await _ipc.Glamourer.ApplyAllAsync(_logger, handler, glamourer, appId, token).ConfigureAwait(false);
            await _ipc.Penumbra.RedrawAsync(_logger, handler, appId, token).ConfigureAwait(false);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(_logger, handler, appId, 30000, token).ConfigureAwait(false);

            Guid? customizeProfile = null;
            if (!string.IsNullOrEmpty(customizePlus))
                customizeProfile = await _ipc.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlus).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.HeelsData))
                await _ipc.Heels.SetOffsetForPlayerAsync(handler.Address, data.HeelsData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.MoodlesData))
                await _ipc.Moodles.SetStatusAsync(handler.Address, data.MoodlesData).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(data.PetNamesData))
                await _ipc.PetNames.SetPlayerData(handler.Address, data.PetNamesData).ConfigureAwait(false);

            _logger.LogInformation("Live data appliqué au PNJ (index {Index}, {Mods} fichier(s) mod)", idx, modPaths.Count);
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
            _logger.LogWarning(ex, "Application du live data au PNJ échouée");
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
            await _ipc.Penumbra.RemoveTemporaryCollectionAsync(_logger, handle.ApplicationId, handle.Collection).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nettoyage du live data PNJ échoué");
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
