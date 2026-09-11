using System.Collections.Frozen;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// The source of a tenant's plan projection, and its only sanctioned writer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only sanctioned reader AND writer of <c>platform_entitlement_cache</c>.</b> No
/// module may query that table — Tenancy included, whose infrastructure holds it. That is
/// what makes swapping the registered implementation change the answer without touching
/// module code, which is the Phase 02a completion criterion this port exists for: a direct
/// table read would bypass the registered provider entirely, and the L1 → L2 → durable →
/// Hub order <see href="../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>
/// declares normative would be evaluated by nobody.
/// </para>
/// <para>
/// <b><see cref="GetAsync"/> takes the tenant explicitly, and that is not the same
/// question <see cref="IFeatureFlags"/> answers.</b> <see cref="RefreshAsync"/> is driven
/// by <c>PUT /api/internal/tenants/{id}/entitlements</c>, which carries a tenant in its
/// path and runs with no tenant context of its own — so a context-resolved port could not
/// serve its own writer. The context-resolved read is <see cref="IFeatureFlags"/>, one
/// level up.
/// </para>
/// <para>
/// <b>Push-primary, which is not push-only.</b> The Hub pushes a projection, so
/// <see cref="RefreshAsync"/> takes one; but ADR-0034 makes a demand-driven
/// <c>POST /api/v1/internal/license/verify</c> the LAST step of a normative resolution
/// order, and that fallback is preserved for <c>HubEntitlementProvider</c> to implement in
/// <see href="../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase 02c</see>. What
/// LearnStack does not do is <b>poll</b>. The absolute prohibition on depending upon a
/// reachable Hub belongs to host resolution, where an anonymous page load must never wait
/// on a control plane; borrowing that sentence here would delete a fallback ADR-0034 calls
/// normative.
/// </para>
/// </remarks>
public interface IEntitlementProvider
{
    /// <summary>The tenant's current projection.</summary>
    Task<EntitlementProjection> GetAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>
    /// Applies a pushed projection, unless it is older than the stored one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Generation-guarded and atomic.</b> <c>generation</c> is monotonic per tenant and
    /// pushes arrive out of order — a retried delivery, two operator edits in flight, a Hub
    /// redeploy. An implementation that persists compares against the stored generation
    /// <b>inside</b> the same statement that performs the write, never as a read-then-write:
    /// a stale push must not resurrect a revoked plan.
    /// </para>
    /// <para>
    /// <b>The comparison admits the equal case.</b> A push applies when its generation is
    /// greater than or equal to the stored one. The difference is only the equal case, and
    /// the equal case is the provisioning flow: the provisioning insert writes
    /// <c>generation</c> default 1 and the Hub's first real projection for that tenant also
    /// carries 1. Under a strict <c>&gt;</c> that projection is discarded and the tenant
    /// keeps an empty row while this method reports
    /// <see cref="EntitlementRefreshOutcome.IgnoredAsStale"/> — a paid tenant reading as
    /// unentitled, reported as success. Replay at the same generation is idempotent
    /// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
    /// Amendment 1 § 3</see>, which reads § 1's strict form as <c>&gt;=</c>).
    /// </para>
    /// </remarks>
    Task<EntitlementRefreshOutcome> RefreshAsync(
        EntitlementProjection projection, CancellationToken ct = default);
}

/// <summary>One tenant's plan, as the Hub projects it.</summary>
/// <param name="TenantId">Whose plan it is.</param>
/// <param name="PlanCode">The plan's code. Carried on the wire as <c>tier</c>.</param>
/// <param name="Features">Feature key to granted, as projected.</param>
/// <param name="Limits">Limit key to ceiling, where <c>-1</c> is unlimited and <c>0</c> denied.</param>
/// <param name="Compliance">Residency, retention and consent caps.</param>
/// <param name="ExpiresAt">
/// When the plan lapses, or <c>null</c> for no scheduled expiry. Stored as
/// <c>valid_until</c>.
/// </param>
/// <param name="GraceUntil">How long a lapsed plan keeps working.</param>
/// <param name="Generation">Monotonic per tenant. The guard <see cref="IEntitlementProvider.RefreshAsync"/> reads.</param>
/// <remarks>
/// <b><c>Compliance</c> and <c>Generation</c> are carried because the contract requires
/// them.</b> The wire schema ADR-0034 pins in both repositories lists
/// <c>tenant_id, tier, features, limits, compliance, expires_at, grace_until, generation</c>
/// as required, and <c>platform_entitlement_cache</c> already carries
/// <c>compliance jsonb NOT NULL</c> and <c>generation bigint NOT NULL</c>. A record that
/// dropped either would make the only sanctioned writer structurally unable to persist a
/// column its own table declares <c>NOT NULL</c>, and would silently discard the caps that
/// gate data residency and recording consent. Two names differ from the wire —
/// <c>PlanCode</c> for <c>tier</c>, <c>ExpiresAt</c> for the <c>valid_until</c> column —
/// following the shipped entity and the mapping the glossary already records.
/// </remarks>
public sealed record EntitlementProjection(
    TenantId TenantId,
    string PlanCode,
    IReadOnlyDictionary<string, bool> Features,
    IReadOnlyDictionary<string, long> Limits,
    ComplianceCaps Compliance,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceUntil,
    long Generation);

/// <summary>The compliance caps a plan carries.</summary>
public sealed record ComplianceCaps(IReadOnlyDictionary<string, ComplianceCap> Caps)
{
    /// <summary>No caps at all.</summary>
    /// <remarks>
    /// A <see cref="FrozenDictionary{TKey,TValue}"/>, as the Null provider's feature and
    /// limit maps already are. This instance is shared by every projection that carries it,
    /// for every tenant, and a <c>Dictionary</c> behind the read-only interface was one cast
    /// away from a write — measured by the review of Packet 9: a cap added through
    /// <c>IDictionary</c> appeared in the next tenant's projection.
    /// </remarks>
    public static readonly ComplianceCaps None =
        new(FrozenDictionary<string, ComplianceCap>.Empty);
}

/// <summary>One compliance cap.</summary>
/// <param name="Allowed">Whether the capability is permitted.</param>
/// <param name="Forced">Whether it is mandatory rather than merely permitted.</param>
/// <param name="Value">The cap's value, where it has one — a region, a retention period.</param>
public sealed record ComplianceCap(bool Allowed, bool Forced, string? Value);

/// <summary>What became of a pushed projection.</summary>
public enum EntitlementRefreshOutcome
{
    /// <summary>The projection is now the stored one.</summary>
    Applied,

    /// <summary>An older or equal-but-superseded projection; nothing changed.</summary>
    IgnoredAsStale,
}
