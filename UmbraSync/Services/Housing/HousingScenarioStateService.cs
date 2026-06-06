using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace UmbraSync.Services.Housing;

public sealed class RenamedLocalScenario
{
    public string OriginalPath { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
}

public sealed class HousingScenarioStateSnapshot
{
    public string? ActiveTempFile { get; set; }
    public Guid? AppliedShareId { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public List<RenamedLocalScenario> RenamedLocals { get; set; } = [];
}

public sealed class HousingScenarioStateService
{
    private const string StateFileName = "HousingScenarioState.json";

    private readonly ILogger<HousingScenarioStateService> _logger;
    private readonly string _stateFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public HousingScenarioStateService(ILogger<HousingScenarioStateService> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _stateFilePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, StateFileName);
    }

    public HousingScenarioStateSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return null;
            string json = File.ReadAllText(_stateFilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<HousingScenarioStateSnapshot>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lecture impossible du state file scénario, ignoré : {Path}", _stateFilePath);
            return null;
        }
    }

    /// <summary>
    /// Écriture atomique : write to .tmp puis File.Move pour éviter un état corrompu en cas de crash pendant l'écriture.
    /// </summary>
    public void Save(HousingScenarioStateSnapshot state)
    {
        string tmpPath = _stateFilePath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(tmpPath, json);
            if (File.Exists(_stateFilePath))
            {
                File.Replace(tmpPath, _stateFilePath, null);
            }
            else
            {
                File.Move(tmpPath, _stateFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Écriture impossible du state file scénario : {Path}", _stateFilePath);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_stateFilePath)) File.Delete(_stateFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suppression impossible du state file scénario : {Path}", _stateFilePath);
        }
    }
}
