using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Http;
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
        await using var dataSource = DataSource();

        var exitCode = await Runner(dataSource).RunAsync(CancellationToken.None);

        exitCode.Should().Be(0);

        foreach (var tenant in SeedData.All)
        {
            (await ScalarAsPlatformAsync("SELECT count(*) FROM tenants WHERE id = @tenant", "tenant", tenant.TenantId.Value))
                .Should().Be(1L, "each seed tenant is provisioned once");

            (await ScalarAsPlatformAsync("SELECT count(*) FROM organizations WHERE tenant_id = @tenant", "tenant", tenant.TenantId.Value))
                .Should().Be(2L,
                    "the default organization comes from provisioning and the second from "
                    + "an ordinary command — one organization would make "
                    + "organization-scoped isolation unobservable in the seed");

            (await ScalarAsPlatformAsync("""
                SELECT count(*) FROM tenants
                WHERE id = @tenant AND default_organization_id IS NOT NULL
                """, "tenant", tenant.TenantId.Value))
                .Should().Be(1L, "a tenant without a default organization serves nothing");

            (await ScalarAsPlatformAsync("SELECT count(*) FROM platform_host_to_tenant WHERE tenant_id = @tenant", "tenant", tenant.TenantId.Value))
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
        await using var dataSource = DataSource();
        await Runner(dataSource).RunAsync(CancellationToken.None);

        (await TextAsPlatformAsync(
            "SELECT organization_id::text FROM platform_host_to_tenant WHERE host = @host",
            SeedData.Yoga.Host))
            .Should().Be(SeedData.Yoga.DefaultOrganization.OrganizationId.Value.ToString(),
                "one seed host resolves to an organization");

        // Existence AND nullity, in one count. `SELECT organization_id` returning null is
        // ambiguous between "the column is NULL" and "there is no row", and the ambiguous
        // form was measured passing with demo-yoga never seeded at all — which is the
        // exact scenario this case exists to catch.
        (await CountAsPlatformAsync(
            """
            SELECT count(*) FROM platform_host_to_tenant
            WHERE host = @host AND organization_id IS NULL
            """,
            SeedData.English.Host))
            .Should().Be(1L, "and the other resolves to the tenant as a whole");

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
        await using var dataSource = DataSource();

        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0);
        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0,
            "a second run is the ordinary case, not an error");

        (await ScalarAsPlatformAsync("SELECT count(*) FROM tenants WHERE id = ANY(@ids)", "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(2L, "and it did not double anything");
        (await ScalarAsPlatformAsync("SELECT count(*) FROM organizations WHERE tenant_id = ANY(@ids)", "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(4L);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_conflict_stops_the_run()
    {
        // The seed's whole claim is that it is evidence about the request path, and that
        // is only true if a refusal stops it. `make seed` gates on the exit code, so a
        // seeder that swallowed a policy denial would hand the next step a database it
        // cannot use while reporting success.
        //
        // Measured before this case existed: replacing the throw with a log-and-return —
        // every failure swallowed, every run exiting 0 — left all three other cases green.
        // A seeder that silently ignores a 42501 was indistinguishable from a correct one.
        //
        // Provoked by composing every act unresolved. Provisioning still succeeds, because
        // it is the one command marked [AllowsUnresolvedTenantContext]; the second
        // organization is then refused by the pipeline with a code that is NOT
        // business_rule_violation, which is the only code the runner treats as
        // "already seeded".
        await using var dataSource = DataSource();

        var alwaysUnresolved = new SeedRunner(
            _ => SeedComposition.Build(dataSource, context: null, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

        var seed = async () => await alwaysUnresolved.RunAsync(CancellationToken.None);

        (await seed.Should().ThrowAsync<InvalidOperationException>(
            "a refusal that is not a conflict means the seed did not do its job"))
            .WithMessage("*second organization*");

        // And it stopped where it failed rather than carrying on: the host row for the
        // first tenant was never written.
        (await CountAsPlatformAsync(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
            SeedData.English.Host))
            .Should().Be(0L);
    }

    [Fact]
    public async Task A_host_naming_another_tenants_organization_is_refused_not_crashed()
    {
        // The most consequential write in the module: `platform_host_to_tenant` is the row
        // that decides whose data an anonymous request sees. The organization id is
        // caller-supplied, and the only thing that checked it was the composite foreign
        // key — which raises 23503, has no arm in HttpStatusMap, and therefore answered
        // 500 after the transaction opened and the tenant was announced. Measured.
        //
        // The foreign key stays; it is what makes the race impossible rather than merely
        // unlikely. What this adds is an answer the caller can act on.
        await using var dataSource = DataSource();

        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0);

        var provider = SeedComposition.Build(
            dataSource,
            new SeedTenantContext(
                SeedData.English.TenantId, SeedData.English.DefaultOrganization.OrganizationId),
            NullLoggerFactory.Instance);

        await using (provider)
        {
            // SchemaFixture's OrgA1 belongs to a different tenant entirely.
            var result = await provider.GetRequiredService<ISender>().Send(
                new MapHostToTenantCommand(
                    "smuggled.learnstack.local",
                    OrganizationId.From(SchemaFixture.OrgA1),
                    IsActive: true,
                    IsPubliclyLive: true));

            result.IsFailure.Should().BeTrue("the organization is not this tenant's");
            HttpStatusMap.For(result.Error!.Code).Should().Be(StatusCodes.Status409Conflict,
                "a 500 here is the defect; the caller can fix this input");
            result.Error.Details.Should().ContainKey(
                nameof(MapHostToTenantCommand.OrganizationId));
        }

        (await CountAsPlatformAsync(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
            "smuggled.learnstack.local"))
            .Should().Be(0L, "and nothing was written");
    }

    [Fact]
    public async Task A_conflict_that_is_not_a_uniqueness_refusal_still_stops_the_run()
    {
        // The sharp edge of idempotency-by-conflict. `business_rule_violation` was a safe
        // proxy for "already seeded" while provisioning was the only command: every cause
        // of it really was "this row exists". MapHostToTenantCommand broke that — it
        // returns the same top-level code for a host already taken, an organization that
        // is not this tenant's, and a host the deployment reserved — and only the first
        // means there is nothing to do.
        //
        // Driven with a seed tenant whose host names an organization belonging to somebody
        // else, which is the plausible mistake: the two tenants' organizations are
        // declared side by side in SeedData, so a copy-paste puts one tenant's id under
        // the other. With the top-level code as the test, the run logged "already
        // present", exited 0, and never wrote the row that decides whose data an anonymous
        // request sees.
        await using var dataSource = DataSource();

        // Driven through the reserved-host path, which reaches the same classification
        // without breaking an earlier act: provisioning and the second organization both
        // succeed, and only the host mapping is refused — with `lockey_host_reserved`,
        // which is a `business_rule_violation` that emphatically does not mean the row is
        // already there.
        var runner = new SeedRunner(
            context => SeedComposition.Build(
                dataSource, context, NullLoggerFactory.Instance,
                new OneReservedHost(SeedData.English.Host)),
            NullLogger<SeedRunner>.Instance);

        var seed = async () =>
            await runner.RunAsync(CancellationToken.None, [SeedData.English]);

        (await seed.Should().ThrowAsync<InvalidOperationException>(
            "a conflict that is not a uniqueness refusal means the seed did not do its job"))
            .WithMessage("*host mapping*");

        (await CountAsPlatformAsync(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
            SeedData.English.Host))
            .Should().Be(0L, "and the row was never written");
    }

    [Fact]
    public async Task A_seed_host_another_tenant_already_holds_stops_the_run()
    {
        // "Taken" is not "taken by us". platform_host_to_tenant's primary key is the host,
        // globally, so a conflict is equally consistent with our own prior run and with a
        // different tenant holding the name — and the seeder treated both as "already
        // present", exited 0, and left the demo host pointing at somebody else's data.
        //
        // RLS is what makes the discrimination cheap: under demo-english's own
        // announcement the row is visible only if the row is demo-english's.
        await using var dataSource = DataSource();

        // The fixture's tenant A claims the seed host first, on its own announcement.
        await using (var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.AppConnectionString))
        await using (var claim = new NpgsqlCommand(
            $"""
             BEGIN;
             SELECT set_config('app.tenant_id', '{SchemaFixture.TenantA}', true);
             INSERT INTO platform_host_to_tenant
                 (host, tenant_id, organization_id, is_active, is_publicly_live)
             VALUES ('{SeedData.English.Host}', '{SchemaFixture.TenantA}', NULL, true, true);
             COMMIT;
             """,
            (NpgsqlConnection)connection))
        {
            await claim.ExecuteNonQueryAsync();
        }

        try
        {
            var seed = async () => await Runner(dataSource)
                .RunAsync(CancellationToken.None, [SeedData.English]);

            (await seed.Should().ThrowAsync<InvalidOperationException>(
                "a host held by another tenant is not this seed's prior run"))
                .WithMessage("*not this tenant's*");
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM platform_host_to_tenant WHERE host = @host",
                (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("host", SeedData.English.Host);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The seeder, composed exactly as <c>Program.cs</c> composes it.
    /// </summary>
    /// <remarks>
    /// Through <see cref="SeedComposition"/> rather than a hand-copy of its registrations.
    /// The copy this replaced had already drifted on the axis that mattered — it built a
    /// data source per act where the entry point shares one — so the case that claimed to
    /// exercise "the same shape Program.cs builds" was exercising a different one.
    /// </remarks>
    private static SeedRunner Runner(NpgsqlDataSource dataSource) =>
        new(context => SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

    private NpgsqlDataSource DataSource() =>
        NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

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

    /// <summary>A count, under the platform role, with one named parameter.</summary>
    /// <remarks>
    /// The name is passed rather than inferred from the value's type. Inferring it bound
    /// every shape but <c>Guid[]</c> as <c>"tenant"</c>, so a caller adding a third
    /// parameter shape got a silent mis-binding instead of a compile error.
    /// </remarks>
    private async Task<long> ScalarAsPlatformAsync(
        string sql, string parameterName, object value)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);
        query.Parameters.AddWithValue(parameterName, value);

        return (long)(await query.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsPlatformAsync(string sql, string host)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);
        query.Parameters.AddWithValue("host", host);

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


    /// <summary>A deployment that has reserved exactly one host.</summary>
    private sealed class OneReservedHost(string host) : IReservedHostRegistry
    {
        public bool IsReserved(string normalizedHost) =>
            string.Equals(normalizedHost, host, StringComparison.Ordinal);
    }
}
