using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Stagehand.Api;
using System.Numerics;
using UmbraSync.API.Dto.StageShare;
using UmbraSync.Localization;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Interop.Ipc;

public sealed class IpcCallerStagehand : IIpcCaller
{
    public const string TemporaryStageIdPrefix = "UmbraSync.Stage.";

    private readonly ILogger<IpcCallerStagehand> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareMediator _mediator;
    private readonly IStagehandApiConsumer? _api;
    private readonly HashSet<string> _ownedStageIds = new(StringComparer.Ordinal);
    private readonly Lock _ownedLock = new();
    private bool _eventsSubscribed;
    private bool _disposed;

    public bool APIAvailable { get; private set; }
    public StagehandApiAvailability Availability { get; private set; } = StagehandApiAvailability.StagehandMissing;

    public IpcCallerStagehand(ILogger<IpcCallerStagehand> logger, IDalamudPluginInterface pi,
        DalamudUtilService dalamudUtil, MareMediator mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _mediator = mediator;

        try
        {
            _api = StagehandApi.CreateIpcClient(pi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de créer le client IPC Stagehand");
            _api = null;
        }

        CheckAPI();
    }

    public void CheckAPI()
    {
        if (_api == null || _disposed)
        {
            SetAvailability(StagehandApiAvailability.StagehandMissing);
            return;
        }

        StagehandApiAvailability availability;
        try
        {
            availability = _api.CheckApiAvailability();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Stagehand IPC indisponible");
            availability = StagehandApiAvailability.StagehandMissing;
        }

        SetAvailability(availability);
        if (APIAvailable && !_eventsSubscribed)
            SubscribeEvents();
    }

    private void SetAvailability(StagehandApiAvailability availability)
    {
        bool wasAvailable = APIAvailable;
        Availability = availability;
        APIAvailable = availability == StagehandApiAvailability.Available;
        if (wasAvailable == APIAvailable) return;

        _logger.LogInformation("Stagehand IPC {state} ({availability})", APIAvailable ? "disponible" : "indisponible", availability);
        if (!APIAvailable)
        {
            lock (_ownedLock)
            {
                _ownedStageIds.Clear();
            }
        }
        _mediator.Publish(new StagehandAvailabilityChangedMessage(APIAvailable));
    }

    private void SubscribeEvents()
    {
        if (_api == null) return;
        try
        {
            _api.LocationChanged += OnLocationChanged;
            _api.LocalStageDefinitionsChanged += OnLocalStageDefinitionsChanged;
            _api.LocalStageDefinitionEdited += OnLocalStageDefinitionEdited;
            _eventsSubscribed = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de s'abonner aux événements Stagehand");
            _eventsSubscribed = false;
        }
    }

    private void UnsubscribeEvents()
    {
        if (_api == null || !_eventsSubscribed) return;
        try
        {
            _api.LocationChanged -= OnLocationChanged;
            _api.LocalStageDefinitionsChanged -= OnLocalStageDefinitionsChanged;
            _api.LocalStageDefinitionEdited -= OnLocalStageDefinitionEdited;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Désabonnement des événements Stagehand en échec");
        }
        _eventsSubscribed = false;
    }

    private void OnLocationChanged(StageLocation location)
    {
        _mediator.Publish(new StagehandLocationChangedMessage(ToDto(location)));
    }

    private void OnLocalStageDefinitionsChanged()
        => _mediator.Publish(new StagehandLocalStagesChangedMessage());

    private void OnLocalStageDefinitionEdited(string fullPath)
        => _mediator.Publish(new StagehandLocalStageEditedMessage(fullPath));

    public string DescribeUnavailability()
        => Availability switch
        {
            StagehandApiAvailability.Available => string.Empty,
            StagehandApiAvailability.StagehandTooOld => Loc.Get("Ipc.Stagehand.Unavailable.TooOld"),
            StagehandApiAvailability.StagehandTooNew => Loc.Get("Ipc.Stagehand.Unavailable.TooNew"),
            _ => Loc.Get("Ipc.Stagehand.Unavailable.NotLoaded"),
        };

    public async Task<StageLocationDto?> GetLocationAsync()
    {
        if (!APIAvailable || _api == null) return null;
        try
        {
            StageLocation location = await _dalamudUtil.RunOnFrameworkThread(() => _api.GetLocation()).ConfigureAwait(false);
            return ToDto(location);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de lire le lieu Stagehand");
            return null;
        }
    }

    public async Task<LocalStageDefinition[]> GetLocalStageDefinitionsAsync()
    {
        if (!APIAvailable || _api == null) return [];
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => _api.GetLocalStageDefinitions() ?? []).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de lister les Stages locaux");
            return [];
        }
    }

    public async Task<bool> CreateOrUpdateTemporaryStageAsync(string stageId, string definitionString,
        Vector3 translation, Quaternion rotation, float uniformScale, string debugName)
    {
        if (!APIAvailable || _api == null) return false;
        if (!stageId.StartsWith(TemporaryStageIdPrefix, StringComparison.Ordinal))
        {
            _logger.LogWarning("Identifiant de Stage temporaire refusé : préfixe manquant");
            return false;
        }

        try
        {
            bool ok = await _dalamudUtil.RunOnFrameworkThread(() =>
                _api.TryCreateOrUpdateTemporaryStageWithTransform(definitionString, stageId, translation, rotation, uniformScale, debugName))
                .ConfigureAwait(false);
            if (ok)
            {
                lock (_ownedLock)
                {
                    _ownedStageIds.Add(stageId);
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de créer le Stage temporaire {id}", stageId);
            return false;
        }
    }

    public async Task<bool> SetTemporaryStageVisibleAsync(string stageId, bool visible)
    {
        if (!APIAvailable || _api == null) return false;
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => _api.TrySetTemporaryStageVisible(stageId, visible)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de changer la visibilité du Stage temporaire {id}", stageId);
            return false;
        }
    }

    public async Task<bool> DestroyTemporaryStageAsync(string stageId)
    {
        lock (_ownedLock)
        {
            _ownedStageIds.Remove(stageId);
        }
        if (!APIAvailable || _api == null) return false;
        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => _api.TryDestroyTemporaryStage(stageId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de détruire le Stage temporaire {id}", stageId);
            return false;
        }
    }

    public async Task DestroyAllOwnedTemporaryStagesAsync()
    {
        string[] ids;
        lock (_ownedLock)
        {
            ids = [.. _ownedStageIds];
        }
        foreach (string id in ids)
        {
            await DestroyTemporaryStageAsync(id).ConfigureAwait(false);
        }
    }

    private static StageLocationDto ToDto(StageLocation location)
        => new(location.WorldId, location.TerritoryId, location.WardId, location.DivisionId, location.HouseId, location.RoomId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (APIAvailable && _api != null)
            {
                string[] ids;
                lock (_ownedLock)
                {
                    ids = [.. _ownedStageIds];
                    _ownedStageIds.Clear();
                }
                foreach (string id in ids)
                {
                    try
                    {
                        if (_dalamudUtil.IsOnFrameworkThread)
                            _api.TryDestroyTemporaryStage(id);
                        else
                            _ = _dalamudUtil.RunOnFrameworkThread(() => _api.TryDestroyTemporaryStage(id));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Destruction du Stage temporaire {id} en échec au déchargement", id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nettoyage des Stages temporaires en échec au déchargement");
        }

        UnsubscribeEvents();
        APIAvailable = false;

        try
        {
            _api?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dispose du client IPC Stagehand en échec");
        }
    }
}
