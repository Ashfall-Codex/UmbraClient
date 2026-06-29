using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace UmbraSync.Utils;

internal static unsafe class NativeGameObjectUtils
{
    public static bool IsValidObjectTableEntry(GameObject* gameObject, int objectIndex)
        => gameObject != null
            && objectIndex >= 0
            && objectIndex <= ushort.MaxValue
            && gameObject->ObjectIndex == objectIndex;

    private static bool TryGetGameObjectByIndex(ushort objectIndex, nint expectedAddress, out GameObject* gameObject)
    {
        gameObject = null;
        var manager = GameObjectManager.Instance();
        if (manager == null)
            return false;

        var objects = manager->Objects.IndexSorted;
        if (objectIndex >= objects.Length)
            return false;

        var candidate = objects[objectIndex].Value;
        if (!IsValidObjectTableEntry(candidate, objectIndex))
            return false;

        if (expectedAddress != nint.Zero && (nint)candidate != expectedAddress)
            return false;

        gameObject = candidate;
        return true;
    }

    /// <summary>
    /// Retrouve un GameObject* vivant à partir de son adresse, en validant via l'object table
    /// (anti use-after-free). Tente d'abord l'index préféré, puis un scan complet.
    /// </summary>
    public static bool TryFindGameObjectByAddress(nint address, ushort preferredObjectIndex, out GameObject* gameObject)
    {
        gameObject = null;
        if (address == nint.Zero)
            return false;

        var manager = GameObjectManager.Instance();
        if (manager == null)
            return false;

        var objects = manager->Objects.IndexSorted;
        if (TryGetGameObjectByIndex(preferredObjectIndex, address, out var preferred))
        {
            gameObject = preferred;
            return true;
        }

        for (var i = 0; i < objects.Length; i++)
        {
            var candidate = objects[i].Value;
            if (!IsValidObjectTableEntry(candidate, i) || (nint)candidate != address)
                continue;

            gameObject = candidate;
            return true;
        }

        return false;
    }
}
