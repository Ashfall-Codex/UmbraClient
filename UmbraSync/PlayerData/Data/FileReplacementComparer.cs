namespace UmbraSync.PlayerData.Data;

public class FileReplacementComparer : IEqualityComparer<FileReplacement>
{
    private static readonly FileReplacementComparer _instance = new();

    private FileReplacementComparer()
    { }

    public static FileReplacementComparer Instance => _instance;

    public bool Equals(FileReplacement? x, FileReplacement? y)
    {
        if (x == null || y == null) return false;
        return x.ResolvedPath.Equals(y.ResolvedPath) && UnorderedSetComparison.SetsEqual(x.GamePaths, y.GamePaths);
    }

    public int GetHashCode(FileReplacement obj)
    {
        return HashCode.Combine(obj.ResolvedPath.GetHashCode(StringComparison.OrdinalIgnoreCase), UnorderedSetComparison.OrderIndependentHashCode(obj.GamePaths));
    }
}