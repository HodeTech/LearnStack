using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using Microsoft.Extensions.Logging;
using Npgsql;

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
/// <param name="dataSource">
/// The application data source, built on first use.
/// <para>
/// <b>Its own connection, and NOT the module <c>DbContext</c>.</b> Two reasons, each
/// sufficient. First, <c>AddModuleDbContext</c>'s factory throws when there is no ambient
/// transaction — so injecting the context made resolving <see cref="IFeatureFlags"/>
/// anywhere outside an open unit-of-work frame throw before a single flag was read, which
/// is the opposite of what this port promises: middleware, an endpoint filter, a health
/// check and the anonymous rate limiter are all natural readers and none of them is inside
/// one. It threw even for a plan key with no killswitch, which never touches the overlay.
/// </para>
/// <para>
/// Second, the cache flight that runs the load is process-wide and detached — it survives
/// the abandonment of the caller that started it. A factory closing over a request-scoped
/// context would keep reading through a connection the request's unit of work has since
/// disposed: a race between a live reader and a connection returning to the pool. Opening
/// and disposing a connection INSIDE the factory means an abandoned flight owns and
/// releases everything it touches, exactly as <c>FeatureFlags.LoadTenantFlagsAsync</c>
/// does.
/// </para>
/// </param>
public sealed class KillswitchOverlay(
    Lazy<NpgsqlDataSource> dataSource,
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
    /// The whole switch set, in one query, on a connection of this method's own.
    /// </summary>
    /// <remarks>
    /// One entry rather than one per key, so a toggle invalidates a single cache key.
    /// <b>No tenant announcement, and none is possible</b>: <c>platform_killswitches</c>
    /// carries no tenant column and its only policy is
    /// <c>FOR SELECT TO learnstack_app USING (true)</c> — the switch is global by
    /// construction, so there is nothing to announce and the <c>GRANT</c> bounds the role
    /// instead.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, bool>> LoadAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.Value
            .OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var read = new NpgsqlCommand(
            "SELECT key, is_enabled FROM platform_killswitches", connection);

        var switches = new Dictionary<string, bool>(StringComparer.Ordinal);

        await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            switches[reader.GetString(0)] = reader.GetBoolean(1);
        }

        return switches;
    }

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
