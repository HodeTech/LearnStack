namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// Reads the platform-wide switches. The last word in feature resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry holds the whole set.</b> The overlay is read through the L1 cache under
/// the single family <c>platform:tenancy:killswitch</c> and invalidated on toggle, so the
/// table is touched on a cache miss rather than on every request — and a toggle
/// invalidates one key, which it must, because <c>ICacheService</c> deliberately has no
/// <c>RemoveByPrefixAsync</c> to sweep a per-key family with.
/// </para>
/// <para>
/// <b>A read failure resolves to the key's default — <c>true</c>, the enabled state — and
/// logs at <c>Error</c>.</b> A cache or database outage must not disable every gated path
/// platform-wide: a killswitch nobody can read is a killswitch that fails open, and that
/// is the correct direction here precisely because flipping one is the exceptional act
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// § 5</see>).
/// </para>
/// <para>
/// <b>Tenant-free by construction.</b> Unlike <see cref="IFeatureFlags"/> this takes no
/// tenant and needs none: <c>platform_killswitches</c> carries no tenant column and its
/// read policy is <c>USING (true)</c>, so the answer is the same for every caller —
/// including one with no resolvable tenant at all.
/// </para>
/// </remarks>
public interface IKillswitchOverlay
{
    /// <summary>Whether the switch is on. <c>true</c> when nobody has flipped it.</summary>
    Task<bool> IsEnabledAsync(KillswitchKey key, CancellationToken ct = default);
}
