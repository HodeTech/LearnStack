using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Uncached, scalar lookup on the announced ambient transaction.</summary>
public sealed class TenantExistenceReader(TenancyDbContext db, ITenantContext tenantContext) : ITenantExistenceReader
{
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => db.Tenants
        .AnyAsync(tenant => tenant.Id == tenantContext.TenantId && tenant.DeletedAt == null, cancellationToken);
}
