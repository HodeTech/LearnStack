using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Tenancy.Infrastructure;

/// <summary>
/// Writes <c>platform_killswitches</c> inside an admitted platform scope and records the
/// flip as a MUST row on the same transaction (ADR-0101 § 13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Resolve the key from <see cref="KillswitchKeys.All"/> only, enter the scope
/// (which records its entry first), make sure the row exists, lock it and read the before
/// state, update it, write the MUST result row on the work transaction, commit, and only then
/// remove the overlay cache entry. Removing before the commit would let a concurrent load
/// repopulate the stale value; removing after a commit whose outcome is unknown still happens,
/// because the flip may have landed.
/// </para>
/// <para>
/// <b>The first toggle of a key cannot race.</b> <c>SELECT … FOR UPDATE</c> locks nothing when no
/// row exists, so two first toggles would both read the registry default as their before state.
/// The row is therefore created at its default with <c>ON CONFLICT DO NOTHING</c> first; the
/// lock and the read then see one row, and the loser reads the winner's committed state.
/// </para>
/// <para>
/// <b>What it does not do.</b> It cannot reach other instances: the cache is process-local
/// and the overlay refreshes within <see cref="KillswitchOverlay.OverlayTtl"/>. The runbook
/// states that lag. Concurrent toggles serialise on the row lock, so each one's before state is
/// the previous one's after state.
/// </para>
/// </remarks>
public sealed partial class KillswitchToggle(
    IPlatformAdminScope scope,
    ICacheService cache,
    IClock clock,
    ILogger<KillswitchToggle> logger) : IKillswitchToggle
{
    private const string EnsureRow =
        "INSERT INTO platform_killswitches (key, is_enabled, reason, toggled_at, toggled_by) "
        + "VALUES (@key, @default, NULL, @at, NULL) ON CONFLICT (key) DO NOTHING";

    private const string SelectBefore = "SELECT is_enabled FROM platform_killswitches WHERE key = @key FOR UPDATE";

    private const string Update =
        "UPDATE platform_killswitches SET is_enabled = @enabled, reason = @reason, "
        + "toggled_at = @at, toggled_by = NULL WHERE key = @key";

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

            var at = clock.UtcNow;
            await handle.ExecuteAsync(
                EnsureRow,
                [
                    new PlatformSqlParameter("key", key.Value),
                    new PlatformSqlParameter("default", registryDefault),
                    new PlatformSqlParameter("at", at),
                ],
                cancellationToken);

            var rows = await handle.QueryAsync(
                SelectBefore,
                [new PlatformSqlParameter("key", key.Value)],
                reader => reader.GetBoolean(0),
                cancellationToken);
            var before = rows[0];

            await handle.ExecuteAsync(
                Update,
                [
                    new PlatformSqlParameter("key", key.Value),
                    new PlatformSqlParameter("enabled", enabled),
                    new PlatformSqlParameter("reason", admission.Ticket),
                    new PlatformSqlParameter("at", at),
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

            // Past the point where the caller's abort matters: a cancelled commit leaves its
            // outcome unknown, which is the state this order is built to avoid.
            commitAttempted = true;
            await handle.CommitAsync(CancellationToken.None);
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
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The cache is an optimisation here; the overlay TTL bounds the lag. The failure
            // is still worth a line: a store that cannot remove is a store that serves stale.
            LogCacheRemovalFailed(logger, failure);
        }
    }

    [LoggerMessage(
        EventId = 7104,
        Level = LogLevel.Warning,
        Message = "The killswitch overlay cache entry could not be removed after a toggle; it expires within the overlay TTL.")]
    private static partial void LogCacheRemovalFailed(ILogger logger, Exception failure);
}
