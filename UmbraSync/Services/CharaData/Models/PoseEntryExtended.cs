using Dalamud.Utility;
using Lumina.Excel.Sheets;
using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Dto.CharaData;

namespace UmbraSync.Services.CharaData.Models;

public sealed record PoseEntryExtended : PoseEntry
{
    private PoseEntryExtended(PoseEntry basePose, CharaDataMetaInfoExtendedDto parent) : base(basePose)
    {
        HasPoseData = !string.IsNullOrEmpty(basePose.PoseData);
        HasWorldData = (WorldData ?? default) != default;
        if (HasWorldData)
        {
            Position = new(basePose.WorldData!.Value.PositionX, basePose.WorldData!.Value.PositionY, basePose.WorldData!.Value.PositionZ);
            Rotation = new(basePose.WorldData!.Value.RotationX, basePose.WorldData!.Value.RotationY, basePose.WorldData!.Value.RotationZ, basePose.WorldData!.Value.RotationW);
        }
        MetaInfo = parent;
    }

    public CharaDataMetaInfoExtendedDto MetaInfo { get; }
    public bool HasPoseData { get; }
    public bool HasWorldData { get; }
    public Vector3 Position { get; } = new();
    public Vector2 MapCoordinates { get; private set; } = new();
    public Quaternion Rotation { get; } = new();
    public Map Map { get; private set; }
    public string WorldDataDescriptor { get; private set; } = string.Empty;

    public static async Task<PoseEntryExtended> Create(PoseEntry baseEntry, CharaDataMetaInfoExtendedDto parent, DalamudUtilService dalamudUtilService)
    {
        PoseEntryExtended newPose = new(baseEntry, parent);

        if (newPose.HasWorldData)
        {
            var worldData = newPose.WorldData!.Value;
            newPose.MapCoordinates = await dalamudUtilService.RunOnFrameworkThread(() =>
                    MapUtil.WorldToMap(new Vector2(worldData.PositionX, worldData.PositionY), dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map))
                .ConfigureAwait(false);
            newPose.Map = dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map;

            newPose.WorldDataDescriptor = WorldLocationDescription.Build(worldData, newPose.MapCoordinates, dalamudUtilService);
        }

        return newPose;
    }
}