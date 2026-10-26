using System.Threading.RateLimiting;

namespace LearnStack.Api.Authentication;

/// <summary>Owns ADR-0064's subject quota and ADR-0066's bounded registry/cleanup lifetime.</summary>
internal sealed class TenantSubjectRateLimiter : IDisposable, IAsyncDisposable
{
    internal const int PermitPerWindow = 600;
    internal const int MaximumPartitions = 4096;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int SweepBatchSize = 128;
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Func<RateLimiter> _createPartition;
    private readonly TimeSpan _window;
    private readonly int _maximumPartitions;
    private readonly Dictionary<SubjectKey, Entry> _partitions = [];
    private readonly Queue<Entry> _sweepOrder = new();
    private readonly ITimer _sweeper;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _cleanupFailures = [];
    // A throwing Dispose is not evidence that its resource was released. Keep
    // ownership, never try it twice, and surface the failure at joined shutdown.
    private readonly List<object> _failedResources = [];
    private Retirement? _retirement;
    private bool _closed;

    public TenantSubjectRateLimiter()
        : this(() => CreateBudget(), Window, TimeProvider.System) { }

    /// <summary>The factory owns replenishment; the injected clock controls only retirement.</summary>
    internal TenantSubjectRateLimiter(
        Func<RateLimiter> createPartition, TimeSpan window, TimeProvider timeProvider,
        int maximumPartitions = MaximumPartitions)
    {
        ArgumentNullException.ThrowIfNull(createPartition);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumPartitions, 0);
        _createPartition = createPartition;
        _window = window;
        _maximumPartitions = maximumPartitions;
        _sweeper = timeProvider.CreateTimer(_ => Sweep(), null, SweepPeriod, SweepPeriod);
    }

    internal static FixedWindowRateLimiter CreateBudget(
        int permitLimit = PermitPerWindow, TimeSpan? window = null, bool autoReplenishment = true) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window ?? Window,
            AutoReplenishment = autoReplenishment,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

    internal SubjectAdmissionDecision AttemptAcquire(VerifiedProviderSubject identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var key = new SubjectKey(identity.Issuer, identity.Subject);
        RateLimitLease? lease = null;
        try
        {
            Retirement? retirement;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_partitions.TryGetValue(key, out var retained))
                    return Acquire(retained, out lease);
                if (_cleanupFailures.Count != 0) return FaultRefusal();
                if (_partitions.Count < _maximumPartitions)
                    return AllocateAndAcquire(key, out lease);
                if (_retirement is not null) return CapacityRefusal();
                retirement = BeginRetirement(maximumRetirements: 1);
                if (retirement is null) return CapacityRefusal();
            }

            // Only this request joins its synchronous, one-resource cleanup.
            // Other callers can use free slots; no slot is reserved for us.
            CompleteRetirement(retirement);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                // A competing caller may have installed this same key. Failed
                // own cleanup still never debits or dispatches this request.
                if (retirement.Failed) return FaultRefusal();
                if (_partitions.TryGetValue(key, out var retained))
                    return Acquire(retained, out lease);
                if (_cleanupFailures.Count != 0) return FaultRefusal();
                if (_partitions.Count >= _maximumPartitions) return CapacityRefusal();
                return AllocateAndAcquire(key, out lease);
            }
        }
        finally
        {
            // Even child-lease disposal is outside the registry owner lock.
            lease?.Dispose();
        }
    }

    private SubjectAdmissionDecision AllocateAndAcquire(SubjectKey key, out RateLimitLease lease)
    {
        var entry = new Entry(key, _createPartition());
        _partitions.Add(key, entry);
        _sweepOrder.Enqueue(entry);
        return Acquire(entry, out lease);
    }

    private SubjectAdmissionDecision Acquire(Entry entry, out RateLimitLease lease)
    {
        lease = entry.Limiter.AttemptAcquire(1);
        return lease.IsAcquired
            ? new(SubjectAdmissionOutcome.Acquired, null)
            : new(SubjectAdmissionOutcome.QuotaExhausted,
                lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) ? hint : _window);
    }

    private static SubjectAdmissionDecision CapacityRefusal() =>
        new(SubjectAdmissionOutcome.RegistryCapacity, Window);

    private static SubjectAdmissionDecision FaultRefusal() =>
        new(SubjectAdmissionOutcome.RegistryFault, null);

    // Called under _gate. Reserve the sole cleanup owner before detaching any
    // entries, so timer and acquisition can never accumulate retiring batches.
    private Retirement? BeginRetirement(int maximumRetirements)
    {
        var retirement = new Retirement();
        _retirement = retirement;
        var count = Math.Min(_sweepOrder.Count, SweepBatchSize);
        for (var index = 0; index < count; index++)
        {
            var entry = _sweepOrder.Dequeue();
            // Stock IdleDuration exists only while all permits are available.
            // Wall-clock age alone cannot restore a spent quota.
            if (entry.Limiter.IdleDuration is { } idle && idle >= _window)
            {
                _partitions.Remove(entry.Key);
                retirement.Limiters.Add(entry.Limiter);
                if (retirement.Limiters.Count == maximumRetirements) break;
            }
            else _sweepOrder.Enqueue(entry);
        }
        if (retirement.Limiters.Count != 0) return retirement;
        _retirement = null;
        retirement.Completion.SetResult();
        return null;
    }

    private void Sweep()
    {
        Retirement? retirement;
        lock (_gate)
        {
            if (_closed || _retirement is not null || _cleanupFailures.Count != 0) return;
            retirement = BeginRetirement(SweepBatchSize);
        }
        if (retirement is not null) CompleteRetirement(retirement);
    }

    private void CompleteRetirement(Retirement retirement)
    {
        try
        {
            foreach (var limiter in retirement.Limiters)
            {
                try { limiter.Dispose(); }
                catch (Exception error)
                {
                    retirement.Failed = true;
                    RecordCleanupFailure(limiter, error);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                retirement.Completion.SetResult();
                _retirement = null;
            }
        }
    }

    private void RecordCleanupFailure(object resource, Exception error)
    {
        bool first;
        lock (_gate)
        {
            first = _cleanupFailures.Count == 0;
            _cleanupFailures.Add(error);
            _failedResources.Add(resource);
        }
        if (first)
        {
            try { TenantSubjectAdmissionMetrics.RecordCleanupFailure(); }
            catch (Exception diagnosticFailure)
            {
                // An in-process listener is application code. Its exception
                // must not abandon the remaining owned resources in this batch.
                lock (_gate) _cleanupFailures.Add(diagnosticFailure);
            }
        }
    }

    public void Dispose() => Shutdown(asynchronous: false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(Shutdown(asynchronous: true));

    private Task Shutdown(bool asynchronous)
    {
        Task retirement;
        lock (_gate)
        {
            if (_closed) return _shutdown.Task;
            _closed = true;
            retirement = _retirement?.Completion.Task ?? Task.CompletedTask;
        }
        _ = CompleteShutdown(asynchronous, retirement);
        return _shutdown.Task;
    }

    private async Task CompleteShutdown(bool asynchronous, Task retirement)
    {
        try { await DrainShutdown(asynchronous, retirement).ConfigureAwait(false); }
        catch (Exception error)
        {
            // Every shutdown caller observes the same completion even if an
            // unexpected implementation failure interrupts the normal drain.
            _shutdown.TrySetException(error);
        }
    }

    private async Task DrainShutdown(bool asynchronous, Task retirement)
    {
        try { await _sweeper.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { RecordCleanupFailure(_sweeper, error); }
        // The timer join alone cannot witness an acquisition-owned cleanup.
        await retirement.ConfigureAwait(false);

        Entry[] remaining;
        lock (_gate)
        {
            remaining = _partitions.Values.ToArray();
            _sweepOrder.Clear();
            _partitions.Clear();
        }
        foreach (var entry in remaining)
        {
            try
            {
                if (asynchronous) await entry.Limiter.DisposeAsync().ConfigureAwait(false);
                else entry.Limiter.Dispose();
            }
            catch (Exception error) { RecordCleanupFailure(entry.Limiter, error); }
        }
        Exception[] failures;
        lock (_gate) failures = _cleanupFailures.ToArray();
        if (failures.Length == 0) _shutdown.SetResult();
        else _shutdown.SetException(new AggregateException(failures));
    }

    private readonly record struct SubjectKey(string Issuer, string Subject);
    private sealed record Entry(SubjectKey Key, RateLimiter Limiter);
    private sealed class Retirement
    {
        internal List<RateLimiter> Limiters { get; } = [];
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Failed { get; set; }
    }
}
