using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Whether a user holds an active membership covering a tenant, and optionally an
/// organization within it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership confirms a claim; it never selects a tenant.</b>
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § The signals</see> is explicit that this signal is "confirming J at request
/// time", not a resolution source. It is consulted on exactly the matrix rows where
/// a claim reaches past what the host already vouches for — 7, 10 and 14 — and on
/// no other.
/// </para>
/// <para>
/// <b>Active is part of the question.</b> A membership that was revoked answers
/// <c>false</c>; the caller has no second call to make and no status to interpret,
/// which is what keeps the reconciliation matrix a matrix rather than a workflow.
/// </para>
/// </remarks>
public interface ITenantMembershipReader
{
    /// <summary>
    /// <c>true</c> when <paramref name="userId"/> holds an active membership
    /// covering <paramref name="tenantId"/> — and
    /// <paramref name="organizationId"/> when one is named.
    /// </summary>
    Task<bool> CoversAsync(
        UserId userId,
        TenantId tenantId,
        OrganizationId? organizationId = null,
        CancellationToken cancellationToken = default);
}
