using System.Diagnostics.Metrics;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>Counters a monitoring owner can alert on without searching logs (ADR-0101 § 14).</summary>
public static class PlatformOperatorMetrics
{
    /// <summary>The meter name; covered by the host's <c>learnstack.*</c> subscription.</summary>
    public const string MeterName = "learnstack.platform_operator";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Admissions =
        Meter.CreateCounter<long>("learnstack.platform_operator.admissions", description: "Operator admissions by outcome and operation.");

    private static readonly Counter<long> Refusals =
        Meter.CreateCounter<long>("learnstack.platform_operator.refusals", description: "Operator refusals by cause.");

    private static readonly Counter<long> Results =
        Meter.CreateCounter<long>("learnstack.platform_operator.results", description: "MUST result rows written by operation.");

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

    /// <summary>Counts a written result row.</summary>
    public static void Result(PlatformOperation operation) =>
        Results.Add(1, new KeyValuePair<string, object?>("operation", operation.AuditSlug));
}
