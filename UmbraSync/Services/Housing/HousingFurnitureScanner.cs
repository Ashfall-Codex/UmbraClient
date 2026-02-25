using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

public sealed class HousingFurnitureScanner : IMediatorSubscriber
{
    private static readonly string[] HousingPathPrefixes = ["bg/ffxiv/hou/", "bgcommon/hou/"];
    private static readonly string[] AllowedExtensions = [".mdl", ".tex", ".mtrl", ".sgb", ".lgb"];
    private const int StabilizationDelayMs = 5000;

    private readonly ILogger<HousingFurnitureScanner> _logger;
    private readonly MareMediator _mediator;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _collectedPaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stabilizationCts;
    private bool _isScanning;
    private LocationInfo _scanLocation;

    public HousingFurnitureScanner(ILogger<HousingFurnitureScanner> logger, MareMediator mediator)
    {
        _logger = logger;
        _mediator = mediator;

        _mediator.Subscribe<PenumbraResourceLoadMessage>(this, OnResourceLoad);
    }

    public MareMediator Mediator => _mediator;
    public bool IsScanning => _isScanning;
    public int CollectedFileCount { get { lock (_lock) return _collectedPaths.Count; } }

    public void StartScan(LocationInfo location)
    {
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

    private void OnResourceLoad(PenumbraResourceLoadMessage msg)
    {
        if (!_isScanning) return;

        var gamePath = msg.GamePath;
        var filePath = msg.FilePath;

        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(filePath)) return;
        if (string.Equals(gamePath, filePath, StringComparison.Ordinal)) return;

        bool isHousingPath = false;
        foreach (var prefix in HousingPathPrefixes)
        {
            if (gamePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                isHousingPath = true;
                break;
            }
        }
        if (!isHousingPath) return;

        bool hasValidExtension = false;
        foreach (var ext in AllowedExtensions)
        {
            if (gamePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                hasValidExtension = true;
                break;
            }
        }
        if (!hasValidExtension) return;

        lock (_lock)
        {
            if (!_isScanning) return;
            _collectedPaths[gamePath] = filePath;

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
    }
}
