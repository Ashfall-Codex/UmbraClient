namespace UmbraSync.Services;

public enum ProfileBackgroundFamily
{
    Parchment,
    Illustrated,
    Wood,
}

public sealed record ProfileBackground(int IconId, string Key, ProfileBackgroundFamily Family);

public static class ProfileBackgrounds
{
    public static IReadOnlyList<ProfileBackground> All { get; } =
    [
        new(193720, "WondrousWhimsy", ProfileBackgroundFamily.Parchment),
        new(193743, "ExplorerNotebook", ProfileBackgroundFamily.Parchment),
        new(193666, "SharlayanAgora", ProfileBackgroundFamily.Parchment),
        new(193010, "SolidBeige", ProfileBackgroundFamily.Parchment),
        new(193770, "UltimaOmega", ProfileBackgroundFamily.Illustrated),
        new(193773, "Shantotto", ProfileBackgroundFamily.Illustrated),
        new(193723, "Halone", ProfileBackgroundFamily.Illustrated),
        new(193724, "Menphina", ProfileBackgroundFamily.Illustrated),
        new(193731, "Azeyma", ProfileBackgroundFamily.Illustrated),
        new(193733, "Nophica", ProfileBackgroundFamily.Illustrated),
        new(193713, "LightWood", ProfileBackgroundFamily.Wood),
        new(193714, "DarkWood", ProfileBackgroundFamily.Wood),
        new(193721, "WoodPanels", ProfileBackgroundFamily.Wood),
    ];

    public static bool IsKnown(int iconId) => iconId != 0 && All.Any(b => b.IconId == iconId);

    public static ProfileBackground? Find(int iconId) => All.FirstOrDefault(b => b.IconId == iconId);
}
