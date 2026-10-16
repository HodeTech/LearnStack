namespace LearnStack.Infrastructure.Jobs;

/// <summary>Finite business policy, durable facts and concrete cleanup ownership are distinct.</summary>
internal sealed record JobBusinessExecutionResult(BackgroundJobFailureDecision Decision,
    bool CommitAcknowledged, bool CommitStarted, bool KnownRollback, bool DurableOutcomeUncertain, bool CleanupFailed)
{
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; init; } = [];
    internal required SharedKernel.Jobs.JobApplicationSourceOwner SourceOwner { get; init; }
}
