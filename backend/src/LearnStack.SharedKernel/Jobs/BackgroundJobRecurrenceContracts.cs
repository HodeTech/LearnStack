namespace LearnStack.SharedKernel.Jobs;

/// <summary>
/// The sole recurrence input. It deliberately does not implement IBackgroundJobParameters;
/// only the stored-recipient path may create a child job with this contract.
/// </summary>
public sealed record BackgroundJobOccurrenceParameters(BackgroundJobOccurrenceId OccurrenceId);

public enum BackgroundJobRecipientPolicy
{
    ProvisionedNonArchivedTenants,
    ServingTenants,
    ServingOrganizations,
}

public enum BackgroundJobRecurrenceSchedule
{
    Interval,
    Daily,
}

public enum BackgroundJobMisfirePolicy
{
    CoalesceLatest,
}

/// <summary>
/// Immutable code manifest. Key is lowercase ASCII module.name (underscores admitted),
/// at most 150 characters; revision is separate from the logical recurrence identity.
/// The registry requires UTC instants and validates interval, daily and jitter bounds.
/// </summary>
public sealed class BackgroundJobRecurrenceRegistration(
    string key, Type declaringType, long revision, DateTimeOffset activatesAt,
    BackgroundJobKey<BackgroundJobOccurrenceParameters> target,
    BackgroundJobRecurrenceSchedule schedule, DateTimeOffset anchor, TimeSpan period,
    TimeSpan jitter, BackgroundJobRecipientPolicy recipientPolicy,
    BackgroundJobMisfirePolicy misfirePolicy = BackgroundJobMisfirePolicy.CoalesceLatest)
{
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));
    public Type DeclaringType { get; } = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
    public long Revision { get; } = revision;
    public DateTimeOffset ActivatesAt { get; } = activatesAt;
    public BackgroundJobKey<BackgroundJobOccurrenceParameters> Target { get; } = target;
    public BackgroundJobRecurrenceSchedule Schedule { get; } = schedule;
    public DateTimeOffset Anchor { get; } = anchor;
    public TimeSpan Period { get; } = period;
    public TimeSpan Jitter { get; } = jitter;
    public BackgroundJobRecipientPolicy RecipientPolicy { get; } = recipientPolicy;
    public BackgroundJobMisfirePolicy MisfirePolicy { get; } = misfirePolicy;
}
