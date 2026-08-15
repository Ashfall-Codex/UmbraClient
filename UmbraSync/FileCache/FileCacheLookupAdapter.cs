using UmbraSync.Core.Abstractions;
using UmbraSync.WebAPI.Files;

namespace UmbraSync.FileCache;

public sealed class FileCacheLookupAdapter : IFileCacheLookup
{
    private readonly FileCacheManager _fileDbManager;

    public FileCacheLookupAdapter(FileCacheManager fileDbManager)
    {
        _fileDbManager = fileDbManager;
    }

    public ICachedFile? GetByHash(string hash, bool preferSubst = false)
        => _fileDbManager.GetFileCacheByHash(hash, preferSubst);

    public ICachedFile MigrateToExtension(ICachedFile cachedFile, string extension)
        => cachedFile is FileCacheEntity entity
            ? _fileDbManager.MigrateFileHashToExtension(entity, extension)
            : cachedFile;

    public void Remove(string hash, string prefixedFilePath)
        => _fileDbManager.RemoveHashedFile(hash, prefixedFilePath);

    public void Flush() => _fileDbManager.WriteOutFullCsv();

    public bool Exists(ICachedFile cachedFile) => File.Exists(cachedFile.ResolvedFilepath);
}

/// <summary>Expose la liste des transferts interdits de l'orchestrateur sous forme de test unitaire.</summary>
public sealed class ForbiddenTransferRegistryAdapter : IForbiddenTransferRegistry
{
    private readonly FileDownloadManager _downloadManager;

    public ForbiddenTransferRegistryAdapter(FileDownloadManager downloadManager)
    {
        _downloadManager = downloadManager;
    }

    public bool IsForbidden(string hash)
        => _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, hash, StringComparison.Ordinal));
}
