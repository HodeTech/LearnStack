using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Tenancy.Infrastructure;

/// <summary>
/// The killswitch read path: one cached entry over <c>platform_killswitches</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in the Tenancy module's infrastructure because the table does.</b> The
/// switch set is not tenant data, but the migration chain that creates it is this
/// module's, and a store in the composition root would put raw SQL where no module rule
/// can see it.
/// </para>
/// <para>
/// <b>No writer, and that is reachability rather than scheduling.</b> Every toggle runs
/// inside <c>EnterPlatformAdminScope(reason)</c> whose registered gate is
/// <c>DenyAllPlatformAdminGate</c>, so
/// <see href="../../../../../docs/roadmap/phase-03-identity-admin.md">Phase 03</see> owns
/// the toggle command, its permission and its runbook. This type reads a table nothing in
/// this packet can write, and every gated read honours a flipped switch the day one exists.
/// </para>
/// </remarks>
public sealed class KillswitchOverlay(
    TenancyDbContext context,
    ICacheService cache,
    ILogger<KillswitchOverlay> logger)
    : IKillswitchOverlay
{
    /// <summary>How long the switch set stays cached.</summary>
    /// <remarks>
    /// <b>Stated here rather than inherited.</b> Standards 20 fixes this family at the
    /// 60-second hot-path default, and it IS the staleness bound an operator sees: a
    /// killswitch flipped during an incident takes at most this long to take effect on an
    /// instance whose cache is warm, because Packet 9 ships no writer to invalidate it
    /// eagerly. Phase 03's toggle command ships the invalidation with it.
    /// </remarks>
    public static readonly TimeSpan OverlayTtl = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(KillswitchKey key, CancellationToken ct = default)
    {
        // The DEFAULT is what an unreadable overlay answers, and it is read from the
        // registry rather than hard-coded true: a switch the registry does not declare is
        // one nothing should be gating on, and answering true for it would hide that.
        if (!KillswitchKeys.All.TryGetValue(key, out var whenUnknown))
        {
            LogUndeclared(logger, key.Value, null);

            return true;
        }

        IReadOnlyDictionary<string, bool> switches;

        try
        {
            switches = await cache.GetOrSetAsync(
                CacheKey.ForKillswitchOverlay(),
                LoadAsync,
                new CacheOptions(OverlayTtl),
                ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Error, and the key's default. A cache or database outage must not disable
            // every gated path platform-wide — which is what returning false here would
            // do, on every instance, for every switch, until someone noticed.
            LogReadFailed(logger, key.Value, failure);

            return whenUnknown;
        }

        // An absent row and `true` mean the same thing: nobody has flipped it.
        return !switches.TryGetValue(key.Value, out var enabled) || enabled;
    }

    /// <summary>
    /// The whole switch set, in one query.
    /// </summary>
    /// <remarks>
    /// One entry rather than one per key, so a toggle invalidates a single cache key.
    /// <c>AsNoTracking</c> because nothing here writes and a tracked entity would sit in
    /// the request's change tracker where the audit interceptor would snapshot it.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, bool>> LoadAsync(CancellationToken ct) =>
        await context.PlatformKillswitches
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Key, row => row.IsEnabled, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

    private static readonly Action<ILogger, string, Exception?> LogReadFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogReadFailed)),
            "The killswitch overlay could not be read for {Key}; it resolved to the key's default, which is enabled. A cache or database outage must not disable every gated path platform-wide.");

    private static readonly Action<ILogger, string, Exception?> LogUndeclared =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(LogUndeclared)),
            "'{Key}' is not in KillswitchKeys, so nothing can flip it and no gated path should be reading it. It resolved to enabled.");
}
