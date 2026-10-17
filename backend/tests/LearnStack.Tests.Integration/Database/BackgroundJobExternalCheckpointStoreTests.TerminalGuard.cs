using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Persistence.Migrations;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("retry_exhausted")]
    [InlineData(null)]
    public async Task Terminal_storage_freezes_the_requested_reason_before_app_cleanup(string? replacement)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services, 6, store, runtime);
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var write = new NpgsqlCommand("UPDATE background_jobs SET terminal_reason=@reason WHERE id=@id",
            (NpgsqlConnection)control)
        { CommandTimeout = 5 };
        write.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        write.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)replacement ?? DBNull.Value);
        var substitute = async () => await write.ExecuteNonQueryAsync();
        var refused = (await substitute.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refused.MessageText.Should().Be("a terminal request retains its original reason");
        await AssertNoPlanRequestAsync(database, normal, "permanent_failure");
        if (replacement is not null)
        {
            await RestorePrecedingTerminalTransitionForGuardControlAsync(database);
            (await write.ExecuteNonQueryAsync()).Should().Be(1,
                "without only the new guard the same sixth admission can launder permanent failure into exhaustion");
        }
    }

    [Theory]
    [InlineData("current_absent")]
    [InlineData("prior_absent")]
    [InlineData("prior_future")]
    public async Task Terminal_storage_requires_complete_positive_no_start_history(string attack)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services,
            attack == "current_absent" ? 1 : 6, store, runtime);
        var acquired = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        if (attack != "current_absent")
        {
            await CompleteTerminalGuardNoStartAsync(host.Services, acquired, runtime);
            // Plant only malformed historical evidence. Restore FORCE and every
            // attempt trigger before exercising the real control-role boundary.
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER TABLE background_job_attempts NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE background_job_attempts DISABLE TRIGGER USER
                """);
            var changed = attack == "prior_absent" ? "NULL" : "clock_timestamp()+interval '1 day'";
            await SchemaQueries.ExecuteAsync(owner, null,
                "UPDATE background_job_attempts SET no_start_at=" + changed + " WHERE job_id=@id AND ordinal=1",
                ("id", normal.Envelope.JobId.Value));
            await SchemaQueries.ExecuteAsync(owner, null, """
                ALTER TABLE background_job_attempts ENABLE TRIGGER USER;
                ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY
                """);
        }
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        var complete = async () => await SchemaQueries.ExecuteAsync(control, null,
            "UPDATE background_jobs SET dead_lettered_at=clock_timestamp() WHERE id=@id", ("id", normal.Envelope.JobId.Value));
        var refused = (await complete.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refused.MessageText.Should().Be("no-plan terminal completion requires complete positive no-start history");
        await RestorePrecedingTerminalTransitionForGuardControlAsync(database);
        await complete();
        (await SchemaQueries.CountAsync(control, "SELECT count(*) FROM background_jobs WHERE dead_lettered_at IS NOT NULL"))
            .Should().Be(1, "removing only the new guard exposes the same malformed history");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Terminal_storage_accepts_complete_actual_app_no_start_history(int ordinal)
    {
        // Raw status SQL isolates the DDL gate. It does not claim terminal
        // runtime/audit delivery, which uses the separately owned MUST writer.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services, ordinal, store, runtime);
        var acquired = await store.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.CommitAcknowledged.Should().BeTrue();
        await CompleteTerminalGuardNoStartAsync(host.Services, acquired, runtime);
        await AssertTerminalNoStartPairAsync(database, normal, acquired.Value!.RecoveryToken);
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null,
            "UPDATE background_jobs SET dead_lettered_at=clock_timestamp() WHERE id=@id", ("id", normal.Envelope.JobId.Value));
        var after = await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, normal.Envelope.JobId);
        after!.NormalAttempts.Should().Be(ordinal);
        after.Phase.Should().Be("terminal_pending");
        after.OwnershipToken.Should().BeNull();
        (await SchemaQueries.CountAsync(control, """
            SELECT count(*) FROM background_jobs WHERE dead_lettered_at IS NOT NULL AND terminal_reason='permanent_failure'
            """)).Should().Be(1);
    }

    [Theory]
    [InlineData("retired_definition", true)]
    [InlineData("invalid_envelope", true)]
    [InlineData("permanent_failure", false)]
    public async Task Terminal_storage_distinguishes_unadmitted_retirement_from_invented_failure(string reason, bool valid)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await ExternalRetryJobAsync(database);
        await using var control = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(control, null, """
            UPDATE background_jobs SET phase='terminal_pending',terminal_reason=@reason WHERE id=@id;
            UPDATE background_jobs SET ownership_kind='recovery',ownership_token=@token,
                ownership_until=clock_timestamp()+interval '1 minute' WHERE id=@id
            """, ("id", job.Value), ("reason", reason), ("token", Guid.NewGuid()));
        var complete = async () => await SchemaQueries.ExecuteAsync(control, null,
            "UPDATE background_jobs SET dead_lettered_at=clock_timestamp() WHERE id=@id", ("id", job.Value));
        if (valid) await complete();
        else
        {
            var refused = (await complete.Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refused.MessageText.Should().Be("unadmitted terminal completion cannot invent attempt history");
            await RestorePrecedingTerminalTransitionForGuardControlAsync(database);
            await complete();
        }
        (await SchemaQueries.CountAsync(control, "SELECT count(*) FROM background_jobs WHERE dead_lettered_at IS NOT NULL"))
            .Should().Be(1);
        (await SchemaQueries.CountAsync(control, "SELECT count(*) FROM background_job_attempts")).Should().Be(0);
        (await SchemaQueries.CountAsync(control, "SELECT count(*) FROM background_job_reservations")).Should().Be(0);
    }

    private static async Task<ConfirmedStartReceipt> RequestedTerminalGuardFixtureAsync(DisposableSchemaDatabase database,
        IServiceProvider services, int ordinal, PostgresBackgroundJobControlStore store, BackgroundJobRuntimeGate runtime)
    {
        var normal = await NoPlanOrdinalAsync(database, services, ordinal, store, runtime);
        var prepared = await services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(normal,
            normal.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.KnownRollback.Should().BeTrue();
        prepared.CleanupFailed.Should().BeFalse();
        prepared.Decision.TerminalReason.Should().Be(BackgroundJobTerminalReason.PermanentFailure);
        var requested = await store.RequestNoPlanExternalTerminalAsync(BackgroundJobExecutionIdentity.FromReceipt(normal),
            BackgroundJobTerminalReason.PermanentFailure, CancellationToken.None);
        requested.ThrowIfFailed();
        requested.CommitAcknowledged.Should().BeTrue();
        requested.CleanupConfirmed.Should().BeTrue();
        normal.Run.Close();
        return normal;
    }

    private static async Task CompleteTerminalGuardNoStartAsync(IServiceProvider services,
        OwnedBackgroundJobExternalRecovery acquired, BackgroundJobRuntimeGate runtime)
    {
        var recovery = BindRecovery(acquired, runtime);
        try
        {
            var executor = services.GetRequiredService<JobExecutor>();
            var read = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
            read.CommitAcknowledged.Should().BeTrue();
            read.CleanupFailed.Should().BeFalse();
            var written = await executor.RecordExternalTerminalNoStartAsync(recovery, read.Handle!,
                recovery.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
            written.CommitAcknowledged.Should().BeTrue();
            written.CleanupFailed.Should().BeFalse();
            recovery.SourceOwner.CleanupConfirmed.Should().BeTrue();
        }
        finally { recovery.Run.Close(); recovery.SourceOwner.CloseIssuance(); }
    }

    private static async Task RestorePrecedingTerminalTransitionForGuardControlAsync(DisposableSchemaDatabase database)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var prior = new add_external_job_terminal_history_guard().DownOperations.OfType<SqlOperation>().Should().ContainSingle().Which;
        await SchemaQueries.ExecuteAsync(owner, null, prior.Sql);
    }
}
