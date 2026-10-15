using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Every_control_and_vendor_table_is_nonempty_but_denies_application_select()
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(schema.Postgres.OutboxConnectionString);
        foreach (var table in SchemaTableClasses.Control.Concat(SchemaTableClasses.Vendor))
        {
            (await SchemaQueries.CountAsync(dispatcher, $"SELECT count(*) FROM \"{table}\""))
                .Should().BeGreaterThan(0, $"{table} needs a populated positive control for its app denial");
        }
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        var all = await SchemaQueries.CountEveryTableAsync(app, transaction: null);
        foreach (var table in SchemaTableClasses.Control.Concat(SchemaTableClasses.Vendor))
            all[table].Should().Be(SchemaTableClasses.SelectDenied);
    }

    [Fact]
    public async Task Control_tables_have_force_rls_and_no_policies_or_app_platform_public_grants()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT tablename FROM pg_policies WHERE schemaname = 'public'
              AND tablename IN ('background_job_recurrences','background_job_occurrences')
            """)).Should().BeEmpty("control access is an enumerated dispatcher grant, never tenant authority");
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT table_name || ':' || grantee || ':' || privilege_type
            FROM information_schema.column_privileges
            WHERE table_schema = 'public' AND table_name IN ('background_job_recurrences','background_job_occurrences')
              AND grantee IN ('learnstack_app','learnstack_platform','PUBLIC')
            """)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("background_jobs")]
    [InlineData("background_job_attempts")]
    [InlineData("background_job_recipients")]
    public async Task Org_scoped_job_rows_preserve_cross_org_and_tenantwide_read_semantics(string table)
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM {table} WHERE organization_id = '{SchemaFixture.OrgA1}'", transaction)).Should().Be(1L);
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM {table} WHERE organization_id = '{SchemaFixture.OrgA2}'", transaction)).Should().Be(0L);
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM {table} WHERE organization_id IS NULL", transaction)).Should().Be(1L);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", "tenant");
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM {table} WHERE organization_id = '{SchemaFixture.OrgA2}'", transaction)).Should().Be(1L);
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM {table} WHERE tenant_id = '{SchemaFixture.TenantB}'", transaction)).Should().Be(0L);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("background_job_occurrences")]
    public async Task The_app_denial_sweep_rejects_a_granted_empty_vendor_or_control_table(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.CountEveryTableAsync(app, transaction: null);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, $"GRANT SELECT ON {table} TO learnstack_app");
        var scan = async () => await SchemaQueries.CountEveryTableAsync(app, transaction: null);
        await scan.Should().ThrowAsync<Xunit.Sdk.XunitException>(
            "a zero-row SELECT is not the required 42501 ACL refusal");
        await SchemaQueries.ExecuteAsync(owner, null, $"REVOKE SELECT ON {table} FROM learnstack_app");
        await SchemaQueries.CountEveryTableAsync(app, transaction: null);
    }

    private static readonly string[] RuntimeRoles = ["app", "dispatcher", "platform"];
    private static readonly string[] PurgeVerbs = ["DELETE FROM", "TRUNCATE"];

    public static IEnumerable<object[]> RuntimePurgeCases() =>
        from role in RuntimeRoles
        from table in BackgroundJobSchemaInventory.Tables
        from verb in PurgeVerbs
        select new object[] { role, table, verb };

    [Theory]
    [MemberData(nameof(RuntimePurgeCases))]
    public async Task No_runtime_role_can_delete_or_truncate_job_history(string role, string table, string verb)
    {
        var connectionString = role switch
        {
            "app" => schema.Postgres.AppConnectionString,
            "dispatcher" => schema.Postgres.OutboxConnectionString,
            "platform" => schema.Postgres.PlatformConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        if (role == "app")
            await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        var purge = async () => await SchemaQueries.ExecuteAsync(connection, transaction, $"{verb} {table}");
        (await purge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
            .Be(PostgresErrorCodes.InsufficientPrivilege, "retention/purge authority belongs to Phase 11");
    }
}
