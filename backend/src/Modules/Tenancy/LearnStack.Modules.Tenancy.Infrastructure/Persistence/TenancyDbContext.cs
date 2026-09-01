using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Tenancy;
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
/// <b>The global query filters come from the base.</b>
/// <c>TenantScopedDbContext</c> owns the two members they close over and applies
/// one to every entity implementing <c>ITenantOwned</c>; this context adds none
/// of its own. Two of its eight entity types deliberately get no filter:
/// <see cref="Tenants"/>, which is tenant-owned <b>self-keyed</b> — its <c>id</c>
/// is the tenant id, and its policy says so — and
/// <see cref="PlatformHostMappings"/>, which is <b>platform-scoped</b> and read
/// in order to determine the tenant, so a tenant-keyed predicate on it would make
/// host resolution return zero rows forever. Row Level Security remains the
/// isolation boundary
/// (<see href="../../../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see>); the filters are the layer above it.
/// </para>
/// </remarks>
public sealed class TenancyDbContext(
    DbContextOptions<TenancyDbContext> options, ITenantContext tenantContext)
    : TenantScopedDbContext(options, tenantContext)
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

        // The base applies the tenant filters, and it runs after the
        // configurations so every entity type is in the model when it sweeps.
        base.OnModelCreating(modelBuilder);

        // Last, so it also rewrites anything the configurations named explicitly.
        // ToSnakeCase is idempotent, so a name already in snake_case is unchanged.
        modelBuilder.ApplySnakeCaseNames();
    }
}
