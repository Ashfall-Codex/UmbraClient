using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;

namespace UmbraSync.Services;

public record UmbraProfileData(bool IsFlagged, bool IsNSFW, string Base64ProfilePicture, string Description,
    string? Base64RpProfilePicture = null, string? RpDescription = null, bool IsRpNSFW = false,
    string? RpFirstName = null, string? RpLastName = null, string? RpTitle = null, string? RpAge = null,
    string? RpRace = null, string? RpEthnicity = null,
    string? RpHeight = null, string? RpBuild = null, string? RpResidence = null, string? RpOccupation = null, string? RpAffiliation = null,
    string? RpAlignment = null, string? RpAdditionalInfo = null, string? RpNameColor = null,
    List<RpCustomField>? RpCustomFields = null,
    string? MoodlesData = null,
    ushort ChatIcon = 0,
    byte RpLevel = 0,
    RpProfileVisibility? RpVisibility = null,
    string? Base64RpBanner = null,
    string? RpCardStyle = null)
{
    public ProfileImage ImageData { get; init; } = ProfileImage.FromBase64(Base64ProfilePicture);
    public ProfileImage RpImageData { get; init; } = ProfileImage.FromBase64(Base64RpProfilePicture);
    public ProfileImage RpBannerData { get; init; } = ProfileImage.FromBase64(Base64RpBanner);
    public Lazy<ProfileCardStyle> CardStyle { get; } = new Lazy<ProfileCardStyle>(() => ProfileCardStyle.Parse(RpCardStyle));

    internal UmbraProfileData WithImagesStoredIn(ProfileImageStore store) => this with
    {
        Base64ProfilePicture = string.Empty,
        Base64RpProfilePicture = null,
        Base64RpBanner = null,
        ImageData = ImageData.StoreIn(store),
        RpImageData = RpImageData.StoreIn(store),
        RpBannerData = RpBannerData.StoreIn(store),
    };
}