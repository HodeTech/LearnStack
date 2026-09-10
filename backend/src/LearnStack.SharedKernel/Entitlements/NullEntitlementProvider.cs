using System.Collections.Frozen;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// The working default: every feature granted, every limit unlimited, nothing persisted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered in every deployment mode, not <c>Development</c> only.</b>
/// <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>
/// names it the working default implementation for the <c>IEntitlementProvider</c> gate,
/// with Phase 02c as the owning phase and "a tenant must be billed or plan-gated" as the
/// trigger. Until that fires there is no billing to enforce and no Hub to ask, so a
/// mode-conditional registration would only make four of the five modes unbootable for a
/// capability none of them uses
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// § 4</see>).
/// </para>
/// <para>
/// <b>The dictionaries are POPULATED, and an empty one would be a silent inversion.</b>
/// Returning empty compiles and passes every feature-side assertion — an absent feature
/// key resolves to its catalog default, which is <c>false</c>, and "no plan gating" reads
/// the same either way. The limit half reverses: an absent LIMIT key resolves to its
/// catalog FLOOR, which is positive, so a provider that promises "no ceiling" would hand
/// every caller the Starter allowance instead. Every key is therefore listed, and the
/// registries are the source of the list so a key added to one cannot be missed here.
/// </para>
/// </remarks>
public sealed class NullEntitlementProvider : IEntitlementProvider
{
    /// <summary>The plan code a projection carries when there is no plan.</summary>
    public const string PlanCode = "null-provider";

    private static readonly FrozenDictionary<string, bool> EveryFeatureGranted =
        FeatureKeys.All.Keys.ToFrozenDictionary(key => key.Value, _ => true);

    private static readonly FrozenDictionary<string, long> EveryLimitUnlimited =
        LimitKeys.All.Keys.ToFrozenDictionary(key => key.Value, _ => LimitKeys.Unlimited);

    /// <inheritdoc />
    public Task<EntitlementProjection> GetAsync(
        TenantId tenantId, CancellationToken ct = default) =>
        Task.FromResult(new EntitlementProjection(
            tenantId,
            PlanCode,
            EveryFeatureGranted,
            EveryLimitUnlimited,
            ComplianceCaps.None,

            // No expiry and no grace: a plan that does not exist cannot lapse, and a
            // far-future instant here would be an expiry somebody eventually has to
            // explain.
            ExpiresAt: null,
            GraceUntil: null,

            // Zero, so any real projection is newer. The guard admits the equal case, and
            // the Hub's first projection for a tenant carries 1 — so a default of 1 here
            // would make that first push a no-op under a strict comparison and a coin flip
            // under an equal-admitting one.
            Generation: 0));

    /// <inheritdoc />
    /// <remarks>
    /// <b><see cref="EntitlementRefreshOutcome.IgnoredAsStale"/>, and never
    /// <see cref="EntitlementRefreshOutcome.Applied"/>.</b> This provider persists nothing,
    /// so reporting <c>Applied</c> would claim durability nothing performed — the same
    /// defect class as a unit-of-work joiner reporting <c>Committed</c> for a row nothing
    /// committed. The caller is the Hub-facing endpoint, and it is better told its push
    /// went nowhere than told it landed.
    /// </remarks>
    public Task<EntitlementRefreshOutcome> RefreshAsync(
        EntitlementProjection projection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        return Task.FromResult(EntitlementRefreshOutcome.IgnoredAsStale);
    }
}
