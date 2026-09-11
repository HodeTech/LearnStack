using System.Text.Json;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Modules.Tenancy.Infrastructure;

/// <summary>
/// Resolves a feature or a limit by composing the plan half, the tenant half and the
/// killswitch overlay.
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives here because <c>tenant_feature_flags</c> is this module's table</b>, and it
/// composes over <see cref="IEntitlementProvider"/> rather than reading
/// <c>platform_entitlement_cache</c> — whose storage belongs to the provider that owns it,
/// Tenancy's infrastructure included.
/// </para>
/// <para>
/// The order is
/// <see href="../../../../../docs/architecture/21-feature-flags.md">Feature Flags
/// § Evaluation</see>'s, with the killswitch last because it must win over both halves.
/// </para>
/// </remarks>
/// <param name="dataSource">
/// The application data source, built on first use.
/// <para>
/// <b>Its OWN connection, and that is a correctness requirement rather than a
/// preference.</b> <c>tenant_feature_flags</c> carries <c>ENABLE</c> + <c>FORCE</c> row
/// security, so a read on a connection that has announced no tenant returns <b>zero rows
/// silently</b> — indistinguishable from "this tenant has set no flags", and it never
/// trips a fail-closed <c>catch</c>. A flag resolver can be called at any point in a
/// request, including before <c>TransactionBehavior</c> opens anything, so riding the
/// ambient connection would make the answer depend on where in the pipeline the caller
/// happened to sit. <c>AuditConfigService</c> reaches for its own connection for exactly
/// this reason and says so.
/// </para>
/// </param>
public sealed class FeatureFlags(
    ITenantContext tenantContext,
    IEntitlementProvider provider,
    IKillswitchOverlay killswitches,
    Lazy<NpgsqlDataSource> dataSource,
    ICacheService cache,
    ILogger<FeatureFlags> logger)
    : IFeatureFlags
{
    /// <summary>How long the tenant's own flag rows stay cached.</summary>
    /// <remarks>
    /// The family Standards 20 registers as <c>{tenant_id}:tenancy:feature-flags</c>, at
    /// the hot-path default. Stated rather than inherited, for the reason the audit
    /// projection's is: this is the staleness a tenant sees when it toggles its own flag.
    /// </remarks>
    public static readonly TimeSpan TenantFlagTtl = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(FeatureKey key, CancellationToken ct = default)
    {
        var descriptor = Descriptor(key);
        var tenantId = RequireTenant(key.Value);

        var granted = descriptor.Source == FeatureSource.PlanProjected
            ? await PlanGrantsAsync(descriptor, tenantId, ct).ConfigureAwait(false)
            : await TenantGrantsAsync(descriptor, tenantId, ct).ConfigureAwait(false);

        // LAST, and it wins over both halves. The overlay consults EXACTLY the key the
        // descriptor names: a feature whose descriptor names none has no overlay, and no
        // `killswitch.` prefix is derived from the feature key's own string.
        if (granted && descriptor.Killswitch is { } killswitch)
        {
            // `&=`, not `=`. A killswitch may only NARROW: it exists to close a capability
            // during an incident, never to open one. With a plain assignment the
            // `granted &&` short-circuit was load-bearing for CORRECTNESS — dropping it
            // made an ungranted feature read as granted whenever its switch was on, which
            // is an unbought capability opened by the very mechanism meant to close one.
            // Now the short-circuit is load-bearing only for cost, and the narrowing is in
            // the assignment where it cannot be lost.
            granted &= await killswitches.IsEnabledAsync(killswitch, ct).ConfigureAwait(false);
        }

        return granted;
    }

    /// <inheritdoc />
    public async Task<long> GetLimitAsync(LimitKey key, CancellationToken ct = default)
    {
        if (!LimitKeys.All.TryGetValue(key, out var descriptor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key.Value,
                "That limit is not in LimitKeys. A key must be declared before anything "
                + "can be measured against it, because an undeclared key would resolve to "
                + "nothing and read as an unlimited allowance.");
        }

        var tenantId = RequireTenant(key.Value);

        // Limits are ALWAYS plan-projected and never consult tenant_feature_flags: a
        // tenant that could raise its own ceiling is not a ceiling.
        var projection = await provider.GetAsync(tenantId, ct).ConfigureAwait(false);

        return projection.Limits.TryGetValue(key.Value, out var limit)
            ? limit
            : descriptor.Default;
    }

    private async Task<bool> PlanGrantsAsync(
        FeatureDescriptor descriptor, TenantId tenantId, CancellationToken ct)
    {
        var projection = await provider.GetAsync(tenantId, ct).ConfigureAwait(false);

        return projection.Features.TryGetValue(descriptor.Key.Value, out var granted)
            ? granted
            : descriptor.Default;
    }

    /// <summary>
    /// The tenant's own row, read through the cache.
    /// </summary>
    /// <remarks>
    /// The value column is JSON — a boolean, a rollout percentage or a variant name — so a
    /// row whose value is not a JSON boolean is not an answer to this question and falls
    /// back to the descriptor's default rather than being coerced. A percentage read as
    /// <c>true</c> would enable an experiment for every tenant that had it at 5%.
    /// </remarks>
    private async Task<bool> TenantGrantsAsync(
        FeatureDescriptor descriptor, TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> flags;

        try
        {
            flags = await cache.GetOrSetAsync(
                CacheKey.ForTenant(tenantId.Value, "tenancy", "feature-flags"),
                token => LoadTenantFlagsAsync(tenantId, token),
                new CacheOptions(TenantFlagTtl),
                ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The documented posture for an unreachable tenant table is an answer, not an exception.
        catch (Exception failure) when (failure is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // Fail closed — treat as disabled — which is the posture Hybrid License Model
            // § Failure policy by key class writes for every tenant flag, and the one each
            // of their descriptors declares (Every_tenant_flag_fails_closed pins it): the
            // source is a table in this deployment, so unreachable means a database outage,
            // and under one an experiment is off rather than on. It threw instead, so every
            // path gated on a tenant flag failed outright during the outage the posture
            // exists for.
            //
            // Every failure and not only a DbException: the data source is built lazily, so
            // a missing credential arrives as an InvalidOperationException. A cancellation
            // still leaves — it is the caller's, not an outage. Logged at Error, because an
            // outage this answers silently is one nobody sees.
            LogTenantFlagsUnreachable(logger, descriptor.Key.Value, failure);

            return false;
        }

        if (!flags.TryGetValue(descriptor.Key.Value, out var raw))
        {
            return descriptor.Default;
        }

        if (bool.TryParse(raw, out var plain))
        {
            return plain;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => Unreadable(descriptor, raw),
            };
        }
        catch (JsonException)
        {
            return Unreadable(descriptor, raw);
        }
    }

    private bool Unreadable(FeatureDescriptor descriptor, string raw)
    {
        LogUnreadableFlag(logger, descriptor.Key.Value, raw, null);

        return descriptor.Default;
    }

    /// <summary>
    /// One tenant's flags, on a connection of this method's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BEGIN; SET LOCAL app.tenant_id; SELECT; COMMIT</c> — the announcement is the
    /// point. Read-only, so the transaction exists for the <c>SET LOCAL</c> rather than
    /// for atomicity: a <c>SET LOCAL</c> outside a transaction lasts for the statement and
    /// would leave the pooled connection announcing a tenant afterwards.
    /// </para>
    /// <para>
    /// The <c>SELECT</c> names its tenant too, bound from the trusted argument rather than
    /// read back from the setting. Row security is the second layer, not the only one
    /// (Database Standards § Raw SQL): without the predicate, a policy regression put
    /// another tenant's row into this tenant's cache — measured by the fourth review of
    /// Packet 9, reading as a role that row security does not bind.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> LoadTenantFlagsAsync(
        TenantId tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.Value
            .OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            announce.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await announce.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        await using (var read = new NpgsqlCommand(
            "SELECT key, value::text FROM tenant_feature_flags WHERE tenant_id = @tenant",
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);

            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flags[reader.GetString(0)] = reader.GetString(1);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return flags;
    }

    private static FeatureDescriptor Descriptor(FeatureKey key) =>
        FeatureKeys.All.TryGetValue(key, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(
                nameof(key),
                key.Value,
                "That feature is not in FeatureKeys. A key must be declared before anything "
                + "can gate on it, because an undeclared key resolves to nothing and would "
                + "read as a capability the plan never granted OR one it always does, "
                + "depending on which half answered.");

    /// <summary>
    /// The tenant, or a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It throws rather than guessing.</b> There is no sensible answer for "does this
    /// tenant have the feature" when there is no tenant, and inventing one — the platform
    /// sentinel, or the first tenant found — would answer a question nobody asked. An
    /// operator path that genuinely reads across tenants uses
    /// <c>IEntitlementAdminQuery</c>, which Phase 02c ships with the surface that needs it.
    /// </para>
    /// <para>
    /// <b>Resolved is the context's claim, not a proof of its value</b> — the query filters
    /// check the id as well as the flag, and this read did not. An id never constructed
    /// surfaced as an exception from inside the cache key, and the nil or platform sentinel
    /// would be a lookup under a tenant that cannot exist once a persisting provider lands;
    /// the sentinel is also a value the hard rules forbid a request to carry.
    /// </para>
    /// </remarks>
    private TenantId RequireTenant(string key)
    {
        if (!tenantContext.IsResolved)
        {
            throw new TenantContextMissingException(
                $"'{key}' was resolved on a request with no tenant. IFeatureFlags answers "
                + "for the tenant in context and there is none; a cross-tenant read is "
                + "IEntitlementAdminQuery's, which Phase 02c ships.");
        }

        var tenantId = tenantContext.TenantId;

        if (!StronglyTypedId.IsAssigned(tenantId) || tenantId == TenantId.PlatformSentinel)
        {
            throw new TenantContextMissingException(
                $"'{key}' was resolved under a context that calls itself resolved and names no "
                + "real tenant — an unassigned id, or the platform sentinel. IFeatureFlags "
                + "answers only for a tenant that can own a flag.");
        }

        return tenantId;
    }

    private static readonly Action<ILogger, string, Exception?> LogTenantFlagsUnreachable =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(LogTenantFlagsUnreachable)),
            "The tenant's flags could not be read, so {Key} answered disabled: a tenant flag fails closed, because its source is a table in this deployment and an unreachable one is a database outage (architecture/26 § Failure policy by key class).");

    private static readonly Action<ILogger, string, string, Exception?> LogUnreadableFlag =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogUnreadableFlag)),
            "The tenant flag {Key} holds {Value}, which is not a JSON boolean, so it answered no question and the catalog default stands. A rollout percentage read as `true` would enable an experiment for every tenant that had it at five percent.");
}
