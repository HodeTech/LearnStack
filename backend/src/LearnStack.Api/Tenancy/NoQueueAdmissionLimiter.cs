using System.Threading.RateLimiting;

namespace LearnStack.Api.Tenancy;

/// <summary>Coordinates ADR-0054's single-permit, no-queue anonymous admission.</summary>
/// <remarks>
/// Known exhausted visitors never spend peer permits. The actual visitor registry
/// is owned here so a peer refusal cannot allocate an unknown visitor partition.
/// ASP.NET may retry even successful global admission after endpoint refusal;
/// both outcomes are request-local snapshots with independent returned leases.
/// </remarks>
internal sealed class NoQueueAdmissionLimiter : PartitionedRateLimiter<HttpContext>
{
    private const int SweepBatchSize = 128;
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly PartitionedRateLimiter<HttpContext> _peer;
    private readonly Func<RateLimiter> _createVisitor;
    private readonly TimeSpan _window;
    // The traversal queue holds the same owned entries, never shadow membership.
    private readonly Dictionary<string, Entry> _visitors = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _sweepOrder = new();
    private readonly ITimer _sweeper;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _sweeping;

    public NoQueueAdmissionLimiter(PartitionedRateLimiter<HttpContext> peer)
        : this(peer, () => CreateVisitorBudget(), RateLimitingExtensions.Window, TimeProvider.System) { }

    /// <summary>The clock controls sweeping; the factory owns quota/replenishment.</summary>
    internal NoQueueAdmissionLimiter(PartitionedRateLimiter<HttpContext> peer,
        Func<RateLimiter> createVisitor, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(createVisitor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _peer = peer;
        _createVisitor = createVisitor;
        _window = window;
        _sweeper = timeProvider.CreateTimer(_ => Sweep(), null, SweepPeriod, SweepPeriod);
    }

    internal static FixedWindowRateLimiter CreateVisitorBudget(bool autoReplenishment = true) =>
        new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = RateLimitingExtensions.AnonymousPermitPerWindow,
            Window = RateLimitingExtensions.Window,
            AutoReplenishment = autoReplenishment,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

    // There is no meaningful composite snapshot, and diagnostics must not allocate.
    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;

    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        // This is the middleware's one-permit owner, not a general queued adapter.
        ArgumentOutOfRangeException.ThrowIfNotEqual(permitCount, 1);
        var identity = resource.RequestServices.GetRequiredService<AnonymousRequestIdentity>().For(resource);
        List<RateLimitLease> leases = [];
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (resource.Items.TryGetValue(this, out var saved) && saved is Outcome prior)
                    return new SnapshotLease(prior);

                var outcome = Admit(resource, identity, leases);
                resource.Items[this] = outcome;
                return new SnapshotLease(outcome);
            }
        }
        finally
        {
            // Even lease disposal is outside the owner lock. Snapshots retain no
            // child lease or limiter reference, including after registry retirement.
            foreach (var lease in leases) lease.Dispose();
        }
    }

    private Outcome Admit(HttpContext request, AnonymousVisitor identity, List<RateLimitLease> leases)
    {
        _visitors.TryGetValue(identity.VisitorKey, out var entry);
        if (entry is not null)
        {
            var probe = entry.Limiter.AttemptAcquire(0);
            leases.Add(probe);
            if (!probe.IsAcquired) return Capture(probe);
        }

        var peerLease = _peer.AttemptAcquire(request);
        leases.Add(peerLease);
        if (!peerLease.IsAcquired) return Capture(peerLease);

        if (entry is null)
        {
            entry = new Entry(identity.VisitorKey, _createVisitor());
            _visitors.Add(entry.Key, entry);
            _sweepOrder.Enqueue(entry);
        }

        var visitorLease = entry.Limiter.AttemptAcquire(1);
        leases.Add(visitorLease);
        // The factory supplies a full limiter; no competing debit/retirement can
        // follow the successful zero probe. Replenishment only increases quota.
        if (!visitorLease.IsAcquired)
            throw new InvalidOperationException("Anonymous visitor quota changed within coordinated admission.");
        return Capture(visitorLease);
    }

    private static Outcome Capture(RateLimitLease lease) =>
        new(lease.IsAcquired, lease.GetAllMetadata().ToDictionary(StringComparer.Ordinal));

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<RateLimitLease>(cancellationToken)
            : ValueTask.FromResult(AttemptAcquireCore(resource, permitCount));

    private void Sweep()
    {
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;
        try
        {
            int remaining;
            lock (_gate) remaining = _closed ? 0 : _sweepOrder.Count;
            // Entries admitted during this pass join behind the original census.
            // Survivors rotate, so every original entry is inspected exactly once.
            while (remaining > 0)
            {
                List<RateLimiter> retired = [];
                lock (_gate)
                {
                    if (_closed) return;
                    var batch = Math.Min(remaining, SweepBatchSize);
                    for (var index = 0; index < batch; index++)
                    {
                        var entry = _sweepOrder.Dequeue();
                        if (entry.Limiter.IdleDuration is { } idle && idle >= _window)
                        {
                            _visitors.Remove(entry.Key);
                            retired.Add(entry.Limiter);
                        }
                        else _sweepOrder.Enqueue(entry);
                    }
                    remaining -= batch;
                }
                foreach (var limiter in retired) limiter.Dispose();
            }
        }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shutdown(asynchronous: false).GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await Shutdown(asynchronous: true).ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

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
        try { await _sweeper.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }

        Entry[] remaining;
        lock (_gate)
        {
            remaining = _sweepOrder.ToArray();
            _sweepOrder.Clear();
            _visitors.Clear();
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
        try
        {
            if (asynchronous) await _peer.DisposeAsync().ConfigureAwait(false);
            else _peer.Dispose();
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 0) _shutdown.SetResult();
        else _shutdown.SetException(new AggregateException(failures));
    }

    private sealed record Entry(string Key, RateLimiter Limiter);
    private sealed record Outcome(bool Acquired, IReadOnlyDictionary<string, object?> Metadata);

    private sealed class SnapshotLease(Outcome outcome) : RateLimitLease
    {
        public override bool IsAcquired => outcome.Acquired;
        public override IEnumerable<string> MetadataNames => outcome.Metadata.Keys;
        public override bool TryGetMetadata(string metadataName, out object? value) =>
            outcome.Metadata.TryGetValue(metadataName, out value);
    }
}
