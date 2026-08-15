using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.Core.Abstractions;
using UmbraSync.MareConfiguration.Models;

namespace AshfallSync.Tests;

internal sealed class FakeCachedFile : ICachedFile
{
    public required string Hash { get; init; }
    public required string PrefixedFilePath { get; init; }
    public required string ResolvedFilepath { get; set; }
}

internal sealed class FakeFileCache : IFileCacheLookup
{
    private readonly Dictionary<string, FakeCachedFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingOnDisk = new(StringComparer.Ordinal);

    public int FlushCount { get; private set; }
    public List<string> Removed { get; } = [];

    public FakeFileCache With(string hash, string path = "", bool existsOnDisk = true)
    {
        var resolved = string.IsNullOrEmpty(path) ? $"/cache/{hash}.tex" : path;
        _files[hash] = new FakeCachedFile { Hash = hash, PrefixedFilePath = $"{{cache}}/{hash}", ResolvedFilepath = resolved };
        if (!existsOnDisk) _missingOnDisk.Add(hash);
        return this;
    }

    public ICachedFile? GetByHash(string hash, bool preferSubst = false)
        => _files.TryGetValue(hash, out var f) ? f : null;

    public ICachedFile MigrateToExtension(ICachedFile cachedFile, string extension)
    {
        var migrated = (FakeCachedFile)cachedFile;
        migrated.ResolvedFilepath = $"{migrated.ResolvedFilepath}.{extension}";
        return migrated;
    }

    public void Remove(string hash, string prefixedFilePath) => Removed.Add(hash);

    public void Flush() => FlushCount++;

    public bool Exists(ICachedFile cachedFile) => !_missingOnDisk.Contains(cachedFile.Hash);
}

internal sealed class FakeForbiddenTransfers : IForbiddenTransferRegistry
{
    private readonly HashSet<string> _forbidden;

    public FakeForbiddenTransfers(params string[] forbidden)
        => _forbidden = new HashSet<string>(forbidden, StringComparer.Ordinal);

    public bool IsForbidden(string hash) => _forbidden.Contains(hash);
}

internal sealed class FakeCompressionSettings : ITextureCompressionSettings
{
    public TextureCompressionMode Mode { get; init; } = TextureCompressionMode.AlwaysSourceQuality;
    public IReadOnlyList<string> UidsToOverride { get; init; } = [];
}

internal static class Build
{
    public static FileReplacementData Replacement(string hash, params string[] gamePaths)
        => new() { Hash = hash, GamePaths = gamePaths };

    public static FileReplacementData Swap(string swapPath, params string[] gamePaths)
        => new() { Hash = string.Empty, FileSwapPath = swapPath, GamePaths = gamePaths };

    public static CharacterData Data(params FileReplacementData[] playerReplacements)
    {
        var data = new CharacterData();
        if (playerReplacements.Length > 0)
            data.FileReplacements[ObjectKind.Player] = [.. playerReplacements];
        return data;
    }
}
