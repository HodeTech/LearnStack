using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Theory]
    [InlineData("ack")]
    [InlineData("confirmed")]
    [InlineData("changed_owner")]
    public async Task Actual_retry_owner_counts_ACK_or_locked_confirmation_once_but_never_a_later_owner(string outcome)
    {
        await using var database = await DatabaseAsync();
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            RenewalInterval = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromMinutes(3),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
        };
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) => Task.FromResult(Result<None>.Fail(new Error(new LocalizedMessage("lockey_concurrency_conflict")))),
        };
        await using var host = Fixture.Host(database, probe, configure: services => services.AddSingleton(bounds));
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: outcome == "ack" ? int.MaxValue : 0, holdCommitResponse: true);
        await using var source = Source(proxy.ConnectionString);
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["transactional"], gate);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = (await routing.TryClaimAsync(job, 1))!;
        var work = generation.TryBegin()!;
        var execution = controller.TryBegin(claim, work)!;
        work.HoldExecution(execution);
        var admission = executor.ExecutePublishedAsync(claim, execution.Publication, execution.AdmissionGate, null, work.Token);
        work.HoldAdmission(admission);
        execution.HoldAdmission(admission);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var started = await admission.WaitAsync(deadline.Token);
            started.CleanupFailed.Should().BeFalse();
            await execution.WaitForPublicationAsync().WaitAsync(deadline.Token);
            var business = executor.ExecuteBusinessAsync(started.Receipt!, execution.Gate, execution.Token);
            execution.HoldBusiness(business);
            work.HoldBusiness(business);
            var result = await business.WaitAsync(deadline.Token);
            result.KnownRollback.Should().BeTrue();
            result.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Retry);
            // Only the existing control lane can now write: the completed
            // handler's actual deadline/renewal observers have been joined.
            (await execution.JoinBusinessObserversAsync()).Should().BeTrue();
            var completion = controller.FinishBusinessAsync(execution, result);
            controller.Snapshot.Retries.Should().Be(0);
            controller.Start();
            if (outcome != "ack")
            {
                await proxy.CommitResponseHeld.WaitAsync(deadline.Token);
                proxy.CompletedCommits.Should().Be(1);
                var durable = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                durable.Phase.Should().Be("pending");
                await using (var generationRead = new NpgsqlCommand("SELECT wake_generation FROM background_jobs WHERE id=@id", locker))
                {
                    generationRead.Parameters.AddWithValue("id", job.Value);
                    (await generationRead.ExecuteScalarAsync(deadline.Token)).Should().Be(2L);
                }
                controller.Snapshot.Retries.Should().Be(0, "a held/lost physical ACK supplies no observation yet");
                if (outcome == "changed_owner")
                {
                    await DispatchAsync(database, "UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", ("id", job.Value));
                    var replacement = (await routing.TryClaimAsync(job, 2))!;
                    replacement.Should().NotBeNull();
                    var next = await executor.ExecuteAsync(replacement, BackgroundJobTestRun.Create());
                    next.Receipt!.NormalAttemptOrdinal.Should().Be(2);
                }
                else
                {
                    locked = await locker.BeginTransactionAsync(deadline.Token);
                    await using var hold = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", locker, locked);
                    hold.Parameters.AddWithValue("id", job.Value);
                    (await hold.ExecuteScalarAsync(deadline.Token)).Should().Be(job.Value);
                }
                proxy.ReleaseHeldResponse();
                if (locked is not null)
                {
                    // Force a real waiter; neither snapshots nor a changed
                    // phase can substitute for the original locked probe.
                    await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                    await using var wait = new NpgsqlCommand("""
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                            AND usename='learnstack_outbox_admin' AND @blocker=ANY(pg_blocking_pids(pid))
                            AND query LIKE '%FOR UPDATE%')
                        """, observer);
                    wait.Parameters.AddWithValue("blocker", locker.ProcessID);
                    while (!(bool)(await wait.ExecuteScalarAsync(deadline.Token))!) await Task.Delay(10, deadline.Token);
                    completion.IsCompleted.Should().BeFalse();
                    controller.Snapshot.Retries.Should().Be(0);
                    await locked.RollbackAsync(deadline.Token);
                    await locked.DisposeAsync();
                    locked = null;
                }
            }
            (await completion.WaitAsync(deadline.Token)).Should().BeTrue();
            var expected = outcome == "changed_owner" ? 0 : 1;
            for (var index = 0; index < 3; index++) controller.Snapshot.Retries.Should().Be(expected);
            proxy.DroppedCommitResponses.Should().Be(outcome == "ack" ? 0 : 1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job))
                .Should().Be(outcome == "changed_owner" ? 2 : 1);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
            gate.StopStarts();
            controller.RequestStop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var joined = await execution.JoinAsync(operationCleanupConfirmed: true);
            await work.CompleteAsync(joined);
            generation.CloseStarts();
            await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
