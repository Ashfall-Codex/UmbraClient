using System.Text.Json.Nodes;

namespace UmbraSync.Modules.Housing.Services;

public static class StageDefinitionRewriter
{
    public const string HashMarkerPrefix = "umbra://";
    private const string ModpacksProperty = "EmbeddedModpacks";
    private const string ResourcesProperty = "ModdedResources";
    private const string TypeProperty = "Type";
    private const string DiskPathProperty = "SourceDiskPath";
    private const string DiskType = "Disk";

    public readonly record struct ModResource(string ModpackId, string GamePath, JsonObject Node, string Type);

    public static IReadOnlyList<ModResource> EnumerateResources(JsonNode root)
    {
        var result = new List<ModResource>();
        if (root is not JsonObject rootObject || rootObject[ModpacksProperty] is not JsonObject modpacks)
            return result;

        foreach (var (modpackId, modpackNode) in modpacks)
        {
            if (modpackNode is not JsonObject modpack || modpack[ResourcesProperty] is not JsonObject resources)
                continue;
            foreach (var (gamePath, resourceNode) in resources)
            {
                if (resourceNode is not JsonObject resource) continue;
                string type = resource[TypeProperty]?.GetValue<string>() ?? string.Empty;
                result.Add(new ModResource(modpackId, gamePath, resource, type));
            }
        }
        return result;
    }

    public static void ReplaceWithMarker(JsonObject resourceNode, string hash)
    {
        resourceNode.Clear();
        resourceNode[TypeProperty] = DiskType;
        resourceNode[DiskPathProperty] = HashMarkerPrefix + hash;
    }

    public static bool RemoveResource(ModResource resource)
    {
        if (resource.Node.Parent is not JsonObject resources) return false;
        return resources.Remove(resource.GamePath);
    }

    public static string? ReadMarker(JsonObject resourceNode)
    {
        if (!string.Equals(resourceNode[TypeProperty]?.GetValue<string>(), DiskType, StringComparison.Ordinal)) return null;
        string? path = resourceNode[DiskPathProperty]?.GetValue<string>();
        if (path == null || !path.StartsWith(HashMarkerPrefix, StringComparison.Ordinal)) return null;
        string hash = path[HashMarkerPrefix.Length..];
        return hash.Length == 0 ? null : hash;
    }

    public static IReadOnlyList<string> CollectMarkers(JsonNode root)
    {
        var hashes = new List<string>();
        foreach (var resource in EnumerateResources(root))
        {
            string? hash = ReadMarker(resource.Node);
            if (hash != null && !hashes.Contains(hash, StringComparer.Ordinal))
                hashes.Add(hash);
        }
        return hashes;
    }

    public static int ResolveMarkers(JsonNode root, Func<string, string?> resolvePath, out List<string> unresolved)
    {
        unresolved = [];
        int resolved = 0;
        foreach (var resource in EnumerateResources(root))
        {
            string? hash = ReadMarker(resource.Node);
            if (hash == null) continue;
            string? path = resolvePath(hash);
            if (string.IsNullOrEmpty(path))
            {
                unresolved.Add(hash);
                RemoveResource(resource);
                continue;
            }
            resource.Node[DiskPathProperty] = path;
            resolved++;
        }
        return resolved;
    }

    public static string ReadStageName(JsonNode root)
        => (root as JsonObject)?["Info"]?["Name"]?.GetValue<string>() ?? string.Empty;

    public static int ReadFormatVersion(JsonNode root)
    {
        JsonNode? node = (root as JsonObject)?["FormatVersion"];
        return node != null && node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<int>() : 0;
    }
}
