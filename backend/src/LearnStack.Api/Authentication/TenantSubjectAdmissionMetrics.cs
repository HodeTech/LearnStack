using System.Diagnostics.Metrics;

namespace LearnStack.Api.Authentication;

/// <summary>Closed, identity-free admission and sticky cleanup-fault signals.</summary>
internal static class TenantSubjectAdmissionMetrics
{
    internal const string MeterName = "LearnStack.Api";
    internal const string RejectedCounterName = "learnstack_tenant_subject_admission_rejected_total";
    internal const string CleanupFailedCounterName = "learnstack_tenant_subject_cleanup_failed_total";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(RejectedCounterName);
    private static readonly Counter<long> CleanupFailed = Meter.CreateCounter<long>(CleanupFailedCounterName);

    internal static void RecordRejected(SubjectAdmissionOutcome outcome)
    {
        var reason = outcome switch
        {
            SubjectAdmissionOutcome.QuotaExhausted => "quota",
            SubjectAdmissionOutcome.RegistryCapacity => "registry_capacity",
            SubjectAdmissionOutcome.RegistryFault => "registry_fault",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        Rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    internal static void RecordCleanupFailure() => CleanupFailed.Add(1);
}
