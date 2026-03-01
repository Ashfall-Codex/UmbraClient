using System.Text.Json;
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
    private static readonly string[] GamePathPrefixes = ["bgcommon/", "bg/", "common/", "chara/", "vfx/", "shader/"];
    private const int StabilizationDelayMs = 5000;

    private readonly ILogger<HousingFurnitureScanner> _logger;
    private readonly MareMediator _mediator;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _collectedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collectedFurnitureKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sourceHousingModDirs = new(StringComparer.OrdinalIgnoreCase);
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
    public int CollectedFurnitureCount { get { lock (_lock) return _collectedFurnitureKeys.Count; } }
    // Noms des répertoires de mods housing sources détectés (hors UmbraHousing_*).
    public IReadOnlyList<string> GetSourceHousingModDirectories() { lock (_lock) return _sourceHousingModDirs.ToList(); }

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
            _collectedFurnitureKeys.Clear();
            _sourceHousingModDirs.Clear();
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
            foreach (var mp in modPaths)
                _logger.LogDebug("[HousingScan] Mod activé : {Path}", mp);

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
                    TrackCollectedPath(gamePath, resolvedPath);
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
    
    private void ScanModDirectoryForHousingFiles(string modBasePath, HashSet<string> candidatePaths)
    {
        if (!Directory.Exists(modBasePath))
        {
            _logger.LogDebug("[HousingScan] Répertoire de mod introuvable : {Path}", modBasePath);
            return;
        }

        _logger.LogDebug("[HousingScan] Scan du répertoire de mod : {Path}", modBasePath);

        // Passe 1 : collecter tous les fichiers avec extension valide et identifier si c'est un mod housing
        var allFiles = new List<string>();
        bool isHousingMod = false;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(modBasePath, "*.*", SearchOption.AllDirectories))
            {
                if (!HasValidExtension(filePath)) continue;
                allFiles.Add(filePath);

                // Vérifier si au moins un fichier a un chemin housing
                if (!isHousingMod)
                {
                    var rel = Path.GetRelativePath(modBasePath, filePath).Replace('\\', '/');
                    if (ExtractHousingGamePath(rel) != null)
                        isHousingMod = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HousingScan] Erreur lors du scan de {Path}", modBasePath);
        }

        if (!isHousingMod)
        {
            _logger.LogDebug("[HousingScan] Mod {Path} : {Scanned} fichiers scannés, pas un mod housing",
                Path.GetFileName(modBasePath), allFiles.Count);
            return;
        }

        // Tracker le répertoire source (hors mods générés par UmbraSync)
        var modDirName = Path.GetFileName(modBasePath);
        if (!modDirName.StartsWith(HousingShareManager.HousingModPrefix, StringComparison.Ordinal))
        {
            lock (_lock) _sourceHousingModDirs.Add(modDirName);
        }

        // Passe 2 : extraire TOUS les chemins de jeu du mod housing (y compris textures/matériaux partagés)
        int housingFound = 0;
        int sharedFound = 0;

        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(modBasePath, filePath).Replace('\\', '/');

            // Essayer d'abord les préfixes housing
            var gamePath = ExtractHousingGamePath(relativePath);
            if (gamePath != null)
            {
                candidatePaths.Add(gamePath);
                housingFound++;
                continue;
            }

            // Sinon, essayer les préfixes génériques (textures/matériaux partagés du mod)
            gamePath = ExtractGenericGamePath(relativePath);
            if (gamePath != null)
            {
                candidatePaths.Add(gamePath);
                sharedFound++;
                _logger.LogDebug("[HousingScan] Chemin partagé extrait : {RelPath} → {GamePath}", relativePath, gamePath);
            }
        }

        _logger.LogDebug("[HousingScan] Mod {Path} : {Scanned} fichiers scannés, {Housing} housing + {Shared} partagés",
            Path.GetFileName(modBasePath), allFiles.Count, housingFound, sharedFound);

        // Passe 3 : parser les fichiers JSON du mod pour capturer les mappings game_path → mod_path
        // Nécessaire pour les textures partagées (common/) dont le chemin filesystem ne correspond pas au game path réel
        ParseModJsonForHousingPaths(modBasePath, candidatePaths);
    }
    
    private static string? ExtractHousingGamePath(string relativePath)
    {
        foreach (var prefix in HousingPathPrefixes)
        {
            var index = relativePath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return relativePath[index..].ToLowerInvariant();
            }
        }
        return null;
    }

    private static string? ExtractGenericGamePath(string relativePath)
    {
        foreach (var prefix in GamePathPrefixes)
        {
            var index = relativePath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return relativePath[index..].ToLowerInvariant();
            }
        }
        return null;
    }

    // Parse les fichiers JSON de configuration du mod Penumbra pour extraire les game paths housing.
    private void ParseModJsonForHousingPaths(string modBasePath, HashSet<string> candidatePaths)
    {
        int initialCount = candidatePaths.Count;

        // Parser default_mod.json
        var defaultModFile = Path.Combine(modBasePath, "default_mod.json");
        if (File.Exists(defaultModFile))
        {
            ExtractHousingPathsFromJson(defaultModFile, candidatePaths, isGroupFile: false);
        }

        // Parser tous les group_*.json (contiennent les options avec leurs mappings Files)
        try
        {
            foreach (var groupFile in Directory.EnumerateFiles(modBasePath, "group_*.json"))
            {
                ExtractHousingPathsFromJson(groupFile, candidatePaths, isGroupFile: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HousingScan] Erreur lors de l'énumération des fichiers group JSON dans {Path}", modBasePath);
        }

        int newPaths = candidatePaths.Count - initialCount;
        if (newPaths > 0)
        {
            _logger.LogInformation("[HousingScan] {Count} chemins housing supplémentaires extraits des JSON du mod {Mod}",
                newPaths, Path.GetFileName(modBasePath));
        }
    }

    // Extrait les game paths housing depuis un fichier JSON de mod Penumbra.
    private void ExtractHousingPathsFromJson(string jsonFilePath, HashSet<string> candidatePaths, bool isGroupFile)
    {
        try
        {
            var jsonText = File.ReadAllText(jsonFilePath);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (isGroupFile)
            {
                // Structure group_*.json : { "Options": [ { "Files": { gamePath: modPath } } ] }
                if (root.TryGetProperty("Options", out var options) && options.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in options.EnumerateArray())
                    {
                        if (option.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
                        {
                            AddHousingPathsFromFilesElement(files, candidatePaths);
                        }
                    }
                }
            }
            else
            {
                // Structure default_mod.json : { "Files": { gamePath: modPath } }
                if (root.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
                {
                    AddHousingPathsFromFilesElement(files, candidatePaths);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HousingScan] Erreur lors du parsing de {File}", Path.GetFileName(jsonFilePath));
        }
    }

    /// Ajoute les game paths housing depuis un élément JSON Files (dictionnaire gamePath → modPath).
    private static void AddHousingPathsFromFilesElement(JsonElement filesElement, HashSet<string> candidatePaths)
    {
        foreach (var prop in filesElement.EnumerateObject())
        {
            var gamePath = prop.Name;
            if (IsHousingPath(gamePath) && HasValidExtension(gamePath))
            {
                candidatePaths.Add(gamePath.ToLowerInvariant());
            }
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
            TrackCollectedPath(gamePath, filePath);
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
    
    // Ajoute un chemin collecté et met à jour le compteur de meubles distincts.
    private void TrackCollectedPath(string gamePath, string resolvedPath)
    {
        _collectedPaths[gamePath] = resolvedPath;
        var key = ExtractFurnitureKey(gamePath);
        if (key != null)
            _collectedFurnitureKeys.Add(key);
    }
    
    // Extrait l'identification du meuble depuis un chemin de jeu.
    private static string? ExtractFurnitureKey(string gamePath)
    {
        var segments = gamePath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            // L'identifiant de meuble est un segment numérique
            if (segments[i].Length >= 3 && segments[i].Length <= 5
                && int.TryParse(segments[i], System.Globalization.NumberStyles.None, null, out _))
            {
                return string.Join('/', segments[..(i + 1)]);
            }
        }
        return null;
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
