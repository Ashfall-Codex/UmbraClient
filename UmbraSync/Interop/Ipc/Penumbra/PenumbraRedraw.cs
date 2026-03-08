using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;
using PenumbraApi = global::Penumbra.Api.Helpers;
using PenumbraEnum = global::Penumbra.Api.Enums;
using PenumbraIpc = global::Penumbra.Api.IpcSubscribers;

namespace UmbraSync.Interop.Ipc.Penumbra;

public sealed class PenumbraRedraw : IDisposable
{
    private readonly PenumbraCore _core;
    private readonly ConcurrentDictionary<IntPtr, bool> _penumbraRedrawRequests = new();
    private readonly PenumbraApi.EventSubscriber<nint, int> _penumbraObjectIsRedrawn;
    private readonly PenumbraIpc.RedrawObject _penumbraRedraw;
    private readonly PenumbraIpc.RedrawAll _penumbraRedrawAll;

    public PenumbraRedraw(PenumbraCore core)
    {
        _core = core;

        // Initialiser les API de redraw
        _penumbraRedraw = new PenumbraIpc.RedrawObject(_core.PluginInterface);
        _penumbraRedrawAll = new PenumbraIpc.RedrawAll(_core.PluginInterface);
        _penumbraObjectIsRedrawn = PenumbraIpc.GameObjectRedrawn.Subscriber(_core.PluginInterface, RedrawEvent);
    }
    
    public void RedrawCharacter(ICharacter character)
    {
        if (!_core.APIAvailable) return;
        _penumbraRedraw.Invoke(character.ObjectIndex, PenumbraEnum.RedrawType.AfterGPose);
    }


    public async Task RedrawAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!_core.APIAvailable || _core.DalamudUtil.IsZoning) return;

        var semaphoreAcquired = false;
        try
        {
            await _core.RedrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            semaphoreAcquired = true;

            await _core.RedrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                logger.LogDebug("[{appid}] Calling on IPC: PenumbraRedraw", applicationId);
                _penumbraRedraw.Invoke(chara.ObjectIndex, setting: PenumbraEnum.RedrawType.Redraw);

            }, token).ConfigureAwait(false);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                _core.RedrawManager.RedrawSemaphore.Release();
            }
        }
    }
    
    public void RedrawNow(ILogger logger, Guid applicationId, int objectIndex)
    {
        if (!_core.APIAvailable || _core.DalamudUtil.IsZoning) return;
        logger.LogTrace("[{applicationId}] Immediately redrawing object index {objId}", applicationId, objectIndex);
        _penumbraRedraw.Invoke(objectIndex);
    }
    
    public void RedrawAll()
    {
        if (!_core.APIAvailable) return;
        _penumbraRedrawAll.Invoke(PenumbraEnum.RedrawType.Redraw);
    }
    
    private void RedrawEvent(IntPtr objectAddress, int objectTableIndex)
    {
        bool wasRequested = false;
        if (_penumbraRedrawRequests.TryGetValue(objectAddress, out var redrawRequest) && redrawRequest)
        {
            _penumbraRedrawRequests[objectAddress] = false;
        }
        else
        {
            _core.Mediator.Publish(new PenumbraRedrawMessage(objectAddress, objectTableIndex, wasRequested));
        }
    }
    public void CancelRedraws()
    {
        _core.RedrawManager.Cancel();
    }

    public void Dispose()
    {
        _penumbraObjectIsRedrawn.Dispose();
    }
}
