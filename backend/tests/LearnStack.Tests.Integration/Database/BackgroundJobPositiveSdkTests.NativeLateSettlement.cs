using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_stock_recovery_counts_late_settlement_only_after_provider_and_private_cleanup(bool hostStop)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, "succeeded");
        await using var originalHost = ShortRecoveryOriginalHost(database, provider.Settings);
        var flow = await AdmitPrepareAndArmAsync(database, originalHost, resource,
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var original = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        original.CleanupFailed.Should().BeFalse();
        original.Observation.Should().NotBeNull();
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));

        var probe = new BackgroundJobPositiveReconcileProbe("late_success");
        var bounds = new BackgroundJobRuntimeOptions { ProbeTimeout = TimeSpan.FromSeconds(5) };
        await using var host = Fixture.Host(database, provider.Settings, reconcileProbe: probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        using var metrics = new BackgroundJobMetricProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration? callback = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(deadline.Token);
            await probe.Entered.WaitAsync(deadline.Token);
            // This is the actual stock worker's bound invocation, not a direct
            // synthetic generation ticket or a manufactured framework Task.
            var work = NativeSdkInvocation(runtime);
            var execution = (BackgroundJobExecutionControl)NativeSdkField(work, "_execution");
            execution.Identity!.IsRecovery.Should().BeTrue();
            callback = work.Token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Late settlement callback was not released.");
            });
            if (hostStop) runtime.RequestStop();
            await work.ReturnRequested.WaitAsync(deadline.Token);
            await callbackEntered.Task.WaitAsync(deadline.Token);
            runtime.Snapshot.Control!.LateSettlements!.Reconcile.Should().Be(0);
            var actual = (Task<JobExternalExecutionResult>)NativeSdkField(execution, "_externalProvider");
            actual.IsCompleted.Should().BeFalse();
            probe.Release();
            var result = await actual.WaitAsync(deadline.Token);
            result.CleanupFailed.Should().BeFalse();
            result.ProviderIssued.Should().BeTrue();
            result.Observation!.IsCompleted.Should().BeTrue();
            runtime.Snapshot.Control.Executions.Should().Be(1);
            runtime.Snapshot.Control.LateSettlements.Reconcile.Should().Be(0,
                "the real typed provider result cannot replace held private callback cleanup");
            BackgroundJobMetricProbe.One(metrics.Read(), "learnstack_job_control_total", ("operation", "late_reconcile_settled"), ("pool", "none")).Should().Be(0);
            releaseCallback.Set();
            while (runtime.Snapshot.Control!.Executions != 0
                || runtime.Snapshot.Control.LateSettlements!.Reconcile != (hostStop ? 0 : 1))
                await Task.Delay(10, deadline.Token);
            for (var scrape = 0; scrape < 2; scrape++)
            {
                var points = metrics.Read();
                BackgroundJobMetricProbe.One(points, "learnstack_job_control_total", ("operation", "late_reconcile_settled"), ("pool", "none"))
                    .Should().Be(hostStop ? 0 : 1);
                points.Where(point => point.Name == "learnstack_job_control_total")
                    .Should().OnlyContain(point => point.Tags.Count == 2 && point.Tags.ContainsKey("operation") && point.Tags.ContainsKey("pool"));
            }
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, execution.JobId))!.NormalAttempts.Should().Be(1);
        }
        finally
        {
            probe.Release();
            releaseCallback.Set();
            if (callback is { } actualCallback) await actualCallback.DisposeAsync();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.Control!.LateSettlements!.Reconcile.Should().Be(hostStop ? 0 : 1);
        (await provider.CountsAsync(flow.Operation)).Executes.Should().Be(1);
    }
}
