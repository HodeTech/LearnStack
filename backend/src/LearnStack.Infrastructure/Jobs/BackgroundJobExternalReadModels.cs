using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobExternalReadStatus { Completed, Refused, Failed }

/// <summary>
/// Exact acknowledged app read and cleanup provenance. Snapshot data does not
/// grant provider entry, a checkpoint, quiescence or current ownership.
/// </summary>
internal sealed class JobExternalReadHandle
{
    private readonly ConfirmedStartReceipt? _original;
    private readonly BackgroundJobExternalRecoveryBinding? _recovery;

    internal JobExternalReadHandle(ConfirmedStartReceipt original, ReadBackgroundJobExternalStateQuery read)
    {
        _original = original;
        Read = read;
        if (read.Authority.IsRecovery || !ReferenceEquals(read.Authority.SourceOwner, original.SourceOwner)
            || !ReferenceEquals(read.Authority.Run, original.Run)) throw Refused();
        RequireRecorded();
    }

    internal JobExternalReadHandle(BackgroundJobExternalRecoveryBinding recovery, ReadBackgroundJobExternalStateQuery read)
    {
        _recovery = recovery;
        Read = read;
        if (!read.Authority.IsRecovery || !ReferenceEquals(read.Authority.SourceOwner, recovery.SourceOwner)
            || !ReferenceEquals(read.Authority.Run, recovery.Run)) throw Refused();
        RequireRecorded();
    }

    private ReadBackgroundJobExternalStateQuery Read { get; }
    internal BackgroundJobExternalSnapshot Snapshot => Read.RecordedSnapshot ?? throw Refused();

    internal BackgroundJobExternalSnapshot Require(ConfirmedStartReceipt original)
    {
        if (!ReferenceEquals(_original, original) || original.Run.IsClosed) throw Refused();
        RequireRecorded();
        return Snapshot;
    }

    internal BackgroundJobExternalSnapshot Require(BackgroundJobExternalRecoveryBinding recovery)
    {
        if (!ReferenceEquals(_recovery, recovery) || recovery.Run.IsClosed) throw Refused();
        RequireRecorded();
        return Snapshot;
    }

    private void RequireRecorded()
    {
        if (!Read.CommitAcknowledged || Read.RecordedSnapshot?.Envelope is not { } envelope
            || !Read.Authority.MatchesEnvelope(envelope) || !Read.Authority.SourceOwner.CleanupConfirmed)
            throw Refused();
    }

    private static InvalidOperationException Refused() =>
        new("The external read handle requires its exact acknowledged read and clean source.");
}

/// <summary>Actual read completion; failed scope owners and unpublished candidates stay retained.</summary>
internal sealed class JobExternalReadResult(JobExternalReadStatus status, JobExternalReadHandle? handle,
    bool cleanupFailed, ReadBackgroundJobExternalStateQuery request, IReadOnlyList<JobStartCleanupOwnership> retained)
{
    internal JobExternalReadStatus Status { get; } = status;
    internal JobExternalReadHandle? Handle { get; } = handle;
    internal bool CleanupFailed { get; } = cleanupFailed;
    internal bool CommitAcknowledged => Request.CommitAcknowledged;
    internal JobApplicationSourceOwner SourceOwner => Request.Authority.SourceOwner;
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; } = retained;
    // Retains the exact candidate/owner even when publication is refused.
    internal ReadBackgroundJobExternalStateQuery Request { get; } = request;
}
