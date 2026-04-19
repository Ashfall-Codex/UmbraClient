using Microsoft.Extensions.Logging;
using System.Text;
using UmbraSync.API.Data;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Events;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;
using UmbraSync.WebAPI.Files.Models;

namespace UmbraSync.Services;

public class PlayerPerformanceService : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly ILogger<PlayerPerformanceService> _logger;
    private readonly MareMediator _mediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly NotificationTracker _notificationTracker;
    private readonly PairPerformanceMetricsCache _metricsCache;

    public PlayerPerformanceService(ILogger<PlayerPerformanceService> logger, MareMediator mediator,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService, FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer, NotificationTracker notificationTracker,
        PairPerformanceMetricsCache metricsCache)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
        _notificationTracker = notificationTracker;
        _metricsCache = metricsCache;
    }

    public async Task<bool> CheckBothThresholds(PairHandler pairHandler, CharacterData charaData)
    {
        var pair = pairHandler.Pair;
        var ident = pair.Ident;
        var dataHash = charaData.DataHash.Value;

        // Tente de récupérer les métriques du cache
        if (!string.IsNullOrEmpty(ident) && !string.IsNullOrEmpty(dataHash) &&
            _metricsCache.TryGetMetrics(ident, dataHash, out var cachedMetrics))
        {
            _logger.LogDebug("Métriques récupérées du cache pour {ident} (hash: {hash})", ident, dataHash);
            pair.LastAppliedApproximateVRAMBytes = cachedMetrics.ApproximateVramBytes;
            pair.LastAppliedDataTris = cachedMetrics.TriangleCount;
            return true;
        }

        bool notPausedAfterVram = ComputeAndAutoPauseOnVRAMUsageThresholds(pairHandler, charaData, []);
        if (!notPausedAfterVram) return false;
        bool notPausedAfterTris = await CheckTriangleUsageThresholds(pairHandler, charaData).ConfigureAwait(false);
        if (!notPausedAfterTris) return false;

        // Stocke les métriques dans le cache
        if (!string.IsNullOrEmpty(ident) && !string.IsNullOrEmpty(dataHash))
        {
            var metrics = new PairPerformanceMetrics(pair.LastAppliedDataTris, pair.LastAppliedApproximateVRAMBytes);
            _metricsCache.StoreMetrics(ident, dataHash, metrics);
            _logger.LogDebug("Métriques stockées dans le cache pour {ident} (hash: {hash})", ident, dataHash);
        }

        return true;
    }


    // Tente de récupérer les métriques depuis le cache sans recalculer.
    // Utile pour le PairLedger quand il vérifie les paires visibles.
    public bool TryGetCachedMetrics(string ident, string dataHash, out PairPerformanceMetrics metrics)
    {
        return _metricsCache.TryGetMetrics(ident, dataHash, out metrics);
    }

    public async Task<bool> CheckTriangleUsageThresholds(PairHandler pairHandler, CharacterData charaData)
    {
        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        long triUsage = 0;

        var moddedModelHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith("mdl", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hash in moddedModelHashes)
        {
            triUsage += await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(hash)).ConfigureAwait(false);
        }

        pair.LastAppliedDataTris = triUsage;

        _logger.LogDebug("Calculated Triangle usage for {p}", pairHandler);

        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;

        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (!autoPause || _serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
            return true;

        long triUsageThreshold = (long)config.TrisAutoPauseThresholdThousands * 1000;

        if (triUsage > triUsageThreshold)
        {
            if (notify && !pair.IsApplicationBlocked)
            {
                _mediator.Publish(new NotificationMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID}) automatically blocked",
                    $"Player {pair.PlayerName} ({pair.UserData.AliasOrUID}) exceeded your configured triangle auto block threshold (" +
                    $"{triUsage}/{triUsageThreshold} triangles)" +
                    $" and has been automatically blocked.",
                    MareConfiguration.Models.NotificationType.Warning));
                _notificationTracker.Upsert(NotificationEntry.PlayerAutoBlockedTriangles(
                    pair.UserData.UID, pair.UserData.AliasOrUID, triUsage, triUsageThreshold));
            }

            _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                $"Exceeds triangle threshold: ({triUsage}/{triUsageThreshold} triangles)")));

            return false;
        }

        return true;
    }

    public bool ComputeAndAutoPauseOnVRAMUsageThresholds(PairHandler pairHandler, CharacterData charaData, List<DownloadFileTransfer> toDownloadFiles, bool affect = false)
    {
        var config = _playerPerformanceConfigService.Current;
        var pair = pairHandler.Pair;

        long vramUsage = 0;

        var moddedTextureHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var hash in moddedTextureHashes)
        {
            long fileSize;

            var download = toDownloadFiles.Find(f => string.Equals(hash, f.Hash, StringComparison.OrdinalIgnoreCase));
            if (download != null)
            {
                fileSize = download.TotalRaw;
            }
            else
            {
                var fileEntry = _fileCacheManager.GetFileCacheByHash(hash, preferSubst: true);
                if (fileEntry == null) continue;

                if (fileEntry.Size == null)
                {
                    fileEntry.Size = new FileInfo(fileEntry.ResolvedFilepath).Length;
                    _fileCacheManager.UpdateHashedFile(fileEntry, computeProperties: true);
                }

                fileSize = fileEntry.Size.Value;
            }

            vramUsage += fileSize;
        }

        pair.LastAppliedApproximateVRAMBytes = vramUsage;

        _logger.LogDebug("Calculated VRAM usage for {p}", pairHandler);

        bool isDirect = pair.UserPair != null;
        bool autoPause = config.AutoPausePlayersExceedingThresholds;
        bool notify = isDirect ? config.NotifyAutoPauseDirectPairs : config.NotifyAutoPauseGroupPairs;

        if (autoPause && isDirect && config.IgnoreDirectPairs)
            autoPause = false;

        if (!autoPause || _serverConfigurationManager.IsUidWhitelisted(pair.UserData.UID))
            return true;

        long vramUsageThresholdBytes = (long)config.VRAMSizeAutoPauseThresholdMiB * 1024 * 1024;

        if (vramUsage > vramUsageThresholdBytes)
        {
            if (!affect)
                return false;

            if (notify && !pair.IsApplicationBlocked)
            {
                _mediator.Publish(new NotificationMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID}) automatically blocked",
                    $"Player {pair.PlayerName} ({pair.UserData.AliasOrUID}) exceeded your configured VRAM auto block threshold (" +
                    $"{UiSharedService.ByteToString(vramUsage, addSuffix: true)}/{config.VRAMSizeAutoPauseThresholdMiB}MiB)" +
                    $" and has been automatically blocked.",
                    MareConfiguration.Models.NotificationType.Warning));
                _notificationTracker.Upsert(NotificationEntry.PlayerAutoBlockedVRAM(
                    pair.UserData.UID, pair.UserData.AliasOrUID, UiSharedService.ByteToString(vramUsage, addSuffix: true), config.VRAMSizeAutoPauseThresholdMiB));
            }

            _mediator.Publish(new EventMessage(new Event(pair.PlayerName, pair.UserData, nameof(PlayerPerformanceService), EventSeverity.Warning,
                $"Exceeds VRAM threshold: ({UiSharedService.ByteToString(vramUsage, addSuffix: true)}/{config.VRAMSizeAutoPauseThresholdMiB} MiB)")));

            return false;
        }

        return true;
    }

    public async Task<bool> ShrinkTextures(PairHandler pairHandler, CharacterData charaData, CancellationToken token)
    {
        var config = _playerPerformanceConfigService.Current;

        if (config.TextureShrinkMode == MareConfiguration.Models.TextureShrinkMode.Never)
            return false;

        // XXX: Temporary
        if (config.TextureShrinkMode == MareConfiguration.Models.TextureShrinkMode.Default)
            return false;

        var moddedTextureHashes = charaData.FileReplacements.SelectMany(k => k.Value)
            .Where(p => string.IsNullOrEmpty(p.FileSwapPath) && p.GamePaths.Any(g => g.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool shrunken = false;

        await Parallel.ForEachAsync(moddedTextureHashes,
            token,
            async (hash, ct) =>
            {
                var fileEntry = _fileCacheManager.GetFileCacheByHash(hash, preferSubst: true);
                if (fileEntry == null) return;
                if (fileEntry.IsSubstEntry) return;

                var texFormat = _xivDataAnalyzer.GetTexFormatByHash(hash);
                var filePath = fileEntry.ResolvedFilepath;
                var tmpFilePath = _fileCacheManager.GetSubstFilePath(Guid.NewGuid().ToString(), "tmp");
                var newFilePath = _fileCacheManager.GetSubstFilePath(hash, "tex");
                var mipLevel = 0;
                uint width = texFormat.Width;
                uint height = texFormat.Height;
                long offsetDelta = 0;

                uint bitsPerPixel = texFormat.Format switch
                {
                    0x1130 => 8, // L8
                    0x1131 => 8, // A8
                    0x1440 => 16, // A4R4G4B4
                    0x1441 => 16, // A1R5G5B5
                    0x1450 => 32, // A8R8G8B8
                    0x1451 => 32, // X8R8G8B8
                    0x2150 => 32, // R32F
                    0x2250 => 32, // G16R16F
                    0x2260 => 64, // R32G32F
                    0x2460 => 64, // A16B16G16R16F
                    0x2470 => 128, // A32B32G32R32F
                    0x3420 => 4, // DXT1
                    0x3430 => 8, // DXT3
                    0x3431 => 8, // DXT5
                    0x4140 => 16, // D16
                    0x4250 => 32, // D24S8
                    0x6120 => 4, // BC4
                    0x6230 => 8, // BC5
                    0x6432 => 8, // BC7
                    _ => 0
                };

                uint maxSize = (bitsPerPixel <= 8) ? (2048U * 2048U) : (1024U * 1024U);

                while (width * height > maxSize && mipLevel < texFormat.MipCount - 1)
                {
                    offsetDelta += width * height * bitsPerPixel / 8;
                    mipLevel++;
                    width /= 2;
                    height /= 2;
                }

                if (offsetDelta == 0)
                    return;

                _logger.LogDebug("Shrinking {hash} from from {a}x{b} to {c}x{d}",
                    hash, texFormat.Width, texFormat.Height, width, height);

                try
                {
#pragma warning disable MA0004
                    await using (var inFile = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var outFile = new FileStream(tmpFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
#pragma warning restore MA0004
                    {
                        using var reader = new BinaryReader(inFile, Encoding.UTF8, leaveOpen: false);
                        using var writer = new BinaryWriter(outFile, Encoding.UTF8, leaveOpen: false);

                        var header = reader.ReadBytes(80);
                        reader.BaseStream.Position = 14;
                        byte mipByte = reader.ReadByte();
                        byte mipCount = (byte)(mipByte & 0x7F);

                        writer.Write(header);

                        // Update width/height
                        writer.BaseStream.Position = 8;
                        writer.Write((ushort)width);
                        writer.Write((ushort)height);

                        // Update the mip count
                        writer.BaseStream.Position = 14;
                        writer.Write((ushort)((mipByte & 0x80) | (mipCount - mipLevel)));

                        // Reset all of the LoD mips
                        writer.BaseStream.Position = 16;
                        for (int i = 0; i < 3; ++i)
                            writer.Write((uint)0);

                        // Reset all of the mip offsets
                        // (This data is garbage in a lot of modded textures, so its hard to fix it up correctly)
                        writer.BaseStream.Position = 28;
                        for (int i = 0; i < 13; ++i)
                            writer.Write((uint)80);

                        // Write the texture data shifted
                        outFile.Position = 80;
                        inFile.Position = 80 + offsetDelta;

                        await inFile.CopyToAsync(outFile, 81920, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to shrink texture {hash}", hash);
                    if (File.Exists(tmpFilePath))
                        File.Delete(tmpFilePath);
                    return; // do not try to finalize if write failed
                }

                // Finalize after streams have been disposed to avoid file-in-use errors
                try
                {
                    if (File.Exists(newFilePath))
                    {
                        File.Delete(newFilePath);
                    }

                    File.Move(tmpFilePath, newFilePath);

                    var substEntry = _fileCacheManager.CreateSubstEntry(newFilePath);
                    if (substEntry != null)
                        substEntry.CompressedSize = fileEntry.CompressedSize;
                    shrunken = true;

                    // Make sure it's a cache file before trying to delete it !!
                    bool shouldDelete = fileEntry.IsCacheEntry && File.Exists(filePath);

                    if (_playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal && shouldDelete)
                    {
                        try
                        {
                            _logger.LogDebug("Deleting original texture: {filePath}", filePath);
                            File.Delete(filePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete original texture {filePath}", filePath);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to finalize shrunken texture {hash}", hash);
                    if (File.Exists(tmpFilePath))
                        File.Delete(tmpFilePath);
                }
            }
        ).ConfigureAwait(false);

        return shrunken;
    }
}