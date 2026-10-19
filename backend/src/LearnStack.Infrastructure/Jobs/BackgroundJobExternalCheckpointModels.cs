using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobExternalCheckpointStatus { Completed, Refused, Failed, Indeterminate }

/// <summary>
/// Exact write and its physical completion facts, independently of cleanup.
/// An unknown COMMIT never becomes acknowledged because its task has finished.
/// </summary>
internal sealed class JobExternalCheckpointResult
{
    internal JobExternalCheckpointResult(JobExternalCheckpointStatus status, RecordBackgroundJobArmCommand? arm,
        RecordBackgroundJobOutcomeCommand? outcome, bool issued, bool cleanupFailed, IReadOnlyList<JobStartCleanupOwnership> retained)
    {
        if ((arm is null) == (outcome is null)) throw new ArgumentException("A checkpoint result retains one exact write.");
        Status = status;
        Arm = arm;
        Outcome = outcome;
        Issued = issued;
        CleanupFailed = cleanupFailed;
        RetainedCleanup = retained;
    }

    internal JobExternalCheckpointStatus Status { get; }
    internal RecordBackgroundJobArmCommand? Arm { get; }
    internal RecordBackgroundJobOutcomeCommand? Outcome { get; }
    internal BackgroundJobCommitGate Gate => Arm?.Gate ?? Outcome!.Gate;
    internal BackgroundJobCheckpointKind Kind => Gate.Kind;
    internal bool Issued { get; }
    internal bool CommitStarted => Issued && Gate.CommitStarted;
    internal bool CommitAcknowledged => Arm?.CommitAcknowledged ?? Outcome!.CommitAcknowledged;
    internal bool KnownRollback => Issued && Gate.KnownRollback;
    internal bool CommitUncertain => CommitStarted && !CommitAcknowledged && !KnownRollback;
    internal bool CleanupFailed { get; }
    internal JobApplicationSourceOwner SourceOwner => Arm?.Authority.SourceOwner ?? Outcome!.Authority.SourceOwner;
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; }
}
