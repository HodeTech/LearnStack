using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_capacity_checkpoint_lost_ACK_uses_exact_committed_cleanup_and_release_history_without_a_second_start()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 2, holdCommitResponse: true);
        var calls = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                proxy.CompletedCommits.Should().Be(2, "read and admission committed; the refused business transaction cannot COMMIT");
                return Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_business_rule_violation"))));
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
        var controlName = "capacity_checkpoint_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        Exception? testFailure = null;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            await EnsureQualificationStartAsync(runtime, source, job, proxy.CommitResponseHeld);
            await proxy.CommitResponseHeld.WaitAsync(TestWait);
            proxy.CompletedCommits.Should().Be(3, "the held COMMIT is the actual app cleanup checkpoint after a rolled-back business owner");
            await AssertNativeCapacityReleaseAsync(database, job, "transactional_rollback", cleanup: true);
            var execution = NativeCommitExecution(runtime, job);
            execution.Gate.CommitAcknowledged.Should().BeFalse();
            var original = execution.Identity;
            original.Should().NotBeNull();
            locked = await locker.BeginTransactionAsync();
            await LockNativeJobAsync(locker, locked, job, CancellationToken.None);
            proxy.ReleaseHeldResponse();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, CancellationToken.None);
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            execution.Identity.Should().BeSameAs(original);
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            calls.Should().Be(1);
            await locked.RollbackAsync();
            await locked.DisposeAsync();
            locked = null;
            await EventuallyAsync(async () => runtime.Snapshot.Control!.Executions == 0
                && (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))?.Phase == "terminal_pending");
            await AssertNativeCapacityReleaseAsync(database, job, "transactional_rollback", cleanup: true);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            calls.Should().Be(1);
            probe.Constructions.Should().Equal("CapacityRoot");
        }
        catch (Exception failure) { testFailure = failure; }
        finally
        {
            await PreserveNativeBodyFailureAsync(testFailure, async () =>
            {
                proxy.ReleaseHeldResponse();
                if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
                await runtime.StopAsync().WaitAsync(TestWait);
                await runtime.Completion.WaitAsync(TestWait);
                await DisposePinnedSourceAsync(source);
            });
        }
    }
}
