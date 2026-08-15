namespace UmbraSync.Core.Abstractions;

public interface ICachedFile
{
    string Hash { get; }
    string PrefixedFilePath { get; }
    string ResolvedFilepath { get; }
}

public interface IFileCacheLookup
{
    ICachedFile? GetByHash(string hash, bool preferSubst = false);
    ICachedFile MigrateToExtension(ICachedFile cachedFile, string extension);

    void Remove(string hash, string prefixedFilePath);

    void Flush();

    bool Exists(ICachedFile cachedFile);
}
