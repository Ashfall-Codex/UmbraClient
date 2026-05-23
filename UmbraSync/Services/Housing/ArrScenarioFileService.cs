using Microsoft.Extensions.Logging;
using System.Text.Json;
using UmbraSync.API.Dto.CharaData;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Métadonnées d'un scénario ARR trouvé sur disque.
/// Les champs Territory/Ward/Plot sont best-effort selon ce que le JSON ARR expose ;
/// si la structure exacte évolue côté ARR, les valeurs restent null et l'UI affiche
/// quand même le scénario (le user reste responsable du matching).
/// </summary>
public sealed class ArrScenarioFileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; } = -1;
    public uint? Territory { get; set; }
    public uint? Ward { get; set; }
    public uint? Plot { get; set; }
}

public sealed class ArrScenarioFileService
{
    private readonly ILogger<ArrScenarioFileService> _logger;
    private readonly ArrPathResolver _arrPathResolver;

    public ArrScenarioFileService(ILogger<ArrScenarioFileService> logger, ArrPathResolver arrPathResolver)
    {
        _logger = logger;
        _arrPathResolver = arrPathResolver;
    }

    /// <summary>
    /// Liste tous les scénarios ARR locaux (hors temp files UmbraSync).
    /// </summary>
    public List<ArrScenarioFileInfo> ListLocalScenarios()
    {
        var result = new List<ArrScenarioFileInfo>();
        string? scenariosPath = _arrPathResolver.TryGetScenariosPath();
        if (scenariosPath == null) return result;

        try
        {
            foreach (string jsonFile in Directory.EnumerateFiles(scenariosPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(jsonFile);

                // Skip nos propres fichiers temporaires
                if (fileName.StartsWith("UmbraTemp_", StringComparison.Ordinal)) continue;

                var info = ParseScenarioFile(jsonFile, fileName);
                if (info != null) result.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scan ARR scenarios échoué");
        }

        return result;
    }

    /// <summary>
    /// Heuristique : un scénario matche une location si ses champs connus (Territory/Ward/Plot)
    /// matchent ceux de LocationInfo. Champs non-parsés = ignorés (tolérant).
    /// </summary>
    public static bool MatchesLocation(ArrScenarioFileInfo info, LocationInfo location)
    {
        if (info.Territory.HasValue && info.Territory.Value != location.TerritoryId) return false;
        if (info.Ward.HasValue && info.Ward.Value != location.WardId) return false;
        if (info.Plot.HasValue && info.Plot.Value != location.HouseId) return false;
        return true;
    }

    private ArrScenarioFileInfo? ParseScenarioFile(string filePath, string fileName)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var info = new ArrScenarioFileInfo
            {
                FilePath = filePath,
                FileName = fileName,
                Title = ReadString(root, "Title"),
                Description = ReadString(root, "Description"),
                Version = ReadInt(root, "Version", -1),
            };

            // Location : peut être un objet { Territory, Housing { Ward, Plot, ... } } selon V2+
            if (root.TryGetProperty("Location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            {
                if (TryReadUint(loc, "Territory", out var territory)) info.Territory = territory;
                if (TryReadUint(loc, "TerritoryId", out var territoryId)) info.Territory = territoryId;

                // Housing peut être un sous-objet, ou les champs peuvent être au niveau Location
                if (loc.TryGetProperty("Housing", out var housing) && housing.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadUint(housing, "Ward", out var ward)) info.Ward = ward;
                    if (TryReadUint(housing, "WardId", out var wardId)) info.Ward = wardId;
                    if (TryReadUint(housing, "Plot", out var plot)) info.Plot = plot;
                    if (TryReadUint(housing, "PlotId", out var plotId)) info.Plot = plotId;
                    if (TryReadUint(housing, "HouseId", out var houseId)) info.Plot = houseId;
                }
                else
                {
                    if (TryReadUint(loc, "Ward", out var ward)) info.Ward = ward;
                    if (TryReadUint(loc, "WardId", out var wardId)) info.Ward = wardId;
                    if (TryReadUint(loc, "Plot", out var plot)) info.Plot = plot;
                    if (TryReadUint(loc, "PlotId", out var plotId)) info.Plot = plotId;
                    if (TryReadUint(loc, "HouseId", out var houseId)) info.Plot = houseId;
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parse échoué pour scénario {File}", filePath);
            return null;
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }
        return fallback;
    }

    private static bool TryReadUint(JsonElement parent, string propertyName, out uint value)
    {
        if (parent.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            try
            {
                value = (uint)prop.GetInt64();
                return true;
            }
            catch
            {
                value = 0;
                return false;
            }
        }
        value = 0;
        return false;
    }
}
