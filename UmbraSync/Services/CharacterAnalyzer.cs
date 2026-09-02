using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;

namespace UmbraSync.Services;

public sealed class CharacterAnalyzer : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _baseAnalysisCts = new();
    private string _lastDataHash = string.Empty;
    private CharacterAnalysisSummary _previousSummary = CharacterAnalysisSummary.Empty;
    private DateTime _lastAutoAnalysis = DateTime.MinValue;
    private string _lastAutoAnalysisHash = string.Empty;
    private const int AutoAnalysisFileDeltaThreshold = 25;
    private const long AutoAnalysisSizeDeltaThreshold = 50L * 1024 * 1024;
    private static readonly TimeSpan AutoAnalysisCooldown = TimeSpan.FromMinutes(2);
    private const long NotificationSizeThreshold = 300L * 1024 * 1024;
    private const long NotificationTriangleThreshold = 150_000;
    private bool _sizeWarningShown;
    private bool _triangleWarningShown;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;

    public CharacterAnalyzer(ILogger<CharacterAnalyzer> logger, MareMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer, PlayerPerformanceConfigService playerPerformanceConfigService)
        : base(logger, mediator)
    {
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var tokenSource = EnsureFreshCts(ref _baseAnalysisCts);
            var token = tokenSource.Token;
            _ = BaseAnalysis(msg.CharacterData, token);
        });
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = modelAnalyzer;
        _playerPerformanceConfigService = playerPerformanceConfigService;
    }

    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }
    public CharacterAnalysisSummary CurrentSummary { get; private set; } = CharacterAnalysisSummary.Empty;
    public DateTime? LastCompletedAnalysis { get; private set; }
    internal Dictionary<ObjectKind, Dictionary<string, FileDataEntry>> LastAnalysis { get; } = [];

    public void CancelAnalyze()
    {
        CancelAndDispose(ref _analysisCts);
    }

    public async Task ComputeAnalysis(bool print = true, bool recalculate = false)
    {
        Logger.LogDebug("=== Calculating Character Analysis ===");

        var analysisCts = EnsureFreshCts(ref _analysisCts);
        var cancelToken = analysisCts.Token;

        var allFiles = LastAnalysis.SelectMany(v => v.Value.Select(d => d.Value)).ToList();
        if (allFiles.Exists(c => !c.IsComputed || recalculate))
        {
            var remaining = allFiles.Where(c => !c.IsComputed || recalculate).ToList();
            TotalFiles = remaining.Count;
            CurrentFile = 1;
            Logger.LogDebug("=== Computing {amount} remaining files ===", remaining.Count);

            Mediator.Publish(new HaltScanMessage(nameof(CharacterAnalyzer)));
            try
            {
                foreach (var file in remaining)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken, ignoreCacheEntries: true).ConfigureAwait(false);
                    CurrentFile++;
                }

                _fileCacheManager.WriteOutFullCsv();

            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to analyze files");
            }
            finally
            {
                Mediator.Publish(new ResumeScanMessage(nameof(CharacterAnalyzer)));
            }
        }

        RefreshSummary(false, _lastDataHash);

        Mediator.Publish(new CharacterDataAnalyzedMessage());

        if (!cancelToken.IsCancellationRequested)
        {
            LastCompletedAnalysis = DateTime.UtcNow;
        }

        CancelAndDispose(ref _analysisCts);

        if (print) PrintAnalysis();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        CancelAndDispose(ref _analysisCts);
        CancelAndDispose(ref _baseAnalysisCts);
    }

    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        LastAnalysis.Clear();

        foreach (var obj in charaData.FileReplacements)
        {
            Dictionary<string, FileDataEntry> data = new(StringComparer.OrdinalIgnoreCase);
            foreach (var fileEntry in obj.Value)
            {
                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, ignoreCacheEntries: true, validate: false).ToList();
                if (fileCacheEntries.Count == 0) continue;

                var filePath = fileCacheEntries[0].ResolvedFilepath;
                FileInfo fi = new(filePath);
                string ext = "unk?";
                try
                {
                    ext = fi.Extension[1..];
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not identify extension for {path}", filePath);
                }

                var tris = await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(fileEntry.Hash)).ConfigureAwait(false);

                foreach (var entry in fileCacheEntries)
                {
                    data[fileEntry.Hash] = new FileDataEntry(fileEntry.Hash, ext,
                        [.. fileEntry.GamePaths],
                        fileCacheEntries.Select(c => c.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList(),
                        entry.Size > 0 ? entry.Size.Value : 0,
                        entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                        tris);
                }
            }

            LastAnalysis[obj.Key] = data;
        }

        _lastDataHash = charaData.DataHash.Value;
        RefreshSummary(true, _lastDataHash);

        Mediator.Publish(new CharacterDataAnalyzedMessage());

    }

    internal static void LogAnalysis(ILogger logger,
        Dictionary<ObjectKind, Dictionary<string, FileDataEntry>> analysis, string? subject = null)
    {
        foreach (var kvp in analysis)
        {
            int fileCounter = 1;
            int totalFiles = kvp.Value.Count;
            if (subject == null)
                logger.LogInformation("=== Analysis for {obj} ===", kvp.Key);
            else
                logger.LogInformation("=== Analysis for {uid}:{obj} ===", subject, kvp.Key);

            foreach (var entry in kvp.Value.OrderBy(b => b.Value.GamePaths.OrderBy(p => p, StringComparer.Ordinal).First(), StringComparer.Ordinal))
            {
                logger.LogInformation("File {x}/{y}: {hash}", fileCounter++, totalFiles, entry.Key);
                foreach (var path in entry.Value.GamePaths)
                {
                    logger.LogInformation("  Game Path: {path}", path);
                }
                if (entry.Value.FilePaths.Count > 1) logger.LogInformation("  Multiple fitting files detected for {key}", entry.Key);
                foreach (var filePath in entry.Value.FilePaths)
                {
                    logger.LogInformation("  File Path: {path}", filePath);
                }
                logger.LogInformation("  Size: {size}, Compressed: {compressed}", UiSharedService.ByteToString(entry.Value.OriginalSize),
                    UiSharedService.ByteToString(entry.Value.CompressedSize));
            }
        }

        foreach (var kvp in analysis)
        {
            logger.LogInformation("=== Detailed summary by file type for {obj} ===", kvp.Key);
            foreach (var entry in kvp.Value.Select(v => v.Value).GroupBy(v => v.FileType, StringComparer.Ordinal))
            {
                logger.LogInformation("{ext} files: {count}, size extracted: {size}, size compressed: {sizeComp}", entry.Key, entry.Count(),
                    UiSharedService.ByteToString(entry.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(entry.Sum(v => v.CompressedSize)));
            }
            logger.LogInformation("=== Total summary for {obj} ===", kvp.Key);
            logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}", kvp.Value.Count,
                UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.OriginalSize)), UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.CompressedSize)));
        }

        logger.LogInformation("=== Total summary for all currently present objects ===");
        logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}",
            analysis.Values.Sum(v => v.Values.Count),
            UiSharedService.ByteToString(analysis.Values.Sum(c => c.Values.Sum(v => v.OriginalSize))),
            UiSharedService.ByteToString(analysis.Values.Sum(c => c.Values.Sum(v => v.CompressedSize))));
    }

    private void PrintAnalysis()
    {
        if (LastAnalysis.Count == 0) return;

        LogAnalysis(Logger, LastAnalysis);
        Logger.LogInformation("IMPORTANT NOTES:\n\r- For uploads and downloads only the compressed size is relevant.\n\r- An unusually high total files count beyond 200 and up will also increase your download time to others significantly.");
    }

    private void RefreshSummary(bool evaluateAutoAnalysis, string dataHash)
    {
        var summary = CalculateSummary();
        CurrentSummary = summary;

        if (evaluateAutoAnalysis)
        {
            EvaluateAutoAnalysis(summary, dataHash);
        }
        else
        {
            _previousSummary = summary;

            if (!summary.HasUncomputedEntries && string.Equals(_lastAutoAnalysisHash, dataHash, StringComparison.Ordinal))
            {
                _lastAutoAnalysisHash = string.Empty;
            }
        }

        EvaluateThresholdNotifications(summary);
    }

    private CharacterAnalysisSummary CalculateSummary()
    {
        if (LastAnalysis.Count == 0)
        {
            return CharacterAnalysisSummary.Empty;
        }

        long original = 0;
        long compressed = 0;
        long triangles = 0;
        int files = 0;
        bool hasUncomputed = false;

        foreach (var obj in LastAnalysis.Values)
        {
            foreach (var entry in obj.Values)
            {
                files++;
                original += entry.OriginalSize;
                compressed += entry.CompressedSize;
                triangles += entry.Triangles;
                hasUncomputed |= !entry.IsComputed;
            }
        }

        return new CharacterAnalysisSummary(files, original, compressed, triangles, hasUncomputed);
    }

    private void EvaluateAutoAnalysis(CharacterAnalysisSummary newSummary, string dataHash)
    {
        var previous = _previousSummary;
        _previousSummary = newSummary;

        if (newSummary.TotalFiles == 0)
        {
            return;
        }

        if (string.Equals(_lastAutoAnalysisHash, dataHash, StringComparison.Ordinal))
        {
            return;
        }

        if (IsAnalysisRunning)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAutoAnalysis < AutoAnalysisCooldown)
        {
            return;
        }

        bool firstSummary = previous.TotalFiles == 0;
        bool filesIncreased = newSummary.TotalFiles - previous.TotalFiles >= AutoAnalysisFileDeltaThreshold;
        bool sizeIncreased = newSummary.TotalCompressedSize - previous.TotalCompressedSize >= AutoAnalysisSizeDeltaThreshold;
        bool needsCompute = newSummary.HasUncomputedEntries;

        if (!firstSummary && !filesIncreased && !sizeIncreased && !needsCompute)
        {
            return;
        }

        _lastAutoAnalysis = now;
        _lastAutoAnalysisHash = dataHash;
        _ = ComputeAnalysis(print: false);
    }

    private void EvaluateThresholdNotifications(CharacterAnalysisSummary summary)
    {
        if (summary.IsEmpty || summary.HasUncomputedEntries)
        {
            ResetThresholdFlagsIfNeeded(summary);
            return;
        }

        if (!_playerPerformanceConfigService.Current.ShowSelfAnalysisWarnings)
        {
            ResetThresholdFlagsIfNeeded(summary);
            return;
        }

        bool sizeExceeded = summary.TotalCompressedSize >= NotificationSizeThreshold;
        bool trianglesExceeded = summary.TotalTriangles >= NotificationTriangleThreshold;
        List<string> exceededReasons = new();

        if (sizeExceeded && !_sizeWarningShown)
        {
            exceededReasons.Add($"un poids partagé de {UiSharedService.ByteToString(summary.TotalCompressedSize)} (≥ 300 MiB)");
            _sizeWarningShown = true;
        }
        else if (!sizeExceeded && _sizeWarningShown)
        {
            _sizeWarningShown = false;
        }

        if (trianglesExceeded && !_triangleWarningShown)
        {
            exceededReasons.Add($"un total de {UiSharedService.TrisToString(summary.TotalTriangles)} triangles (≥ 150k)");
            _triangleWarningShown = true;
        }
        else if (!trianglesExceeded && _triangleWarningShown)
        {
            _triangleWarningShown = false;
        }

        if (exceededReasons.Count == 0) return;

        string combined = string.Join(" et ", exceededReasons);
        string message = $"Attention : votre self-analysis indique {combined}. Des joueurs risquent de ne pas vous voir et UmbraSync peut activer un auto-pause. Pensez à réduire textures ou modèles lourds.";
        Mediator.Publish(new DualNotificationMessage("Self Analysis", message, NotificationType.Warning));
    }

    private void ResetThresholdFlagsIfNeeded(CharacterAnalysisSummary summary)
    {
        if (summary.IsEmpty)
        {
            _sizeWarningShown = false;
            _triangleWarningShown = false;
            return;
        }

        if (summary.TotalCompressedSize < NotificationSizeThreshold)
        {
            _sizeWarningShown = false;
        }

        if (summary.TotalTriangles < NotificationTriangleThreshold)
        {
            _triangleWarningShown = false;
        }
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct CharacterAnalysisSummary(int TotalFiles, long TotalOriginalSize, long TotalCompressedSize, long TotalTriangles, bool HasUncomputedEntries)
    {
        public static CharacterAnalysisSummary Empty => new();
        public bool IsEmpty => TotalFiles == 0 && TotalOriginalSize == 0 && TotalCompressedSize == 0 && TotalTriangles == 0;
    }

    internal sealed record FileDataEntry(string Hash, string FileType, List<string> GamePaths, List<string> FilePaths, long OriginalSize, long CompressedSize, long Triangles)
    {
        public bool IsComputed => OriginalSize > 0 && CompressedSize > 0;
        public async Task ComputeSizes(FileCacheManager fileCacheManager, CancellationToken token, bool ignoreCacheEntries = true)
        {
            var compressedsize = await fileCacheManager.GetCompressedFileData(Hash, token).ConfigureAwait(false);
            var normalSize = new FileInfo(FilePaths[0]).Length;
            var entries = fileCacheManager.GetAllFileCachesByHash(Hash, ignoreCacheEntries: ignoreCacheEntries, validate: false);
            foreach (var entry in entries)
            {
                entry.Size = normalSize;
                entry.CompressedSize = compressedsize.Item2.LongLength;
            }
            OriginalSize = normalSize;
            CompressedSize = compressedsize.Item2.LongLength;
        }
        public long OriginalSize { get; private set; } = OriginalSize;
        public long CompressedSize { get; private set; } = CompressedSize;
        public long Triangles { get; private set; } = Triangles;

        public Lazy<string> Format = new(() =>
        {
            switch (FileType)
            {
                case "tex":
                    {
                        try
                        {
                            using var stream = new FileStream(FilePaths[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var reader = new BinaryReader(stream);
                            reader.BaseStream.Position = 4;
                            var format = (TexFile.TextureFormat)reader.ReadInt32();
                            var width = reader.ReadInt16();
                            var height = reader.ReadInt16();
                            return $"{format} ({width}x{height})";
                        }
                        catch
                        {
                            return "Unknown";
                        }
                    }
                default:
                    return string.Empty;
            }
        });
    }

    private CancellationTokenSource EnsureFreshCts(ref CancellationTokenSource? cts)
    {
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        return cts;
    }

    private void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            Logger.LogTrace(ex, "CharacterAnalyzer CTS already disposed");
        }

        cts.Dispose();
        cts = null;
    }
}