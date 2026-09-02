using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace UmbraSync.Services.Housing;

public sealed partial class NpcPoseCatalog
{
    public enum PoseCategory
    {
        Standing,
        WeaponDrawn,
        Chair,
        GroundSit,
        Lying,
    }

    /// <param name="Key">Clé ActionTimeline (ex. <c>emote/s_pose02_loop</c>), stockée dans la scène.</param>
    /// <param name="TimelineId">Ligne de la feuille du client courant.</param>
    /// <param name="Number">Numéro de pose affiché : la variante 01 est la 2e pose du cycle.</param>
    public sealed record PoseOption(string Key, ushort TimelineId, PoseCategory Category, int Number);
    [GeneratedRegex(@"^emote/(?<prefix>[a-z]_)?pose(?<n>\d+)_loop$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PoseKeyRegex();

    private readonly ILogger<NpcPoseCatalog> _logger;
    private readonly IDataManager _dataManager;
    private readonly Lock _lock = new();

    private IReadOnlyList<PoseOption>? _options;
    private Dictionary<string, ushort>? _byKey;

    public NpcPoseCatalog(ILogger<NpcPoseCatalog> logger, IDataManager dataManager)
    {
        _logger = logger;
        _dataManager = dataManager;
    }

    public IReadOnlyList<PoseOption> Options
    {
        get { lock (_lock) { Build(); return _options!; } }
    }

    /// <summary>Timeline à appliquer pour une clé de pose, 0 si vide ou inconnue du client courant.</summary>
    public ushort Resolve(string? poseKey)
    {
        if (string.IsNullOrEmpty(poseKey)) return 0;
        lock (_lock)
        {
            Build();
            return _byKey!.GetValueOrDefault(poseKey);
        }
    }

    public PoseOption? Find(string? poseKey)
    {
        if (string.IsNullOrEmpty(poseKey)) return null;
        lock (_lock)
        {
            Build();
            return _options!.FirstOrDefault(o => string.Equals(o.Key, poseKey, StringComparison.Ordinal));
        }
    }

    private void Build()
    {
        if (_options != null) return;

        var found = new Dictionary<string, PoseOption>(StringComparer.Ordinal);
        try
        {
            foreach (var row in _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>())
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue) continue;

                var key = row.Key.ExtractText();
                if (string.IsNullOrEmpty(key)) continue;

                var match = PoseKeyRegex().Match(key);
                if (!match.Success) continue;
                if (CategoryOf(match.Groups["prefix"].Value) is not { } category) continue;
                if (!int.TryParse(match.Groups["n"].Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int variant) || variant <= 0)
                    continue;
                
                if (found.TryGetValue(key, out var existing) && existing.TimelineId >= row.RowId) continue;
                found[key] = new PoseOption(key, (ushort)row.RowId, category, variant + 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chargement des variantes de pose depuis ActionTimeline échoué");
        }

        _options = found.Values
            .OrderBy(o => (int)o.Category)
            .ThenBy(o => o.Number)
            .ToList();
        _byKey = _options.ToDictionary(o => o.Key, o => o.TimelineId, StringComparer.Ordinal);
    }

    private static PoseCategory? CategoryOf(string prefix) => prefix switch
    {
        "" => PoseCategory.Standing,
        "b_" => PoseCategory.WeaponDrawn,
        "s_" => PoseCategory.Chair,
        "j_" => PoseCategory.GroundSit,
        "l_" => PoseCategory.Lying,
        _ => null,
    };
}
