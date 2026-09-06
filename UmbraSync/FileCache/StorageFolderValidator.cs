using System.Text.RegularExpressions;

namespace UmbraSync.FileCache;

public enum StorageFolderIssue
{
    None,
    Missing,
    NotWritable,
    PenumbraDirectory,
    OneDrive,
    IllegalCharacters,
    ForeignContent,
    SameAsCurrent,
    Nested,
    NotEnoughSpace,
}

// Règles communes de validation d'un dossier de stockage. Centralisées ici parce que le

public static partial class StorageFolderValidator
{
    public static readonly string[] TemporaryExtensions = [".tmp", ".lz4tmp", ".cdntmp", ".blk"];

    public static StorageFolderIssue Validate(string? path, string? penumbraModDirectory)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return StorageFolderIssue.Missing;

        if (path.Contains("onedrive", StringComparison.OrdinalIgnoreCase))
            return StorageFolderIssue.OneDrive;

        if (!string.IsNullOrEmpty(penumbraModDirectory)
            && string.Equals(Normalize(path), Normalize(penumbraModDirectory), StringComparison.OrdinalIgnoreCase))
            return StorageFolderIssue.PenumbraDirectory;

        if (!IsPathReadableByGame(path))
            return StorageFolderIssue.IllegalCharacters;

        if (!IsDirectoryWritable(path))
            return StorageFolderIssue.NotWritable;

        if (HasForeignContent(path))
            return StorageFolderIssue.ForeignContent;

        return StorageFolderIssue.None;
    }

    /// <summary>
    /// Le jeu ne sait lire qu'un sous-ensemble de caractères dans les chemins de ressources :
    /// un accent ou un point dans le dossier de stockage et les fichiers ne se chargent plus.
    /// </summary>
    public static bool IsPathReadableByGame(string? path)
    {
        return !string.IsNullOrEmpty(path) && PathRegex().IsMatch(path);
    }

    public static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            using FileStream fs = File.Create(Path.Combine(dirPath, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public static bool HasForeignContent(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsStorageFile(file)) return true;
            }

            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
                if (!string.Equals(name, FileCacheManager.SubstPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsStorageFile(file)) return true;
                }

                if (Directory.EnumerateDirectories(dir).Any()) return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    public static bool IsStorageFile(string filePath)
    {
        if (IsTemporaryFile(filePath)) return true;
        return Path.GetFileNameWithoutExtension(filePath).Length == 40;
    }

    public static bool IsTemporaryFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return TemporaryExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    public static bool IsSameOrNested(string root, string candidate)
    {
        string normalizedRoot = Normalize(root);
        string normalizedCandidate = Normalize(candidate);

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameVolume(string first, string second)
    {
        try
        {
            string? firstRoot = Path.GetPathRoot(Path.GetFullPath(first));
            string? secondRoot = Path.GetPathRoot(Path.GetFullPath(second));
            return !string.IsNullOrEmpty(firstRoot)
                && string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();
}
