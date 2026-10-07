using System.Globalization;
using UmbraSync.API.Dto.Establishment;
using UmbraSync.Localization;

namespace UmbraSync.UI;

public static class EstablishmentLocationText
{
    private static readonly Dictionary<uint, string> DistrictNamesByTerritory = new()
    {
        { 339, "Brumée" }, { 340, "Lavandière" }, { 341, "La Coupe" },
        { 641, "Shirogane" }, { 979, "Empyrée" },
    };

    private static string ResolveDistrictName(uint territoryId)
        => DistrictNamesByTerritory.TryGetValue(territoryId, out var name) ? name : $"Zone {territoryId}";

    public static string? FormatHousing(EstablishmentLocationDto? loc, IReadOnlyDictionary<ushort, string> worldData)
    {
        if (loc == null || (EstablishmentLocationType)loc.LocationType != EstablishmentLocationType.Housing)
            return null;

        var serverName = loc.ServerId.HasValue && worldData.TryGetValue((ushort)loc.ServerId.Value, out var sn)
            ? sn : loc.ServerId?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var districtName = ResolveDistrictName(loc.TerritoryId);
        var subdivText = loc.DivisionId > 1 ? $" {Loc.Get("Establishment.Detail.Subdivision")}" : string.Empty;
        var isApt = loc.IsApartment == true || loc.RoomId.HasValue;
        var locationLine = isApt
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("Establishment.Detail.Apartment"), loc.WardId, loc.RoomId ?? 0)
            : string.Format(CultureInfo.CurrentCulture, Loc.Get("Establishment.Detail.Ward"), loc.WardId, loc.PlotId ?? 0);
        return $"{districtName} • {serverName} • {locationLine}{subdivText}";
    }
}
