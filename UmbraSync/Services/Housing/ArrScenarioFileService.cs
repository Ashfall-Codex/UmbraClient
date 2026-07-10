using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace UmbraSync.Services.Housing;

public sealed class ArrScenarioFileInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; } = -1;
    public uint? Territory { get; set; }
    public uint? Server { get; set; }
    public uint? HousingDivision { get; set; }
    public int? HousingWard { get; set; }
    public int? HousingPlot { get; set; }
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
    
    public static bool MatchesLocation(ArrScenarioFileInfo info, ArrRawLocation location)
    {
        if (info.Territory.HasValue && info.Territory.Value != location.Territory) return false;
        if (!location.InHousing) return true;

        if (info.Server.HasValue && info.Server.Value != location.Server) return false;
        if (info.HousingDivision.HasValue && info.HousingDivision.Value != location.Division) return false;
        if (info.HousingWard.HasValue && info.HousingWard.Value != location.Ward) return false;
        if (location.Indoor && info.HousingPlot.HasValue && info.HousingPlot.Value != location.Plot) return false;
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

            // Objet "Location" à plat, convention ARR (cf. ScenarioData.ScenarioLocation) :
            // { Territory, Server, HousingDivision, HousingWard, HousingPlot }.
            if (root.TryGetProperty("Location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            {
                if (TryReadUint(loc, "Territory", out var territory)) info.Territory = territory;
                if (TryReadUint(loc, "Server", out var server)) info.Server = server;
                if (TryReadUint(loc, "HousingDivision", out var division)) info.HousingDivision = division;
                if (TryReadInt(loc, "HousingWard", out var ward)) info.HousingWard = ward;
                if (TryReadInt(loc, "HousingPlot", out var plot)) info.HousingPlot = plot;
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

    private static bool TryReadInt(JsonElement parent, string propertyName, out int value)
    {
        if (parent.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out value))
        {
            return true;
        }
        value = 0;
        return false;
    }
}
