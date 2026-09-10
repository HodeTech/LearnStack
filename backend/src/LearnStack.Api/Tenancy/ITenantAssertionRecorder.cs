namespace LearnStack.Api.Tenancy;

/// <summary>Which client assertion disagreed with what the API resolved.</summary>
public enum TenantAssertionDimension
{
    Tenant,
    Organization,
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
/// authenticated mismatch is bounded by token issuance, an anonymous one is
/// not.
/// </param>
public readonly record struct TenantAssertionRejection(
    Guid ResolvedTenantId,
    TenantAssertionDimension Dimension,
    Guid AssertedValue,
    bool IsAuthenticated);

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
/// burst threshold. A name promising a row per call would be wrong on two of the three
/// paths.
/// </para>
/// </remarks>
public interface ITenantAssertionRecorder
{
    /// <summary>
    /// Records one rejected assertion.
    /// </summary>
    /// <remarks>
    /// Asynchronous because the Packet 9 implementation writes a MUST-class row, and a
    /// MUST-class row is not something to start and walk away from: a fire-and-forget
    /// write is one the process can lose at shutdown without anything noticing, which is
    /// the opposite of what a durability class means.
    /// </remarks>
    Task RecordRejectionAsync(
        TenantAssertionRejection rejection, CancellationToken cancellationToken = default);

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
}
