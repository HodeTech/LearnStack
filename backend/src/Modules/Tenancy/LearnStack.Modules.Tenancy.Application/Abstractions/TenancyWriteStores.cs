using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>The write side of the <c>Tenant</c> aggregate.</summary>
/// <remarks>
/// Declared here and implemented across the boundary: <c>Application → Infrastructure</c>
/// is a forbidden edge, and the reverse reference already exists, so a handler that named
/// <c>TenancyDbContext</c> would be a project cycle the compiler refuses.
/// </remarks>
public interface ITenantWriteStore : IAggregateWriteStore<Domain.Tenant, TenantId>;

/// <summary>The write side of the <c>Organization</c> aggregate.</summary>
/// <remarks>
/// A second port rather than one fused with the first, deliberately: the rule that
/// confines cross-aggregate writes counts how many of these a handler takes, and a
/// combined port would hide the very thing ADR-0042 exists to enumerate.
/// </remarks>
public interface IOrganizationWriteStore : IAggregateWriteStore<Organization, OrganizationId>;
