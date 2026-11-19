using System.Diagnostics.Metrics;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>Counters a monitoring owner can alert on without searching logs (ADR-0101 § 14).</summary>
/// <remarks>
/// Names follow the platform convention (<c>learnstack_*_total</c>, Standard 10), and every
/// label is a closed set: an operation slug, a refusal cause or a token-failure cause, never a
/// value a caller wrote.
/// </remarks>
public static class PlatformOperatorMetrics
{
    /// <summary>The meter name; covered by the host's <c>learnstack.*</c> subscription.</summary>
    public const string MeterName = "learnstack.platform_operator";

    /// <summary>Admissions by <c>outcome</c> and <c>operation</c>.</summary>
    public const string AdmissionCounterName = "learnstack_platform_operator_admissions_total";

    /// <summary>Refusals by <c>cause</c>, at the gate and before it.</summary>
    public const string RefusalCounterName = "learnstack_platform_operator_refusals_total";

    /// <summary>MUST result rows committed, by <c>operation</c>.</summary>
    public const string ResultCounterName = "learnstack_platform_operator_results_total";

    /// <summary>Acts refused with 503 because a MUST row could not be written, by <c>operation</c>.</summary>
    public const string AuditFailureCounterName = "learnstack_platform_operator_audit_failures_total";

    /// <summary>Issuer metadata or key fetches that failed or were refused (no label).</summary>
    public const string MetadataFailureCounterName = "learnstack_platform_operator_metadata_failures_total";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Admissions =
        Meter.CreateCounter<long>(AdmissionCounterName, description: "Operator admissions by outcome and operation.");

    private static readonly Counter<long> Refusals =
        Meter.CreateCounter<long>(RefusalCounterName, description: "Operator refusals by cause.");

    private static readonly Counter<long> Results =
        Meter.CreateCounter<long>(ResultCounterName, description: "MUST result rows committed by operation.");

    private static readonly Counter<long> AuditFailures =
        Meter.CreateCounter<long>(AuditFailureCounterName, description: "Operator acts refused because the MUST row was not durable.");

    private static readonly Counter<long> MetadataFailures =
        Meter.CreateCounter<long>(
            MetadataFailureCounterName, description: "Operator issuer metadata or key fetches that failed or were refused.");

    /// <summary>Counts a failed or refused issuer metadata fetch; every operator is refused while it persists.</summary>
    public static void MetadataFailed() => MetadataFailures.Add(1);

    /// <summary>Counts an admission.</summary>
    public static void Admitted(PlatformOperation operation) =>
        Admissions.Add(1, new KeyValuePair<string, object?>("outcome", "admitted"),
            new KeyValuePair<string, object?>("operation", operation.AuditSlug));

    /// <summary>Counts a refusal by its cause.</summary>
    public static void Refused(PlatformRefusal refusal, PlatformOperation? operation)
    {
        Admissions.Add(1, new KeyValuePair<string, object?>("outcome", "refused"),
            new KeyValuePair<string, object?>("operation", operation?.AuditSlug ?? "unknown"));
        Refusals.Add(1, new KeyValuePair<string, object?>("cause", refusal.ToString()));
    }

    /// <summary>Counts a pre-authentication refusal, which never reaches the gate.</summary>
    public static void Unauthenticated(string cause) =>
        Refusals.Add(1, new KeyValuePair<string, object?>("cause", cause));

    /// <summary>Counts a result row that committed with its state change.</summary>
    public static void Result(PlatformOperation operation) =>
        Results.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug));

    /// <summary>Counts an act that was refused because its MUST row could not be written.</summary>
    public static void AuditFailed(PlatformOperation operation) =>
        AuditFailures.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug));
}
