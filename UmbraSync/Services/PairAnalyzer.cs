using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.FileCache;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;

namespace UmbraSync.Services;

public sealed class PairAnalyzer : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileCacheManager;
    // Utilisé uniquement pour l'analyse détaillée en DEBUG
#if DEBUG
    private readonly XivDataAnalyzer _xivDataAnalyzer;
#endif
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _baseAnalysisCts = new();
#if DEBUG
    private string _lastDataHash = string.Empty;
#endif

    public PairAnalyzer(ILogger<PairAnalyzer> logger, Pair pair, MareMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        Pair = pair;
#if DEBUG
        Mediator.SubscribeKeyed<PairDataAppliedMessage>(this, pair.UserData.UID, (msg) =>
        {
            var tokenSource = EnsureFreshCts(ref _baseAnalysisCts);
            var token = tokenSource.Token;
            if (msg.CharacterData != null)
            {
                _ = BaseAnalysis(msg.CharacterData, token);
            }
            else
            {
                LastAnalysis.Clear();
                _lastDataHash = string.Empty;
            }
        });
#endif
        _fileCacheManager = fileCacheManager;
        // En Release ce champ n'est pas utilisé; évite un avertissement en l'affectant à un discard
#if DEBUG
        _xivDataAnalyzer = modelAnalyzer;
#else
        _ = modelAnalyzer;
#endif

#if DEBUG
        var lastReceivedData = pair.LastReceivedCharacterData;
        var baseAnalysisCts = _baseAnalysisCts;
        if (lastReceivedData != null && baseAnalysisCts != null)
            _ = BaseAnalysis(lastReceivedData, baseAnalysisCts.Token);
#endif
    }

    public Pair Pair { get; init; }
    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }
    internal Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> LastAnalysis { get; } = [];
    internal string LastPlayerName { get; set; } = string.Empty;

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

            Mediator.Publish(new HaltScanMessage(nameof(PairAnalyzer)));
            try
            {
                foreach (var file in remaining)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken, ignoreCacheEntries: false).ConfigureAwait(false);
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
                Mediator.Publish(new ResumeScanMessage(nameof(PairAnalyzer)));
            }
        }

        LastPlayerName = Pair.PlayerName ?? string.Empty;
        Mediator.Publish(new PairDataAnalyzedMessage(Pair.UserData.UID));

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

#if DEBUG
    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        LastAnalysis.Clear();

        foreach (var obj in charaData.FileReplacements)
        {
            Dictionary<string, CharacterAnalyzer.FileDataEntry> data = new(StringComparer.OrdinalIgnoreCase);
            foreach (var fileEntry in obj.Value)
            {
                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, ignoreCacheEntries: false, validate: false).ToList();
                if (fileCacheEntries.Count == 0) continue;

                var filePath = fileCacheEntries[^1].ResolvedFilepath;
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
                    data[fileEntry.Hash] = new CharacterAnalyzer.FileDataEntry(fileEntry.Hash, ext,
                        [.. fileEntry.GamePaths],
                        fileCacheEntries.Select(c => c.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList(),
                        entry.Size > 0 ? entry.Size.Value : 0,
                        entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                        tris);
                }
            }

            LastAnalysis[obj.Key] = data;
        }

        Mediator.Publish(new PairDataAnalyzedMessage(Pair.UserData.UID));

        _lastDataHash = charaData.DataHash.Value;
    }
#endif

    private void PrintAnalysis()
    {
        if (LastAnalysis.Count == 0) return;
        CharacterAnalyzer.LogAnalysis(Logger, LastAnalysis, Pair.UserData.UID);
    }

    private static CancellationTokenSource EnsureFreshCts(ref CancellationTokenSource? cts)
    {
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        return cts;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignoré intentionnellement: annulation concurrente pendant la destruction
        }

        cts.Dispose();
        cts = null;
    }
}