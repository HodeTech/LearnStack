using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenancyAdministrationMigrationTests(SchemaFixture schema)
{
    private const string Predecessor = "20261029045617_qualify_seed_administration_origins";
    private const string InitialGuardMigration = "20261029101520_enforce_tenancy_administration";

    [Theory]
    [InlineData("Suspended", false)]
    [InlineData("Active", true)]
    public async Task Invalid_established_default_refuses_deployment_without_repair_and_restores_forced_rls(
        string status, bool deleted)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var migration = Context(database);
        await migration.GetService<IMigrator>().MigrateAsync(Predecessor);
        var tenant = Guid.CreateVersion7();
        var organization = Guid.CreateVersion7();
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@tenant,@slug,'Migration fixture','Trial',clock_timestamp(),@actor,1);
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version,deleted_at,deleted_by)
                    VALUES(@organization,@tenant,'main','Migration fixture',@status,clock_timestamp(),@actor,1,
                        CASE WHEN @deleted THEN clock_timestamp() ELSE NULL END,CASE WHEN @deleted THEN @actor ELSE NULL END);
                UPDATE public.tenants SET default_organization_id=@organization WHERE id=@tenant;
                """, ("tenant", tenant), ("organization", organization), ("slug", "migration-" + tenant.ToString("N")),
                ("actor", UserId.SystemActor.Value), ("status", status), ("deleted", deleted));
            await transaction.CommitAsync();
        }
        var before = await SnapshotAsync(database, tenant);
        var apply = async () => await migration.Database.MigrateAsync();
        var refusal = (await apply.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.Should().Be("tenancy_default_organization");
        (await migration.Database.GetAppliedMigrationsAsync()).Last().Should().Be(Predecessor);
        (await SnapshotAsync(database, tenant)).Should().Be(before);
        await AssertForcedRlsAsync(database);

        // Explicit operator remediation of test-owned old data. The deployment did
        // not pick another default or rewrite history on the operator's behalf.
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction,
                "UPDATE public.organizations SET status='Active',deleted_at=NULL,deleted_by=NULL WHERE id=@organization",
                ("organization", organization));
            await transaction.CommitAsync();
        }
        var valid = await SnapshotAsync(database, tenant);
        await migration.Database.MigrateAsync();
        (await SnapshotAsync(database, tenant)).Should().Be(valid);
        await AssertForcedRlsAsync(database);
        migration.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Theory]
    [InlineData(Predecessor)]
    [InlineData(InitialGuardMigration)]
    public async Task Invoker_guards_have_no_runtime_execute_grant_and_fresh_and_upgrade_definitions_match(string predecessor)
    {
        await using var upgrade = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var migration = Context(upgrade);
        await migration.GetService<IMigrator>().MigrateAsync(predecessor);
        await migration.Database.MigrateAsync();
        var upgraded = await GuardDefinitionsAsync(upgrade);
        upgraded.Should().HaveCount(2);
        await using var fresh = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        (await GuardDefinitionsAsync(fresh)).Should().Equal(upgraded);
        await migration.Database.MigrateAsync();
        (await GuardDefinitionsAsync(upgrade)).Should().Equal(upgraded);
        await AssertForcedRlsAsync(upgrade);
    }

    [Fact]
    public async Task Forward_guard_upgrade_preserves_a_retained_incomplete_root_and_fences_its_next_update()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using var migration = Context(database);
        await migration.GetService<IMigrator>().MigrateAsync(InitialGuardMigration);
        var tenant = Guid.CreateVersion7();
        var organization = Guid.CreateVersion7();
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await AnnounceAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version,updated_at,updated_by)
                    VALUES(@tenant,'retained-upgrade','Retained upgrade','Trial',clock_timestamp(),@actor,7,clock_timestamp(),@actor);
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@organization,@tenant,'main','Main','Active',clock_timestamp(),@actor,0);
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", tenant), ("organization", organization), ("actor", UserId.SystemActor.Value));
            await transaction.CommitAsync();
        }
        var before = await SnapshotAsync(database, tenant);
        await migration.Database.MigrateAsync();
        (await SnapshotAsync(database, tenant)).Should().Be(before);
        await AssertForcedRlsAsync(database);
        await using var retained = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(retained);
        await using (var transaction = await retained.BeginTransactionAsync())
        {
            await AnnounceAsync(retained, transaction, tenant);
            var forge = async () => await ExecuteAsync(retained, transaction,
                "UPDATE public.tenants SET row_version=0,updated_at=NULL,updated_by=NULL WHERE id=@tenant", ("tenant", tenant));
            (await forge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0311");
            await transaction.RollbackAsync();
        }
        (await SnapshotAsync(database, tenant)).Should().Be(before);
        await using (var transaction = await retained.BeginTransactionAsync())
        {
            await AnnounceAsync(retained, transaction, tenant);
            await ExecuteAsync(retained, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));
            await ExecuteAsync(retained, transaction, """
                UPDATE public.tenants SET default_organization_id=@organization,row_version=row_version+1 WHERE id=@tenant;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("organization", organization), ("tenant", tenant));
            await transaction.CommitAsync();
        }
        (await SnapshotAsync(database, tenant)).Should().Contain(organization.ToString("D"));
    }

    [Theory]
    [InlineData("11111111-1111-7111-8111-111111111111")]
    [InlineData("22222222-2222-7222-8222-222222222222")]
    [InlineData("ffffffff-ffff-7fff-bfff-ffffffffffff")]
    public async Task Actual_trigger_accepts_the_same_canonical_tenant_key_as_the_clr_plan(string identifier)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var tenant = Guid.Parse(identifier);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@tenant,'key-fixture','Key fixture','Trial',clock_timestamp(),@actor,0);
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@first,@tenant,'first','First','Active',clock_timestamp(),@actor,0),
                          (@second,@tenant,'second','Second','Active',clock_timestamp(),@actor,0);
                UPDATE public.tenants SET default_organization_id=@first WHERE id=@tenant;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", tenant), ("actor", UserId.SystemActor.Value), ("first", first), ("second", second));
            await transaction.CommitAsync();
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));
            await ExecuteAsync(app, transaction, """
                UPDATE public.tenants SET default_organization_id=@second,row_version=row_version+1 WHERE id=@tenant;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", tenant), ("second", second));
            await transaction.CommitAsync();
        }
        (await SnapshotAsync(database, tenant)).Should().Contain(second.ToString("D"));
    }

    private static TenancyDbContext Context(DisposableSchemaDatabase database) => new(
        new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(database.MigrationConnectionString,
            options => options.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable)).Options,
        StaticTenantContextAccessor.Unresolved);

    private static async Task<string> SnapshotAsync(DisposableSchemaDatabase database, Guid tenant)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, tenant);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('tenant',(SELECT to_jsonb(t) FROM public.tenants AS t),
                'organizations',(SELECT jsonb_agg(to_jsonb(o) ORDER BY id) FROM public.organizations AS o))::text
            """, app, transaction);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertForcedRlsAsync(DisposableSchemaDatabase database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_class WHERE oid IN ('public.tenants'::regclass,'public.organizations'::regclass)
                AND relrowsecurity AND relforcerowsecurity
            """, owner);
        (await command.ExecuteScalarAsync()).Should().Be(2L);
    }

    private static async Task<IReadOnlyList<string>> GuardDefinitionsAsync(DisposableSchemaDatabase database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_functiondef(p.oid),pg_get_userbyid(p.proowner),p.prosecdef,p.provolatile::text,
                array_to_string(p.proconfig,','),has_function_privilege('learnstack_app',p.oid,'EXECUTE'),
                has_function_privilege('learnstack_platform',p.oid,'EXECUTE'),
                has_function_privilege('learnstack_outbox_admin',p.oid,'EXECUTE')
            FROM pg_catalog.pg_proc AS p JOIN pg_catalog.pg_namespace AS n ON n.oid=p.pronamespace
            WHERE n.nspname='public' AND p.proname IN ('fn_tenancy_administration_guard','fn_tenancy_default_organization_guard')
            ORDER BY p.proname
            """, owner);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync())
        {
            reader.GetString(1).Should().Be("learnstack_migration");
            reader.GetBoolean(2).Should().BeFalse();
            reader.GetString(3).Should().Be("v");
            reader.GetString(4).Should().Be("search_path=pg_catalog");
            reader.GetBoolean(5).Should().BeFalse();
            reader.GetBoolean(6).Should().BeFalse();
            reader.GetBoolean(7).Should().BeFalse();
            definitions.Add(reader.GetString(0));
        }
        return definitions;
    }

    private static Task AnnounceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant) =>
        ExecuteAsync(connection, transaction,
            "SELECT set_config('app.tenant_id',@tenant,true),set_config('app.organization_id','',true),set_config('app.scope','',true)",
            ("tenant", tenant.ToString("D")));

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
