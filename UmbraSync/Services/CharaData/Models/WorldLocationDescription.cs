using System.Globalization;
using System.Numerics;
using System.Text;
using UmbraSync.API.Dto.CharaData;

namespace UmbraSync.Services.CharaData.Models;

internal static class WorldLocationDescription
{
    public static string Build(WorldData worldData, Vector2 mapCoordinates, DalamudUtilService dalamudUtilService)
    {
        StringBuilder sb = new();
        sb.AppendLine("Server: " + dalamudUtilService.WorldData.Value[(ushort)worldData.LocationInfo.ServerId]);
        sb.AppendLine("Territory: " + dalamudUtilService.TerritoryData.Value[worldData.LocationInfo.TerritoryId]);
        sb.AppendLine("Map: " + dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].MapName);

        if (worldData.LocationInfo.WardId != 0)
            sb.AppendLine("Ward #: " + worldData.LocationInfo.WardId);

        if (worldData.LocationInfo.DivisionId != 0)
        {
            sb.AppendLine("Subdivision: " + worldData.LocationInfo.DivisionId switch
            {
                1 => "No",
                2 => "Yes",
                _ => "-"
            });
        }

        if (worldData.LocationInfo.HouseId != 0)
        {
            sb.AppendLine("House #: " + (worldData.LocationInfo.HouseId == 100 ? "Apartments" : worldData.LocationInfo.HouseId.ToString(CultureInfo.InvariantCulture)));
        }

        if (worldData.LocationInfo.RoomId != 0)
        {
            sb.AppendLine("Apartment #: " + worldData.LocationInfo.RoomId);
        }

        sb.AppendLine("Coordinates: X: " + mapCoordinates.X.ToString("0.0", CultureInfo.InvariantCulture)
            + ", Y: " + mapCoordinates.Y.ToString("0.0", CultureInfo.InvariantCulture));

        return sb.ToString();
    }
}
