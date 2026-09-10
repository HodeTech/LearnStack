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
        var flags = await cache.GetOrSetAsync(
            CacheKey.ForTenant(tenantId.Value, "tenancy", "feature-flags"),
            token => LoadTenantFlagsAsync(tenantId, token),
            new CacheOptions(TenantFlagTtl),
            ct).ConfigureAwait(false);

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
    /// <c>BEGIN; SET LOCAL app.tenant_id; SELECT; COMMIT</c> — the announcement is the
    /// point. Read-only, so the transaction exists for the <c>SET LOCAL</c> rather than
    /// for atomicity: a <c>SET LOCAL</c> outside a transaction lasts for the statement and
    /// would leave the pooled connection announcing a tenant afterwards.
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
            "SELECT key, value::text FROM tenant_feature_flags", connection, transaction))
        {
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
    /// <b>It throws rather than guessing.</b> There is no sensible answer for "does this
    /// tenant have the feature" when there is no tenant, and inventing one — the platform
    /// sentinel, or the first tenant found — would answer a question nobody asked. An
    /// operator path that genuinely reads across tenants uses
    /// <c>IEntitlementAdminQuery</c>, which Phase 02c ships with the surface that needs it.
    /// </remarks>
    private TenantId RequireTenant(string key) =>
        tenantContext.IsResolved
            ? tenantContext.TenantId
            : throw new TenantContextMissingException(
                $"'{key}' was resolved on a request with no tenant. IFeatureFlags answers "
                + "for the tenant in context and there is none; a cross-tenant read is "
                + "IEntitlementAdminQuery's, which Phase 02c ships.");

    private static readonly Action<ILogger, string, string, Exception?> LogUnreadableFlag =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogUnreadableFlag)),
            "The tenant flag {Key} holds {Value}, which is not a JSON boolean, so it answered no question and the catalog default stands. A rollout percentage read as `true` would enable an experiment for every tenant that had it at five percent.");
}
