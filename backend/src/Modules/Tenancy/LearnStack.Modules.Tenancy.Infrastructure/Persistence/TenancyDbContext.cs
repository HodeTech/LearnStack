using LearnStack.Modules.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The Tenancy module's unit of persistence — one <c>DbContext</c> per module,
/// per <see href="../../../../../../docs/decisions/0002-initial-architecture.md">ADR-0002</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not own a connection.</b> Per
/// <see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// the connection belongs to <c>IUnitOfWork</c>, one per scope, and every module
/// context is built on it. A context that opened its own would never see the
/// <c>SET LOCAL app.tenant_id</c> issued on the ambient one, and every read
/// through it would return zero rows under the corrected policy — silently,
/// because a policy that filters everything is indistinguishable from a table
/// with no matching data. Packet 6 step 6 completes that seam; until then the
/// registration takes a connection from DI rather than a connection string, so
/// there is one call site to change and not many.
/// </para>
/// <para>
/// <b>No global query filters here yet.</b> The tenant and organization filters
/// are Packet 7's, with <c>TenantResolverMiddleware</c> and the request-scoped
/// <c>ITenantContext</c> they read. Between the two packets no tenant-owned table
/// is read on a request path, and with the policies live and <c>app.tenant_id</c>
/// unset every predicate evaluates to <c>NULL</c> and every query correctly
/// returns zero rows — fail-closed by construction rather than by a filter that
/// does not exist yet
/// (<see href="../../../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see>).
/// </para>
/// </remarks>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<TenantLocale> TenantLocales => Set<TenantLocale>();

    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    public DbSet<TenantFeatureFlag> TenantFeatureFlags => Set<TenantFeatureFlag>();

    public DbSet<PlatformEntitlement> PlatformEntitlements => Set<PlatformEntitlement>();

    public DbSet<PlatformHostMapping> PlatformHostMappings => Set<PlatformHostMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);

        // Last, so it also rewrites anything the configurations named explicitly.
        // ToSnakeCase is idempotent, so a name already in snake_case is unchanged.
        modelBuilder.ApplySnakeCaseNames();
    }
}
