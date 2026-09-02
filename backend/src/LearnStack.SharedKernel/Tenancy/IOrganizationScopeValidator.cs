using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Answers one question: does this organization belong to this tenant?
/// </summary>
/// <remarks>
/// <para>
/// <b>The seventh sanctioned setter of <c>app.tenant_id</c></b>
/// (<see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// Amendment 3</see>). It cannot run on the ambient unit of work, because the
/// question is asked at the request edge — before the pipeline reaches
/// <c>TransactionBehavior</c> at step 6, so there is no ambient transaction to
/// enlist on. It therefore owns a short read-only transaction of its own, on its
/// own connection, connected as <c>learnstack_app</c> like every other setter in
/// that table — a validator that reached for <c>learnstack_platform</c> would be
/// invisible to the isolation suite, which is the failure mode ADR-0003 names by
/// hand.
/// </para>
/// <para>
/// <b>A valid organization id from another tenant is a mismatch, not an override.</b>
/// That is the whole of what this exists to establish, and it is why the read is on
/// the composite key <c>(tenant_id, id)</c> and never on <c>id</c> alone — the
/// primary key is the surrogate id, so a lookup by id would happily return another
/// tenant's row and then compare it in application code, outside the policy.
/// </para>
/// </remarks>
public interface IOrganizationScopeValidator
{
    /// <summary>
    /// <c>true</c> when <paramref name="organizationId"/> is an organization of
    /// <paramref name="tenantId"/>.
    /// </summary>
    Task<bool> BelongsToTenantAsync(
        TenantId tenantId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
