using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData("live")]
    [InlineData("expired")]
    [InlineData("lost")]
    public async Task Recovery_first_publication_requires_actual_probe_ack_and_cleanup_without_rebasing_its_observed_window(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        await using var host = RecoveryHost(database);
        var original = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        await ParkExpiredExternalAsync(database, original);
        var options = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(20),
            RenewalInterval = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromSeconds(35),
        };
        options.Validate();
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        await using var acquisitionProxy = new CommitResponseDroppingProxy(database.OutboxConnectionString);
        await using var acquisitionSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(acquisitionProxy.ConnectionString));
        var acquire = new PostgresBackgroundJobControlStore(acquisitionSource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var unknown = await acquire.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
            original.Envelope.Queue, CancellationToken.None);
        acquisitionProxy.DroppedCommitResponses.Should().Be(1);
        unknown.CommitAcknowledged.Should().BeFalse();
        unknown.CommitUncertain.Should().BeTrue();
        unknown.CleanupConfirmed.Should().BeTrue();
        unknown.Value.Should().BeNull();
        DateTimeOffset until;
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        await using (var command = new NpgsqlCommand("SELECT ownership_until FROM background_jobs WHERE id=@id", (NpgsqlConnection)connection)
        { CommandTimeout = 5 })
        {
            command.Parameters.AddWithValue("id", job.Value);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            until = row.GetFieldValue<DateTimeOffset>(0);
        }
        if (mode == "expired") await WaitForRecoveryClockAsync(database, until.AddSeconds(-2));
        await using var probeProxy = new CommitResponseDroppingProxy(database.OutboxConnectionString, lifetime: TimeSpan.FromSeconds(40));
        probeProxy.HoldNextExternalRecoveryRollbackResponse(mode == "lost");
        await using var probeSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(probeProxy.ConnectionString));
        var store = new PostgresBackgroundJobControlStore(probeSource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        var probing = store.ProbeExternalRecoveryAsync(unknown, unknown.Probes.Single(), CancellationToken.None);
        Exception? premiseFailure = null;
        try
        {
            await probeProxy.ExternalRecoveryRollbackHeld.WaitAsync(TimeSpan.FromSeconds(10));
            probing.IsCompleted.Should().BeFalse();
            unknown.Value.Should().BeNull("server lock release is not the client's rollback acknowledgement");
            await using (var connection = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
                await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
                await using var locked = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE SKIP LOCKED",
                    (NpgsqlConnection)connection, (NpgsqlTransaction)transaction)
                { CommandTimeout = 5 };
                locked.Parameters.AddWithValue("id", job.Value);
                (await locked.ExecuteScalarAsync()).Should().Be(job.Value, "the real server ROLLBACK already released the lock");
                await transaction.RollbackAsync();
            }
            if (mode == "expired") await WaitForRecoveryClockAsync(database, until);
        }
        catch (Exception failure) { premiseFailure = failure; }
        finally { probeProxy.ReleaseExternalRecoveryRollbackResponse(); }
        // The store owns its finite probe deadline. Even a failed test premise
        // must join that actual task, rather than abandon it after another wait.
        var result = await probing;
        premiseFailure.Should().BeNull("the actual probe is joined before reporting a failed test premise");
        result.CommitAcknowledged.Should().BeFalse();
        result.KnownRollback.Should().BeFalse();
        unknown.CommitAcknowledged.Should().BeFalse();
        await using var healthySource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var healthy = new PostgresBackgroundJobControlStore(healthySource, Fixture.Registry, options, new SystemGuidFactory(), runtime);
        BackgroundJobExternalRecoveryReceipt receipt;
        if (mode == "lost")
        {
            result.Failed.Should().BeTrue();
            result.Value.Should().BeNull();
            unknown.Value.Should().BeNull();
            var later = await healthy.ProbeExternalRecoveryAsync(unknown, unknown.Probes.Single(), CancellationToken.None);
            later.ThrowIfFailed();
            later.Value.Should().NotBeNull();
            receipt = later.Value!;
            result.Failed.Should().BeTrue("a later clean probe never rewrites the first probe's failure or physical ACK");
            result.Value.Should().BeNull();
        }
        else
        {
            result.ThrowIfFailed();
            result.CleanupConfirmed.Should().BeTrue();
            result.Value.Should().NotBeNull();
            receipt = result.Value!;
        }
        unknown.Value.Should().BeSameAs(receipt);
        var run = new BackgroundJobRunGate(runtime.RunStop);
        using (JobApplicationSourceOwner.Create().Enter())
        {
            if (mode == "expired")
            {
                Action bind = () => receipt.Bind(run);
                bind.Should().Throw<InvalidOperationException>();
                run.IsClosed.Should().BeTrue();
            }
            else _ = receipt.Bind(run);
        }
        if (mode != "expired")
        {
            var dbNow = receipt.DbNow;
            var repeated = await healthy.ProbeExternalRecoveryAsync(unknown, unknown.Probes.Single(), CancellationToken.None);
            repeated.ThrowIfFailed();
            repeated.Value.Should().BeSameAs(receipt);
            repeated.Value!.DbNow.Should().Be(dbNow);
            repeated.Value.OwnershipUntil.Should().Be(until);
        }
        await AssertSingleUnresolvedAttemptAsync(database, original);
    }

    private static async Task WaitForRecoveryClockAsync(DisposableSchemaDatabase database, DateTimeOffset until)
    {
        var waiting = Stopwatch.StartNew();
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT clock_timestamp()>=@until", (NpgsqlConnection)connection)
            { CommandTimeout = 5 };
            command.Parameters.AddWithValue("until", until);
            if (await command.ExecuteScalarAsync() is true) return;
            if (waiting.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("The actual recovery clock did not advance.");
            await Task.Delay(10);
        }
    }
}
