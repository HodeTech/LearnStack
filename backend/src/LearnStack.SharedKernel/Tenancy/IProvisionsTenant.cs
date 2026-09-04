using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// A request that creates the tenant it names, and therefore carries the tenant id the
/// ambient transaction must announce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pipeline needs the value and not a flag.</b> The policy on <c>tenants</c> is
/// <c>WITH CHECK (id = app.tenant_id)</c> — measured against a live PostgreSQL with the
/// shipped policy: inserting a tenant with <c>app.tenant_id</c> unset fails 42501, and
/// with it set to the empty string, which is exactly what the unit of work writes for an
/// unresolved context, it fails 42501 identically. With it set to the new tenant's own id
/// the insert, the organization insert and the back-reference update all commit. So the
/// transaction has to be announced with an id that belongs to no resolved context, and
/// the only place that id exists before the handler runs is the request.
/// </para>
/// <para>
/// <b>The announcement stays with <c>TransactionBehavior</c>.</b> It reads this and
/// announces once, rather than the handler announcing a second time — which would leave a
/// window inside the ambient transaction where <c>app.tenant_id</c> is the empty string
/// and any statement issued in it is silently fail-closed, and would hand every handler
/// in the solution the ability to retarget the ambient tenant. The setter set
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// Amendment 3</see> closes at seven stays closed.
/// </para>
/// <para>
/// <b>It grants nothing on its own.</b> The behavior honours it only when the context is
/// <i>unresolved</i>. A caller already authenticated for tenant A who sends a
/// provisioning request naming tenant B falls through to the ordinary path, the
/// transaction is announced with A, and B's insert is refused by the policy — the
/// confused deputy is closed by the database rather than by a check somebody has to
/// remember.
/// </para>
/// </remarks>
public interface IProvisionsTenant
{
    /// <summary>The registry-assigned id of the tenant this request creates.</summary>
    TenantId ProvisioningTenantId { get; }
}
