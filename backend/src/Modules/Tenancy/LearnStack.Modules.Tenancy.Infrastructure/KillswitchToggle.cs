using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Infrastructure;

/// <summary>
/// Writes <c>platform_killswitches</c> inside an admitted platform scope and records the
/// flip as a MUST row on the same transaction (ADR-0101 § 13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Resolve the key from <see cref="KillswitchKeys.All"/> only, enter the scope
/// (which records its entry first), read the before state, upsert, write the MUST result row
/// on the work transaction, commit, and only then remove the overlay cache entry. Removing
/// before the commit would let a concurrent load repopulate the stale value; removing after
/// a commit whose outcome is unknown still happens, because the flip may have landed.
/// </para>
/// <para>
/// <b>What it does not do.</b> It cannot reach other instances: the cache is process-local
/// and the overlay refreshes within <see cref="KillswitchOverlay.OverlayTtl"/>. The runbook
/// states that lag. Concurrent toggles are last writer wins; the row has no version.
/// </para>
/// </remarks>
public sealed class KillswitchToggle(
    IPlatformAdminScope scope,
    ICacheService cache,
    IClock clock) : IKillswitchToggle
{
    private const string SelectBefore = "SELECT is_enabled FROM platform_killswitches WHERE key = @key FOR UPDATE";

    private const string Upsert =
        "INSERT INTO platform_killswitches (key, is_enabled, reason, toggled_at, toggled_by) "
        + "VALUES (@key, @enabled, @reason, @at, NULL) "
        + "ON CONFLICT (key) DO UPDATE SET is_enabled = EXCLUDED.is_enabled, "
        + "reason = EXCLUDED.reason, toggled_at = EXCLUDED.toggled_at, toggled_by = NULL";

    /// <inheritdoc />
    public async Task<KillswitchToggleResult> ToggleAsync(
        PlatformAdmission admission, KillswitchKey key, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Operation, PlatformOperations.KillswitchToggle))
        {
            throw new ArgumentException("The admission is not for a killswitch toggle.", nameof(admission));
        }

        if (!KillswitchKeys.All.TryGetValue(key, out var registryDefault))
        {
            return new KillswitchToggleResult(KillswitchToggleOutcome.UnknownKey, key, true, true);
        }

        var commitAttempted = false;
        try
        {
            await using var handle = await scope.EnterAsync(admission, cancellationToken);

            var rows = await handle.QueryAsync(
                SelectBefore,
                [new PlatformSqlParameter("key", key.Value)],
                reader => reader.GetBoolean(0),
                cancellationToken);
            var before = rows.Count == 0 ? registryDefault : rows[0];

            await handle.ExecuteAsync(
                Upsert,
                [
                    new PlatformSqlParameter("key", key.Value),
                    new PlatformSqlParameter("enabled", enabled),
                    new PlatformSqlParameter("reason", admission.Ticket),
                    new PlatformSqlParameter("at", clock.UtcNow),
                ],
                cancellationToken);

            await handle.WriteResultAsync(
                new PlatformResult(
                    TenantId.PlatformSentinel,
                    "PlatformKillswitch",
                    key.Value,
                    State(before),
                    State(enabled)),
                cancellationToken);

            commitAttempted = true;
            await handle.CommitAsync(cancellationToken);
            return new KillswitchToggleResult(KillswitchToggleOutcome.Applied, key, before, enabled);
        }
        finally
        {
            if (commitAttempted)
            {
                // Including an indeterminate commit: the flip may have landed, and a stale
                // cache entry must not outlive it. A failure here is not the caller's to
                // handle; the entry expires within the overlay TTL regardless.
                await RemoveCacheAsync();
            }
        }
    }

    private static string State(bool enabled) => enabled ? "{\"isEnabled\":true}" : "{\"isEnabled\":false}";

    private async Task RemoveCacheAsync()
    {
        try
        {
            await cache.RemoveAsync(CacheKey.ForKillswitchOverlay(), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The cache is an optimisation here; the overlay TTL bounds the lag.
        }
    }
}
