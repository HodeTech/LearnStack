namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// The only module-facing read of a feature or a limit.
/// </summary>
/// <remarks>
/// <para>
/// <b>It composes; it does not query.</b> It asks
/// <see cref="IEntitlementProvider"/> for the plan half, reads
/// <c>tenant_feature_flags</c> for the tenant half, applies the killswitch overlay, and
/// caches. That is what makes swapping the registered provider change the answer without
/// touching module code — a direct read of <c>platform_entitlement_cache</c> would bypass
/// the provider entirely, and the resolution order ADR-0034 declares normative would be
/// evaluated by nobody
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// § 2</see>).
/// </para>
/// <para>
/// <b>It takes no tenant, unlike <see cref="IEntitlementProvider"/>.</b> The tenant comes
/// from <c>ITenantContext</c>, and a request with none throws rather than guessing: an
/// operator path that genuinely reads across tenants uses <c>IEntitlementAdminQuery</c>,
/// which <see href="../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase 02c</see>
/// ships with the surface that needs it.
/// </para>
/// <para>
/// The pair <c>IFeatureFlagService</c> / <c>IUsageLimitService</c> that
/// <see href="../../../../docs/decisions/0021-feature-based-entitlement.md">ADR-0021</see>
/// § Runtime service contracts named is <b>withdrawn</b>, not renamed.
/// </para>
/// </remarks>
public interface IFeatureFlags
{
    /// <summary>Whether the current tenant has the feature.</summary>
    Task<bool> IsEnabledAsync(FeatureKey key, CancellationToken ct = default);

    /// <summary>
    /// The current tenant's ceiling for the limit.
    /// </summary>
    /// <remarks>
    /// <b>Returns <see cref="long"/>, never <c>long?</c>.</b> "Not projected" is not a
    /// third state a caller can act on, and a nullable return would push a <c>?? what</c>
    /// decision to every call site. An absent key resolves to its catalog default. The
    /// sentinels are <see cref="LimitKeys.Unlimited"/> and <see cref="LimitKeys.Denied"/>.
    /// </remarks>
    Task<long> GetLimitAsync(LimitKey key, CancellationToken ct = default);
}
