using System.Threading.RateLimiting;

namespace LearnStack.Api.Tenancy;

/// <summary>Preserves one failed no-queue admission when ASP.NET retries it asynchronously.</summary>
/// <remarks>
/// RateLimitingMiddleware calls AttemptAcquire then AcquireAsync on a refusal.
/// Re-running a chain burns its earlier, non-refundable fixed-window permits twice.
/// This wrapper keeps peer-first ordering and one charge per actual HTTP request.
/// It is specific to the no-queue anonymous chain, not a queued limiter adapter.
/// </remarks>
internal sealed class NoQueueAdmissionLimiter(PartitionedRateLimiter<HttpContext> inner)
    : PartitionedRateLimiter<HttpContext>
{
    private readonly PartitionedRateLimiter<HttpContext> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => _inner.GetStatistics(resource);

    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Items.TryGetValue(this, out var saved) && saved is Refusal prior && prior.Permits == permitCount)
            return new RefusedLease(prior.Metadata);

        var lease = _inner.AttemptAcquire(resource, permitCount);
        if (lease.IsAcquired) return lease;
        var refusal = new Refusal(permitCount, lease.GetAllMetadata().ToDictionary(StringComparer.Ordinal));
        resource.Items[this] = refusal;
        lease.Dispose();
        // Each attempt owns its own lease; no disposed lease is returned on retry.
        return new RefusedLease(refusal.Metadata);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<RateLimitLease>(cancellationToken)
            : ValueTask.FromResult(AttemptAcquireCore(resource, permitCount));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed record Refusal(int Permits, IReadOnlyDictionary<string, object?> Metadata);

    private sealed class RefusedLease(IReadOnlyDictionary<string, object?> metadata) : RateLimitLease
    {
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => metadata.Keys;
        public override bool TryGetMetadata(string metadataName, out object? value) => metadata.TryGetValue(metadataName, out value);
    }
}
