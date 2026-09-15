using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
namespace UmbraSync.PlayerData.Handlers;


public sealed partial class PairHandler
{
    private static readonly TimeSpan ExternalSyncCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExternalSyncReclaimCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExternalSyncReclaimWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExternalResetDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OwnGlamourerCallGrace = TimeSpan.FromSeconds(3);
    private const int MaxExternalSyncReclaims = 3;

    private readonly Queue<DateTime> _externalSyncReclaims = new();
    private DateTime _nextExternalSyncCheckUtc = DateTime.MinValue;
    private long _lastOwnGlamourerCallTicks;
    private int _externalSyncReclaimPending;
    private volatile bool _externalSyncReclaimSuspended;

    private DateTime LastOwnGlamourerCallUtc
    {
        get => new(Interlocked.Read(ref _lastOwnGlamourerCallTicks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _lastOwnGlamourerCallTicks, value.Ticks);
    }

    private bool CanReclaimAppearance()
        => _ipcManager.Mare.IsReclaimEnabled
           && !_externalSyncReclaimSuspended
           && !Pair.IsPaused
           && IsVisible
           && _state.Penumbra.Collection != Guid.Empty
           && _state.LastAppliedData != null
           && !IsApplyingOrDownloading
           && !_dalamudUtil.IsInGpose
           && !_dalamudUtil.IsInCutscene
           && !_dalamudUtil.IsZoning;

    // Appelé sur le framework thread (DelayedFrameworkUpdateMessage)
    private void CheckExternalSyncReclaim()
    {
        if (!IsVisible)
        {
            if (_externalSyncReclaimSuspended)
                ResetExternalSyncReclaim();
            return;
        }

        var handler = _charaHandler;
        if (handler == null || handler.Address == nint.Zero || !CanReclaimAppearance()) return;

        var now = DateTime.UtcNow;
        if (now < _nextExternalSyncCheckUtc) return;
        _nextExternalSyncCheckUtc = now + ExternalSyncCheckInterval;

        if (handler.GetGameObject() is not { } gameObject) return;
        if (_ipcManager.Mare.GetEffectiveCollection(gameObject.ObjectIndex) is not { } effective) return;
        if (effective.Id == _state.Penumbra.Collection || !IpcCallerMare.IsForeignSyncCollection(effective.Name)) return;

        ScheduleExternalSyncReclaim("collection " + IpcCallerMare.DescribeForeignSyncCollection(effective.Name) + " imposée", TimeSpan.Zero);
    }

    private void OnGlamourerReset(nint address)
    {
        var handler = _charaHandler;
        if (handler == null || handler.Address == nint.Zero || handler.Address != address) return;
        if (!CanReclaimAppearance()) return;
        if (DateTime.UtcNow - LastOwnGlamourerCallUtc < OwnGlamourerCallGrace) return;

        ScheduleExternalSyncReclaim("apparence réinitialisée par un autre plugin", ExternalResetDebounce);
    }

    private void ResetExternalSyncReclaim()
    {
        lock (_externalSyncReclaims)
        {
            _externalSyncReclaims.Clear();
            _externalSyncReclaimSuspended = false;
        }
    }

    private void ScheduleExternalSyncReclaim(string reason, TimeSpan delay)
    {
        var now = DateTime.UtcNow;
        lock (_externalSyncReclaims)
        {
            while (_externalSyncReclaims.Count > 0 && now - _externalSyncReclaims.Peek() > ExternalSyncReclaimWindow)
                _externalSyncReclaims.Dequeue();

            if (_externalSyncReclaims.Count > 0 && now - _externalSyncReclaims.Last() < ExternalSyncReclaimCooldown)
                return;

            if (_externalSyncReclaims.Count >= MaxExternalSyncReclaims)
            {
                _externalSyncReclaimSuspended = true;
                Logger.LogWarning("{pairHandler} : {count} reprises de la main en {window}, coexistence suspendue pour ce joueur jusqu'à sa sortie de portée",
                    this, _externalSyncReclaims.Count, ExternalSyncReclaimWindow);
                return;
            }

            if (Interlocked.Exchange(ref _externalSyncReclaimPending, 1) != 0)
                return;

            _externalSyncReclaims.Enqueue(now);
        }

        Logger.LogInformation("{pairHandler} : Umbra reprend la main ({reason})", this, reason);

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay).ConfigureAwait(false);

                var data = _state.CachedData ?? _state.LastAppliedData;
                if (data == null || !CanReclaimAppearance())
                    return;

                // Réassignation forcée de la collection et application complète : mods, Glamourer, greffons, redraw
                _state.Penumbra.AssignedObjectIndex = -1;
                _state.ForceApplyMods = true;
                _state.PendingModReapply = true;
                ApplyCharacterData(Guid.NewGuid(), data, forceApplyCustomization: true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to take back {user} from another sync plugin", Pair.UserData.UID);
            }
            finally
            {
                Interlocked.Exchange(ref _externalSyncReclaimPending, 0);
            }
        });
    }
}
