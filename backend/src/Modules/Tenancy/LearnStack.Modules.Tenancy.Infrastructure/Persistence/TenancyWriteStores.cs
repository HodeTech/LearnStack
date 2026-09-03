using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The <c>Tenant</c> aggregate's writes, against the module context.
/// </summary>
/// <remarks>
/// <b>Each method saves, and that is load-bearing rather than convenient.</b> The EF model
/// carries no relationship between <c>Tenant</c> and <c>Organization</c>, so batching both
/// into one <c>SaveChanges</c> leaves the order EF sends them unspecified — and
/// provisioning depends on it: the organization's composite foreign key names
/// <c>(tenant_id, id)</c>, so the tenant row has to land first. Saving per call is what
/// makes the handler's statement order the database's statement order.
/// </remarks>
public sealed class TenantWriteStore(TenancyDbContext db) : ITenantWriteStore
{
    public async Task AddAsync(Tenant aggregate, CancellationToken cancellationToken = default)
    {
        db.Tenants.Add(aggregate);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Tenant aggregate, CancellationToken cancellationToken = default)
    {
        db.Tenants.Update(aggregate);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The <c>Organization</c> aggregate's writes. Same shape, same reason.</summary>
/// <remarks>
/// <c>public</c> only so the composition root can name it in a registration; nothing
/// outside that line should. The port is the type callers depend on.
/// </remarks>
public sealed class OrganizationWriteStore(TenancyDbContext db) : IOrganizationWriteStore
{
    public async Task AddAsync(Organization aggregate, CancellationToken cancellationToken = default)
    {
        db.Organizations.Add(aggregate);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Organization aggregate, CancellationToken cancellationToken = default)
    {
        db.Organizations.Update(aggregate);
        await db.SaveChangesAsync(cancellationToken);
    }
}
