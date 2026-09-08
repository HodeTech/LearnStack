using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// The tenant a row belongs to — the identifier every isolation layer keys on.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <see cref="LearnStack.SharedKernel"/> rather than in the Tenancy
/// module, per ADR-0023 Amendment 2's cross-cutting placement rule: it appears in
/// <c>ITenantContext</c>, on every <c>[TenantOwned]</c> entity, in cache keys, in
/// job payloads and in integration-event envelopes, so a module-owned type would
/// make every one of those a reference to Tenancy.
/// </para>
/// <para>
/// <b>There is no <c>TenantId.New()</c>, and that is a constraint rather than an
/// omission.</b> A tenant id is never minted inside a handler: the registry that
/// owns the <c>Tenant</c> aggregate assigns it — the Hub in SaaS / Dedicated,
/// configuration in Self-Hosted, the fixture in a seed — and the provisioning
/// transaction sets <c>app.tenant_id</c> to that value before the <c>INSERT</c>,
/// so the self-keyed policy's <c>WITH CHECK</c> passes. A handler that generated
/// its own could not satisfy its own policy. See
/// <see href="../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>.
/// </para>
/// <para>
/// <b>The platform sentinel is <see cref="PlatformSentinel"/>, chosen by Packet 9
/// with the schema that stores it.</b> Its irreversible consumer is
/// <c>audit_log</c>'s <c>tenant_id</c> column, which is why the value was left
/// unfixed until the table existed
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
/// § 1</see>). It is carried by exactly one class of row: a platform-scope
/// operation with no resolvable tenant, written standalone — entry into
/// <c>EnterPlatformAdminScope</c> and the operations performed inside it. It is
/// never written by a tenant request path and never announced by
/// <c>SetTenantContextAsync</c>; <c>SetProvisioningTenantContextAsync</c> refuses
/// it and <c>Tenant.Create</c> refuses it, with the <c>tenants</c> CHECK as the
/// backstop rather than the control. ADR-0036 forbids it on a different path — an
/// unauthenticated tenant-assertion rejection must never write under it — and the
/// two rules do not conflict: one is an audited operator action, the other an
/// anonymous request.
/// </para>
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantId : IStronglyTypedId<Guid>
{
    /// <summary>
    /// The reserved tenant a platform-scope audit row carries when no tenant owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the nil uuid</b>, and the reason is measured rather than aesthetic. All-zero
    /// is what three shipped mechanisms read as *no tenant* —
    /// <c>NpgsqlUnitOfWork.SetTenantContextAsync</c> maps it to the empty string,
    /// <c>SetProvisioningTenantContextAsync</c> throws on it, and
    /// <c>TenantOwnership.EnsureRealTenant</c> refuses it in every aggregate factory —
    /// and a fourth, <see cref="StronglyTypedId.IsAssigned{TId}"/>, reports it
    /// unassigned. Choosing it would mean the same value means "no tenant" to the filter
    /// layer, the unit of work and eight aggregate factories, and "a real tenant" to the
    /// one component that writes audit rows.
    /// </para>
    /// <para>
    /// UUIDv7-shaped, on the precedent <see cref="UserId.SystemActor"/> already sets for
    /// the actor column. Making the tenant sentinel look unlike the actor sentinel would
    /// be the surprising outcome.
    /// </para>
    /// <para>
    /// No tenant can be provisioned under it: <c>tenants</c> carries
    /// <c>ck_tenants_not_platform_sentinel</c>, and the two writer-side guards refuse it
    /// before the constraint is reached — a constraint cannot stop a session variable
    /// from being announced, which is the half that actually matters.
    /// </para>
    /// </remarks>
    public static TenantId PlatformSentinel { get; } =
        From(Guid.Parse("00000000-0000-7000-8000-000000000002"));
}
