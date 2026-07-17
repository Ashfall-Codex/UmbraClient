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
    private readonly ITargetManager _targetManager;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SpawnedNpc> _spawned = new();
    private LocationInfo? _currentLocation;
    private int _nameSeq;

    private readonly Dictionary<nint, ActionRuntime> _runtimes = new();
    private readonly System.Diagnostics.Stopwatch _moveClock = System.Diagnostics.Stopwatch.StartNew();

    private const float WalkSpeed = 2.5f;   // y/s
    private const float RunSpeed = 6.3f;    // y/s
    private const float TurnSpeed = 6.3f;   // rad/s
    private const float ArriveEps = 0.05f;
    private const float PostEmotePause = 1f;        // délai par défaut après la fin d'une emote
    private const float DefaultTimelineDuration = 3f; // durée par défaut d'une action Timeline
    private const float SyncTimeoutSeconds = 30f;    // garde-fou de la barrière Sync

    private sealed record SpawnedNpc(nint Address, NpcLiveHandle? Live, bool Shared);

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
        DalamudUtilService dalamudUtil, ITargetManager targetManager)
        : base(logger, mediator)
    {
        _store = store;
        _spawner = spawner;
        _lookAt = lookAt;
        _liveAppearance = liveAppearance;
        _arrScenarioFiles = arrScenarioFiles;
        _dalamudUtil = dalamudUtil;
        _targetManager = targetManager;

        Mediator.Subscribe<HousingPlotEnteredMessage>(this, msg => { _ = OnEnteredAsync(msg.LocationInfo); });
        Mediator.Subscribe<HousingPlotLeftMessage>(this, m => { _ = OnLeftAsync(); });
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => ForgetSpawned());
        Mediator.Subscribe<HousingNpcAddRequestMessage>(this, m => { _ = DebugAddFromSelfAsync(); });
        Mediator.Subscribe<HousingNpcWipeMessage>(this, m => { _ = WipeCurrentRoomAsync(); });
        Mediator.Subscribe<FrameworkUpdateMessage>(this, m => OnFrameworkUpdate());
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public LocationInfo? CurrentLocation => _currentLocation;

    public List<HousingNpcScenario> ScenesForCurrentRoom()
        => _currentLocation is { } loc ? _store.ScenesForLocation(loc) : new();

    public Task RefreshAsync()
        => _currentLocation is { } loc ? OnEnteredAsync(loc) : Task.CompletedTask;

    public async Task<string?> CreateSceneAsync(string title)
    {
        if (_currentLocation is not { } loc) return null;
        var scene = _store.CreateScene(loc, title);
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DespawnAllInternalAsync().ConfigureAwait(false);

            var scenes = _store.ScenesForLocation(loc).Where(s => s.Enabled).ToList();
            int total = scenes.Sum(s => s.Entries.Count);
            if (total == 0) return;

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
    }

    private async Task OnLeftAsync()
    {
        _currentLocation = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DespawnAllInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
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
        var shared = _spawned.Where(s => s.Shared).ToList();
        if (shared.Count == 0) return;
        _spawned.RemoveAll(s => s.Shared);
        foreach (var npc in shared)
        {
            try
            {
                _runtimes.Remove(npc.Address);
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
        var actor = await _dalamudUtil.RunOnFrameworkThread(
            () => _spawner.Spawn(name, pos, entry.Rotation, entry.Appearance, 0)).ConfigureAwait(false);
        if (actor == null) return;

        NpcLiveHandle? live = null;
        if (entry.LiveData != null)
            live = await _liveAppearance.ApplyAsync(actor.Address, entry.LiveData, CancellationToken.None).ConfigureAwait(false);

        _spawned.Add(new SpawnedNpc(actor.Address, live, shared));

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
            _runtimes[actor.Address] = new ActionRuntime
            {
                Actions = entry.Actions.ToArray(),
                GroupId = groupId,
                Looping = entry.Looping,
                LoopDelay = entry.LoopDelay,
                WaitLeft = 1f,
            };
        }
    }

    private async Task DespawnAllInternalAsync()
    {
        _lookAt.Clear();
        _runtimes.Clear();
        if (_spawned.Count == 0) return;
        var spawned = _spawned.ToList();
        _spawned.Clear();
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
        _runtimes.Clear();
        foreach (var npc in _spawned)
            if (npc.Live != null) _ = _liveAppearance.RevertAsync(npc.Live);
        _spawned.Clear();
    }

    // ---- Moteur de séquence d'actions (avancé chaque frame) ----

    private void OnFrameworkUpdate()
    {
        if (_runtimes.Count == 0) { _moveClock.Restart(); return; }
        float dt = (float)_moveClock.Elapsed.TotalSeconds;
        _moveClock.Restart();
        if (dt <= 0f || dt > 0.5f) return;

        foreach (var kv in _runtimes.ToArray())
        {
            try { AdvanceActions(kv.Key, kv.Value, dt); }
            catch (Exception ex) { Logger.LogWarning(ex, "Avance de séquence PNJ échouée"); }
        }

        ReleaseSyncBarriers();
    }

    private void ReleaseSyncBarriers()
    {
        foreach (var group in _runtimes.Values.GroupBy(r => r.GroupId, StringComparer.Ordinal))
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

    private static void AdvanceActions(nint addr, ActionRuntime rt, float dt)
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
    
    private static void StepMove(nint addr, ActionRuntime rt, float x, float y, float z, float speed, NativeNpcSpawner.MoveAnim anim, float dt)
    {
        if (MoveToward(addr, new System.Numerics.Vector3(x, y, z), speed, anim, dt))
        {
            NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle);
            Advance(rt);
        }
    }

    private static void StepPath(nint addr, ActionRuntime rt, NpcPathAction p, float dt)
    {
        if (p.Points.Count == 0) { Advance(rt); return; }
        if (rt.PathIndex >= p.Points.Count) { NativeNpcSpawner.SetMovementAnim(addr, NativeNpcSpawner.MoveAnim.Idle); Advance(rt); return; }

        var pt = p.Points[rt.PathIndex];
        if (MoveToward(addr, new System.Numerics.Vector3(pt.X, pt.Y, pt.Z), SpeedOf(pt.Speed, pt.CustomSpeed), AnimOf(pt.Speed), dt))
            rt.PathIndex++;
    }

    private static void StepRotation(nint addr, ActionRuntime rt, float targetRot, float dt)
    {
        float curRot = NativeNpcSpawner.GetRotation(addr);
        if (!AngleClose(curRot, targetRot, 0.06f))
        {
            NativeNpcSpawner.SetTransform(addr, NativeNpcSpawner.GetPosition(addr), RotateToward(curRot, targetRot, TurnSpeed * dt));
            return;
        }
        NativeNpcSpawner.SetTransform(addr, NativeNpcSpawner.GetPosition(addr), targetRot);
        Advance(rt);
    }

    // Avance d'un pas vers la cible. Tourne d'abord vers elle, puis translate. True quand arrivé.
    private static bool MoveToward(nint addr, System.Numerics.Vector3 target, float speed, NativeNpcSpawner.MoveAnim anim, float dt)
    {
        var cur = NativeNpcSpawner.GetPosition(addr);
        float targetRot = MathF.Atan2(target.X - cur.X, target.Z - cur.Z);
        float curRot = NativeNpcSpawner.GetRotation(addr);

        if (!AngleClose(curRot, targetRot, 0.06f))
        {
            NativeNpcSpawner.SetMovementAnim(addr, anim);
            NativeNpcSpawner.SetTransform(addr, cur, RotateToward(curRot, targetRot, TurnSpeed * dt));
            return false;
        }

        float dist = System.Numerics.Vector3.Distance(cur, target);
        if (dist >= ArriveEps)
        {
            NativeNpcSpawner.SetMovementAnim(addr, anim);
            float t = MathF.Min(speed * dt / dist, 1f);
            var newPos = System.Numerics.Vector3.Lerp(cur, target, t);
            NativeNpcSpawner.SetTransform(addr, newPos, targetRot);
            if (System.Numerics.Vector3.Distance(newPos, target) >= ArriveEps) return false;
        }
        NativeNpcSpawner.SetTransform(addr, target, targetRot);
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

    public Task AddFromSelfAsync(string sceneId, string displayName) => AddCapturedAsync(sceneId, displayName, fromTarget: false, includeLive: false);
    public Task AddFromTargetAsync(string sceneId, string displayName) => AddCapturedAsync(sceneId, displayName, fromTarget: true, includeLive: false);
    public Task AddFromSelfLiveAsync(string sceneId, string displayName) => AddCapturedAsync(sceneId, displayName, fromTarget: false, includeLive: true);
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
            var scene = _store.CreateScene(loc, title);

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
        var sceneId = _store.ScenesForLocation(loc).FirstOrDefault()?.Id ?? _store.CreateScene(loc, "Scène").Id;
        await AddFromSelfAsync(sceneId, string.Empty).ConfigureAwait(false);
    }

    private async Task AddCapturedAsync(string sceneId, string displayName, bool fromTarget, bool includeLive)
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
                Mediator.Publish(new NotificationMessage(Loc.Get("HousingNpc.Notif.Title"), fromTarget ? Loc.Get("HousingNpc.Notif.NoTarget") : Loc.Get("HousingNpc.Notif.NoPlayer"), NotificationType.Error));
                return;
            }

            var appearance = await _dalamudUtil.RunOnFrameworkThread(() => _spawner.ReadAppearance(sourceAddr)).ConfigureAwait(false);

            CharacterData? liveData = null;
            if (includeLive)
            {
                liveData = await _liveAppearance.CaptureSelfAsync().ConfigureAwait(false);
                if (liveData == null)
                    Logger.LogWarning("Capture du live data échouée, apparence brute seule conservée");
            }

            var entry = new HousingNpcEntry
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? sourceName : displayName,
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
        if (disposing)
        {
            _ = DespawnAllInternalAsync();
        }
        base.Dispose(disposing);
    }
}
