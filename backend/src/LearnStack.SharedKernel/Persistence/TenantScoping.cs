using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>
/// An entity whose rows belong to one tenant and are keyed by a
/// <see cref="Identifiers.TenantId"/> column.
/// </summary>
/// <remarks>
/// <para>
/// The interface and the <see cref="TenantOwnedAttribute"/> marker say the same
/// thing to two different readers. The interface is what the EF global query
/// filter binds to — it gives the filter a typed property to compare, which a
/// bare attribute cannot — and the attribute is what a reflection scan finds on
/// an entity that is tenant-owned without carrying the column, which is exactly
/// the self-keyed case below. An entity carries the attribute; it implements the
/// interface unless its class exempts it.
/// </para>
/// <para>
/// <b>The tenant-owned self-keyed class does not implement this.</b> On
/// <c>tenants</c> the row's own <c>id</c> <i>is</i> the tenant id, so there is no
/// <c>TenantId</c> column to compare and the policy keys on <c>id</c>. It carries
/// <c>[TenantOwned(SelfKeyed = true)]</c> and nothing else. See
/// <see href="../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>.
/// </para>
/// </remarks>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}

/// <summary>
/// An entity that additionally narrows to an organization inside its tenant.
/// </summary>
/// <remarks>
/// <b>Nullable, and the null is a scope rather than an absence.</b> A row with no
/// organization is <i>tenant-wide</i> — visible to every organization in its
/// tenant — which is why both the canonical Row Level Security policy and the EF
/// filter read <c>OrganizationId is null || OrganizationId == current</c> rather
/// than an equality alone ([ADR-0017](../../../../docs/decisions/0017-tenant-organization-hierarchy.md)).
/// </remarks>
public interface IOrganizationScoped : ITenantOwned
{
    OrganizationId? OrganizationId { get; }
}

/// <summary>
/// Marks an entity as belonging to one of the tenant-owned table classes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The marker's scope is decided by table class, not by the presence of a
/// <c>TenantId</c> property.</b> Two tables are exceptions and they except
/// different things: <c>tenants</c> is tenant-owned <b>self-keyed</b> and carries
/// this marker with <see cref="SelfKeyed"/> set, because its <c>id</c> is the
/// tenant id; and <c>platform_host_to_tenant</c> is <b>platform-scoped</b> and
/// carries no marker at all, because it is read <i>in order to</i> determine the
/// tenant — a tenant-keyed predicate on it would make host resolution return zero
/// rows forever, and it has a <c>TenantId</c> property regardless.
/// </para>
/// <para>
/// <c>Every_TenantOwned_Entity_HasFilterAndRlsPolicy</c> reads this marker and
/// requires, for each entity carrying it: a tenant key, an EF global query filter
/// referencing that key, and — in the migration that creates its table —
/// <c>ENABLE</c> and <c>FORCE ROW LEVEL SECURITY</c> plus exactly one policy with
/// both a <c>USING</c> and a <c>WITH CHECK</c> clause.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TenantOwnedAttribute : Attribute
{
    /// <summary>
    /// <c>true</c> when the entity's own identifier <i>is</i> the tenant id, so it
    /// carries no <c>TenantId</c> column and does not implement
    /// <see cref="ITenantOwned"/>. Exactly one entity class is in this position;
    /// a second one is a schema change, not a flag.
    /// </summary>
    public bool SelfKeyed { get; init; }
}

/// <summary>
/// Marks a tenant-owned entity that additionally narrows to an organization.
/// </summary>
/// <remarks>
/// Implies <see cref="TenantOwnedAttribute"/>: an organization exists only inside
/// a tenant, so an organization-scoped entity is tenant-owned by construction and
/// carries both markers. <c>Every_OrgScoped_Entity_HasOrgIdAndFilter</c> reads
/// this one and additionally requires the two <c>AS RESTRICTIVE</c> write guards,
/// <c>FOR UPDATE</c> and <c>FOR DELETE</c> — measured as load-bearing, not
/// decorative: with the tenant-scope read hatch set and the delete guard dropped,
/// a <c>DELETE</c> removed another organization's row.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OrganizationScopedAttribute : Attribute;
