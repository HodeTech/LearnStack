using System.Collections.Immutable;

namespace LearnStack.Infrastructure.Jobs;

public enum BackgroundJobObservationFamily { Queue = 1, Deferred, Quarantine, ReservedCapacity, FanoutPending }

/// <summary>One finite status aggregate. It contains no tenant, job, route or causal identity.</summary>
public sealed record BackgroundJobObservationPoint(BackgroundJobObservationFamily Family, string Label,
    int Code, long Count, long EligibleCount, double OldestEligibleAgeSeconds);

/// <summary>A complete immutable statement result, published only after actual rollback and cleanup.</summary>
public sealed class BackgroundJobDurableObservation
{
    private BackgroundJobDurableObservation(ImmutableArray<BackgroundJobObservationPoint> points,
        bool hasUnresolvedCapacityRecovery)
    {
        Points = points;
        HasUnresolvedCapacityRecovery = hasUnresolvedCapacityRecovery;
    }

    public ImmutableArray<BackgroundJobObservationPoint> Points { get; }
    public bool HasUnresolvedCapacityRecovery { get; }
    public bool HasQuarantinedJobs => Points.Any(point => point.Family == BackgroundJobObservationFamily.Quarantine
        && point.Label == "job" && point.Count != 0);
    public bool HasQuarantinedControl => Points.Any(point => point.Family == BackgroundJobObservationFamily.Quarantine
        && point.Label != "job" && point.Count != 0);
    public bool HasDeferredWork => Points.Any(point => point.Family == BackgroundJobObservationFamily.Deferred && point.Count != 0);

    internal static BackgroundJobDurableObservation Create(BackgroundJobObservationManifest manifest,
        IEnumerable<BackgroundJobObservationPoint> points, bool hasUnresolvedCapacityRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(points);
        var result = ImmutableArray.CreateBuilder<BackgroundJobObservationPoint>();
        var keys = new HashSet<(BackgroundJobObservationFamily Family, string Label, int Code)>();
        foreach (var point in points)
        {
            if (point is null || result.Count >= manifest.MaximumRows || !manifest.Allows(point)
                || !keys.Add((point.Family, point.Label, point.Code))
                || point.Count < 0 || point.EligibleCount < 0 || point.EligibleCount > point.Count
                || !double.IsFinite(point.OldestEligibleAgeSeconds) || point.OldestEligibleAgeSeconds < 0
                || point.EligibleCount == 0 && point.OldestEligibleAgeSeconds != 0
                || point.Family != BackgroundJobObservationFamily.Queue && (point.EligibleCount != 0 || point.OldestEligibleAgeSeconds != 0))
                throw new InvalidOperationException("The durable job observation is invalid or incomplete.");
            result.Add(point);
        }
        if (keys.Count(key => key.Family == BackgroundJobObservationFamily.Quarantine) != 4)
            throw new InvalidOperationException("The durable job observation is missing an artifact.");
        return new(result.ToImmutable(), hasUnresolvedCapacityRecovery);
    }
}
