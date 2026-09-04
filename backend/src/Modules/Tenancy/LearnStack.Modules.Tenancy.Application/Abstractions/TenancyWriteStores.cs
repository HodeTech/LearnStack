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

/// <summary>The write side of <c>platform_host_to_tenant</c>.</summary>
/// <remarks>
/// Not an <see cref="IAggregateWriteStore{TRoot,TId}"/>, and the reason is the key:
/// <c>PlatformHostMapping</c> is identified by its host, a string, not by a strongly-typed
/// id — one answer per host, globally. It is also not an aggregate root: it is the
/// projection the resolver reads before any tenant is known. A port of its own keeps both
/// facts visible rather than forcing the shape.
/// </remarks>
public interface IPlatformHostMappingStore
{
    Task AddAsync(PlatformHostMapping mapping, CancellationToken cancellationToken = default);
}
