using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Every_authoritative_column_has_the_exact_app_dispatcher_platform_and_public_budget()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        foreach (var table in BackgroundJobSchemaInventory.Tables)
        {
            var columns = await SchemaQueries.ReadStringsAsync(owner,
                $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' ORDER BY column_name");
            columns.Should().NotBeEmpty();
            foreach (var role in new[] { "learnstack_app", "learnstack_outbox_admin", "learnstack_platform", "PUBLIC" })
                foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "REFERENCES" })
                {
                    var granted = await SchemaQueries.ReadStringsAsync(owner, $"""
                    SELECT column_name FROM information_schema.column_privileges
                    WHERE table_schema = 'public' AND table_name = '{table}' AND grantee = '{role}'
                      AND privilege_type = '{privilege}' ORDER BY column_name
                    """);
                    granted.Should().BeEquivalentTo(BackgroundJobGrantExpectations.Columns(role, table, privilege, columns),
                        $"{role} has an exact {privilege} column budget on {table}");
                }
        }
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT table_name || ':' || privilege_type FROM information_schema.table_privileges
            WHERE table_schema = 'public' AND table_name LIKE 'background_job%'
              AND grantee <> 'learnstack_migration' AND privilege_type <> 'SELECT'
            """)).Should().BeEmpty("runtime writes are enumerated column grants, with no table-wide widening");
        (await BackgroundJobGrantOptions.ReadAsync(owner)).Should().BeEmpty(
            "runtime roles and PUBLIC must not delegate any table, column, sequence or function privilege");
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var isolatedOwner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        foreach (var probe in BackgroundJobGrantOptions.Probes)
        {
            await SchemaQueries.ExecuteAsync(isolatedOwner, null, probe.Grant);
            try
            {
                (await BackgroundJobGrantOptions.ReadAsync(isolatedOwner)).Should().ContainSingle()
                    .Which.Should().Be(probe.Finding, "the same detector must expose each actual WITH GRANT OPTION mutation");
            }
            finally
            {
                await SchemaQueries.ExecuteAsync(isolatedOwner, null, probe.Restore);
            }
            (await BackgroundJobGrantOptions.ReadAsync(isolatedOwner)).Should().BeEmpty(
                "restoring the exact original ACL must clear the finding");
        }

    }

    [Theory]
    [InlineData("dispatcher", "SELECT parameters FROM background_jobs")]
    [InlineData("dispatcher", "SELECT plan FROM background_jobs")]
    [InlineData("dispatcher", "SELECT traceparent FROM background_jobs")]
    [InlineData("dispatcher", "SELECT causal_actor_user_id FROM background_jobs")]
    [InlineData("dispatcher", "SELECT causation_event_id FROM background_jobs")]
    [InlineData("dispatcher", "SELECT outcome FROM background_job_attempts")]
    [InlineData("dispatcher", "INSERT INTO background_jobs (id) VALUES (uuidv7())")]
    [InlineData("dispatcher", "UPDATE background_jobs SET processed_at = now()")]
    [InlineData("dispatcher", "UPDATE background_jobs SET plan = '{}'")]
    [InlineData("dispatcher", "UPDATE background_job_recipients SET accepted_job_id = uuidv7()")]
    [InlineData("dispatcher", "UPDATE background_job_reservations SET released_at = now()")]
    [InlineData("app", "UPDATE background_jobs SET ownership_token = uuidv7()")]
    [InlineData("app", "UPDATE background_jobs SET bridge_token = uuidv7()")]
    [InlineData("app", "UPDATE background_jobs SET wake_generation = 2")]
    [InlineData("app", "UPDATE background_jobs SET parameters = '{}'")]
    [InlineData("app", "UPDATE background_job_attempts SET ordinal = 2")]
    [InlineData("app", "INSERT INTO background_job_recipients (id) VALUES (uuidv7())")]
    [InlineData("app", "INSERT INTO background_jobs (id, normal_attempts) VALUES (uuidv7(),1)")]
    [InlineData("app", "INSERT INTO background_job_attempts (id, execute_intent_at) VALUES (uuidv7(), now())")]
    [InlineData("app", "INSERT INTO background_job_reservations (released_at) VALUES (now())")]
    [InlineData("platform", "SELECT * FROM background_jobs")]
    [InlineData("platform", "SELECT * FROM background_job_capacity")]
    public async Task Real_role_denied_columns_refuse_before_any_row_is_read_or_changed(string role, string sql)
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
        var statement = async () => await SchemaQueries.ExecuteAsync(connection, transaction, sql);
        (await statement.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should()
            .Be(PostgresErrorCodes.InsufficientPrivilege);
    }
}
