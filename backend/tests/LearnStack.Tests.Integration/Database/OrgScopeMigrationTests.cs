using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The organization INSERT correction upgrades a populated database without
/// changing its rows and reverses to the exact policies and function it replaced.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OrgScopeMigrationTests
{
    private const string PreviousTenancyMigration = "20260910141731_create_platform_killswitches";
    private const string PreviousAuditMigration = "20260911150558_audit_config_live_override_unique";
    private const string CorrectedTenancyMigration = "20260914114306_org_insert_scope_and_keyless_guard";
    private const string CorrectedAuditMigration = "20260914114341_audit_log_org_insert_scope";

    private readonly SchemaFixture _schema;

    public OrgScopeMigrationTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task OrgScopeCorrection_PopulatedUpgradeDownAndReapply_PreservesRowsAndRestoresDefinitions()
    {
        // Only this disposable database is reversed. The shared schema, and every
        // other test's committed seed, remain intact.
        await using var database = await DisposableSchemaDatabase.CreateAsync(
            _schema.Postgres, applyMigrations: false);
        await MigrateTenancyAsync(database.MigrationConnectionString, PreviousTenancyMigration);
        await MigrateAuditAsync(database.MigrationConnectionString, PreviousAuditMigration);
        await SeedTenantAsync(database.AppConnectionString);

        await InsertPairAsync(database.AppConnectionString, null, null, "tenant-row", commit: true);
        await InsertPairAsync(database.AppConnectionString,
            SchemaFixture.OrgA1, SchemaFixture.OrgA1, "org-row", commit: true);
        // A positive reproduction against the historical schema: both old INSERT
        // policies admitted a tenant-wide row from an organization-scoped session.
        await InsertPairAsync(database.AppConnectionString,
            SchemaFixture.OrgA1, null, "legacy-org-insert", commit: true);

        var originalDefinitions = await ReadDefinitionsAsync(database.MigrationConnectionString);
        var tenantRows = await ReadRowsAsync(database.AppConnectionString, null);
        var organizationRows = await ReadRowsAsync(database.AppConnectionString, SchemaFixture.OrgA1);
        tenantRows.Should().HaveCount(4, "each table contains two tenant-wide rows");
        organizationRows.Should().HaveCount(6, "the matching organization sees every seeded row");
        originalDefinitions.Policies.Should().HaveCount(6);
        originalDefinitions.Function.Should().Contain("OLD.id");

        await ApplyCorrectionAsync(database.MigrationConnectionString);

        var correctedDefinitions = await ReadDefinitionsAsync(database.MigrationConnectionString);
        AssertOnlyIntendedDefinitionsChanged(originalDefinitions, correctedDefinitions);
        await AssertRowsPreservedAsync(database.AppConnectionString, tenantRows, organizationRows);
        await AssertCorrectedWritesAsync(database.AppConnectionString);

        // Reverse the dependent Audit change before Tenancy, without reversing
        // any migration that created the populated tables.
        await MigrateAuditAsync(database.MigrationConnectionString, PreviousAuditMigration);
        await MigrateTenancyAsync(database.MigrationConnectionString, PreviousTenancyMigration);

        (await ReadDefinitionsAsync(database.MigrationConnectionString)).Should()
            .BeEquivalentTo(originalDefinitions, "Down must restore the exact previous definitions");
        await AssertRowsPreservedAsync(database.AppConnectionString, tenantRows, organizationRows);
        await InsertPairAsync(database.AppConnectionString,
            SchemaFixture.OrgA1, null, "restored-legacy-insert", commit: false);

        await ApplyCorrectionAsync(database.MigrationConnectionString);

        (await ReadDefinitionsAsync(database.MigrationConnectionString)).Should()
            .BeEquivalentTo(correctedDefinitions, "reapplying must reproduce the first upgraded schema");
        await AssertRowsPreservedAsync(database.AppConnectionString, tenantRows, organizationRows);
        await AssertCorrectedWritesAsync(database.AppConnectionString);
    }

    private static void AssertOnlyIntendedDefinitionsChanged(
        SchemaDefinitions original, SchemaDefinitions corrected)
    {
        corrected.Policies.Keys.Should().BeEquivalentTo(original.Policies.Keys);
        foreach (var (name, definition) in original.Policies)
        {
            if (name is "tenant_settings_isolation" or "audit_log_isolation")
            {
                corrected.Policies[name].Should().NotBe(definition,
                    $"the INSERT correction must replace {name}'s WITH CHECK");
            }
            else
            {
                corrected.Policies[name].Should().Be(definition,
                    $"the correction must preserve the existing restrictive policy {name}");
            }
        }

        corrected.Function.Should().NotBe(original.Function)
            .And.NotContain("OLD.id", "the shared guard must also work on rows without an id column");
    }

    private static async Task AssertCorrectedWritesAsync(string appConnectionString)
    {
        // Independent transactions prove that BOTH policies reject the row: if
        // they shared a transaction, the first failure would abort the second act.
        foreach (var (table, statement) in Inserts)
        {
            await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetScopeAsync(connection, transaction, SchemaFixture.OrgA1);

            var act = () => ExecuteInsertAsync(
                connection, transaction, statement, null, "refused-org-insert");

            (await act.Should().ThrowAsync<PostgresException>($"{table} must enforce the corrected INSERT scope"))
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Positive controls under both legal write scopes, rolled back so the
        // retained-row comparison remains about the original populated database.
        await InsertPairAsync(appConnectionString, null, null, "allowed-tenant-insert", commit: false);
        await InsertPairAsync(appConnectionString,
            SchemaFixture.OrgA1, SchemaFixture.OrgA1, "allowed-org-insert", commit: false);
    }

    private static async Task AssertRowsPreservedAsync(
        string appConnectionString, IReadOnlyList<string> tenantRows, IReadOnlyList<string> organizationRows)
    {
        (await ReadRowsAsync(appConnectionString, null)).Should().Equal(tenantRows,
            "tenant-wide reads and every stored column must survive the migration");
        (await ReadRowsAsync(appConnectionString, SchemaFixture.OrgA1)).Should().Equal(organizationRows,
            "organization reads must retain both their own rows and the tenant-wide fallback");
    }

    private static async Task<List<string>> ReadRowsAsync(string appConnectionString, Guid? organizationId)
    {
        await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, organizationId);

        return await SchemaQueries.ReadStringsAsync(connection,
            """
            SELECT row_data FROM (
                SELECT 'tenant_settings' AS table_name, id, to_jsonb(s)::text AS row_data
                FROM public.tenant_settings AS s
                UNION ALL
                SELECT 'audit_log' AS table_name, id, to_jsonb(a)::text AS row_data
                FROM public.audit_log AS a
            ) AS rows
            ORDER BY table_name, id
            """, transaction);
    }

    private static async Task<SchemaDefinitions> ReadDefinitionsAsync(string migrationConnectionString)
    {
        // Catalogue inspection uses the migration login; every data read and
        // behavioral assertion above uses a separate authenticated app login.
        await using var connection = await PostgresFixture.OpenAsync(migrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT policyname, to_jsonb(p)::text
            FROM pg_catalog.pg_policies AS p
            WHERE schemaname = 'public' AND tablename IN ('tenant_settings', 'audit_log')
            ORDER BY policyname
            """, (NpgsqlConnection)connection);

        var policies = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                policies.Add(reader.GetString(0), reader.GetString(1));
            }
        }

        command.CommandText =
            "SELECT pg_catalog.pg_get_functiondef('public.fn_organization_id_immutable()'::regprocedure)";
        return new SchemaDefinitions(policies, (string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task SeedTenantAsync(string appConnectionString)
    {
        await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, null);
        (await SchemaQueries.ReadStringsAsync(connection, "SELECT current_user", transaction))
            .Should().Equal("learnstack_app");

        await SchemaQueries.ExecuteAsync(connection, transaction,
            """
            INSERT INTO public.tenants
                (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@tenant, 'migration-probe', 'Migration probe', 'Trial', now(), @actor, 0);
            INSERT INTO public.organizations
                (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@org, @tenant, 'main', 'Main', 'Active', now(), @actor, 0);
            """,
            ("tenant", SchemaFixture.TenantA), ("org", SchemaFixture.OrgA1), ("actor", SchemaFixture.Actor));
        await transaction.CommitAsync();
    }

    private static async Task InsertPairAsync(
        string appConnectionString, Guid? sessionOrganizationId, Guid? rowOrganizationId, string label, bool commit)
    {
        await using var connection = await PostgresFixture.OpenAsync(appConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, sessionOrganizationId);
        foreach (var (_, statement) in Inserts)
        {
            await ExecuteInsertAsync(connection, transaction, statement, rowOrganizationId, label);
        }

        if (commit)
        {
            await transaction.CommitAsync();
        }
    }

    private static Task ExecuteInsertAsync(
        DbConnection connection, DbTransaction transaction, string statement, Guid? organizationId, string label) =>
        SchemaQueries.ExecuteAsync(connection, transaction, statement,
            ("tenant", SchemaFixture.TenantA), ("organization", organizationId?.ToString() ?? string.Empty),
            ("actor", SchemaFixture.Actor), ("label", label));

    private static async Task SetScopeAsync(
        DbConnection connection, DbTransaction transaction, Guid? organizationId)
    {
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        await SchemaQueries.SetSettingAsync(connection, transaction,
            "app.organization_id", organizationId?.ToString() ?? string.Empty);
    }

    private static async Task ApplyCorrectionAsync(string migrationConnectionString)
    {
        await MigrateTenancyAsync(migrationConnectionString, CorrectedTenancyMigration);
        await MigrateAuditAsync(migrationConnectionString, CorrectedAuditMigration);
    }

    private static async Task MigrateTenancyAsync(string connectionString, string target)
    {
        await using var context = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task MigrateAuditAsync(string connectionString, string target)
    {
        await using var context = new AuditDbContext(
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(AuditDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static readonly (string Table, string Statement)[] Inserts =
    [
        ("tenant_settings",
            """
            INSERT INTO public.tenant_settings
                (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, NULLIF(@organization, '')::uuid, @label,
                    '{"preserved": true}'::jsonb, now(), @actor, 0)
            """),
        ("audit_log",
            """
            INSERT INTO public.audit_log
                (tenant_id, organization_id, actor_user_id, module, operation,
                 operation_type, operation_class, outcome, correlation_id, after_state)
            VALUES (@tenant, NULLIF(@organization, '')::uuid, @actor, 'tenancy',
                    'tenancy.organization.create', 'Create', 'Must', 'success', @label,
                    '{"preserved": true}'::jsonb)
            """),
    ];

    private sealed record SchemaDefinitions(Dictionary<string, string> Policies, string Function);
}
