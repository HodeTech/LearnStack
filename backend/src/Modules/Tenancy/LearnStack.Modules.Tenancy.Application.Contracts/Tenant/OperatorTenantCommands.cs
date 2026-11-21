using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

/// <summary>
/// Provisions a Tenant and its default Organization for an admitted operator (ADR-0102 § 2, § 3).
/// </summary>
/// <remarks>
/// The operator supplies the tenant's id and slug, and the act is idempotent on that pair: the same
/// pair finds the tenant already there, a known slug under another id refuses. Reachable only through
/// the operator source; the seed's own <see cref="ProvisionTenantCommand"/> is a different request
/// with a different policy, so neither authority can perform the other's act.
/// </remarks>
public sealed record OperatorProvisionTenantCommand(
    TenantId TenantId,
    string Slug,
    string DisplayName,
    OrganizationId DefaultOrganizationId,
    string DefaultOrganizationSlug,
    string DefaultOrganizationDisplayName)
    : IRequest<Result<ProvisionedTenantDto>>;

/// <summary>
/// Maps a host to the operator-named Tenant through the host-mapping store (ADR-0102 § 2, § 4).
/// </summary>
/// <remarks>
/// The tenant comes from the context the executor installed for the act, not from this body. The
/// operator asserts ownership of the host; LearnStack verifies no DNS.
/// </remarks>
public sealed record OperatorMapHostCommand(
    string Host,
    OrganizationId? OrganizationId = null,
    bool IsActive = true,
    bool IsPubliclyLive = true) : IRequest<Result<HostMappingDto>>;
