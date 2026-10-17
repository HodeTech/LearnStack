namespace LearnStack.Infrastructure.Jobs;

/// <summary>Sanitized settings captured from the same connection string as the shared dispatcher source.</summary>
public sealed record BackgroundJobPhysicalPoolSettings(bool Pooling, int MaxPoolSize);

/// <summary>The conservative current acquisition manifest; later SQL owners require requalification.</summary>
public sealed record BackgroundJobPhysicalCapacity(int Pools, int Workers, int ControlPools,
    int InvocationOwners, int RequiredConnections)
{
    public int RecurrenceConnections { get; } = 2;
    public static BackgroundJobPhysicalCapacity Calculate(BackgroundJobInfrastructureRegistry topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var pools = topology.Pools.Count;
        var workers = topology.Pools.Values.Sum(pool => pool.Workers);
        var controls = topology.Pools.Values.Count(pool => pool.Kind == BackgroundJobWorkerPoolKind.Control);
        var invocations = topology.Pools.Values.Sum(pool => BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers));
        // Stock W + four per-pool controls/lifecycle + two schedulers per
        // Control pool + shared/bridge/messaging eleven + retained invocation U.
        // Each invocation has at most one actual routing, recovery acquisition,
        // confirmation or checkpoint-history SQL operation, including cleanup.
        // Step 5 adds one batch renewal slot, two recovery/probe slots and
        // two terminal/probe slots. Cleanup failure seals its exact lane.
        return new(pools, workers, controls, invocations,
            // Recurrence has one serialized SQL owner and one native manager
            // connection, including retained startup/native return and cleanup.
            checked(workers + 4 * pools + 2 * controls + 18 + invocations));
    }

    public void Validate(BackgroundJobPhysicalPoolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxPoolSize < 1 || settings.Pooling && settings.MaxPoolSize < RequiredConnections)
            throw new ArgumentException("The background job source does not meet the supported physical capacity.", nameof(settings));
        // Nonpooling still has the same owner bound. MaxPoolSize is not its
        // physical checkout cap, so this predicate creates no nonpooling ban.
    }
}
