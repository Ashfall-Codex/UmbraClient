namespace UmbraSync.Utils;
// Support Blake3
public enum FileHashAlgorithm
{
    Sha1,
    Blake3,
}

public static class FileHash
{
    public const int Sha1Length = 40;
    public const int Blake3Length = 64;

    public static bool TryDetect(ReadOnlySpan<char> hash, out FileHashAlgorithm algorithm)
    {
        algorithm = hash.Length == Blake3Length ? FileHashAlgorithm.Blake3 : FileHashAlgorithm.Sha1;
        return (hash.Length == Sha1Length || hash.Length == Blake3Length) && IsHex(hash);
    }
    
    public static bool IsAccepted(ReadOnlySpan<char> hash)
        => TryDetect(hash, out var algorithm) && algorithm == FileHashAlgorithm.Sha1;

    public static bool IsBlake3(ReadOnlySpan<char> hash)
        => TryDetect(hash, out var algorithm) && algorithm == FileHashAlgorithm.Blake3;

    // Nom de fichier du cache : le hash, suivi éventuellement d'une ou plusieurs extensions.
    public static bool IsAcceptedFileName(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.IndexOf('.');
        return IsAccepted(dot < 0 ? fileName : fileName[..dot]);
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }
}
