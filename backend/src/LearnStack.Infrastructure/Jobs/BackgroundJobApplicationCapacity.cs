namespace LearnStack.Infrastructure.Jobs;

/// <summary>The job-only application checkout bound; unrelated HTTP demand needs its own headroom.</summary>
public sealed record BackgroundJobApplicationCapacity(int InvocationOwners, int RequiredConnections)
{
    public static BackgroundJobApplicationCapacity Calculate(BackgroundJobInfrastructureRegistry topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var invocations = topology.Pools.Values
            .Where(pool => pool.Kind == BackgroundJobWorkerPoolKind.Transactional)
            .Sum(pool => BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers));
        // One sequential framework/business unit and one independently tracked
        // auxiliary operation per invocation, including timed-out cache flights.
        return new(invocations, checked(2 * invocations));
    }

    public void Validate(BackgroundJobApplicationPoolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxPoolSize < 1 || settings.Pooling && settings.MaxPoolSize < RequiredConnections)
            throw new ArgumentException("The background job application source does not meet its job-only physical capacity.", nameof(settings));
    }
}
