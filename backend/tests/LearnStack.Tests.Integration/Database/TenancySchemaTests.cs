using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The tenancy schema, its Row Level Security policies and its grants, asserted
/// against a real PostgreSQL with the four roles provisioned.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case connects as <c>learnstack_app</c>.</b> A test that connected as
/// the owner — or as either <c>BYPASSRLS</c> role — would pass with every policy
/// inert and prove nothing, which is the failure mode
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see> names by hand. The one exception is the structural sweep,
/// which reads catalogue tables rather than data.
/// </para>
/// <para>
/// These are the four cases that amendment lists as the minimum, plus the two
/// this schema adds: the organization-id immutability trigger, and the owner
/// being denied on the platform-scoped table.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancySchemaTests : IClassFixture<TenancySchemaFixture>
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111");
    private static readonly Guid SettingA = Guid.Parse("11111111-aaaa-7111-8111-111111111111");

    private readonly TenancySchemaFixture _schema;

    public TenancySchemaTests(TenancySchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task EveryTableEnablesAndForcesRowLevelSecurity()
    {
        // No exception list, deliberately: a table that needed one would be a
        // table nobody had classified, and the three classes in Database
        // Standards § Table classes cover all eight.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT relname FROM pg_class
            WHERE relname = ANY(@tables) AND NOT (relrowsecurity AND relforcerowsecurity)
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tables", TenancySchemaFixture.Tables);

        var unprotected = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                unprotected.Add(reader.GetString(0));
            }
        }

        unprotected.Should().BeEmpty("ENABLE without FORCE lets the owner bypass its own policies");
    }

    [Fact]
    public async Task NoTenantOwnedTableCarriesASecondPermissivePolicy()
    {
        // The defect ADR-0003 Amendment 3 corrects. CREATE POLICY is permissive by
        // default and PostgreSQL combines permissive policies with OR, so a second
        // one WIDENS access — it made every tenant-wide row visible to every
        // tenant. Only platform_host_to_tenant carries several, and they are
        // per-command (SELECT/INSERT/UPDATE/DELETE), which do not combine.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT tablename, count(*)
            FROM pg_policies
            WHERE schemaname = 'public'
              AND permissive = 'PERMISSIVE'
              AND tablename <> 'platform_host_to_tenant'
            GROUP BY tablename HAVING count(*) > 1
            """, (NpgsqlConnection)connection);

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeFalse(
            "a second permissive policy is OR-ed with the first and widens access");
    }

    [Fact]
    public async Task WithNoTenantContextEveryTenantOwnedTableReturnsZeroRows()
    {
        // Fail-closed, and it is the NULLIF that delivers it: an unset dotted GUC
        // reads as the empty string on a pooled connection, and ''::uuid RAISES
        // rather than filtering. NULLIF makes it NULL, and a NULL predicate is
        // false for USING and WITH CHECK alike.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tenants", (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task ATenantSeesItsOwnRowsAndOnlyThose()
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantA);

        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM tenants), (SELECT count(*) FROM organizations)",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        reader.GetInt64(0).Should().Be(1);
        reader.GetInt64(1).Should().Be(1);
    }

    [Fact]
    public async Task ATenantWideRowOfOneTenantIsInvisibleToAnother()
    {
        // THE case the superseded template leaked. Its two permissive policies
        // were OR-ed, so a row with organization_id IS NULL satisfied the
        // organization half on its own — visible to every tenant.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantB);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_settings",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

        (await command.ExecuteScalarAsync()).Should().Be(0L,
            "tenant A's tenant-wide setting must not be visible to tenant B");
    }

    [Fact]
    public async Task AWriteNamingAForeignTenantIsRefused()
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantB);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (gen_random_uuid(), @foreign, 'sneak', 'Sneak', 'Active', now(), @actor, 0)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("foreign", TenantA);
        command.Parameters.AddWithValue("actor", Guid.Parse("00000000-0000-7000-8000-000000000001"));

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("42501", "WITH CHECK refuses a row outside the caller's tenant");
    }

    [Fact]
    public async Task OrganizationIdIsImmutableAfterInsert()
    {
        // Tenant-wide to org-scoped: the NULL -> value move, which `<>` would miss
        // because `<>` is NULL when either side is null. The restrictive UPDATE
        // guard does not cover it either — it admits the row when the NEW
        // organization_id is the caller's own, which is exactly this move.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, TenantA);
        await SetSettingAsync(connection, transaction, "app.organization_id", OrgA.ToString());

        await using var command = new NpgsqlCommand(
            "UPDATE tenant_settings SET organization_id = @org WHERE id = @id",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("org", OrgA);
        command.Parameters.AddWithValue("id", SettingA);

        var act = async () => await command.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("immutable after insert");
    }

    [Fact]
    public async Task TheOwnerIsDeniedOnThePlatformScopedTable()
    {
        // platform_host_to_tenant's policies are role-qualified TO learnstack_app,
        // so NO policy applies to the owner — and under FORCE that is a denial
        // rather than a bypass. Rows arrive through learnstack_app under tenant
        // context, or through learnstack_platform. Stated as a test because it
        // reads like a mistake and is the design.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform_host_to_tenant", (NpgsqlConnection)connection);

        (await command.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task EveryMappedIdentifierIsSnakeCase()
    {
        // Every policy predicate, every GRANT and every index name in Database
        // Standards is written against snake_case identifiers, so one PascalCase
        // column is a column the policy does not mention and the grant does not
        // cover.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = ANY(@tables) AND column_name <> lower(column_name)
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tables", TenancySchemaFixture.Tables);

        var offenders = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                offenders.Add(reader.GetString(0));
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task TheApplicationRoleHoldsExactlyTheGrantsTheMatrixNames()
    {
        // There is no ALTER DEFAULT PRIVILEGES, so every grant is one the migration
        // wrote. Asserted table by table because the matrix is the whole of the
        // bound on the bypass roles, and a widened grant is invisible otherwise.
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT table_name, string_agg(privilege_type, ',' ORDER BY privilege_type)
            FROM information_schema.role_table_grants
            WHERE grantee = 'learnstack_app' AND table_schema = 'public'
            GROUP BY table_name ORDER BY table_name
            """, (NpgsqlConnection)connection);

        var grants = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                grants[reader.GetString(0)] = reader.GetString(1);
            }
        }

        grants.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenants"] = "INSERT,SELECT,UPDATE",
            ["organizations"] = "DELETE,INSERT,SELECT,UPDATE",
            ["tenant_domains"] = "DELETE,INSERT,SELECT,UPDATE",
            ["tenant_locales"] = "DELETE,INSERT,SELECT,UPDATE",
            ["tenant_settings"] = "DELETE,INSERT,SELECT,UPDATE",
            ["tenant_feature_flags"] = "DELETE,INSERT,SELECT,UPDATE",
            ["platform_entitlement_cache"] = "INSERT,SELECT,UPDATE",
            ["platform_host_to_tenant"] = "DELETE,INSERT,SELECT,UPDATE",
        });
    }

    private static Task SetTenantAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid tenantId)
        => SetSettingAsync(connection, transaction, "app.tenant_id", tenantId.ToString());

    private static async Task SetSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string name,
        string value)
    {
        // set_config(..., true) rather than SET LOCAL: PostgreSQL's SET takes no
        // bind parameter — `SET LOCAL x = $1` is a syntax error, measured — and the
        // third argument `true` is what makes it transaction-local. The transaction
        // is passed explicitly because that locality is the whole point: a setting
        // applied to the wrong transaction is applied to nothing.
        await using var command = new NpgsqlCommand(
            "SELECT set_config(@name, @value, true)",
            (NpgsqlConnection)connection,
            (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Applies the tenancy migration once and seeds the two tenants the isolation
/// cases compare.
/// </summary>
/// <remarks>
/// The seed runs as <c>learnstack_migration</c> inside a transaction that sets
/// <c>app.tenant_id</c> per tenant — the only way to insert, since every table's
/// <c>WITH CHECK</c> is live from the moment the migration finishes.
/// </remarks>
public sealed class TenancySchemaFixture : IAsyncLifetime
{
    internal static readonly string[] Tables =
    [
        "tenants", "organizations", "tenant_domains", "tenant_locales",
        "tenant_settings", "tenant_feature_flags",
        "platform_entitlement_cache", "platform_host_to_tenant",
    ];

    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history_tenancy"))
            .Options;

        await using (var context = new TenancyDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await SeedAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    private async Task SeedAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            """
            BEGIN;
            SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('11111111-1111-7111-8111-111111111111','alpha','Alpha','Trial', now(),
                    '00000000-0000-7000-8000-000000000001', 0);
            INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('aaaaaaaa-1111-7111-8111-111111111111','11111111-1111-7111-8111-111111111111',
                    'main','Main','Active', now(), '00000000-0000-7000-8000-000000000001', 0);
            UPDATE tenants SET default_organization_id = 'aaaaaaaa-1111-7111-8111-111111111111'
            WHERE id = '11111111-1111-7111-8111-111111111111';
            INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
            VALUES ('11111111-aaaa-7111-8111-111111111111','11111111-1111-7111-8111-111111111111',
                    NULL, 'tz', '"Europe/Istanbul"', now(), '00000000-0000-7000-8000-000000000001', 0);
            COMMIT;

            BEGIN;
            SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES ('22222222-2222-7222-8222-222222222222','beta','Beta','Trial', now(),
                    '00000000-0000-7000-8000-000000000001', 0);
            COMMIT;
            """, (NpgsqlConnection)connection);

        await command.ExecuteNonQueryAsync();
    }
}
