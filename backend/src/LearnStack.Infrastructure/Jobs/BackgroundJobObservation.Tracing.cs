using System.Diagnostics;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobTraceOperation
{
    Admission, Execution, ExternalExecute, ExternalReconcile, ExternalRead,
    ExternalCheckpoint, CapacityCleanup, RecipientSubmission, Terminal,
}

public sealed partial class BackgroundJobObservation
{
    // Creation never starts the Activity. Its caller owns it before invoking
    // arbitrary Start observers and retains that owner through actual cleanup.
    internal Activity CreateActivity(JobTraceOperation operation, ActivityContext parent)
    {
        var name = operation switch
        {
            JobTraceOperation.Admission => "job.admission",
            JobTraceOperation.Execution => "job.execution",
            JobTraceOperation.ExternalExecute => "job.external_execute",
            JobTraceOperation.ExternalReconcile => "job.external_reconcile",
            JobTraceOperation.ExternalRead => "job.external_read",
            JobTraceOperation.ExternalCheckpoint => "job.external_checkpoint",
            JobTraceOperation.CapacityCleanup => "job.capacity_cleanup",
            JobTraceOperation.RecipientSubmission => "job.recipient_submission",
            JobTraceOperation.Terminal => "job.terminal",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        // A fresh-root caller clears Activity.Current before creation AND Start;
        // a default ActivityContext alone would inherit the ambient parent.
        try
        {
            if (_activities.CreateActivity(name, ActivityKind.Internal, parent, idFormat: ActivityIdFormat.W3C) is { } activity)
                return activity;
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            Interlocked.Exchange(ref _faulted, 1);
        }
        var fallback = new Activity(name).SetIdFormat(ActivityIdFormat.W3C);
        if (parent != default) fallback.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        return fallback;
    }
}
