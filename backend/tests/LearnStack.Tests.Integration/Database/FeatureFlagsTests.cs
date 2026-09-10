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
        // code — the completion criterion. Here the double denies what the default grants,
        // and the answer follows the provider.
        (await Flags().IsEnabledAsync(FeatureKeys.CustomDomain))
            .Should().BeTrue("NullEntitlementProvider grants every feature");

        (await Flags(provider: new DenyingProvider()).IsEnabledAsync(FeatureKeys.CustomDomain))
            .Should().BeFalse("the registered provider is what answers, not a table read");
    }

    [Fact]
    public async Task A_tenant_flag_comes_from_the_tenant_table()
    {
        // The fixture seeds tenant A's `live-classroom` flag false. It matches no registry
        // spelling, so this asserts the mechanism on a key that IS declared: an absent row
        // resolves to the descriptor default.
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
    public async Task A_limit_comes_from_the_projection_and_ignores_the_tenant_table()
    {
        (await Flags().GetLimitAsync(LimitKeys.MaxUsers))
            .Should().Be(LimitKeys.Unlimited, "the null provider projects no ceiling");

        (await Flags(provider: new DenyingProvider()).GetLimitAsync(LimitKeys.MaxUsers))
            .Should().Be(LimitKeys.All[LimitKeys.MaxUsers].Default,
                "an unprojected limit falls to its catalog floor, never to -1 or 0");
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

    [Fact]
    public async Task An_undeclared_key_is_refused_rather_than_resolved()
    {
        var act = async () =>
            await Flags().IsEnabledAsync(new FeatureKey("nobody.declared.this"));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private async Task CleanFlagAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM tenant_feature_flags WHERE key = 'learning.lesson_player.v2'",
            (NpgsqlConnection)connection);
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
        ITenantContext? tenantContext = null) =>
        new(tenantContext ?? Resolved,
            provider ?? new NullEntitlementProvider(),
            killswitches ?? new EnabledOverlay(),
            new Lazy<NpgsqlDataSource>(
                () => NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString)),
            new InMemoryCacheService(
                new SystemClock(),
                new ServiceCollection().AddMetrics().BuildServiceProvider()
                    .GetRequiredService<IMeterFactory>()),
            NullLogger<FeatureFlags>.Instance);

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
