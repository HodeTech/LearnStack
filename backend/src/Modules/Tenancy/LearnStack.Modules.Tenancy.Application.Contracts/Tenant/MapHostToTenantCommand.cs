using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

/// <summary>
/// Points a hostname at the ambient tenant, or at one of its organizations.
/// </summary>
/// <remarks>
/// <para>
/// <b>It runs under the ordinary announcement, like any other tenant write.</b>
/// <c>platform_host_to_tenant</c> is read platform-scoped — the resolver has no tenant yet
/// when it looks — but written tenant-keyed, so this command needs a resolved context and
/// the policy checks the row's tenant against it. That asymmetry is
/// [Database Standards § Table classes](../../../../../../docs/standards/05-database.md)'s,
/// not this command's.
/// </para>
/// <para>
/// <b>The two flags are separate and both default to closed.</b> A row exists before DNS
/// points anywhere, so <c>IsActive</c> says the tenant owns the mapping and
/// <c>IsPubliclyLive</c> says it may serve anonymous traffic
/// ([ADR-0036](../../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md)).
/// Collapsing them would serve an unlaunched tenant's catalog, pricing and branding to
/// anyone who guessed the hostname.
/// </para>
/// <para>
/// <b>Never populated by calling the Hub</b>
/// ([ADR-0034](../../../../../../docs/decisions/0034-hub-contract-surface-invariant.md)):
/// an anonymous page load must not depend on a control plane being reachable, so the row
/// arrives from configuration, from the seeder, or from
/// <c>PUT /api/internal/tenants/{id}/host-mappings</c> — never from a lookup at resolution
/// time.
/// </para>
/// </remarks>
public sealed record MapHostToTenantCommand(
    string Host,
    OrganizationId? OrganizationId = null,
    bool IsActive = false,
    bool IsPubliclyLive = false) : IRequest<Result<HostMappingDto>>;

/// <summary>The stored mapping, with the host in the spelling the resolver compares.</summary>
/// <remarks>
/// Both flags, not just the second. They are a pair — a row exists before DNS points
/// anywhere — and a response carrying only <c>IsPubliclyLive</c> would let a caller read
/// "false" as "not mine yet" when it means "mine, and not serving".
/// </remarks>
public sealed record HostMappingDto(
    string Host,
    TenantId TenantId,
    OrganizationId? OrganizationId,
    bool IsActive,
    bool IsPubliclyLive);
