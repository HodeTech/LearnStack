using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Tenancy.Infrastructure;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The composing read: plan half, tenant half, killswitch last.
/// </summary>
/// <remarks>
/// Against the real <c>tenant_feature_flags</c> as <c>learnstack_app</c>, because the
/// tenant half is a policy-guarded read and a resolver that saw another tenant's flag is
/// the failure worth catching. The plan half runs through the registered provider rather
/// than a table read, which is the property the Phase 02a completion criterion names.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class FeatureFlagsTests
{
    private readonly SchemaFixture _schema;

    public FeatureFlagsTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_plan_feature_comes_from_the_provider_and_never_from_the_tenant_table()
    {
        // Swapping the registered provider changes the answer without touching module
        // code — the completion criterion. And a tenant row for the same key changes
        // nothing, in either direction: 'false' under the provider that grants, 'true' under
        // the one that denies. Without the rows this case could not see a resolver that fell
        // back to the tenant table for a plan key — the fifth review of Packet 9.
        await SeedFlagAsync(SchemaFixture.TenantA, FeatureKeys.CustomDomain.Value, "false");

        try
        {
            (await Flags().IsEnabledAsync(FeatureKeys.CustomDomain))
                .Should().BeTrue("NullEntitlementProvider grants every feature, and the row is not asked");

            await SeedFlagAsync(SchemaFixture.TenantA, FeatureKeys.CustomDomain.Value, "true");

            (await Flags(provider: new DenyingProvider()).IsEnabledAsync(FeatureKeys.CustomDomain))
                .Should().BeFalse("the registered provider is what answers, not a table read");
        }
        finally
        {
            await CleanFlagAsync(FeatureKeys.CustomDomain.Value);
        }
    }

    [Fact]
    public async Task An_absent_tenant_flag_row_resolves_to_the_catalog_default()
    {
        // The fixture seeds tenant A's `live-classroom` flag false. It matches no registry
        // spelling, so this asserts the other half of the mechanism on a key that IS
        // declared: with no row, the descriptor default stands. The row being honoured is
        // A_tenant_flag_row_is_honoured_when_it_is_a_JSON_boolean.
        (await Flags().IsEnabledAsync(FeatureKeys.LessonPlayerV2))
            .Should().BeFalse("no row for it, so the catalog default stands");
    }

    [Fact]
    public async Task A_tenant_flag_row_is_honoured_when_it_is_a_JSON_boolean()
    {
        await using (var seeded = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString))
        await using (var transaction = await seeded.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(seeded, transaction, SchemaFixture.TenantA);
            await using var write = new NpgsqlCommand(
                """
                INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
                VALUES (@tenant, 'learning.lesson_player.v2', 'true',
                        '00000000-0000-7000-8000-000000000001')
                ON CONFLICT (tenant_id, key) DO UPDATE SET value = 'true'
                """,
                (NpgsqlConnection)seeded, (NpgsqlTransaction)transaction);
            write.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await write.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        try
        {
            (await Flags().IsEnabledAsync(FeatureKeys.LessonPlayerV2)).Should().BeTrue();
        }
        finally
        {
            await CleanFlagAsync();
        }
    }

    [Fact]
    public async Task A_flag_holding_a_rollout_percentage_answers_no_question()
    {
        // The value column is JSON — a boolean, a percentage, a variant name. A percentage
        // coerced to `true` would enable an experiment for every tenant that had it at 5%.
        await using (var seeded = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString))
        await using (var transaction = await seeded.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(seeded, transaction, SchemaFixture.TenantA);
            await using var write = new NpgsqlCommand(
                """
                INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
                VALUES (@tenant, 'learning.lesson_player.v2', '5',
                        '00000000-0000-7000-8000-000000000001')
                ON CONFLICT (tenant_id, key) DO UPDATE SET value = '5'
                """,
                (NpgsqlConnection)seeded, (NpgsqlTransaction)transaction);
            write.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await write.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        try
        {
            (await Flags().IsEnabledAsync(FeatureKeys.LessonPlayerV2))
            .Should().BeFalse("the catalog default stands when the row is not a boolean");
        }
        finally
        {
            await CleanFlagAsync();
        }
    }

    [Fact]
    public async Task A_tenant_flag_fails_closed_when_its_table_cannot_be_reached()
    {
        // Architecture/26 fixes the posture — fail closed, treat as disabled — and the read
        // threw instead, so every path gated on a tenant flag failed outright during the
        // outage the posture exists for. Measured the way the review measured it: a data
        // source pointed at a port nothing listens on.
        var flags = Unreachable();

        (await flags.IsEnabledAsync(FeatureKeys.LessonPlayerV2)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_tenant_flag_read_still_leaves_as_a_cancellation()
    {
        // The fallback answers an OUTAGE. A caller that cancelled is not asking any more,
        // and a `false` returned to it would be an answer nobody requested.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await Unreachable().IsEnabledAsync(FeatureKeys.LessonPlayerV2, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>The resolver over a database nothing answers on.</summary>
    private static FeatureFlags Unreachable() =>
        new(Resolved,
            new NullEntitlementProvider(),
            new EnabledOverlay(),
            new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(
                "Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=2")),
            new InMemoryCacheService(
                new SystemClock(),
                new ServiceCollection().AddMetrics().BuildServiceProvider()
                    .GetRequiredService<IMeterFactory>()),
            NullLogger<FeatureFlags>.Instance);

    [Fact]
    public async Task A_killswitch_overrides_a_granted_plan_feature()
    {
        // LAST, and it wins. The provider grants recording; the switch is flipped off.
        var flags = Flags(killswitches: new FlippedOverlay());

        (await flags.IsEnabledAsync(FeatureKeys.ClassroomRecording))
            .Should().BeFalse("the killswitch is the last word");

        (await flags.IsEnabledAsync(FeatureKeys.CustomDomain))
            .Should().BeTrue("its descriptor names no killswitch, so it has no overlay");
    }

    [Fact]
    public async Task A_killswitch_can_only_narrow_and_never_grants()
    {
        // The half the first version missed. Its companion above drives granted = true
        // through a FLIPPED overlay; this drives granted = FALSE through an ENABLED one,
        // which is the direction that matters: a switch exists to close a capability
        // during an incident, never to open one. With a plain assignment instead of `&=`,
        // an unbought feature reads as granted the moment nobody has flipped its switch.
        (await Flags(provider: new DenyingProvider()).IsEnabledAsync(FeatureKeys.ClassroomRecording))
            .Should().BeFalse("the plan does not grant it, and no switch can");
    }

    [Fact]
    public async Task One_tenants_flags_are_never_answered_from_anothers()
    {
        // The cache is a process-wide singleton and the key is the ONLY isolation boundary
        // in front of it, so the tenant segment is load-bearing. Nothing constrained it:
        // every case used one tenant and a fresh cache, so a key with a hard-coded tenant
        // read identically. Both callers share ONE cache here, which is what makes the
        // second answer evidence.
        var cache = new InMemoryCacheService(
            new SystemClock(),
            new ServiceCollection().AddMetrics().BuildServiceProvider()
                .GetRequiredService<IMeterFactory>());

        await using (var seeded = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString))
        await using (var transaction = await seeded.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(seeded, transaction, SchemaFixture.TenantA);
            await using var write = new NpgsqlCommand(
                """
                INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
                VALUES (@tenant, 'learning.lesson_player.v2', 'true',
                        '00000000-0000-7000-8000-000000000001')
                ON CONFLICT (tenant_id, key) DO UPDATE SET value = 'true'
                """,
                (NpgsqlConnection)seeded, (NpgsqlTransaction)transaction);
            write.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await write.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        try
        {
            (await Flags(cache: cache).IsEnabledAsync(FeatureKeys.LessonPlayerV2))
                .Should().BeTrue("tenant A set it");

            (await Flags(cache: cache, tenantContext: new TenantBContext())
                .IsEnabledAsync(FeatureKeys.LessonPlayerV2))
                .Should().BeFalse("tenant B set nothing, and must not read tenant A's set");
        }
        finally
        {
            await CleanFlagAsync();
        }
    }

    [Fact]
    public async Task The_tenant_flags_read_names_its_tenant_where_row_security_would_not()
    {
        // Database Standards § Raw SQL: a raw query carries its tenant predicate, and row
        // security is the second layer rather than the only one. Every other case here reads
        // as learnstack_app, whose policies isolate on their own — so the loader passed all of
        // them with no predicate at all. learnstack_platform bypasses row security by design,
        // which leaves the predicate as the one thing between tenant A's cache and tenant B's
        // row: measured by the fourth review of Packet 9, without it A read B's flag as its own.
        await using (var seeded = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString))
        await using (var transaction = await seeded.BeginTransactionAsync())
        {
            await SchemaQueries.SetTenantAsync(seeded, transaction, SchemaFixture.TenantB);
            await using var write = new NpgsqlCommand(
                """
                INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
                VALUES (@tenant, 'learning.lesson_player.v2', 'true',
                        '00000000-0000-7000-8000-000000000001')
                ON CONFLICT (tenant_id, key) DO UPDATE SET value = 'true'
                """,
                (NpgsqlConnection)seeded, (NpgsqlTransaction)transaction);
            write.Parameters.AddWithValue("tenant", SchemaFixture.TenantB);
            await write.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        try
        {
            (await Flags(tenantContext: new TenantBContext(), connectionString: _schema.Postgres.PlatformConnectionString)
                .IsEnabledAsync(FeatureKeys.LessonPlayerV2))
                .Should().BeTrue("the premise: this role reads the table's rows at all");

            (await Flags(connectionString: _schema.Postgres.PlatformConnectionString)
                .IsEnabledAsync(FeatureKeys.LessonPlayerV2))
                .Should().BeFalse("tenant A has no row, and tenant B's row is not tenant A's");
        }
        finally
        {
            await CleanFlagAsync();
        }
    }

    [Fact]
    public async Task An_undeclared_limit_key_is_refused_rather_than_read_as_unlimited()
    {
        // The sibling of the feature-side guard, and the one nothing killed. `LimitKey` is
        // a plain record struct over a string, so a key deleted from the registry leaves
        // every call site compiling — and a resolver that answered instead of refusing
        // would hand back -1 and let the gated operation run unbounded.
        var act = async () => await Flags().GetLimitAsync(new LimitKey("limits.nobody.declared"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_limit_read_with_no_tenant_is_refused_rather_than_guessed()
    {
        // Covered on the feature path and not on this one. A guess here is worse than a
        // guess there: once a persisting provider lands in Phase 02c, an invented tenant
        // becomes a real lookup key on platform_entitlement_cache — and the obvious
        // invention, the platform sentinel, is a value the hard rules forbid announcing
        // as a request's tenant at all.
        var act = async () =>
            await Flags(tenantContext: UnresolvedTenantContext.Instance)
                .GetLimitAsync(LimitKeys.MaxUsers);

        await act.Should().ThrowAsync<TenantContextMissingException>();
    }

    [Fact]
    public async Task A_limit_comes_from_the_projection_and_ignores_the_tenant_table()
    {
        // A tenant row spelling the limit key is there so "ignores" is measured rather than
        // assumed: a tenant that could raise its own ceiling is not a ceiling.
        await SeedFlagAsync(SchemaFixture.TenantA, LimitKeys.MaxUsers.Value, "5");

        try
        {
            (await Flags().GetLimitAsync(LimitKeys.MaxUsers))
                .Should().Be(LimitKeys.Unlimited, "the null provider projects no ceiling");

            (await Flags(provider: new DenyingProvider()).GetLimitAsync(LimitKeys.MaxUsers))
                .Should().Be(LimitKeys.All[LimitKeys.MaxUsers].Default,
                    "an unprojected limit falls to its catalog floor, never to -1, 0 or the row");
        }
        finally
        {
            await CleanFlagAsync(LimitKeys.MaxUsers.Value);
        }
    }

    [Fact]
    public async Task A_request_with_no_tenant_is_refused_rather_than_guessed()
    {
        // There is no sensible answer for "does this tenant have the feature" when there
        // is no tenant, and inventing one would answer a question nobody asked.
        var flags = Flags(tenantContext: UnresolvedTenantContext.Instance);

        var act = async () => await flags.IsEnabledAsync(FeatureKeys.CustomDomain);

        await act.Should().ThrowAsync<TenantContextMissingException>();
    }

    [Theory]
    [InlineData("unassigned")]
    [InlineData("platform-sentinel")]
    public async Task A_resolved_context_that_names_no_real_tenant_is_refused(string shape)
    {
        // Resolved is the context's claim about itself, not a proof of its value (the fifth
        // review of Packet 9). Unassigned, the id failed from inside the cache key with an
        // exception about the id type; the sentinel read as a tenant, which the hard rules
        // forbid a request to carry. Both halves, because the limit read resolves the tenant
        // on its own path.
        //
        // The unassigned id comes out of an array element: Vogen refuses `default(TenantId)`
        // at compile time (VOG009).
        TenantId[] slot = new TenantId[1];
        var tenant = shape == "unassigned" ? slot[0] : TenantId.PlatformSentinel;
        var flags = Flags(tenantContext: new NamedTenantContext(tenant));

        await flags.Invoking(resolver => resolver.IsEnabledAsync(FeatureKeys.CustomDomain))
            .Should().ThrowAsync<TenantContextMissingException>();
        await flags.Invoking(resolver => resolver.GetLimitAsync(LimitKeys.MaxUsers))
            .Should().ThrowAsync<TenantContextMissingException>();
    }

    [Fact]
    public async Task An_undeclared_key_is_refused_rather_than_resolved()
    {
        var act = async () =>
            await Flags().IsEnabledAsync(new FeatureKey("nobody.declared.this"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>Writes one tenant's row for a key as <c>learnstack_app</c>, replacing any value it held.</summary>
    private async Task SeedFlagAsync(Guid tenant, string key, string value)
    {
        await using var seeded = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await seeded.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(seeded, transaction, tenant);

        await using var write = new NpgsqlCommand(
            """
            INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
            VALUES (@tenant, @key, @value::jsonb, '00000000-0000-7000-8000-000000000001')
            ON CONFLICT (tenant_id, key) DO UPDATE SET value = EXCLUDED.value
            """,
            (NpgsqlConnection)seeded, (NpgsqlTransaction)transaction);
        write.Parameters.AddWithValue("tenant", tenant);
        write.Parameters.AddWithValue("key", key);
        write.Parameters.AddWithValue("value", value);
        await write.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task CleanFlagAsync(string key = "learning.lesson_player.v2")
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM tenant_feature_flags WHERE key = @key",
            (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }

    private static readonly ITenantContext Resolved = new ResolvedContext();

    /// <summary>Tenant A, resolved. The constructor of the real one is internal.</summary>
    private sealed class ResolvedContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => "00-feature-flags-01";

        public string? ModuleName => "tenancy";
    }

    private FeatureFlags Flags(
        IEntitlementProvider? provider = null,
        IKillswitchOverlay? killswitches = null,
        ITenantContext? tenantContext = null,
        ICacheService? cache = null,
        string? connectionString = null) =>
        new(tenantContext ?? Resolved,
            provider ?? new NullEntitlementProvider(),
            killswitches ?? new EnabledOverlay(),
            new Lazy<NpgsqlDataSource>(
                () => NpgsqlDataSource.Create(connectionString ?? _schema.Postgres.AppConnectionString)),
            cache ?? new InMemoryCacheService(
                new SystemClock(),
                new ServiceCollection().AddMetrics().BuildServiceProvider()
                    .GetRequiredService<IMeterFactory>()),
            NullLogger<FeatureFlags>.Instance);

    /// <summary>Tenant B, resolved. The second tenant the cache key has to separate.</summary>
    private sealed class TenantBContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => TenantId.From(SchemaFixture.TenantB);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => "00-feature-flags-b1";

        public string? ModuleName => "tenancy";
    }

    /// <summary>A context that calls itself resolved and names whatever id it is given.</summary>
    private sealed class NamedTenantContext(TenantId tenantId) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => tenantId;

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => "00-feature-flags-named";

        public string? ModuleName => "tenancy";
    }

    /// <summary>Grants nothing and projects no limit — the opposite of the default.</summary>
    private sealed class DenyingProvider : IEntitlementProvider
    {
        public Task<EntitlementProjection> GetAsync(
            TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(new EntitlementProjection(
                tenantId, "denied",
                new Dictionary<string, bool>(StringComparer.Ordinal),
                new Dictionary<string, long>(StringComparer.Ordinal),
                ComplianceCaps.None, null, null, 1));

        public Task<EntitlementRefreshOutcome> RefreshAsync(
            EntitlementProjection projection, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EnabledOverlay : IKillswitchOverlay
    {
        public Task<bool> IsEnabledAsync(KillswitchKey key, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FlippedOverlay : IKillswitchOverlay
    {
        public Task<bool> IsEnabledAsync(KillswitchKey key, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
