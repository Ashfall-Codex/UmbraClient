using System.Text.Json.Serialization;
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Base64ProfilePicture { get; set; }
    public string? ProfilePictureHash { get; set; }
    public string Description { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Base64RpProfilePicture { get; set; }
    public string? RpProfilePictureHash { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Base64RpBanner { get; set; }
    public string? RpBannerHash { get; set; }
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
    public string? RpCardStyle { get; set; }
    public List<RpCustomField>? RpCustomFields { get; set; }
    public string? MoodlesData { get; set; }
    public ushort ChatIcon { get; set; }
    public byte RpLevel { get; set; }
    public DateTime? CachedAtUtc { get; set; }

    public UmbraProfileData ToProfileData(ProfileImageStore store) => new(
        IsFlagged, IsNSFW, string.Empty, Description,
        null, RpDescription, IsRpNSFW,
        RpFirstName, RpLastName, RpTitle, RpAge,
        RpRace, RpEthnicity,
        RpHeight, RpBuild, RpResidence, RpOccupation, RpAffiliation,
        RpAlignment, RpAdditionalInfo, RpNameColor,
        RpCustomFields,
        MoodlesData,
        ChatIcon,
        RpLevel,
        RpCardStyle: RpCardStyle)
    {
        ImageData = store.Get(ProfilePictureHash),
        RpImageData = store.Get(RpProfilePictureHash),
        RpBannerData = store.Get(RpBannerHash),
    };

    public bool MoveLegacyImagesTo(ProfileImageStore store)
    {
        if (Base64ProfilePicture == null && Base64RpProfilePicture == null && Base64RpBanner == null) return false;

        ProfilePictureHash ??= Move(Base64ProfilePicture, store);
        RpProfilePictureHash ??= Move(Base64RpProfilePicture, store);
        RpBannerHash ??= Move(Base64RpBanner, store);
        Base64ProfilePicture = null;
        Base64RpProfilePicture = null;
        Base64RpBanner = null;
        return true;
    }

    private static string? Move(string? base64, ProfileImageStore store)
    {
        var bytes = ProfileImage.Decode(base64);
        if (bytes.Length == 0) return null;

        return store.Put(bytes) ?? throw new IOException("Écriture d'une image de fiche impossible");
    }

    public static ProfileCacheEntry FromProfile(UserData user, string? charName, uint? worldId, UmbraProfileData profile) => new()
    {
        UID = user.UID,
        Alias = user.Alias,
        CharName = charName,
        WorldId = worldId,
        IsFlagged = profile.IsFlagged,
        IsNSFW = profile.IsNSFW,
        ProfilePictureHash = profile.ImageData.StoredHash,
        Description = profile.Description,
        RpProfilePictureHash = profile.RpImageData.StoredHash,
        RpBannerHash = profile.RpBannerData.StoredHash,
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
        RpCardStyle = profile.RpCardStyle,
        RpCustomFields = profile.RpCustomFields,
        MoodlesData = profile.MoodlesData,
        ChatIcon = profile.ChatIcon,
        RpLevel = profile.RpLevel,
    };
}
