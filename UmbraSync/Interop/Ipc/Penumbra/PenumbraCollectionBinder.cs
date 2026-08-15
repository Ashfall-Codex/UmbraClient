using Microsoft.Extensions.Logging;
using PenumbraEnum = global::Penumbra.Api.Enums;

namespace UmbraSync.Interop.Ipc.Penumbra;

public sealed class PenumbraCollectionBinding
{
    public Guid Collection { get; set; } = Guid.Empty;

    public int AssignedObjectIndex { get; set; } = -1;

    public void Reset()
    {
        Collection = Guid.Empty;
        AssignedObjectIndex = -1;
    }
}

public enum PenumbraBindFailure
{
    None,
    ObjectIndexUnavailable,
    CollectionRefused,
    AssignRefused,
    StateRefused,
}

public readonly record struct PenumbraBindResult(PenumbraBindFailure Failure, string Reason)
{
    public bool Success => Failure == PenumbraBindFailure.None;

    public static PenumbraBindResult Ok { get; } = new(PenumbraBindFailure.None, string.Empty);
}


public sealed class PenumbraCollectionBinder
{
    private readonly IpcManager _ipcManager;

    public PenumbraCollectionBinder(IpcManager ipcManager)
    {
        _ipcManager = ipcManager;
    }


    public async Task<PenumbraBindResult> EnsureBoundAsync(ILogger logger, PenumbraCollectionBinding binding,
        string collectionOwnerUid, Func<Task<ushort>> resolveObjectIndex)
    {
        var objIndex = await resolveObjectIndex().ConfigureAwait(false);
        if (objIndex == ushort.MaxValue)
            return new PenumbraBindResult(PenumbraBindFailure.ObjectIndexUnavailable, "Index d'objet introuvable");

        if (binding.Collection == Guid.Empty)
        {
            binding.Collection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, collectionOwnerUid).ConfigureAwait(false);
            binding.AssignedObjectIndex = -1;
            if (binding.Collection == Guid.Empty)
                return new PenumbraBindResult(PenumbraBindFailure.CollectionRefused, "Création de la collection temporaire refusée par Penumbra");
        }

        if (binding.AssignedObjectIndex == objIndex)
            return PenumbraBindResult.Ok;

        var assign = await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(logger, binding.Collection, objIndex).ConfigureAwait(false);
        if (assign != PenumbraEnum.PenumbraApiEc.Success)
        {
            binding.AssignedObjectIndex = -1;
            return new PenumbraBindResult(PenumbraBindFailure.AssignRefused, $"Assignation de la collection refusée par Penumbra ({assign})");
        }

        binding.AssignedObjectIndex = objIndex;
        return PenumbraBindResult.Ok;
    }


    public async Task<PenumbraBindResult> ApplyStateAsync(ILogger logger, Guid applicationId, PenumbraCollectionBinding binding,
        Dictionary<string, string>? modPaths, string? manipulationData)
    {
        var applied = await _ipcManager.Penumbra.ApplyTemporaryStateAsync(logger, applicationId, binding.Collection,
            modPaths, manipulationData).ConfigureAwait(false);

        return applied
            ? PenumbraBindResult.Ok
            : new PenumbraBindResult(PenumbraBindFailure.StateRefused, "Pose de l'état Penumbra refusée");
    }

    public async Task<PenumbraBindResult> BindAndApplyAsync(ILogger logger, Guid applicationId, PenumbraCollectionBinding binding,
        string collectionOwnerUid, Func<Task<ushort>> resolveObjectIndex,
        Dictionary<string, string>? modPaths, string? manipulationData)
    {
        var bound = await EnsureBoundAsync(logger, binding, collectionOwnerUid, resolveObjectIndex).ConfigureAwait(false);
        if (!bound.Success) return bound;

        return await ApplyStateAsync(logger, applicationId, binding, modPaths, manipulationData).ConfigureAwait(false);
    }

    public Task RemoveAsync(ILogger logger, Guid applicationId, PenumbraCollectionBinding binding)
    {
        var collection = binding.Collection;
        binding.Reset();
        return collection == Guid.Empty
            ? Task.CompletedTask
            : _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(logger, applicationId, collection);
    }
}
