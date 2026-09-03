using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The two seed tenants, written by the seeder against the shipped schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seeder's output is a Packet 7 deliverable, so it is asserted rather than
/// assumed.</b> Two tenants in unrelated domains are what tests the genericity claim, and
/// [Phase 02d](../../../../docs/roadmap/phase-02d-walking-skeleton.md) renders both in a
/// browser — a seed that silently wrote one tenant, or wrote both host rows in the same
/// class, would be discovered there rather than here.
/// </para>
/// <para>
/// <b>As <c>learnstack_app</c>, and through the real commands.</b> The runner is the
/// production type, sending the production commands through the production pipeline; only
/// the connection string differs. Run as the migration or platform role this would pass
/// with every policy inert.
/// </para>
/// <para>
/// The container is shared, so each case removes what it wrote. Cleanup runs as
/// <c>learnstack_platform</c>: the rows belong to tenants with no context to announce.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeederTests : IAsyncLifetime
{
    private readonly SchemaFixture _schema;

    public SeederTests(SchemaFixture schema) => _schema = schema;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => CleanUpAsync();

    [Fact]
    public async Task The_seed_writes_two_tenants_each_with_two_organizations_and_one_host()
    {
        var exitCode = await Runner().RunAsync(CancellationToken.None);

        exitCode.Should().Be(0);

        foreach (var tenant in SeedData.All)
        {
            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM tenants WHERE id = @tenant", tenant.TenantId.Value))
                .Should().Be(1L, "each seed tenant is provisioned once");

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM organizations WHERE tenant_id = @tenant",
                tenant.TenantId.Value))
                .Should().Be(2L,
                    "the default organization comes from provisioning and the second from "
                    + "an ordinary command — one organization would make "
                    + "organization-scoped isolation unobservable in the seed");

            (await ScalarAsPlatformAsync(
                """
                SELECT count(*) FROM tenants
                WHERE id = @tenant AND default_organization_id IS NOT NULL
                """,
                tenant.TenantId.Value))
                .Should().Be(1L, "a tenant without a default organization serves nothing");

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM platform_host_to_tenant WHERE tenant_id = @tenant",
                tenant.TenantId.Value))
                .Should().Be(1L, "one row per tenant, not one per organization");
        }
    }

    [Fact]
    public async Task The_two_host_rows_exercise_both_live_classifications()
    {
        // The reason the seed sets organization_id on one row and leaves it null on the
        // other. `OrgHost` and `TenantHost` take different paths through the resolver and
        // the factory, and a seed that produced only one of them would leave the other
        // exercised by fixtures alone — which is what Packet 7 moved the seed earlier to
        // avoid.
        await Runner().RunAsync(CancellationToken.None);

        (await TextAsPlatformAsync(
            "SELECT organization_id::text FROM platform_host_to_tenant WHERE host = @host",
            SeedData.English.Host))
            .Should().Be(SeedData.English.DefaultOrganization.OrganizationId.Value.ToString(),
                "one seed host resolves to an organization");

        (await TextAsPlatformAsync(
            "SELECT organization_id::text FROM platform_host_to_tenant WHERE host = @host",
            SeedData.Yoga.Host))
            .Should().BeNull("and the other resolves to the tenant as a whole");

        foreach (var tenant in SeedData.All)
        {
            (await TextAsPlatformAsync(
                """
                SELECT (is_active AND is_publicly_live)::text
                FROM platform_host_to_tenant WHERE host = @host
                """,
                tenant.Host))
                .Should().Be("true",
                    "a seed host that is not publicly live is a 404 in the browser Phase "
                    + "02d renders it in");
        }
    }

    [Fact]
    public async Task Running_the_seed_twice_changes_nothing_and_still_succeeds()
    {
        // `make seed` is documented as safe to repeat, and it runs on every `make dev`.
        // The second run cannot pre-check: under the provisioning announcement a SELECT
        // over `tenants` returns no rows by policy, so idempotency is a uniqueness
        // refusal recognised as "already seeded" rather than a query.
        (await Runner().RunAsync(CancellationToken.None)).Should().Be(0);
        (await Runner().RunAsync(CancellationToken.None)).Should().Be(0,
            "a second run is the ordinary case, not an error");

        (await ScalarAsPlatformAsync(
            "SELECT count(*) FROM tenants WHERE id = ANY(@ids)",
            SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(2L, "and it did not double anything");
        (await ScalarAsPlatformAsync(
            "SELECT count(*) FROM organizations WHERE tenant_id = ANY(@ids)",
            SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(4L);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private SeedRunner Runner() =>
        new(Compose, NullLogger<SeedRunner>.Instance);

    /// <summary>
    /// The seeder's own composition, on the fixture's container.
    /// </summary>
    /// <remarks>
    /// A provider per act, around a <c>StaticTenantContextAccessor</c> — the same shape
    /// <c>Program.cs</c> builds, and the reason the seeder never writes
    /// <c>ITenantContextAccessor.Current</c>. Kept in step with it by hand; running the
    /// executable instead would put a process boundary between the assertion and the
    /// failure.
    /// </remarks>
    private ServiceProvider Compose(ITenantContext? context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<ITenantWriteStore, TenantWriteStore>();
        services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
        services.AddScoped<IPlatformHostMappingStore, PlatformHostMappingStore>();
        services.AddLearnStackMediatRPipeline(typeof(ITenantWriteStore).Assembly);

        return services.BuildServiceProvider();
    }

    private async Task CleanUpAsync()
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        var ids = SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray();

        foreach (var statement in new[]
        {
            "DELETE FROM platform_host_to_tenant WHERE tenant_id = ANY(@ids)",
            "UPDATE tenants SET default_organization_id = NULL WHERE id = ANY(@ids)",
            "DELETE FROM organizations WHERE tenant_id = ANY(@ids)",
            "DELETE FROM tenants WHERE id = ANY(@ids)",
        })
        {
            await using var cleanup = new NpgsqlCommand(statement, (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("ids", ids);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> ScalarAsPlatformAsync(string sql, object parameter)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);
        query.Parameters.AddWithValue(parameter is Guid[]? "ids" : "tenant", parameter);

        return (long)(await query.ExecuteScalarAsync())!;
    }

    private async Task<string?> TextAsPlatformAsync(string sql, string host)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);
        query.Parameters.AddWithValue("host", host);

        return (await query.ExecuteScalarAsync()) as string;
    }

}
