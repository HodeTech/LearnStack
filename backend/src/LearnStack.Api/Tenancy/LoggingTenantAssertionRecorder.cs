using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The Packet 4 recorder: a structured warning and a counter. Packet 9 replaces
/// the registration with an auditing implementation once <c>IAuditStore</c> and
/// <c>audit_log</c> exist.
/// </summary>
/// <remarks>
/// The metric labels are fixed and bounded — tenant id, dimension, source, and
/// whether a principal was attached. Per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see>, the effective host and the source IP
/// are <b>never</b> labels: both are attacker-chosen and unbounded, and a
/// cardinality explosion in the metrics store is a self-inflicted outage.
/// </remarks>
public sealed class LoggingTenantAssertionRecorder : ITenantAssertionRecorder
{
    /// <summary>The meter name every LearnStack metric hangs off.</summary>
    public const string MeterName = "LearnStack.Api";

    public const string MismatchCounterName = "learnstack_tenant_assertion_mismatch_total";
    public const string UnresolvedCounterName = "learnstack_tenant_assertion_unresolved_total";

    private readonly ILogger<LoggingTenantAssertionRecorder> _logger;
    private readonly Counter<long> _mismatches;
    private readonly Counter<long> _unresolved;

    public LoggingTenantAssertionRecorder(
        ILogger<LoggingTenantAssertionRecorder> logger,
        IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _logger = logger;

        var meter = meterFactory.Create(MeterName);
        _mismatches = meter.CreateCounter<long>(MismatchCounterName);
        _unresolved = meter.CreateCounter<long>(UnresolvedCounterName);
    }

    public void RecordRejection(TenantAssertionRejection rejection)
    {
        _mismatches.Add(
            1,
            new KeyValuePair<string, object?>("tenant", rejection.ResolvedTenantId),
            new KeyValuePair<string, object?>("dimension", rejection.Dimension.ToString()),
            new KeyValuePair<string, object?>("source", "header"),
            new KeyValuePair<string, object?>("authenticated", rejection.IsAuthenticated));

        // Warning, not Error: the request was refused, which is the system
        // working. It is worth a human's attention because the usual cause is
        // a misconfigured BFF or a stale host mapping — a failure that is
        // otherwise silent, because the response is a valid page for the wrong
        // tenant.
        AssertionRejected(
            _logger,
            rejection.Dimension.ToString(),
            rejection.ResolvedTenantId,
            rejection.AssertedValue,
            rejection.IsAuthenticated,
            null);
    }

    // LoggerMessage source-generated delegate (CA1848), matching the house
    // pattern in LoggingBehavior and AuditLogBehavior.
    private static readonly Action<ILogger, string, Guid, Guid, bool, Exception?> AssertionRejected =
        LoggerMessage.Define<string, Guid, Guid, bool>(
            LogLevel.Warning,
            new EventId(4001, nameof(AssertionRejected)),
            "Rejected a {Dimension} assertion on tenant {ResolvedTenantId}: the client "
            + "asserted {AssertedValue}. Authenticated: {IsAuthenticated}. Recorded, not "
            + "audited — IAuditStore lands in Packet 9.");

    public void RecordUnresolved(TenantAssertionDimension dimension)
    {
        // No tenant label: there is no tenant. Adding one would mean inventing
        // a sentinel, and a sentinel tenant is an unauthenticated, unbounded
        // write target no tenant admin watches.
        //
        // The dimension IS labelled. It is bounded by the enum, and without it
        // the counter cannot answer the first question an operator asks —
        // whether the malformed header was the tenant's or the organization's.
        // The parameter was accepted and dropped, which made the signature
        // promise something the metric did not carry.
        _unresolved.Add(
            1,
            new KeyValuePair<string, object?>("source", "header"),
            new KeyValuePair<string, object?>("dimension", dimension.ToString()));
    }
}
