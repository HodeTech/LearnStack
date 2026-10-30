using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual SELECT-only lifecycle grants, their denied siblings and reversible migration.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class BackgroundJobLifecycleProjectionTests(SchemaFixture schema)
{
    private static readonly string[] Expected =
    [
        "organizations:deleted_at:SELECT:NO", "organizations:id:SELECT:NO",
        "organizations:status:SELECT:NO", "organizations:tenant_id:SELECT:NO",
        "tenants:default_organization_id:SELECT:NO", "tenants:deleted_at:SELECT:NO",
        "tenants:id:SELECT:NO", "tenants:status:SELECT:NO",
    ];

    [Fact]
    public async Task Only_the_eight_named_columns_are_visible_and_an_extra_grant_is_detected()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        (await GrantsAsync(migration)).Should().Equal(Expected);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await SchemaQueries.CountAsync(dispatcher, "SELECT count(id) FROM tenants")).Should().Be(2);
        (await SchemaQueries.CountAsync(dispatcher, "SELECT count(id) FROM organizations")).Should().Be(3);
        await SchemaQueries.ExecuteAsync(migration, null, "GRANT SELECT (display_name) ON tenants TO learnstack_outbox_admin");
        try
        {
            var widened = await GrantsAsync(migration);
            widened.Except(Expected, StringComparer.Ordinal).Should().ContainSingle().Which.Should().Be("tenants:display_name:SELECT:NO");
            var exactBudget = () => widened.Should().Equal(Expected);
            exactBudget.Should().Throw<Xunit.Sdk.XunitException>("the exact same budget assertion must refuse a real extra column grant");
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, "REVOKE SELECT (display_name) ON tenants FROM learnstack_outbox_admin");
        }
        (await GrantsAsync(migration)).Should().Equal(Expected);
    }

    [Theory]
    [InlineData("SELECT * FROM tenants")]
    [InlineData("SELECT display_name FROM tenants")]
    [InlineData("SELECT slug FROM tenants")]
    [InlineData("SELECT * FROM organizations")]
    [InlineData("SELECT display_name FROM organizations")]
    [InlineData("UPDATE tenants SET status='Active'")]
    [InlineData("UPDATE organizations SET status='Active'")]
    [InlineData("DELETE FROM tenants")]
    [InlineData("INSERT INTO tenants (id) VALUES (uuidv7())")]
    public async Task Projection_does_not_grant_names_wider_reads_or_tenancy_mutation(string sql)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString);
        var query = async () => await SchemaQueries.ExecuteAsync(dispatcher, null, sql);
        (await query.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Projection_grant_down_and_reapply_change_only_the_named_column_budget()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(database.MigrationConnectionString, postgres => postgres.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable)).Options;
        await using var context = new TenancyDbContext(options, StaticTenantContextAccessor.Unresolved);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        const string PreviousMigration = "20261002001839_tenant_locale_default_enabled";
        const string ProjectionMigration = "20261017210806_grant_job_lifecycle_projection";
        var all = context.Database.GetMigrations().ToArray();
        var projectionIndex = Array.IndexOf(all, ProjectionMigration);
        projectionIndex.Should().BeGreaterThan(0);
        all[projectionIndex - 1].Should().Be(PreviousMigration);
        var migrator = context.GetService<IMigrator>();
        // Start at the owned historical boundary: later forward-only guards
        // must never be rolled back to qualify this reversible grant migration.
        await migrator.MigrateAsync(PreviousMigration);
        (await GrantsAsync(migration)).Should().BeEmpty();
        var tables = await SchemaQueries.ReadStringsAsync(migration, SchemaQueries.TableNames);
        tables.Should().NotBeEmpty();
        await migrator.MigrateAsync(ProjectionMigration);
        (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(all.Take(projectionIndex + 1));
        (await GrantsAsync(migration)).Should().Equal(Expected);
        (await SchemaQueries.ReadStringsAsync(migration, SchemaQueries.TableNames)).Should().Equal(tables);
        await migrator.MigrateAsync(PreviousMigration);
        (await GrantsAsync(migration)).Should().BeEmpty();
        (await SchemaQueries.ReadStringsAsync(migration, SchemaQueries.TableNames)).Should().Equal(tables);
        await migrator.MigrateAsync(ProjectionMigration);
        (await GrantsAsync(migration)).Should().Equal(Expected);

        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryPath.RolesScript())!, "../../.."));
        var document = await File.ReadAllTextAsync(Path.Combine(root, "docs/standards/05-database.md"));
        var section = document[document.IndexOf("### Job lifecycle projection\n", StringComparison.Ordinal)..];
        var canonical = section.Split("```sql\n", StringSplitOptions.None)[1].Split("\n```", StringSplitOptions.None)[0];
        // A pregranted schema cannot distinguish a sufficient canonical block
        // from an empty one. Exercise both versions from a demonstrated empty ACL.
        await migrator.MigrateAsync(PreviousMigration);
        (await GrantsAsync(migration)).Should().BeEmpty();
        canonical.Split("default_organization_id", StringSplitOptions.None).Should().HaveCount(2,
            "the missing-column control must remove one actual required grant");
        var missingColumn = canonical.Replace(",default_organization_id", "", StringComparison.Ordinal);
        missingColumn.Should().NotBe(canonical);
        await SchemaQueries.ExecuteAsync(migration, null, missingColumn);
        var deficient = await GrantsAsync(migration);
        Expected.Except(deficient, StringComparer.Ordinal).Should().ContainSingle().Which.Should()
            .Be("tenants:default_organization_id:SELECT:NO");
        var exactBudget = () => deficient.Should().Equal(Expected);
        exactBudget.Should().Throw<Xunit.Sdk.XunitException>(
            "the same exact-budget assertion must reject a canonical block missing a required column");

        await migrator.MigrateAsync(ProjectionMigration);
        await migrator.MigrateAsync(PreviousMigration);
        (await GrantsAsync(migration)).Should().BeEmpty();
        await SchemaQueries.ExecuteAsync(migration, null, canonical);
        (await GrantsAsync(migration)).Should().Equal(Expected, "the sole canonical grant block must itself produce the bounded ACL");
        await migrator.MigrateAsync(ProjectionMigration);
        (await GrantsAsync(migration)).Should().Equal(Expected);
        (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(all.Take(projectionIndex + 1));
        // Only after every named down/up control, qualify the full current schema.
        await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
        (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(all);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        (await GrantsAsync(migration)).Should().Equal(Expected);
    }

    private static Task<List<string>> GrantsAsync(System.Data.Common.DbConnection migration) =>
        SchemaQueries.ReadStringsAsync(migration, """
            SELECT table_name||':'||column_name||':'||privilege_type||':'||is_grantable FROM information_schema.column_privileges
            WHERE table_schema='public' AND grantee='learnstack_outbox_admin' AND table_name IN ('tenants','organizations')
            ORDER BY table_name,column_name,privilege_type,is_grantable
            """);
}
