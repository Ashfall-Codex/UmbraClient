using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Factories;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.PlayerData.Redraw;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using ObjectKind = UmbraSync.API.Data.Enum.ObjectKind;

namespace UmbraSync.PlayerData.Handlers;

/// <summary>
/// Remet un pair dans son apparence d'origine : retrait de la collection Penumbra, restauration
/// Glamourer et révocation des greffons optionnels (C+, Heels, Honorific, Moodles, PetNames).
/// </summary>
public sealed class PairCharacterReverter
{
    public sealed record Context(
        Func<string?> GetPlayerName,
        Func<string> DescribeForLog,
        Func<bool> IsVisible,
        Func<GameObjectHandler?> GetCharaHandler,
        Action CancelInFlightWork);

    private readonly ILogger _logger;
    private readonly Pair _pair;
    private readonly PairAppliedState _state;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly PairRedrawCoordinator _pairRedrawCoordinator;
    private readonly MareMediator _mediator;
    private readonly Context _context;

    public PairCharacterReverter(ILogger logger, Pair pair, PairAppliedState state, IpcManager ipcManager,
        DalamudUtilService dalamudUtil, GameObjectHandlerFactory gameObjectHandlerFactory,
        PairRedrawCoordinator pairRedrawCoordinator, MareMediator mediator, Context context)
    {
        _logger = logger;
        _pair = pair;
        _state = state;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _pairRedrawCoordinator = pairRedrawCoordinator;
        _mediator = mediator;
        _context = context;
    }

    public async Task UndoApplicationAsync(Guid applicationId = default)
    {
        var name = _context.GetPlayerName();
        _logger.LogDebug("Undoing application of {pair}", _context.DescribeForLog());
        _state.LastAppliedData = null;
        _state.PendingModReapply = false;
        try
        {
            if (applicationId == Guid.Empty)
                applicationId = Guid.NewGuid();
            _context.CancelInFlightWork();

            _logger.LogDebug("[{applicationId}] Removing Temp Collection for {pair}", applicationId, _context.DescribeForLog());
            if (_state.Penumbra.Collection != Guid.Empty)
            {
                var col = _state.Penumbra.Collection;
                try
                {
                    await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(_logger, applicationId, col).ConfigureAwait(false);
                    _state.Penumbra.Collection = Guid.Empty;
                    _state.Penumbra.AssignedObjectIndex = -1;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to remove temporary collection {col}, likely already removed", col);
                }
            }

            if (!string.IsNullOrEmpty(name))
            {
                _logger.LogTrace("[{applicationId}] Restoring state for {pair}", applicationId, _context.DescribeForLog());
                if (!_context.IsVisible())
                {
                    _logger.LogDebug("[{applicationId}] Restoring Glamourer for {pair}", applicationId, _context.DescribeForLog());
                    await _ipcManager.Glamourer.RevertByNameAsync(_logger, name, applicationId).ConfigureAwait(false);
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    _logger.LogInformation("[{applicationId}] CachedData is null {isNull}, contains things: {contains}", applicationId, _state.CachedData == null, (_state.CachedData?.FileReplacements.Values.Count ?? 0) > 0);

                    if (_state.CachedData != null && _state.CachedData.FileReplacements.Values.Count > 0)
                    {
                        foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in _state.CachedData.FileReplacements)
                        {
                            try
                            {
                                await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException ex)
                            {
                                _logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                                break;
                            }
                        }
                    }
                    else
                    {
                        _logger.LogDebug("[{applicationId}] Restoring Glamourer (fallback) for {pair}", applicationId, _context.DescribeForLog());
                        await _ipcManager.Glamourer.RevertByNameAsync(_logger, name, applicationId).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                _logger.LogTrace("[{applicationId}] Not restoring state, PlayerName is null or empty", applicationId);
            }

            _state.CachedData = null;
            _mediator.Publish(new PairDataAppliedMessage(_pair.UserData.UID, null));
            _logger.LogDebug("Undo Application [{applicationId}] complete", applicationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error on undoing application of {user}", _pair.UserData.UID);
        }
    }
    
    public async Task RevertToRestoredAsync(Guid applicationId)
    {
        var name = _context.GetPlayerName();
        _logger.LogDebug("[{applicationId}] Reverting to restored state for {pair}", applicationId, _context.DescribeForLog());

        var charaHandler = _context.GetCharaHandler();
        if (charaHandler is null || charaHandler.Address == nint.Zero)
        {
            _logger.LogDebug("[{applicationId}] Character handler is null or invalid, skipping revert", applicationId);
            return;
        }

        try
        {
            var gameObject = await _dalamudUtil.RunOnFrameworkThread(() => charaHandler.GetGameObject()).ConfigureAwait(false);
            if (gameObject is not Dalamud.Game.ClientState.Objects.Types.ICharacter character)
            {
                _logger.LogDebug("[{applicationId}] Game object is not a character, skipping revert", applicationId);
                return;
            }
            if (_ipcManager.Penumbra.APIAvailable && _state.Penumbra.Collection != Guid.Empty)
            {
                _logger.LogDebug("[{applicationId}] Clearing Penumbra mods for {pair}", applicationId, _context.DescribeForLog());
                try
                {
                    var assign = await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(_logger, _state.Penumbra.Collection, character.ObjectIndex).ConfigureAwait(false);
                    if (assign == global::Penumbra.Api.Enums.PenumbraApiEc.Success)
                        _state.Penumbra.AssignedObjectIndex = character.ObjectIndex;
                    
                    await _ipcManager.Penumbra.ApplyTemporaryStateAsync(_logger, applicationId, _state.Penumbra.Collection,
                        new Dictionary<string, string>(StringComparer.Ordinal), string.Empty).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{applicationId}] Failed to clear Penumbra mods for {user}", applicationId, _pair.UserData.UID);
                }
            }
            var kinds = new HashSet<ObjectKind>(_state.CustomizeIds.Keys);
            if (_state.CachedData is not null)
            {
                foreach (var kind in _state.CachedData.FileReplacements.Keys)
                {
                    kinds.Add(kind);
                }
            }
            kinds.Add(ObjectKind.Player);
            var characterName = character.Name.TextValue;
            if (string.IsNullOrEmpty(characterName))
            {
                characterName = character.Name.ToString();
            }
            if (string.IsNullOrEmpty(characterName))
            {
                _logger.LogWarning("[{applicationId}] Failed to determine character name for {user}, using fallback", applicationId, _pair.UserData.UID);
                characterName = name ?? _pair.UserData.UID;
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            _logger.LogDebug("[{applicationId}] Reverting {count} ObjectKinds for {pair}", applicationId, kinds.Count, _context.DescribeForLog());
            foreach (var kind in kinds)
            {
                try
                {
                    await RevertCustomizationDataAsync(kind, characterName, applicationId, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[{applicationId}] Revert operation timed out for {kind} on {user}", applicationId, kind, _pair.UserData.UID);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{applicationId}] Failed to revert {kind} for {user}", applicationId, kind, _pair.UserData.UID);
                }
            }

            _state.CachedData = null;
            _mediator.Publish(new PairDataAppliedMessage(_pair.UserData.UID, null));

            _logger.LogInformation("[{applicationId}] Revert to restored state complete for {user}", applicationId, _pair.UserData.UID);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{applicationId}] Failed to revert handler {user} during pause", applicationId, _pair.UserData.UID);
        }
    }

    public async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(_pair.Ident);
        if (address == nint.Zero) return;

        _logger.LogDebug("[{applicationId}] Reverting all Customization for {alias} {objectKind}", applicationId, _pair.UserData.AliasOrUID, objectKind);

        if (_state.CustomizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _state.CustomizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            _logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.Glamourer.RevertAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            _logger.LogDebug("[{applicationId}] Restoring Heels for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            _logger.LogDebug("[{applicationId}] Restoring C+ for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            _logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            _logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
            _logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}", applicationId, _pair.UserData.AliasOrUID);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _pairRedrawCoordinator.RedrawAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _pairRedrawCoordinator.RedrawAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _pairRedrawCoordinator.RedrawAsync(_logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }
}
