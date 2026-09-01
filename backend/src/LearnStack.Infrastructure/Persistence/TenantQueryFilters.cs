using System.Linq.Expressions;
using System.Reflection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// What a module <c>DbContext</c> exposes so its global query filters have
/// something to close over.
/// </summary>
/// <remarks>
/// <para>
/// <b>These must be instance members of the context.</b> That is the whole
/// mechanism, and the reason is EF's model cache: a filter expression is compiled
/// into the model once per context type, and anything it closes over that is
/// <i>not</i> reached through the context instance is evaluated at that moment and
/// baked in as a SQL literal. Reached through the context, EF re-evaluates the
/// member per query and emits a parameter.
/// <c>Two_contexts_under_two_tenants_each_see_only_their_own_rows</c> holds the
/// property and fails against the baked-in form.
/// <para>
/// <b>The baked-in failure has two directions, and which one a host gets depends
/// on who builds the model first.</b> Whatever <c>CurrentTenantId</c> returns at
/// that moment is the literal every later query carries.
/// In the API host the first build is necessarily inside a request — the module
/// registration refuses to resolve a context outside the ambient transaction — so
/// the literal is a real tenant's id and every later request reads **that
/// tenant's rows**. Row Level Security still refuses to serve them, so it is a
/// zero-row outage rather than a leak, but the id in the <c>WHERE</c> clause
/// belongs to someone else. In a test or design-time host the first build is
/// unresolved, the literal is the all-zero id, and every query returns nothing
/// for the life of the process — which is what this repository measured, and why
/// the measurement alone would have named the wrong direction for production.
/// </para>
/// </para>
/// <para>
/// Implemented via <see cref="TenantScopedDbContext"/>, which every module context
/// derives from rather than restating. The extension that consumes this interface
/// additionally constrains its argument to a <see cref="DbContext"/>, because an
/// implementer that is not one cannot be a closure root EF re-evaluates — the
/// interface alone would let the mechanism be defeated by a caller that satisfied
/// its shape.
/// </para>
/// </remarks>
public interface ITenantScopedDbContext
{
    /// <summary>
    /// The tenant every filtered query narrows to, or the all-zero id when no
    /// tenant is resolved.
    /// </summary>
    /// <remarks>
    /// Never throws, and never absent. An unresolved context yields
    /// <see cref="TenantId"/> over <see cref="Guid.Empty"/>, which no row can
    /// carry — the domain refuses it at every factory and
    /// <c>NpgsqlUnitOfWork</c> refuses to write it into <c>app.tenant_id</c> — so
    /// the filter degenerates to "no rows" rather than to "all rows". Fail-closed
    /// is the only acceptable default here; a nullable that EF renders as
    /// <c>tenant_id IS NULL</c> would be a different, subtler wrong answer.
    /// </remarks>
    TenantId CurrentTenantId { get; }

    /// <summary>
    /// The organization the request narrows to, or <c>null</c> for a tenant-wide
    /// request — which sees tenant-wide rows and no organization's rows, exactly
    /// as the Row Level Security policy does with <c>app.organization_id</c> unset.
    /// </summary>
    OrganizationId? CurrentOrganizationId { get; }
}

/// <summary>
/// Applies the tenant and organization global query filters to every entity a
/// module's model marks as scoped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the isolation boundary.</b> Row Level Security is
/// ([ADR-0003 Amendment 3](../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md)),
/// and it holds whether or not a filter exists. These filters are the layer above
/// it: they keep a query from silently returning nothing when it should have
/// narrowed, they make the intent visible in the generated SQL, and they are what
/// an architecture test can check. Deleting them would not open a leak; it would
/// make every cross-tenant read return zero rows from the database instead of
/// from the query — which is the same answer arrived at by luck rather than by
/// design.
/// </para>
/// <para>
/// Applied from <c>OnModelCreating</c> and never from an
/// <c>IEntityTypeConfiguration</c>: a configuration class reached by
/// <c>ApplyConfigurationsFromAssembly</c> is constructed by EF with no access to
/// the context instance, so a filter written there could only close over
/// something else — which is the baked-in-literal failure this seam exists to
/// prevent.
/// </para>
/// </remarks>
public static class TenantQueryFilters
{
    /// <summary>
    /// Adds a filter to every entity type implementing <see cref="ITenantOwned"/>,
    /// narrowing further for those implementing <see cref="IOrganizationScoped"/>.
    /// </summary>
    public static ModelBuilder ApplyTenantQueryFilters<TContext>(
        this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopedDbContext
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // EF refuses a query filter on anything but a root entity type: an
            // owned type is queried through its owner, and on a TPH/TPT hierarchy
            // only the root may carry one. Skipping them here means the first
            // module to model either does not discover the rule by exception at
            // startup — and the root still gets its filter, which is what covers
            // the derived rows.
            if (entityType.BaseType is not null || entityType.IsOwned())
            {
                continue;
            }

            var clrType = entityType.ClrType;

            if (clrType.GetCustomAttribute<TenantOwnedAttribute>() is { SelfKeyed: true })
            {
                // The self-keyed class filters on its own Id, because that Id *is*
                // the tenant key — which is exactly what its policy does
                // (`tenants_isolation` keys on `id`). Filtered rather than skipped:
                // Database Standards § Table classes mandates `t.Id ==
                // currentTenantId` for this class, and skipping it would leave
                // `SELECT … FROM tenants` with no WHERE clause at all — correct
                // only because Row Level Security is underneath, which is the one
                // argument this project does not accept for dropping a layer.
                modelBuilder.Entity(clrType).HasQueryFilter(BuildSelfKeyedFilter(clrType, context));
                continue;
            }

            if (!typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                // The platform-scoped host map, which is read before any tenant
                // exists. Not an omission — a table class, see Database Standards
                // § Table classes. A tenant-keyed predicate here would make host
                // resolution return zero rows forever.
                continue;
            }

            modelBuilder.Entity(clrType).HasQueryFilter(BuildFilter(clrType, context));
        }

        return modelBuilder;
    }

    /// <summary>
    /// <c>e =&gt; e.Id == context.CurrentTenantId</c> for the tenant-owned
    /// self-keyed class.
    /// </summary>
    /// <remarks>
    /// Its <c>Id</c> is a <see cref="TenantId"/>, so the comparison is the same
    /// one every other entity makes — only the property differs.
    /// </remarks>
    private static LambdaExpression BuildSelfKeyedFilter(
        Type clrType, ITenantScopedDbContext context)
    {
        var entity = Expression.Parameter(clrType, "e");

        return Expression.Lambda(
            Expression.Equal(
                Expression.Property(entity, nameof(IHasId<TenantId>.Id)),
                Expression.Property(
                    Expression.Constant(context),
                    nameof(ITenantScopedDbContext.CurrentTenantId))),
            entity);
    }

    /// <summary>
    /// <c>e =&gt; e.TenantId == context.CurrentTenantId</c>, and for an
    /// organization-scoped entity
    /// <c>&amp;&amp; (e.OrganizationId == null || e.OrganizationId == context.CurrentOrganizationId)</c>.
    /// </summary>
    /// <remarks>
    /// Built as an expression tree rather than written as a lambda because the
    /// entity type is only known at model-building time. The shape is exactly what
    /// the compiler emits for a lambda closing over a context property — a member
    /// access rooted at a constant holding the context — which is the shape EF
    /// re-evaluates per query.
    /// </remarks>
    private static LambdaExpression BuildFilter(Type clrType, ITenantScopedDbContext context)
    {
        var entity = Expression.Parameter(clrType, "e");
        var contextConstant = Expression.Constant(context);

        Expression predicate = Expression.Equal(
            Expression.Property(entity, nameof(ITenantOwned.TenantId)),
            Expression.Property(contextConstant, nameof(ITenantScopedDbContext.CurrentTenantId)));

        if (typeof(IOrganizationScoped).IsAssignableFrom(clrType))
        {
            var rowOrganization = Expression.Property(
                entity, nameof(IOrganizationScoped.OrganizationId));

            // Tenant-wide OR the caller's own, mirroring the policy's organization
            // term. The app.scope = 'tenant' hatch is deliberately absent: it has
            // no carrier (see Security Standards § Tenant Context), and a filter
            // that widened where the policy does not would return rows the
            // database then refuses — the confusing direction of disagreement.
            predicate = Expression.AndAlso(
                predicate,
                Expression.OrElse(
                    Expression.Equal(
                        rowOrganization,
                        Expression.Constant(null, typeof(OrganizationId?))),
                    Expression.Equal(
                        rowOrganization,
                        Expression.Property(
                            contextConstant,
                            nameof(ITenantScopedDbContext.CurrentOrganizationId)))));
        }

        return Expression.Lambda(predicate, entity);
    }
}

/// <summary>
/// The base every module <c>DbContext</c> derives from: it owns the two members
/// the filters close over and applies them.
/// </summary>
/// <remarks>
/// A base class rather than a copied pair of properties, because the properties
/// are the mechanism. A module that wrote its own could reasonably write
/// <c>tenantContext.TenantId</c> — which throws on an unresolved context, inside
/// <c>OnModelCreating</c>, where the failure is a model that cannot be built.
/// </remarks>
public abstract class TenantScopedDbContext(
    DbContextOptions options, ITenantContextAccessor accessor)
    : DbContext(options), ITenantScopedDbContext
{
    private static readonly TenantId NoTenant = TenantId.From(Guid.Empty);

    /// <summary>
    /// The ambient context, read fresh on every access.
    /// </summary>
    /// <remarks>
    /// <b>The accessor, not an injected <c>ITenantContext</c>.</b> That contract is
    /// registered transient and resolved from this same accessor, so a context
    /// constructed with one captures whatever the accessor happened to hold at
    /// construction and never moves again. Measured: with the injected form, a
    /// context built under tenant A kept filtering to A after the accessor moved to
    /// B. Every flow the corpus designs writes the accessor before the context is
    /// built — the resolver middleware at scope start, the event transport per
    /// delivery — so the snapshot was correct today and would have been wrong the
    /// first time that ordering changed. Reading through the accessor makes the
    /// property hold by mechanism rather than by ordering, which is the same reason
    /// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
    /// § Sub-decision 10</see> routes every cross-cutting reader through it.
    /// </remarks>
    private ITenantContext Ambient => accessor.Current ?? UnresolvedTenantContext.Instance;

    /// <inheritdoc />
    public TenantId CurrentTenantId =>
        Ambient is { IsResolved: true } context && context.TenantId.IsInitialized()
            ? context.TenantId
            : NoTenant;

    /// <inheritdoc />
    public OrganizationId? CurrentOrganizationId =>
        Ambient is { IsResolved: true } context
        && context.OrganizationId is { } organization
        && organization.IsInitialized()
            ? organization
            : null;

    /// <summary>Applies the tenant filters to every entity the model holds.</summary>
    /// <remarks>
    /// <b>A subclass overriding this calls <c>base.OnModelCreating</c> LAST.</b> The
    /// sweep reads <c>modelBuilder.Model.GetEntityTypes()</c>, so anything a fluent
    /// configuration introduces that is not reachable from a <c>DbSet&lt;T&gt;</c>
    /// property — an entity mapped only by <c>ApplyConfigurationsFromAssembly</c>, a
    /// keyless query type, an owned type declared there — is not in the model yet if
    /// the base runs first, and silently gets no filter. Omitting the call entirely
    /// loses every filter, which
    /// <c>Every_TenantOwned_Entity_HasFilterAndRlsPolicy</c> does catch; calling it
    /// too early loses only the late arrivals, which nothing catches until one
    /// exists.
    /// </remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyTenantQueryFilters(this);
    }
}
