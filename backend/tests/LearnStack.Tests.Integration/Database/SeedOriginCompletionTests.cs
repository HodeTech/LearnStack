using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual completion, uncertain COMMIT and supported CLI controls under the application role.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedOriginCompletionTests(SchemaFixture schema) : IAsyncLifetime
{
    private const string Adoption = "tenancy.tenant.adopt_seed_origin";
    private const string HostWrite = "tenancy.hostmapping.write";
    private const string RefusedDiagnostic = "seed: administration origin adoption was refused or not acknowledged.";
    private DisposableSchemaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, SeedOriginCompletedFoundationFixture.Registration);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Actual_marker_two_repeat_runs_only_two_narrow_queries_and_no_temporary_scope_SQL()
    {
        await using var setup = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(setup);
        using var observed = new ScopeCommandCounter();
        using var logs = LoggerFactory.Create(builder => builder
            .AddFilter((category, level) => category == "Npgsql.Command" && level == LogLevel.Information)
            .AddProvider(observed));
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString, logs);
        var compositions = 0;
        var runner = new SeedAdministrationOriginRunner(context =>
        {
            Interlocked.Increment(ref compositions);
            return SeedComposition.Build(source, context, NullLoggerFactory.Instance,
                seedOriginAdmission: new FixedSeedAdministrationOriginAdmission());
        });

        (await runner.RunAsync([SeedData.English.TenantId])).Should().Be(1);
        Volatile.Read(ref compositions).Should().Be(4, "fresh adoption has two Tenant reads, one command and one mapping read");
        observed.Enabled.Should().Be(1);
        observed.Cleared.Should().Be(1, "the positive control proves the observer sees both completed scope-only commands");
        var installed = await ReadOriginAsync(SeedData.English);

        observed.Reset();
        Interlocked.Exchange(ref compositions, 0);
        (await runner.RunAsync([SeedData.English.TenantId])).Should().Be(0);
        Volatile.Read(ref compositions).Should().Be(2, "the real runner composes only its ordinary Tenant and mapping queries");
        observed.Enabled.Should().Be(0);
        observed.Cleared.Should().Be(0);
        (await ReadOriginAsync(SeedData.English)).Should().Be(installed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_lost_ack_and_unsent_COMMIT_require_a_later_explicit_runner_invocation(bool dropRequest)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        var before = await ReadOriginAsync(SeedData.English);
        var compositions = 0;
        await using (var proxy = new CommitResponseDroppingProxy(_database.AppConnectionString,
            commitsToPass: 1, dropCommitRequest: dropRequest))
        await using (var uncertain = ApplicationDataSource.Build(proxy.ConnectionString))
        {
            var runner = new SeedAdministrationOriginRunner(context =>
            {
                compositions++;
                return SeedComposition.Build(uncertain, context, NullLoggerFactory.Instance,
                    seedOriginAdmission: new FixedSeedAdministrationOriginAdmission());
            });
            await FluentActions.Awaiting(() => runner.RunAsync([SeedData.English.TenantId])
                    .WaitAsync(TimeSpan.FromSeconds(20)))
                .Should().ThrowAsync<Exception>();
            compositions.Should().Be(2, "the uncertain attempt cannot open another runner observation or retry owner");
            AssertCommitFault(proxy, dropRequest);
        }

        var uncertainState = await ReadOriginAsync(SeedData.English);
        AssertUncertainState(before, uncertainState, dropRequest);
        // This is a new explicit invocation, after the uncertain owner's disposal.
        (await Runner(source).RunAsync([SeedData.English.TenantId])).Should().Be(dropRequest ? 1 : 0);
        var recovered = await ReadOriginAsync(SeedData.English);
        recovered.Marker.Should().Be(2);
        recovered.ExactMapping.Should().BeTrue();
        recovered.Success.Should().Be(2);
        recovered.Indeterminate.Should().Be(2);
        if (!dropRequest) recovered.Should().Be(uncertainState, "the acknowledged historical state is a query-only repeat");
        (await Runner(source).RunAsync([SeedData.English.TenantId])).Should().Be(0);
        (await ReadOriginAsync(SeedData.English)).Should().Be(recovered);
    }

    [Fact]
    public async Task Actual_origin_CLI_and_warm_script_selection_preserve_marker_two_and_all_tenant_rows()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        var fresh = await RunCliAsync(_database.AppConnectionString, origins: true);
        fresh.Code.Should().Be(0);
        fresh.Output.Should().Contain("administration origin adoption completed (1 tenants changed)");
        var installed = await ReadOriginAsync(SeedData.English);
        installed.Marker.Should().Be(2);
        installed.ExactMapping.Should().BeTrue();
        installed.Success.Should().Be(2);
        var snapshot = await SnapshotRevisionsAsync();

        var repeat = await RunCliAsync(_database.AppConnectionString, origins: true);
        repeat.Code.Should().Be(0);
        repeat.Output.Should().Contain("administration origin adoption completed (0 tenants changed)");
        (await SnapshotRevisionsAsync()).Should().Be(snapshot);

        // scripts/seed.mjs invokes this ordinary content stage before its fixed
        // origin runner. Marker 2 must reach the completed-content preservation path.
        var warm = await RunCliAsync(_database.AppConnectionString, origins: false);
        warm.Code.Should().Be(0);
        warm.Output.Should().Contain("seed-result:");
        var selected = await ScriptSelectionAsync(warm.Output);
        using (var selection = JsonDocument.Parse(selected))
            selection.RootElement.GetProperty("tenantIds").EnumerateArray().Select(value => value.GetString())
                .Should().Equal(SeedData.English.TenantId.Value.ToString("D"));
        var selectedRepeat = await RunCliAsync(_database.AppConnectionString, origins: true, selection: selected);
        selectedRepeat.Code.Should().Be(0);
        selectedRepeat.Output.Should().Contain("(0 tenants changed)");
        (await SnapshotRevisionsAsync()).Should().Be(snapshot);
        (await ReadOriginAsync(SeedData.English)).Should().Be(installed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Warm_script_selection_routes_marker_two_with_missing_or_disabled_management_mapping_to_refusal(bool disable)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        (await Runner(source).RunAsync([SeedData.English.TenantId])).Should().Be(1);
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, false);
            var sql = disable
                ? "UPDATE public.platform_host_to_tenant SET is_active=false,is_publicly_live=false WHERE host=@host"
                : "DELETE FROM public.platform_host_to_tenant WHERE host=@host";
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, sql,
                ("host", SeedAdministrationOriginManifest.Find(SeedData.English.TenantId)!.Host));
            await transaction.CommitAsync();
        }
        var before = await ReadOriginAsync(SeedData.English);
        before.Marker.Should().Be(2);
        before.ExactMapping.Should().BeFalse();
        var snapshot = await SnapshotRevisionsAsync();
        var warm = await RunCliAsync(_database.AppConnectionString, origins: false);
        warm.Code.Should().Be(0, "warm content validation preserves topology and emits the retained qualified marker");
        var selected = await ScriptSelectionAsync(warm.Output);
        using (var selection = JsonDocument.Parse(selected))
            selection.RootElement.GetProperty("tenantIds").EnumerateArray().Select(value => value.GetString())
                .Should().Equal(SeedData.English.TenantId.Value.ToString("D"));
        var refused = await RunCliAsync(_database.AppConnectionString, origins: true, selection: selected);
        refused.Code.Should().Be(1);
        refused.Errors.Trim().Should().Be(RefusedDiagnostic);
        (await ReadOriginAsync(SeedData.English)).Should().Be(before,
            "marker-2 routing verifies and refuses before mutation dispatch or another audit intent");
        (await SnapshotRevisionsAsync()).Should().Be(snapshot);
    }

    [Fact]
    public async Task Actual_origin_CLI_refuses_provisioning_only_with_fixed_diagnostic_and_no_successful_topology_act()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        var tenant = SeedData.English;
        await using (var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance))
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
                tenant.TenantId, tenant.Slug, tenant.DisplayName, tenant.DefaultOrganization.OrganizationId,
                tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName));
            result.IsSuccess.Should().BeTrue();
        }
        var before = await ReadOriginAsync(tenant);
        var refused = await RunCliAsync(_database.AppConnectionString, origins: true);
        refused.Code.Should().Be(1);
        refused.Errors.Trim().Should().Be(RefusedDiagnostic);
        var after = await ReadOriginAsync(tenant);
        after.Marker.Should().Be(0);
        after.Version.Should().Be(before.Version);
        after.TenantRevision.Should().Be(before.TenantRevision);
        after.MappingRevision.Should().BeNull();
        after.Success.Should().Be(0);
        after.Failed.Should().Be(2, "direct command refusal retains its normal plural audit evidence");
        await AssertRefusedAuditPairAsync(tenant);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_origin_CLI_exits_one_on_unknown_COMMIT_and_only_a_new_process_converges(bool dropRequest)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        var before = await ReadOriginAsync(SeedData.English);
        // Pass the authority and auxiliary qualification COMMITs plus the first
        // narrow Tenant read. The startup database proof disposes by ROLLBACK.
        await using (var proxy = new CommitResponseDroppingProxy(_database.AppConnectionString,
            commitsToPass: 3, dropCommitRequest: dropRequest, lifetime: TimeSpan.FromSeconds(90)))
        {
            var uncertain = await RunCliAsync(proxy.ConnectionString, origins: true);
            uncertain.Code.Should().Be(1);
            uncertain.Errors.Trim().Should().Be(RefusedDiagnostic);
            AssertCommitFault(proxy, dropRequest);
        }
        var uncertainState = await ReadOriginAsync(SeedData.English);
        AssertUncertainState(before, uncertainState, dropRequest);

        var deliberate = await RunCliAsync(_database.AppConnectionString, origins: true);
        deliberate.Code.Should().Be(0);
        deliberate.Output.Should().Contain(dropRequest ? "(1 tenants changed)" : "(0 tenants changed)");
        var recovered = await ReadOriginAsync(SeedData.English);
        recovered.Marker.Should().Be(2);
        recovered.ExactMapping.Should().BeTrue();
        recovered.Success.Should().Be(2);
        recovered.Indeterminate.Should().Be(2);
        if (!dropRequest) recovered.Should().Be(uncertainState);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Revoked")]
    public async Task The_last_branch_Membership_requires_positive_version_but_a_valid_lifecycle_edit_can_supply_it(string status)
    {
        var member = SeedData.IdentityMemberships.Last();
        member.OrganizationId.Should().NotBeNull();
        member.IsFirstAdmin.Should().BeFalse();
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await StopBeforeFinalProfileAsync(source, member);
        var before = await ReadOriginAsync(member.Identity.Tenant);
        (await ReadMemberVersionAsync(member)).Should().Be(0);
        (await AdoptAsync(source, member.Identity.Tenant, before.Version)).IsFailure.Should().BeTrue();
        var refused = await ReadOriginAsync(member.Identity.Tenant);
        refused.Marker.Should().Be(0);
        refused.TenantRevision.Should().Be(before.TenantRevision);
        refused.MappingRevision.Should().BeNull();
        refused.Success.Should().Be(0);
        refused.Failed.Should().Be(2);
        await AssertRefusedAuditPairAsync(member.Identity.Tenant);

        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value,
                member.OrganizationId!.Value.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, member.Identity.Tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status=@status,row_version=row_version+1,
                    updated_at=statement_timestamp(),updated_by=@actor,
                    deleted_at=CASE WHEN @status='Revoked' THEN statement_timestamp() ELSE NULL END,
                    deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END WHERE id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("status", status), ("actor", UserId.SystemActor.Value), ("member", member.MembershipId));
            await transaction.CommitAsync();
        }
        (await ReadMemberVersionAsync(member)).Should().Be(1);
        // This proves retained current state, not that the seed profile initializer ran.
        (await AdoptAsync(source, member.Identity.Tenant, before.Version)).IsSuccess.Should().BeTrue();
        var completed = await ReadOriginAsync(member.Identity.Tenant);
        completed.Marker.Should().Be(2);
        completed.ExactMapping.Should().BeTrue();
        completed.Success.Should().Be(2);
    }

    [Fact]
    public async Task A_conflicting_immutable_content_pin_in_another_organization_refuses_without_origin_mutation()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallAsync(source);
        var tenant = SeedData.Yoga;
        var course = tenant.Curriculum!.Courses.Single(value => value.OrganizationId == tenant.SecondOrganization.OrganizationId);
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, course.OrganizationId!.Value.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, null, false);
            // A test-owned contradictory pin, outside the admitted content writer
            // population; the completion reader must still refuse its actual row.
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.courses SET slug_key='conflicting-origin-proof-pin',row_version=row_version+1,
                    updated_at=clock_timestamp(),updated_by=@actor WHERE id=@course
                """, ("actor", UserId.SystemActor.Value), ("course", course.Id));
            await transaction.CommitAsync();
        }
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.courses WHERE id=@course", ("course", course.Id))).Should().Be(0,
                "the orgless ordinary owner cannot establish this branch pin");
        }
        var before = await ReadOriginAsync(tenant);
        (await AdoptAsync(source, tenant, before.Version)).IsFailure.Should().BeTrue();
        var after = await ReadOriginAsync(tenant);
        after.Marker.Should().Be(before.Marker);
        after.Version.Should().Be(before.Version);
        after.TenantRevision.Should().Be(before.TenantRevision);
        after.MappingRevision.Should().BeNull();
        after.Success.Should().Be(0);
        after.Failed.Should().Be(2);
        await AssertRefusedAuditPairAsync(tenant);
    }

    private static SeedAdministrationOriginRunner Runner(NpgsqlDataSource source) => new(context =>
        SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));

    private static async Task<Result<long>> AdoptAsync(NpgsqlDataSource source, SeedTenant tenant, long version)
    {
        await using var provider = SeedComposition.Build(source, new SeedTenantContext(tenant.TenantId, null),
            NullLoggerFactory.Instance, seedOriginAdmission: new FixedSeedAdministrationOriginAdmission());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AdoptSeedAdministrationOriginCommand(version));
    }

    private static void AssertCommitFault(CommitResponseDroppingProxy proxy, bool dropRequest)
    {
        proxy.DroppedCommitRequests.Should().Be(dropRequest ? 1 : 0);
        proxy.DroppedCommitResponses.Should().Be(dropRequest ? 0 : 1);
    }

    private static void AssertUncertainState(OriginState before, OriginState observed, bool dropRequest)
    {
        observed.Marker.Should().Be(dropRequest ? 0 : 2);
        observed.Version.Should().Be(before.Version + (dropRequest ? 0 : 1));
        observed.ExactMapping.Should().Be(!dropRequest);
        observed.Success.Should().Be(dropRequest ? 0 : 2);
        observed.Indeterminate.Should().Be(2, "both original MUST intents retain central unknown-COMMIT evidence");
        observed.Failed.Should().Be(0);
        if (dropRequest)
        {
            observed.TenantRevision.Should().Be(before.TenantRevision);
            observed.MappingRevision.Should().BeNull();
        }
    }

    private async Task<OriginState> ReadOriginAsync(SeedTenant tenant)
    {
        var host = SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host;
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
        await using var command = new NpgsqlCommand("""
            SELECT tenant.adopted_seed_origin_version,tenant.row_version,tenant.xmin::text,
                (SELECT mapping.xmin::text FROM public.platform_host_to_tenant AS mapping WHERE mapping.host=@host),
                EXISTS(SELECT 1 FROM public.platform_host_to_tenant AS mapping WHERE mapping.host=@host
                    AND mapping.tenant_id=tenant.id AND mapping.organization_id IS NULL AND mapping.is_active AND mapping.is_publicly_live),
                (SELECT count(*) FROM public.audit_log WHERE (operation=@adoption OR operation=@host_write AND entity_id=@host)
                    AND outcome='success'),
                (SELECT count(*) FROM public.audit_log WHERE (operation=@adoption OR operation=@host_write AND entity_id=@host)
                    AND outcome='indeterminate'),
                (SELECT count(*) FROM public.audit_log WHERE operation_class='Must' AND outcome='failed'
                    AND ((operation=@adoption AND entity_type='Tenant' AND entity_id=tenant.id::text)
                        OR (operation=@host_write AND entity_type='PlatformHostMapping'
                            AND (entity_id=@host OR entity_id IS NULL))))
            FROM public.tenants AS tenant WHERE tenant.id=@tenant
            """, app, transaction);
        command.Parameters.AddWithValue("host", host);
        command.Parameters.AddWithValue("adoption", Adoption);
        command.Parameters.AddWithValue("host_write", HostWrite);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new(reader.GetInt32(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private async Task AssertRefusedAuditPairAsync(SeedTenant tenant)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
        await using var command = new NpgsqlCommand("""
            SELECT operation,operation_class,entity_type,entity_id,outcome
            FROM public.audit_log WHERE operation IN (@adoption,@host_write) AND outcome IN ('failed','denied')
            ORDER BY operation
            """, app, transaction);
        command.Parameters.AddWithValue("adoption", Adoption);
        command.Parameters.AddWithValue("host_write", HostWrite);
        var rows = new List<(string Operation, string Class, string Type, string? Id, string Outcome)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        // Completion refuses before DesignateProjection. Its typed MUST intent
        // still reconciles, but has no subject ID or captured mapping mutation.
        rows.Should().Equal(
            (HostWrite, "Must", "PlatformHostMapping", (string?)null, "failed"),
            (Adoption, "Must", "Tenant", tenant.TenantId.Value.ToString("D"), "failed"));
    }

    private async Task<string> SnapshotRevisionsAsync()
    {
        var snapshot = new StringBuilder();
        await using var app = await OpenAppAsync();
        foreach (var tenant in SeedData.All)
            foreach (var organization in new OrganizationId?[] { null, tenant.DefaultOrganization.OrganizationId, tenant.SecondOrganization.OrganizationId })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, organization?.Value);
                foreach (var table in new[] { "tenants", "organizations", "tenant_locales", "tenant_settings", "platform_host_to_tenant",
                "tenant_content_types", "tenant_level_taxonomies", "tenant_level_taxonomy_items", "customization_generations",
                "courses", "lessons", "course_translations", "lesson_translations", "memberships", "membership_profiles",
                "membership_role_assignments", "roles", "role_permission_grants", "audit_log" })
                {
                    // Closed test-owned names; xmin detects updates as well as row insertion/removal.
                    await using var command = new NpgsqlCommand(
                        $"SELECT COALESCE(string_agg(xmin::text, ',' ORDER BY xmin::text),'') FROM public.{table}", app, transaction);
                    snapshot.Append(tenant.TenantId).Append('/').Append(organization).Append('/').Append(table)
                        .Append(':').Append((string)(await command.ExecuteScalarAsync())!).AppendLine();
                }
            }
        return snapshot.ToString();
    }

    private async Task StopBeforeFinalProfileAsync(NpgsqlDataSource source, SeedFoundationMembership member)
    {
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_stop_origin_last_profile() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='identity.membership_profile.set_seed_profile' AND NEW.outcome='success'
                    AND NEW.entity_id='{{member.MembershipId:D}}' THEN
                    RAISE EXCEPTION 'origin completion profile fixture interruption';
                END IF;
                RETURN NEW;
            END $proof$;
            CREATE TRIGGER proof_stop_origin_last_profile BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_stop_origin_last_profile();
            """);
        try
        {
            await FluentActions.Awaiting(() => SeedOriginCompletedFoundationFixture.InstallAsync(source))
                .Should().ThrowAsync<Exception>();
        }
        finally
        {
            await DeploymentAsync("""
                DROP TRIGGER proof_stop_origin_last_profile ON public.audit_log;
                DROP FUNCTION public.proof_stop_origin_last_profile();
                """);
        }
    }

    private async Task<long> ReadMemberVersionAsync(SeedFoundationMembership member)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value, member.OrganizationId?.Value);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT row_version FROM public.memberships WHERE id=@member", ("member", member.MembershipId));
    }

    private async Task DeploymentAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
    }

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        try
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<CliResult> RunCliAsync(string connection, bool origins, string? selection = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.deps.json"));
        start.ArgumentList.Add(typeof(SeedRunner).Assembly.Location);
        if (origins) start.ArgumentList.Add("--seed-admin-origins-stdin");
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "HOME", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // Credentials stay in environment only; neither argv nor assertion diagnostics carry them.
        start.Environment["ConnectionStrings__Default"] = connection;
        start.Environment["Identity__AuthorityId"] = SeedOriginCompletedFoundationFixture.Registration.AuthorityId.Value.ToString("D");
        start.Environment["Authentication__Tenant__Issuer"] = SeedOriginCompletedFoundationFixture.Registration.Issuer;
        start.Environment["Deployment__Mode"] = "Development";
        start.Environment.Remove("ConnectionStrings__IdentityAuthorization");
        start.Environment.Remove("ConnectionStrings__AuthorizationEvidence");
        var result = await RunChildAsync(start, origins
            ? selection ?? JsonSerializer.Serialize(new
            {
                version = 1,
                tenantIds = new[] { SeedData.English.TenantId.Value.ToString("D") },
            }) : null);
        var password = new NpgsqlConnectionStringBuilder(connection).Password;
        // Boolean assertions never print the credentials even if the child's output is defective.
        (result.Output.Contains(connection, StringComparison.Ordinal) || result.Errors.Contains(connection, StringComparison.Ordinal))
            .Should().BeFalse("the CLI must not emit its connection configuration");
        if (!string.IsNullOrEmpty(password))
            (result.Output.Contains(password, StringComparison.Ordinal) || result.Errors.Contains(password, StringComparison.Ordinal))
                .Should().BeFalse("the CLI must not emit its database credential");
        return result;
    }

    private static async Task<string> ScriptSelectionAsync(string output)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "seed-admin-origins.mjs")))
            repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("The origin selection fixture requires its repository scripts.");
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = repository.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add("""
            import { seedAdministrationOriginTenantIds } from './scripts/seed-admin-origins.mjs';
            let input = '';
            for await (const chunk of process.stdin) input += chunk;
            process.stdout.write(JSON.stringify({ version: 1, tenantIds: seedAdministrationOriginTenantIds(input) }));
            """);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "HOME", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        var selected = await RunChildAsync(start, output);
        selected.Code.Should().Be(0);
        return selected.Output;
    }

    private static async Task<CliResult> RunChildAsync(ProcessStartInfo start, string? input)
    {
        using var child = new Process { StartInfo = start };
        child.Start().Should().BeTrue();
        var output = DrainAsync(child.StandardOutput);
        var errors = DrainAsync(child.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (input is not null) await child.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(output, errors);
        }
        return new(child.ExitCode, await output, await errors);
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        const int capacity = 65_536;
        var captured = new StringBuilder();
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer)) != 0)
            if (captured.Length < capacity) captured.Append(buffer, 0, Math.Min(length, capacity - captured.Length));
        return captured.ToString();
    }

    private sealed record OriginState(int Marker, long Version, string TenantRevision, string? MappingRevision,
        bool ExactMapping, long Success, long Indeterminate, long Failed);
    private sealed record CliResult(int Code, string Output, string Errors);

    /// <summary>Consumes structured command text immediately; retains only two counts, never SQL or parameters.</summary>
    private sealed class ScopeCommandCounter : ILoggerProvider
    {
        private int _enabled;
        private int _cleared;
        public int Enabled => Volatile.Read(ref _enabled);
        public int Cleared => Volatile.Read(ref _cleared);

        public ILogger CreateLogger(string categoryName) => new CounterLogger(this, categoryName == "Npgsql.Command");
        public void Dispose() { }
        public void Reset()
        {
            Interlocked.Exchange(ref _enabled, 0);
            Interlocked.Exchange(ref _cleared, 0);
        }

        private sealed class CounterLogger(ScopeCommandCounter owner, bool isCommand) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => isCommand && logLevel == LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // Npgsql's completed-command event is Information; its executing
                // event is Debug. The factory keeps parameter logging disabled.
                if (!IsEnabled(logLevel) || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                foreach (var field in fields)
                {
                    if (field.Key != "CommandText" || field.Value is not string command) continue;
                    if (string.Equals(command, "SELECT set_config('app.scope', 'tenant', true)", StringComparison.Ordinal))
                        Interlocked.Increment(ref owner._enabled);
                    else if (string.Equals(command, "SELECT set_config('app.scope', '', true)", StringComparison.Ordinal))
                        Interlocked.Increment(ref owner._cleared);
                }
            }
        }
    }
}
