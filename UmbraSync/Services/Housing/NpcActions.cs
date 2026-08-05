using System.Text.Json.Serialization;

namespace UmbraSync.Services.Housing;

public enum NpcMoveSpeed
{
    Walk,
    Run,
    Custom,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$action")]
[JsonDerivedType(typeof(NpcEmoteAction), "Emote")]
[JsonDerivedType(typeof(NpcMovementAction), "Movement")]
[JsonDerivedType(typeof(NpcPathAction), "Path")]
[JsonDerivedType(typeof(NpcRotationAction), "Rotation")]
[JsonDerivedType(typeof(NpcWaitAction), "Wait")]
[JsonDerivedType(typeof(NpcIdleAction), "Idle")]
[JsonDerivedType(typeof(NpcVisibilityAction), "Visibility")]
[JsonDerivedType(typeof(NpcTimelineAction), "Timeline")]
[JsonDerivedType(typeof(NpcSyncAction), "Sync")]
public abstract class NpcAction
{
    public bool Enabled { get; set; } = true;
}

public sealed class NpcEmoteAction : NpcAction
{
    public ushort Emote { get; set; }
    public bool Loop { get; set; }
    public bool StayInPose { get; set; }
    public float Duration { get; set; }
}

public sealed class NpcMovementAction : NpcAction
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public NpcMoveSpeed Speed { get; set; } = NpcMoveSpeed.Walk;
    public float CustomSpeed { get; set; }
}

public sealed class NpcPathAction : NpcAction
{
    public List<NpcPathPoint> Points { get; set; } = new();
}

public sealed class NpcPathPoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public NpcMoveSpeed Speed { get; set; } = NpcMoveSpeed.Walk;
    public float CustomSpeed { get; set; }
}

public sealed class NpcRotationAction : NpcAction
{
    public float TargetRotation { get; set; }
}

public sealed class NpcWaitAction : NpcAction
{
    public float Duration { get; set; }
}

public sealed class NpcIdleAction : NpcAction
{
}

public sealed class NpcVisibilityAction : NpcAction
{
    public bool Visible { get; set; } = true;
}

public sealed class NpcTimelineAction : NpcAction
{
    public List<ushort> TimelineIds { get; set; } = new();
    public float Duration { get; set; }
}

public sealed class NpcSyncAction : NpcAction
{
}