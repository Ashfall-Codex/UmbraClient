using UmbraSync.API.Data;

namespace UmbraSync.Services;

internal class ProfileCacheEntry
{
    public string UID { get; set; } = string.Empty;
    public string? Alias { get; set; }
    public string? CharName { get; set; }
    public uint? WorldId { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsNSFW { get; set; }
    public string Base64ProfilePicture { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Base64RpProfilePicture { get; set; }
    public string? RpDescription { get; set; }
    public bool IsRpNSFW { get; set; }
    public string? RpFirstName { get; set; }
    public string? RpLastName { get; set; }
    public string? RpTitle { get; set; }
    public string? RpAge { get; set; }
    public string? RpRace { get; set; }
    public string? RpEthnicity { get; set; }
    public string? RpHeight { get; set; }
    public string? RpBuild { get; set; }
    public string? RpResidence { get; set; }
    public string? RpOccupation { get; set; }
    public string? RpAffiliation { get; set; }
    public string? RpAlignment { get; set; }
    public string? RpAdditionalInfo { get; set; }
    public string? RpNameColor { get; set; }
    public List<RpCustomField>? RpCustomFields { get; set; }
    public string? MoodlesData { get; set; }

    public UmbraProfileData ToProfileData() => new(
        IsFlagged, IsNSFW, Base64ProfilePicture, Description,
        Base64RpProfilePicture, RpDescription, IsRpNSFW,
        RpFirstName, RpLastName, RpTitle, RpAge,
        RpRace, RpEthnicity,
        RpHeight, RpBuild, RpResidence, RpOccupation, RpAffiliation,
        RpAlignment, RpAdditionalInfo, RpNameColor,
        RpCustomFields,
        MoodlesData);

    public static ProfileCacheEntry FromProfile(UserData user, string? charName, uint? worldId, UmbraProfileData profile) => new()
    {
        UID = user.UID,
        Alias = user.Alias,
        CharName = charName,
        WorldId = worldId,
        IsFlagged = profile.IsFlagged,
        IsNSFW = profile.IsNSFW,
        Base64ProfilePicture = profile.Base64ProfilePicture,
        Description = profile.Description,
        Base64RpProfilePicture = profile.Base64RpProfilePicture,
        RpDescription = profile.RpDescription,
        IsRpNSFW = profile.IsRpNSFW,
        RpFirstName = profile.RpFirstName,
        RpLastName = profile.RpLastName,
        RpTitle = profile.RpTitle,
        RpAge = profile.RpAge,
        RpRace = profile.RpRace,
        RpEthnicity = profile.RpEthnicity,
        RpHeight = profile.RpHeight,
        RpBuild = profile.RpBuild,
        RpResidence = profile.RpResidence,
        RpOccupation = profile.RpOccupation,
        RpAffiliation = profile.RpAffiliation,
        RpAlignment = profile.RpAlignment,
        RpAdditionalInfo = profile.RpAdditionalInfo,
        RpNameColor = profile.RpNameColor,
        RpCustomFields = profile.RpCustomFields,
        MoodlesData = profile.MoodlesData,
    };
}
