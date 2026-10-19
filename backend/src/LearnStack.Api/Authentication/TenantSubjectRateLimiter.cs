using System.Threading.RateLimiting;

namespace LearnStack.Api.Authentication;

/// <summary>Owns ADR-0064's additional stable issuer/subject quota and its lifetime.</summary>
internal sealed class TenantSubjectRateLimiter : IDisposable, IAsyncDisposable
{
    internal const int PermitPerWindow = 600;
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int SweepBatchSize = 128;
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Func<RateLimiter> _createPartition;
    private readonly TimeSpan _window;
    private readonly Dictionary<SubjectKey, Entry> _partitions = [];
    private readonly Queue<Entry> _sweepOrder = new();
    private readonly ITimer _sweeper;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _sweeping;

    public TenantSubjectRateLimiter()
        : this(() => CreateBudget(), Window, TimeProvider.System) { }

    /// <summary>The factory owns replenishment; the injected clock controls only retirement.</summary>
    internal TenantSubjectRateLimiter(
        Func<RateLimiter> createPartition, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(createPartition);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _createPartition = createPartition;
        _window = window;
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

    internal RateLimitLease AttemptAcquire(ValidatedTenantIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RateLimitLease? lease = null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                // Record string equality is ordinal. No actor, scope, host or
                // credential identifier participates in this private key.
                var key = new SubjectKey(identity.Issuer, identity.Subject);
                if (!_partitions.TryGetValue(key, out var entry))
                {
                    entry = new Entry(key, _createPartition());
                    _partitions.Add(key, entry);
                    _sweepOrder.Enqueue(entry);
                }

                lease = entry.Limiter.AttemptAcquire(1);
                // A caller may retain this snapshot beyond retirement/shutdown.
                // It owns neither the child lease nor a reference to its limiter.
                return new SnapshotLease(lease.IsAcquired,
                    lease.GetAllMetadata().ToDictionary(StringComparer.Ordinal));
            }
        }
        finally
        {
            // Even lease disposal is outside the registry owner lock.
            lease?.Dispose();
        }
    }

    private void Sweep()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;
        try
        {
            List<RateLimiter> retired = [];
            lock (_gate)
            {
                if (_closed) return;
                var count = Math.Min(_sweepOrder.Count, SweepBatchSize);
                for (var index = 0; index < count; index++)
                {
                    var entry = _sweepOrder.Dequeue();
                    // Stock IdleDuration exists only while all permits are
                    // available. Wall-clock age alone cannot restore spent quota.
                    if (entry.Limiter.IdleDuration is { } idle && idle >= _window)
                    {
                        _partitions.Remove(entry.Key);
                        retired.Add(entry.Limiter);
                    }
                    else _sweepOrder.Enqueue(entry);
                }
            }
            foreach (var limiter in retired) limiter.Dispose();
        }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    public void Dispose() => Shutdown(asynchronous: false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(Shutdown(asynchronous: true));

    private Task Shutdown(bool asynchronous)
    {
        lock (_gate)
        {
            if (_closed) return _shutdown.Task;
            _closed = true;
        }
        _ = CompleteShutdown(asynchronous);
        return _shutdown.Task;
    }

    private async Task CompleteShutdown(bool asynchronous)
    {
        List<Exception> failures = [];
        // DisposeAsync joins a callback that may already be disposing retired
        // entries. Both sync and async shutdown must wait for that ownership.
        try { await _sweeper.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }

        Entry[] remaining;
        lock (_gate)
        {
            remaining = _sweepOrder.ToArray();
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
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 0) _shutdown.SetResult();
        else _shutdown.SetException(new AggregateException(failures));
    }

    private readonly record struct SubjectKey(string Issuer, string Subject);
    private sealed record Entry(SubjectKey Key, RateLimiter Limiter);

    private sealed class SnapshotLease(bool acquired, IReadOnlyDictionary<string, object?> metadata) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => metadata.Keys;
        public override bool TryGetMetadata(string metadataName, out object? value) =>
            metadata.TryGetValue(metadataName, out value);
    }
}
