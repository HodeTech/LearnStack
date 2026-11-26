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

    /// <summary>Onboarding acts by <c>operation</c> and <c>outcome</c> (applied, replayed, refused, failed).</summary>
    public const string ActCounterName = "learnstack_platform_operator_acts_total";

    /// <summary>Acts refused with 503 because a MUST row could not be written, by <c>operation</c>.</summary>
    public const string AuditFailureCounterName = "learnstack_platform_operator_audit_failures_total";

    /// <summary>Recovery transitions by <c>operation</c> and <c>outcome</c> (ADR-0103 § 10).</summary>
    public const string RecoveryCounterName = "learnstack_platform_operator_recoveries_total";

    /// <summary>Federation operator calls by <c>operation</c> and <c>outcome</c> (ADR-0099 Amendment 10).</summary>
    public const string FederationCounterName = "learnstack_platform_operator_federation_total";

    /// <summary>Data-request operator calls by <c>operation</c> and <c>outcome</c> (ADR-0107 § 8).</summary>
    public const string DataRequestCounterName = "learnstack_platform_operator_data_requests_total";

    /// <summary>Issuer metadata or key fetches that failed or were refused (no label).</summary>
    public const string MetadataFailureCounterName = "learnstack_platform_operator_metadata_failures_total";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Admissions =
        Meter.CreateCounter<long>(AdmissionCounterName, description: "Operator admissions by outcome and operation.");

    private static readonly Counter<long> Refusals =
        Meter.CreateCounter<long>(RefusalCounterName, description: "Operator refusals by cause.");

    private static readonly Counter<long> Results =
        Meter.CreateCounter<long>(ResultCounterName, description: "MUST result rows committed by operation.");

    private static readonly Counter<long> Acts =
        Meter.CreateCounter<long>(ActCounterName, description: "Operator onboarding acts by operation and outcome.");

    private static readonly Counter<long> Recoveries =
        Meter.CreateCounter<long>(RecoveryCounterName, description: "Operator recovery transitions by operation and outcome.");

    private static readonly Counter<long> FederationCalls =
        Meter.CreateCounter<long>(FederationCounterName, description: "Operator federation calls by operation and outcome.");

    private static readonly Counter<long> DataRequestCalls =
        Meter.CreateCounter<long>(DataRequestCounterName, description: "Operator data-request calls by operation and outcome.");

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
        Refusals.Add(1, new KeyValuePair<string, object?>("cause", Cause(refusal)));
    }

    private static string Cause(PlatformRefusal refusal) => refusal switch
    {
        PlatformRefusal.InvalidWitness => "invalid_witness",
        PlatformRefusal.UnknownOperation => "unknown_operation",
        PlatformRefusal.InvalidTicket => "invalid_ticket",
        PlatformRefusal.MissingPermission => "missing_permission",
        _ => "unknown",
    };

    /// <summary>Counts a pre-authentication refusal, which never reaches the gate.</summary>
    public static void Unauthenticated(string cause) =>
        Refusals.Add(1, new KeyValuePair<string, object?>("cause", cause));

    /// <summary>Counts a result row that committed with its state change.</summary>
    public static void Result(PlatformOperation operation) =>
        Results.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug));

    /// <summary>
    /// Counts one onboarding act by how it ended: <c>applied</c> wrote its state, <c>replayed</c> found the state an
    /// earlier run of the same act wrote and so is a setup resumed at that step, <c>refused</c> returned a failure
    /// result, and <c>failed</c> threw.
    /// </summary>
    public static void Act(PlatformOperation operation, PlatformOperatorActOutcome outcome) =>
        Acts.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug),
            new KeyValuePair<string, object?>("outcome", outcome switch
            {
                PlatformOperatorActOutcome.Applied => "applied",
                PlatformOperatorActOutcome.Replayed => "replayed",
                PlatformOperatorActOutcome.Refused => "refused",
                _ => "failed",
            }));

    /// <summary>
    /// Counts one recovery transition by how it ended: <c>applied</c>, <c>stale_fence</c>, <c>not_recoverable</c> or
    /// <c>not_found</c>. A fence refusal is its own label so a monitoring owner can see a replay storm.
    /// </summary>
    public static void Recovery(PlatformOperation operation, OperatorRecoveryOutcome outcome) =>
        Recoveries.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug),
            new KeyValuePair<string, object?>("outcome", outcome switch
            {
                OperatorRecoveryOutcome.Applied => "applied",
                OperatorRecoveryOutcome.StaleFence => "stale_fence",
                OperatorRecoveryOutcome.NotRecoverable => "not_recoverable",
                _ => "not_found",
            }));

    /// <summary>Counts a recovery the owner could not finish: <c>unavailable</c>, or <c>commit_unknown</c> when it may have landed.</summary>
    public static void RecoveryUnavailable(PlatformOperation operation, bool commitOutcomeUnknown) =>
        Recoveries.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug),
            new KeyValuePair<string, object?>("outcome", commitOutcomeUnknown ? "commit_unknown" : "unavailable"));

    /// <summary>
    /// Counts one federation call by how it ended: a report's <c>ok</c>, <c>invalid_cursor</c> or <c>unavailable</c>, or an
    /// act's <c>applied</c>, <c>already_applied</c>, <c>not_found</c>, <c>not_owned</c>, <c>live_connection</c>,
    /// <c>not_applicable</c>, <c>refused</c>, <c>unavailable</c> or <c>uncertain</c>.
    /// </summary>
    public static void Federation(PlatformOperation operation, string outcome) =>
        FederationCalls.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// Counts one data-request call by how it ended: an inspection's <c>ok</c> or <c>invalid_cursor</c>, or a completion's
    /// <c>completed</c>, <c>already_completed</c>, <c>not_found</c>, <c>not_erasure</c>, <c>closure_fanout</c> or
    /// <c>not_awaiting</c>; either may end <c>unavailable</c>, or <c>commit_unknown</c> when a completion's commit may
    /// have landed. A refused MUST row is <c>audit_unavailable</c> and an unexpected failure (answered 500) is
    /// <c>failed</c>. A closure's fan-out counts each participant group once: <c>fan_out_started</c>,
    /// <c>fan_out_adopted</c>, <c>fan_out_waiting</c> or <c>fan_out_failed</c>.
    /// </summary>
    public static void DataRequest(PlatformOperation operation, string outcome) =>
        DataRequestCalls.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Counts an act that was refused because its MUST row could not be written.</summary>
    public static void AuditFailed(PlatformOperation operation) =>
        AuditFailures.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug));
}

/// <summary>How an onboarding act ended, the closed label set of the act counter.</summary>
public enum PlatformOperatorActOutcome
{
    /// <summary>The act wrote its state.</summary>
    Applied = 1,

    /// <summary>The act found the state an earlier run wrote and wrote nothing.</summary>
    Replayed = 2,

    /// <summary>The act returned a failure result.</summary>
    Refused = 3,

    /// <summary>The act threw.</summary>
    Failed = 4,
}
