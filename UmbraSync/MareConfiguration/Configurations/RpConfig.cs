using UmbraSync.API.Data;

namespace UmbraSync.MareConfiguration.Configurations;

[Serializable]
public class RpConfig : IMareConfiguration
{
    public Dictionary<string, CharacterRpProfile> CharacterProfiles { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 2;
}

[Serializable]
public class CharacterRpProfile
{
    public string RpDescription { get; set; } = string.Empty;
    public string RpProfilePictureBase64 { get; set; } = string.Empty;
    public bool IsRpNsfw { get; set; } = false;
    public string RpFirstName { get; set; } = string.Empty;
    public string RpLastName { get; set; } = string.Empty;
    public string RpTitle { get; set; } = string.Empty;
    public string RpAge { get; set; } = string.Empty;
    public string RpRace { get; set; } = string.Empty;
    public string RpEthnicity { get; set; } = string.Empty;
    public string RpHeight { get; set; } = string.Empty;
    public string RpBuild { get; set; } = string.Empty;
    public string RpResidence { get; set; } = string.Empty;
    public string RpOccupation { get; set; } = string.Empty;
    public string RpAffiliation { get; set; } = string.Empty;
    public string RpAlignment { get; set; } = string.Empty;
    public string RpAdditionalInfo { get; set; } = string.Empty;
    public string RpNameColor { get; set; } = string.Empty;
    public List<RpCustomField> RpCustomFields { get; set; } = new();
    public string MoodlesBackupJson { get; set; } = string.Empty;
    public long MoodlesBackupTimestamp { get; set; } = 0;
}