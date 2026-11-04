using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Fixed seed origin adoption through the production app-role pipeline and invoker guards.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedAdministrationOriginTests(SchemaFixture schema) : IAsyncLifetime
{
    private const string Adoption = "tenancy.tenant.adopt_seed_origin";
    private const string HostWrite = "tenancy.hostmapping.write";
    private const string EnglishHost = "manage.demo-english.learnstack.local";
    private const string YogaHost = "manage.demo-yoga.learnstack.local";
    private DisposableSchemaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, SeedOriginCompletedFoundationFixture.Registration);
    }
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Origin_adoption_refuses_a_retained_Tenant_without_a_completed_v1_installation()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        var tenant = SeedData.English;
        await using (var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance))
        await using (var scope = provider.CreateAsyncScope())
        {
            var provisioned = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProvisionTenantCommand(
                tenant.TenantId, tenant.Slug, tenant.DisplayName, tenant.DefaultOrganization.OrganizationId,
                tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName));
            provisioned.IsSuccess.Should().BeTrue();
        }
        var before = await StateAsync(tenant);
        before.Marker.Should().Be(0);
        before.Version.Should().Be(1);
        (await AdoptAsync(source, tenant, before.Version)).IsFailure.Should().BeTrue(
            "a provisioned Tenant/default Organization is not the completed v1 foundation required by ADR-0079");
        (await StateAsync(tenant)).Should().Be(before);
        (await MappingRevisionAsync(tenant, EnglishHost)).Should().BeNull();
        (await SuccessCountAsync(tenant, Adoption)).Should().Be(0);
        (await RefusalCountAsync(tenant)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Completed_content_without_Identity_is_not_a_completed_foundation()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedOriginCompletedFoundationFixture.InstallContentAsync(source);
        var before = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, before.Version)).IsFailure.Should().BeTrue();
        (await StateAsync(SeedData.English)).Should().Be(before);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().BeNull();
        (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(0);
        (await RefusalCountAsync(SeedData.English)).Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_forward_migration_preserves_old_marker_one_but_only_completed_foundations_upgrade(bool complete)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        if (complete) await LegacyAsync(source);
        else await SeedOriginCompletedFoundationFixture.InstallContentAsync(source);
        await InstallLegacyMarkerAsync(SeedData.English);
        var before = await StateAsync(SeedData.English);
        before.Marker.Should().Be(1);
        var mapping = await MappingRevisionAsync(SeedData.English, EnglishHost);

        (await AdoptAsync(source, SeedData.English, before.Version)).IsSuccess.Should().Be(complete);
        (await StateAsync(SeedData.English)).Marker.Should().Be(complete ? 2 : 1);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().Be(mapping);
        if (complete) await AssertAuditPairAsync(SeedData.English, EnglishHost);
        else
        {
            (await StateAsync(SeedData.English)).Should().Be(before);
            (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(0);
            (await RefusalCountAsync(SeedData.English)).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Old_marker_one_with_a_missing_projection_refuses_without_repair()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        await InstallLegacyMarkerAsync(SeedData.English);
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SeedData.English);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction,
                "DELETE FROM public.platform_host_to_tenant WHERE host=@host", ("host", EnglishHost));
            await transaction.CommitAsync();
        }
        var before = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, before.Version)).IsFailure.Should().BeTrue();
        (await StateAsync(SeedData.English)).Should().Be(before);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().BeNull();
        (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(0);
    }

    [Fact]
    public async Task A_completed_legacy_foundation_adopts_both_fixed_NULL_org_origins_with_two_central_MUST_rows_each()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var before = await StateAsync(SeedData.English);
        before.Marker.Should().Be(0);
        (await AdoptAsync(source, SeedData.English, before.Version)).IsSuccess.Should().BeTrue();
        var after = await StateAsync(SeedData.English);
        after.Marker.Should().Be(2);
        after.Version.Should().Be(before.Version + 1);
        await AssertMappingAsync(SeedData.English, EnglishHost);
        await AssertAuditPairAsync(SeedData.English, EnglishHost);

        var second = await StateAsync(SeedData.Yoga);
        (await AdoptAsync(source, SeedData.Yoga, second.Version)).IsSuccess.Should().BeTrue();
        await AssertMappingAsync(SeedData.Yoga, YogaHost);
        await AssertAuditPairAsync(SeedData.Yoga, YogaHost);
    }

    [Fact]
    public async Task Marker_zero_with_the_exact_existing_live_projection_adopts_without_reinserting_or_fabricating_a_change()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        await InsertMappingAsync(SeedData.English, EnglishHost);
        var before = await StateAsync(SeedData.English);
        var mappingRevision = await MappingRevisionAsync(SeedData.English, EnglishHost);

        (await AdoptAsync(source, SeedData.English, before.Version)).IsSuccess.Should().BeTrue();
        (await StateAsync(SeedData.English)).Marker.Should().Be(2);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().Be(mappingRevision,
            "locked verification does not UPDATE or reinsert the existing projection");
        await AssertAuditPairAsync(SeedData.English, EnglishHost);
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SeedData.English);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log
            WHERE operation=@operation AND entity_id=@host AND outcome='success'
                AND before_state IS NULL AND after_state IS NULL
            """, ("operation", HostWrite), ("host", EnglishHost))).Should().Be(1L);
    }

    [Fact]
    public async Task A_two_Tenant_interruption_preserves_the_first_commit_and_resumes_only_the_unadopted_Tenant()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var first = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, first.Version)).IsSuccess.Should().BeTrue();
        var committed = await StateAsync(SeedData.English);
        (await StateAsync(SeedData.Yoga)).Marker.Should().Be(0);

        // A new process/data source resumes after the first Tenant's independent COMMIT.
        await using var restarted = ApplicationDataSource.Build(_database.AppConnectionString);
        var second = await StateAsync(SeedData.Yoga);
        (await AdoptAsync(restarted, SeedData.Yoga, second.Version)).IsSuccess.Should().BeTrue();
        (await StateAsync(SeedData.English)).Should().Be(committed);
        await AssertAuditPairAsync(SeedData.English, EnglishHost);
        await AssertAuditPairAsync(SeedData.Yoga, YogaHost);
    }

    [Fact]
    public async Task The_fixed_upgrade_runner_resumes_a_partial_upgrade_and_repeats_without_any_audit_or_state_write()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var initial = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, initial.Version)).IsSuccess.Should().BeTrue();
        var english = await StateAsync(SeedData.English);
        var englishMapping = await MappingRevisionAsync(SeedData.English, EnglishHost);
        var runner = new SeedAdministrationOriginRunner(context => SeedComposition.Build(source, context,
            NullLoggerFactory.Instance, seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));

        (await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId))).Should().Be(1);
        (await StateAsync(SeedData.English)).Should().Be(english);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().Be(englishMapping);
        var yoga = await StateAsync(SeedData.Yoga);
        var yogaMapping = await MappingRevisionAsync(SeedData.Yoga, YogaHost);
        var before = await TenantAuditCountAsync(SeedData.English) + await TenantAuditCountAsync(SeedData.Yoga);

        (await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId))).Should().Be(0);
        // Ordinary warm content verification must admit retained marker 2 before
        // its completed-content check and preserve the installed foundation.
        await SeedOriginCompletedFoundationFixture.InstallContentAsync(source);
        (await StateAsync(SeedData.English)).Should().Be(english);
        (await StateAsync(SeedData.Yoga)).Should().Be(yoga);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().Be(englishMapping);
        (await MappingRevisionAsync(SeedData.Yoga, YogaHost)).Should().Be(yogaMapping);
        (await TenantAuditCountAsync(SeedData.English) + await TenantAuditCountAsync(SeedData.Yoga)).Should().Be(before);
        await AssertAuditPairAsync(SeedData.English, EnglishHost);
        await AssertAuditPairAsync(SeedData.Yoga, YogaHost);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("organization")]
    [InlineData("foreign")]
    public async Task A_disabled_scoped_or_foreign_existing_host_refuses_without_overwrite_or_scope_disclosure(string conflict)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var holder = conflict == "foreign" ? SeedData.Yoga : SeedData.English;
        await InsertMappingAsync(holder, EnglishHost,
            organization: conflict == "organization" ? holder.DefaultOrganization.OrganizationId.Value : null,
            live: conflict != "disabled");
        var before = await StateAsync(SeedData.English);
        var mappingRevision = await MappingRevisionAsync(holder, EnglishHost);

        var result = await AdoptAsync(source, SeedData.English, before.Version);
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("business_rule_violation");
        result.Error.Details.Should().BeNull("the refusal exposes no holder or alternate scope");
        (await StateAsync(SeedData.English)).Should().Be(before);
        (await MappingRevisionAsync(holder, EnglishHost)).Should().Be(mappingRevision);
        (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(0);
    }

    [Fact]
    public async Task Marker_two_with_a_missing_mapping_refuses_and_never_restores_it()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var initial = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, initial.Version)).IsSuccess.Should().BeTrue();
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SeedData.English);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction,
                "DELETE FROM public.platform_host_to_tenant WHERE host=@host", ("host", EnglishHost));
            await transaction.CommitAsync();
        }
        var before = await StateAsync(SeedData.English);
        (await AdoptAsync(source, SeedData.English, before.Version)).IsFailure.Should().BeTrue();
        (await StateAsync(SeedData.English)).Should().Be(before);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().BeNull();
        (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(1);
    }

    [Theory]
    [InlineData(Adoption)]
    [InlineData(HostWrite)]
    public async Task Either_central_MUST_failure_rolls_back_the_mapping_and_Tenant_marker_together(string operation)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        var before = await StateAsync(SeedData.English);
        // Deployment-only fault injection; the act and the observations authenticate as learnstack_app.
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_reject_origin_audit() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='{{operation}}' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed origin audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $proof$;
            CREATE TRIGGER proof_reject_origin_audit BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_reject_origin_audit();
            """);
        var act = async () => await AdoptAsync(source, SeedData.English, before.Version);
        await act.Should().ThrowAsync<Exception>();
        (await StateAsync(SeedData.English)).Should().Be(before);
        (await MappingRevisionAsync(SeedData.English, EnglishHost)).Should().BeNull();
        (await SuccessCountAsync(SeedData.English, Adoption)).Should().Be(0);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("unknown")]
    [InlineData("legacy_write")]
    [InlineData("missing_projection")]
    [InlineData("missing_fence")]
    [InlineData("no_version_advance")]
    [InlineData("branch_scope")]
    public async Task Direct_application_marker_updates_cannot_reset_or_bypass_the_actual_projection_and_owner_proof(string invalid)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        if (invalid == "reset")
        {
            var state = await StateAsync(SeedData.English);
            (await AdoptAsync(source, SeedData.English, state.Version)).IsSuccess.Should().BeTrue();
        }
        else if (invalid != "missing_projection") await InsertMappingAsync(SeedData.English, EnglishHost);
        var before = await StateAsync(SeedData.English);
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value,
            invalid == "branch_scope" ? SeedData.English.DefaultOrganization.OrganizationId.Value : null);
        if (invalid != "missing_fence")
            await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, true);
        var attempt = async () => await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET adopted_seed_origin_version=@marker,
                row_version=row_version+@advance,updated_at=clock_timestamp(),updated_by=@actor
            WHERE id=@tenant
            """, ("marker", invalid == "reset" ? 0 : invalid == "unknown" ? 3 : invalid == "legacy_write" ? 1 : 2),
            ("advance", invalid == "no_version_advance" ? 0 : 1),
            ("actor", UserId.SystemActor.Value), ("tenant", SeedData.English.TenantId.Value));
        (await attempt.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await transaction.RollbackAsync();
        (await StateAsync(SeedData.English)).Should().Be(before);
    }

    [Fact]
    public async Task Direct_application_INSERT_cannot_begin_with_a_nonzero_marker()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SeedData.English);
        var attempt = async () => await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,adopted_seed_origin_version)
            VALUES(@tenant,'demo-english','Proof','Trial',clock_timestamp(),@actor,1)
            """, ("tenant", SeedData.English.TenantId.Value), ("actor", UserId.SystemActor.Value));
        (await attempt.Should().ThrowAsync<PostgresException>()).Which.ConstraintName
            .Should().Be("ck_tenants_adopted_seed_origin_version");
    }

    [Fact]
    public async Task A_marker_transition_followed_by_same_transaction_projection_removal_cannot_commit()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await LegacyAsync(source);
        await InsertMappingAsync(SeedData.English, EnglishHost);
        var before = await StateAsync(SeedData.English);
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SeedData.English);
        await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, true);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET adopted_seed_origin_version=2,row_version=row_version+1,
                updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant;
            DELETE FROM public.platform_host_to_tenant WHERE host=@host;
            """, ("actor", UserId.SystemActor.Value), ("tenant", SeedData.English.TenantId.Value), ("host", EnglishHost));
        var complete = async () => await transaction.CommitAsync();
        (await complete.Should().ThrowAsync<PostgresException>()).Which.ConstraintName
            .Should().Be("ck_tenants_adopted_seed_origin_version");
        (await StateAsync(SeedData.English)).Should().Be(before);
        await AssertMappingAsync(SeedData.English, EnglishHost);
    }

    private static Task LegacyAsync(NpgsqlDataSource source) =>
        SeedOriginCompletedFoundationFixture.InstallAsync(source);

    private static async Task<Result<long>> AdoptAsync(NpgsqlDataSource source, SeedTenant tenant, long version)
    {
        await using var provider = SeedComposition.Build(source, new SeedTenantContext(tenant.TenantId, null),
            NullLoggerFactory.Instance, seedOriginAdmission: new FixedAdmission());
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AdoptSeedAdministrationOriginCommand(version));
    }

    private async Task<NpgsqlConnection> OpenAppAsync() =>
        (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);

    private static Task ScopeAsync(NpgsqlConnection app, NpgsqlTransaction transaction, SeedTenant tenant) =>
        IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);

    private async Task<TenantState> StateAsync(SeedTenant tenant)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await using var command = new NpgsqlCommand(
            "SELECT row_version,adopted_seed_origin_version,xmin::text FROM public.tenants WHERE id=@tenant", app, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2));
    }

    private async Task InsertMappingAsync(SeedTenant tenant, string host, Guid? organization = null, bool live = true)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
            VALUES(@host,@tenant,@organization,@live,@live)
            """, ("host", host), ("tenant", tenant.TenantId.Value), ("organization", organization), ("live", live));
        await transaction.CommitAsync();
    }

    private async Task<string?> MappingRevisionAsync(SeedTenant tenant, string host)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await using var command = new NpgsqlCommand("SELECT xmin::text FROM public.platform_host_to_tenant WHERE host=@host", app, transaction);
        command.Parameters.AddWithValue("host", host);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task AssertMappingAsync(SeedTenant tenant, string host)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.platform_host_to_tenant WHERE host=@host AND tenant_id=@tenant
                AND organization_id IS NULL AND is_active AND is_publicly_live
            """, ("host", host), ("tenant", tenant.TenantId.Value))).Should().Be(1L);
    }

    private async Task AssertAuditPairAsync(SeedTenant tenant, string host)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log WHERE outcome='success' AND operation_class='Must'
                AND organization_id IS NULL AND actor_user_id IS NULL
                AND ((operation=@adoption AND entity_type='Tenant' AND entity_id=@tenant)
                    OR (operation=@host_write AND entity_type='PlatformHostMapping' AND entity_id=@host))
            """, ("adoption", Adoption), ("tenant", tenant.TenantId.Value.ToString("D")),
            ("host_write", HostWrite), ("host", host))).Should().Be(2L);
    }

    private async Task<long> SuccessCountAsync(SeedTenant tenant, string operation)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND outcome='success'", ("operation", operation));
    }

    private async Task<long> RefusalCountAsync(SeedTenant tenant)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND outcome IN ('failed','denied')",
            ("operation", Adoption));
    }

    private async Task<long> TenantAuditCountAsync(SeedTenant tenant)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.audit_log");
    }

    private async Task DeploymentAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
    }

    private async Task InstallLegacyMarkerAsync(SeedTenant tenant)
    {
        // Restore the original production guard, perform its actual app-role 0→1
        // transition, then apply the new forward guard without altering history.
        await DeploymentAsync("""
            DROP TRIGGER tg_tenants_seed_administration_origin_guard ON public.tenants;
            DROP TRIGGER tg_tenants_seed_administration_origin_complete ON public.tenants;
            DROP FUNCTION public.fn_tenancy_seed_administration_origin_guard();
            ALTER TABLE public.tenants DROP CONSTRAINT ck_tenants_adopted_seed_origin_version;
            """);
        var legacy = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        SeedAdministrationOriginDdl.Install(legacy);
        foreach (var statement in legacy.Operations.Cast<SqlOperation>()) await DeploymentAsync(statement.Sql);
        var host = SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host;
        await InsertMappingAsync(tenant, host);
        await using (var app = await OpenAppAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, null, true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.tenants SET adopted_seed_origin_version=1,row_version=row_version+1,
                    updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant
                """, ("actor", UserId.SystemActor.Value), ("tenant", tenant.TenantId.Value));
            await transaction.CommitAsync();
        }
        var qualified = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        SeedAdministrationOriginDdl.InstallQualifiedCompletion(qualified);
        foreach (var statement in qualified.Operations.Cast<SqlOperation>()) await DeploymentAsync(statement.Sql);
    }

    private sealed class FixedAdmission : ISeedAdministrationOriginAdmission
    {
        public SeedAdministrationOriginDeclaration? ForTenant(TenantId tenantId) => SeedAdministrationOriginManifest.Find(tenantId);
    }

    private sealed record TenantState(long Version, int Marker, string Revision);
}

/// <summary>Real fixed content and Identity acts shared by origin completion controls.</summary>
internal static class SeedOriginCompletedFoundationFixture
{
    internal static readonly IdentityAuthorityRegistration Registration = new(
        IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-000000009001")),
        "http://localhost:8080/realms/learnstack");

    internal static async Task InstallAsync(NpgsqlDataSource source)
    {
        await InstallContentAsync(source);
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            issuer = Registration.Issuer,
            users = SeedData.IdentityUsers.Select((person, index) => new
            {
                actorId = person.UserId.Value,
                opaqueSub = $"origin-fixture-provider-id-{index}",
            }),
        });
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var witness = await SeedProviderWitness.ReadAsync(input, Registration);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        await new SeedIdentityFoundationRunner(context => SeedComposition.Build(source, context,
            NullLoggerFactory.Instance, identityAuthority: Registration, seedAdmission: admission), admission).RunAsync();
    }

    internal static async Task InstallContentAsync(NpgsqlDataSource source)
    {
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None)).Should().Be(0);
    }
}
