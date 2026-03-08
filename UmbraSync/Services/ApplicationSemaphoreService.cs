using System.Runtime.InteropServices;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace UmbraSync.Services;

[StructLayout(LayoutKind.Auto)]
public readonly record struct ApplicationSemaphoreSnapshot(bool IsEnabled, int Limit, int InFlight, int Waiting)
{
    public int Remaining => Math.Max(0, Limit - InFlight);
}

public sealed class ApplicationSemaphoreService : DisposableMediatorSubscriberBase
{
    private const int HardLimit = 10;
    private readonly MareConfigService _configService;
    private readonly Lock _limitLock = new();
    private readonly LinkedList<PriorityWaiter> _highQueue = new();
    private readonly LinkedList<PriorityWaiter> _lowQueue = new();
    private int _currentLimit;
    private int _availableSlots;
    private int _pendingReductions;
    private int _pendingIncrements;
    private int _inFlight;

    public ApplicationSemaphoreService(ILogger<ApplicationSemaphoreService> logger, MareMediator mediator,
        MareConfigService configService)
        : base(logger, mediator)
    {
        _configService = configService;
        _currentLimit = CalculateLimit();
        _availableSlots = _currentLimit;

        Mediator.Subscribe<PairProcessingLimitChangedMessage>(this, _ => UpdateSemaphoreLimit());
    }

    private bool IsEnabled => _configService.Current.EnableParallelPairProcessing;

    public ApplicationSemaphoreSnapshot GetSnapshot()
    {
        lock (_limitLock)
        {
            var enabled = IsEnabled;
            var limit = enabled ? _currentLimit : 1;
            var waiting = _highQueue.Count + _lowQueue.Count;
            var inFlight = Math.Max(0, Volatile.Read(ref _inFlight));
            return new ApplicationSemaphoreSnapshot(enabled, limit, inFlight, waiting);
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken, bool highPriority = false)
    {
        PriorityWaiter waiter;

        lock (_limitLock)
        {
            if (_availableSlots > 0)
            {
                _availableSlots--;
                Interlocked.Increment(ref _inFlight);
                return new Releaser(this);
            }

            waiter = new PriorityWaiter(cancellationToken);
            if (highPriority)
                _highQueue.AddLast(waiter);
            else
                _lowQueue.AddLast(waiter);
        }

        try
        {
            await waiter.Tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_limitLock)
            {
                // Remove from whichever queue it's in; the node may already have been
                // removed by ReleaseOne if it completed the TCS just before cancellation
                // was observed.  LinkedList.Remove(T) is O(n) but queues are tiny (≤ HardLimit).
                _highQueue.Remove(waiter);
                _lowQueue.Remove(waiter);
            }

            waiter.Dispose();
            Logger.LogTrace("Semaphore acquire was cancelled");
            throw;
        }

        waiter.Dispose();
        Interlocked.Increment(ref _inFlight);
        return new Releaser(this);
    }

    private void UpdateSemaphoreLimit()
    {
        lock (_limitLock)
        {
            var desiredLimit = CalculateLimit();

            if (desiredLimit == _currentLimit)
                return;

            Logger.LogDebug("Updating pair processing limit from {old} to {new}", _currentLimit, desiredLimit);

            if (desiredLimit > _currentLimit)
            {
                var increment = desiredLimit - _currentLimit;
                _pendingIncrements += increment;

                // Release as many pending increments as possible by waking waiters or adding slots
                while (_pendingIncrements > 0)
                {
                    if (TryDequeueAndComplete())
                    {
                        _pendingIncrements--;
                    }
                    else if (_availableSlots < HardLimit)
                    {
                        _availableSlots++;
                        _pendingIncrements--;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                var decrement = _currentLimit - desiredLimit;

                // Try to reclaim available slots first
                var removed = Math.Min(decrement, _availableSlots);
                _availableSlots -= removed;

                var remaining = decrement - removed;
                if (remaining > 0)
                {
                    _pendingReductions += remaining;
                }

                if (_pendingIncrements > 0)
                {
                    var offset = Math.Min(_pendingIncrements, _pendingReductions);
                    _pendingIncrements -= offset;
                    _pendingReductions -= offset;
                }
            }

            _currentLimit = desiredLimit;
            Logger.LogInformation("Pair processing concurrency updated to {limit} (pending reductions: {pending})",
                _currentLimit, _pendingReductions);
        }
    }

    private int CalculateLimit()
    {
        if (!_configService.Current.EnableParallelPairProcessing)
            return 1;
        return Math.Clamp(_configService.Current.MaxConcurrentPairApplications, 1, HardLimit);
    }

    /// <summary>
    /// Tries to dequeue the highest-priority waiter and complete its TCS.
    /// Must be called under <see cref="_limitLock"/>.
    /// Returns true if a waiter was woken up.
    /// </summary>
    private bool TryDequeueAndComplete()
    {
        while (_highQueue.First != null)
        {
            var waiter = _highQueue.First.Value;
            _highQueue.RemoveFirst();
            if (waiter.Tcs.TrySetResult(true))
                return true;
            // If TrySetResult failed, the waiter was already cancelled — skip and try next
        }

        while (_lowQueue.First != null)
        {
            var waiter = _lowQueue.First.Value;
            _lowQueue.RemoveFirst();
            if (waiter.Tcs.TrySetResult(true))
                return true;
        }

        return false;
    }

    private void ReleaseOne()
    {
        var inFlight = Interlocked.Decrement(ref _inFlight);
        if (inFlight < 0)
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }

        lock (_limitLock)
        {
            if (_pendingReductions > 0)
            {
                _pendingReductions--;
                return;
            }

            if (_pendingIncrements > 0)
            {
                if (TryDequeueAndComplete())
                {
                    _pendingIncrements--;
                    return;
                }

                if (_availableSlots < HardLimit)
                {
                    _availableSlots++;
                    _pendingIncrements--;
                }
                return;
            }

            // Normal release: wake a waiter or return the slot
            if (TryDequeueAndComplete())
                return;

            _availableSlots++;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Logger.LogDebug("Disposing ApplicationSemaphoreService");

        lock (_limitLock)
        {
            foreach (var waiter in _highQueue)
            {
                waiter.Tcs.TrySetCanceled();
                waiter.Dispose();
            }
            _highQueue.Clear();

            foreach (var waiter in _lowQueue)
            {
                waiter.Tcs.TrySetCanceled();
                waiter.Dispose();
            }
            _lowQueue.Clear();
        }
    }

    private sealed class PriorityWaiter : IDisposable
    {
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _ctrRegistration;

        public PriorityWaiter(CancellationToken ct)
        {
            _ctrRegistration = ct.Register(() => Tcs.TrySetCanceled(ct));
        }

        public void Dispose()
        {
            _ctrRegistration.Dispose();
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private ApplicationSemaphoreService? _owner;

        public Releaser(ApplicationSemaphoreService owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseOne();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class NoopReleaser : IAsyncDisposable
    {
        public static readonly NoopReleaser Instance = new();
        private NoopReleaser() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
