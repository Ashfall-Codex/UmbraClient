using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace UmbraSync.Services;

public sealed class ProfileImageStore(ILogger logger, string directory)
{
    private const int HashLength = 64;
    private const string FileExtension = ".img";
    private static readonly TimeSpan InFlightWriteWindow = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private volatile bool _closed;

    public ProfileImage Get(string? hash) => IsValidHash(hash) ? ProfileImage.FromStore(this, hash!) : ProfileImage.Empty;

    public string? Put(byte[] bytes)
    {
        if (_closed) return null;

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = PathFor(hash);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            lock (_gate)
            {
                var existing = new FileInfo(path);
                if (!existing.Exists || existing.Length != bytes.LongLength)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, path, overwrite: true);
                }
                else
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Impossible d'écrire l'image de fiche {hash}", hash);
            TryDelete(temp);
            return null;
        }
    }

    public byte[] ReadNow(string hash)
    {
        try
        {
            var path = PathFor(hash);
            var bytes = File.ReadAllBytes(path);
            if (string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.Ordinal)) return bytes;

            logger.LogDebug("Image de fiche {hash} corrompue, supprimée", hash);
            TryDelete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Image de fiche {hash} illisible", hash);
        }

        return Array.Empty<byte>();
    }

    public void RetainOnly(IReadOnlySet<string> hashes)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var referenced = name.EndsWith(FileExtension, StringComparison.Ordinal)
                    && hashes.Contains(name[..^FileExtension.Length]);
                lock (_gate)
                {
                    if (referenced || now - File.GetLastWriteTimeUtc(file) < InFlightWriteWindow) continue;

                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Nettoyage des images de fiche impossible");
        }
    }

    public void Close() => _closed = true;

    public void Clear()
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Suppression des images de fiche impossible");
        }
    }

    private string PathFor(string hash) => Path.Combine(directory, hash + FileExtension);

    private static bool IsValidHash(string? hash) => hash is { Length: HashLength } && hash.All(char.IsAsciiHexDigitUpper);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Suppression d'un fichier d'image de fiche impossible");
        }
    }
}
