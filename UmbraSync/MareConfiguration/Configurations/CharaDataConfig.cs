using UmbraSync.MareConfiguration.Models;

namespace UmbraSync.MareConfiguration.Configurations;

public class CharaDataConfig : IMareConfiguration
{
    public bool OpenMareHubOnGposeStart { get; set; } = false;
    public string LastSavedCharaDataLocation { get; set; } = string.Empty;
    public Dictionary<string, CharaDataFavorite> FavoriteCodes { get; set; } = [];
    public bool DownloadMcdDataOnConnection { get; set; } = true;
    public int Version { get; set; } = 0;
    public bool NearbyOwnServerOnly { get; set; } = false;
    public bool NearbyIgnoreHousingLimitations { get; set; } = false;
    public bool NearbyDrawWisps { get; set; } = true;
    public int NearbyMaxWisps { get; set; } = 20;
    public int NearbyDistanceFilter { get; set; } = 100;
    public bool NearbyShowOwnData { get; set; } = false;
    public bool ShowHelpTexts { get; set; } = true;
    public bool NearbyShowAlways { get; set; } = false;
    public bool AbbreviateCharaNames { get; set; } = false;
    public string McdfLocalFolder { get; set; } = string.Empty;
}