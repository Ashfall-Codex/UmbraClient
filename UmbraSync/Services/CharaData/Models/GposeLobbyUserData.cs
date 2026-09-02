using Dalamud.Utility;
using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Utils;

namespace UmbraSync.Services.CharaData.Models;

public sealed record GposeLobbyUserData(UserData UserData)
{
    public void Reset()
    {
        HasWorldDataUpdate = WorldData != null;
        HasPoseDataUpdate = ApplicablePoseData != null;
        SpawnedVfxId = null;
        LastAppliedCharaDataDate = DateTime.MinValue;
    }

    private WorldData? _worldData;
    public WorldData? WorldData
    {
        get => _worldData; set
        {
            _worldData = value;
            HasWorldDataUpdate = true;
        }
    }

    public bool HasWorldDataUpdate { get; set; } = false;

    private PoseData? _fullPoseData;
    private PoseData? _deltaPoseData;

    public PoseData? FullPoseData
    {
        get => _fullPoseData;
        set
        {
            _fullPoseData = value;
            ApplicablePoseData = CombinePoseData();
            HasPoseDataUpdate = true;
        }
    }

    public PoseData? DeltaPoseData
    {
        get => _deltaPoseData;
        set
        {
            _deltaPoseData = value;
            ApplicablePoseData = CombinePoseData();
            HasPoseDataUpdate = true;
        }
    }

    public PoseData? ApplicablePoseData { get; private set; }
    public bool HasPoseDataUpdate { get; set; } = false;
    public Guid? SpawnedVfxId { get; set; }
    public Vector3? LastWorldPosition { get; set; }
    public Vector3? TargetWorldPosition { get; set; }
    public DateTime? UpdateStart { get; set; }
    private CharaDataDownloadDto? _charaData;
    public CharaDataDownloadDto? CharaData
    {
        get => _charaData; set
        {
            _charaData = value;
            LastUpdatedCharaData = _charaData?.UpdatedDate ?? DateTime.MaxValue;
        }
    }

    public DateTime LastUpdatedCharaData { get; private set; } = DateTime.MaxValue;
    public DateTime LastAppliedCharaDataDate { get; set; } = DateTime.MinValue;
    public nint Address { get; set; }
    public string AssociatedCharaName { get; set; } = string.Empty;

    private PoseData? CombinePoseData()
    {
        if (DeltaPoseData == null && FullPoseData != null) return FullPoseData;
        if (FullPoseData == null) return null;

        PoseData output = FullPoseData!.Value.DeepClone();
        PoseData delta = DeltaPoseData!.Value;

        foreach (var bone in FullPoseData!.Value.Bones)
        {
            if (!delta.Bones.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.Bones.Remove(bone.Key);
            }
            else
            {
                output.Bones[bone.Key] = data;
            }
        }

        foreach (var bone in FullPoseData!.Value.MainHand)
        {
            if (!delta.MainHand.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.MainHand.Remove(bone.Key);
            }
            else
            {
                output.MainHand[bone.Key] = data;
            }
        }

        foreach (var bone in FullPoseData!.Value.OffHand)
        {
            if (!delta.OffHand.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.OffHand.Remove(bone.Key);
            }
            else
            {
                output.OffHand[bone.Key] = data;
            }
        }

        return output;
    }

    public string WorldDataDescriptor { get; private set; } = string.Empty;
    public Vector2 MapCoordinates { get; private set; }
    public Lumina.Excel.Sheets.Map Map { get; private set; }
    public HandledCharaDataEntry? HandledChara { get; set; }

    public async Task SetWorldDataDescriptor(DalamudUtilService dalamudUtilService)
    {
        if (WorldData == null)
        {
            WorldDataDescriptor = "No World Data found";
        }

        var worldData = WorldData!.Value;
        MapCoordinates = await dalamudUtilService.RunOnFrameworkThread(() =>
                MapUtil.WorldToMap(new Vector2(worldData.PositionX, worldData.PositionY), dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map))
            .ConfigureAwait(false);
        Map = dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map;

        WorldDataDescriptor = WorldLocationDescription.Build(worldData, MapCoordinates, dalamudUtilService);
    }
}