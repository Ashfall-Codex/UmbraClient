using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UmbraSync.Models;

public partial class MoodleStatusInfo
{
    [JsonPropertyName("IconID")]
    public int IconID { get; set; }

    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("Type")]
    public int Type { get; set; }

    public string CleanTitle => TagRegex().Replace(Title, string.Empty).Trim();

    public string CleanDescription => TagRegex().Replace(Description, string.Empty).Trim();

    [GeneratedRegex(@"\[/?(?:color|glow|i|b|u)(?:=[^\]]*)?]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TagRegex();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static List<MoodleStatusInfo> ParseMoodles(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return [];

        try
        {
            var trimmed = data.AsSpan().Trim();
            if (trimmed.Length == 0) return [];

            if (trimmed[0] == '[')
                return JsonSerializer.Deserialize<List<MoodleStatusInfo>>(data, JsonOptions) ?? [];

            if (trimmed[0] == '{')
            {
                var manager = JsonSerializer.Deserialize<MoodleStatusManager>(data, JsonOptions);
                return manager?.Statuses ?? [];
            }

            return ParseFromMemoryPack(data);
        }
        catch
        {
            return [];
        }
    }
    
    public static string RemoveMoodleAtIndex(string rawData, int index)
    {
        if (string.IsNullOrWhiteSpace(rawData)) return rawData;
        var trimmed = rawData.AsSpan().Trim();

        // JSON format
        if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
        {
            var node = JsonNode.Parse(rawData);
            JsonArray? array = null;
            if (node is JsonArray arr) array = arr;
            else if (node is JsonObject obj && obj["Statuses"] is JsonArray statusArr) array = statusArr;
            if (array == null || index < 0 || index >= array.Count) return rawData;
            array.RemoveAt(index);
            return node!.ToJsonString();
        }

        // Base64/MemoryPack format
        try
        {
            var bytes = Convert.FromBase64String(rawData);
            var entries = ParseEntryOffsets(bytes);
            if (index < 0 || index >= entries.Count) return rawData;

            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(entries.Count - 1));
            for (int i = 0; i < entries.Count; i++)
            {
                if (i == index) continue;
                ms.Write(bytes, entries[i].start, entries[i].end - entries[i].start);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return rawData;
        }
    }

    public static string ReplaceMoodleAtIndex(string rawData, int index, MoodleFullStatus newMoodle)
    {
        if (string.IsNullOrWhiteSpace(rawData)) return AddMoodle(rawData, newMoodle);
        var trimmed = rawData.AsSpan().Trim();

        if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
        {
            var moodleNode = JsonSerializer.SerializeToNode(newMoodle);
            var node = JsonNode.Parse(rawData);
            JsonArray? array = null;
            if (node is JsonArray arr) array = arr;
            else if (node is JsonObject obj && obj["Statuses"] is JsonArray statusArr) array = statusArr;
            if (array == null || index < 0 || index >= array.Count) return rawData;
            array[index] = moodleNode;
            return node!.ToJsonString();
        }

        try
        {
            var bytes = Convert.FromBase64String(rawData);
            var entries = ParseEntryOffsets(bytes);
            if (index < 0 || index >= entries.Count) return rawData;

            var newEntryBytes = WriteMoodleEntry(newMoodle);
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(entries.Count));
            for (int i = 0; i < entries.Count; i++)
            {
                if (i == index)
                    ms.Write(newEntryBytes);
                else
                    ms.Write(bytes, entries[i].start, entries[i].end - entries[i].start);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return rawData;
        }
    }

    public static string AddMoodle(string rawData, MoodleFullStatus newMoodle)
    {
        if (!string.IsNullOrWhiteSpace(rawData))
        {
            var trimmed = rawData.AsSpan().Trim();

            // JSON format
            if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
            {
                var moodleNode = JsonSerializer.SerializeToNode(newMoodle);
                var node = JsonNode.Parse(rawData);
                if (node is JsonArray arr)
                {
                    arr.Add(moodleNode);
                    return node.ToJsonString();
                }
                if (node is JsonObject obj && obj["Statuses"] is JsonArray statusArr)
                {
                    statusArr.Add(moodleNode);
                    return node.ToJsonString();
                }
            }
        }

        // Base64/MemoryPack format (or empty)
        int existingCount = 0;
        byte[] existingEntryBytes = [];

        if (!string.IsNullOrWhiteSpace(rawData))
        {
            try
            {
                var bytes = Convert.FromBase64String(rawData);
                if (bytes.Length >= 4)
                {
                    existingCount = BitConverter.ToInt32(bytes, 0);
                    if (bytes.Length > 4)
                        existingEntryBytes = bytes[4..];
                }
            }
            catch { /* not valid base64 either, start fresh */ }
        }

        var newEntryBytes = WriteMoodleEntry(newMoodle);
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(existingCount + 1));
        if (existingEntryBytes.Length > 0)
            ms.Write(existingEntryBytes);
        ms.Write(newEntryBytes);
        return Convert.ToBase64String(ms.ToArray());
    }
    
    private static List<(int start, int end)> ParseEntryOffsets(byte[] bytes)
    {
        var entries = new List<(int start, int end)>();
        if (bytes.Length < 4) return entries;

        int count = BitConverter.ToInt32(bytes, 0);
        if (count <= 0 || count > 100) return entries;
        int offset = 4;

        for (int i = 0; i < count && offset < bytes.Length; i++)
        {
            int start = offset;
            if (!Skip(bytes, ref offset, 1)) break;   // MemoryPack object header
            if (!Skip(bytes, ref offset, 16)) break;  // GUID
            if (!Skip(bytes, ref offset, 4)) break;   // IconID
            SkipUtf16String(bytes, ref offset);        // Titre
            SkipUtf16String(bytes, ref offset);        // Description
            SkipUtf16String(bytes, ref offset);        // CustomFXPath
            if (!Skip(bytes, ref offset, 8)) break;   // Expiration (int64)
            if (!Skip(bytes, ref offset, 4)) break;   // Type
            if (!Skip(bytes, ref offset, 4)) break;   // Modifieur
            if (!Skip(bytes, ref offset, 4)) break;   // Stacks
            if (!Skip(bytes, ref offset, 4)) break;   // StackSteps
            if (!Skip(bytes, ref offset, 16)) break;  // ChainedStatus GUID
            if (!Skip(bytes, ref offset, 4)) break;   // ChainTrigger
            SkipUtf16String(bytes, ref offset);        // Appliquer
            SkipUtf16String(bytes, ref offset);        // Retirer
            entries.Add((start, offset));
        }

        return entries;
    }

    private static void SkipUtf16String(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length) { offset = bytes.Length; return; }
        int charCount = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        if (charCount <= 0) return;
        int byteCount = charCount * 2;
        if (offset + byteCount > bytes.Length) { offset = bytes.Length; return; }
        offset += byteCount;
    }
    
    private static byte[] WriteMoodleEntry(MoodleFullStatus m)
    {
        const byte MemberCount = 14;                            // Statut 14 [MemoryPackable] fields
        using var ms = new MemoryStream();
        ms.WriteByte(MemberCount);                              // MemoryPack object header 
        ms.Write(Guid.Parse(m.GUID).ToByteArray());            // GUID (16 bytes)
        ms.Write(BitConverter.GetBytes(m.IconID));              // IconID (4 bytes)
        WriteUtf16String(ms, m.Title);                         // Titre
        WriteUtf16String(ms, m.Description);                   // Description
        WriteUtf16String(ms, m.CustomFXPath);                  // CustomFXPath
        ms.Write(BitConverter.GetBytes(m.ExpiresAt));          // Expire le (8 bytes, int64)
        ms.Write(BitConverter.GetBytes(m.Type));               // Type (4 bytes)
        ms.Write(BitConverter.GetBytes(m.Modifiers));          // Modifiers (4 bytes, uint32 flags)
        ms.Write(BitConverter.GetBytes(m.Stacks));             // Stacks (4 bytes)
        ms.Write(BitConverter.GetBytes(m.StackSteps));         // StackSteps (4 bytes)
        ms.Write(Guid.Parse(m.ChainedStatus).ToByteArray());  // ChainedStatus GUID (16 bytes)
        ms.Write(BitConverter.GetBytes(m.ChainTrigger));       // ChainTrigger (4 bytes)
        WriteUtf16String(ms, m.Applier);                       // Appliquer
        WriteUtf16String(ms, m.Dispeller);                     // Retirer
        return ms.ToArray();
    }

    private static void WriteUtf16String(MemoryStream ms, string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            ms.Write(BitConverter.GetBytes(0));
            return;
        }
        ms.Write(BitConverter.GetBytes(s.Length));
        ms.Write(Encoding.Unicode.GetBytes(s));
    }

    private static List<MoodleStatusInfo> ParseFromMemoryPack(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return []; }

        if (bytes.Length < 4) return [];

        var result = new List<MoodleStatusInfo>();
        int offset = 0;

        int count = ReadInt32(bytes, ref offset);
        if (count <= 0 || count > 100) return [];

        for (int i = 0; i < count && offset < bytes.Length; i++)
        {
            try
            {
                if (offset >= bytes.Length) break;
                offset += 1;                                       // MemoryPack object header
                if (!Skip(bytes, ref offset, 16)) break;          // GUID
                int iconId = ReadInt32(bytes, ref offset);         // IconID
                string title = ReadUtf16String(bytes, ref offset); // Title
                string description = ReadUtf16String(bytes, ref offset); // Description
                ReadUtf16String(bytes, ref offset);                // CustomFXPath

                if (!Skip(bytes, ref offset, 8)) break;           // ExpiresAt (int64)
                int type = ReadInt32(bytes, ref offset);           // Type
                if (!Skip(bytes, ref offset, 4)) break;           // Modifiers

                if (!Skip(bytes, ref offset, 4)) break;           // Stacks
                if (!Skip(bytes, ref offset, 4)) break;           // StackSteps
                if (!Skip(bytes, ref offset, 16)) break;          // ChainedStatus GUID
                if (!Skip(bytes, ref offset, 4)) break;           // ChainTrigger
                ReadUtf16String(bytes, ref offset);                // Applier
                ReadUtf16String(bytes, ref offset);                // Dispeller

                if (iconId > 0)
                {
                    result.Add(new MoodleStatusInfo
                    {
                        IconID = iconId,
                        Title = title,
                        Description = description,
                        Type = type
                    });
                }
            }
            catch
            {
                break;
            }
        }

        return result;
    }

    private static int ReadInt32(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length) { offset = bytes.Length; return 0; }
        int val = BitConverter.ToInt32(bytes, offset);
        offset += 4;
        return val;
    }

    private static bool Skip(byte[] bytes, ref int offset, int count)
    {
        if (offset + count > bytes.Length) { offset = bytes.Length; return false; }
        offset += count;
        return true;
    }

    private static string ReadUtf16String(byte[] bytes, ref int offset)
    {
        if (offset + 4 > bytes.Length) { offset = bytes.Length; return string.Empty; }

        int charCount = BitConverter.ToInt32(bytes, offset);
        offset += 4;

        if (charCount < 0) return string.Empty; // null string
        if (charCount == 0) return string.Empty;

        int byteCount = charCount * 2;
        if (offset + byteCount > bytes.Length) { offset = bytes.Length; return string.Empty; }

        string result = Encoding.Unicode.GetString(bytes, offset, byteCount);
        offset += byteCount;
        return result;
    }
}

internal class MoodleStatusManager
{
    [JsonPropertyName("Statuses")]
    public List<MoodleStatusInfo> Statuses { get; set; } = [];
}
