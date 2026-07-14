using System.Text.Json;

namespace UmbraSync.Services.Housing;

public static class AnamnesisCharaImporter
{
    private static readonly string[] CustomizeOrder =
    {
        "Race", "Gender", "Age", "Height", "Tribe", "Head", "Hair", "EnableHighlights", "Skintone",
        "REyeColor", "HairTone", "Highlights", "FacialFeatures", "LimbalEyes", "Eyebrows", "LEyeColor",
        "Eyes", "Nose", "Jaw", "Mouth", "LipsToneFurPattern", "EarMuscleTailSize", "TailEarsType", "Bust",
        "FacePaint", "FacePaintColor",
    };

    public static (NpcAppearance Appearance, string Nickname) Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var app = new NpcAppearance { Customize = new byte[CustomizeOrder.Length] };
        for (int i = 0; i < CustomizeOrder.Length; i++)
        {
            if (root.TryGetProperty(CustomizeOrder[i], out var el))
                app.Customize[i] = ReadCustomizeByte(CustomizeOrder[i], el);
        }

        app.ModelCharaId = 0; 
        app.Equipment = new[]
        {
            ReadEquip(root, "HeadGear"),
            ReadEquip(root, "Body"),
            ReadEquip(root, "Hands"),
            ReadEquip(root, "Legs"),
            ReadEquip(root, "Feet"),
            ReadEquip(root, "Ears"),
            ReadEquip(root, "Neck"),
            ReadEquip(root, "Wrists"),
            ReadEquip(root, "RightRing"),
            ReadEquip(root, "LeftRing"),
        };

        app.MainHand = ReadWeapon(root, "MainHand");
        app.OffHand = ReadWeapon(root, "OffHand");

        string nickname = root.TryGetProperty("Nickname", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : string.Empty;

        return (app, nickname);
    }

    private static byte ReadCustomizeByte(string field, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return unchecked((byte)el.GetInt32());
            case JsonValueKind.True:
                return field == "EnableHighlights" ? (byte)0x80 : (byte)1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String:
                string s = el.GetString() ?? string.Empty;
                return field switch
                {
                    "Race" => RaceValue(s),
                    "Gender" => GenderValue(s),
                    "Age" => AgeValue(s),
                    "Tribe" => TribeValue(s),
                    "FacialFeatures" => FacialFeatureFlags(s),
                    _ => ParseByte(s),
                };
            default:
                return 0;
        }
    }

    private static NpcEquipPiece ReadEquip(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            return new NpcEquipPiece();
        return new NpcEquipPiece
        {
            Id = ReadUShort(e, "ModelBase"),
            Variant = ReadByte(e, "ModelVariant"),
            Stain0 = ReadByte(e, "DyeId"),
            Stain1 = ReadByte(e, "DyeId2"),
        };
    }

    private static NpcWeapon ReadWeapon(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            return new NpcWeapon();
        return new NpcWeapon
        {
            Id = ReadUShort(e, "ModelSet"),
            Type = ReadUShort(e, "ModelBase"),
            Variant = ReadUShort(e, "ModelVariant"),
            Stain0 = ReadByte(e, "DyeId"),
            Stain1 = ReadByte(e, "DyeId2"),
        };
    }

    internal static byte RaceValue(string s) => Clean(s) switch
    {
        "hyur" => 1, "elezen" => 2, "lalafell" => 3, "miqote" => 4, "roegadyn" => 5,
        "aura" => 6, "hrothgar" => 7, "viera" => 8, _ => ParseByte(s),
    };

    internal static byte GenderValue(string s) => Clean(s) switch
    {
        "masculine" or "male" => 0, "feminine" or "female" => 1, _ => ParseByte(s),
    };

    internal static byte AgeValue(string s) => Clean(s) switch
    {
        "young" => 4, "normal" => 1, "old" => 3, _ => ParseByte(s),
    };

    internal static byte TribeValue(string s) => Clean(s) switch
    {
        "midlander" => 1, "highlander" => 2, "wildwood" => 3, "duskwight" => 4, "plainsfolk" => 5,
        "dunesfolk" => 6, "seekerofthesun" => 7, "keeperofthemoon" => 8, "seawolf" => 9,
        "hellsguard" => 10, "raen" => 11, "xaela" => 12, "helion" or "helions" => 13, "thelost" => 14,
        "rava" => 15, "veena" => 16, _ => ParseByte(s),
    };

    // Champ à bits : "First, Second" → 1 | 2. Peut aussi être un entier direct.
    internal static byte FacialFeatureFlags(string s)
    {
        byte v = 0;
        foreach (var part in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            v |= Clean(part) switch
            {
                "first" => 1, "second" => 2, "third" => 4, "fourth" => 8, "fifth" => 16,
                "sixth" => 32, "seventh" => 64, "legacytattoo" => 128, _ => ParseByte(part),
            };
        }
        return v;
    }

    private static ushort ReadUShort(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? unchecked((ushort)v.GetInt32()) : (ushort)0;

    private static byte ReadByte(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? unchecked((byte)v.GetInt32()) : (byte)0;

    internal static byte ParseByte(string s) => byte.TryParse(s, out var b) ? b : (byte)0;

    internal static string Clean(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
