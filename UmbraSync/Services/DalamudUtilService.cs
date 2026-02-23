using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Interop;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using DalamudGameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
#pragma warning disable CS8500 // required pointer interop with Dalamud/FFXIV structs

namespace UmbraSync.Services;

public class DalamudUtilService : IHostedService, IMediatorSubscriber
{
    public struct PlayerCharacter
    {
        public uint ObjectId { get; set; }
        public string Name { get; set; }
        public uint HomeWorldId { get; set; }
        public nint Address { get; set; }
    };

    private sealed class PlayerInfo
    {
        public PlayerCharacter Character { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    private readonly List<uint> _classJobIdsIgnoredForPets = [30];
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IDataManager _gameData;
    private readonly BlockedCharacterHandler _blockedCharacterHandler;
    private readonly IFramework _framework;
    private readonly IGameGui _gameGui;
    private readonly IToastGui _toastGui;
    private readonly ILogger<DalamudUtilService> _logger;
    private readonly IObjectTable _objectTable;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly Dictionary<string, ConditionFlag> _conditionLookup = new(StringComparer.OrdinalIgnoreCase);
    private uint? _classJobId;
    private DateTime _delayedFrameworkUpdateCheck = DateTime.UtcNow;
    private string _lastGlobalBlockPlayer = string.Empty;
    private string _lastGlobalBlockReason = string.Empty;
    private ushort _lastZone;
    private readonly Dictionary<string, PlayerCharacter> _playerCharas = new(StringComparer.Ordinal);
    private readonly List<string> _notUpdatedCharas = [];
    private bool _sentBetweenAreas;
    private static readonly Dictionary<uint, PlayerInfo> _playerInfoCache = new();

    // Prefer IObjectTable.LocalPlayer when available (newer Dalamud),
    // otherwise fall back to obsolete IClientState.LocalPlayer (suppressed) for compatibility.
    private IPlayerCharacter? LocalPlayer => _objectTable.LocalPlayer;


    public DalamudUtilService(ILogger<DalamudUtilService> logger, IClientState clientState, IObjectTable objectTable, IFramework framework,
        IGameGui gameGui, IToastGui toastGui, ICondition condition, IDataManager gameData, ITargetManager targetManager,
        BlockedCharacterHandler blockedCharacterHandler, MareMediator mediator, PerformanceCollectorService performanceCollector)
    {
        _logger = logger;
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;
        _gameGui = gameGui;
        _toastGui = toastGui;
        _condition = condition;
        _gameData = gameData;
        _blockedCharacterHandler = blockedCharacterHandler;
        Mediator = mediator;
        _performanceCollector = performanceCollector;
        WorldData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.World>(Dalamud.Game.ClientLanguage.English)!
                .Where(w => w.Name.ByteLength > 0 && w.DataCenter.RowId != 0 && (w.IsPublic || char.IsUpper((char)w.Name.Data.Span[0])))
                .ToDictionary(w => (ushort)w.RowId, w => w.Name.ToString());
        });
        UiColors = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.UIColor>(Dalamud.Game.ClientLanguage.English)!
                .Where(x => x.RowId != 0 && !(x.RowId >= 500 && (x.Dark & 0xFFFFFF00) == 0))
                .ToDictionary(x => (int)x.RowId);
        });
        TerritoryData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>(Dalamud.Game.ClientLanguage.English)!
            .Where(w => w.RowId != 0)
            .ToDictionary(w => w.RowId, w =>
            {
                StringBuilder sb = new();
                sb.Append(w.PlaceNameRegion.Value.Name);
                if (w.PlaceName.ValueNullable != null)
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceName.Value.Name);
                }
                return sb.ToString();
            });
        });
        MapData = new(() =>
        {
            return gameData.GetExcelSheet<Lumina.Excel.Sheets.Map>(Dalamud.Game.ClientLanguage.English)!
            .Where(w => w.RowId != 0)
            .ToDictionary(w => w.RowId, w =>
            {
                StringBuilder sb = new();
                sb.Append(w.PlaceNameRegion.Value.Name);
                if (w.PlaceName.ValueNullable != null)
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceName.Value.Name);
                }
                if (w.PlaceNameSub.ValueNullable != null && !string.IsNullOrEmpty(w.PlaceNameSub.Value.Name.ToString()))
                {
                    sb.Append(" - ");
                    sb.Append(w.PlaceNameSub.Value.Name);
                }
                return (w, sb.ToString());
            });
        });
        mediator.Subscribe<TargetPairMessage>(this, (msg) =>
        {
            if (clientState.IsPvP) return;
            var ident = msg.Pair.GetPlayerNameHash();
            _ = RunOnFrameworkThread(() =>
            {
                var addr = GetPlayerCharacterFromCachedTableByIdent(ident);
                var pc = LocalPlayer!;
                var gobj = CreateGameObject(addr);
                // Any further than roughly 55y is out of range for targetting
                if (gobj != null && Vector3.Distance(pc.Position, gobj.Position) < 55.0f)
                    targetManager.Target = gobj;
                else
                    _toastGui.ShowError("Player out of range.");
            }).ConfigureAwait(false);
        });
        IsWine = Util.IsWine();
    }

    public bool IsWine { get; init; }
    [SuppressMessage("Major Code Smell", "S2325:Use static", Justification = "Exposed via service instance for easier mocking and DI")]
    public unsafe GameObject* GposeTarget
    {
        get => TargetSystem.Instance()->GPoseTarget;
        set => TargetSystem.Instance()->GPoseTarget = value;
    }

    private unsafe bool HasGposeTarget => GposeTarget != null;
    private unsafe int GPoseTargetIdx => !HasGposeTarget ? -1 : GposeTarget->ObjectIndex;

    public async Task<IGameObject?> GetGposeTargetGameObjectAsync()
    {
        if (!HasGposeTarget)
            return null;

        return await _framework.RunOnFrameworkThread(() => _objectTable[GPoseTargetIdx]).ConfigureAwait(true);
    }
    public bool IsAnythingDrawing { get; private set; }
    public bool IsInCutscene { get; private set; }
    public bool IsInDuty { get; private set; }
    public bool IsInGpose { get; private set; }
    public bool IsLoggedIn { get; private set; }
    public bool IsOnFrameworkThread => _framework.IsInFrameworkUpdateThread;
    public bool IsZoning => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
    public bool IsInCombatOrPerforming { get; private set; }
    public bool IsInHousingMode => _condition[ConditionFlag.UsingHousingFunctions];
    public bool HasModifiedGameFiles => _gameData.HasModifiedGameDataFiles;

    private unsafe bool IsInActiveDuty()
    {
        if (!_condition[ConditionFlag.BoundByDuty] && !_condition[ConditionFlag.BoundByDuty56] && !_clientState.IsPvP)
            return false;

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            return true;

        var instanceDirector = eventFramework->GetInstanceContentDirector();
        if (instanceDirector == null)
            return !_condition[ConditionFlag.BoundByDuty95];

        // ContentFlags == 1 soit Tourism / Explorer Mode
        return instanceDirector->ContentFlags != 1;
    }

    public bool IsConditionActive(string flagName)
    {
        if (_conditionLookup.TryGetValue(flagName, out var cachedFlag))
            return _condition[cachedFlag];

        if (Enum.TryParse<ConditionFlag>(flagName, true, out var flag))
        {
            _conditionLookup[flagName] = flag;
            return _condition[flag];
        }

        return false;
    }

    public Lazy<Dictionary<ushort, string>> WorldData { get; private set; }
    public Lazy<Dictionary<int, Lumina.Excel.Sheets.UIColor>> UiColors { get; private set; }
    public Lazy<Dictionary<uint, string>> TerritoryData { get; private set; }
    public Lazy<Dictionary<uint, (Lumina.Excel.Sheets.Map Map, string MapName)>> MapData { get; private set; }

    public MareMediator Mediator { get; }

    public Dalamud.Game.ClientState.Objects.Types.IGameObject? CreateGameObject(IntPtr reference)
    {
        EnsureIsOnFramework();
        return _objectTable.CreateObjectReference(reference);
    }

    public async Task<Dalamud.Game.ClientState.Objects.Types.IGameObject?> CreateGameObjectAsync(IntPtr reference)
    {
        return await RunOnFrameworkThread(() => _objectTable.CreateObjectReference(reference)).ConfigureAwait(false);
    }

    public void EnsureIsOnFramework()
    {
        if (!_framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Can only be run on Framework");
    }

    public Dalamud.Game.ClientState.Objects.Types.ICharacter? GetCharacterFromObjectTableByIndex(int index)
    {
        EnsureIsOnFramework();
        var objTableObj = _objectTable[index];
        if (objTableObj!.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player) return null;
        return (Dalamud.Game.ClientState.Objects.Types.ICharacter)objTableObj;
    }

    public unsafe IntPtr GetCompanion(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero) return IntPtr.Zero;
        return (IntPtr)mgr->LookupBuddyByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetCompanionAsync(IntPtr? playerPointer = null)
    {
        return await RunOnFrameworkThread(() => GetCompanion(playerPointer)).ConfigureAwait(false);
    }

    public async Task<ICharacter?> GetGposeCharacterFromObjectTableByNameAsync(string name, bool onlyGposeCharacters = false)
    {
        return await RunOnFrameworkThread(() => GetGposeCharacterFromObjectTableByName(name, onlyGposeCharacters)).ConfigureAwait(false);
    }

    public ICharacter? GetGposeCharacterFromObjectTableByName(string name, bool onlyGposeCharacters = false)
    {
        EnsureIsOnFramework();
        return (ICharacter?)_objectTable
            .FirstOrDefault(i => (!onlyGposeCharacters || i.ObjectIndex >= 200) && string.Equals(i.Name.ToString(), name, StringComparison.Ordinal));
    }

    public IEnumerable<ICharacter?> GetGposeCharactersFromObjectTable()
    {
        return _objectTable.Where(o => o.ObjectIndex > 200 && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player).Cast<ICharacter>();
    }

    public bool GetIsPlayerPresent()
    {
        EnsureIsOnFramework();
        return LocalPlayer != null && LocalPlayer.IsValid();
    }

    public async Task<bool> GetIsPlayerPresentAsync()
    {
        return await RunOnFrameworkThread(GetIsPlayerPresent).ConfigureAwait(false);
    }

    public unsafe IntPtr GetMinionOrMount(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero) return IntPtr.Zero;
        return _objectTable.GetObjectAddress(((GameObject*)playerPointer)->ObjectIndex + 1);
    }

    public async Task<IntPtr> GetMinionOrMountAsync(IntPtr? playerPointer = null)
    {
        return await RunOnFrameworkThread(() => GetMinionOrMount(playerPointer)).ConfigureAwait(false);
    }

    public unsafe IntPtr GetPet(IntPtr? playerPointer = null)
    {
        EnsureIsOnFramework();
        if (_classJobIdsIgnoredForPets.Contains(_classJobId ?? 0)) return IntPtr.Zero;
        var mgr = CharacterManager.Instance();
        playerPointer ??= GetPlayerPointer();
        if (playerPointer == IntPtr.Zero || (IntPtr)mgr == IntPtr.Zero) return IntPtr.Zero;
        return (IntPtr)mgr->LookupPetByOwnerObject((BattleChara*)playerPointer);
    }

    public async Task<IntPtr> GetPetAsync(IntPtr? playerPointer = null)
    {
        return await RunOnFrameworkThread(() => GetPet(playerPointer)).ConfigureAwait(false);
    }

    public async Task<IPlayerCharacter?> GetPlayerCharacterAsync()
    {
        return await RunOnFrameworkThread(GetPlayerCharacter).ConfigureAwait(false);
    }

    public IPlayerCharacter? GetPlayerCharacter()
    {
        EnsureIsOnFramework();
        return LocalPlayer;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByName(string characterName)
    {
        foreach (var c in _playerCharas.Values)
        {
            if (c.Name.Equals(characterName, StringComparison.Ordinal))
                return c.Address;
        }
        return 0;
    }

    public IntPtr GetPlayerCharacterFromCachedTableByIdent(string characterName)
    {
        if (_playerCharas.TryGetValue(characterName, out var pchar)) return pchar.Address;
        return IntPtr.Zero;
    }

    public bool TryGetWorldIdByIdent(string ident, out ushort worldId)
    {
        if (_playerCharas.TryGetValue(ident, out var pchar))
        {
            worldId = (ushort)pchar.HomeWorldId;
            return true;
        }
        worldId = 0;
        return false;
    }

    public bool TryGetPlayerCharacterByObjectId(uint objectId, out PlayerCharacter playerCharacter)
    {
        foreach (var entry in _playerCharas.Values)
        {
            if (entry.ObjectId == objectId)
            {
                playerCharacter = entry;
                return true;
            }
        }

        playerCharacter = default;
        return false;
    }

    public string GetPlayerName()
    {
        EnsureIsOnFramework();
        return LocalPlayer?.Name.ToString() ?? "--";
    }

    public async Task<string> GetPlayerNameAsync()
    {
        return await RunOnFrameworkThread(GetPlayerName).ConfigureAwait(false);
    }

    public async Task<string> GetPlayerNameHashedAsync()
    {
        return await RunOnFrameworkThread(() => (GetPlayerName() + GetHomeWorldId()).GetHash256()).ConfigureAwait(false);
    }

    public IntPtr GetPlayerPointer()
    {
        EnsureIsOnFramework();
        return LocalPlayer?.Address ?? IntPtr.Zero;
    }

    public async Task<IntPtr> GetPlayerPointerAsync()
    {
        return await RunOnFrameworkThread(GetPlayerPointer).ConfigureAwait(false);
    }

    public uint GetHomeWorldId()
    {
        EnsureIsOnFramework();
        return LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    public uint GetWorldId()
    {
        EnsureIsOnFramework();
        return LocalPlayer?.CurrentWorld.RowId ?? 0;
    }

    public unsafe LocationInfo GetMapData()
    {
        EnsureIsOnFramework();
        var agentMap = AgentMap.Instance();
        var houseMan = HousingManager.Instance();
        var player = LocalPlayer;
        var serverId = player?.CurrentWorld.RowId ?? 0;
        uint mapId = agentMap == null ? 0 : agentMap->CurrentMapId;
        uint territoryId = agentMap == null ? 0 : agentMap->CurrentTerritoryId;
        uint divisionId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentDivision());
        uint wardId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentWard() + 1);
        uint houseId = 0;
        var tempHouseId = houseMan == null ? 0 : (houseMan->GetCurrentPlot() + 1);
        if (!houseMan->IsInside()) tempHouseId = 0;
        if (tempHouseId < 0) // Should not happen with +1 but keep logic
        {
            divisionId = tempHouseId == -126 ? 2 : (uint)1;
            tempHouseId = 101;
        }
        if (tempHouseId == 0) tempHouseId = 0;
        houseId = (uint)tempHouseId;
        if (houseId != 0)
        {
            territoryId = HousingManager.GetOriginalHouseTerritoryTypeId();
        }
        uint roomId = houseMan == null ? 0 : (uint)(houseMan->GetCurrentRoom());

        return new LocationInfo()
        {
            ServerId = serverId,
            MapId = mapId,
            TerritoryId = territoryId,
            DivisionId = divisionId,
            WardId = wardId,
            HouseId = houseId,
            RoomId = roomId
        };
    }

    public unsafe void SetMarkerAndOpenMap(Vector3 position, Map map)
    {
        EnsureIsOnFramework();
        var agentMap = AgentMap.Instance();
        if (agentMap == null) return;
        agentMap->OpenMapByMapId(map.RowId);
        agentMap->SetFlagMapMarker(map.TerritoryType.RowId, map.RowId, position);
    }

    public async Task<LocationInfo> GetMapDataAsync()
    {
        return await RunOnFrameworkThread(GetMapData).ConfigureAwait(false);
    }

    public async Task<uint> GetWorldIdAsync()
    {
        return await RunOnFrameworkThread(GetWorldId).ConfigureAwait(false);
    }

    public async Task<uint> GetHomeWorldIdAsync()
    {
        return await RunOnFrameworkThread(GetHomeWorldId).ConfigureAwait(false);
    }

    public unsafe bool IsGameObjectPresent(IntPtr key)
    {
        return _objectTable.Any(f => f.Address == key);
    }

    public bool IsObjectPresent(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        EnsureIsOnFramework();
        return obj != null && obj.IsValid();
    }

    public async Task<bool> IsObjectPresentAsync(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        return await RunOnFrameworkThread(() => IsObjectPresent(obj)).ConfigureAwait(false);
    }

    public async Task RunOnFrameworkThread(System.Action act, [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        var fileName = Path.GetFileNameWithoutExtension(callerFilePath);
        await _performanceCollector.LogPerformanceAsync(this, $"RunOnFramework:Act/{fileName}>{callerMember}:{callerLineNumber}", async () =>
        {
            if (!_framework.IsInFrameworkUpdateThread)
            {
                await _framework.RunOnFrameworkThread(act).ContinueWith((_) => Task.CompletedTask).ConfigureAwait(false);
                while (_framework.IsInFrameworkUpdateThread) // yield the thread again, should technically never be triggered
                {
                    _logger.LogTrace("Still on framework");
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }
            else
                act();
        }).ConfigureAwait(false);
    }

    public async Task<T> RunOnFrameworkThread<T>(Func<T> func, [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        var fileName = Path.GetFileNameWithoutExtension(callerFilePath);
        return await _performanceCollector.LogPerformanceAsync(this, $"RunOnFramework:Func<{typeof(T)}>/{fileName}>{callerMember}:{callerLineNumber}", async () =>
        {
            if (!_framework.IsInFrameworkUpdateThread)
            {
                var result = await _framework.RunOnFrameworkThread(func).ContinueWith((task) => task.Result).ConfigureAwait(false);
                while (_framework.IsInFrameworkUpdateThread) // yield the thread again, should technically never be triggered
                {
                    _logger.LogTrace("Still on framework");
                    await Task.Delay(1).ConfigureAwait(false);
                }
                return result;
            }

            return func.Invoke();
        }).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting DalamudUtilService");
#pragma warning disable S2696 // Instance members should not write to "static" fields
        Plugin.Instance!.RealOnFrameworkUpdate = this.FrameworkOnUpdate;
#pragma warning restore S2696
        _framework.Update += Plugin.Instance!.OnFrameworkUpdate;
        if (IsLoggedIn)
        {
            _classJobId = LocalPlayer!.ClassJob.RowId;
        }

        _logger.LogInformation("Started DalamudUtilService");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace("Stopping {type}", GetType());

        Mediator.UnsubscribeAll(this);
        _framework.Update -= Plugin.Instance!.OnFrameworkUpdate;
        return Task.CompletedTask;
    }

    public async Task WaitWhileCharacterIsDrawing(ILogger logger, GameObjectHandler handler, Guid redrawId, int timeOut = 5000, CancellationToken? ct = null)
    {
        if (!_clientState.IsLoggedIn) return;

        const int tick = 250;
        int curWaitTime = 0;
        bool EnsureHandlerValid(string reason)
        {
            if (handler.Address != IntPtr.Zero) return true;
            logger.LogWarning("[{redrawId}] Stopping wait for {handler}: {reason}", redrawId, handler, reason);
            return false;
        }

        if (!EnsureHandlerValid("object reference is null")) return;

        try
        {
            logger.LogTrace("[{redrawId}] Starting wait for {handler} to draw", redrawId, handler);
            await Task.Delay(tick).ConfigureAwait(true);
            curWaitTime += tick;

            while ((!ct?.IsCancellationRequested ?? true) && curWaitTime < timeOut)
            {
                if (!EnsureHandlerValid("object address became zero")) return;

                var isDrawing = await handler.IsBeingDrawnRunOnFrameworkAsync().ConfigureAwait(false);
                if (!isDrawing) break;

                logger.LogTrace("[{redrawId}] Waiting for {handler} to finish drawing", redrawId, handler);
                curWaitTime += tick;
                await Task.Delay(tick).ConfigureAwait(true);
            }

            logger.LogTrace("[{redrawId}] Finished drawing after {curWaitTime}ms", redrawId, curWaitTime);
        }
        catch (AccessViolationException ex)
        {
            logger.LogWarning(ex, "Error accessing {handler}, object does not exist anymore?", handler);
        }
    }

    public async Task WaitForFullyLoadedAsync(GameObjectHandler handler, CancellationToken cancellationToken = default)
    {
        if (!_clientState.IsLoggedIn) return;
        if (handler.Address == IntPtr.Zero) return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isLoaded = await RunOnFrameworkThread(() => IsObjectFullyLoaded(handler.Address)).ConfigureAwait(false);
            if (!IsZoning && isLoaded)
                return;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static unsafe bool IsObjectFullyLoaded(nint address)
    {
        if (address == nint.Zero)
            return false;

        var gameObject = (GameObject*)address;
        if (gameObject == null)
            return false;

        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
            return false;

        if ((ulong)gameObject->RenderFlags == 2048)
            return false;

        var characterBase = (CharacterBase*)drawObject;
        if (characterBase->HasModelInSlotLoaded != 0)
            return false;

        if (characterBase->HasModelFilesInSlotLoaded != 0)
            return false;

        return true;
    }

    public unsafe void WaitWhileGposeCharacterIsDrawing(IntPtr characterAddress, int timeOut = 5000)
    {
        Thread.Sleep(500);
        var obj = (GameObject*)characterAddress;
        const int tick = 250;
        int curWaitTime = 0;
        _logger.LogTrace("RenderFlags: {flags}", obj->RenderFlags.ToString("X"));
        while (obj->RenderFlags != FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags.None && curWaitTime < timeOut)
        {
            _logger.LogTrace($"Waiting for gpose actor to finish drawing");
            curWaitTime += tick;
            Thread.Sleep(tick);
        }

        Thread.Sleep(tick * 2);
    }

    public Vector2 WorldToScreen(Dalamud.Game.ClientState.Objects.Types.IGameObject? obj)
    {
        if (obj == null) return Vector2.Zero;
        return _gameGui.WorldToScreen(obj.Position, out var screenPos) ? screenPos : Vector2.Zero;
    }

    public PlayerCharacter FindPlayerByNameHash(string ident)
    {
        _playerCharas.TryGetValue(ident, out var result);
        return result;
    }

    private unsafe PlayerInfo GetPlayerInfo(DalamudGameObject chara)
    {
        uint id = chara.EntityId;

        if (!_playerInfoCache.TryGetValue(id, out var info))
        {
            var homeWorldId = ((BattleChara*)chara.Address)->Character.HomeWorld;
            info = new PlayerInfo
            {
                Character = new PlayerCharacter
                {
                    ObjectId = id,
                    Name = chara.Name.TextValue,
                    HomeWorldId = homeWorldId,
                    Address = chara.Address
                },
                Hash = Crypto.GetHash256(chara.Name.TextValue + homeWorldId.ToString())
            };
            _playerInfoCache[id] = info;
        }
        else
        {
            var character = info.Character;
            character.Address = chara.Address;
            info.Character = character;
        }

        return info;
    }

    private unsafe void CheckCharacterForDrawing(PlayerCharacter p)
    {
        var gameObj = (GameObject*)p.Address;
        var drawObj = gameObj->DrawObject;
        var characterName = p.Name;
        bool isDrawing = false;
        bool isDrawingChanged = false;
        if ((nint)drawObj != IntPtr.Zero)
        {
            isDrawing = gameObj->RenderFlags == (FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags)0b100000000000;
            if (!isDrawing)
            {
                isDrawing = ((CharacterBase*)drawObj)->HasModelInSlotLoaded != 0;
                if (!isDrawing)
                {
                    isDrawing = ((CharacterBase*)drawObj)->HasModelFilesInSlotLoaded != 0;
                    if (isDrawing && !string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                        && !string.Equals(_lastGlobalBlockReason, "HasModelFilesInSlotLoaded", StringComparison.Ordinal))
                    {
                        _lastGlobalBlockPlayer = characterName;
                        _lastGlobalBlockReason = "HasModelFilesInSlotLoaded";
                        isDrawingChanged = true;
                    }
                }
                else
                {
                    if (!string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                        && !string.Equals(_lastGlobalBlockReason, "HasModelInSlotLoaded", StringComparison.Ordinal))
                    {
                        _lastGlobalBlockPlayer = characterName;
                        _lastGlobalBlockReason = "HasModelInSlotLoaded";
                        isDrawingChanged = true;
                    }
                }
            }
            else
            {
                if (!string.Equals(_lastGlobalBlockPlayer, characterName, StringComparison.Ordinal)
                    && !string.Equals(_lastGlobalBlockReason, "RenderFlags", StringComparison.Ordinal))
                {
                    _lastGlobalBlockPlayer = characterName;
                    _lastGlobalBlockReason = "RenderFlags";
                    isDrawingChanged = true;
                }
            }
        }

        if (isDrawingChanged)
        {
            _logger.LogTrace("Global draw block: START => {name} ({reason})", characterName, _lastGlobalBlockReason);
        }

        IsAnythingDrawing |= isDrawing;
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        _performanceCollector.LogPerformance(this, $"FrameworkOnUpdate", FrameworkOnUpdateInternal);
    }

    private unsafe void FrameworkOnUpdateInternal()
    {
        if (LocalPlayer?.IsDead ?? false)
        {
            return;
        }

        bool isNormalFrameworkUpdate = DateTime.UtcNow < _delayedFrameworkUpdateCheck.AddMilliseconds(200);

        _performanceCollector.LogPerformance(this, $"FrameworkOnUpdateInternal+{(isNormalFrameworkUpdate ? "Regular" : "Delayed")}", () =>
        {
            IsAnythingDrawing = false;
            _performanceCollector.LogPerformance(this, $"ObjTableToCharas",
                () =>
                {
                    if (_sentBetweenAreas)
                        return;

                    _notUpdatedCharas.AddRange(_playerCharas.Keys);

                    for (int i = 0; i < _objectTable.Length; i += 2)
                    {
                        var chara = _objectTable[i];
                        if (chara == null || chara.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
                            continue;

                        if (_blockedCharacterHandler.IsCharacterBlocked(chara.Address, out bool firstTime) && firstTime)
                        {
                            _logger.LogTrace("Skipping character {addr}, blocked/muted", chara.Address.ToString("X", CultureInfo.InvariantCulture));
                            continue;
                        }

                        var info = GetPlayerInfo(chara);

                        if (!IsAnythingDrawing)
                            CheckCharacterForDrawing(info.Character);
                        _notUpdatedCharas.Remove(info.Hash);
                        _playerCharas[info.Hash] = info.Character;
                    }

                    foreach (var notUpdatedChara in _notUpdatedCharas)
                    {
                        _playerCharas.Remove(notUpdatedChara);
                    }

                    _notUpdatedCharas.Clear();
                });

            if (!IsAnythingDrawing && !string.IsNullOrEmpty(_lastGlobalBlockPlayer))
            {
                _logger.LogTrace("Global draw block: END => {name}", _lastGlobalBlockPlayer);
                _lastGlobalBlockPlayer = string.Empty;
                _lastGlobalBlockReason = string.Empty;
            }

            if (_clientState.IsGPosing && !IsInGpose)
            {
                _logger.LogDebug("Gpose start");
                IsInGpose = true;
                Mediator.Publish(new GposeStartMessage());
            }
            else if (!_clientState.IsGPosing && IsInGpose)
            {
                _logger.LogDebug("Gpose end");
                IsInGpose = false;
                Mediator.Publish(new GposeEndMessage());
            }

            if ((_condition[ConditionFlag.Performing] || _condition[ConditionFlag.InCombat]) && !IsInCombatOrPerforming)
            {
                _logger.LogDebug("Combat/Performance start");
                IsInCombatOrPerforming = true;
                Mediator.Publish(new CombatOrPerformanceStartMessage());
                Mediator.Publish(new HaltScanMessage(nameof(IsInCombatOrPerforming)));
            }
            else if ((!_condition[ConditionFlag.Performing] && !_condition[ConditionFlag.InCombat]) && IsInCombatOrPerforming)
            {
                _logger.LogDebug("Combat/Performance end");
                IsInCombatOrPerforming = false;
                Mediator.Publish(new CombatOrPerformanceEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(IsInCombatOrPerforming)));
            }

            if (_condition[ConditionFlag.WatchingCutscene] && !IsInCutscene)
            {
                _logger.LogDebug("Cutscene start");
                IsInCutscene = true;
                Mediator.Publish(new CutsceneStartMessage());
                Mediator.Publish(new HaltScanMessage(nameof(IsInCutscene)));
            }
            else if (!_condition[ConditionFlag.WatchingCutscene] && IsInCutscene)
            {
                _logger.LogDebug("Cutscene end");
                IsInCutscene = false;
                Mediator.Publish(new CutsceneEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(IsInCutscene)));
            }

            if (IsInCutscene)
            {
                Mediator.Publish(new CutsceneFrameworkUpdateMessage());
                return;
            }

            var inActiveDuty = IsInActiveDuty();

            if (inActiveDuty && !IsInDuty)
            {
                _logger.LogDebug("Duty start");
                IsInDuty = true;
                Mediator.Publish(new InstanceOrDutyStartMessage());
            }
            else if (!inActiveDuty && IsInDuty)
            {
                _logger.LogDebug("Duty end");
                IsInDuty = false;
                Mediator.Publish(new InstanceOrDutyEndMessage());
            }

            if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            {
                var zone = _clientState.TerritoryType;
                if (_lastZone != zone)
                {
                    _lastZone = zone;
                    if (!_sentBetweenAreas)
                    {
                        _logger.LogDebug("Zone switch/Gpose start");
                        _sentBetweenAreas = true;
                        _playerInfoCache.Clear();
                        Mediator.Publish(new ZoneSwitchStartMessage());
                        Mediator.Publish(new HaltScanMessage(nameof(ConditionFlag.BetweenAreas)));
                    }
                }

                return;
            }

            if (_sentBetweenAreas)
            {
                _logger.LogDebug("Zone switch/Gpose end");
                _sentBetweenAreas = false;
                Mediator.Publish(new ZoneSwitchEndMessage());
                Mediator.Publish(new ResumeScanMessage(nameof(ConditionFlag.BetweenAreas)));
            }

            var localPlayer = LocalPlayer;
            if (localPlayer != null)
            {
                _classJobId = localPlayer.ClassJob.RowId;
            }

            Mediator.Publish(new PriorityFrameworkUpdateMessage());

            if (!IsInCombatOrPerforming)
                Mediator.Publish(new FrameworkUpdateMessage());

            if (isNormalFrameworkUpdate)
                return;

            if (localPlayer != null && !IsLoggedIn)
            {
                _logger.LogDebug("Logged in");
                IsLoggedIn = true;
                _lastZone = _clientState.TerritoryType;
                Mediator.Publish(new DalamudLoginMessage());
            }
            else if (localPlayer == null && IsLoggedIn)
            {
                _logger.LogDebug("Logged out");
                IsLoggedIn = false;
                Mediator.Publish(new DalamudLogoutMessage());
            }

            if (IsInCombatOrPerforming)
                Mediator.Publish(new FrameworkUpdateMessage());

            Mediator.Publish(new DelayedFrameworkUpdateMessage());

            _delayedFrameworkUpdateCheck = DateTime.UtcNow;
        });
    }
}
#pragma warning restore CS8500
