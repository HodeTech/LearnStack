namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// The name of one plan feature or tenant flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>A type rather than a string, so a key must be declared before it can be asked
/// about.</b> <see cref="IFeatureFlags"/> takes this and never a <c>string</c>, which is
/// the compile-time half of the rule
/// <see href="../../../../docs/architecture/21-feature-flags.md">Feature Flags</see>
/// states: a key exists in <see cref="FeatureKeys"/> before anything reads it.
/// </para>
/// <para>
/// The spelling is the one-way door. It lands in the Hub's plan validators, in the
/// <c>features</c> jsonb every projection persists, and in the wire schema
/// <see href="../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>
/// pins in both repositories — so these strings are the Hub's, verbatim, rather than
/// LearnStack's preference
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// Amendment 1 § 1</see>).
/// </para>
/// </remarks>
public readonly record struct FeatureKey(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The name of one numeric ceiling.</summary>
/// <remarks>
/// Limits are always plan-projected: there is no tenant-flag half, because a tenant that
/// could raise its own ceiling is not a ceiling.
/// </remarks>
public readonly record struct LimitKey(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>The name of one platform-wide switch.</summary>
/// <remarks>
/// Distinct from <see cref="FeatureKey"/> on purpose. A killswitch belongs to no tenant
/// and answers a different question — "is this capability on for the deployment" rather
/// than "is this tenant entitled to it" — and the two are resolved from different tables
/// by different roles.
/// </remarks>
public readonly record struct KillswitchKey(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Where a feature key's answer comes from.</summary>
public enum FeatureSource
{
    /// <summary>The Hub's plan projection, read through <see cref="IEntitlementProvider"/>.</summary>
    /// <remarks>
    /// Never served from <c>tenant_feature_flags</c>. A plan-projected key found in that
    /// table is a tenant that granted itself an entitlement, which is what
    /// <c>PlanProjected_Keys_NotInTenantFlags</c> exists to catch.
    /// </remarks>
    PlanProjected,

    /// <summary>The tenant's own <c>tenant_feature_flags</c> row.</summary>
    /// <remarks>Experiments, rollouts and opt-ins — never anything a plan is sold on.</remarks>
    TenantFlag,
}

/// <summary>
/// What a feature answers when its source cannot be reached past the grace window.
/// </summary>
/// <remarks>
/// <see href="../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>
/// § The entitlement read path requires every feature key class to declare this
/// explicitly, and ADR-0045 Amendment 1 § 5 makes it a required member of every
/// descriptor rather than a table somebody consults. A posture nobody wrote down is a
/// posture nobody re-reads.
/// <para>
/// <b>Declared here, read in <see href="../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase
/// 02c</see>.</b> Nothing in this packet reads it, and that is not an oversight: the
/// degraded path belongs to a provider that can be unreachable, and the only provider
/// shipped here answers from constants and cannot be. `HubEntitlementProvider` is the
/// first reader. The member ships now because the posture is per key and a key added
/// later without one would be a key whose failure behaviour nobody decided — which is
/// exactly what Amendment 1 § 5 exists to prevent.
/// </para>
/// </remarks>
public enum DegradedPosture
{
    /// <summary>Treat as disabled. The default, and the only safe answer for a security surface.</summary>
    FailClosed,

    /// <summary>
    /// Disabled on a cold start, but the last known value otherwise.
    /// </summary>
    /// <remarks>
    /// For product capabilities where losing the feature mid-use is the worse failure: a
    /// paying tenant should not lose recording because the Hub is down — but the platform
    /// must not invent an entitlement it has never seen, which is why a cold start is
    /// still closed.
    /// </remarks>
    FailOpenToLastKnown,
}

/// <summary>Whether exceeding a limit refuses the operation or merely reports it.</summary>
/// <remarks>
/// Packet 9 ships the descriptor and the read. The enforcement path — the <c>403</c> and
/// the <c>usage.alert.soft_limit_reached</c> signal — lands in
/// <see href="../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase 02c</see>, the
/// first phase in which <c>IUsageReporter</c> exists for a soft limit to report to.
/// </remarks>
public enum LimitEnforcement
{
    /// <summary>The gated operation refuses with <c>403</c> once usage reaches the limit.</summary>
    Hard,

    /// <summary>The operation succeeds; a banner and a Hub-side usage alert follow.</summary>
    Soft,
}

/// <summary>Everything the resolver needs to know about one feature key.</summary>
/// <param name="Key">The key itself.</param>
/// <param name="Source">Which of the two halves answers it.</param>
/// <param name="Default">
/// The answer when the source has no entry for it. <c>false</c> for every plan feature —
/// an unlisted capability is one the plan does not grant.
/// </param>
/// <param name="Degraded">What it answers when the source is unreachable past its grace window.</param>
/// <param name="Killswitch">
/// The switch that overrides it, or <c>null</c> for none.
/// </param>
/// <remarks>
/// <b>The killswitch is named, never inferred.</b> Deriving <c>killswitch.classroom.recording</c>
/// from <c>classroom.recording</c> by prefix would make a renamed key silently ungated —
/// the rename would compile, the overlay would look for a switch nobody declared, and the
/// expensive path it guards would stay on through the incident it exists for
/// (ADR-0045 Amendment 1 § 5).
/// </remarks>
public sealed record FeatureDescriptor(
    FeatureKey Key,
    FeatureSource Source,
    bool Default,
    DegradedPosture Degraded,
    KillswitchKey? Killswitch = null);

/// <summary>Everything the resolver needs to know about one limit key.</summary>
/// <param name="Key">The key itself.</param>
/// <param name="Default">
/// The floor an unprojected limit falls back to.
/// </param>
/// <param name="Enforcement">Whether exceeding it refuses or reports.</param>
/// <remarks>
/// <b>Never <c>-1</c> and never <c>0</c>.</b> Unlimited is a gift and zero is an outage:
/// a floor of <c>-1</c> hands an unentitled tenant an unbounded allowance, and a floor of
/// <c>0</c> denies every gated operation the moment a projection is late
/// (<see href="../../../../docs/architecture/26-hybrid-license-model.md">Hybrid License
/// Model § Failure policy by key class</see>).
/// </remarks>
public sealed record LimitDescriptor(LimitKey Key, long Default, LimitEnforcement Enforcement);
