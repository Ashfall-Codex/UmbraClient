using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;
using PenumbraApi = global::Penumbra.Api.Helpers;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;
namespace UmbraSync.Interop.Ipc.Penumbra;
public sealed class PenumbraResources : IDisposable
{
    private readonly PenumbraCore _core;
    private readonly PenumbraApi.EventSubscriber<nint, string, string> _penumbraGameObjectResourcePathResolved;
    private readonly PenumbraIpc.GetGameObjectResourcePaths _penumbraResourcePaths;
    private readonly PenumbraIpc.ResolveDefaultPath _penumbraResolveDefaultPath;

    public PenumbraResources(PenumbraCore core)
    {
        _core = core;

        // Initialiser les IPC de ressources
        _penumbraResourcePaths = new PenumbraIpc.GetGameObjectResourcePaths(_core.PluginInterface);
        _penumbraResolveDefaultPath = new PenumbraIpc.ResolveDefaultPath(_core.PluginInterface);
        _penumbraGameObjectResourcePathResolved = PenumbraIpc.GameObjectResourcePathResolved.Subscriber(_core.PluginInterface, ResourceLoaded);
    }
    
    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, GameObjectHandler handler)
    {
        if (!_core.APIAvailable) return null;

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("Calling On IPC: Penumbra.GetGameObjectResourcePaths");
            var idx = handler.GetGameObject()?.ObjectIndex;
            if (idx == null) return null;
            return _penumbraResourcePaths.Invoke(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?[]> GetObjectResourcePathsAsync(ILogger logger, ushort[] indices)
    {
        if (!_core.APIAvailable) return [];

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("Calling On IPC: Penumbra.GetGameObjectResourcePaths for {Count} objects", indices.Length);
            return _penumbraResourcePaths.Invoke(indices);
        }).ConfigureAwait(false);
    }
    
    public async Task<Dictionary<string, string>> ResolveDefaultCollectionPathsAsync(ILogger logger, string[] gamePaths)
    {
        if (!_core.APIAvailable) return new Dictionary<string, string>(StringComparer.Ordinal);

        return await _core.DalamudUtil.RunOnFrameworkThread(() =>
        {
            logger.LogTrace("Calling On IPC: Penumbra.ResolveDefaultPath pour {Count} chemins", gamePaths.Length);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var gamePath in gamePaths)
            {
                try
                {
                    var resolved = _penumbraResolveDefaultPath.Invoke(gamePath);
                    if (!string.IsNullOrEmpty(resolved) && !string.Equals(gamePath, resolved, StringComparison.OrdinalIgnoreCase))
                    {
                        result[gamePath] = resolved;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogTrace(ex, "Échec de la résolution du chemin default pour {Path}", gamePath);
                }
            }
            return result;
        }).ConfigureAwait(false);
    }

    private void ResourceLoaded(IntPtr ptr, string arg1, string arg2)
    {
        if (ptr != IntPtr.Zero && string.Compare(arg1, arg2, ignoreCase: true, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            _core.Mediator.Publish(new PenumbraResourceLoadMessage(ptr, arg1, arg2));
        }
    }

    public void Dispose()
    {
        _penumbraGameObjectResourcePathResolved.Dispose();
    }
}
