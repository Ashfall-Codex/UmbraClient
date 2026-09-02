using UmbraSync.API.Data;

namespace UmbraSync.PlayerData.Data;

public class FileReplacementDataComparer : IEqualityComparer<FileReplacementData>
{
    private static readonly FileReplacementDataComparer _instance = new();

    private FileReplacementDataComparer()
    { }

    public static FileReplacementDataComparer Instance => _instance;

    public bool Equals(FileReplacementData? x, FileReplacementData? y)
    {
        if (x == null || y == null) return false;
        return x.Hash.Equals(y.Hash) && UnorderedSetComparison.SetsEqual(x.GamePaths.ToHashSet(StringComparer.Ordinal), y.GamePaths.ToHashSet(StringComparer.Ordinal)) && string.Equals(x.FileSwapPath, y.FileSwapPath, StringComparison.Ordinal);
    }

    public int GetHashCode(FileReplacementData obj)
    {
        return HashCode.Combine(obj.Hash.GetHashCode(StringComparison.OrdinalIgnoreCase), UnorderedSetComparison.OrderIndependentHashCode(obj.GamePaths), StringComparer.Ordinal.GetHashCode(obj.FileSwapPath));
    }
}