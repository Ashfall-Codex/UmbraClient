using MessagePack;

namespace UmbraSync.Modules.Housing.Services;

[MessagePackObject]
public class StageSharePlaintextV1
{
    [Key(0)] public string StageName { get; set; } = string.Empty;
    [Key(1)] public int StageFormatVersion { get; set; }
    [Key(2)] public string DefinitionJson { get; set; } = string.Empty;
    [Key(3)] public float[] Translation { get; set; } = new float[3];
    [Key(4)] public float[] Rotation { get; set; } = [0f, 0f, 0f, 1f];
    [Key(5)] public float UniformScale { get; set; } = 1f;
    [Key(6)] public List<StageShareFileRef> Files { get; set; } = [];
}

[MessagePackObject]
public class StageShareFileRef
{
    [Key(0)] public string Hash { get; set; } = string.Empty;
    [Key(1)] public long Size { get; set; }
    [Key(2)] public string GamePath { get; set; } = string.Empty;
}
