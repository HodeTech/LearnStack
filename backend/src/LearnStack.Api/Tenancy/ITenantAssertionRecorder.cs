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
/// One seam, two implementations across two packets.
/// <see cref="LoggingTenantAssertionRecorder"/> is the only registered one in
/// Packet 4 and writes a structured warning and a metric; Packet 9 swaps in an
/// auditing implementation once <c>IAuditStore</c> and <c>audit_log</c> exist.
/// The middleware, its bounds and its metrics do not change — the registration
/// does.
/// </para>
/// <para>
/// <b>Packet 4 must not describe the outcome as audited</b>, and this interface
/// is deliberately named <c>Record</c> rather than <c>Audit</c> so the
/// distinction survives a careless read. A log line that is honestly a log line
/// beats an audit row that does not exist.
/// </para>
/// </remarks>
public interface ITenantAssertionRecorder
{
    void RecordRejection(TenantAssertionRejection rejection);

    /// <summary>
    /// An assertion arrived on a request whose tenant never resolved. There is
    /// no tenant to write the record under, so this is counted and not
    /// recorded — which is the rule ADR-0036 states, not a gap.
    /// </summary>
    void RecordUnresolved(TenantAssertionDimension dimension);
}
