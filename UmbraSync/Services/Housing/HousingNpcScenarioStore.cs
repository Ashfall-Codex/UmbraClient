using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.CharaData;

namespace UmbraSync.Services.Housing;

public sealed class HousingNpcEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public NpcAppearance Appearance { get; set; } = new();
    public CharacterData? LiveData { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }
    public bool FacePlayer { get; set; }
    public List<NpcAction> Actions { get; set; } = new();
    public bool Looping { get; set; } = true;
    public float LoopDelay { get; set; }
    public ushort Emote { get; set; }
    public bool EmoteLoop { get; set; }
    public List<NpcWaypoint> Waypoints { get; set; } = new();
    public bool Run { get; set; }
    public void MigrateLegacyToActions()
    {
        if (Actions.Count > 0) return;
        if (Emote == 0 && Waypoints.Count == 0) return;

        if (Emote != 0)
            Actions.Add(new NpcEmoteAction { Emote = Emote, Loop = EmoteLoop, StayInPose = true });

        var speed = Run ? NpcMoveSpeed.Run : NpcMoveSpeed.Walk;
        foreach (var wp in Waypoints)
        {
            Actions.Add(new NpcMovementAction { X = wp.X, Y = wp.Y, Z = wp.Z, Speed = speed });
            if (wp.Emote != 0)
                Actions.Add(new NpcEmoteAction { Emote = wp.Emote });
            if (wp.PauseSeconds > 0f)
                Actions.Add(new NpcWaitAction { Duration = wp.PauseSeconds });
        }
    }
}

public sealed class NpcWaypoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public ushort Emote { get; set; }
    public float PauseSeconds { get; set; }
}

public sealed class HousingNpcScenario
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Scène";
    public bool Enabled { get; set; } = true;

    public uint ServerId { get; set; }
    public uint TerritoryId { get; set; }
    public uint WardId { get; set; }
    public uint HouseId { get; set; }
    public uint DivisionId { get; set; }
    public uint RoomId { get; set; }

    /// <summary>
    /// Territory de l'intérieur où la scène a été créée : il encode le plan (S/M/L, appartement, chambre)
    /// alors que <see cref="TerritoryId"/> ne porte que le quartier. Sert à valider une réattribution
    /// vers un autre logement. 0 = inconnu (scènes antérieures à ce champ).
    /// </summary>
    public uint InteriorTerritoryId { get; set; }

    public List<HousingNpcEntry> Entries { get; set; } = new();
}


public sealed class HousingNpcScenarioStore
{
    private const string FileName = "housing_npc_scenarios.json";

    private readonly ILogger<HousingNpcScenarioStore> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly System.Threading.Lock _lock = new();
    private List<HousingNpcScenario> _scenes = new();

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

    public List<HousingNpcScenario> ScenesForLocation(LocationInfo loc)
    {
        lock (_lock) return _scenes.Where(s => Matches(s, loc)).ToList();
    }

    /// <summary>Toutes les scènes, y compris celles d'un autre logement (invisibles via <see cref="ScenesForLocation"/>).</summary>
    public List<HousingNpcScenario> AllScenes()
    {
        lock (_lock) return _scenes.ToList();
    }

    /// <summary>Scènes qui ne correspondent pas au logement courant : candidates à une réattribution.</summary>
    public List<HousingNpcScenario> ScenesNotAtLocation(LocationInfo loc)
    {
        lock (_lock) return _scenes.Where(s => !Matches(s, loc)).ToList();
    }

    /// <summary>
    /// Ré-attribue une scène à un autre logement : seules les 6 clés de localisation changent,
    /// les PNJ et leurs coordonnées (locales au plan intérieur) sont conservés tels quels.
    /// </summary>
    public bool ReassignScene(string sceneId, LocationInfo loc, uint interiorTerritoryId)
    {
        lock (_lock)
        {
            var scene = _scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
            if (scene == null) return false;

            scene.ServerId = loc.ServerId;
            scene.TerritoryId = loc.TerritoryId;
            scene.WardId = loc.WardId;
            scene.HouseId = loc.HouseId;
            scene.DivisionId = loc.DivisionId;
            scene.RoomId = loc.RoomId;
            if (interiorTerritoryId != 0) scene.InteriorTerritoryId = interiorTerritoryId;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Renseigne le plan intérieur des scènes du logement courant qui ne le connaissent pas encore
    /// (créées avant l'introduction du champ). Répare le parc existant au fil des visites.
    /// </summary>
    public void StampInteriorTerritory(LocationInfo loc, uint interiorTerritoryId)
    {
        if (interiorTerritoryId == 0) return;
        lock (_lock)
        {
            bool changed = false;
            foreach (var scene in _scenes.Where(s => Matches(s, loc) && s.InteriorTerritoryId == 0))
            {
                scene.InteriorTerritoryId = interiorTerritoryId;
                changed = true;
            }
            if (changed) Save();
        }
    }

    public HousingNpcScenario? GetScene(string sceneId)
    {
        lock (_lock) return _scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
    }

    public HousingNpcScenario CreateScene(LocationInfo loc, string title, uint interiorTerritoryId = 0)
    {
        lock (_lock)
        {
            var scene = new HousingNpcScenario
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Scène" : title,
                ServerId = loc.ServerId,
                TerritoryId = loc.TerritoryId,
                WardId = loc.WardId,
                HouseId = loc.HouseId,
                DivisionId = loc.DivisionId,
                RoomId = loc.RoomId,
                InteriorTerritoryId = interiorTerritoryId,
            };
            _scenes.Add(scene);
            Save();
            return scene;
        }
    }

    public void RemoveScene(string sceneId)
    {
        lock (_lock)
        {
            if (_scenes.RemoveAll(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal)) > 0) Save();
        }
    }

    public void AddEntryToScene(string sceneId, HousingNpcEntry entry)
    {
        lock (_lock)
        {
            var scene = _scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
            if (scene == null) return;
            scene.Entries.Add(entry);
            Save();
        }
    }

    public void RemoveEntry(string sceneId, string entryId)
    {
        lock (_lock)
        {
            var scene = _scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
            if (scene == null) return;
            if (scene.Entries.RemoveAll(e => string.Equals(e.Id, entryId, StringComparison.Ordinal)) > 0) Save();
        }
    }

    public int ClearForLocation(LocationInfo loc)
    {
        lock (_lock)
        {
            int removed = _scenes.RemoveAll(s => Matches(s, loc));
            if (removed > 0) Save();
            return removed;
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
            if (loaded != null)
            {
                _scenes = loaded;
                foreach (var scene in _scenes)
                    foreach (var entry in scene.Entries)
                        entry.MigrateLegacyToActions();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chargement des scènes PNJ housing échoué");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_scenes, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sauvegarde des scènes PNJ housing échouée");
        }
    }
}
