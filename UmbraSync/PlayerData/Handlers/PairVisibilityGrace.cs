using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Utils;

namespace UmbraSync.PlayerData.Handlers;

/// <summary>
/// Période de grâce avant éviction d'un pair devenu invisible. Un joueur qui sort de portée revient
/// souvent quelques secondes plus tard : on ne détruit pas son état immédiatement. Passé le délai,
/// le handler est marqué pour suppression et sa collection Penumbra est libérée.
/// </summary>
public sealed class PairVisibilityGrace
{
    public static readonly TimeSpan EvictionGrace = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;
    private readonly Pair _pair;
    private readonly PairAppliedState _state;
    private readonly IpcManager _ipcManager;
    private readonly Func<string> _describeForLog;
    private readonly Func<bool> _isVisible;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _graceCts;
    private DateTime? _invisibleSinceUtc;
    private DateTime? _evictionDueAtUtc;

    public PairVisibilityGrace(ILogger logger, Pair pair, PairAppliedState state, IpcManager ipcManager,
        Func<string> describeForLog, Func<bool> isVisible)
    {
        _logger = logger;
        _pair = pair;
        _state = state;
        _ipcManager = ipcManager;
        _describeForLog = describeForLog;
        _isVisible = isVisible;
    }

    public bool ScheduledForDeletion { get; set; }
    public DateTime? InvisibleSinceUtc => _invisibleSinceUtc;
    public DateTime? EvictionDueAtUtc => _evictionDueAtUtc;

    public void Start()
    {
        CancellationToken token;
        lock (_gate)
        {
            _graceCts = _graceCts?.CancelRecreate() ?? new CancellationTokenSource();
            token = _graceCts.Token;
            _invisibleSinceUtc = DateTime.UtcNow;
            _evictionDueAtUtc = _invisibleSinceUtc.Value + EvictionGrace;
        }

        _logger.LogDebug("Starting visibility grace period for {pair}, eviction due at {time}",
            _describeForLog(), _evictionDueAtUtc);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EvictionGrace, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (_isVisible()) return;

                _logger.LogInformation("Visibility grace period expired for {user}, scheduling for deletion", _pair.UserData.UID);
                ScheduledForDeletion = true;

                // Clean up Penumbra collection when the grace period expires
                if (_state.Penumbra.Collection != Guid.Empty)
                {
                    var applicationId = Guid.NewGuid();
                    try
                    {
                        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(_logger, applicationId, _state.Penumbra.Collection).ConfigureAwait(false);
                        _state.Penumbra.Collection = Guid.Empty;
                        _state.Penumbra.AssignedObjectIndex = -1;
                        _logger.LogDebug("[{applicationId}] Removed temporary collection after visibility grace timeout", applicationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[{applicationId}] Failed to remove temporary collection after visibility grace timeout", applicationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Grace period was cancelled (player became visible again)
            }
        }, CancellationToken.None);
    }

    /// <summary>Le pair est redevenu visible (ou le handler part) : on annule l'éviction en attente.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_graceCts != null)
            {
                _logger.LogDebug("Cancelling visibility grace period for {pair}", _describeForLog());
                _graceCts.Cancel();
                _graceCts.Dispose();
                _graceCts = null;
            }

            _invisibleSinceUtc = null;
            _evictionDueAtUtc = null;
            ScheduledForDeletion = false;
        }
    }
}
