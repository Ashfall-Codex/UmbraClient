namespace UmbraSync.Services;

public enum ProfileHeadingFamily
{
    Rules,
    Ornaments,
    Frames,
}

public enum ProfileHeadingStyle
{
    Rule,
    DoubleRule,
    Underline,
    Editorial,
    Dashes,
    Milestones,
    Diamonds,
    HollowDiamond,
    Stars,
    Pearls,
    Trefoil,
    Chevrons,
    Scrolls,
    Waves,
    Brackets,
    Parentheses,
    Cartouche,
    Band,
}

public static class ProfileHeadingStyles
{
    public static IReadOnlyList<ProfileHeadingStyle> All { get; } = Enum.GetValues<ProfileHeadingStyle>();

    public static ProfileHeadingFamily FamilyOf(ProfileHeadingStyle style) => style switch
    {
        ProfileHeadingStyle.Rule or ProfileHeadingStyle.DoubleRule or ProfileHeadingStyle.Underline
            or ProfileHeadingStyle.Editorial or ProfileHeadingStyle.Dashes or ProfileHeadingStyle.Milestones => ProfileHeadingFamily.Rules,
        ProfileHeadingStyle.Brackets or ProfileHeadingStyle.Parentheses or ProfileHeadingStyle.Cartouche
            or ProfileHeadingStyle.Band => ProfileHeadingFamily.Frames,
        _ => ProfileHeadingFamily.Ornaments,
    };

    public static string KeyOf(ProfileHeadingStyle style) => style switch
    {
        ProfileHeadingStyle.Rule => "rule",
        ProfileHeadingStyle.DoubleRule => "double",
        ProfileHeadingStyle.Underline => "underline",
        ProfileHeadingStyle.Editorial => "editorial",
        ProfileHeadingStyle.Dashes => "dashes",
        ProfileHeadingStyle.Milestones => "milestones",
        ProfileHeadingStyle.HollowDiamond => "hollow",
        ProfileHeadingStyle.Stars => "stars",
        ProfileHeadingStyle.Pearls => "pearls",
        ProfileHeadingStyle.Trefoil => "trefoil",
        ProfileHeadingStyle.Chevrons => "chevrons",
        ProfileHeadingStyle.Scrolls => "scrolls",
        ProfileHeadingStyle.Waves => "waves",
        ProfileHeadingStyle.Brackets => "brackets",
        ProfileHeadingStyle.Parentheses => "parens",
        ProfileHeadingStyle.Cartouche => "cartouche",
        ProfileHeadingStyle.Band => "band",
        _ => "diamonds",
    };

    public static ProfileHeadingStyle Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return ProfileHeadingStyle.Diamonds;
        foreach (var style in All)
        {
            if (string.Equals(KeyOf(style), key, StringComparison.OrdinalIgnoreCase))
                return style;
        }
        return ProfileHeadingStyle.Diamonds;
    }

    public static string? Normalize(string? key)
    {
        var style = Parse(key);
        return style == ProfileHeadingStyle.Diamonds ? null : KeyOf(style);
    }
}
