using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

/// <summary>
/// Creates a tenant and the organization its content hangs from, in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one operation sanctioned to write two aggregate roots at once</b>
/// (<see href="../../../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>),
/// by enumeration rather than by principle: a tenant whose default organization failed to
/// commit is a tenant no request can serve, and a second transaction is a window in which
/// exactly that state exists. The allow-list has one entry and
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> is what keeps it at
/// one.
/// </para>
/// <para>
/// <b>Both ids are inbound, and that is a policy consequence rather than a style
/// choice.</b> <c>tenants</c> is self-keyed and its policy is
/// <c>WITH CHECK (id = app.tenant_id)</c>, so the transaction must be announced with the
/// id before the insert — and a handler that minted its own could not satisfy its own
/// policy. This is the one place the ordinary rule against taking a tenant id from a
/// request does not apply, and the guarantee is narrower than "the tenant does not exist
/// yet" — nothing verifies that. What holds is that the <b>only statement the
/// announcement authorises is an <c>INSERT</c> the primary key rejects when the tenant
/// already exists</b>: an id naming a live tenant announces that tenant, then fails
/// <c>pk_tenants</c> and rolls back, having read and written nothing.
/// <see cref="IProvisionsTenant"/> narrows it further by being honoured only when the
/// context is unresolved.
/// </para>
/// <para>
/// <b>Anything added to that transaction inherits the assumption, so read this first.</b>
/// Two things are already scheduled inside it: the MUST-class audit write ADR-0033 puts
/// immediately before the commit, and the outbox flush at pipeline step 7. Each would
/// execute under a caller-chosen tenant id, and neither is refused by <c>pk_tenants</c>.
/// Whatever lands there must not assume the announced tenant is new — or this command
/// must start refusing a tenant that already exists, which needs a read surface it does
/// not have today.
/// </para>
/// <para>
/// <b><c>[AllowsUnresolvedTenantContext]</c> and not <c>[PublicSurface]</c>.</b> The
/// request legitimately arrives with no tenant — there is none until it succeeds — but it
/// is emphatically not reachable by an unauthenticated caller. Phase 03's permission
/// check is what will gate it; until then its only callers are the seeder and the tests.
/// </para>
/// </remarks>
[AllowsUnresolvedTenantContext]
public sealed record ProvisionTenantCommand(
    TenantId TenantId,
    string Slug,
    string DisplayName,
    OrganizationId DefaultOrganizationId,
    string DefaultOrganizationSlug,
    string DefaultOrganizationDisplayName)
    : IRequest<Result<ProvisionedTenantDto>>, IProvisionsTenant
{
    /// <inheritdoc />
    /// <remarks>
    /// What <c>TransactionBehavior</c> announces on the ambient transaction, so the
    /// policy admits the row this command is about to insert.
    /// </remarks>
    public TenantId ProvisioningTenantId => TenantId;
}

/// <summary>What provisioning produced.</summary>
/// <remarks>
/// <para>
/// The ids are echoed rather than generated, so a caller that lost its correlation can
/// still tie the result to what it asked for.
/// </para>
/// <para>
/// Strongly typed, not raw <c>Guid</c>: [Backend Coding Standards](../../../../../../docs/standards/02-backend-coding.md)
/// says never to expose a raw <c>Guid</c> on a public surface, and Packet 4 shipped the
/// OpenAPI mapping for these identifiers so that the response schema is unaffected by
/// keeping them. A DTO is exactly where the erosion starts.
/// </para>
/// </remarks>
public sealed record ProvisionedTenantDto(
    TenantId TenantId,
    string Slug,
    OrganizationId DefaultOrganizationId,
    string DefaultOrganizationSlug);
