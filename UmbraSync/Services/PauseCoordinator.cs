using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Data.Extensions;
using UmbraSync.API.Dto.Group;
using UmbraSync.API.Dto.User;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services;

public class PauseCoordinator : MediatorSubscriberBase, IDisposable
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(5);

    private readonly PairManager _pairManager;
    private readonly Lazy<ApiController> _apiController;
    private readonly ConcurrentDictionary<string, PendingPause> _pending = new(StringComparer.Ordinal);

    public PauseCoordinator(ILogger<PauseCoordinator> logger, MareMediator mediator,
        PairManager pairManager, IServiceProvider serviceProvider)
        : base(logger, mediator)
    {
        _pairManager = pairManager;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());

        Mediator.Subscribe<PauseMessage>(this, msg => PauseDirectPair(msg.UserData));
        Mediator.Subscribe<CyclePauseMessage>(this, msg => _ = CyclePauseAsync(msg.UserData));
        Mediator.Subscribe<GroupPairPauseMessage>(this, msg => PauseGroupPair(msg.Group, msg.UserData, msg.CurrentPermissions));
        Mediator.Subscribe<GroupWidePauseMessage>(this, msg => PauseGroupWide(msg.Group, msg.CurrentPermissions, msg.CallerUID));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => _pending.Clear());
    }

    // Public API pour les callbacks serveur
    
    public bool ShouldIgnorePauseUpdate(string uid, bool incomingPaused)
    {
        if (!_pending.TryGetValue(uid, out var pending))
            return false;

        if (pending.ExpiresAt <= DateTime.UtcNow)
        {
            _pending.TryRemove(uid, out _);
            return false;
        }

        if (incomingPaused == pending.ExpectedPaused)
        {
            _pending.TryRemove(uid, out _);
            return false;
        }

        Logger.LogDebug("Ignoring stale pause update for {Uid}: incoming={Incoming}, expected={Expected}", uid, incomingPaused, pending.ExpectedPaused);
        return true;
    }
    public bool IsPendingFor(string uid) =>
        _pending.TryGetValue(uid, out var p) && p.ExpiresAt > DateTime.UtcNow;

    //Pause directe (pair individuel)

    private void PauseDirectPair(UserData userData)
    {
        var pair = _pairManager.GetPairByUID(userData.UID);
        if (pair?.UserPair == null)
        {
            Logger.LogWarning("PauseDirectPair: pair not found or no UserPair for {uid}", userData.UID);
            return;
        }

        bool isCurrentlyPaused = pair.UserPair.OwnPermissions.IsPaused();
        bool shouldPause = !isCurrentlyPaused;

        Logger.LogInformation("PauseDirectPair: toggling {uid}: {from} -> {to}", userData.UID, isCurrentlyPaused, shouldPause);

        var perm = pair.UserPair.OwnPermissions;
        perm.SetPaused(shouldPause);
        pair.UserPair.OwnPermissions = perm;

        MarkPending(userData.UID, shouldPause);
        _ = _apiController.Value.UserSetPairPermissions(new UserPermissionsDto(userData, perm));

        if (!shouldPause)
        {
            pair.UnholdApplication("IndividualPerformanceThreshold");
        }

        ApplyPauseToHandler(pair, shouldPause);
    }

    // Pause individuelle dans une Syncshell

    private void PauseGroupPair(GroupData group, UserData userData, GroupUserPermissions currentPermissions)
    {
        bool shouldPause = !currentPermissions.IsPaused();

        Logger.LogInformation("PauseGroupPair: toggling {uid} in group {gid}: -> {to}", userData.UID, group.GID, shouldPause);

        var newPerm = currentPermissions;
        newPerm.SetPaused(shouldPause);

        MarkPending($"group:{group.GID}:{userData.UID}", shouldPause);
        _ = _apiController.Value.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(group, userData, newPerm));
    }

    // Pause globale d'une Syncshell 

    private void PauseGroupWide(GroupData group, GroupUserPermissions currentPermissions, string callerUID)
    {
        var newPerm = currentPermissions;
        newPerm.SetPaused(!currentPermissions.IsPaused());

        Logger.LogInformation("PauseGroupWide: toggling group {gid}: -> {to}", group.GID, newPerm.IsPaused());

        MarkPending($"groupwide:{group.GID}", newPerm.IsPaused());
        _ = _apiController.Value.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(group, new UserData(callerUID), newPerm));
    }

    // CyclePause

    private async Task CyclePauseAsync(UserData userData)
    {
        using var timeoutCts = new CancellationTokenSource(PendingTimeout);
        var token = timeoutCts.Token;

        try
        {
            var pair = _pairManager.GetPairByUID(userData.UID);
            if (pair?.UserPair == null)
            {
                Logger.LogWarning("CyclePauseAsync: pair {uid} not found or no UserPair", userData.UID);
                return;
            }

            // Phase 1 : Pause
            var targetPermissions = pair.UserPair.OwnPermissions;
            targetPermissions.SetPaused(true);
            pair.UserPair.OwnPermissions = targetPermissions;

            MarkPending(userData.UID, true);
            await _apiController.Value.UserSetPairPermissions(new UserPermissionsDto(userData, targetPermissions)).ConfigureAwait(false);

            // Attendre confirmation serveur (polling 250ms)
            bool pauseApplied = false;
            while (!token.IsCancellationRequested)
            {
                var updatedPair = _pairManager.GetPairByUID(userData.UID);
                if (updatedPair?.UserPair != null && updatedPair.UserPair.OwnPermissions.IsPaused())
                {
                    pauseApplied = true;
                    pair = updatedPair;
                    break;
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }

            if (!pauseApplied)
            {
                Logger.LogWarning("CyclePauseAsync: timed out waiting for pause ACK for {uid}", userData.UID);
                return;
            }

            // Phase 2 : Unpause
            targetPermissions = pair.UserPair!.OwnPermissions;
            targetPermissions.SetPaused(false);
            pair.UserPair.OwnPermissions = targetPermissions;

            MarkPending(userData.UID, false);
            await _apiController.Value.UserSetPairPermissions(new UserPermissionsDto(userData, targetPermissions)).ConfigureAwait(false);

            pair.ApplyLastReceivedData(forced: true);
            Logger.LogInformation("CyclePauseAsync: completed for {uid}", userData.UID);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("CyclePauseAsync: cancelled for {uid}", userData.UID);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "CyclePauseAsync: failed for {uid}", userData.UID);
        }
    }

    // Helpers

    private void MarkPending(string key, bool expectedPaused)
    {
        _pending[key] = new PendingPause(expectedPaused, DateTime.UtcNow.Add(PendingTimeout));
        Logger.LogTrace("Tracked pending pause for {Key}: expected={Expected}", key, expectedPaused);
    }

    private void ApplyPauseToHandler(Pair pair, bool shouldPause)
    {
        if (pair.Handler != null)
        {
            pair.Handler.SetPaused(shouldPause);

            if (!shouldPause)
            {
                _pairManager.CancelPendingOffline(pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
        }
        else
        {
            Logger.LogWarning("Handler not available for {uid}, using fallback", pair.UserData.UID);
            if (shouldPause)
            {
                Mediator.Publish(new PlayerVisibilityMessage(pair.Ident, IsVisible: false, Invalidate: true));
            }
            else
            {
                _pairManager.CancelPendingOffline(pair.UserData.UID);
                pair.ApplyLastReceivedData(forced: true);
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pending.Clear();
            UnsubscribeAll();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly record struct PendingPause(bool ExpectedPaused, DateTime ExpiresAt);
}
