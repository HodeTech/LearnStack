using LearnStack.Modules.Customization.Application.Contracts.Customization;
using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
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
/// The container is shared, but each case owns a disposable migrated database.
/// Append-only audit rows and restrictive Education foreign keys are never erased
/// to reset a later case; dropping the test-owned database ends the fixture.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeederTests : IAsyncLifetime
{
    private readonly SchemaFixture _schema;
    private DisposableSchemaDatabase _database = null!; // Initialized by the per-test fixture.

    public SeederTests(SchemaFixture schema) => _schema = schema;

    public async Task InitializeAsync() => _database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

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
    public async Task Every_seeded_tenant_starts_with_a_live_content_type_and_level_taxonomy()
    {
        // The built-ins exist so a tenant that has authored nothing still resolves
        // something. Asserted as ACTIVE, not merely present: a draft is addressable and
        // constrains nothing, so a seed that registered without publishing would leave
        // the runtime with the same nothing it started with — and every structural
        // check would still pass.
        await using var dataSource = DataSource();

        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0);

        foreach (var tenant in SeedData.All)
        {
            (await ScalarAsPlatformAsync("""
                SELECT count(*) FROM tenant_content_types
                WHERE tenant_id = @tenant AND key = 'card' AND status = 'Active'
                  AND renderer_key = 'default-card' AND deleted_at IS NULL
                """, "tenant", tenant.TenantId.Value))
                .Should().Be(1L, "one live card content type per tenant");

            (await ScalarAsPlatformAsync("""
                SELECT count(*) FROM tenant_level_taxonomies
                WHERE tenant_id = @tenant AND key = 'plain' AND status = 'Active'
                  AND deleted_at IS NULL
                """, "tenant", tenant.TenantId.Value))
                .Should().Be(1L, "one live plain level taxonomy per tenant");

            (await ScalarAsPlatformAsync("""
                SELECT count(*) FROM tenant_level_taxonomy_items
                WHERE tenant_id = @tenant AND taxonomy_key = 'plain'
                """, "tenant", tenant.TenantId.Value))
                .Should().Be(3L, "a level vocabulary with no bands resolves everything to nothing");

            // The counter every cache key embeds. Both built-in and declared definitions
            // each register and publish; a reader holding a pre-seed key cannot reach a
            // stale entry — and a generation still at its default would mean the writes
            // happened without invalidating anything.
            (await ScalarAsPlatformAsync("""
                SELECT generation FROM customization_generations WHERE tenant_id = @tenant
                """, "tenant", tenant.TenantId.Value))
                .Should().Be(SeedData.CustomizationGeneration(tenant), "one bump per customization write, in that write's transaction");
        }
    }

    [Fact]
    public async Task The_built_ins_are_the_tenant_s_own_rows_and_not_shared()
    {
        // Both tenants get a `card` and a `plain`, and they are four rows rather than
        // two: a built-in is a row a tenant may deprecate and succeed on its own, which
        // is only true if it belongs to that tenant. A shared row would make one
        // tenant's edit the other's.
        await using var dataSource = DataSource();

        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0);

        // Scoped to the seed's tenants: the shared fixture seeds customization rows
        // for two others, and a global count would be measuring both.
        var ids = SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray();

        (await ScalarAsPlatformAsync(
            "SELECT count(DISTINCT tenant_id) FROM tenant_content_types "
            + "WHERE key = 'card' AND tenant_id = ANY(@ids)", "ids", ids))
            .Should().Be(2L, "the built-in belongs to each tenant separately");

        (await ScalarAsPlatformAsync(
            "SELECT count(DISTINCT id) FROM tenant_content_types "
            + "WHERE key = 'card' AND tenant_id = ANY(@ids)", "ids", ids))
            .Should().Be(2L, "one row per tenant, each with its own id");
    }

    [Fact]
    public async Task Running_the_seed_twice_changes_nothing_and_still_succeeds()
    {
        // `make seed` is documented as safe to repeat, and it runs on every `make dev`.
        // Contextual pre-checks skip completed acts before sending any writer. The
        // full snapshot includes every root, satellite, generation and audit outcome.
        await using var dataSource = DataSource();

        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0);
        var before = await SnapshotAsync();
        (await Runner(dataSource).RunAsync(CancellationToken.None)).Should().Be(0,
            "a second run is the ordinary case, not an error");
        (await SnapshotAsync()).Should().Be(before, "all rows, timestamps, versions, generations and every audit outcome remain unchanged");

        (await ScalarAsPlatformAsync("SELECT count(*) FROM tenants WHERE id = ANY(@ids)", "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(2L, "and it did not double anything");
        (await ScalarAsPlatformAsync("SELECT count(*) FROM organizations WHERE tenant_id = ANY(@ids)", "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(4L);

        // The built-ins have two ways to double that the tenancy rows do not: the
        // register conflicts on a versioned key, and the publish refuses because the
        // definition is already live. Both are "already seeded" and neither may write.
        // Scoped to the seed's own tenants: the shared fixture seeds customization rows
        // for two OTHER tenants, and an unfiltered count would be measuring both.
        (await ScalarAsPlatformAsync(
            "SELECT count(*) FROM tenant_content_types WHERE tenant_id = ANY(@ids)",
            "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(SeedData.Inventory.ContentTypes, "a second run registers nothing");
        (await ScalarAsPlatformAsync(
            "SELECT count(*) FROM tenant_level_taxonomy_items WHERE tenant_id = ANY(@ids)",
            "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(SeedData.Inventory.Bands, "and adds no bands");
        (await ScalarAsPlatformAsync(
            "SELECT max(generation) FROM customization_generations WHERE tenant_id = ANY(@ids)",
            "ids", SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray()))
            .Should().Be(SeedData.All.Max(SeedData.CustomizationGeneration), "and invalidates nothing, because it changed nothing");
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
        // Refusing contextual verification must stop before any writer. Supplying
        // an unresolved context for every requested scope exercises that boundary.
        await using var dataSource = DataSource();

        var alwaysUnresolved = new SeedRunner(
            _ => SeedComposition.Build(dataSource, context: null, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

        var seed = async () => await alwaysUnresolved.RunAsync(CancellationToken.None);

        (await seed.Should().ThrowAsync<InvalidOperationException>(
            "a refusal that is not a conflict means the seed did not do its job"))
            .WithMessage("*Reading seed verification*");

        // And it stopped where it failed rather than carrying on: the host row for the
        // first tenant was never written.
        (await CountAsPlatformAsync(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host",
            SeedData.English.Host))
            .Should().Be(0L);
    }

    [Theory]
    [InlineData("content-type")]
    [InlineData("taxonomy")]
    public async Task A_built_in_whose_id_another_tenant_holds_stops_the_run(string subject)
    {
        // The ownership verification, for the two customization acts. A definition's
        // id is a uuid primary key and therefore GLOBAL, while its key is per tenant —
        // so a second tenant handed the first tenant's id conflicts on the primary key
        // and the conflict looks exactly like a prior run of its own.
        //
        // Without the check the seeder logs "already present", exits 0, and leaves that
        // tenant with no definition at all — the same masking defect the host act was
        // given this verification for.
        //
        // BOTH arms, because they are two switch cases: measured, with only the
        // taxonomy arm forced to report "owned", the whole 1461-test suite stayed
        // green while half the defect this packet exists to close was still open.
        await using var dataSource = DataSource();

        // ONLY the first tenant, so the second genuinely has no definition of its own.
        // Seeding both first would make the ownership check answer "yes, mine" about a
        // row it wrote a moment earlier, and the case would prove nothing.
        (await Runner(dataSource)
            .RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        // The second tenant, re-declared with the first tenant's built-in id.
        var collidingYoga = subject == "content-type"
            ? SeedData.Yoga with { BuiltInContentTypeId = SeedData.English.BuiltInContentTypeId }
            : SeedData.Yoga with { BuiltInTaxonomyId = SeedData.English.BuiltInTaxonomyId };

        var seed = async () => await Runner(dataSource)
            .RunAsync(CancellationToken.None, [collidingYoga]);

        // Matched on the ownership refusal's own words, not on the act label: with the
        // check gone the run still fails, one act later, because the publish cannot see
        // a row it does not own — and an assertion on the label alone passes for that
        // too. Measured: it did.
        (await seed.Should().ThrowAsync<InvalidOperationException>(
            "a conflict on a row this tenant does not own is not a prior run"))
            .WithMessage("*the row that holds the name is not this tenant's*");
    }

    [Fact]
    public async Task An_organization_whose_id_another_tenant_holds_stops_the_run()
    {
        // The same hole, in the act that predates this packet: `SecondOrganizationAct`'s
        // ownership arm had no negative case either, and forcing it to report "owned"
        // is invisible to the whole suite. It is not the slug that collides globally —
        // ux_organizations_tenant_id_slug is per tenant — it is the primary key, the
        // same shape as the two customization cases above.
        await using var dataSource = DataSource();

        (await Runner(dataSource)
            .RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);

        var collidingYoga = SeedData.Yoga with
        {
            SecondOrganization = SeedData.Yoga.SecondOrganization with
            {
                OrganizationId = SeedData.English.SecondOrganization.OrganizationId,
            },
        };

        var seed = async () => await Runner(dataSource)
            .RunAsync(CancellationToken.None, [collidingYoga]);

        (await seed.Should().ThrowAsync<InvalidOperationException>(
            "an organization id another tenant holds is not this tenant's prior run"))
            .WithMessage("*the row that holds the name is not this tenant's*");
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
        await ProvisionForeignAsync(dataSource);

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
        await ProvisionForeignAsync(dataSource);

        // A foreign tenant claims the seed host first, on its own announcement.
        await using (var connection = await PostgresFixture.OpenAsync(
            _database.AppConnectionString))
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
                _database.PlatformConnectionString);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM platform_host_to_tenant WHERE host = @host",
                (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("host", SeedData.English.Host);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Seeder_process_returns_zero_on_repeat_and_nonzero_on_mismatch()
    {
        async Task<int> RunProcess()
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.deps.json"));
            start.ArgumentList.Add(typeof(SeedRunner).Assembly.Location);
            // The test-owned password stays in the environment, never argv or a failure message.
            start.Environment["ConnectionStrings__Default"] = _database.AppConnectionString;
            using var process = new System.Diagnostics.Process { StartInfo = start };
            process.Start().Should().BeTrue();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await Task.WhenAll(output, errors);
            return process.ExitCode;
        }
        (await RunProcess()).Should().Be(0);
        var complete = await SnapshotAsync();
        (await RunProcess()).Should().Be(0);
        (await SnapshotAsync()).Should().Be(complete);
        // A test-owned pre-existing mismatch, not a second normal seed write path.
        await using (var connection = await PostgresFixture.OpenAsync(_database.PlatformConnectionString))
        await using (var change = new NpgsqlCommand("UPDATE tenants SET display_name = 'Different' WHERE id = @id", (NpgsqlConnection)connection))
        {
            change.Parameters.AddWithValue("id", SeedData.English.TenantId.Value);
            (await change.ExecuteNonQueryAsync()).Should().Be(1);
        }
        var mismatch = await SnapshotAsync();
        (await RunProcess()).Should().Be(1);
        (await SnapshotAsync()).Should().Be(mismatch);
    }

    [Fact]
    public async Task Complete_inventory_has_exact_states_pins_translations_and_null_or_exact_write_scope()
    {
        await using var source = DataSource();
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        await AssertInventoryAsync(source);
        var propertyCounts = new List<int>();
        foreach (var tenant in SeedData.All)
        {
            var type = tenant.Curriculum?.ContentType ?? throw new InvalidOperationException("Missing curriculum.");
            await using var provider = SeedComposition.Build(source, new SeedTenantContext(tenant.TenantId, null), NullLoggerFactory.Instance);
            var result = await provider.GetRequiredService<ISender>().Send(new GetContentTypeSeedStateQuery(type.Id));
            result.IsSuccess.Should().BeTrue();
            using var schema = JsonDocument.Parse(result.Value!.State!.JsonSchema);
            propertyCounts.Add(schema.RootElement.GetProperty("properties").EnumerateObject().Count());
        }
        propertyCounts.Distinct().Count().Should().Be(SeedData.All.Count,
            "the two seeded schemas must differ in shape, not only property names or text");
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log")).Should().Be(SeedData.ExpectedAuditWrites,
            "verification is Off, and each normal seed write is audited exactly once");
    }

    [Theory]
    [InlineData("content type")]
    [InlineData("lesson translation")]
    public async Task Interrupted_authoring_resumes_without_rewriting_completed_acts(string interruptedAct)
    {
        await using var source = DataSource();
        var interrupted = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance),
            new InterruptAfter(interruptedAct));
        var run = () => interrupted.RunAsync(CancellationToken.None);
        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Injected seed interruption");
        var previousAudits = await AuditRowsAsync();
        previousAudits.Should().NotBeEmpty("the interruption follows real committed seed writes");
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        await AssertInventoryAsync(source);
        (await AuditRowsAsync()).Should().Contain(previousAudits, "the completed writes retain their exact durable audit rows");
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log")).Should().Be(SeedData.ExpectedAuditWrites,
            "resuming creates only the remaining acts, without duplicate success or failure audits");
        var completed = await SnapshotAsync();
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        (await SnapshotAsync()).Should().Be(completed);
    }

    [Fact]
    public async Task Concurrent_seeds_prove_a_real_provisioning_race_and_converge_without_duplicates()
    {
        await using var source = DataSource();
        using var barrier = new Barrier(2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        ServiceProvider Compose(ITenantContext? context)
        {
            if (context is null && !barrier.SignalAndWait(TimeSpan.FromSeconds(30), deadline.Token))
                throw new InvalidOperationException("Seed race barrier timed out.");
            return SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        }
        var first = new SeedRunner(Compose, NullLogger<SeedRunner>.Instance);
        var second = new SeedRunner(Compose, NullLogger<SeedRunner>.Instance);
        // Both runners read the absent tenant before either provisioning write can start.
        // Only the first tenant participates in this barrier; later runs use normal scopes.
        var outcomes = await Task.WhenAll(Task.Run(() => first.RunAsync(deadline.Token, [SeedData.English])),
            Task.Run(() => second.RunAsync(deadline.Token, [SeedData.English])));
        outcomes.Should().OnlyContain(code => code == 0);
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        await AssertInventoryAsync(source);
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log WHERE outcome = 'success'"))
            .Should().Be(SeedData.ExpectedAuditWrites, "only one successful writer wins each act");
        var completed = await SnapshotAsync();
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        (await SnapshotAsync()).Should().Be(completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_translation_writers_recheck_the_completed_act_after_a_real_non_provisioning_race(bool divergent)
    {
        await using var source = DataSource();
        var original = SeedData.English.Curriculum!.Courses[0];
        var course = original with { Status = "Draft", Lessons = [], Translations = [original.Translations[0]] };
        var tenant = SeedData.English with { Curriculum = SeedData.English.Curriculum with { Courses = [course] } };
        var competingTranslation = course.Translations[0] with { Title = course.Translations[0].Title + " competing" };
        var competingCourse = course with { Translations = [competingTranslation] };
        var competing = divergent
            ? tenant with { Curriculum = tenant.Curriculum! with { Courses = [competingCourse] } }
            : tenant;
        var interrupted = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance),
            new InterruptAfter("course"));
        var initial = () => interrupted.RunAsync(CancellationToken.None, [tenant]);
        (await initial.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Injected seed interruption");

        // Hold the first UPDATE after the incomplete-state reads. The competing
        // translation batch waits on the root or translation's unique key. Both
        // have chosen a writer, so one real refusal must reach ActAsync's recheck.
        var lockKey = Random.Shared.NextInt64(1, long.MaxValue);
        await using var owner = await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await using (var setup = new NpgsqlCommand($"""
            CREATE FUNCTION seed_translation_gate() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = '{course.Id}'::uuid THEN
                    PERFORM pg_advisory_xact_lock({lockKey});
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER seed_translation_gate BEFORE UPDATE ON courses
            FOR EACH ROW EXECUTE FUNCTION seed_translation_gate();
            SELECT pg_advisory_lock({lockKey});
            """, (NpgsqlConnection)owner))
            await setup.ExecuteNonQueryAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var observer = await source.OpenConnectionAsync(deadline.Token);
        var firstLog = new SeedActRecorder("course translation");
        var secondLog = new SeedActRecorder("course translation");
        async Task<InvalidOperationException?> Attempt(SeedTenant declared, SeedActRecorder log)
        {
            try
            {
                var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance), log);
                (await runner.RunAsync(deadline.Token, [declared])).Should().Be(0);
                return null;
            }
            catch (InvalidOperationException refused) { return refused; }
        }
        var first = Attempt(tenant, firstLog);
        var second = Attempt(competing, secondLog);
        try
        {
            var bothWaiting = false;
            while (!deadline.IsCancellationRequested)
            {
                await using var waiting = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database() AND usename = 'learnstack_app'
                      AND wait_event_type = 'Lock'
                      AND (query LIKE '%courses%' OR query LIKE '%course_translations%')
                    """, observer);
                if ((long)(await waiting.ExecuteScalarAsync(deadline.Token))! == 2)
                {
                    bothWaiting = true;
                    break;
                }
                await Task.Delay(25, deadline.Token);
            }
            bothWaiting.Should().BeTrue("both real translation writers must reach their locked save before release");
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({lockKey})", (NpgsqlConnection)owner);
            await release.ExecuteScalarAsync();
            await Task.WhenAll(first, second);
        }
        var outcomes = await Task.WhenAll(first, second);
        SeedTenant winner;
        if (divergent)
        {
            outcomes.Should().ContainSingle(outcome => outcome == null);
            outcomes.Should().ContainSingle(outcome => outcome != null).Which!.Message.Should().StartWith("Seed mismatch in course");
            var loserLog = outcomes[0] is not null ? firstLog : secondLog;
            loserLog.Completed.Should().BeFalse("the typed race must fail its exact postcondition before reporting the act complete; a later final-state check is insufficient");
            winner = outcomes[0] is null ? tenant : competing;
        }
        else
        {
            outcomes.Should().OnlyContain(outcome => outcome == null);
            new[] { firstLog.AlreadyPresent, secondLog.AlreadyPresent }.Should().ContainSingle(present => present);
            winner = tenant;
        }
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log WHERE operation = 'education.course.translation_add' AND outcome = 'success'"))
            .Should().Be(1);
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log WHERE operation = 'education.course.translation_add' AND outcome <> 'success'"))
            .Should().Be(1, "the loser is a witnessed real refusal, not an incidental completed-act skip");
        var state = (await ReadAsync(source, new SeedTenantContext(tenant.TenantId, course.OrganizationId), new GetCourseSeedStateQuery(course.Id))).State!;
        state.Version.Should().Be(1);
        state.Translations.Should().ContainSingle().Which.Slug.Should().Be(course.Translations[0].Slug);
        state.Translations[0].Title.Should().Be(winner.Curriculum!.Courses[0].Translations[0].Title);
        var completed = await SnapshotAsync();
        (await Runner(source).RunAsync(CancellationToken.None, [winner])).Should().Be(0);
        (await SnapshotAsync()).Should().Be(completed, "the rerun retains the failed race audit and adds no outcome");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("default organization")]
    [InlineData("second organization")]
    [InlineData("host scope")]
    [InlineData("locale")]
    [InlineData("undeclared locale")]
    [InlineData("type key")]
    [InlineData("type revision")]
    [InlineData("schema")]
    [InlineData("taxonomy")]
    [InlineData("theme")]
    [InlineData("course pin")]
    [InlineData("course access")]
    [InlineData("course state")]
    [InlineData("course translation")]
    [InlineData("lesson pin")]
    [InlineData("lesson sort")]
    [InlineData("lesson body")]
    public async Task Completed_seed_mismatch_fails_without_mutating_any_existing_row_or_audit(string mismatch)
    {
        await using var source = DataSource();
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        var original = SeedData.English;
        if (mismatch == "undeclared locale")
        {
            await using var provider = SeedComposition.Build(source, new SeedTenantContext(original.TenantId, null), NullLoggerFactory.Instance);
            await using var scope = provider.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var state = (await sender.Send(new GetTenantSeedStateQuery())).Value!.State!;
            (await sender.Send(new AddTenantLocaleCommand(state.Version, "fr", true, false, 3))).IsSuccess.Should().BeTrue();
        }
        var before = await SnapshotAsync();
        var content = original.Curriculum ?? throw new InvalidOperationException("Missing fixture curriculum.");
        var course = content.Courses[0];
        var lesson = course.Lessons[0];
        var changed = mismatch switch
        {
            "tenant" => original with { DisplayName = "Different" },
            "default organization" => original with { DefaultOrganization = original.DefaultOrganization with { DisplayName = "Different" } },
            "second organization" => original with { SecondOrganization = original.SecondOrganization with { DisplayName = "Different" } },
            "host scope" => original with { MapHostToDefaultOrganization = true },
            "locale" => original with { Curriculum = content with { Locales = [content.Locales[0] with { Sort = 4 }] } },
            "undeclared locale" => original,
            "type key" => original with { Curriculum = content with { ContentType = content.ContentType with { Key = "different" } } },
            "type revision" => original with { Curriculum = content with { ContentType = content.ContentType with { SchemaVersion = 2 } } },
            "schema" => original with { Curriculum = content with { ContentType = content.ContentType with { JsonSchema = "{}" } } },
            "taxonomy" => original with { Curriculum = content with { Taxonomy = content.Taxonomy with { Bands = content.Taxonomy.Bands.RemoveAt(0) } } },
            "theme" => original with { Curriculum = content with { Theme = content.Theme with { Value = content.Theme.Value.Replace("#1d4ed8", "#3730a3", StringComparison.Ordinal) } } },
            "course pin" => CourseChanged(course with { LevelTaxonomySchemaVersion = 2 }),
            "course access" => CourseChanged(course with { ContentAccess = "enrollment_required" }),
            "course state" => CourseChanged(course with { Status = "Draft" }),
            "course translation" => CourseChanged(course with { Translations = [course.Translations[0] with { Title = "Different" }] }),
            "lesson pin" => LessonChanged(lesson with { ContentTypeSchemaVersion = 2 }),
            "lesson sort" => LessonChanged(lesson with { Sort = 99 }),
            "lesson body" => LessonChanged(lesson with { Translations = [lesson.Translations[0] with { Body = "{}" }] }),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var run = () => Runner(source).RunAsync(CancellationToken.None, [changed]);
        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Seed mismatch*");
        (await SnapshotAsync()).Should().Be(before, "a visible mismatch is refused before any writer or overwrite");
        SeedTenant CourseChanged(SeedCourse value) => original with { Curriculum = content with { Courses = content.Courses.SetItem(0, value) } };
        SeedTenant LessonChanged(SeedLesson value) => CourseChanged(course with { Lessons = course.Lessons.SetItem(0, value) });
    }

    [Fact]
    public async Task Semantic_json_property_order_does_not_turn_a_completed_act_into_a_write()
    {
        await using var source = DataSource();
        (await Runner(source).RunAsync(CancellationToken.None)).Should().Be(0);
        var before = await SnapshotAsync();
        var tenant = SeedData.English;
        var content = tenant.Curriculum ?? throw new InvalidOperationException("Missing fixture curriculum.");
        var course = content.Courses[0];
        var lesson = course.Lessons[0];
        static string Reordered(string value)
        {
            var node = JsonNode.Parse(value)?.AsObject() ?? throw new InvalidOperationException("Expected object fixture.");
            return new JsonObject(node.Reverse().Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))).ToJsonString();
        }
        var expected = tenant with
        {
            Curriculum = content with
            {
                Theme = content.Theme with { Value = Reordered(content.Theme.Value) },
                ContentType = content.ContentType with { JsonSchema = Reordered(content.ContentType.JsonSchema) },
                Courses = content.Courses.SetItem(0, course with
                {
                    Lessons = course.Lessons.SetItem(0, lesson with
                    { Translations = [lesson.Translations[0] with { Body = Reordered(lesson.Translations[0].Body) }] })
                }),
            }
        };
        (await Runner(source).RunAsync(CancellationToken.None, [expected])).Should().Be(0);
        (await SnapshotAsync()).Should().Be(before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Seed_publication_refuses_a_different_active_revision_without_retiring_or_rebinding_it(bool taxonomy, bool expectedDraftExists)
    {
        await using var source = DataSource();
        var tenant = SeedData.English with { Curriculum = null };
        var interrupted = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance),
            new InterruptAfter(taxonomy ? (expectedDraftExists ? "level taxonomy" : "content type publication")
                : (expectedDraftExists ? "content type" : "host mapping")));
        var initial = () => interrupted.RunAsync(CancellationToken.None, [tenant]);
        await initial.Should().ThrowAsync<InvalidOperationException>();
        var incumbent = Guid.CreateVersion7();
        var context = new SeedTenantContext(tenant.TenantId, null);
        if (taxonomy)
        {
            var definition = SeedData.Taxonomies(tenant).First();
            await Write(new RegisterTenantLevelTaxonomyCommand(incumbent, definition.Key, definition.SchemaVersion + 1,
                definition.DisplayName, [.. definition.Bands.Select(band => new TaxonomyItemInput(band.Key, band.DisplayName, band.Sort, band.Metadata))]));
            await Write(new PublishTenantLevelTaxonomyCommand(incumbent));
        }
        else
        {
            var definition = SeedData.ContentTypes(tenant).First();
            await Write(new RegisterTenantContentTypeCommand(incumbent, definition.Key, definition.SchemaVersion + 1,
                definition.DisplayName, definition.JsonSchema, definition.RendererKey));
            await Write(new PublishTenantContentTypeCommand(incumbent));
        }
        var before = await SnapshotAsync();
        var audits = await AuditRowsAsync();
        var successes = await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log WHERE outcome = 'success'");
        var rerun = () => Runner(source).RunAsync(CancellationToken.None, [tenant]);
        (await rerun.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Seed mismatch*");
        (await SnapshotAsync()).Should().Be(before,
            "absent/Draft expected state, the other Active revision, versions, generations and all audit rows must remain unchanged");
        (await AuditRowsAsync()).Should().Contain(audits);
        (await ScalarAsPlatformAsync("SELECT count(*) FROM audit_log WHERE outcome = 'success'")).Should().Be(successes);

        async Task Write<T>(IRequest<Result<T>> request)
        {
            await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
            await using var scope = provider.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<ISender>().Send(request)).IsSuccess.Should().BeTrue();
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
        NpgsqlDataSource.Create(_database.AppConnectionString);

    private async Task AssertInventoryAsync(NpgsqlDataSource source)
    {
        (await ScalarAsPlatformAsync("SELECT count(*) FROM courses")).Should().Be(SeedData.Inventory.Courses);
        (await ScalarAsPlatformAsync("SELECT count(*) FROM lessons")).Should().Be(SeedData.Inventory.Lessons);
        (await ScalarAsPlatformAsync("SELECT (SELECT count(*) FROM course_translations) + (SELECT count(*) FROM lesson_translations)"))
            .Should().Be(SeedData.Inventory.Translations);
        foreach (var tenant in SeedData.All)
        {
            var curriculum = tenant.Curriculum ?? throw new InvalidOperationException("Missing fixture curriculum.");
            var wide = new SeedTenantContext(tenant.TenantId, null);
            var tenantRow = (await ReadAsync(source, wide, new GetTenantSeedStateQuery())).State;
            tenantRow.Should().NotBeNull();
            tenantRow!.Locales.Should().BeEquivalentTo(curriculum.Locales);
            var theme = (await ReadAsync(source, wide, new GetSettingSeedStateQuery(curriculum.Theme.Id))).State;
            theme.Should().NotBeNull();
            theme!.OrganizationId.Should().BeNull();
            JsonNode.DeepEquals(JsonNode.Parse(theme.Value), JsonNode.Parse(curriculum.Theme.Value)).Should().BeTrue();
            foreach (var course in curriculum.Courses)
            {
                var context = new SeedTenantContext(tenant.TenantId, course.OrganizationId);
                var actual = (await ReadAsync(source, context, new GetCourseSeedStateQuery(course.Id))).State;
                actual.Should().NotBeNull();
                actual!.TenantId.Should().Be(tenant.TenantId);
                actual.OrganizationId.Should().Be(course.OrganizationId);
                actual.Status.Should().Be(course.Status);
                actual.ContentAccess.Should().Be(course.ContentAccess);
                actual.LevelTaxonomyKey.Should().Be(course.LevelTaxonomyKey);
                actual.LevelTaxonomySchemaVersion.Should().Be(course.LevelTaxonomySchemaVersion);
                actual.LevelBandKey.Should().Be(course.LevelBandKey);
                actual.Translations.Should().BeEquivalentTo(course.Translations);
                var foreign = new SeedTenantContext(SeedData.All.Single(other => other.TenantId != tenant.TenantId).TenantId, null);
                (await ReadAsync(source, foreign, new GetCourseSeedStateQuery(course.Id))).State.Should().BeNull();
                if (course.OrganizationId is not null)
                {
                    var sibling = new SeedTenantContext(tenant.TenantId, course.OrganizationId == tenant.DefaultOrganization.OrganizationId
                        ? tenant.SecondOrganization.OrganizationId : tenant.DefaultOrganization.OrganizationId);
                    (await ReadAsync(source, sibling, new GetCourseSeedStateQuery(course.Id))).State.Should().BeNull();
                    (await ReadAsync(source, wide, new GetCourseSeedStateQuery(course.Id))).State.Should().BeNull();
                }
                foreach (var lesson in course.Lessons)
                {
                    var member = (await ReadAsync(source, context, new GetLessonSeedStateQuery(lesson.Id))).State;
                    member.Should().NotBeNull();
                    member!.CourseId.Should().Be(course.Id);
                    member.TenantId.Should().Be(tenant.TenantId);
                    member.OrganizationId.Should().Be(course.OrganizationId);
                    member.Sort.Should().Be(lesson.Sort);
                    member.Status.Should().Be(lesson.Status);
                    member.ContentTypeKey.Should().Be(lesson.ContentTypeKey);
                    member.ContentTypeSchemaVersion.Should().Be(lesson.ContentTypeSchemaVersion);
                    member.Translations.Should().HaveCount(lesson.Translations.Length);
                    foreach (var expected in lesson.Translations)
                    {
                        var translation = member.Translations.Single(value => value.Locale == expected.Locale);
                        translation.Title.Should().Be(expected.Title);
                        translation.Slug.Should().Be(expected.Slug);
                        JsonNode.DeepEquals(JsonNode.Parse(translation.Body), JsonNode.Parse(expected.Body)).Should().BeTrue();
                    }
                    (await ReadAsync(source, foreign, new GetLessonSeedStateQuery(lesson.Id))).State.Should().BeNull();
                }
            }
        }
    }

    private static async Task<T> ReadAsync<T>(NpgsqlDataSource source, ITenantContext context, IRequest<Result<T>> query)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(query);
        result.IsSuccess.Should().BeTrue();
        return result.Value!; // The successful query contract returns a non-null lookup DTO.
    }

    private static async Task ProvisionForeignAsync(NpgsqlDataSource source)
    {
        await using var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
            TenantId.From(SchemaFixture.TenantA), "foreign-owner", "Foreign owner", OrganizationId.From(SchemaFixture.OrgA1), "main", "Main"));
        result.IsSuccess.Should().BeTrue();
    }

    private async Task<string> SnapshotAsync()
    {
        var output = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var ids = SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray();
        await using var connection = await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        foreach (var table in new[] { "tenants", "organizations", "tenant_locales", "tenant_settings", "platform_host_to_tenant",
            "tenant_content_types", "tenant_level_taxonomies", "tenant_level_taxonomy_items", "customization_generations",
            "courses", "lessons", "course_translations", "lesson_translations", "audit_log" })
        {
            var column = table == "tenants" ? "id" : "tenant_id";
            // Table/column are a closed test-owned list, never external SQL input.
            await using var query = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(row) ORDER BY to_jsonb(row)::text), '[]'::jsonb)::text FROM {table} row WHERE {column} = ANY(@ids)", (NpgsqlConnection)connection);
            query.Parameters.AddWithValue("ids", ids);
            output.Add(table, (string)(await query.ExecuteScalarAsync())!);
        }
        return System.Text.Json.JsonSerializer.Serialize(output);
    }

    private async Task<string[]> AuditRowsAsync()
    {
        var rows = new List<string>();
        await using var connection = await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        await using var query = new NpgsqlCommand("SELECT to_jsonb(row)::text FROM audit_log row ORDER BY id", (NpgsqlConnection)connection);
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return [.. rows];
    }

    private sealed class SeedActRecorder(string act) : ILogger<SeedRunner>
    {
        public bool Completed { get; private set; }
        public bool AlreadyPresent { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id is 7002 or 7003 && state is IEnumerable<KeyValuePair<string, object?>> values
                && values.Any(value => value.Key == "What" && Equals(value.Value, act)))
            {
                Completed = true;
                AlreadyPresent = eventId.Id == 7003;
            }
        }
    }

    private sealed class InterruptAfter(string act) : ILogger<SeedRunner>
    {
        private bool _interrupted;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_interrupted && eventId.Id == 7002 && state is IEnumerable<KeyValuePair<string, object?>> values
                && values.Any(value => value.Key == "What" && Equals(value.Value, act)))
            {
                _interrupted = true;
                throw new InvalidOperationException("Injected seed interruption");
            }
        }
    }

    /// <summary>A scalar with no parameter, for the counts that need none.</summary>
    private Task<long> ScalarAsPlatformAsync(string sql) =>
        ScalarAsPlatformAsync(sql, parameterName: null, value: null);

    /// <summary>A scalar under the platform role, with an optional named parameter.</summary>
    /// <remarks>
    /// The name is passed rather than inferred from the value's type. Inferring it bound
    /// every shape but <c>Guid[]</c> as <c>"tenant"</c>, so a caller adding a third
    /// parameter shape got a silent mis-binding instead of a compile error.
    /// </remarks>
    private async Task<long> ScalarAsPlatformAsync(
        string sql, string? parameterName, object? value)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _database.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);

        if (parameterName is not null)
        {
            query.Parameters.AddWithValue(parameterName, value!);
        }

        return (long)(await query.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsPlatformAsync(string sql, string host)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _database.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);
        query.Parameters.AddWithValue("host", host);

        return (long)(await query.ExecuteScalarAsync())!;
    }

    private async Task<string?> TextAsPlatformAsync(string sql, string host)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _database.PlatformConnectionString);
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
