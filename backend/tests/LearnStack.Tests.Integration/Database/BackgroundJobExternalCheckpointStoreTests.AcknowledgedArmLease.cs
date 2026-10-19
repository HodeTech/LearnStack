using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acknowledged_arm_cannot_open_provider_scope_after_its_actual_sql_lease_window_expires_without_a_timer(bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var construction = new ProviderConstructionProbe();
        await using var host = BridgeCanaryHost(database, construction);
        var receipt = await AdmitFixtureAsync(host.Services, database, await FixtureClaim.ClaimAsync(database, job));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare)))
            .CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        DateTimeOffset until;
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
        await using (var lease = new NpgsqlCommand("""
            UPDATE background_jobs SET ownership_until=clock_timestamp()+@duration
            WHERE id=@id AND current_attempt_id=@attempt AND ownership_token=@token
            RETURNING ownership_until
            """, (NpgsqlConnection)dispatcher))
        {
            lease.Parameters.AddWithValue("duration", expire ? TimeSpan.FromSeconds(6) : TimeSpan.FromMinutes(2));
            lease.Parameters.AddWithValue("id", job.Value);
            lease.Parameters.AddWithValue("attempt", receipt.AttemptId.Value);
            lease.Parameters.AddWithValue("token", receipt.Claim.Token);
            await using var row = await lease.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            until = row.GetFieldValue<DateTimeOffset>(0);
        }
        var arm = await executor.RecordExternalArmAsync(receipt, read.Handle!,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        arm.CleanupFailed.Should().BeFalse();
        if (expire)
        {
            var waiting = Stopwatch.StartNew();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            while (true)
            {
                await using var clock = new NpgsqlCommand("SELECT clock_timestamp()>=@until", (NpgsqlConnection)dispatcher)
                { CommandTimeout = 5 };
                clock.Parameters.AddWithValue("until", until);
                if ((bool)(await clock.ExecuteScalarAsync())!) break;
                if (waiting.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The observed Arm lease did not expire.");
                await Task.Delay(10);
            }
        }
        receipt.Run.IsClosed.Should().BeFalse("no native renewal timer, cancellation or manual stop masks this boundary");
        receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        var (bridge, scopes) = ObservedProviderBridge(host.Services);
        var result = await bridge.ExecuteExternalAsync(read.Handle!, arm, CancellationToken.None);
        result.Status.Should().Be(expire ? JobExternalExecutionStatus.EntryRefused : JobExternalExecutionStatus.ProviderFault,
            "the live fixture throws in its constructor; known expiry never entered the provider");
        result.Observation.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        scopes.Calls.Should().Be(expire ? 0 : 1);
        construction.Calls.Should().Be(expire ? 0 : 1);
        if (!expire) construction.ConstructorCanaryHit.Should().BeTrue();
        receipt.Run.TrySealUnusedExternalExecute().Should().BeTrue("the unchanged adapter canary never invokes a provider method");
        arm.CommitAcknowledged.Should().BeTrue("lease expiry never rewrites a physical acknowledgement");
    }
}
