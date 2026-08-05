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

}
