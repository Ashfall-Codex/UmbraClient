using System.Text.Json;

namespace UmbraSync.Services.Housing;

public static class ArrScenarioImporter
{
    public sealed class ParsedNpc
    {
        public string Name { get; set; } = string.Empty;
        public NpcAppearance Appearance { get; set; } = new();
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Rotation { get; set; }
        public bool FacePlayer { get; set; } = true;
        public List<NpcAction> Actions { get; } = new();
    }

    public sealed class ParsedScenario
    {
        public string Title { get; set; } = string.Empty;
        public bool Looping { get; set; } = true;
        public float LoopDelay { get; set; }
        public List<ParsedNpc> Npcs { get; } = new();
        public int SkippedActions { get; set; }
    }

    public static ParsedScenario Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var result = new ParsedScenario
        {
            Title = ReadString(root, "Title"),
            Looping = ReadBool(root, "Looping", true),
            LoopDelay = ReadFloat(root, "LoopDelay"),
        };

        if (!root.TryGetProperty("Npcs", out var npcs) || npcs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var npc in npcs.EnumerateArray())
        {
            var parsed = new ParsedNpc { Name = ReadString(npc, "Name") };

            if (npc.TryGetProperty("Position", out var posEl))
            {
                parsed.X = ReadFloat(posEl, "X");
                parsed.Y = ReadFloat(posEl, "Y");
                parsed.Z = ReadFloat(posEl, "Z");
            }
            parsed.Rotation = ReadFloat(npc, "Rotation");

            if (npc.TryGetProperty("Behavior", out var beh))
                parsed.FacePlayer = ReadBool(beh, "TrackPlayer", true);

            if (npc.TryGetProperty("Appearance", out var appEl))
                parsed.Appearance = ParseAppearance(appEl);

            if (npc.TryGetProperty("Actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var act in actions.EnumerateArray())
                {
                    var mapped = ParseAction(act);
                    if (mapped != null) parsed.Actions.Add(mapped);
                    else result.SkippedActions++;
                }
            }

            result.Npcs.Add(parsed);
        }

        return result;
    }
    
    private static readonly (string Name, string? Alt)[] CustomizeFields =
    {
        ("Race", null), ("Sex", "Gender"), ("BodyType", "Age"), ("Height", null), ("Tribe", null),
        ("Face", null), ("HairStyle", null), ("Highlights", "Highllights"), ("SkinColor", null),
        ("EyeColorRight", null), ("HairColor", null), ("HighlightsColor", "HighllightsColor"),
        ("FacialFeatures", null), ("TattooColor", null), ("Eyebrows", null), ("EyeColorLeft", null),
        ("EyeShape", null), ("Nose", null), ("Jaw", null), ("Lipstick", null), ("LipColorFurPattern", null),
        ("MuscleMass", null), ("TailShape", null), ("BustSize", null), ("FacePaint", null), ("FacePaintColor", null),
    };

    private static NpcAppearance ParseAppearance(JsonElement appEl)
    {
        var app = new NpcAppearance { Customize = new byte[CustomizeFields.Length] };

        JsonDocument? decoded = null;
        JsonElement a;
        if (appEl.ValueKind == JsonValueKind.Object)
        {
            a = appEl;
        }
        else if (appEl.ValueKind == JsonValueKind.String)
        {
            var b64 = appEl.GetString();
            if (string.IsNullOrEmpty(b64)) return app;
            try { decoded = JsonDocument.Parse(Convert.FromBase64String(b64)); }
            catch { return app; }
            a = decoded.RootElement;
        }
        else
        {
            return app;
        }

        try
        {
            for (int i = 0; i < CustomizeFields.Length; i++)
                app.Customize[i] = ReadCustomize(a, i, CustomizeFields[i].Name, CustomizeFields[i].Alt);

            app.ModelCharaId = TryGetProp(a, "ModelCharaId", "ModelCZarId", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0;

            app.Equipment = new[]
            {
                ReadEquip(a, "HeadGear"),
                ReadEquip(a, "Body"),
                ReadEquip(a, "Hands"),
                ReadEquip(a, "Legs"),
                ReadEquip(a, "Feet"),
                ReadEquip(a, "Ears"),
                ReadEquip(a, "Neck"),
                ReadEquip(a, "Wrists"),
                ReadEquip(a, "RightRing"),
                ReadEquip(a, "LeftRing"),
            };

            app.MainHand = ReadWeapon(a, "MainHand");
            app.OffHand = ReadWeapon(a, "OffHand");
            app.HideWeapon = ReadBool(a, "HideWeapons", false);
            app.HideHeadgear = ReadBool(a, "HideHeadgear", false);
        }
        finally
        {
            decoded?.Dispose();
        }
        return app;
    }

    private static byte ReadCustomize(JsonElement a, int index, string name, string? alt)
    {
        if (!TryGetProp(a, name, alt, out var v)) return 0;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return unchecked((byte)v.GetInt32());
            case JsonValueKind.True:
                return index == 7 ? (byte)0x80 : (byte)1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String:
                string s = v.GetString() ?? string.Empty;
                return index switch
                {
                    0 => AnamnesisCharaImporter.RaceValue(s),
                    1 => AnamnesisCharaImporter.GenderValue(s),
                    2 => AnamnesisCharaImporter.AgeValue(s),
                    4 => AnamnesisCharaImporter.TribeValue(s),
                    12 => AnamnesisCharaImporter.FacialFeatureFlags(s),
                    _ => AnamnesisCharaImporter.ParseByte(s),
                };
            default:
                return 0;
        }
    }

    private static bool TryGetProp(JsonElement e, string name, string? alt, out JsonElement value)
    {
        if (e.TryGetProperty(name, out value)) return true;
        if (alt != null && e.TryGetProperty(alt, out value)) return true;
        value = default;
        return false;
    }

    private static NpcEquipPiece ReadEquip(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            return new NpcEquipPiece();
        return new NpcEquipPiece
        {
            Id = (ushort)ReadInt(e, "ModelId"),
            Variant = ReadByte(e, "Variant"),
            Stain0 = ReadByte(e, "Stain0"),
            Stain1 = ReadByte(e, "Stain1"),
        };
    }

    private static NpcWeapon ReadWeapon(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            return new NpcWeapon();
        return new NpcWeapon
        {
            Id = (ushort)ReadInt(e, "ModelSetId"),
            Type = (ushort)ReadInt(e, "Base"),
            Variant = (ushort)ReadInt(e, "Variant"),
            Stain0 = ReadByte(e, "Stain0"),
            Stain1 = ReadByte(e, "Stain1"),
        };
    }


    private static NpcAction? ParseAction(JsonElement act)
    {
        string kind = ReadString(act, "$action");
        bool enabled = ReadBool(act, "Enabled", true);
        float duration = ReadFloat(act, "Duration");

        switch (kind)
        {
            case "Emote":
                return new NpcEmoteAction
                {
                    Enabled = enabled,
                    Emote = (ushort)ReadInt(act, "Emote"),
                    Loop = ReadBool(act, "Loop", false),
                    StayInPose = ReadBool(act, "StayInEmotePose", false),
                    Duration = duration,
                };
            case "Movement":
                var (mx, my, mz) = ReadVector(act, "TargetPosition");
                return new NpcMovementAction { Enabled = enabled, X = mx, Y = my, Z = mz, Speed = ReadSpeed(act, "Speed") };
            case "Path":
                var pathAction = new NpcPathAction { Enabled = enabled };
                if (act.TryGetProperty("Points", out var points) && points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pt in points.EnumerateArray())
                    {
                        var (px, py, pz) = ReadVector(pt, "Point");
                        pathAction.Points.Add(new NpcPathPoint
                        {
                            X = px, Y = py, Z = pz,
                            Speed = ReadSpeed(pt, "Speed"),
                            CustomSpeed = ReadFloat(pt, "CustomSpeed"),
                        });
                    }
                }
                return pathAction;
            case "Rotation":
                return new NpcRotationAction { Enabled = enabled, TargetRotation = ReadFloat(act, "TargetRotation") };
            case "Waiting":
                return new NpcWaitAction { Enabled = enabled, Duration = duration };
            case "Idle":
                return new NpcIdleAction { Enabled = enabled };
            case "Spawn":
                return new NpcVisibilityAction { Enabled = enabled, Visible = true };
            case "Despawn":
                return new NpcVisibilityAction { Enabled = enabled, Visible = false };
            case "Sync":
                return new NpcSyncAction { Enabled = enabled };
            case "Timeline":
                var timeline = new NpcTimelineAction { Enabled = enabled, Duration = duration };
                if (act.TryGetProperty("ActionSlots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var slot in slots.EnumerateArray())
                    {
                        ushort id = (ushort)ReadInt(slot, "TimelineId");
                        if (id != 0) timeline.TimelineIds.Add(id);
                    }
                }
                return timeline;
            case "Empty":
                return null;
            default:
                return null;
        }
    }

    private static NpcMoveSpeed ReadSpeed(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return NpcMoveSpeed.Walk;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetInt32() switch { 1 => NpcMoveSpeed.Run, 2 => NpcMoveSpeed.Custom, _ => NpcMoveSpeed.Walk };
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? string.Empty).ToLowerInvariant() switch
            {
                "running" => NpcMoveSpeed.Run,
                "custom" => NpcMoveSpeed.Custom,
                _ => NpcMoveSpeed.Walk,
            };
        return NpcMoveSpeed.Walk;
    }


    private static (float X, float Y, float Z) ReadVector(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Object) return (0f, 0f, 0f);
        return (ReadFloat(v, "X"), ReadFloat(v, "Y"), ReadFloat(v, "Z"));
    }

    private static string ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static bool ReadBool(JsonElement e, string prop, bool fallback)
    {
        if (!e.TryGetProperty(prop, out var v)) return fallback;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    }

    private static float ReadFloat(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;

    private static int ReadInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static byte ReadByte(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? unchecked((byte)v.GetInt32()) : (byte)0;
}
