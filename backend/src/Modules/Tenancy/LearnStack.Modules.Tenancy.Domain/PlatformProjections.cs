using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Persistence;

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
/// Written only through <c>IEntitlementProvider.RefreshAsync</c>, and read only
/// through <c>IEntitlementProvider</c>. The read half is
/// <c>Modules_Do_Not_Read_Entitlement_Cache_Directly</c>, catalogued and owed by
/// <see href="../../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Packet 10</see>
/// — it is not in force yet, and the write half has no rule at all until
/// <see href="../../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase 02c</see>
/// ships <c>RefreshAsync</c> for it to guard.
/// </para>
/// </remarks>
[TenantOwned]
public sealed class PlatformEntitlement : ITenantOwned
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

    /// <summary>Maps <paramref name="host"/> to a tenant, or to one of its organizations.</summary>
    /// <remarks>
    /// <para>
    /// <b>The host is normalized here, not by the caller.</b> The resolver compares
    /// ordinally against what <see cref="EffectiveHost.Normalize"/> produces from a
    /// request's <c>Host</c> header, so a row stored in any other spelling — an uppercase
    /// letter, a trailing dot, a port, an unpunycoded IDN — matches nothing and the tenant
    /// is a 404 on its own domain. Normalizing at the factory is what makes that
    /// unreachable rather than a review item.
    /// </para>
    /// <para>
    /// <b>Both flags default to false.</b> The lifecycle
    /// <see href="../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
    /// describes is submit → row → DNS instructions → activate, so a row that arrives
    /// already serving anonymous traffic has skipped the two steps that decide whether it
    /// should. A caller that wants a live host says so.
    /// </para>
    /// </remarks>
    public static PlatformHostMapping Create(
        string host,
        TenantId tenantId,
        OrganizationId? organizationId = null,
        bool isActive = false,
        bool isPubliclyLive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var normalized = EffectiveHost.Normalize(host)
            ?? throw new ArgumentException(
                $"'{host}' is not a usable host: it must normalize to a name the resolver "
                + "can compare against a request's Host header.",
                nameof(host));

        TenantOwnership.EnsureRealTenant(
            tenantId,
            "A host mapping names the tenant it resolves to; the tenant id was never assigned.",
            nameof(tenantId));

        if (organizationId is { } organization
            && (!organization.IsInitialized() || organization.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "The organization id was never assigned. Pass null for a tenant-wide host.",
                nameof(organizationId));
        }

        // Publicly live without being active is the combination the two flags exist to
        // keep apart, inverted: it would serve anonymous traffic for a mapping the tenant
        // does not yet own. The database has no CHECK for it — this is the guard.
        if (isPubliclyLive && !isActive)
        {
            throw new ArgumentException(
                "A host cannot be publicly live before it is active: the lifecycle is "
                + "submit → row → DNS instructions → activate.",
                nameof(isPubliclyLive));
        }

        return new PlatformHostMapping
        {
            Host = normalized,
            TenantId = tenantId,
            OrganizationId = organizationId,
            IsActive = isActive,
            IsPubliclyLive = isPubliclyLive,
        };
    }

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
