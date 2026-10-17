using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    public async Task Native_unresolved_reports_keep_occupancy_without_outcome_audit_or_another_Execute(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await provider.SetModeAsync(resource, mode);
        var bounds = new BackgroundJobRuntimeOptions
        {
            ExternalWaitTimeout = TimeSpan.FromSeconds(4),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
            QuiescenceGrace = TimeSpan.FromMilliseconds(50),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, applicationPoolSize: 24);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            using var complete = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            while ((await NativeUnresolvedFactsAsync(database, job)).Phase != "quarantined"
                || runtime.Snapshot.Control!.Executions != 0
                || runtime.Snapshot.Control is not { HasQuarantinedJobs: true, QuarantineObservationHealthy: true })
                await Task.Delay(20, complete.Token);
            var facts = await NativeUnresolvedFactsAsync(database, job);
            facts.Should().Be(new NativeUnresolvedFacts("quarantined", 0, 1, 0, 1, 0, 0, 0, facts.Operation, "unknown_outcome"));
            var counts = await provider.CountsAsync(facts.Operation);
            counts.Executes.Should().Be(1);
            if (mode == "unknown") counts.Reconciles.Should().Be(0);
            else counts.Reconciles.Should().BeGreaterThanOrEqualTo(1,
                "a Running probe within grace may legitimately be paced once more before containment");
            counts.Effects.Should().Be(0);
            (await provider.CancellationRequestsAsync(facts.Operation)).Should().Be(mode == "unknown" ? 0 : 1,
                "an expired original deadline requires an actual provider cancellation request, not a pre-cancelled recovery token");
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
            var quarantinedHealth = await QuarantineAsOnlyHealthFaultAsync(runtime, complete.Token);
            quarantinedHealth.IsHealthy.Should().BeFalse("quarantine remains unhealthy after its actual execution cleaned up");
            var health = await new LearnStack.Api.Jobs.BackgroundJobHealthCheck(runtime)
                .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
            health.Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
            ((BackgroundJobControlHealth)health.Data["control"]).HasQuarantinedJobs.Should().BeTrue();
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
        if (mode == "unknown")
        {
            // New host, source, gate and facade: no process-local quarantine
            // counter or cached observation can carry across this restart.
            await using var restartedHost = Fixture.Host(database, provider.Settings, configure: services =>
            {
                services.RemoveAll<BackgroundJobRuntimeOptions>();
                services.AddSingleton(bounds);
            }, applicationPoolSize: 24);
            await using var restartedSource = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
            var restarted = NativeSdkRuntime(restartedHost.Services, restartedSource, bounds);
            restarted.Snapshot.Control!.HasQuarantinedJobs.Should().BeNull();
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await restarted.StartAsync(startup.Token);
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var restartedHealth = await QuarantineAsOnlyHealthFaultAsync(restarted, ready.Token);
                restartedHealth.IsHealthy.Should().BeFalse();
                restarted.Snapshot.Control!.Executions.Should().Be(0);
                var retained = await NativeUnresolvedFactsAsync(database, job);
                retained.Phase.Should().Be("quarantined");
                retained.Unreleased.Should().Be(1);
                retained.Normal.Should().Be(1);
                (await provider.CountsAsync(retained.Operation)).Should().Be((1L, 0L, 0L));
            }
            finally
            {
                restarted.RequestStop();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await restarted.StopAsync(cleanup.Token);
                await restarted.Completion.WaitAsync(cleanup.Token);
            }
            restarted.Snapshot.CleanupConfirmed.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("timeout")]
    [InlineData("host_stop")]
    [InlineData("late_success")]
    [InlineData("late_running")]
    public async Task Actual_native_probe_accounts_only_after_provider_and_private_callback_cleanup(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, mode == "late_success" ? "succeeded" : "running");
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var flow = await AdmitPrepareAndArmAsync(database, originalHost, resource,
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var original = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        original.CleanupFailed.Should().BeFalse();
        original.Status.Should().Be(JobExternalExecutionStatus.Reported);
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        // This test arranges only the initial expired eligibility. All recovery
        // acquisition, stage timers, callbacks and dispositions below are real.
        await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));
        var probe = new BackgroundJobPositiveReconcileProbe(mode);
        var bounds = new BackgroundJobRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromSeconds(2),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
            QuiescenceGrace = TimeSpan.FromMilliseconds(50),
        };
        await using var host = Fixture.Host(database, provider.Settings, reconcileProbe: probe);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        var runtimeGate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var time = new NativeProbeClock();
        var control = new BackgroundJobControl(source, Fixture.Registry, host.Services.GetRequiredService<JobExecutor>(),
            runtimeGate, bounds, new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtimeGate, bounds, time);
        var work = generation.TryBegin()!;
        var job = flow.Receipt.Envelope.JobId;
        var execution = control.TryBeginRecovery(job, await GenerationAsync(database, job), flow.Receipt.Envelope.Queue, work)!;
        work.HoldExecution(execution);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        CancellationTokenRegistration? callback = null;
        Task<bool>? actual = null;
        var clean = false;
        try
        {
            control.Start();
            actual = Task.Run(() => control.ExecuteExternalRecoveryAsync(execution));
            await probe.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            probe.Context!.Deadline.Should().Be(flow.Receipt.Deadline);
            probe.Context.AttemptId.Should().Be(flow.Receipt.AttemptId);
            execution.Identity!.Token.Should().NotBe(flow.Receipt.Claim.Token);
            execution.Identity.Deadline.Should().BeBefore(DateTimeOffset.UtcNow);
            if (mode != "fault")
            {
                execution.Token.IsCancellationRequested.Should().BeFalse("fresh recovery may query/cancel after the original deadline");
                callback = execution.Token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Actual probe cancellation callback was not released.");
                });
                if (mode == "host_stop") { runtimeGate.StopStarts(); control.RequestStop(); }
                try { await work.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"Actual stage return signal missing: stage={NativeProbeClock.StageLabel(work)}, "
                        + $"closed={NativeSdkField(work, "_stageWaitClosed")}, cause={work.ReconcileWaitExpired}, "
                        + $"initializing={NativeSdkField(work, "_stageTimerInitializing")}, fault={NativeSdkField(work, "_stageTimerFaulted")}, "
                        + $"cancelled={work.Token.IsCancellationRequested}, callbacks={string.Join(';', time.Callbacks)}");
                }
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                work.ReconcileWaitExpired.Should().Be(mode != "host_stop");
                actual.IsCompleted.Should().BeFalse("the real provider still owns its held method task");
                (await NativeUnresolvedFactsAsync(database, job)).Failures.Should().Be(0);
                probe.Release();
                var providerTask = (Task<JobExternalExecutionResult>)NativeSdkField(execution, "_externalProvider");
                var returned = await providerTask.WaitAsync(TimeSpan.FromSeconds(10));
                returned.CleanupFailed.Should().BeFalse();
                if (mode is "late_success" or "late_running")
                {
                    returned.Status.Should().Be(JobExternalExecutionStatus.Reported);
                    returned.Observation.Should().NotBeNull();
                    returned.Observation!.State.Should().Be(mode == "late_success"
                        ? BackgroundJobExternalState.Succeeded : BackgroundJobExternalState.Running);
                    returned.Observation.IsCompleted.Should().Be(mode == "late_success");
                    returned.Observation.JobId.Should().Be(flow.Receipt.Envelope.JobId);
                    returned.Observation.AttemptId.Should().Be(flow.Receipt.AttemptId);
                    returned.Observation.OperationId.Should().Be(flow.Operation);
                }
                if (mode == "timeout")
                {
                    returned.Status.Should().Be(JobExternalExecutionStatus.ProviderFault);
                    // The provider's own scope/registration can be clean while
                    // another actual private-token callback is still retained.
                    actual.IsCompleted.Should().BeFalse();
                    typeof(BackgroundJobExecutionControl).GetField("_externalUnresolved", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(execution).Should().BeNull();
                    (await NativeUnresolvedFactsAsync(database, job)).Failures.Should().Be(0);
                }
                releaseCallback.Set();
            }
            clean = await actual.WaitAsync(TimeSpan.FromSeconds(15));
            clean.Should().BeTrue();
            var after = await NativeUnresolvedFactsAsync(database, job);
            after.Normal.Should().Be(1);
            after.Finalize.Should().Be(0);
            after.Unreleased.Should().Be(1);
            after.Outcomes.Should().Be(0);
            after.NoStarts.Should().Be(0);
            after.Audits.Should().Be(0);
            after.Failures.Should().Be(mode is "fault" or "timeout" ? 1 : 0);
            after.Phase.Should().Be(mode == "late_running" ? "quarantined" : "reconciling");
            after.Quarantine.Should().Be(mode == "late_running" ? "unknown_outcome" : null);
            var counts = await provider.CountsAsync(flow.Operation);
            counts.Should().Be((1L, 1L, mode == "late_success" ? 1L : 0L));
            (await provider.CancellationRequestsAsync(flow.Operation)).Should().Be(1);
            var statusTask = typeof(BackgroundJobExecutionControl).GetField("_externalUnresolved", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(execution) as Task<OwnedBackgroundJobControl<BackgroundJobControlChange>>;
            if (mode is "host_stop" or "late_success") statusTask.Should().BeNull();
            else
            {
                statusTask.Should().NotBeNull();
                (await statusTask!).CommitAcknowledged.Should().BeTrue();
                (await statusTask).CleanupConfirmed.Should().BeTrue();
            }
        }
        finally
        {
            probe.Release();
            releaseCallback.Set();
            if (callback is { } registered) await registered.DisposeAsync();
            if (actual is not null) clean = await actual.WaitAsync(TimeSpan.FromSeconds(15));
            await work.CompleteAsync(await execution.JoinAsync(clean));
            generation.CloseStarts();
            control.RequestStop();
            await control.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }
        await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        control.Snapshot.Executions.Should().Be(0);
    }

    private static async Task<BackgroundJobRuntimeSnapshot> QuarantineAsOnlyHealthFaultAsync(
        BackgroundJobRuntime runtime, CancellationToken token)
    {
        while (true)
        {
            var snapshot = runtime.Snapshot;
            if (snapshot.Control is { HasQuarantinedJobs: true, QuarantineObservationHealthy: true, Executions: 0 } observed
                && (snapshot with { Control = observed with { HasQuarantinedJobs = false } }).IsHealthy)
                return snapshot;
            // Only this immutable test comparison substitutes the quarantine bit;
            // all actual pool/bridge/maintenance/progress state must be healthy.
            await Task.Delay(20, token);
        }
    }

    private sealed record NativeUnresolvedFacts(string Phase, int Failures, int Normal, int Finalize,
        long Unreleased, long Outcomes, long NoStarts, long Audits, Guid Operation, string? Quarantine);

    private sealed class NativeProbeClock : TimeProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Callbacks { get; } = new();
        internal static string StageLabel(object invocation) => invocation.GetType()
            .GetField("_stageWaitStage", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(invocation)?.ToString() ?? "prospective";
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(actual =>
            {
                if (actual is HangfireGenerationWork.Invocation invocation)
                {
                    var at = GetTimestamp();
                    var until = (long)NativeSdkField(invocation, "_stageWaitUntil");
                    Callbacks.Enqueue($"{StageLabel(invocation)}:{GetElapsedTime(at, until).TotalMilliseconds:F3}ms_remaining");
                }
                callback(actual);
            }, state, dueTime, period);
    }

    private static async Task<NativeUnresolvedFacts> NativeUnresolvedFactsAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.unsuccessful_probes,j.normal_attempts,j.finalize_attempts,
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id AND r.released_at IS NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.outcome_kind IS NOT NULL),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                (SELECT count(*) FROM audit_log a WHERE a.operation='platform.job.dead_letter' AND a.metadata->>'job_id'=@text),
                j.operation_id,j.quarantine_reason FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        command.Parameters.AddWithValue("text", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3), row.GetInt64(4),
            row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.IsDBNull(8) ? Guid.Empty : row.GetGuid(8), row.IsDBNull(9) ? null : row.GetString(9));
    }
}
