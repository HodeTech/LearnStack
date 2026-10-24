using System.Globalization;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_native_recovery_owner_renews_past_old_deadlines_without_admission_or_another_Execute(bool loseAcquisitionAck)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        await using var originalHost = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = TimeSpan.FromSeconds(4) });
        });
        var flow = await AdmitPrepareAndArmAsync(database, originalHost, Guid.CreateVersion7(),
            OrganizationId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        var executed = await flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None);
        executed.Status.Should().Be(JobExternalExecutionStatus.Reported);
        flow.Receipt.Run.Close();
        await WaitForDatabaseLeaseAsync(database, flow.Receipt.Deadline);
        // This slice arranges the parked row explicitly. Automatic park/bridge
        // generation and full facade invocation are separate controls.
        await using (var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(connection, null, """
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", flow.Receipt.Envelope.JobId.Value));
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        await using var recoveryHost = Fixture.Host(database, provider.Settings, construction);
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromSeconds(30),
            ProgressTimeout = TimeSpan.FromSeconds(30),
        };
        bounds.Validate();
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: loseAcquisitionAck ? 0 : int.MaxValue, lifetime: TimeSpan.FromSeconds(90));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(proxy.ConnectionString));
        var runtime = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var time = new NativeLeaseTimerObservation();
        var control = new BackgroundJobControl(source, Fixture.Registry, recoveryHost.Services.GetRequiredService<JobExecutor>(),
            runtime, bounds, new SystemGuidFactory(), time);
        var generation = new HangfireGenerationWork(Fixture.Registry.Infrastructure.Pools["external"], runtime, bounds, time);
        var work = generation.TryBegin()!;
        var execution = control.TryBeginRecovery(flow.Receipt.Envelope.JobId, await GenerationAsync(database, flow.Receipt.Envelope.JobId),
            flow.Receipt.Envelope.Queue, work)!;
        work.HoldExecution(execution);
        var executionToken = execution.Token;
        Task<bool>? actual = null;
        var clean = false;
        var boundary = "rooted";
        int? baselineChanges = null;
        ExceptionDispatchInfo? failure = null;
        Exception? cleanupFailure = null;
        string? cleanupBoundary = null;
        void Observe(string record, string at, Exception error)
        {
            static string Count(long? value) => value switch
            {
                null => "unavailable",
                >= 0 and <= 10000 => value.Value.ToString(CultureInfo.InvariantCulture),
                _ => "out_of_range",
            };
            try
            {
                int? changes = null;
                try { changes = time.Timer(execution).Changes; }
                catch (KeyNotFoundException) { }
                var snapshot = control.Snapshot;
                var kind = error switch
                {
                    TimeoutException => "timeout",
                    OperationCanceledException => "cancellation",
                    Xunit.Sdk.XunitException => "assertion",
                    InvalidOperationException => "invalid_state",
                    _ => "other",
                };
                var state = actual switch
                {
                    null => "not_issued",
                    { IsCanceled: true } => "canceled",
                    { IsFaulted: true } => "faulted",
                    { IsCompletedSuccessfully: true } => "succeeded",
                    _ => "pending",
                };
                output.WriteLine("Native recovery " + record + ";boundary=" + at + ";kind=" + kind
                    + ";actual=" + state + ";timerChanges=" + Count(changes)
                    + ";baselineChanges=" + Count(baselineChanges) + ";renewals=" + Count(snapshot.Renewals)
                    + ";renewalLosses=" + Count(snapshot.LeaseLossRenewalRefused)
                    + ";elapsedLosses=" + Count(snapshot.LeaseLossElapsed)
                    + ";runClosed=" + (execution.RunGate.IsClosed ? "true" : "false")
                    + ";tokenCancelled=" + (executionToken.IsCancellationRequested ? "true" : "false")
                    + ";dropped=" + Count(proxy.DroppedCommitResponses)
                    + ";clean=" + (clean ? "true" : "false") + ".");
            }
            catch (Exception errorWhileObserving) when (NativeWaitCatchable(errorWhileObserving)) { }
        }
        void Preserve(Exception error, string at)
        {
            if (failure is not null) return;
            failure = ExceptionDispatchInfo.Capture(error);
            Observe("failure", at, error);
        }
        async Task CleanupAsync(string at, Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception error) when (NativeWaitCatchable(error))
            {
                if (cleanupFailure is null) { cleanupFailure = error; cleanupBoundary = at; }
                Preserve(error, at);
            }
        }
        try
        {
            control.Snapshot.Executions.Should().Be(1, "the native owner is rooted before any recovery SQL");
            execution.WaitForPublicationAsync().IsCompleted.Should().BeTrue("there is no unfinished admission publication");
            actual = Task.Run(() => control.ExecuteExternalRecoveryAsync(execution));
            boundary = "construction";
            await construction.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            boundary = "identity";
            execution.Identity.Should().NotBeNull();
            var identity = execution.Identity!;
            identity.IsRecovery.Should().BeTrue();
            identity.Token.Should().NotBe(flow.Receipt.Claim.Token);
            identity.AdmissionToken.Should().Be(flow.Receipt.Claim.Token);
            identity.AttemptId.Should().Be(flow.Receipt.AttemptId);
            identity.Deadline.Should().BeBefore(DateTimeOffset.UtcNow);
            execution.RunGate.IsClosed.Should().BeFalse();
            proxy.DroppedCommitResponses.Should().Be(loseAcquisitionAck ? 1 : 0);
            var oldLease = identity.OwnershipUntil;
            baselineChanges = time.Timer(execution).Changes;
            boundary = "renewal_wait";
            control.Start();
            using var until = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!until.IsCancellationRequested && (time.Timer(execution).Changes <= baselineChanges.Value || control.Snapshot.Renewals == 0))
                await Task.Delay(10);
            time.Timer(execution).Changes.Should().BeGreaterThan(baselineChanges.Value,
                "the initial timer setup does not prove a later renewal rearmed it");
            boundary = "database_renewal";
            (await NativeLeaseAsync(database, identity.JobId)).Should().BeAfter(oldLease);
            boundary = "old_lease";
            await WaitForDatabaseLeaseAsync(database, oldLease);
            execution.RunGate.IsClosed.Should().BeFalse("actual recovery renewal survives both expired original and acquisition windows");
            execution.Token.IsCancellationRequested.Should().BeFalse();
            boundary = "provider_before";
            (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 0L, 1L));
            construction.Release();
            boundary = "reconcile";
            clean = await actual.WaitAsync(TimeSpan.FromSeconds(15));
            clean.Should().BeTrue();
            boundary = "row_facts";
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, identity.JobId))!.Phase.Should().Be("finalize_pending");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, identity.JobId)).Should().Be(1);
            (await provider.CountsAsync(flow.Operation)).Should().Be((1L, 1L, 1L));
        }
        catch (Exception error) when (NativeWaitCatchable(error)) { Preserve(error, boundary); }
        finally
        {
            await CleanupAsync("cleanup_actual", async () =>
            {
                construction.Release();
                if (actual is not null) clean = await actual;
            });
            await CleanupAsync("cleanup_work", async () => await work.CompleteAsync(await execution.JoinAsync(clean)));
            await CleanupAsync("cleanup_control", async () =>
            {
                generation.CloseStarts();
                control.RequestStop();
                await control.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            });
            if (cleanupFailure is not null) Observe("cleanup", cleanupBoundary!, cleanupFailure);
        }
        failure?.Throw();
        try
        {
            control.Snapshot.Executions.Should().Be(0);
            await generation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            Observe("failure", "post_cleanup", error);
            throw;
        }
    }
}
