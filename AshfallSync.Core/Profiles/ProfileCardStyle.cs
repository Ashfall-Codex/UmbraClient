using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmbraSync.Services;
public sealed record ProfileCardStyle
{
    public const float DefaultBannerOpacity = 0.5f;
    public const float MinBannerOpacity = 0.1f;
    public const float MaxBannerOpacity = 1f;
    public const float DefaultFadeRadius = 0.5f;
    public const float MinFadeRadius = 0f;
    public const float MaxFadeRadius = 0.9f;
    public const float DefaultBackgroundVeil = 0.4f;
    public const float MinBackgroundVeil = 0f;
    public const float MaxBackgroundVeil = 0.9f;
    public const float MinBackgroundDimming = 0f;
    public const float MaxBackgroundDimming = 0.9f;
    public const int MaxJsonLength = 1024;

    public static ProfileCardStyle Default { get; } = new();
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; init; }

    [JsonPropertyName("textColor")]
    public string? TextColor { get; init; }

    [JsonPropertyName("bannerOpacity")]
    public float BannerOpacity { get; init; } = DefaultBannerOpacity;

    [JsonPropertyName("fadeEdges")]
    public bool FadeEdges { get; init; } = true;

    [JsonPropertyName("fadeRadius")]
    public float FadeRadius { get; init; } = DefaultFadeRadius;

    [JsonPropertyName("background")]
    public int Background { get; init; }

    [JsonPropertyName("backgroundDimming")]
    public float BackgroundDimming { get; init; }

    [JsonPropertyName("backgroundFade")]
    public bool BackgroundFade { get; init; } = true;

    [JsonPropertyName("backgroundVeil")]
    public float BackgroundVeil { get; init; } = DefaultBackgroundVeil;

    [JsonIgnore]
    public bool HasBackground => Background != 0;

    [JsonIgnore]
    public bool IsDefault => Equals(Default);

    public static ProfileCardStyle Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength) return Default;
        try
        {
            return JsonSerializer.Deserialize<ProfileCardStyle>(json)?.Normalized() ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string Serialize()
    {
        var normalized = Normalized();
        return normalized.IsDefault ? string.Empty : JsonSerializer.Serialize(normalized);
    }

    public ProfileCardStyle Normalized() => this with
    {
        AccentColor = IsHexColor(AccentColor) ? AccentColor!.ToUpperInvariant() : null,
        TextColor = IsHexColor(TextColor) ? TextColor!.ToUpperInvariant() : null,
        BannerOpacity = Bound(BannerOpacity, MinBannerOpacity, MaxBannerOpacity, DefaultBannerOpacity),
        FadeRadius = Bound(FadeRadius, MinFadeRadius, MaxFadeRadius, DefaultFadeRadius),
        Background = ProfileBackgrounds.IsKnown(Background) ? Background : 0,
        BackgroundDimming = Bound(BackgroundDimming, MinBackgroundDimming, MaxBackgroundDimming, MinBackgroundDimming),
        BackgroundVeil = Bound(BackgroundVeil, MinBackgroundVeil, MaxBackgroundVeil, DefaultBackgroundVeil),
    };

    private static float Bound(float value, float min, float max, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static bool IsHexColor(string? value)
        => value is { Length: 7 } && value[0] == '#'
           && uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
}
