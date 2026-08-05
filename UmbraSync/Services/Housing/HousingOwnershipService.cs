using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;

/// <summary>
/// Mémorise, pour la session courante, les logements dont le joueur est réellement propriétaire.
/// </summary>
public sealed class HousingOwnershipService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly List<LocationInfo> _knownOwned = new();
    private readonly System.Threading.Lock _lock = new();

    public HousingOwnershipService(ILogger<HousingOwnershipService> logger, MareMediator mediator,
        DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        Mediator.Subscribe<HousingPlotEnteredMessage>(this, msg => { _ = OnEnteredAsync(msg.LocationInfo); });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>true dès qu'un logement possédé a été observé : en dessous, aucun verdict n'est fiable.</summary>
    public bool HasKnownOwnedLocations
    {
        get { lock (_lock) return _knownOwned.Count > 0; }
    }

    public IReadOnlyList<LocationInfo> GetKnownOwnedLocations()
    {
        lock (_lock) return _knownOwned.ToList();
    }

    /// <summary>
    /// true si la localisation correspond à un logement possédé observé. Le numéro de chambre est
    /// ignoré : la parcelle entière appartient au propriétaire, chambres comprises.
    /// </summary>
    public bool IsKnownOwned(LocationInfo loc)
    {
        lock (_lock) return _knownOwned.Any(k => SamePlot(k, loc));
    }

    /// <summary>
    /// true si l'on peut affirmer que ce partage ne pointe plus vers un logement du joueur.
    /// Exige d'avoir observé au moins un logement possédé sur le même monde, sinon on s'abstient.
    /// </summary>
    public bool IsLikelyOrphan(LocationInfo loc)
    {
        lock (_lock)
        {
            if (_knownOwned.Count == 0) return false;
            if (!_knownOwned.Any(k => k.ServerId == loc.ServerId)) return false;
            return !_knownOwned.Any(k => SamePlot(k, loc));
        }
    }

    private static bool SamePlot(LocationInfo a, LocationInfo b)
        => a.ServerId == b.ServerId && a.TerritoryId == b.TerritoryId
           && a.WardId == b.WardId && a.HouseId == b.HouseId && a.DivisionId == b.DivisionId;

    private async Task OnEnteredAsync(LocationInfo loc)
    {
        try
        {
            if (!await _dalamudUtil.RunOnFrameworkThread(() => _dalamudUtil.OwnsCurrentHouse()).ConfigureAwait(false))
            {
                // Preuve directe qu'un logement précédemment enregistré ne nous appartient plus (déménagement).
                lock (_lock)
                {
                    if (_knownOwned.RemoveAll(k => SamePlot(k, loc)) > 0)
                        Logger.LogInformation("Logement retiré des biens connus : plus de permissions");
                }
                return;
            }

            lock (_lock)
            {
                if (_knownOwned.Any(k => SamePlot(k, loc))) return;
                _knownOwned.Add(loc);
            }

            Logger.LogInformation("Logement possédé mémorisé pour la session : S{Server} T{Territory} W{Ward} H{House} D{Division}",
                loc.ServerId, loc.TerritoryId, loc.WardId, loc.HouseId, loc.DivisionId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Vérification de propriété du logement échouée");
        }
    }
}
