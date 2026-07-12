using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using UmbraSync.API.Dto.CharaData;

namespace UmbraSync.Services.Housing;

public sealed class HousingNpcEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public NpcAppearance Appearance { get; set; } = new();
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }
}

public sealed class HousingNpcScenario
{
    public uint ServerId { get; set; }
    public uint TerritoryId { get; set; }
    public uint WardId { get; set; }
    public uint HouseId { get; set; }
    public uint DivisionId { get; set; }
    public uint RoomId { get; set; }
    public List<HousingNpcEntry> Entries { get; set; } = new();
}


public sealed class HousingNpcScenarioStore
{
    private const string FileName = "housing_npc_scenarios.json";

    private readonly ILogger<HousingNpcScenarioStore> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly object _lock = new();
    private List<HousingNpcScenario> _scenarios = new();

    public HousingNpcScenarioStore(ILogger<HousingNpcScenarioStore> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        Load();
    }

    private string FilePath => Path.Combine(_pluginInterface.ConfigDirectory.FullName, FileName);

    private static bool Matches(HousingNpcScenario s, LocationInfo loc)
        => s.ServerId == loc.ServerId && s.TerritoryId == loc.TerritoryId && s.WardId == loc.WardId
           && s.HouseId == loc.HouseId && s.DivisionId == loc.DivisionId && s.RoomId == loc.RoomId;

    public HousingNpcScenario? Find(LocationInfo loc)
    {
        lock (_lock) return _scenarios.FirstOrDefault(s => Matches(s, loc));
    }

    public void AddEntry(LocationInfo loc, HousingNpcEntry entry)
    {
        lock (_lock)
        {
            var scenario = _scenarios.FirstOrDefault(s => Matches(s, loc));
            if (scenario == null)
            {
                scenario = new HousingNpcScenario
                {
                    ServerId = loc.ServerId,
                    TerritoryId = loc.TerritoryId,
                    WardId = loc.WardId,
                    HouseId = loc.HouseId,
                    DivisionId = loc.DivisionId,
                    RoomId = loc.RoomId,
                };
                _scenarios.Add(scenario);
            }
            scenario.Entries.Add(entry);
            Save();
        }
    }

    public int ClearForLocation(LocationInfo loc)
    {
        lock (_lock)
        {
            int removed = _scenarios.RemoveAll(s => Matches(s, loc));
            if (removed > 0) Save();
            return removed;
        }
    }

    public void RemoveEntry(LocationInfo loc, string entryId)
    {
        lock (_lock)
        {
            var scenario = _scenarios.FirstOrDefault(s => Matches(s, loc));
            if (scenario == null) return;
            int removed = scenario.Entries.RemoveAll(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
            if (scenario.Entries.Count == 0) _scenarios.Remove(scenario);
            if (removed > 0) Save();
        }
    }

    public void SaveChanges()
    {
        lock (_lock) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonSerializer.Deserialize<List<HousingNpcScenario>>(File.ReadAllText(FilePath));
            if (loaded != null) _scenarios = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chargement des scénarios PNJ housing échoué");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_scenarios, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sauvegarde des scénarios PNJ housing échouée");
        }
    }
}
