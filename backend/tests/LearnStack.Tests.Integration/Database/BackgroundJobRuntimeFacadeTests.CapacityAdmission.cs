using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_capacity_completion_releases_with_business_child_outbox_and_MUST_even_when_business_ACK_is_lost(bool loseAck)
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: loseAck ? 2 : int.MaxValue, holdCommitResponse: loseAck, lifetime: TimeSpan.FromMinutes(2));
        var effects = new NativeCommitEffects();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (request, services, token) =>
            {
                request.Should().Be("CapacityRoot");
                proxy.CompletedCommits.Should().Be(2, "the real read and counted capacity admission precede the business owner");
                entered.TrySetResult();
                await StageNativeCommitEffectsAsync(services, effects, token);
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry());
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, appConnectionString: new NpgsqlConnectionStringBuilder(proxy.ConnectionString)
        { Pooling = true, MaxPoolSize = 100 }.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var controlName = "capacity_commit_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await EnsureQualificationStartAsync(runtime, source, job, entered.Task);
            if (loseAck)
            {
                await proxy.CommitResponseHeld.WaitAsync(TestWait);
                proxy.CompletedCommits.Should().Be(3);
                await AssertNativeCapacityReleaseAsync(database, job, "completed", cleanup: false);
                await AssertNativeCommitEffectsAsync(database, effects, committed: true);
                locked = await locker.BeginTransactionAsync();
                await LockNativeJobAsync(locker, locked, job, CancellationToken.None);
                proxy.ReleaseHeldResponse();
                await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, CancellationToken.None);
                var execution = NativeCommitExecution(runtime, job);
                var business = await NativeCommitBusinessTask(execution).WaitAsync(TestWait);
                business.DurableOutcomeUncertain.Should().BeTrue();
                execution.Identity!.JobId.Should().Be(job);
                runtime.Snapshot.Control!.Executions.Should().Be(1);
                await locked.RollbackAsync();
                await locked.DisposeAsync();
                locked = null;
            }
            await EventuallyAsync(async () => await NativeBusinessCompletedAsync(database, source, job)
                && runtime.Snapshot.Control!.Executions == 0);
            await AssertNativeCapacityReleaseAsync(database, job, "completed", cleanup: false);
            await AssertNativeCommitEffectsAsync(database, effects, committed: true);
            probe.Constructions.Should().Equal("CapacityRoot");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
            await runtime.StopAsync().WaitAsync(TestWait);
            await runtime.Completion.WaitAsync(TestWait);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static async Task AssertNativeCapacityReleaseAsync(DisposableSchemaDatabase database, BackgroundJobId job,
        string reason, bool cleanup)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var query = new NpgsqlCommand("""
            SELECT j.normal_attempts, a.transactional_cleanup_at, a.no_start_at, r.released_at, r.release_reason,
                j.processed_at, a.execute_intent_at, a.outcome, a.quiescent_at
            FROM background_jobs j JOIN background_job_attempts a ON a.tenant_id=j.tenant_id AND a.job_id=j.id
            JOIN background_job_reservations r ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE j.id=@job AND j.capacity_group=@group AND r.capacity_group=j.capacity_group
            """, connection, transaction);
        query.Parameters.AddWithValue("job", job.Value);
        query.Parameters.AddWithValue("group", BackgroundJobRuntimeFixture.CapacityGroup);
        await using var row = await query.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetInt32(0).Should().Be(1);
        row.IsDBNull(1).Should().Be(!cleanup);
        row.IsDBNull(2).Should().BeTrue("a handler that ran is never labeled never_started");
        row.IsDBNull(3).Should().BeFalse();
        row.GetString(4).Should().Be(reason);
        row.IsDBNull(5).Should().Be(cleanup);
        for (var index = 6; index < 9; index++) row.IsDBNull(index).Should().BeTrue("transactional release fabricates no external evidence");
        (await row.ReadAsync()).Should().BeFalse();
    }
}
