using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The real-time half: a structured warning and a counter.
/// </summary>
/// <remarks>
/// Shipped alone in Packet 4 and still the registered inner half in Packet 9, where
/// <see cref="AuditingTenantAssertionRecorder"/> decorates it with the <c>audit_log</c>
/// row. It stays a separate type on purpose: the counter and the warning cost no I/O and
/// are what a deployment still has when the audit store is unreachable, and the
/// architecture rule that says only one file may name these two counters is easier to keep
/// true of a file that does nothing else.
/// <para>
/// The metric labels are fixed and bounded — tenant id, dimension, source, and
/// whether a principal was attached. Per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see>, the effective host and the source IP
/// are <b>never</b> labels: both are attacker-chosen and unbounded, and a
/// cardinality explosion in the metrics store is a self-inflicted outage.
/// </para>
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

    /// <inheritdoc />
    /// <remarks>
    /// Completed synchronously, and it still satisfies the seam. Nothing here does I/O —
    /// which is exactly why this type is worth keeping registrable on its own: a
    /// deployment that has no application credential, or one deliberately running without
    /// the audit store, still counts and still warns.
    /// </remarks>
    public Task RecordRejectionAsync(
        TenantAssertionRejection rejection, CancellationToken cancellationToken = default)
    {
        Record(rejection);
        return Task.CompletedTask;
    }

    /// <summary>The metric and the warning, for a decorator that has more to do after.</summary>
    public void Record(TenantAssertionRejection rejection)
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
            + "asserted {AssertedValue}. Authenticated: {IsAuthenticated}. An authenticated "
            + "mismatch is audited per occurrence; an anonymous one is audited as a burst.");

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
