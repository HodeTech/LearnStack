namespace LearnStack.Infrastructure.Jobs;

/// <summary>The job-only application checkout bound; unrelated HTTP demand needs its own headroom.</summary>
public sealed record BackgroundJobApplicationCapacity(int InvocationOwners, int RequiredConnections)
{
    public int RecipientOwners { get; } = BackgroundJobRecurrenceControl.ApplicationOwnerLimit;
    public static BackgroundJobApplicationCapacity Calculate(BackgroundJobInfrastructureRegistry topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var invocations = topology.Pools.Values
            .Where(pool => pool.Kind is BackgroundJobWorkerPoolKind.Transactional or BackgroundJobWorkerPoolKind.External)
            .Sum(pool => BackgroundJobRuntimeOptions.RetainedHardBound(pool.Workers));
        // Transactional work and external prepare/checkpoint/finalize/recovery
        // each use one sequential main unit and one tracked auxiliary operation.
        // A timed-out cache waiter still owns its actual auxiliary cleanup.
        // The one terminal-lane no-start owner adds its own main/auxiliary pair.
        // Four actual recipient owners also retain their main/auxiliary pairs.
        return new(invocations, checked(2 * invocations + 2 + 2 * BackgroundJobRecurrenceControl.ApplicationOwnerLimit));
    }

    public void Validate(BackgroundJobApplicationPoolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxPoolSize < 1 || settings.Pooling && settings.MaxPoolSize < RequiredConnections)
            throw new ArgumentException("The background job application source does not meet its job-only physical capacity.", nameof(settings));
    }
}
