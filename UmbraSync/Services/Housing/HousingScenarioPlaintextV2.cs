using MessagePack;

namespace UmbraSync.Services.Housing;

[MessagePackObject]
public class HousingScenarioPlaintextV2
{
    /// <summary><see cref="HousingNpcScenario"/> sérialisé en JSON (System.Text.Json).</summary>
    [Key(0)] public string SceneJson { get; set; } = string.Empty;

    /// <summary>Version du format de scène, pour les évolutions futures du modèle.</summary>
    [Key(1)] public int SceneFormatVersion { get; set; } = 1;
}
