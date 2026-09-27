using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;
using HousingFurnitureRow = Lumina.Excel.Sheets.HousingFurniture;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Recharge les seuls meubles qu'un partage housing modifie.
///
/// « /penumbra redraw furniture » recharge tous les meubles de la pièce. Un meuble rechargé de cette
/// façon perd le revêtement posé dessus : les cloisons tapissées redevenaient nues chez le
/// propriétaire comme chez les visiteurs, alors que le partage ne les concernait pas (issue #102).
/// </summary>
public sealed class HousingFurnitureRedrawService
{
    private const string IndoorFurnitureRoot = "bgcommon/hou/indoor/";
    private const string OutdoorFurnitureRoot = "bgcommon/hou/outdoor/";
    private const string ZoneDecorRoot = "bg/";
    private const uint IndoorFurnitureRowBase = 0x30000;

    private readonly ILogger<HousingFurnitureRedrawService> _logger;
    private readonly IDataManager _gameData;
    private readonly DalamudUtilService _dalamudUtil;
    private Dictionary<uint, ushort>? _modelKeyByRow;
    private Dictionary<uint, ushort>? _modelKeyByItem;

    public HousingFurnitureRedrawService(ILogger<HousingFurnitureRedrawService> logger, IDataManager gameData,
        DalamudUtilService dalamudUtil)
    {
        _logger = logger;
        _gameData = gameData;
        _dalamudUtil = dalamudUtil;
    }

    /// <param name="ModelKeys">Meubles d'intérieur touchés, par clé de modèle.</param>
    /// <param name="HasUnmappedPaths">Des fichiers partagés ne se rattachent à aucun meuble précis.</param>
    public readonly record struct Target(IReadOnlySet<ushort> ModelKeys, bool HasUnmappedPaths)
    {
        public static Target Empty { get; } = new(new HashSet<ushort>(), false);
    }

    public static Target BuildTarget(IEnumerable<string> gamePaths)
    {
        var modelKeys = new HashSet<ushort>();
        bool unmapped = false;

        foreach (var path in gamePaths)
        {
            // Le décor de zone et le mobilier de jardin ne sont pas des meubles de la pièce.
            if (path.StartsWith(ZoneDecorRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(OutdoorFurnitureRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = path.StartsWith(IndoorFurnitureRoot, StringComparison.OrdinalIgnoreCase)
                ? HousingFurnitureScanner.ExtractFurnitureKey(path)
                : null;
            if (key != null && ushort.TryParse(key.AsSpan(key.LastIndexOf('/') + 1), out var modelKey))
                modelKeys.Add(modelKey);
            else
                unmapped = true;
        }

        return new Target(modelKeys, unmapped);
    }

    /// <returns>Nombre de meubles rechargés.</returns>
    public async Task<int> RedrawAsync(Target target)
    {
        if (target.ModelKeys.Count == 0) return 0;

        return await _dalamudUtil.RunOnFrameworkThread(() => RedrawOnFrameworkThread(target.ModelKeys)).ConfigureAwait(false);
    }

    private unsafe int RedrawOnFrameworkThread(IReadOnlySet<ushort> modelKeys)
    {
        var housingManager = HousingManager.Instance();
        if (housingManager == null) return 0;

        var territory = (IndoorTerritory*)housingManager->CurrentTerritory;
        if (territory == null || territory->GetTerritoryType() is not HousingTerritoryType.Indoor) return 0;

        EnsureLookups();
        int placed = 0;
        int identified = 0;
        int redrawn = 0;
        int sampled = 0;

        foreach (ref var furniture in territory->FurnitureManager.FurnitureMemory)
        {
            if (furniture.Index < 0) continue;

            var gameObject = territory->FurnitureManager.ObjectManager.ObjectArray.Objects[furniture.Index].Value;
            if (gameObject == null) continue;
            placed++;

            if (!TryGetModelKey(gameObject->BaseId, furniture.Id, out var modelKey))
            {
                // Quelques exemples suffisent à comprendre un identifiant qu'on ne sait pas lire.
                if (sampled++ < 3)
                    _logger.LogInformation("Redraw housing ciblé : meuble non identifié (Id={Id}, BaseId={BaseId})", furniture.Id, gameObject->BaseId);
                continue;
            }
            identified++;

            if (!modelKeys.Contains(modelKey)) continue;

            gameObject->DisableDraw();
            redrawn++;
        }

        _logger.LogInformation("Redraw housing ciblé : {Redrawn} meuble(s) rechargé(s) sur {Placed} posés ({Identified} identifiés, modèle(s) visés : {Keys})",
            redrawn, placed, identified, string.Join(", ", modelKeys));
        return redrawn;
    }

    private void EnsureLookups()
    {
        if (_modelKeyByRow != null && _modelKeyByItem != null) return;

        var byRow = new Dictionary<uint, ushort>();
        var byItem = new Dictionary<uint, ushort>();
        foreach (var row in _gameData.GetExcelSheet<HousingFurnitureRow>())
        {
            byRow[row.RowId] = row.ModelKey;
            if (row.Item.RowId != 0) byItem[row.Item.RowId] = row.ModelKey;
        }

        _modelKeyByRow = byRow;
        _modelKeyByItem = byItem;
    }

    // Selon le champ lu, un meuble posé est désigné par sa ligne de la feuille HousingFurniture,
    // par cette ligne sans sa base, ou par l'objet d'inventaire. On essaie du plus sûr au moins sûr.
    private bool TryGetModelKey(uint baseId, uint furnitureId, out ushort modelKey)
    {
        var byRow = _modelKeyByRow!;
        var byItem = _modelKeyByItem!;

        if (byRow.TryGetValue(baseId, out modelKey) || byRow.TryGetValue(furnitureId, out modelKey)) return true;
        if (byItem.TryGetValue(baseId, out modelKey) || byItem.TryGetValue(furnitureId, out modelKey)) return true;
        if (baseId != 0 && byRow.TryGetValue(baseId + IndoorFurnitureRowBase, out modelKey)) return true;
        if (furnitureId != 0 && byRow.TryGetValue(furnitureId + IndoorFurnitureRowBase, out modelKey)) return true;

        modelKey = 0;
        return false;
    }
}
