using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Interop.Ipc;
using UmbraSync.Services.Mediator;
using UmbraSync.Services;

namespace UmbraSync.Services.Housing;

public sealed class HousingFurnitureScanner : IMediatorSubscriber
{
    private static readonly string[] HousingPathPrefixes = ["bg/ffxiv/hou/", "bgcommon/hou/"];
    private static readonly string[] AllowedExtensions = [".mdl", ".tex", ".mtrl", ".sgb", ".lgb"];
    private const int StabilizationDelayMs = 5000;
    private const int MaxObjectIndex = 600;

    private readonly ILogger<HousingFurnitureScanner> _logger;
    private readonly MareMediator _mediator;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _collectedPaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stabilizationCts;
    private bool _isScanning;
    private LocationInfo _scanLocation;

    public HousingFurnitureScanner(ILogger<HousingFurnitureScanner> logger, MareMediator mediator, IpcCallerPenumbra penumbra, DalamudUtilService dalamudUtil)
    {
        _logger = logger;
        _mediator = mediator;
        _penumbra = penumbra;
        _dalamudUtil = dalamudUtil;

        _mediator.Subscribe<PenumbraResourceLoadMessage>(this, OnResourceLoad);
    }

    public MareMediator Mediator => _mediator;
    public bool IsScanning => _isScanning;
    public int CollectedFileCount { get { lock (_lock) return _collectedPaths.Count; } }

    public void StartScan(LocationInfo location)
    {
        if (!_dalamudUtil.IsInHousingMode || location.HouseId == 0)
        {
            _logger.LogWarning("StartScan rejected: IsInHousingMode={HousingMode}, HouseId={HouseId}",
                _dalamudUtil.IsInHousingMode, location.HouseId);
            return;
        }

        lock (_lock)
        {
            _collectedPaths.Clear();
            _isScanning = true;
            _scanLocation = location;
            _stabilizationCts?.Cancel();
            _stabilizationCts?.Dispose();
            _stabilizationCts = null;
        }
        _logger.LogInformation("Housing furniture scan started for location {Server}:{Territory}:{Ward}:{House}",
            location.ServerId, location.TerritoryId, location.WardId, location.HouseId);

        // Scan des ressources déjà chargées en mémoire via Penumbra IPC
        _ = ScanExistingResourcesAsync();
    }

    public void StopScan()
    {
        lock (_lock)
        {
            _isScanning = false;
            _stabilizationCts?.Cancel();
            _stabilizationCts?.Dispose();
            _stabilizationCts = null;
        }
        _logger.LogInformation("Housing furniture scan stopped");
    }

    public Dictionary<string, string> GetCollectedPaths()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_collectedPaths, StringComparer.Ordinal);
        }
    }

    private async Task ScanExistingResourcesAsync()
    {
        try
        {
            var indices = new ushort[MaxObjectIndex];
            for (int i = 0; i < MaxObjectIndex; i++)
                indices[i] = (ushort)i;

            var allResources = await _penumbra.GetObjectResourcePathsAsync(_logger, indices).ConfigureAwait(false);
            if (allResources.Length == 0) return;

            lock (_lock)
            {
                if (!_isScanning) return;

                foreach (var resources in allResources)
                {
                    if (resources == null) continue;
                    foreach (var (gamePath, filePaths) in resources)
                    {
                        if (!IsHousingPath(gamePath)) continue;
                        if (!HasValidExtension(gamePath)) continue;

                        foreach (var filePath in filePaths)
                        {
                            if (!string.Equals(gamePath, filePath, StringComparison.OrdinalIgnoreCase))
                            {
                                _collectedPaths[gamePath] = filePath;
                                break;
                            }
                        }
                    }
                }

                if (_collectedPaths.Count > 0)
                {
                    _logger.LogInformation("Active scan found {Count} existing housing resource paths", _collectedPaths.Count);
                    ResetStabilizationTimer();
                }
                else
                {
                    _logger.LogInformation("Active scan found no existing housing resource paths");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan existing housing resources via Penumbra IPC");
        }
    }

    private void OnResourceLoad(PenumbraResourceLoadMessage msg)
    {
        if (!_isScanning) return;

        var gamePath = msg.GamePath;
        var filePath = msg.FilePath;

        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(filePath)) return;
        if (string.Equals(gamePath, filePath, StringComparison.Ordinal)) return;
        if (!IsHousingPath(gamePath)) return;
        if (!HasValidExtension(gamePath)) return;

        lock (_lock)
        {
            if (!_isScanning) return;
            _collectedPaths[gamePath] = filePath;
            ResetStabilizationTimer();
        }
    }

    private void ResetStabilizationTimer()
    {
        _stabilizationCts?.Cancel();
        _stabilizationCts?.Dispose();
        _stabilizationCts = new CancellationTokenSource();
        var token = _stabilizationCts.Token;
        var location = _scanLocation;
        var count = _collectedPaths.Count;

        _ = Task.Delay(StabilizationDelayMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _logger.LogInformation("Housing furniture scan stabilized with {Count} files", count);
            _mediator.Publish(new HousingScanCompleteMessage(location, count));
        }, TaskScheduler.Default);
    }

    private static bool IsHousingPath(string gamePath)
    {
        foreach (var prefix in HousingPathPrefixes)
        {
            if (gamePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasValidExtension(string gamePath)
    {
        foreach (var ext in AllowedExtensions)
        {
            if (gamePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
