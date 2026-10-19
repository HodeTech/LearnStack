using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Tenancy;

/// <summary>Which client assertion disagreed with what the API resolved.</summary>
public enum TenantAssertionDimension
{
    Tenant,
    Organization,
}

/// <summary>Whether the rejected assertion came from a header or validated token.</summary>
public enum TenantAssertionSource
{
    Header,
    JwtClaim,
}

internal static class TenantAssertionSourceExtensions
{
    public static string ToLabel(this TenantAssertionSource source) => source switch
    {
        TenantAssertionSource.Header => "header",
        TenantAssertionSource.JwtClaim => "jwt-claim",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown assertion source."),
    };
}

/// <summary>
/// One rejected assertion, as
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see> defines it.
/// </summary>
/// <param name="ResolvedTenantId">
/// The tenant the record is written under — <b>always</b> the resolved one,
/// never the asserted one. Writing the asserted id would mean setting
/// <c>app.tenant_id</c> to an attacker-chosen value, handing an anonymous
/// client a primitive that writes rows into an arbitrary tenant's audit log.
/// </param>
/// <param name="Dimension">Tenant or organization.</param>
/// <param name="AssertedValue">
/// What the client claimed. Kept as a <see cref="Guid"/>, so it is an opaque
/// identifier and never attacker-authored free text.
/// </param>
/// <param name="IsAuthenticated">
/// Whether a validated principal was attached. The two tiers have different
/// amplification profiles and ADR-0036 treats them differently: an
/// authenticated durable attempt spends a shared target-tenant allowance (ADR-0065);
/// anonymous occurrences use a separate burst window.
/// </param>
/// <param name="Source">Header or validated JWT claim.</param>
/// <param name="ActorUserId">The validated actor, absent for an anonymous request.</param>
/// <param name="CorrelationId">Captured request correlation, including on failed resolution.</param>
/// <param name="TrustedOrganizationId">
/// The independently classified host organization, or the resolved organization for a
/// later header refusal. Metadata only; the durable row remains tenant-wide.
/// </param>
public readonly record struct TenantAssertionRejection(
    Guid ResolvedTenantId,
    TenantAssertionDimension Dimension,
    Guid AssertedValue,
    bool IsAuthenticated,
    TenantAssertionSource Source,
    UserId? ActorUserId,
    string? CorrelationId,
    OrganizationId? TrustedOrganizationId);

/// <summary>
/// A membership refusal with no independently defended tenant. It carries attribution,
/// but no claimed tenant or value that could become a durable audit destination.
/// </summary>
public readonly record struct TenantNoTenantRefusal(
    TenantAssertionSource Source,
    TenantAssertionDimension Dimension,
    UserId ActorUserId,
    string? CorrelationId);

/// <summary>
/// Records a rejected tenant or organization assertion.
/// </summary>
/// <remarks>
/// <para>
/// One seam, two implementations. <see cref="LoggingTenantAssertionRecorder"/> writes a
/// structured warning and a metric and was the only registered one from Packet 4;
/// <see cref="AuditingTenantAssertionRecorder"/> is registered from Packet 9 and
/// <b>decorates</b> it with the <c>audit_log</c> row. The middleware, its bounds and its
/// metrics did not change — the registration did.
/// </para>
/// <para>
/// The interface stays named <c>Record</c> rather than <c>Audit</c>. Not every occurrence
/// produces a row and that is the design, not a shortfall: an unresolved request produces
/// none at all, and an anonymous mismatch produces one only when its window crosses the
/// burst threshold. Authenticated occurrences beyond the target-tenant allowance also
/// produce only diagnostics. This admission exception does not weaken mutation MUST durability.
/// </para>
/// </remarks>
public interface ITenantAssertionRecorder
{
    /// <summary>
    /// Records one rejected assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asynchronous because the Packet 9 implementation writes a MUST-class row, and a
    /// MUST-class row is not something to start and walk away from: a fire-and-forget
    /// write is one the process can lose at shutdown without anything noticing, which is
    /// the opposite of what a durability class means.
    /// </para>
    /// <para>
    /// <b>No <c>CancellationToken</c>, and its absence is the contract.</b> The only thing
    /// this method does is record, and the request it records is the request that would
    /// cancel it — a client that sends the crossing occurrence and then drops the socket
    /// would abandon the write at <c>OpenConnectionAsync</c>, before a statement is
    /// issued, having already consumed the burst window. Taking a token and ignoring it
    /// would be a lie in the signature; <c>AuditLogBehavior</c>'s reconcile solves the
    /// same problem one file over by passing <c>CancellationToken.None</c>, and it is a
    /// parameter there only because the rest of that method genuinely uses one. Npgsql's
    /// own connection and command timeouts still bound the write.
    /// </para>
    /// </remarks>
    Task RecordRejectionAsync(TenantAssertionRejection rejection);

    /// <summary>
    /// An assertion arrived on a request whose tenant never resolved. There is
    /// no tenant to write the record under, so this is counted and not
    /// recorded — which is the rule ADR-0036 states, not a gap.
    /// </summary>
    /// <remarks>
    /// Synchronous, and it stays synchronous. There is no row on this path and there
    /// cannot be one: <c>audit_log</c> is tenant-owned, so writing here would mean
    /// inventing a tenant. A <c>Task</c>-returning signature would imply an I/O this
    /// method is defined by not doing.
    /// </remarks>
    void RecordUnresolved(TenantAssertionDimension dimension);

    /// <summary>
    /// Counts and diagnoses a validated membership refusal with no defended tenant.
    /// Synchronous: this path performs no audit-store I/O and writes no row.
    /// </summary>
    void RecordNoTenantRefusal(TenantNoTenantRefusal refusal);
}
