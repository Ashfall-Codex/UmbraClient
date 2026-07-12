using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services.Housing;


public sealed class HousingNpcScenarioService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly HousingNpcScenarioStore _store;
    private readonly NativeNpcSpawner _spawner;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ITargetManager _targetManager;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<nint> _spawned = new();
    private LocationInfo? _currentLocation;
    private int _nameSeq;

    // Génère un nom d'acteur valide et unique à partir d'un compteur.
    private static string MakeNpcName(int seq)
    {
        var sb = new System.Text.StringBuilder("Umbra Npc");
        int n = seq + 1;
        while (n > 0) { n--; sb.Append((char)('a' + (n % 26))); n /= 26; }
        return sb.ToString();
    }

    public HousingNpcScenarioService(ILogger<HousingNpcScenarioService> logger, MareMediator mediator,
        HousingNpcScenarioStore store, NativeNpcSpawner spawner,
        DalamudUtilService dalamudUtil, ITargetManager targetManager) : base(logger, mediator)
    {
        _store = store;
        _spawner = spawner;
        _dalamudUtil = dalamudUtil;
        _targetManager = targetManager;

        Mediator.Subscribe<HousingPlotEnteredMessage>(this, msg => { _ = OnEnteredAsync(msg.LocationInfo); });
        Mediator.Subscribe<HousingPlotLeftMessage>(this, m => { _ = OnLeftAsync(); });
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => ForgetSpawned());
        Mediator.Subscribe<HousingNpcAddRequestMessage>(this, m => { _ = AddFromSelfAsync(string.Empty); });
        Mediator.Subscribe<HousingNpcWipeMessage>(this, m => { _ = WipeCurrentRoomAsync(); });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public LocationInfo? CurrentLocation => _currentLocation;

    public HousingNpcScenario? GetCurrentScenario()
        => _currentLocation is { } loc ? _store.Find(loc) : null;

    public Task RefreshAsync()
        => _currentLocation is { } loc ? OnEnteredAsync(loc) : Task.CompletedTask;

    public async Task RemoveEntryAsync(string entryId)
    {
        if (_currentLocation is not { } loc) return;
        _store.RemoveEntry(loc, entryId);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task MoveEntryToPlayerAsync(string entryId)
    {
        if (_currentLocation is not { } loc) return;
        var entry = _store.Find(loc)?.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
        if (entry == null) return;
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return;
        entry.X = player.Position.X;
        entry.Y = player.Position.Y;
        entry.Z = player.Position.Z;
        entry.Rotation = player.Rotation;
        _store.SaveChanges();
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task PersistAndRefreshAsync()
    {
        _store.SaveChanges();
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task OnEnteredAsync(LocationInfo loc)
    {
        _currentLocation = loc;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DespawnAllInternalAsync().ConfigureAwait(false);

            var scenario = _store.Find(loc);
            if (scenario == null || scenario.Entries.Count == 0) return;

            Logger.LogInformation("Scénario PNJ : {Count} PNJ pour la room {Server}:{Territory}:{Ward}:{House}:{Division}:{Room}",
                scenario.Entries.Count, loc.ServerId, loc.TerritoryId, loc.WardId, loc.HouseId, loc.DivisionId, loc.RoomId);

            foreach (var entry in scenario.Entries)
                await SpawnEntryInternalAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Spawn du scénario PNJ échoué");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnLeftAsync()
    {
        _currentLocation = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnAllInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }


    private async Task SpawnEntryInternalAsync(HousingNpcEntry entry)
    {
        var name = MakeNpcName(_nameSeq++);
        var pos = new System.Numerics.Vector3(entry.X, entry.Y, entry.Z);
        var actor = await _dalamudUtil.RunOnFrameworkThread(
            () => _spawner.Spawn(name, pos, entry.Rotation, entry.Appearance)).ConfigureAwait(false);
        if (actor == null) return;
        _spawned.Add(actor.Address);
    }

    private async Task DespawnAllInternalAsync()
    {
        if (_spawned.Count == 0) return;
        var addresses = _spawned.ToList();
        _spawned.Clear();
        foreach (var addr in addresses)
        {
            try { await _dalamudUtil.RunOnFrameworkThread(() => _spawner.Despawn(addr)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.LogWarning(ex, "Despawn PNJ échoué ({Addr:X})", addr); }
        }
    }

    private void ForgetSpawned()
    {
        _spawned.Clear();
    }

    public Task AddFromSelfAsync(string displayName) => AddCapturedAsync(displayName, fromTarget: false);
    public Task AddFromTargetAsync(string displayName) => AddCapturedAsync(displayName, fromTarget: true);

    private async Task AddCapturedAsync(string displayName, bool fromTarget)
    {
        try
        {
            var loc = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
            if (loc.HouseId == 0)
            {
                Mediator.Publish(new NotificationMessage("Scénario PNJ", "Tu dois être dans un logement pour ajouter un PNJ.", NotificationType.Error));
                return;
            }

            var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (player == null) return;

            var (sourceAddr, sourceName) = await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                if (fromTarget)
                {
                    var t = _targetManager.Target;
                    return (t?.Address ?? nint.Zero, t?.Name.TextValue ?? string.Empty);
                }
                return (_dalamudUtil.GetPlayerPointer(), player.Name.TextValue);
            }).ConfigureAwait(false);

            if (sourceAddr == nint.Zero)
            {
                Mediator.Publish(new NotificationMessage("Scénario PNJ", fromTarget ? "Aucune cible sélectionnée." : "Joueur introuvable.", NotificationType.Error));
                return;
            }

            var appearance = await _dalamudUtil.RunOnFrameworkThread(() => _spawner.ReadAppearance(sourceAddr)).ConfigureAwait(false);

            var entry = new HousingNpcEntry
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? sourceName : displayName,
                Appearance = appearance,
                X = player.Position.X,
                Y = player.Position.Y,
                Z = player.Position.Z,
                Rotation = player.Rotation,
            };
            _store.AddEntry(loc, entry);

            await _gate.WaitAsync().ConfigureAwait(false);
            try { await SpawnEntryInternalAsync(entry).ConfigureAwait(false); }
            finally { _gate.Release(); }

            Mediator.Publish(new NotificationMessage("Scénario PNJ", "PNJ ajouté à ta room et spawné.", NotificationType.Info));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Capture PNJ housing échouée");
            Mediator.Publish(new NotificationMessage("Scénario PNJ", "Échec de l'ajout (voir logs).", NotificationType.Error));
        }
    }

    private async Task WipeCurrentRoomAsync()
    {
        var loc = _currentLocation;
        if (loc == null)
        {
            Mediator.Publish(new NotificationMessage("Scénario PNJ", "Hors housing : rien à vider.", NotificationType.Info));
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnAllInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }

        int removed = _store.ClearForLocation(loc.Value);
        Mediator.Publish(new NotificationMessage("Scénario PNJ", $"Room vidée ({removed} scénario(s) supprimé(s)).", NotificationType.Info));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = DespawnAllInternalAsync();
        base.Dispose(disposing);
    }
}
