using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_lifecycle_qualification_resends_after_a_locked_hint_and_confirmed_handoff()
    {
        await using var database = await DatabaseAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return Task.FromResult(Result.Ok(None.Value));
            },
        };
        await using var host = Fixture.Host(database, probe);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var bounds = NativeCommitFaultBounds();
        var runtime = Runtime(host.Services, source, bounds, timeProvider: time);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? held = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy
                && time.DelayCount(bounds.ControlPoll) == 3), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, bounds, new SystemGuidFactory());
            var claim = (await bridge.TryClaimAsync([], deadline.Token))!;
            claim.JobId.Should().Be(job);
            held = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, held, job, deadline.Token);
            var skipped = CreateQualificationWakeup(ActualProvider(runtime), Fixture.Registry.Infrastructure.Pools["transactional"], job);
            await EventuallyQualificationAsync(async () => await StateAsync(source, skipped) == "Succeeded", deadline.Token);
            calls.Should().Be(0, "a successful native hint need not acquire the locked application row");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
            await held.RollbackAsync(deadline.Token);
            await held.DisposeAsync();
            held = null;
            (await bridge.AcknowledgeAsync(claim, skipped, deadline.Token)).Should().BeTrue();
            (await VendorAsync(source, job)).Should().Be(skipped);
            (await bridge.TryClaimAsync([], deadline.Token)).Should().BeNull("the accepted repair grace is still live");

            var resent = await EnsureQualificationStartAsync(runtime, source, job, entered.Task, deadline.Token);
            resent.Should().NotBeNull().And.NotBe(skipped);
            await entered.Task.WaitAsync(deadline.Token);
            await EventuallyQualificationAsync(async () => await StateAsync(source, resent!) == "Succeeded", deadline.Token);
            while (runtime.Snapshot.Control is not { Executions: 0, PendingOutcomes: 0 })
            {
                time.Advance(bounds.ControlPoll);
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            calls.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            if (held is not null) { await held.RollbackAsync(); await held.DisposeAsync(); }
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static async Task<string?> EnsureQualificationStartAsync(BackgroundJobRuntime runtime,
        PrivilegedDataSource source, LearnStack.SharedKernel.Jobs.BackgroundJobId job, Task started,
        CancellationToken cancellationToken = default)
    {
        // These cases qualify native execution/cleanup, not bridge repair timing.
        // An already-started body may itself hold the row needed by ACK. Otherwise
        // observe the committed ACK before sending an additional real stock hint.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TestWait);
        await EventuallyQualificationAsync(async () => started.IsCompleted
            || await VendorAsync(source, job, deadline.Token) is not null, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return started.IsCompleted ? null : CreateQualificationWakeup(ActualProvider(runtime),
            Fixture.Registry.Infrastructure.Pools["transactional"], job);
    }
}
