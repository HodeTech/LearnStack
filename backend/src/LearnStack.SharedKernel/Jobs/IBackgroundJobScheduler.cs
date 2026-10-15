namespace LearnStack.SharedKernel.Jobs;

/// <summary>Declares identifier-only ordinary job input in its owning Application.Contracts assembly.</summary>
/// <remarks>Use a sealed record with public init-only identifier auto-properties; custom accessors are refused.</remarks>
public interface IBackgroundJobParameters { }

/// <summary>A typed lookup key, never submission authority.</summary>
/// <remarks>The registry admits lowercase ASCII module.job_name.vN, with underscore-separated names.</remarks>
public readonly record struct BackgroundJobKey<TParameters>(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Inserts module-owned intent on the current enrolled writable transaction.</summary>
/// <remarks>
/// Tenant, organization, producer module and causal metadata come from trusted enrollment.
/// Successful INSERT returns an application-minted identity; it does not commit the owner.
/// Recurrence input deliberately does not implement IBackgroundJobParameters and cannot use this port.
/// </remarks>
public interface IBackgroundJobScheduler
{
    Task<BackgroundJobId> EnqueueAsync<TParameters>(
        BackgroundJobKey<TParameters> definition, TParameters parameters,
        CancellationToken cancellationToken = default)
        where TParameters : IBackgroundJobParameters;

    Task<BackgroundJobId> ScheduleAsync<TParameters>(
        BackgroundJobKey<TParameters> definition, TParameters parameters, DateTimeOffset dueAt,
        CancellationToken cancellationToken = default)
        where TParameters : IBackgroundJobParameters;
}
