using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;

namespace UmbraSync.WebAPI.Files;

public readonly record struct DownloadClaim(bool IsOwner, Task<bool> Completion);

public sealed class FileDownloadDeduplicator : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inFlight = new(StringComparer.Ordinal);

    public FileDownloadDeduplicator(ILogger<FileDownloadDeduplicator> logger, MareMediator mediator)
        : base(logger, mediator)
    {
        Mediator.Subscribe<DisconnectedMessage>(this, _ => CompleteAll(false));
    }

    public DownloadClaim Claim(string hash)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _inFlight.GetOrAdd(hash, tcs);

        if (ReferenceEquals(existing, tcs))
        {
            Logger.LogDebug("Download claim: IsOwner=true for hash {hash}", hash);
            // Schedule automatic expiry so a stuck owner never blocks waiters forever
            _ = ExpireClaimAsync(hash, tcs);
            return new DownloadClaim(IsOwner: true, Completion: tcs.Task);
        }

        Logger.LogDebug("Download claim: IsOwner=false for hash {hash}, waiting on existing download", hash);
        return new DownloadClaim(IsOwner: false, Completion: existing.Task);
    }

    public bool Complete(string hash, bool success)
    {
        if (_inFlight.TryRemove(hash, out var tcs))
        {
            Logger.LogDebug("Download complete: hash {hash}, success={success}", hash, success);
            tcs.TrySetResult(success);
            return true;
        }
        return false;
    }

    public void CompleteAll(bool success)
    {
        Logger.LogDebug("Completing all in-flight downloads with success={success} (count={count})", success, _inFlight.Count);
        foreach (var kvp in _inFlight)
        {
            if (_inFlight.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetResult(success);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteAll(false);
        }
        base.Dispose(disposing);
    }

    private async Task ExpireClaimAsync(string hash, TaskCompletionSource<bool> tcs)
    {
        try
        {
            await Task.Delay(ClaimTimeout).ConfigureAwait(false);
            if (tcs.Task.IsCompleted) return;

            Logger.LogWarning("Download claim for hash {hash} expired after {timeout}, releasing waiters", hash, ClaimTimeout);
            if (_inFlight.TryRemove(hash, out var current) && ReferenceEquals(current, tcs))
            {
                tcs.TrySetResult(false);
            }
            else if (current != null)
            {
                // Someone else replaced it, put it back
                _inFlight.TryAdd(hash, current);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error in claim expiry for hash {hash}", hash);
        }
    }
}
