using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using UmbraSync.Services.Mediator;

namespace UmbraSync.WebAPI.Files;

/// <summary>
/// Issue d'un téléchargement partagé. Released signifie que le propriétaire n'a pas tranché (annulation,
/// déconnexion, expiration) : les waiters ne basculent pas sur le serveur principal et ne posent pas de cooldown.
/// </summary>
public enum DownloadClaimOutcome { Success, Failed, Released }

public readonly record struct DownloadClaim(bool IsOwner, Task<DownloadClaimOutcome> Completion, long Generation);

public sealed class FileDownloadDeduplicator : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, InFlightClaim> _inFlight = new(StringComparer.Ordinal);
    private long _generation;

    private sealed class InFlightClaim(long generation)
    {
        public long Generation { get; } = generation;
        public TaskCompletionSource<DownloadClaimOutcome> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public FileDownloadDeduplicator(ILogger<FileDownloadDeduplicator> logger, MareMediator mediator)
        : base(logger, mediator)
    {
        Mediator.Subscribe<DisconnectedMessage>(this, _ => CompleteAll(DownloadClaimOutcome.Released));
    }

    public DownloadClaim Claim(string hash)
    {
        var candidate = new InFlightClaim(Interlocked.Increment(ref _generation));
        var existing = _inFlight.GetOrAdd(hash, candidate);

        if (ReferenceEquals(existing, candidate))
        {
            Logger.LogDebug("Download claim: IsOwner=true for hash {hash} (gen {gen})", hash, candidate.Generation);
            // Expiration automatique : un propriétaire bloqué ne doit pas retenir les waiters indéfiniment
            _ = ExpireClaimAsync(hash, candidate);
            return new DownloadClaim(IsOwner: true, Completion: candidate.Source.Task, Generation: candidate.Generation);
        }

        Logger.LogDebug("Download claim: IsOwner=false for hash {hash}, waiting on existing download", hash);
        return new DownloadClaim(IsOwner: false, Completion: existing.Source.Task, Generation: existing.Generation);
    }

    /// <summary>
    /// Ne termine que le claim de la génération indiquée : après un CompleteAll (déconnexion), un
    /// ancien propriétaire qui finit en retard ne doit pas libérer le claim de son successeur.
    /// </summary>
    public bool Complete(string hash, long generation, DownloadClaimOutcome outcome)
    {
        if (_inFlight.TryGetValue(hash, out var entry) && entry.Generation == generation
            && _inFlight.TryRemove(new KeyValuePair<string, InFlightClaim>(hash, entry)))
        {
            Logger.LogDebug("Download complete: hash {hash}, outcome={outcome}", hash, outcome);
            entry.Source.TrySetResult(outcome);
            return true;
        }
        return false;
    }

    public void CompleteAll(DownloadClaimOutcome outcome)
    {
        Logger.LogDebug("Completing all in-flight downloads with outcome={outcome} (count={count})", outcome, _inFlight.Count);
        foreach (var kvp in _inFlight)
        {
            if (_inFlight.TryRemove(kvp))
            {
                kvp.Value.Source.TrySetResult(outcome);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                CompleteAll(DownloadClaimOutcome.Released);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error while releasing download claims on dispose");
            }
        }
        base.Dispose(disposing);
    }

    private async Task ExpireClaimAsync(string hash, InFlightClaim claim)
    {
        try
        {
            // WaitAsync plutôt que Task.Delay : le timer est libéré dès que le claim se termine.
            await claim.Source.Task.WaitAsync(ClaimTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Download claim for hash {hash} expired after {timeout}, releasing waiters", hash, ClaimTimeout);
            if (_inFlight.TryRemove(new KeyValuePair<string, InFlightClaim>(hash, claim)))
                claim.Source.TrySetResult(DownloadClaimOutcome.Released);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error in claim expiry for hash {hash}", hash);
        }
    }
}
