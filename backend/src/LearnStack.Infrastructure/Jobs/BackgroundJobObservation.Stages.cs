using System.Diagnostics;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobObservedStage
{
    Admission, Transactional, Prepare, Finalize, Execute, Reconcile,
    ExternalRead, Arm, Outcome, NoStart, SelectedOutcome, CapacityCleanup, RecipientSubmission, UnknownBusiness,
}

internal enum JobObservedOutcome
{
    Acknowledged, Failed, UnknownCommit, Refused, Invalid, LostClaim, Canceled, CapacityDeferred,
    Retry, Terminal, Retained, NotCommitted, ProviderSucceeded, ProviderFailed,
    ProviderCancelledRepeatable, ProviderCancelledNotRepeatable, Running, NeverStarted,
    UnknownEffect, ProviderFault, UnissuedProviderFault,
}

public sealed partial class BackgroundJobObservation
{
    internal long? StartStage()
    {
        try { return _time.GetTimestamp(); }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            Interlocked.Exchange(ref _faulted, 1);
            return null;
        }
    }

    // Called only after the actual executor has joined its owned cleanup. A
    // listener may block this actual task, but cannot change its durable facts.
    internal void RecordStage(long? started, string? definition, string? profile, JobObservedStage stage,
        JobObservedOutcome outcome, bool cleanupFailed, string? reason = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Enum.IsDefined(stage) || !Enum.IsDefined(outcome))
        {
            Interlocked.Exchange(ref _faulted, 1);
            return;
        }
        var name = definition is null ? "unknown" : _jobs.Contains(definition) ? definition
            : _retiredJobs.Contains(definition) ? "retired" : "unknown";
        var boundedProfile = profile is "transactional" or "external" or "control" ? profile : "unknown";
        var stageName = StageName(stage);
        var tags = new TagList
        {
            { "job_name", name }, { "profile", boundedProfile }, { "stage", stageName },
            { "outcome", outcome.ToString() }, { "cleanup", cleanupFailed ? "retained" : "clean" },
        };
        try
        {
            if (started is { } timestamp)
                _duration.Record(Math.Max(0, _time.GetElapsedTime(timestamp, _time.GetTimestamp()).TotalSeconds), tags);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { Interlocked.Exchange(ref _faulted, 1); }

        var counterTags = new TagList
        {
            { "job_name", name }, { "profile", boundedProfile }, { "stage", stageName },
            { "reason", BoundedReason(outcome, reason) },
        };
        try
        {
            if (outcome is JobObservedOutcome.Failed or JobObservedOutcome.Retry or JobObservedOutcome.Terminal
                or JobObservedOutcome.ProviderFailed)
                _failed.Add(1, counterTags);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { Interlocked.Exchange(ref _faulted, 1); }
        try
        {
            if (outcome is JobObservedOutcome.UnknownCommit or JobObservedOutcome.UnknownEffect or JobObservedOutcome.ProviderFault)
                _unknown.Add(1, counterTags);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { Interlocked.Exchange(ref _faulted, 1); }
    }

    internal static JobObservedStage BusinessStage(BackgroundJobExecutionStage? stage) => stage switch
    {
        BackgroundJobExecutionStage.Transactional => JobObservedStage.Transactional,
        BackgroundJobExecutionStage.Prepare => JobObservedStage.Prepare,
        BackgroundJobExecutionStage.Finalize => JobObservedStage.Finalize,
        _ => JobObservedStage.UnknownBusiness,
    };

    private static string StageName(JobObservedStage stage) => stage switch
    {
        JobObservedStage.Admission => "admission",
        JobObservedStage.Transactional => "transactional",
        JobObservedStage.Prepare => "prepare",
        JobObservedStage.Finalize => "finalize",
        JobObservedStage.Execute => "execute",
        JobObservedStage.Reconcile => "reconcile",
        JobObservedStage.ExternalRead => "external_read",
        JobObservedStage.Arm => "arm",
        JobObservedStage.Outcome => "outcome",
        JobObservedStage.NoStart => "no_start",
        JobObservedStage.SelectedOutcome => "selected_outcome",
        JobObservedStage.CapacityCleanup => "capacity_cleanup",
        JobObservedStage.RecipientSubmission => "recipient_submission",
        JobObservedStage.UnknownBusiness => "unknown_business",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string BoundedReason(JobObservedOutcome outcome, string? reason) => outcome switch
    {
        JobObservedOutcome.UnknownCommit => "unknown_commit",
        JobObservedOutcome.UnknownEffect => "unknown_effect",
        JobObservedOutcome.ProviderFault => "provider_unconfirmed",
        JobObservedOutcome.ProviderFailed => reason is "Permanent" or "TransientRepeatable" or "TransientNotRepeatable" ? reason : "invalid_policy",
        _ => reason is "concurrency_conflict" or "rate_limited" or "dependency_unavailable" or "audit_unavailable"
            or "permanent_failure" or "unexpected_fault" ? reason : "stage_failed",
    };
}
