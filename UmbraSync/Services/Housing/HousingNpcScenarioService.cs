using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Localization;

namespace UmbraSync.Services.Housing;


public sealed class HousingNpcScenarioService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly HousingNpcScenarioStore _store;
    private readonly NativeNpcSpawner _spawner;
    private readonly LookAtService _lookAt;
    private readonly NpcLiveAppearanceService _liveAppearance;
    private readonly ArrScenarioFileService _arrScenarioFiles;
    private readonly DalamudUtilService _dalamudUtil;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly List<SpawnedNpc> _spawned = new();
    private LocationInfo? _currentLocation;
    private uint _currentInteriorTerritoryId;
    private readonly HashSet<string> _reassignHintShown = new(StringComparer.Ordinal);
    private int _nameSeq;

    private readonly Dictionary<nint, ActionRuntime> _runtimes = new();
    private readonly Dictionary<nint, Anchor> _anchors = new();
    private readonly System.Diagnostics.Stopwatch _moveClock = System.Diagnostics.Stopwatch.StartNew();

    private sealed class Anchor
    {
        public System.Numerics.Vector3 Pos;
        public float Rot;
    }

    private const float WalkSpeed = 2.5f;   // y/s
    private const float RunSpeed = 6.3f;    // y/s
    private const float TurnSpeed = 6.3f;   // rad/s
    private const float ArriveEps = 0.05f;
    private const float PostEmotePause = 1f;        // délai par défaut après la fin d'une emote
    private const float DefaultTimelineDuration = 3f; // durée par défaut d'une action Timeline
    private const float SyncTimeoutSeconds = 30f;    // garde-fou de la barrière Sync

    private sealed record SpawnedNpc(nint Address, NpcLiveHandle? Live, bool Shared, string EntryId);

    // État d'exécution de la séquence d'actions d'un PNJ (avancée chaque frame).
    private sealed class ActionRuntime
    {
        public NpcAction[] Actions = System.Array.Empty<NpcAction>();
        public string GroupId = string.Empty; // scène d'appartenance (barrière Sync entre PNJ d'une même scène)
        public bool AtSync;                   // en attente à un point de rendez-vous
        public float SyncTimeout;             // garde-fou si un PNJ ne se présente jamais
        public bool Looping = true;
        public float LoopDelay;
        public int Index;
        public bool Finished;

        public float WaitLeft;      // pause générique (Wait, post-emote, délai de boucle, démarrage)
        public int PathIndex;       // point courant d'une action Path
        public bool AwaitingEmote;  // attente de la fin de l'emote courante
        public float EmoteGrace;    // délai min avant de tester IsEmoting
        public ushort CurrentEmote; // emote en cours (pour rejeu si boucle)
        public bool EmoteLoopThis;  // rejouer l'emote courante en boucle
        public float EmoteDuration; // durée forcée (0 = jusqu'à la fin une fois)
        public float EmoteElapsed;
    }

    // Génère un nom d'acteur valide et unique à partir d'un compteur.
    private static string MakeNpcName(int seq)
    {
        var sb = new System.Text.StringBuilder("Umbra Npc");
        int n = seq + 1;
        while (n > 0) { n--; sb.Append((char)('a' + (n % 26))); n /= 26; }
        return sb.ToString();
    }

    public HousingNpcScenarioService(ILogger<HousingNpcScenarioService> logger, MareMediator mediator,
        HousingNpcScenarioStore store, NativeNpcSpawner spawner, LookAtService lookAt,
        NpcLiveAppearanceService liveAppearance, ArrScenarioFileService arrScenarioFiles,
        DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _store = store;
        _spawner = spawner;
        _lookAt = lookAt;
        _liveAppearance = liveAppearance;
        _arrScenarioFiles = arrScenarioFiles;
        _dalamudUtil = dalamudUtil;

        Mediator.Subscribe<HousingPlotEnteredMessage>(this, msg => { _ = OnEnteredAsync(msg.LocationInfo); });
        Mediator.Subscribe<HousingPlotLeftMessage>(this, m => { _ = OnLeftAsync(); });
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => ForgetSpawned());
        Mediator.Subscribe<HousingNpcAddRequestMessage>(this, m => { _ = DebugAddFromSelfAsync(); });
        Mediator.Subscribe<HousingNpcWipeMessage>(this, m => { _ = WipeCurrentRoomAsync(); });
        Mediator.Subscribe<FrameworkUpdateMessage>(this, m => OnFrameworkUpdate());
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await DespawnAllInternalAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Despawn des PNJ à l'arrêt incomplet");
        }
    }
    public LocationInfo? CurrentLocation => _currentLocation;

    /// <summary>Plan intérieur du logement courant (0 si inconnu / hors housing).</summary>
    public uint CurrentInteriorTerritoryId => _currentInteriorTerritoryId;

    public string SelectedEntryId { get; set; } = string.Empty;
    public nint TryGetSpawnedAddress(string entryId)
    {
        lock (_stateLock)
            return _spawned.Find(s => string.Equals(s.EntryId, entryId, StringComparison.Ordinal))?.Address ?? nint.Zero;
    }
    
    public void SetEntryTransformLive(string sceneId, string entryId, System.Numerics.Vector3 position, float rotation)
    {
        var entry = FindEntry(sceneId, entryId);
        if (entry == null) return;
        entry.X = position.X;
        entry.Y = position.Y;
        entry.Z = position.Z;
        entry.Rotation = rotation;

        var addr = TryGetSpawnedAddress(entryId);
        if (addr != nint.Zero) Move(addr, position, rotation);
    }

    // Déplace un acteur ET mémorise sa position voulue, pour la ré-imposer chaque frame (anti-gravité).
    private void Move(nint addr, System.Numerics.Vector3 position, float rotation)
    {
        lock (_stateLock)
        {
            if (_anchors.TryGetValue(addr, out var a)) { a.Pos = position; a.Rot = rotation; }
        }
        NativeNpcSpawner.SetTransform(addr, position, rotation);
    }

    public List<HousingNpcScenario> ScenesForCurrentRoom()
        => _currentLocation is { } loc ? _store.ScenesForLocation(loc) : new();

    /// <summary>
    /// Scènes rattachées à un autre logement. Un déménagement ne détruit rien : les scènes restent
    /// dans le fichier, seule la clé de localisation ne correspond plus. Elles sont récupérables via
    /// <see cref="ReassignSceneToCurrentAsync"/>.
    /// </summary>
    public List<HousingNpcScenario> OrphanScenes()
        => _currentLocation is { } loc ? _store.ScenesNotAtLocation(loc) : _store.AllScenes();

    /// <summary>
    /// true si la scène a été créée dans un intérieur de même plan que le logement courant : les
    /// coordonnées des PNJ y restent valides. false si le plan diffère, null si l'origine est inconnue.
    /// </summary>
    public bool? IsLayoutCompatible(HousingNpcScenario scene)
    {
        if (scene.InteriorTerritoryId == 0 || _currentInteriorTerritoryId == 0) return null;
        return scene.InteriorTerritoryId == _currentInteriorTerritoryId;
    }

    /// <summary>Ré-attribue une scène au logement courant (déménagement).</summary>
    public async Task<bool> ReassignSceneToCurrentAsync(string sceneId)
    {
        if (_currentLocation is not { } loc)
        {
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedHousing"), NotificationType.Error));
            return false;
        }

        if (!_store.ReassignScene(sceneId, loc, _currentInteriorTerritoryId)) return false;

        Logger.LogInformation("Scène {SceneId} ré-attribuée au logement courant {Server}:{Territory}:{Ward}:{House}:{Division}:{Room}",
            sceneId, loc.ServerId, loc.TerritoryId, loc.WardId, loc.HouseId, loc.DivisionId, loc.RoomId);

        await RefreshAsync().ConfigureAwait(false);
        Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.SceneReassigned"), NotificationType.Info));
        return true;
    }

    public Task RefreshAsync()
        => _currentLocation is { } loc ? OnEnteredAsync(loc) : Task.CompletedTask;

    public async Task<string?> CreateSceneAsync(string title)
    {
        if (_currentLocation is not { } loc) return null;
        var scene = _store.CreateScene(loc, title, _currentInteriorTerritoryId);
        await RefreshAsync().ConfigureAwait(false);
        return scene.Id;
    }

    public async Task RemoveSceneAsync(string sceneId)
    {
        _store.RemoveScene(sceneId);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task RemoveEntryAsync(string sceneId, string entryId)
    {
        _store.RemoveEntry(sceneId, entryId);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task MoveEntryToPlayerAsync(string sceneId, string entryId)
    {
        var entry = _store.GetScene(sceneId)?.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
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
    
    private static CharacterData WithoutMods(CharacterData data) => new()
    {
        GlamourerData = data.GlamourerData,
        CustomizePlusData = data.CustomizePlusData,
        HeelsData = data.HeelsData,
        HonorificData = data.HonorificData,
        MoodlesData = data.MoodlesData,
        PetNamesData = data.PetNamesData,
        ManipulationData = string.Empty,
        FileReplacements = new(),
    };

    private HousingNpcEntry? FindEntry(string sceneId, string entryId)
        => _store.GetScene(sceneId)?.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
    
    public async Task AddMovementAtPlayerAsync(string sceneId, string entryId, bool run)
    {
        var entry = FindEntry(sceneId, entryId);
        if (entry == null) return;
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return;
        entry.Actions.Add(new NpcMovementAction
        {
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z,
            Speed = run ? NpcMoveSpeed.Run : NpcMoveSpeed.Walk,
        });
        _store.SaveChanges();
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task AddPathPointAtPlayerAsync(string sceneId, string entryId, int actionIndex, bool run)
    {
        var entry = FindEntry(sceneId, entryId);
        if (entry == null || actionIndex < 0 || actionIndex >= entry.Actions.Count) return;
        if (entry.Actions[actionIndex] is not NpcPathAction path) return;
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return;
        path.Points.Add(new NpcPathPoint
        {
            X = player.Position.X,
            Y = player.Position.Y,
            Z = player.Position.Z,
            Speed = run ? NpcMoveSpeed.Run : NpcMoveSpeed.Walk,
        });
        _store.SaveChanges();
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task SetActionRotationToPlayerAsync(string sceneId, string entryId, int actionIndex)
    {
        var entry = FindEntry(sceneId, entryId);
        if (entry == null || actionIndex < 0 || actionIndex >= entry.Actions.Count) return;
        if (entry.Actions[actionIndex] is not NpcRotationAction rot) return;
        var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return;
        rot.TargetRotation = player.Rotation;
        _store.SaveChanges();
        await RefreshAsync().ConfigureAwait(false);
    }

    private async Task OnEnteredAsync(LocationInfo loc)
    {
        _currentLocation = loc;
        try
        {
            _currentInteriorTerritoryId = await _dalamudUtil.GetInteriorTerritoryIdAsync().ConfigureAwait(false);
            _store.StampInteriorTerritory(loc, _currentInteriorTerritoryId);
        }
        catch (Exception ex)
        {
            _currentInteriorTerritoryId = 0;
            Logger.LogWarning(ex, "Lecture du plan intérieur échouée");
        }

        bool nothingHere = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DespawnAllInternalAsync().ConfigureAwait(false);

            var scenes = _store.ScenesForLocation(loc).Where(s => s.Enabled).ToList();
            int total = scenes.Sum(s => s.Entries.Count);
            if (total == 0)
            {
                nothingHere = true;
                return;
            }

            Logger.LogInformation("Scènes PNJ : {SceneCount} scène(s) activée(s), {NpcCount} PNJ pour la room {Server}:{Territory}:{Ward}:{House}:{Division}:{Room}",
                scenes.Count, total, loc.ServerId, loc.TerritoryId, loc.WardId, loc.HouseId, loc.DivisionId, loc.RoomId);

            foreach (var scene in scenes)
                foreach (var entry in scene.Entries)
                    await SpawnEntryInternalAsync(entry, scene.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Spawn du scénario PNJ échoué");
        }
        finally
        {
            _gate.Release();
        }

        // Hors du verrou : purement informatif, et l'aller-retour framework n'a pas à le retenir.
        if (nothingHere) await SuggestReassignAsync(loc).ConfigureAwait(false);
    }

    private async Task OnLeftAsync()
    {
        _currentLocation = null;
        _currentInteriorTerritoryId = 0;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnAllInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Chez soi, sans aucune scène pour ce logement mais avec des scènes rattachées ailleurs : signale
    /// une fois (par logement et par session) qu'elles sont récupérables, sinon l'utilisateur les croit perdues.
    /// </summary>
    private async Task SuggestReassignAsync(LocationInfo loc)
    {
        try
        {
            string key = $"{loc.ServerId}:{loc.TerritoryId}:{loc.WardId}:{loc.HouseId}:{loc.DivisionId}:{loc.RoomId}";
            if (!_reassignHintShown.Add(key)) return;

            var orphans = _store.ScenesNotAtLocation(loc).Where(s => s.Entries.Count > 0).ToList();
            if (orphans.Count == 0) return;

            // Ne rien suggérer chez les autres : la réattribution n'a de sens que dans son propre logement.
            if (!await _dalamudUtil.RunOnFrameworkThread(() => _dalamudUtil.OwnsCurrentHouse()).ConfigureAwait(false)) return;

            // Un plan différent invaliderait les coordonnées : on ne pousse la suggestion que si
            // au moins une scène est géométriquement compatible, ou d'origine inconnue.
            if (_currentInteriorTerritoryId != 0
                && orphans.All(s => s.InteriorTerritoryId != 0 && s.InteriorTerritoryId != _currentInteriorTerritoryId))
                return;

            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"),
                string.Format(Loc.Get("HousingNpc.Notif.OrphanScenes"), orphans.Count), NotificationType.Info,
                TimeSpan.FromSeconds(12)));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Suggestion de réattribution échouée");
        }
    }

    
    
    public async Task ApplySharedSceneAsync(HousingNpcScenario scene)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DespawnSharedInternalAsync().ConfigureAwait(false);
            foreach (var entry in scene.Entries)
            {
                entry.MigrateLegacyToActions();
                await SpawnEntryInternalAsync(entry, scene.Id, shared: true).ConfigureAwait(false);
            }
            Logger.LogInformation("Scène partagée appliquée : {Count} PNJ", scene.Entries.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveSharedSceneAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnSharedInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DespawnSharedInternalAsync()
    {
        List<SpawnedNpc> shared;
        lock (_stateLock)
        {
            shared = _spawned.Where(s => s.Shared).ToList();
            if (shared.Count == 0) return;
            _spawned.RemoveAll(s => s.Shared);
            foreach (var npc in shared)
            {
                _runtimes.Remove(npc.Address);
                _anchors.Remove(npc.Address);
            }
        }

        foreach (var npc in shared)
        {
            try
            {
                if (npc.Live != null) await _liveAppearance.RevertAsync(npc.Live).ConfigureAwait(false);
                await _dalamudUtil.RunOnFrameworkThread(() => _spawner.Despawn(npc.Address)).ConfigureAwait(false);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "Despawn PNJ partagé échoué ({Addr:X})", npc.Address); }
        }
    }

    private async Task SpawnEntryInternalAsync(HousingNpcEntry entry, string groupId, bool shared = false)
    {
        var name = MakeNpcName(_nameSeq++);
        var pos = new System.Numerics.Vector3(entry.X, entry.Y, entry.Z);
        bool hasLive = entry.LiveData != null;

        // Avec du live data, le draw est différé : la collection Penumbra doit être en place avant
        // que le jeu ne charge le moindre modèle, sinon les pièces chargées entre-temps restent en
        // vanilla (de façon aléatoire — c'était le bug de la coupe de cheveux qui manquait).
        var actor = await _dalamudUtil.RunOnFrameworkThread(
            () => _spawner.Spawn(name, pos, entry.Rotation, entry.Appearance, 0, deferDraw: hasLive)).ConfigureAwait(false);
        if (actor == null) return;

        NpcLiveHandle? live = null;
        if (hasLive)
        {
            live = await _liveAppearance.PrepareCollectionAsync(actor.Address, entry.LiveData!).ConfigureAwait(false);
            
            try
            {
                // Le draw ne démarre qu'ici, collection déjà assignée. Si la préparation a échoué, on
                // dessine quand même : mieux vaut un PNJ en apparence brute qu'un acteur invisible.
                await _dalamudUtil.RunOnFrameworkThread(
                    () => _spawner.BeginDraw(actor.Address, entry.Appearance)).ConfigureAwait(false);

                await WaitUntilRenderedAsync(actor.Address).ConfigureAwait(false);

                if (live != null)
                    await _liveAppearance.FinalizeAsync(live, entry.LiveData!, CancellationToken.None).ConfigureAwait(false);

                await _dalamudUtil.RunOnFrameworkThread(
                    () => NativeNpcSpawner.ApplyDisplayFlags(actor.Address, entry.Appearance)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Spawn du PNJ interrompu, nettoyage de l'acteur et de sa collection");
                try
                {
                    if (live != null) await _liveAppearance.RevertAsync(live).ConfigureAwait(false);
                    await _dalamudUtil.RunOnFrameworkThread(() => _spawner.Despawn(actor.Address)).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    Logger.LogWarning(cleanupEx, "Nettoyage après spawn interrompu incomplet");
                }
                // On abandonne ce PNJ sans propager : les autres entrées de la scène doivent aboutir.
                return;
            }
        }

        lock (_stateLock)
        {
            _spawned.Add(new SpawnedNpc(actor.Address, live, shared, entry.Id));
            _anchors[actor.Address] = new Anchor { Pos = pos, Rot = entry.Rotation };
        }

        if (entry.FacePlayer)
        {
            await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                var p = _dalamudUtil.GetPlayerPointer();
                if (p != nint.Zero) _lookAt.LookAt(actor.Address, p);
            }).ConfigureAwait(false);
        }

        if (entry.Actions.Count > 0)
        {
            var runtime = new ActionRuntime
            {
                Actions = entry.Actions.ToArray(),
                GroupId = groupId,
                Looping = entry.Looping,
                LoopDelay = entry.LoopDelay,
                WaitLeft = 1f,
            };
            lock (_stateLock) _runtimes[actor.Address] = runtime;
        }
    }
    
    private async Task WaitUntilRenderedAsync(nint address, int timeoutMs = 6000)
    {
        const int stepMs = 100;
        for (int elapsed = 0; elapsed < timeoutMs; elapsed += stepMs)
        {
            bool rendered = await _dalamudUtil.RunOnFrameworkThread(
                () => _spawner.IsAlive(address) && NativeNpcSpawner.HasDrawObject(address)).ConfigureAwait(false);
            if (rendered) return;
            await Task.Delay(stepMs).ConfigureAwait(false);
        }
        Logger.LogWarning("PNJ {Addr:X} non rendu après {Timeout}ms — apparence appliquée malgré tout", address, timeoutMs);
    }

    private async Task DespawnAllInternalAsync()
    {
        _lookAt.Clear();
        List<SpawnedNpc> spawned;
        lock (_stateLock)
        {
            _runtimes.Clear();
            _anchors.Clear();
            if (_spawned.Count == 0) return;
            spawned = _spawned.ToList();
            _spawned.Clear();
        }

        foreach (var npc in spawned)
        {
            try
            {
                if (npc.Live != null) await _liveAppearance.RevertAsync(npc.Live).ConfigureAwait(false);
                await _dalamudUtil.RunOnFrameworkThread(() => _spawner.Despawn(npc.Address)).ConfigureAwait(false);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "Despawn PNJ échoué ({Addr:X})", npc.Address); }
        }
    }

    private void ForgetSpawned()
    {
        _lookAt.Clear();
        List<SpawnedNpc> spawned;
        lock (_stateLock)
        {
            _runtimes.Clear();
            _anchors.Clear();
            spawned = _spawned.ToList();
            _spawned.Clear();
        }

        foreach (var npc in spawned)
        {
            if (npc.Live == null) continue;
            npc.Live.DisposeHandler();
            RevertLiveSafe(npc.Live);
        }
    }

    // Le revert n'est pas attendu (appelé depuis le framework thread), mais ne doit pas
    // remonter une exception non observée.
    private void RevertLiveSafe(NpcLiveHandle live)
    {
        _ = Task.Run(async () =>
        {
            try { await _liveAppearance.RevertAsync(live).ConfigureAwait(false); }
            catch (Exception ex) { Logger.LogWarning(ex, "Revert de l'apparence live échoué"); }
        });
    }

    // Appelé sous _stateLock depuis OnFrameworkUpdate.
    private void PruneDeadActors()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            var npc = _spawned[i];
            if (_spawner.IsAlive(npc.Address)) continue;

            _runtimes.Remove(npc.Address);
            _anchors.Remove(npc.Address);
            _spawned.RemoveAt(i);
            if (npc.Live != null)
            {
                npc.Live.DisposeHandler();
                RevertLiveSafe(npc.Live);
            }
            Logger.LogWarning("PNJ {Addr:X} disparu (acteur libéré par le jeu), état nettoyé", npc.Address);
        }
    }

    // ---- Moteur de séquence d'actions (avancé chaque frame) ----

    private void OnFrameworkUpdate()
    {
        float dt = (float)_moveClock.Elapsed.TotalSeconds;
        _moveClock.Restart();

        // Snapshots sous verrou : un spawn/despawn concurrent (thread pool) muterait les collections
        // en pleine itération. Le travail lui-même se fait hors verrou.
        KeyValuePair<nint, ActionRuntime>[] runtimes;
        lock (_stateLock)
        {
            if (_spawned.Count == 0) return;
            PruneDeadActors();
            if (_spawned.Count == 0) return;
            runtimes = _runtimes.ToArray();
        }

        if (runtimes.Length > 0 && dt > 0f && dt <= 0.5f)
        {
            foreach (var kv in runtimes)
            {
                try { AdvanceActions(kv.Key, kv.Value, dt); }
                catch (Exception ex) { Logger.LogWarning(ex, "Avance de séquence PNJ échouée"); }
            }
            ReleaseSyncBarriers(runtimes);
        }

        // Snapshot pris APRÈS l'avance : les actions viennent de mettre les ancres à jour, les
        // ré-imposer depuis un état antérieur annulerait le déplacement de la frame.
        (nint Address, System.Numerics.Vector3 Pos, float Rot)[] anchors;
        lock (_stateLock)
        {
            anchors = _spawned
                .Where(n => _anchors.ContainsKey(n.Address))
                .Select(n => (n.Address, _anchors[n.Address].Pos, _anchors[n.Address].Rot))
                .ToArray();
        }

        foreach (var (address, pos, rot) in anchors)
            NativeNpcSpawner.SetTransform(address, pos, rot);
    }

    private static void ReleaseSyncBarriers(KeyValuePair<nint, ActionRuntime>[] runtimes)
    {
        foreach (var group in runtimes.Select(kv => kv.Value).GroupBy(r => r.GroupId, StringComparer.Ordinal))
        {
            var active = group.Where(r => !r.Finished).ToList();
            if (active.Count == 0 || !active.TrueForAll(r => r.AtSync)) continue;

            foreach (var rt in active)
            {
                rt.AtSync = false;
                Advance(rt);
            }
        }
    }

    private void AdvanceActions(nint addr, ActionRuntime rt, float dt)
    {
        if (rt.Actions.Length == 0 || rt.Finished) return;
        if (rt.Index >= rt.Actions.Length)
        {
            if (!rt.Looping) { rt.Finished = true; NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle); return; }
            rt.Index = 0;
            rt.WaitLeft = rt.LoopDelay;
            return;
        }

        if (rt.WaitLeft > 0f) { rt.WaitLeft -= dt; return; }

        // Rendez-vous atteint : on attend les autres PNJ de la scène (libération dans ReleaseSyncBarriers).
        if (rt.AtSync)
        {
            rt.SyncTimeout -= dt;
            if (rt.SyncTimeout <= 0f) { rt.AtSync = false; Advance(rt); } // garde-fou : un PNJ ne se présente jamais
            return;
        }

        if (rt.AwaitingEmote)
        {
            rt.EmoteGrace -= dt;
            rt.EmoteElapsed += dt;
            if (rt.EmoteGrace > 0f) return;
            if (rt.EmoteDuration > 0f)
            {
                if (rt.EmoteLoopThis && !NativeNpcSpawner.IsEmoting(addr)) NativeNpcSpawner.PlayEmote(addr, rt.CurrentEmote);
                if (rt.EmoteElapsed < rt.EmoteDuration) return;
            }
            else
            {
                if (NativeNpcSpawner.IsEmoting(addr)) return;
                if (rt.EmoteLoopThis) { NativeNpcSpawner.PlayEmote(addr, rt.CurrentEmote); return; }
            }

            rt.AwaitingEmote = false;
            rt.WaitLeft = PostEmotePause; 
            Advance(rt);
            return;
        }

        var action = rt.Actions[rt.Index];
        if (!action.Enabled) { Advance(rt); return; }

        switch (action)
        {
            case NpcEmoteAction e: StepEmote(addr, rt, e); break;
            case NpcMovementAction m: StepMove(addr, rt, m.X, m.Y, m.Z, SpeedOf(m.Speed, m.CustomSpeed), AnimOf(m.Speed), dt); break;
            case NpcPathAction p: StepPath(addr, rt, p, dt); break;
            case NpcRotationAction r: StepRotation(addr, rt, r.TargetRotation, dt); break;
            case NpcWaitAction w: rt.WaitLeft = w.Duration; NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle); Advance(rt); break;
            case NpcIdleAction: NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle); Advance(rt); break;
            case NpcVisibilityAction v: NativeNpcSpawner.SetVisible(addr, v.Visible); Advance(rt); break;
            case NpcTimelineAction t: StepTimeline(addr, rt, t); break;
            case NpcSyncAction:
                NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle);
                rt.AtSync = true;
                rt.SyncTimeout = SyncTimeoutSeconds;
                break;
            default: Advance(rt); break;
        }
    }

    private static void StepTimeline(nint addr, ActionRuntime rt, NpcTimelineAction t)
    {
        if (t.TimelineIds.Count == 0) { Advance(rt); return; }
        foreach (var id in t.TimelineIds) NativeNpcSpawner.PlayTimeline(addr, id);
        rt.WaitLeft = t.Duration > 0f ? t.Duration : DefaultTimelineDuration;
        Advance(rt);
    }

    private static void StepEmote(nint addr, ActionRuntime rt, NpcEmoteAction e)
    {
        if (e.Emote == 0) { Advance(rt); return; }
        NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle);
        NativeNpcSpawner.PlayEmote(addr, e.Emote);
        rt.AwaitingEmote = true;
        rt.EmoteGrace = 0.5f;
        rt.EmoteElapsed = 0f;
        rt.CurrentEmote = e.Emote;
        rt.EmoteLoopThis = e.Loop;
        rt.EmoteDuration = e.Duration;
    }
    
    private void StepMove(nint addr, ActionRuntime rt, float x, float y, float z, float speed, NativeNpcSpawner.MoveAnim anim, float dt)
    {
        if (MoveToward(addr, new System.Numerics.Vector3(x, y, z), speed, anim, dt))
        {
            NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle);
            Advance(rt);
        }
    }

    private void StepPath(nint addr, ActionRuntime rt, NpcPathAction p, float dt)
    {
        if (p.Points.Count == 0) { Advance(rt); return; }
        if (rt.PathIndex >= p.Points.Count) { NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle); Advance(rt); return; }

        var pt = p.Points[rt.PathIndex];
        if (MoveToward(addr, new System.Numerics.Vector3(pt.X, pt.Y, pt.Z), SpeedOf(pt.Speed, pt.CustomSpeed), AnimOf(pt.Speed), dt))
            rt.PathIndex++;
    }

    private void StepRotation(nint addr, ActionRuntime rt, float targetRot, float dt)
    {
        float curRot = NativeNpcSpawner.GetRotation(addr);
        if (!AngleClose(curRot, targetRot, 0.06f))
        {
            Move(addr, NativeNpcSpawner.GetPosition(addr), RotateToward(curRot, targetRot, TurnSpeed * dt));
            return;
        }
        Move(addr, NativeNpcSpawner.GetPosition(addr), targetRot);
        Advance(rt);
    }

    // Avance d'un pas vers la cible. Tourne d'abord vers elle, puis translate. True quand arrivé.
    private bool MoveToward(nint addr, System.Numerics.Vector3 target, float speed, NativeNpcSpawner.MoveAnim anim, float dt)
    {
        var cur = NativeNpcSpawner.GetPosition(addr);
        float targetRot = MathF.Atan2(target.X - cur.X, target.Z - cur.Z);
        float curRot = NativeNpcSpawner.GetRotation(addr);

        if (!AngleClose(curRot, targetRot, 0.06f))
        {
            NativeNpcSpawner.SetMovementAnim(addr, anim);
            Move(addr, cur, RotateToward(curRot, targetRot, TurnSpeed * dt));
            return false;
        }

        float dist = System.Numerics.Vector3.Distance(cur, target);
        if (dist >= ArriveEps)
        {
            NativeNpcSpawner.SetMovementAnim(addr, anim);
            float t = MathF.Min(speed * dt / dist, 1f);
            var newPos = System.Numerics.Vector3.Lerp(cur, target, t);
            Move(addr, newPos, targetRot);
            if (System.Numerics.Vector3.Distance(newPos, target) >= ArriveEps) return false;
        }
        Move(addr, target, targetRot);
        return true;
    }

    private static float SpeedOf(NpcMoveSpeed s, float custom) => s switch
    {
        NpcMoveSpeed.Run => RunSpeed,
        NpcMoveSpeed.Custom => custom > 0f ? custom : WalkSpeed,
        _ => WalkSpeed,
    };

    private static NativeNpcSpawner.MoveAnim AnimOf(NpcMoveSpeed s)
        => s == NpcMoveSpeed.Run ? NativeNpcSpawner.MoveAnim.Running : NativeNpcSpawner.MoveAnim.Walking;

    private static void Advance(ActionRuntime rt)
    {
        rt.Index++;
        rt.PathIndex = 0;
        rt.AwaitingEmote = false;
        rt.EmoteElapsed = 0f;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    private static bool AngleClose(float a, float b, float eps) => MathF.Abs(WrapAngle(a - b)) < eps;

    private static float RotateToward(float from, float to, float step)
    {
        float diff = WrapAngle(to - from);
        return MathF.Abs(diff) <= step ? to : from + (MathF.Sign(diff) * step);
    }

    public Task AddFromSelfAsync(string sceneId, string displayName) => AddCapturedAsync(sceneId, displayName, includeLive: false);
    public Task AddFromSelfLiveAsync(string sceneId, string displayName) => AddCapturedAsync(sceneId, displayName, includeLive: true);
    public IReadOnlyList<(string Path, string Title)> ListArrScenarios()
        => _arrScenarioFiles.ListLocalScenarios()
            .Select(f => (f.FilePath, string.IsNullOrWhiteSpace(f.Title) ? Path.GetFileNameWithoutExtension(f.FilePath) : f.Title))
            .ToList();

    public async Task ImportArrScenarioAsync(string path)
    {
        try
        {
            if (_currentLocation is not { } loc)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedHousing"), NotificationType.Error));
                return;
            }

            var parsed = ArrScenarioImporter.Parse(path);
            var title = string.IsNullOrWhiteSpace(parsed.Title) ? Path.GetFileNameWithoutExtension(path) : parsed.Title;
            var scene = _store.CreateScene(loc, title, _currentInteriorTerritoryId);

            foreach (var npc in parsed.Npcs)
            {
                scene.Entries.Add(new HousingNpcEntry
                {
                    DisplayName = npc.Name,
                    Appearance = npc.Appearance,
                    X = npc.X,
                    Y = npc.Y,
                    Z = npc.Z,
                    Rotation = npc.Rotation,
                    FacePlayer = npc.FacePlayer,
                    Actions = npc.Actions,
                    Looping = parsed.Looping,
                    LoopDelay = parsed.LoopDelay,
                });
            }
            _store.SaveChanges();
            await RefreshAsync().ConfigureAwait(false);

            Logger.LogInformation("Import ARR : {Npcs} PNJ, {Skipped} action(s) ignorée(s)", parsed.Npcs.Count, parsed.SkippedActions);
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"),
                string.Format(Loc.Get("HousingNpc.Notif.ArrImported"), parsed.Npcs.Count), NotificationType.Info));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Import du scénario ARR échoué");
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.ImportFailed"), NotificationType.Error));
        }
    }

    // Importe un .chara (Anamnesis/Brio/Ktisis) et spawne le PNJ à la position du joueur.
    public async Task AddFromCharaFileAsync(string sceneId, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(sceneId) || _store.GetScene(sceneId) == null)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedScene"), NotificationType.Error));
                return;
            }

            var loc = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
            if (loc.HouseId == 0)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedHousing"), NotificationType.Error));
                return;
            }

            var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (player == null) return;

            var (appearance, nickname) = AnamnesisCharaImporter.Parse(path);

            var entry = new HousingNpcEntry
            {
                DisplayName = string.IsNullOrWhiteSpace(nickname) ? Path.GetFileNameWithoutExtension(path) : nickname,
                Appearance = appearance,
                X = player.Position.X,
                Y = player.Position.Y,
                Z = player.Position.Z,
                Rotation = player.Rotation,
            };
            _store.AddEntryToScene(sceneId, entry);

            await _gate.WaitAsync().ConfigureAwait(false);
            try { await SpawnEntryInternalAsync(entry, sceneId).ConfigureAwait(false); }
            finally { _gate.Release(); }

            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.Added"), NotificationType.Info));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Import .chara échoué");
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.ImportFailed"), NotificationType.Error));
        }
    }

    // Commande debug /usync npcadd : crée/réutilise une scène par défaut et y capture le joueur.
    private async Task DebugAddFromSelfAsync()
    {
        var loc = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
        if (loc.HouseId == 0)
        {
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedHousing"), NotificationType.Error));
            return;
        }
        var sceneId = _store.ScenesForLocation(loc).FirstOrDefault()?.Id
            ?? _store.CreateScene(loc, "Scène", _currentInteriorTerritoryId).Id;
        await AddFromSelfAsync(sceneId, string.Empty).ConfigureAwait(false);
    }

    // Liste des designs Glamourer (pour l'UI « Capturer depuis Glamourer »).
    public Task<List<(Guid Id, string Name)>> GetGlamourerDesignsAsync() => _liveAppearance.GetDesignsAsync();

    // Capture un PNJ dont l'apparence provient d'un design Glamourer (mods du perso conservés).
    public Task AddFromGlamourerDesignAsync(string sceneId, Guid designId, string designName)
        => AddCapturedAsync(sceneId, string.Empty, includeLive: true, (designId, designName));

    private async Task AddCapturedAsync(string sceneId, string displayName, bool includeLive, (Guid Id, string Name)? glamourerDesign = null)
    {
        try
        {
            if (string.IsNullOrEmpty(sceneId) || _store.GetScene(sceneId) == null)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedScene"), NotificationType.Error));
                return;
            }

            var loc = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
            if (loc.HouseId == 0)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NeedHousing"), NotificationType.Error));
                return;
            }

            var player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
            if (player == null) return;

            var sourceAddr = await _dalamudUtil.RunOnFrameworkThread(_dalamudUtil.GetPlayerPointer).ConfigureAwait(false);
            if (sourceAddr == nint.Zero)
            {
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NoPlayer"), NotificationType.Error));
                return;
            }

            // Apparence brute = base du modèle (race, corps). Le design Glamourer est réappliqué
            // par-dessus via le CharacterData : DrawData contient l'équipement RÉEL, pas ce que
            // Glamourer affiche, donc l'apparence brute seule ne suffit pas.
            var appearance = await _dalamudUtil.RunOnFrameworkThread(() => NativeNpcSpawner.ReadAppearance(sourceAddr)).ConfigureAwait(false);

            CharacterData? liveData;
            if (glamourerDesign.HasValue)
            {
                var (designData, designAppearance) = await _liveAppearance.CaptureDesignOnSelfAsync(glamourerDesign.Value.Id).ConfigureAwait(false);
                liveData = designData;
                if (liveData == null)
                {
                    Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.DesignFailed"), NotificationType.Error));
                    return;
                }
                // Le design fait autorité sur les états d'affichage : sans ça, un design qui masque
                // l'arme se voyait rendu avec l'arme du personnage au moment de la capture.
                if (designAppearance != null) appearance = designAppearance;
            }
            else
            {
                liveData = null;
                var live = await _liveAppearance.CaptureSelfAsync().ConfigureAwait(false);
                if (live == null)
                    Logger.LogWarning("Capture du live data échouée, apparence brute seule conservée");
                else
                    liveData = includeLive ? live : WithoutMods(live);
                Logger.LogInformation("Ajout PNJ : capture {Mode}", includeLive ? "AVEC les mods" : "SANS les mods");
            }

            var defaultName = glamourerDesign?.Name ?? player.Name.TextValue;
            var entry = new HousingNpcEntry
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? defaultName : displayName,
                Appearance = appearance,
                LiveData = liveData,
                X = player.Position.X,
                Y = player.Position.Y,
                Z = player.Position.Z,
                Rotation = player.Rotation,
            };
            _store.AddEntryToScene(sceneId, entry);

            await _gate.WaitAsync().ConfigureAwait(false);
            try { await SpawnEntryInternalAsync(entry, sceneId).ConfigureAwait(false); }
            finally { _gate.Release(); }

            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.Added"), NotificationType.Info));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Capture PNJ housing échouée");
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.AddFailed"), NotificationType.Error));
        }
    }

    private async Task WipeCurrentRoomAsync()
    {
        var loc = _currentLocation;
        if (loc == null)
        {
            Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), Loc.Get("HousingNpc.Notif.NothingToClear"), NotificationType.Info));
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnAllInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }

        int removed = _store.ClearForLocation(loc.Value);
        Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), string.Format(Loc.Get("HousingNpc.Notif.RoomCleared"), removed), NotificationType.Info));
    }

    protected override void Dispose(bool disposing)
    {
        // Le despawn a lieu dans StopAsync ; ici on ne fait que relâcher l'état résiduel si le
        // service a été disposé sans passer par l'arrêt du host.
        if (disposing)
        {
            lock (_stateLock)
            {
                _spawned.Clear();
                _runtimes.Clear();
                _anchors.Clear();
            }
            // _gate n'est volontairement pas disposé : le service est disposé deux fois (Dalamud +
            // arrêt du IHost) et une opération encore en vol ferait un WaitAsync sur un sémaphore mort.
        }
        base.Dispose(disposing);
    }
}
