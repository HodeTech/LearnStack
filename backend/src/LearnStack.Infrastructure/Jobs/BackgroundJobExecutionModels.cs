using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Finite business policy, durable facts and concrete cleanup ownership are distinct.</summary>
internal sealed class JobBusinessExecutionResult(BackgroundJobFailureDecision decision,
    bool commitAcknowledged, bool commitStarted, bool knownRollback, bool durableOutcomeUncertain, bool cleanupFailed,
    ConfirmedStartReceipt receipt, BackgroundJobCommitGate gate, bool executionIssued,
    IReadOnlyList<JobStartCleanupOwnership>? retainedCleanup = null, JobExternalReadResult? finalizationRead = null)
{
    internal BackgroundJobFailureDecision Decision { get; } = decision;
    internal bool CommitAcknowledged { get; } = commitAcknowledged;
    internal bool CommitStarted { get; } = commitStarted;
    internal bool KnownRollback { get; } = knownRollback;
    internal bool DurableOutcomeUncertain { get; } = durableOutcomeUncertain;
    internal bool CleanupFailed { get; } = cleanupFailed;
    internal ConfirmedStartReceipt Receipt { get; } = receipt;
    internal BackgroundJobCommitGate Gate { get; } = gate;
    internal bool ExecutionIssued { get; } = executionIssued;
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; } = retainedCleanup ?? [];
    internal JobExternalReadResult? FinalizationRead { get; } = finalizationRead;
    internal JobApplicationSourceOwner SourceOwner => Receipt.SourceOwner;
}
