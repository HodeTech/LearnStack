using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData("ack")]
    [InlineData("confirmed")]
    [InlineData("changed_owner")]
    public async Task Owned_external_retry_counts_only_its_acknowledged_or_exact_locked_original_disposition(string result)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, "repeatable");
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(2),
            RenewalInterval = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromMinutes(3),
        };
        await using var host = Fixture.Host(database, provider.Settings, configure: services => services.AddSingleton(bounds));
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: result == "ack" ? int.MaxValue : 0, holdCommitResponse: true);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], gate);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var controller = new BackgroundJobControl(source, Fixture.Registry, executor, gate, bounds,
            new SystemGuidFactory(), TimeProvider.System);
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
        Task<bool>? actual = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var started = await admission.WaitAsync(deadline.Token);
            started.CleanupFailed.Should().BeFalse();
            await execution.WaitForPublicationAsync().WaitAsync(deadline.Token);
            // No controller lane or native bridge is started. The actual
            // execution owner alone issues the first privileged retry COMMIT.
            actual = controller.ExecuteExternalNormalAsync(execution);
            if (result != "ack")
            {
                await proxy.CommitResponseHeld.WaitAsync(deadline.Token);
                proxy.CompletedCommits.Should().Be(1);
                var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                row.Phase.Should().Be("pending");
                row.NormalAttempts.Should().Be(1);
                controller.Snapshot.Retries.Should().Be(0);
                if (result == "changed_owner")
                {
                    await using var due = new NpgsqlCommand("UPDATE background_jobs SET available_after=clock_timestamp() WHERE id=@id", locker);
                    due.Parameters.AddWithValue("id", job.Value);
                    (await due.ExecuteNonQueryAsync(deadline.Token)).Should().Be(1);
                    var nextClaim = (await routing.TryClaimAsync(job, 2))!;
                    nextClaim.Should().NotBeNull();
                    var next = await executor.ExecuteAsync(nextClaim, BackgroundJobTestRun.Create());
                    next.Receipt!.NormalAttemptOrdinal.Should().Be(2);
                    next.Receipt.Run.Close();
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
                    await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                    await using var wait = new NpgsqlCommand("""
                        SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                            AND usename='learnstack_outbox_admin' AND @blocker=ANY(pg_blocking_pids(pid))
                            AND query LIKE '%FOR UPDATE%')
                        """, observer);
                    wait.Parameters.AddWithValue("blocker", locker.ProcessID);
                    while (!(bool)(await wait.ExecuteScalarAsync(deadline.Token))!) await Task.Delay(10, deadline.Token);
                    actual.IsCompleted.Should().BeFalse();
                    controller.Snapshot.Retries.Should().Be(0);
                    await locked.RollbackAsync(deadline.Token);
                    await locked.DisposeAsync();
                    locked = null;
                }
            }
            (await actual.WaitAsync(deadline.Token)).Should().BeTrue();
            var retry = await (Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>)NativeSdkField(execution, "_externalRetry");
            retry.CleanupConfirmed.Should().BeTrue();
            retry.CommitAcknowledged.Should().Be(result == "ack");
            retry.CommitUncertain.Should().Be(result != "ack");
            if (result != "ack")
            {
                var history = await (Task<OwnedBackgroundJobControl<BackgroundJobProbeResult>>)NativeSdkField(execution, "_externalRetryProbe");
                history.CleanupConfirmed.Should().BeTrue();
                history.Failed.Should().BeFalse();
                history.Value!.Disposition.Should().Be(result == "confirmed"
                    ? BackgroundJobProbeDisposition.ControlCommitted : BackgroundJobProbeDisposition.ChangedOwner);
            }
            controller.Snapshot.Retries.Should().Be(result == "changed_owner" ? 0 : 1);
            proxy.DroppedCommitResponses.Should().Be(result == "ack" ? 0 : 1);
            var checkpoint = await (Task<JobExternalCheckpointResult>)NativeSdkField(execution, "_externalOutcome");
            checkpoint.CommitAcknowledged.Should().BeTrue();
            (await provider.CountsAsync(checkpoint.Outcome!.OperationId)).Should().Be((1L, 0L, 0L));
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        }
        finally
        {
            proxy.ReleaseHeldResponse();
            if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
            if (actual is not null) await actual.WaitAsync(TimeSpan.FromSeconds(30));
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
