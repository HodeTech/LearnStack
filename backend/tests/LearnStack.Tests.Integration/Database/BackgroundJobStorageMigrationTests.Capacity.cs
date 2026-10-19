using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobStorageMigrationTests
{
    [Fact]
    public async Task Capacity_mutex_and_occupancy_are_shared_across_organizations_but_isolated_by_tenant()
    {
        await using var first = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var second = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(first);
        await SchemaQueries.AssertApplicationRoleAsync(second);
        await using var firstTransaction = await first.BeginTransactionAsync();
        await using var secondTransaction = await second.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(first, firstTransaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await EducationSchemaSeed.AnnounceAsync(second, secondTransaction, SchemaFixture.TenantA, SchemaFixture.OrgA2);
        foreach (var (connection, transaction) in new[] { (first, firstTransaction), (second, secondTransaction) })
        {
            (await SchemaQueries.CountAsync(connection, "SELECT count(*) FROM background_job_capacity", transaction)).Should().Be(1L);
            (await SchemaQueries.CountAsync(connection, "SELECT count(*) FROM background_job_reservations WHERE released_at IS NULL", transaction))
                .Should().Be(3L, "all organizations coordinate against the same tenant occupancy");
        }
        await SchemaQueries.ExecuteAsync(first, firstTransaction,
            "SELECT capacity_group FROM background_job_capacity WHERE capacity_group = 'fixture.resource' FOR UPDATE");
        var contend = async () => await SchemaQueries.ExecuteAsync(second, secondTransaction,
            "SELECT capacity_group FROM background_job_capacity WHERE capacity_group = 'fixture.resource' FOR UPDATE NOWAIT");
        (await contend.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        await secondTransaction.RollbackAsync();
        await using var otherTenant = await second.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(second, otherTenant, SchemaFixture.TenantB, SchemaFixture.OrgB1);
        await SchemaQueries.ExecuteAsync(second, otherTenant,
            "SELECT capacity_group FROM background_job_capacity WHERE capacity_group = 'fixture.resource' FOR UPDATE NOWAIT");
        (await SchemaQueries.CountAsync(second, "SELECT count(*) FROM background_job_reservations", otherTenant)).Should().Be(2L);
    }

    [Fact]
    public async Task The_locking_grant_permits_a_real_lock_but_cannot_rewrite_the_capacity_group()
    {
        await using var app = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.ExecuteAsync(app, transaction,
            "SELECT capacity_group FROM background_job_capacity FOR UPDATE");
        var rewrite = async () => await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE background_job_capacity SET capacity_group = 'fixture.changed'");
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("never_started")]
    [InlineData("quiescent")]
    [InlineData("transactional_rollback")]
    public async Task Forged_release_has_no_positive_evidence_and_the_removed_guard_control_exposes_the_attack(string reason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        var job = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, null);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        var sql = "UPDATE background_job_reservations SET released_at = clock_timestamp(), release_reason = @reason WHERE job_id = @job";
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, job.TenantId, null);
            var release = async () => await SchemaQueries.ExecuteAsync(app, transaction, sql, ("reason", reason), ("job", job.JobId));
            var refusal = (await release.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Contain("positive completion or quiescence");
        }
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null,
                "DROP TRIGGER tg_background_job_reservations_guard ON background_job_reservations");
        await using var dirty = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, dirty, job.TenantId, null);
        await SchemaQueries.ExecuteAsync(app, dirty, sql, ("reason", reason), ("job", job.JobId));
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM background_job_reservations WHERE released_at IS NOT NULL", dirty))
            .Should().Be(1L);
    }

    [Fact]
    public async Task Expired_routing_or_execution_ownership_does_not_release_unresolved_occupancy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var job = await NewExternalOutcomeAsync(database, recordOutcome: false);
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET ownership_until = clock_timestamp() - interval '1 second' WHERE id = @job", ("job", job.JobId));
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM background_job_reservations WHERE released_at IS NULL", transaction))
            .Should().Be(1L, "a lease is not positive completion or quiescence evidence");
        var release = async () => await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE background_job_reservations SET released_at = clock_timestamp(), release_reason = 'quiescent' WHERE job_id = @job",
            ("job", job.JobId));
        (await release.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Successful_transactional_completion_releases_once_and_retains_the_durable_receipt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedAsync(database.AppConnectionString, database.OutboxConnectionString);
        var job = BackgroundJobSchemaSeed.Find(SchemaFixture.TenantA, null);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, job.TenantId, null);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            UPDATE background_jobs SET phase = 'completed', processed_at = clock_timestamp() WHERE id = @job;
            UPDATE background_job_reservations SET released_at = clock_timestamp(), release_reason = 'completed' WHERE job_id = @job;
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("job", job.JobId));
        (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM background_job_attempts WHERE job_id = '" + job.JobId + "'", transaction))
            .Should().Be(1L);
        var clear = async () => await SchemaQueries.ExecuteAsync(app, transaction,
            "UPDATE background_job_reservations SET released_at = NULL, release_reason = NULL WHERE job_id = @job", ("job", job.JobId));
        (await clear.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }
}
