using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("external", true, true)]
    [InlineData("external", false, false)]
    [InlineData("transactional", true, false)]
    public async Task Terminal_witness_requires_actual_returned_profile_and_external_proof_to_agree(
        string projectedProfile, bool projectedProof, bool valid)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var host = NoPlanRefusalHost(database);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var control = new PostgresBackgroundJobControlStore(source, Fixture.Registry, new(), new SystemGuidFactory(), runtime);
        var terminal = new PostgresBackgroundJobTerminalStore(source, host.Services.GetRequiredService<IServiceScopeFactory>(),
            new(), new SystemGuidFactory(), runtime);
        var normal = await RequestedTerminalGuardFixtureAsync(database, host.Services, 1, control, runtime);
        var cleanup = await control.AcquireExternalTerminalNoStartAsync([], CancellationToken.None);
        cleanup.ThrowIfFailed();
        await CompleteTerminalGuardNoStartAsync(host.Services, cleanup, runtime);
        await ExpireCleanTerminalOwnerForFixtureAsync(database, normal);
        var acquired = await terminal.TryClaimTerminalAsync([]);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();

        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE background_jobs j SET dead_lettered_at=clock_timestamp()
            WHERE j.id=@id AND j.ownership_token=@token AND j.ownership_until>clock_timestamp()
            RETURNING j.id,j.tenant_id,j.organization_id,j.definition_key,j.queue,j.wake_generation,
                j.current_attempt_id,j.normal_attempts,j.terminal_reason,j.dead_lettered_at,
                old.ownership_token,@profile::text,@proof::boolean
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", normal.Envelope.JobId.Value);
        command.Parameters.AddWithValue("token", acquired.Value!.Token);
        command.Parameters.AddWithValue("profile", projectedProfile);
        command.Parameters.AddWithValue("proof", projectedProof);
        await using (var returned = await command.ExecuteReaderAsync())
        {
            (await returned.ReadAsync()).Should().BeTrue("the control must reach actual UPDATE RETURNING on the live transaction");
            var mint = () => TerminalWitnessControl.Mint(connection, transaction, returned, normal.Envelope.Traceparent);
            if (valid) mint().JobId.Should().Be(normal.Envelope.JobId.Value);
            else mint.Should().Throw<ArgumentException>().WithMessage("The bypass terminal UPDATE did not prove*");
        }
        // This test-only projection is a witness control, not a second terminal
        // writer. Never commit a status transition without its MUST audit.
        await transaction.RollbackAsync();
        (await ReadNoPlanTerminalStateAsync(database, normal)).Should().BeFalse();
        (await ReadNoPlanTerminalAuditAsync(database, normal)).Should().BeNull();
        var completed = await terminal.TryTerminalizeAsync(acquired.Value!, normal.Envelope.Traceparent);
        completed.ThrowIfFailed();
        completed.CommitAcknowledged.Should().BeTrue();
        await AssertNoPlanTerminalAuditAsync(database, normal, acquired.Value!);
    }

    private sealed class TerminalWitnessControl : JobTerminalTransition.MintingSource
    {
        internal static JobTerminalTransition Mint(DbConnection connection, DbTransaction transaction,
            DbDataReader returned, string traceparent) => FromBypassUpdate(connection, transaction, returned, traceparent);
    }
}
