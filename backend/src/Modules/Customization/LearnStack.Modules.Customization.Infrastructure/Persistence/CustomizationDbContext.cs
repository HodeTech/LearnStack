using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>
/// The Customization module's unit of persistence — one <c>DbContext</c> per
/// module, per
/// <see href="../../../../../../docs/decisions/0002-initial-architecture.md">ADR-0002</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not own a connection.</b>
/// <see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// puts the connection on <c>IUnitOfWork</c>, one per scope, and every module
/// context is built on it. A context that opened its own would never see the
/// <c>SET LOCAL app.tenant_id</c> issued on the ambient one, and every read
/// through it would return zero rows under the corrected policy — silently,
/// because a policy that filters everything is indistinguishable from a table with
/// no matching data.
/// </para>
/// <para>
/// <b>Every entity here is tenant-owned, tenant-wide.</b> There is no
/// organization-scoped row and no platform-scoped one, so the base's sweep applies
/// one filter shape to all four and the schema carries no restrictive write guard
/// — there is no organization to guard
/// (<see href="../../../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>).
/// </para>
/// </remarks>
public sealed class CustomizationDbContext(
    DbContextOptions<CustomizationDbContext> options, ITenantContextAccessor accessor)
    : TenantScopedDbContext(options, accessor)
{
    public DbSet<TenantContentType> TenantContentTypes => Set<TenantContentType>();

    /// <remarks>
    /// No <c>DbSet</c> for <c>TenantLevelTaxonomyItem</c>, exactly as
    /// <c>TenancyDbContext</c> exposes none for <c>TenantLocale</c>: an item is
    /// inside this aggregate and is reached through its root. It stays mapped by
    /// <c>ApplyConfigurationsFromAssembly</c>, so it keeps its query filter, its
    /// policy and its place in the isolation sweep — which resolves entity types
    /// from the model, not from the context's properties.
    /// </remarks>
    public DbSet<TenantLevelTaxonomy> TenantLevelTaxonomies => Set<TenantLevelTaxonomy>();

    /// <summary>
    /// The cache-generation counter. Mapped so the four isolation layers reach it;
    /// bumped by one statement rather than through change tracking, because a
    /// read-modify-write loses one of two concurrent customization writes.
    /// </summary>
    public DbSet<CustomizationGeneration> CustomizationGenerations =>
        Set<CustomizationGeneration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomizationDbContext).Assembly);

        // The base applies the tenant filters, and it runs after the configurations
        // so every entity type is in the model when it sweeps.
        base.OnModelCreating(modelBuilder);

        // Last, so it also rewrites anything the configurations named explicitly.
        // ToSnakeCase is idempotent, so a name already in snake_case is unchanged.
        modelBuilder.ApplySnakeCaseNames();
    }
}
