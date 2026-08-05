using System.Collections.Concurrent;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.Services.ActorTracking;

public sealed unsafe class DrawObjectTrackingService : DisposableMediatorSubscriberBase, IHostedService
{
    // Signature de GameObject.EnableDraw (codée en dur : Umbra ne référence pas Penumbra.GameData).
    // Si elle ne résout pas (patch FFXIV), l'init échoue proprement et on retombe sur le polling.
    private const string EnableDrawSignature = "E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9 74 33 45 33 C0";

    private readonly IFramework _framework;
    private readonly IGameInteropProvider _interop;
    private readonly MareConfigService _configService;

    private Hook<GameObject.Delegates.EnableDraw>? _enableDrawHook;
    private Hook<CharacterBase.Delegates.Create>? _createCharacterBaseHook;
    private Hook<CharacterBase.Delegates.Destroy>? _destroyCharacterBaseHook;
    private readonly ThreadLocal<Stack<nint>> _lastGameObject = new(() => new Stack<nint>(), trackAllValues: true);
    private readonly ConcurrentDictionary<nint, nint> _drawObjectToGameObject = new();
    private readonly ConcurrentDictionary<nint, nint> _gameObjectToDrawObject = new();

    public bool HooksActive { get; private set; }

    public DrawObjectTrackingService(ILogger<DrawObjectTrackingService> logger, MareMediator mediator,
        IFramework framework, IGameInteropProvider interop, MareConfigService configService)
        : base(logger, mediator)
    {
        _framework = framework;
        _interop = interop;
        _configService = configService;

        Mediator.Subscribe<DalamudLogoutMessage>(this, _ => ClearMappings());
        Mediator.Subscribe<DalamudLoginMessage>(this, _ =>
        {
            if (HooksActive) ScheduleSeed();
        });
    }

    private void ScheduleSeed() => _ = _framework.RunOnFrameworkThread(SeedExistingDrawObjects);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configService.Current.EnableEventVisibility)
        {
            Logger.LogInformation("Event visibility disabled (flag OFF); draw-object hooks not installed.");
            return Task.CompletedTask;
        }

        try
        {
            InitializeHooks();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize draw-object hooks; falling back to polling visibility.");
            DisposeHooks();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeHooks();
        ClearMappings();
        return Task.CompletedTask;
    }

    /// <summary>True si le game object a actuellement un draw object lié (= rendu).</summary>
    public bool HasDrawObjectLinked(nint gameObject)
        => gameObject != nint.Zero && _gameObjectToDrawObject.ContainsKey(gameObject);

    private void InitializeHooks()
    {
        if (HooksActive) return;

        _enableDrawHook = _interop.HookFromSignature<GameObject.Delegates.EnableDraw>(EnableDrawSignature, EnableDrawDetour);
        _createCharacterBaseHook = _interop.HookFromAddress<CharacterBase.Delegates.Create>(
            (nint)CharacterBase.MemberFunctionPointers.Create, CreateCharacterBaseDetour);
        _destroyCharacterBaseHook = _interop.HookFromAddress<CharacterBase.Delegates.Destroy>(
            (nint)CharacterBase.MemberFunctionPointers.Destroy, DestroyCharacterBaseDetour);

        _enableDrawHook.Enable();
        _createCharacterBaseHook.Enable();
        _destroyCharacterBaseHook.Enable();

        HooksActive = true;
        Logger.LogInformation("DrawObjectTrackingService hooks enabled (event-driven visibility).");

        _ = _framework.RunOnFrameworkThread(SeedExistingDrawObjects);
    }

    private void DisposeHooks()
    {
        _enableDrawHook?.Disable();
        _createCharacterBaseHook?.Disable();
        _destroyCharacterBaseHook?.Disable();

        _enableDrawHook?.Dispose();
        _createCharacterBaseHook?.Dispose();
        _destroyCharacterBaseHook?.Dispose();

        _enableDrawHook = null;
        _createCharacterBaseHook = null;
        _destroyCharacterBaseHook = null;

        HooksActive = false;
    }

    private void ClearMappings()
    {
        _drawObjectToGameObject.Clear();
        _gameObjectToDrawObject.Clear();
        try
        {
            foreach (var stack in _lastGameObject.Values) stack.Clear();
        }
        catch (Exception)
        {
            // Vider les piles est du confort : jamais au prix d'un Dispose qui lève, ce qui
            // empêcherait Dalamud de décharger le plugin.
        }
    }

    private Stack<nint> GameObjectStack => _lastGameObject.Value!;

    private nint LastGameObject
        => _lastGameObject.IsValueCreated && _lastGameObject.Value!.Count > 0
            ? _lastGameObject.Value!.Peek()
            : nint.Zero;

    private void LinkDrawObject(nint gameObject, nint drawObject)
    {
        if (gameObject == nint.Zero || drawObject == nint.Zero) return;

        // Remplacement éventuel d'un lien obsolète des deux côtés.
        if (_gameObjectToDrawObject.TryGetValue(gameObject, out var existingDraw) && existingDraw != drawObject)
        {
            _drawObjectToGameObject.TryRemove(existingDraw, out _);
            PublishUnlinked(gameObject, existingDraw);
        }
        if (_drawObjectToGameObject.TryGetValue(drawObject, out var existingActor) && existingActor != gameObject)
        {
            _gameObjectToDrawObject.TryRemove(existingActor, out _);
            PublishUnlinked(existingActor, drawObject);
        }

        _gameObjectToDrawObject[gameObject] = drawObject;
        _drawObjectToGameObject[drawObject] = gameObject;
        PublishLinked(gameObject, drawObject);
    }

    private void UnlinkDrawObject(nint drawObject)
    {
        if (drawObject == nint.Zero) return;

        if (_drawObjectToGameObject.TryRemove(drawObject, out var gameObject))
        {
            _gameObjectToDrawObject.TryRemove(gameObject, out _);
            PublishUnlinked(gameObject, drawObject);
        }
    }

    private void PublishLinked(nint gameObject, nint drawObject)
    {
        var message = new DrawObjectLinkedMessage(gameObject, drawObject);
        if (_framework.IsInFrameworkUpdateThread) Mediator.Publish(message);
        else _ = _framework.RunOnFrameworkThread(() => Mediator.Publish(message));
    }

    private void PublishUnlinked(nint gameObject, nint drawObject)
    {
        var message = new DrawObjectUnlinkedMessage(gameObject, drawObject);
        if (_framework.IsInFrameworkUpdateThread) Mediator.Publish(message);
        else _ = _framework.RunOnFrameworkThread(() => Mediator.Publish(message));
    }

    private void EnableDrawDetour(GameObject* gameObject)
    {
        GameObjectStack.Push((nint)gameObject);
        try
        {
            _enableDrawHook!.Original(gameObject);
        }
        finally
        {
            if (GameObjectStack.Count > 0) GameObjectStack.Pop();
        }
    }

    private CharacterBase* CreateCharacterBaseDetour(uint model, CustomizeData* customize, EquipmentModelId* equipment, byte unk)
    {
        var result = _createCharacterBaseHook!.Original(model, customize, equipment, unk);
        if (result != null)
        {
            var gameObject = LastGameObject;
            if (gameObject != nint.Zero
                && NativeGameObjectUtils.TryFindGameObjectByAddress(gameObject, 0, out _))
            {
                LinkDrawObject(gameObject, (nint)result);
            }
        }
        return result;
    }

    private void DestroyCharacterBaseDetour(CharacterBase* characterBase)
    {
        if (characterBase != null) UnlinkDrawObject((nint)characterBase);
        _destroyCharacterBaseHook!.Original(characterBase);
    }

    private void SeedExistingDrawObjects()
    {
        var manager = GameObjectManager.Instance();
        if (manager == null) return;

        var objects = manager->Objects.IndexSorted;
        for (var i = 0; i < objects.Length; i++)
        {
            var obj = objects[i].Value;
            if (!NativeGameObjectUtils.IsValidObjectTableEntry(obj, i)) continue;

            var drawObject = obj->DrawObject;
            if (drawObject == null) continue;

            LinkDrawObject((nint)obj, (nint)drawObject);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeHooks();
            ClearMappings();
            // _lastGameObject n'est volontairement PAS disposé : le service est disposé deux fois
            // (Dalamud + arrêt du IHost) et le second passage levait alors sur un ThreadLocal mort,
            // ce qui faisait échouer le déchargement du plugin. La fuite résiduelle (une pile vide
            // par thread ayant exécuté le detour) est sans commune mesure avec un unload cassé.
        }
        base.Dispose(disposing);
    }
}
