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
            // 1. Récupérer les chemins des mods activés dans la collection Default
            var modPaths = await _penumbra.GetEnabledModPathsForDefaultCollectionAsync().ConfigureAwait(false);
            if (modPaths.Count == 0)
            {
                _logger.LogInformation("Aucun mod activé dans la collection Default");
                return;
            }

            _logger.LogInformation("Scan de {Count} mods activés dans la collection Default", modPaths.Count);

            // 2. Scanner les fichiers housing dans chaque répertoire de mod
            var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var modPath in modPaths)
            {
                ScanModDirectoryForHousingFiles(modPath, candidatePaths);
            }

            if (candidatePaths.Count == 0)
            {
                _logger.LogInformation("Aucun fichier housing trouvé dans les mods activés");
                return;
            }

            _logger.LogInformation("{Count} chemins housing candidats trouvés dans les mods", candidatePaths.Count);

            // 3. Vérifier les redirections actives via ResolveDefaultPath
            var resolvedPaths = await _penumbra.ResolveDefaultCollectionPathsAsync(_logger, [.. candidatePaths]).ConfigureAwait(false);

            // 4. Stocker les redirections confirmées
            lock (_lock)
            {
                if (!_isScanning) return;

                foreach (var (gamePath, resolvedPath) in resolvedPaths)
                {
                    _collectedPaths[gamePath] = resolvedPath;
                }

                if (_collectedPaths.Count > 0)
                {
                    _logger.LogInformation("Scan actif : {Count} redirections housing confirmées", _collectedPaths.Count);
                    ResetStabilizationTimer();
                }
                else
                {
                    _logger.LogInformation("Scan actif : aucune redirection housing confirmée");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec du scan des ressources housing via les mods Penumbra");
        }
    }
    
    private static void ScanModDirectoryForHousingFiles(string modBasePath, HashSet<string> candidatePaths)
    {
        if (!Directory.Exists(modBasePath)) return;

        // Scanner depuis la racine du mod (fichiers directs)
        ScanFromRoot(modBasePath, candidatePaths);

        // Scanner depuis les sous-répertoires de premier niveau (options de mod)
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(modBasePath))
            {
                ScanFromRoot(subDir, candidatePaths);
            }
        }
        catch (Exception)
        {
            // Ignorer les erreurs d'accès au filesystem
        }
    }
    
    private static void ScanFromRoot(string root, HashSet<string> candidatePaths)
    {
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!HasValidExtension(filePath)) continue;

                // Calculer le chemin relatif depuis la racine et normaliser en chemin de jeu
                var relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');

                if (IsHousingPath(relativePath))
                {
                    candidatePaths.Add(relativePath);
                }
            }
        }
        catch (Exception)
        {
            // Ignorer les erreurs d'accès au filesystem
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
