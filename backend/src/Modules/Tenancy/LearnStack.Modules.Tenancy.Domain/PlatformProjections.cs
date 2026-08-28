using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// The durable copy of a tenant's plan entitlements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Despite the table name this is a store, not a cache.</b> It is the layer
/// that makes the grace window real: if it were evictable, a Hub outage would
/// revoke every tenant's plan. The <c>learnstack.hub.entitlement</c> event is the
/// eager <i>invalidation</i> signal for the L1/L2 caches in front of it, never the
/// write path.
/// </para>
/// <para>
/// <b>Tenant-owned, not platform-scoped</b>, despite the <c>platform_</c> prefix.
/// Every read resolves the tenant from <c>ITenantContext</c> first, and every
/// write arrives on <c>PUT /api/internal/tenants/{id}/entitlements</c> with the
/// tenant in its path — both directions have a tenant, so the ordinary
/// tenant-owned policy applies and the application role never holds a table-wide
/// read of every tenant's plan
/// (<see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>).
/// </para>
/// <para>
/// Written only through <c>IEntitlementProvider.RefreshAsync</c>. Modules never
/// write it directly, which <c>Modules_Do_Not_Read_Entitlement_Cache_Directly</c>
/// enforces.
/// </para>
/// </remarks>
public sealed class PlatformEntitlement
{
    private PlatformEntitlement()
    {
        PlanCode = null!;
        Features = null!;
        Limits = null!;
        Compliance = null!;
        Source = null!;
    }

    /// <summary>One row per tenant; the tenant id is the primary key.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>The plan. Carried on the wire as <c>tier</c>.</summary>
    public string PlanCode { get; private set; }

    /// <summary>Feature switches, as JSON: <c>Dictionary&lt;string, bool&gt;</c>.</summary>
    public string Features { get; private set; }

    /// <summary>Numeric ceilings, as JSON: <c>Dictionary&lt;string, long&gt;</c>.</summary>
    public string Limits { get; private set; }

    /// <summary>Compliance caps, regions, retention overrides, as JSON.</summary>
    public string Compliance { get; private set; }

    /// <summary>When the entitlement lapses. Carried on the wire as <c>expires_at</c>.</summary>
    public DateTimeOffset ValidUntil { get; private set; }

    /// <summary>Bounds the grace window. Null unless in grace.</summary>
    public DateTimeOffset? GraceUntil { get; private set; }

    /// <summary>
    /// Monotonic. A push is accepted only when its generation is at least the
    /// stored one, so a replayed or out-of-order projection cannot roll a tenant
    /// back onto an older plan.
    /// </summary>
    public long Generation { get; private set; }

    public DateTimeOffset RefreshedAt { get; private set; }

    /// <summary><c>hub</c> | <c>signed-license-key</c> | <c>null-provider</c>.</summary>
    public string Source { get; private set; }
}

/// <summary>
/// The host → tenant resolution index. Read <b>before</b> any tenant is known.
/// </summary>
/// <remarks>
/// <para>
/// The one <b>platform-scoped</b> table: <c>IHostToTenantResolver</c> reads it in
/// order to <i>determine</i> the tenant, so the ordinary tenant-owned predicate
/// would return zero rows and no tenant could ever resolve. Its policies are
/// role-qualified and per-command — the read admits the single row the resolver
/// announces through <c>app.resolving_host</c>, writes stay tenant-keyed
/// (<see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>).
/// </para>
/// <para>
/// <b>Never populated by calling the Hub.</b> Rows arrive from
/// <c>PUT /api/internal/tenants/{id}/host-mappings</c> or from configuration; an
/// anonymous page load must not depend on a control plane being reachable
/// (<see href="../../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>).
/// </para>
/// </remarks>
public sealed class PlatformHostMapping
{
    private PlatformHostMapping() => Host = null!;

    /// <summary>The normalized effective host. Primary key — one answer per host.</summary>
    public string Host { get; private set; }

    public TenantId TenantId { get; private set; }

    /// <summary>Null for a tenant-wide host; set when the host serves one organization.</summary>
    public OrganizationId? OrganizationId { get; private set; }

    /// <summary>
    /// The mapping exists and is owned by this tenant.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IsPubliclyLive"/> on purpose. A row exists before
    /// DNS points at LearnStack — the lifecycle is submit → row → DNS instructions
    /// → activate.
    /// </remarks>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The host may serve anonymous traffic.
    /// </summary>
    /// <remarks>
    /// Without this separate flag, guessing a hostname serves an unlaunched
    /// tenant's pre-launch catalog, pricing and branding to a stranger, and a
    /// released-then-re-registered domain serves the previous tenant's content for
    /// the resolver cache's window
    /// (<see href="../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>).
    /// A host-only request requires it.
    /// </remarks>
    public bool IsPubliclyLive { get; private set; }
}
