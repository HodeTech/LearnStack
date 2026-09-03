using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

/// <summary>
/// Adds an organization — a branch, campus or studio — to a tenant that already exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not marked <c>[AllowsUnresolvedTenantContext]</c>, and the omission is the design.</b>
/// Unlike provisioning, this command writes into a tenant that is already resolvable, so
/// it runs under the ordinary announcement and the organization's tenant column is checked
/// against it by the policy. A caller authenticated for tenant A cannot add an
/// organization to tenant B, and nothing in this file has to remember that — the database
/// does.
/// </para>
/// <para>
/// <b>One aggregate, one port.</b> Provisioning is the single operation
/// [ADR-0042](../../../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)
/// permits to write two roots at once. This writes one, which is why it is an ordinary
/// command and not a second entry on that allow-list.
/// </para>
/// </remarks>
public sealed record CreateOrganizationCommand(
    OrganizationId OrganizationId,
    string Slug,
    string DisplayName) : IRequest<Result<OrganizationDto>>;

/// <summary>What the caller now has.</summary>
public sealed record OrganizationDto(
    OrganizationId OrganizationId, TenantId TenantId, string Slug);
